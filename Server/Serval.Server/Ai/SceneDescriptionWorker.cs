using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Serval.Ai;
using Serval.Contracts;
using Serval.Server.Configuration;
using Serval.Server.Events;
using Serval.Server.Telemetry;

namespace Serval.Server.Ai;

/// <summary>
/// Owns the one vision model in the process and runs descriptions for every camera that asks.
///
/// One model, many cameras — which is the whole difference from the edge, where there is one of
/// each. llama.cpp contexts are not thread-safe and a description costs seconds of CPU, so the
/// work is strictly serialized. The scheduling that matters is therefore fairness: requests are
/// taken round-robin across cameras, because a busy driveway must not starve the back door of
/// every description simply by asking more often.
///
/// Each camera's request slot holds at most one pending request (capacity 1, drop-oldest), so a
/// camera that keeps seeing motion while the model is busy overwrites its own pending ask rather
/// than building a queue of stale ones — when the model frees up it describes what the camera can
/// see now, not the frame that first tripped the gate.
/// </summary>
public sealed class SceneDescriptionWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TelemetryRepository _repository;
    private readonly EventBroadcaster _events;
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly ILogger<SceneDescriptionWorker> _logger;

    private readonly ConcurrentDictionary<string, SceneDescriptionService> _cameras = new();

    /// <summary>Signalled when any camera posts a request, so the loop sleeps rather than spins.</summary>
    private readonly SemaphoreSlim _pending = new(0);

    public SceneDescriptionWorker(
        IServiceProvider services,
        IHostApplicationLifetime lifetime,
        TelemetryRepository repository,
        EventBroadcaster events,
        IOptionsMonitor<ServerOptions> options,
        ILogger<SceneDescriptionWorker> logger)
    {
        _services = services;
        _lifetime = lifetime;
        _repository = repository;
        _events = events;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Read fresh so the prompts, the frame count and the pacing floor can be retuned without a
    /// restart. The model itself cannot — it is loaded once, which is why its paths, thread count
    /// and GPU layers are marked as needing one.
    /// </summary>
    private VisionOptions Vision => _options.CurrentValue.Ai.Vision;

    /// <summary>
    /// The vision runner, loaded on first touch.
    ///
    /// Resolved through the provider rather than injected, the same way <see cref="ClipSummaryWorker"/>
    /// does it, and here for a reason about *when* rather than whether: constructing the model takes
    /// tens of seconds, and the host builds every hosted service before it starts any of them — so a
    /// constructor parameter puts the whole load in front of Kestrel binding its socket. A server
    /// that answers nothing for half a minute after it starts is one the App can reach while its
    /// preferences request cannot, which is the fault this arrangement exists to avoid.
    ///
    /// <see cref="ExecuteAsync"/> forces it immediately, so "on first touch" is in practice "as soon
    /// as the socket is open".
    /// </summary>
    private IVisionInferenceRunner Runner => _services.GetRequiredService<IVisionInferenceRunner>();

    /// <summary>How many frames a request should carry, given what this backend can accept.</summary>
    public int FramesPerRequest => Math.Min(Math.Max(1, Vision.MaxFrames), Runner.MaxFrames);

    /// <summary>Registers a camera and returns the slot its motion gate posts requests into.</summary>
    public SceneDescriptionService Register(string cameraId) =>
        _cameras.GetOrAdd(cameraId, id => new SceneDescriptionService(id));

    public void Forget(string cameraId)
    {
        if (_cameras.TryRemove(cameraId, out SceneDescriptionService? service))
        {
            service.Complete();
        }
    }

    /// <summary>Called by a camera's gate after posting, so the loop wakes without polling.</summary>
    public void Notify() => _pending.Release();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield first so the load runs on the background service's own time rather than inside the
        // host's start sequence, which is what leaves the socket free to open while it happens.
        await Task.Yield();

        try
        {
            _ = Runner;
        }
        catch (Exception ex)
        {
            // A model that is present but unusable — a truncated GGUF, no VRAM — is a real fault
            // and stays fatal, as it was when this loaded during construction. A server that came
            // up and quietly never described anything is the outcome being refused here; it is
            // only the point at which it dies that moved, and it is still during startup.
            _logger.LogCritical(ex, "Vision model failed to load; stopping.");
            _lifetime.StopApplication();
            return;
        }

        DateTimeOffset lastRun = DateTimeOffset.MinValue;

        // Where the last round-robin sweep stopped, so the next one resumes past it rather than
        // always restarting at the same camera.
        int cursor = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _pending.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Re-read each pass rather than captured before the loop, so changing the floor
            // applies to the next description instead of the next restart.
            var minimumGap = TimeSpan.FromSeconds(Vision.MinSecondsBetweenDescriptions);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now - lastRun < minimumGap)
            {
                // The floor is global, not per camera: it exists to bound this process's CPU, and
                // the CPU is shared. Sleep off the remainder rather than dropping the request.
                try
                {
                    await Task.Delay(minimumGap - (now - lastRun), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (!TryTakeNext(ref cursor, out SceneDescriptionService? camera, out SceneRequest? request))
            {
                // A camera that replaces its pending request signals again without adding one, so
                // permits can outnumber requests. A miss means every slot is empty, so collapse the
                // surplus instead of waking once per stale permit. The re-check closes the race
                // where a request arrived mid-drain: its write lands before its signal, so if the
                // permit was swallowed the request is already visible here.
                while (_pending.Wait(0)) { }

                if (!TryTakeNext(ref cursor, out camera, out request))
                {
                    continue;
                }
            }

            try
            {
                await DescribeAsync(camera!, request!, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogDebug("Description abandoned: shutting down.");
                break;
            }
            catch (Exception ex)
            {
                // A failed description must not take down the vision path for every other camera.
                _logger.LogError(
                    ex, "Vision inference failed for camera {CameraId}.", camera!.CameraId);
            }

            lastRun = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Takes one pending request, resuming from where the last sweep left off so no camera can
    /// monopolise the model.
    /// </summary>
    private bool TryTakeNext(
        ref int cursor, out SceneDescriptionService? camera, out SceneRequest? request)
    {
        SceneDescriptionService[] services = _cameras.Values.ToArray();

        for (int i = 0; i < services.Length; i++)
        {
            SceneDescriptionService candidate = services[(cursor + i) % services.Length];
            if (candidate.Requests.TryRead(out SceneRequest? pending))
            {
                cursor = (cursor + i + 1) % services.Length;
                camera = candidate;
                request = pending;
                return true;
            }
        }

        camera = null;
        request = null;
        return false;
    }

    private async Task DescribeAsync(
        SceneDescriptionService camera, SceneRequest request, CancellationToken cancellationToken)
    {
        IReadOnlyList<VisionFrame> frames = request.Frames.Count > Runner.MaxFrames
            ? request.Frames.TakeLast(Runner.MaxFrames).ToList()
            : request.Frames;

        if (frames.Count == 0)
        {
            return;
        }

        string text = await Runner.RunInferenceAsync(frames, PromptFor(frames), cancellationToken);

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning(
                "Vision model returned an empty description for camera {CameraId}.", camera.CameraId);
            return;
        }

        var describedAt = DateTimeOffset.UtcNow;
        camera.Publish(new SceneDescription(text, describedAt));

        var scene = new SceneDocument
        {
            Id = Guid.NewGuid().ToString(),
            CameraId = camera.CameraId,
            ReceivedAt = describedAt,
            Timestamp = frames[^1].CapturedAt,
            Description = text,
            Trigger = request.Trigger,
            MotionScore = request.MotionScore is { } score ? Math.Round(score, 4) : null,
            FrameCount = frames.Count,
            FrameSpanSeconds = Math.Round(request.SpanSeconds, 2),
            Source = TelemetrySource.Server,
        };

        await _repository.UpsertSceneAsync(scene, cancellationToken);
        _events.Publish(new LiveEvent(scene.CameraId!, scene.Type, scene));
    }

    /// <summary>
    /// One frame can only say what is there; several can say what is happening. The multi-frame
    /// prompt states the spacing, because "what changed" is meaningless without knowing over what.
    /// </summary>
    private string PromptFor(IReadOnlyList<VisionFrame> frames)
    {
        if (frames.Count < 2)
        {
            return Vision.Prompt;
        }

        double seconds = (frames[^1].CapturedAt - frames[0].CapturedAt).TotalSeconds;

        return Vision.MotionPrompt
            .Replace("{count}", frames.Count.ToString(), StringComparison.Ordinal)
            .Replace("{seconds}", seconds.ToString("0.#"), StringComparison.Ordinal);
    }

    public override void Dispose()
    {
        _pending.Dispose();
        base.Dispose();
    }
}
