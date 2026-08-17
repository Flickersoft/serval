using System.Globalization;
using Microsoft.Extensions.Options;
using Serval.Ai;
using Serval.Contracts;
using Serval.Server.Alerts;
using Serval.Server.Cameras;
using Serval.Server.Configuration;
using Serval.Server.Events;
using Serval.Server.Snapshots;
using Serval.Server.Telemetry;

namespace Serval.Server.Ai;

/// <summary>
/// Runs server-side detection for the cameras that asked for it, and keeps that set in step with
/// the registry — the same reconcile-and-supervise shape <see cref="Ingest.StreamIngestManager"/>
/// uses for recording, for the same reason: cameras are added, disabled and reconfigured while the
/// server runs, and each session must be able to fail and be restarted without disturbing the rest.
///
/// The two halves are deliberately unlike each other, because their inputs are:
///
/// <list type="bullet">
/// <item><b>Vision</b> needs no new ffmpeg at all. The recording sessions already publish a
/// validated ~1 fps JPEG per camera to <see cref="SnapshotBroadcaster"/>, which is exactly what a
/// frame-difference gate wants, so vision rides on frames that are already being produced.</item>
/// <item><b>Audio</b> has no such source — the Server pulls video only — so each audio-enabled
/// camera gets its own audio-only ffmpeg session.</item>
/// </list>
/// </summary>
public sealed class CameraAiCoordinator : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

    private readonly CameraRepository _cameras;
    private readonly SnapshotBroadcaster _snapshots;
    private readonly DetectFrameBroadcaster _detectFrames;
    private readonly DetectionLoad _load;
    private readonly SceneDescriptionWorker? _vision;
    private readonly IObjectDetector? _detector;
    private readonly SenseVoiceAnalyzer? _analyzer;
    private readonly SoundEventTagger? _tagger;
    private readonly AudioLevelBroadcaster _levels;
    private readonly TelemetryRepository _repository;
    private readonly EventBroadcaster _events;
    private readonly AlertService _alerts;
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CameraAiCoordinator> _logger;

    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    /// <summary>
    /// The one budget every camera's detection is drawn from, sized from what this host measurably
    /// manages. Null until the first reconcile has had a chance to time the detector, and null
    /// forever on a host with no detector or one that could not be timed — in which case nothing
    /// is throttled.
    /// </summary>
    private InferenceScheduler? _scheduler;
    private bool _measured;

    /// <summary>Measured capacity divided by the lanes it was measured across, so the budget can be
    /// rescaled when a hardware lane comes or goes. Zero until the detector has been timed.</summary>
    private double _measuredPerLane;

    /// <summary>The budget currently granted, so a rescale can tell a real change from a no-op.</summary>
    private double _grantedBudget;

    public CameraAiCoordinator(
        CameraRepository cameras,
        SnapshotBroadcaster snapshots,
        DetectFrameBroadcaster detectFrames,
        DetectionLoad load,
        TelemetryRepository repository,
        EventBroadcaster events,
        AlertService alerts,
        IOptionsMonitor<ServerOptions> options,
        AudioLevelBroadcaster levels,
        ILoggerFactory loggerFactory,
        ILogger<CameraAiCoordinator> logger,
        SceneDescriptionWorker? vision = null,
        IObjectDetector? detector = null,
        SenseVoiceAnalyzer? analyzer = null,
        SoundEventTagger? tagger = null)
    {
        _cameras = cameras;
        _snapshots = snapshots;
        _detectFrames = detectFrames;
        _load = load;
        _repository = repository;
        _events = events;
        _alerts = alerts;
        _options = options;
        _levels = levels;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _vision = vision;
        _detector = detector;
        _analyzer = analyzer;
        _tagger = tagger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period());

        _logger.LogInformation(
            "Server-side AI detection is enabled; reconciling every {Period}.", Period());

        try
        {
            do
            {
                try
                {
                    // Re-read each tick, so changing how often cameras are reconciled is itself
                    // applied without one.
                    timer.Period = Period();
                    await ReconcileAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    // A registry read that fails must not end the coordinator; try again next tick.
                    _logger.LogError(ex, "AI reconcile failed; will retry.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
        finally
        {
            await StopAllAsync();
        }
    }

    /// <summary>
    /// Times the detector once and builds the shared budget from it.
    ///
    /// Deferred to the first reconcile rather than done at construction, because it runs real
    /// inference and the constructor is on the path that brings the whole server up. A host whose
    /// detector cannot be timed simply gets no ceiling.
    /// </summary>
    private async Task EnsureBudgetAsync(CancellationToken cancellationToken)
    {
        if (_measured || _detector is null)
        {
            return;
        }

        _measured = true;

        double? perSecond = await InferenceBudget.MeasureAsync(
            _detector, cancellationToken: cancellationToken);

        if (perSecond is not { } measured)
        {
            _logger.LogWarning(
                "Could not time the detector, so detection runs with no budget. Every region every "
                + "camera proposes will be examined.");
            return;
        }

        // Kept per lane so the budget can be rescaled if the host loses one. An average across lanes,
        // which is exact for a CPU pool and an approximation for hardware lanes of unequal speed — see
        // RebudgetForHealth.
        _measuredPerLane = measured / Math.Max(_detector.Concurrency, 1);

        double budget = measured * _detector.Utilisation;
        _scheduler = new InferenceScheduler(budget);
        _load.BudgetPerSecond = budget;
        _grantedBudget = budget;

        _logger.LogInformation(
            "Detection budget: {Budget:0.#} inferences/second ({Measured:0.#}/s measured on "
            + "{Description}, at {Share:P0} of it), shared across every camera.",
            budget, measured, _detector.Description, _detector.Utilisation);
    }

    /// <summary>
    /// Rescales the budget when the detector reports it has lost or regained a lane.
    ///
    /// <para><b>Why this exists.</b> The budget is measured once, so a backend whose lanes are hardware
    /// can halve its real capacity while the scheduler keeps admitting the original figure. The excess
    /// then hits the detector's own busy timeout and becomes a <em>dropped frame</em> — and coverage,
    /// which counts only what the budget refused, deliberately ignores dropped frames. So without this
    /// the status page stays green while half the detections quietly stop happening.</para>
    ///
    /// <para>Rescaled rather than re-measured: timing a detector under live load measures contention,
    /// not capacity.</para>
    ///
    /// <para><b>Inert on a CPU backend.</b> <c>Health.HealthyLanes</c> defaults to
    /// <c>Concurrency</c> and a thread pool does not lose lanes, so the recomputed figure is identical
    /// every tick and nothing is logged. That is what keeps this invisible on a host that has no
    /// accelerator.</para>
    /// </summary>
    private void RebudgetForHealth()
    {
        if (_detector is null)
        {
            return;
        }

        // Published every tick regardless of whether the budget moved, so the status page can name the
        // running backend and its lane count even on a host where nothing ever goes wrong.
        DetectorHealth current = _detector.Health;
        _load.Backend = _detector.Description;
        _load.Lanes = current.Lanes;
        _load.HealthyLanes = current.HealthyLanes;
        _load.DetectorDegraded = current.Degraded;

        if (_scheduler is null || _measuredPerLane <= 0)
        {
            return;
        }

        int available = Math.Max(current.HealthyLanes, 0);
        double budget = _measuredPerLane * available * _detector.Utilisation;

        // Only act on a real change. Recomputing to the same number every reconcile tick and logging it
        // would bury the one occasion that matters in a healthy server's noise.
        if (Math.Abs(budget - _grantedBudget) < 0.01)
        {
            return;
        }

        bool dropped = budget < _grantedBudget;
        _grantedBudget = budget;
        _scheduler.Rebudget(budget);
        _load.BudgetPerSecond = budget;

        if (dropped)
        {
            _logger.LogWarning(
                "Detection capacity fell to {Budget:0.#} inferences/second: {Healthy} of {Lanes} lane(s) "
                + "available on {Description}. {Degraded}",
                budget, current.HealthyLanes, current.Lanes, _detector.Description,
                current.Degraded ?? "No reason reported.");
        }
        else
        {
            _logger.LogInformation(
                "Detection capacity recovered to {Budget:0.#} inferences/second: {Healthy} of {Lanes} "
                + "lane(s) available on {Description}.",
                budget, current.HealthyLanes, current.Lanes, _detector.Description);
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await EnsureBudgetAsync(cancellationToken);

        // Every tick, because a device can be unplugged between them. Inert unless the detector reports
        // it has actually lost or regained a lane.
        RebudgetForHealth();

        List<Camera> cameras = await _cameras.ListAsync(cancellationToken);

        var wanted = cameras
            .Where(c => c.Enabled && (WantsVision(c) || WantsAudio(c)))
            .ToDictionary(c => c.Id, StringComparer.Ordinal);

        // Stop sessions for cameras that went away, were disabled, opted out, or whose source
        // changed — the last because a session is bound to the URL it was started against.
        foreach (string id in _sessions.Keys.ToList())
        {
            Session session = _sessions[id];
            bool keep = wanted.TryGetValue(id, out Camera? camera)
                && Signature(camera!) == session.Signature
                && !session.Task.IsCompleted;

            if (!keep)
            {
                await StopAsync(id);
            }
        }

        foreach ((string id, Camera camera) in wanted)
        {
            if (!_sessions.ContainsKey(id))
            {
                Start(camera);
            }
        }

        // Published every tick rather than on change, because it is what the status page divides the
        // budget by — a stale count would report a per-camera share nobody is actually getting.
        _load.ActiveCameras = _sessions.Count(session => session.Value.UsesDetection);
    }

    /// <summary>
    /// Whether to watch this camera's frames at all.
    ///
    /// Either capability is enough on its own, and that matters: <see cref="SceneDescriptionWorker"/>
    /// is only registered when a 2.3 GB vision model is on disk, while the object detector needs one
    /// a couple of hundred times smaller. Requiring the worker would mean a host that has the
    /// detector and not the model — by far the more likely first deployment — silently never looked
    /// at anything.
    /// </summary>
    private bool WantsVision(Camera camera) =>
        WantsVision(camera.AiVision, _vision is not null, _detector is not null);

    /// <summary>The rule on its own, so it can be pinned without standing up a host.</summary>
    internal static bool WantsVision(bool aiVision, bool hasVisionModel, bool hasDetector) =>
        aiVision && (hasVisionModel || hasDetector);

    private bool WantsAudio(Camera camera) =>
        camera.AiAudio && _analyzer is not null && camera.DetectStream is not null;

    private TimeSpan Period() =>
        TimeSpan.FromSeconds(Math.Max(_options.CurrentValue.ServerAi.ReconcileSeconds, 1));

    private AiOptions Effective(Camera camera) => Effective(_options.CurrentValue.Ai, camera);

    private string Signature(Camera camera) => Signature(camera, _options.CurrentValue.Ai);

    /// <summary>
    /// This camera's settings: the server-wide values with whatever it overrides applied. The one
    /// point where that resolution happens, which is what lets the signature below and the session
    /// itself be certain they are looking at the same thing.
    /// </summary>
    internal static AiOptions Effective(AiOptions global, Camera camera) => CameraAiOptions.For(
        global,
        camera.AudioTuning,
        camera.DetectionTuning,
        camera.SoundTuning,
        camera.MotionTuning);

    /// <summary>
    /// Everything a running session is bound to. A change here means restart it. Only the detect
    /// stream matters: vision reads its snapshots and the audio tap pulls from it, so a change to
    /// the recorded stream alone is none of this coordinator's business.
    ///
    /// <para>The tuning half is <see cref="AiSessionSignature"/> over the camera's <em>effective</em>
    /// settings rather than a list of the fields it overrides. That covers the server-wide values
    /// and the per-camera ones under one rule, which is what it takes for a change made on the
    /// settings page to reach a camera that is already running — see that type for the argument in
    /// full.</para>
    ///
    /// <para>Static, and taking the server-wide settings as an argument, so it can be pinned
    /// without standing up a host — the same reason <see cref="WantsVision(bool, bool, bool)"/> is.</para>
    /// </summary>
    internal static string Signature(Camera camera, AiOptions global) =>
        $"{camera.DetectStream?.Url}|{camera.AiVision}|{camera.AiAudio}"
        + $"|{AiSessionSignature.Of(Effective(global, camera))}";

    private void Start(Camera camera)
    {
        var cts = new CancellationTokenSource();
        var session = new Session(
            Signature(camera), cts, WantsVision(camera) && _detector is not null);

        session.Task = RunSupervisedAsync(camera, cts.Token);
        _sessions[camera.Id] = session;

        _logger.LogInformation(
            "Started AI for camera {CameraId} (vision={Vision}, audio={Audio}).",
            camera.Id, WantsVision(camera), WantsAudio(camera));
    }

    /// <summary>
    /// Keeps a camera's detection running, backing off after failures so a permanently broken
    /// source cannot spin.
    /// </summary>
    private async Task RunSupervisedAsync(Camera camera, CancellationToken cancellationToken)
    {
        TimeSpan backoff = TimeSpan.FromSeconds(2);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(camera, cancellationToken);

                // A clean return means the source ended — a file that stopped looping, or a camera
                // with no audio track. Wait before retrying rather than hammering it.
                backoff = TimeSpan.FromSeconds(30);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI session for camera {CameraId} failed; restarting.", camera.Id);
            }

            try
            {
                await Task.Delay(backoff, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private async Task RunOnceAsync(Camera camera, CancellationToken cancellationToken)
    {
        var jobs = new List<Func<CancellationToken, Task>>();

        // Derived once, here, and handed to both halves. Vision has no per-camera overrides today,
        // but having a single point where a camera's settings are resolved means adding one later
        // is a change inside CameraAiOptions rather than a new read of the globals out here.
        AiOptions ai = Effective(camera);

        if (WantsVision(camera))
        {
            jobs.Add(token => RunVisionAsync(camera, ai, token));
        }

        CameraAudioDetector? audio = null;
        if (WantsAudio(camera))
        {
            // Sound tagging rides on the camera's existing AiAudio flag rather than a third one:
            // it is the same audio session, and a camera that wants its audio listened to has no
            // obvious reason to want only half of what can be heard in it.
            audio = new CameraAudioDetector(
                camera, _analyzer!, _repository, _events, ai, _options.CurrentValue.Ingest,
                _loggerFactory, _tagger, _levels, _alerts);
            jobs.Add(audio.RunAsync);
        }

        try
        {
            // Both halves run until cancelled, so the first to stop for any reason takes the other
            // with it and this returns to the supervisor, which restarts the pair. Task.WhenAll
            // would instead block on the surviving half and hide the failure indefinitely.
            await TaskGroup.RunUntilFirstCompletesAsync(jobs, cancellationToken);
        }
        finally
        {
            audio?.Dispose();
        }
    }

    /// <summary>
    /// Feeds a camera's frames to its vision pipeline until a stream ends or goes quiet.
    ///
    /// Two producers, read by two loops: encoded snapshots for the vision model and the motion gate,
    /// raw frames for object detection. Either falling silent ends the session, because a pipeline
    /// running on half its input is a pipeline reporting things that are not true — a detector with
    /// no frames leaves every episode open, claiming forever that whoever was there still is.
    /// </summary>
    private async Task RunVisionAsync(Camera camera, AiOptions ai, CancellationToken cancellationToken)
    {
        using var pipeline = new CameraVisionPipeline(
            camera, ai, _vision, _detector, _loggerFactory.CreateLogger<CameraVisionPipeline>(),
            _scheduler, _load);

        try
        {
            List<Func<CancellationToken, Task>> readers =
                [token => ReadSnapshotsAsync(camera, pipeline, token)];

            // Only when something will actually look at them. A camera with no detector has no use
            // for raw frames, and subscribing anyway would make the idle timeout below fire on a
            // stream nobody wanted.
            if (pipeline.UsesDetection)
            {
                readers.Add(token => ReadDetectFramesAsync(camera, pipeline, token));
            }

            await TaskGroup.RunUntilFirstCompletesAsync(readers, cancellationToken);
        }
        finally
        {
            // Whatever ends this session — cancellation, a dead producer, a fault — anything still
            // open has to be closed out, or it stays open in storage forever.
            await StoreAsync(camera, pipeline.Finalise(DateTimeOffset.UtcNow), CancellationToken.None);
        }
    }

    /// <summary>
    /// The encoded-snapshot half.
    ///
    /// The subscription is per camera, not every-camera-then-filter. The saved comparisons are
    /// beside the point; the buffer is. A subscriber channel holds 32 frames, so on an
    /// every-camera subscription N cameras leave this loop only 32/N seconds of slack — at 20
    /// cameras, a stall past ~1.6s starts silently dropping the frames the gate needs. Keyed, the
    /// same 32 slots are all this camera's, and a whole burst decodes late rather than being lost.
    /// </summary>
    private async Task ReadSnapshotsAsync(
        Camera camera, CameraVisionPipeline pipeline, CancellationToken cancellationToken)
    {
        TimeSpan idleTimeout = IdleTimeout(_options.CurrentValue.Ingest.SnapshotFps);
        using SnapshotBroadcaster.Subscription subscription = _snapshots.Subscribe(camera.Id);

        while (!cancellationToken.IsCancellationRequested)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(idleTimeout);

            try
            {
                if (!await subscription.Reader.WaitToReadAsync(idle.Token))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Camera {CameraId}: no snapshot for {Seconds:0}s; restarting its AI session.",
                    camera.Id, idleTimeout.TotalSeconds);
                return;
            }

            while (subscription.Reader.TryRead(out Snapshot? snapshot))
            {
                await StoreAsync(camera, pipeline.ProcessSnapshot(snapshot), cancellationToken);
            }
        }
    }

    /// <summary>
    /// The raw-frame half, which is where detection actually happens.
    ///
    /// Each frame's buffer is borrowed from a pool and owned by this loop, so it is returned the
    /// moment the pipeline is done with it — including when the pipeline throws. Holding one past
    /// here, or forgetting it on a fault, is a megabyte leaked per frame.
    /// </summary>
    private async Task ReadDetectFramesAsync(
        Camera camera, CameraVisionPipeline pipeline, CancellationToken cancellationToken)
    {
        TimeSpan idleTimeout = IdleTimeout(_options.CurrentValue.Ingest.DetectFps);
        using DetectFrameBroadcaster.Subscription subscription = _detectFrames.Subscribe(camera.Id);

        while (!cancellationToken.IsCancellationRequested)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            idle.CancelAfter(idleTimeout);

            DetectFrame frame;
            try
            {
                frame = await subscription.ReadAsync(idle.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Camera {CameraId}: no detect frame for {Seconds:0}s; restarting its AI session.",
                    camera.Id, idleTimeout.TotalSeconds);
                return;
            }

            try
            {
                await StoreAsync(
                    camera,
                    await pipeline.ProcessDetectFrameAsync(frame, cancellationToken),
                    cancellationToken);
            }
            finally
            {
                frame.Return();
            }
        }
    }

    /// <summary>
    /// How long a producer may go quiet before its session is restarted.
    ///
    /// If the ffmpeg behind a stream dies, its channel simply goes quiet — it is never completed —
    /// so an unbounded wait would sit there forever and the supervisor would never learn anything
    /// was wrong. Harmless when the gate was only frame differencing and it merely stopped; with
    /// episodes it strands open records claiming someone is still there. Ten frames' grace, so an
    /// occasional late frame is not mistaken for a dead producer.
    /// </summary>
    private static TimeSpan IdleTimeout(double fps) =>
        TimeSpan.FromSeconds(Math.Max(10.0, 10.0 / Math.Max(fps, 0.01)));

    /// <summary>
    /// Persists finished detection episodes and puts every episode on the live feed.
    ///
    /// Only a *closed* episode is written. An open one is broadcast and nothing else, for two
    /// reasons: it arrives once a frame while the thing is still there, so storing it would be a
    /// write a second per camera restating a position the episode's own track carries in full when
    /// it ends; and an open episode in storage is a claim nothing can retract, so a process killed
    /// mid-episode would leave a record insisting forever that someone was still standing there.
    ///
    /// Failures are logged and swallowed: a database that is briefly unavailable must not take down
    /// the camera's session, because the gate in front of the vision model is the more important of
    /// the two jobs and it needs no storage to work.
    /// </summary>
    private async Task StoreAsync(
        Camera camera,
        IReadOnlyList<ObjectEpisode> episodes,
        CancellationToken cancellationToken)
    {
        foreach (ObjectEpisode episode in episodes)
        {
            var document = new DetectionDocument
            {
                Id = episode.Id,
                CameraId = camera.Id,
                ReceivedAt = DateTimeOffset.UtcNow,
                Timestamp = episode.StartedAt,
                EndedAt = episode.EndedAt,
                Label = episode.Label,
                PeakConfidence = episode.PeakConfidence,
                PeakFrameAt = episode.PeakFrameAt,
                FrameCount = episode.FrameCount,
                BestBox = episode.BestBox is { } best ? Box(best) : null,
                Track =
                [
                    .. episode.Track.Select(sample => new DetectionTrackSample
                    {
                        At = sample.At,
                        Box = sample.Box is { } box ? Box(box) : null,
                    }),
                ],
                IsAlert = episode.IsAlert,
                Source = TelemetrySource.Server,
            };

            // Broadcast first, and outside the store's try. An open episode is a position
            // heartbeat — superseded by the next frame's, and free for a slow subscriber to lose.
            // A close is the only notification that ever goes out about this episode ending, so it
            // travels in the lane that does not drop; see EventBroadcaster.
            //
            // Ahead of the write rather than after it because it does not depend on the write. The
            // catch below is there so a database that is briefly unavailable does not take down the
            // camera's session — sequenced the other way it also swallowed the live push, and a
            // Mongo hiccup on a close became an App that went on drawing the object for the rest of
            // its session.
            _events.Publish(
                new LiveEvent(camera.Id, document.Type, document),
                droppable: episode.EndedAt is null);

            // Above the early return, so an alert reaches the queue while its episode is still
            // open. Whether it is one was decided when it opened and is never revised, so waiting
            // for the close would delay the queue by AbsenceSeconds — half a minute after the thing
            // somebody is being told about, by which time the footage is rolling out of the preview
            // buffer the clip is cut from. Idempotent on the episode's id, which is what makes it
            // safe on a path that runs once a frame for as long as the object is in view.
            if (document.IsAlert)
            {
                await _alerts.RaiseObjectAsync(camera, document, cancellationToken);
            }

            if (episode.EndedAt is null)
            {
                continue;
            }

            try
            {
                await _repository.UpsertDetectionAsync(document, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Could not store a {Label} detection for camera {CameraId}.",
                    episode.Label, camera.Id);
            }
        }
    }

    private static DetectionBox Box(ScoredBox scored) => new()
    {
        X = scored.Box.X,
        Y = scored.Box.Y,
        Width = scored.Box.Width,
        Height = scored.Box.Height,
        Score = scored.Score,
    };

    private async Task StopAsync(string cameraId)
    {
        if (!_sessions.Remove(cameraId, out Session? session))
        {
            return;
        }

        _logger.LogInformation("Stopping AI for camera {CameraId}.", cameraId);

        await session.Cancellation.CancelAsync();
        try { await session.Task; } catch { /* expected on cancellation */ }
        session.Cancellation.Dispose();
    }

    private async Task StopAllAsync()
    {
        foreach (string id in _sessions.Keys.ToList())
        {
            await StopAsync(id);
        }
    }

    private sealed class Session(
        string signature, CancellationTokenSource cancellation, bool usesDetection)
    {
        public string Signature { get; } = signature;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        /// <summary>Whether this camera runs the object detector, as opposed to audio only. What the
        /// status page divides the inference budget by.</summary>
        public bool UsesDetection { get; } = usesDetection;

        public Task Task { get; set; } = Task.CompletedTask;
    }
}
