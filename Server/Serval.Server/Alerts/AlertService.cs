using Microsoft.Extensions.Options;
using Serval.Contracts;
using Serval.Server.Cameras;
using Serval.Server.Configuration;
using Serval.Server.Events;
using Serval.Server.Push;
using Serval.Server.Snapshots;

namespace Serval.Server.Alerts;

/// <summary>
/// Turns a detection somebody asked to be told about into a row in the queue and a clip on the way.
///
/// <para>The one place the detection pipelines call. They already decide what is worth an alert —
/// <c>ObjectEventPolicy</c> for objects, <c>SoundEventPolicy</c> for sounds — and this is
/// deliberately not a second opinion on that: it takes <c>IsAlert</c> at its word and does the
/// bookkeeping. Splitting the judgement across two places is how a screen ends up disagreeing with
/// the database about what an alert is.</para>
///
/// <para><b>Nothing here blocks.</b> The callers are the detection loops, which are the thing that
/// must not be slowed by anything: this writes one small document and drops an id in a channel. The
/// ffmpeg happens later, elsewhere, and a database that is briefly unavailable costs an alert
/// rather than a camera's detection.</para>
/// </summary>
public sealed class AlertService
{
    private readonly AlertRepository _alerts;
    private readonly AlertClipWorker _clips;
    private readonly AlertNotifier _notifications;
    private readonly EventBroadcaster _events;
    private readonly SnapshotBroadcaster _snapshots;
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly ILogger<AlertService> _logger;

    public AlertService(
        AlertRepository alerts,
        AlertClipWorker clips,
        AlertNotifier notifications,
        EventBroadcaster events,
        SnapshotBroadcaster snapshots,
        IOptionsMonitor<ServerOptions> options,
        ILogger<AlertService> logger)
    {
        _alerts = alerts;
        _clips = clips;
        _notifications = notifications;
        _events = events;
        _snapshots = snapshots;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Raises an alert for an object-detection episode, if this episode has not raised one already.
    ///
    /// <para>Called while the episode is still open — the moment it is known to be an alert, which
    /// is when it opened — rather than when it closes. Closing waits for the object to be gone for
    /// <c>AbsenceSeconds</c>, half a minute after the interesting part, and a queue that arrives
    /// half a minute late is a queue nobody trusts. It also means the clip can be cut from a buffer
    /// that is still holding the footage.</para>
    ///
    /// <para>The consequence is that <see cref="Alert.PeakAt"/> and <see cref="Alert.Box"/> are the
    /// best frame <em>so far</em> rather than the best of the whole episode. That is the right
    /// picture anyway: the card says "the frame it fired on".</para>
    /// </summary>
    public Task RaiseObjectAsync(
        Camera camera, DetectionDocument detection, CancellationToken cancellationToken) =>
        RaiseAsync(
            new Alert
            {
                Id = detection.Id,
                CameraId = camera.Id,
                Kind = AlertKind.Object,
                At = detection.Timestamp,
                PeakAt = detection.PeakFrameAt,
                Label = detection.Label,
                Title = AlertTitle.ForObject(detection.Label, camera.Name),
                Box = detection.BestBox,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            camera.Name,
            cancellationToken);

    /// <summary>
    /// Raises an alert for a sound event.
    ///
    /// A sound gets a video clip like anything else, and deliberately so: "glass at the back door"
    /// is a thing you want to look at, not only listen to. It carries no box because a sound has no
    /// place in the frame — which is the one thing the card draws differently.
    /// </summary>
    public Task RaiseSoundAsync(
        Camera camera, SoundDocument sound, CancellationToken cancellationToken) =>
        RaiseAsync(
            new Alert
            {
                Id = sound.Id,
                CameraId = camera.Id,
                Kind = AlertKind.Sound,
                At = sound.Timestamp,
                PeakAt = sound.Timestamp,
                Label = sound.Label,
                Title = AlertTitle.ForSound(sound.Label, camera.Name),
                Box = null,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            camera.Name,
            cancellationToken);

    private async Task RaiseAsync(Alert alert, string cameraName, CancellationToken cancellationToken)
    {
        try
        {
            if (!await _alerts.RaiseAsync(alert, cancellationToken))
            {
                // Already raised. The object-detection path reaches here once a frame for as long
                // as the thing is in view, so this is the common case rather than a collision.
                return;
            }

            // Due when the last frame the clip wants has been written. A second on top of the
            // post-roll so the segment holding that frame has closed and been seen — a cut that
            // arrives before the footage does is the one way to get a preview that stops early.
            _clips.Enqueue(
                alert.Id,
                alert.At.AddSeconds(_options.CurrentValue.Media.AlertPostRollSeconds + 1));

            // A picture to stand in for those sixteen seconds. The camera's live snapshot is already
            // encoded and already in memory, so this is a dictionary read here and a file write on
            // the worker — no frame is captured and nothing is re-encoded for it.
            //
            // Read at the raise rather than on the worker so the picture is the one nearest the
            // frame that fired. Null between a restart and the session's first snapshot, which is
            // about a second; that alert falls back to showing its camera's stripe until the clip
            // settles.
            if (_snapshots.Latest(alert.CameraId) is { } snapshot)
            {
                _clips.EnqueuePoster(alert.Id, snapshot.Jpeg);
            }

            // Published straight away, without waiting for the clip. The queue's job is to be
            // timely; a row that says its preview is on the way is worth more than a row that
            // appears twenty seconds after the doorbell.
            AlertResponse published = AlertResponse.From(alert, cameraName);
            _events.Publish(new LiveEvent(alert.CameraId, AlertResponse.EventType, published));

            // Same moment, same words, for whoever is not looking at the screen. Inside this branch
            // rather than above it, so the once-a-frame duplicate path never reaches it — the
            // insert having succeeded is exactly what "this alert is new" means. Enqueue only takes
            // a slot in a channel; see the class note about not blocking.
            _notifications.Enqueue(published);

            _logger.LogInformation(
                "Alert on camera {CameraId}: {Title} at {At:HH:mm:ss}.",
                alert.CameraId, alert.Title, alert.At);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The same rule the detection store follows: a database blip must not take down the
            // camera's session, because detecting is the more important of the two jobs.
            _logger.LogWarning(
                ex, "Could not raise an alert for camera {CameraId}.", alert.CameraId);
        }
    }
}
