using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Serval.Server.Cameras;
using Serval.Server.Clips;
using Serval.Server.Configuration;
using Serval.Server.Events;
using Serval.Server.Media;
using Serval.Server.Recordings;

namespace Serval.Server.Alerts;

/// <param name="DueAt">
/// The earliest this can be cut, which is when the last frame it wants has been written. Not a
/// preference — the footage does not exist before then.
/// </param>
internal sealed record PendingAlertClip(string AlertId, DateTimeOffset DueAt);

/// <param name="Jpeg">
/// The camera's live snapshot as it was when the alert was raised, taken there rather than read
/// here so the picture is the one closest to the frame that fired rather than whatever has arrived
/// by the time this is written. Snapshots are encoded once and handed out freely, so holding the
/// array costs nothing and copies nothing.
/// </param>
internal sealed record PendingAlertPoster(string AlertId, byte[] Jpeg);

/// <summary>
/// Cuts each alert's preview clip once the footage around it exists.
///
/// <para><b>Where the footage comes from.</b> Whichever of the two segment stores the camera
/// actually has. A camera with a detect stream of its own is buffering that stream in a ring, so the
/// preview is a few hundred kilobytes of sub stream and exists whether or not anyone is recording;
/// a camera whose one stream is both recorded and detected on has no ring, because writing a second
/// copy of bytes already on disk would be pure waste, so its preview comes out of the recording.
/// Either way this is <c>-c copy</c> — a container rewrite, not an encode.</para>
///
/// <para><b>Why it waits.</b> An alert fires at its first frame, and a preview that stopped there
/// would show the moment before anything happened. The wait is the post-roll, and it is why an
/// alert takes some seconds to become watchable — the row appears immediately, the clip catches up.
/// </para>
///
/// <para>One at a time, for the reason <see cref="ClipWriteWorker"/> is: this reads and writes the
/// volume the recorders are writing to, and ten cameras alerting at once must not become ten
/// ffmpegs. These are twenty-second clips off a sub stream, so the queue drains in about as long as
/// it took to fill.</para>
/// </summary>
public sealed class AlertClipWorker : BackgroundService
{
    private readonly Channel<PendingAlertClip> _queue = Channel.CreateUnbounded<PendingAlertClip>();

    /// <summary>
    /// Provisional posters, on their own channel and drained by their own loop.
    ///
    /// <para>Not the queue above, and the reason is that loop's own invariant: every clip waits the
    /// same fixed post-roll, so arrival order is due order and the head is always the next one
    /// ready. A poster is due <em>now</em>. Putting one behind a clip that is mid-delay would make
    /// the picture wait out the post-roll of an unrelated alert, which is the whole thing this is
    /// here to avoid.</para>
    ///
    /// <para>They are also different work. This writes bytes that are already encoded; that one
    /// runs ffmpeg. Nothing is gained by making the picture queue behind the video.</para>
    /// </summary>
    private readonly Channel<PendingAlertPoster> _posters =
        Channel.CreateUnbounded<PendingAlertPoster>();

    /// <summary>
    /// Stamped when the container builds this, which is before any hosted service has run and so
    /// before any alert this process raised can exist. <see cref="RecoverAsync"/> uses it to tell
    /// the alerts a previous run stranded from ones being raised right now — without it, a camera
    /// that alerts during startup would have its clip cancelled by the sweep meant to clean up after
    /// the last shutdown.
    /// </summary>
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    private readonly AlertRepository _alerts;
    private readonly AlertStorage _storage;
    private readonly ClipExporter _exporter;
    private readonly ClipMedia _media;
    private readonly RecordingIndex _recordings;
    private readonly PreviewRingIndex _previewRing;
    private readonly CameraRepository _cameras;
    private readonly EventBroadcaster _events;
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly ILogger<AlertClipWorker> _logger;

    public AlertClipWorker(
        AlertRepository alerts,
        AlertStorage storage,
        ClipExporter exporter,
        ClipMedia media,
        RecordingIndex recordings,
        PreviewRingIndex previewRing,
        CameraRepository cameras,
        EventBroadcaster events,
        IOptionsMonitor<ServerOptions> options,
        ILogger<AlertClipWorker> logger)
    {
        _alerts = alerts;
        _storage = storage;
        _exporter = exporter;
        _media = media;
        _recordings = recordings;
        _previewRing = previewRing;
        _cameras = cameras;
        _events = events;
        _options = options;
        _logger = logger;
    }

    /// <summary>Queues an alert that has just been raised.</summary>
    public void Enqueue(string alertId, DateTimeOffset dueAt) =>
        _queue.Writer.TryWrite(new PendingAlertClip(alertId, dueAt));

    /// <summary>
    /// Queues the picture an alert should show until its clip exists.
    ///
    /// Called from the raise with the camera's live snapshot in hand. Takes a slot and returns; the
    /// caller is the detection loop and nothing may make it wait, which is why the file write
    /// happens on this worker rather than there.
    /// </summary>
    public void EnqueuePoster(string alertId, byte[] jpeg) =>
        _posters.Writer.TryWrite(new PendingAlertPoster(alertId, jpeg));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        // Concurrently, so a provisional poster is never behind a clip serving out its post-roll.
        await Task.WhenAll(
            CutLoopAsync(stoppingToken),
            PosterLoopAsync(stoppingToken));
    }

    private async Task CutLoopAsync(CancellationToken stoppingToken)
    {
        await foreach (PendingAlertClip item in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                // Every item waits the same fixed post-roll, so the channel's arrival order is
                // already due order and the head is always the next one ready. That is what lets
                // this be a delay rather than a priority queue.
                TimeSpan wait = item.DueAt - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, stoppingToken);
                }

                await CutAsync(item.AlertId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutting down. The row stays Pending and the next boot's recovery resolves it.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not cut a preview clip for alert {AlertId}.", item.AlertId);

                await SettleAsync(
                    item.AlertId, AlertClipState.Failed, seconds: null, ex.Message, CancellationToken.None);
            }
        }
    }

    private async Task PosterLoopAsync(CancellationToken stoppingToken)
    {
        await foreach (PendingAlertPoster item in _posters.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await WriteProvisionalPosterAsync(item, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A picture that could not be written costs the stripe for sixteen seconds, which is
                // where this started. Nothing downstream depends on it, so it is not worth failing
                // the alert over.
                _logger.LogWarning(
                    ex, "Could not write a provisional poster for alert {AlertId}.", item.AlertId);
            }
        }
    }

    /// <summary>
    /// Writes the camera's live snapshot as the alert's poster, so the screen has a picture from the
    /// moment the alert exists rather than from the moment its clip does.
    ///
    /// <para><b>It is replaced, not kept.</b> <see cref="CutAsync"/> overwrites this with the exact
    /// frame the detection fired on once the footage is there — which matters, because the box is
    /// measured against that frame and a snapshot is up to <c>Ingest:SnapshotFps</c> away from it.
    /// The window this covers is the sixteen seconds where the alternative is nothing at all.</para>
    /// </summary>
    private async Task WriteProvisionalPosterAsync(
        PendingAlertPoster item, CancellationToken cancellationToken)
    {
        Alert? alert = await _alerts.GetAsync(item.AlertId, cancellationToken);

        // Only ever the stand-in. Settled means either the real poster is already on disk or there
        // is never going to be a clip to cut one from, and in the first case writing here would put
        // the approximate picture back over the exact one.
        if (alert is not { ClipState: AlertClipState.Pending })
        {
            return;
        }

        _storage.EnsureRoot();
        await File.WriteAllBytesAsync(
            _storage.PosterFor(item.AlertId), item.Jpeg, cancellationToken);
    }

    /// <summary>
    /// Resolves every alert a previous run left waiting for a clip.
    ///
    /// There is nothing to retry. A pending alert's footage was in a rolling buffer whose index
    /// lived in the process that stopped and whose files the next session clears — so the honest
    /// answer is that the clip is not coming, and saying so is better than a card that spins
    /// forever. This is also the only path that reaches an alert raised seconds before a crash.
    ///
    /// <para>It resolves the clip and nothing else: <see cref="Alert.Recorded"/> stays null, because
    /// the run that would have decided it is the one that stopped. Undetermined is what these are,
    /// and a card that says nothing about the recording beats one that claims there is none.</para>
    /// </summary>
    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            long stranded = await _alerts.FailPendingAsync(_startedAt, cancellationToken);

            if (stranded > 0)
            {
                _logger.LogWarning(
                    "{Count} alert(s) were waiting for a preview clip when the server stopped; "
                    + "their footage is gone, so they are marked as having none.",
                    stranded);
            }
        }
        catch (Exception ex)
        {
            // Mongo may not be up yet on a cold start. This is a tidy-up, not a precondition for
            // taking new work.
            _logger.LogWarning(ex, "Could not sweep alerts left waiting for a preview clip.");
        }
    }

    private async Task CutAsync(string alertId, CancellationToken cancellationToken)
    {
        Alert? alert = await _alerts.GetAsync(alertId, cancellationToken);

        if (alert is null || alert.ClipState != AlertClipState.Pending)
        {
            // Deleted by retention, or already settled. Neither is an error.
            return;
        }

        MediaOptions media = _options.CurrentValue.Media;
        DateTimeOffset from = alert.At.AddSeconds(-media.AlertPreRollSeconds);
        DateTimeOffset to = alert.At.AddSeconds(media.AlertPostRollSeconds);

        // Asked here rather than when the alert was raised, because at that moment the segment
        // covering it had not been closed, indexed, or in some cases finished arriving.
        bool recorded = await IsRecordedAsync(alert.CameraId, alert.At, cancellationToken);

        IReadOnlyList<RecordingSegment> run = ClipExporter.LeadingRun(
            await SegmentsAsync(alert.CameraId, from, to, cancellationToken));

        if (run.Count == 0)
        {
            _logger.LogInformation(
                "Alert {AlertId} on camera {CameraId} has no preview: no footage covers {At:HH:mm:ss}.",
                alert.Id, alert.CameraId, alert.At);

            await SettleAsync(alert.Id, AlertClipState.Unavailable, null, null, cancellationToken, recorded);
            return;
        }

        _storage.EnsureRoot();

        string video = _storage.VideoFor(alert.Id);
        await _exporter.WriteFileAsync(alert.CameraId, run, video, cancellationToken);

        double seconds = (to - from).TotalSeconds;
        seconds = await _media.TryReadDurationAsync(video, cancellationToken) ?? seconds;

        // The frame the detection fired on, which is the picture the box belongs to — offset from
        // where the clip actually starts rather than from where it was asked to, because segments
        // only cut on keyframes and the first one usually begins before the range.
        double posterAt = (alert.PeakAt - run[0].StartedAt).TotalSeconds;
        await _media.TryWritePosterAsync(video, _storage.PosterFor(alert.Id), posterAt, cancellationToken);

        await SettleAsync(alert.Id, AlertClipState.Ready, seconds, null, cancellationToken, recorded);

        _logger.LogInformation(
            "Alert {AlertId} on camera {CameraId}: {Seconds:0.#}s preview from {Segments} segment(s) of {Source}.",
            alert.Id, alert.CameraId, seconds, run.Count,
            _previewRing.HasRing(alert.CameraId) ? "the preview buffer" : "the recording");
    }

    /// <summary>
    /// The segments covering the padded window, from whichever store this camera has.
    ///
    /// The ring wins when there is one. It is the detect stream, so it exists on a camera nobody is
    /// recording — which is the case this whole feature is for — and it is the smaller picture, which
    /// for a twenty-second card is the right trade. A camera with no ring is one whose recorded
    /// stream is also its detect stream, so the recording is those same bytes.
    /// </summary>
    private async Task<IReadOnlyList<RecordingSegment>> SegmentsAsync(
        string cameraId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        _previewRing.HasRing(cameraId)
            ? _previewRing.InRange(cameraId, from, to)
            : await _recordings.InRangeAsync(cameraId, from, to, cancellationToken);

    /// <summary>
    /// Whether a recorded segment covers this instant — asked as a one-second window rather than a
    /// point, because <c>InRangeAsync</c>'s overlap rule excludes a segment that merely touches an
    /// end and a zero-width window touches everything.
    /// </summary>
    private async Task<bool> IsRecordedAsync(
        string cameraId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        List<RecordingSegment> covering =
            await _recordings.InRangeAsync(cameraId, at, at.AddSeconds(1), cancellationToken);

        return covering.Count > 0;
    }

    /// <summary>
    /// Records how the cut went and tells anyone watching, so a card that was waiting becomes one
    /// that plays without the screen having to poll for it.
    /// </summary>
    private async Task SettleAsync(
        string alertId,
        AlertClipState state,
        double? seconds,
        string? error,
        CancellationToken cancellationToken,
        bool? recorded = null)
    {
        await _alerts.SetClipAsync(alertId, state, seconds, error, recorded, cancellationToken);

        if (state == AlertClipState.Failed)
        {
            _storage.Remove(alertId);
        }

        Alert? settled = await _alerts.GetAsync(alertId, cancellationToken);
        if (settled is null)
        {
            return;
        }

        Camera? camera = await _cameras.GetAsync(settled.CameraId, cancellationToken);

        _events.Publish(new LiveEvent(
            settled.CameraId,
            AlertResponse.EventType,
            AlertResponse.From(settled, camera?.Name ?? settled.CameraId)));
    }
}
