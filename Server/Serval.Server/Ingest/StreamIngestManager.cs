using Microsoft.Extensions.Options;
using Serval.Server.Cameras;
using Serval.Server.Configuration;
using Serval.Server.Recordings;
using Serval.Server.Snapshots;

namespace Serval.Server.Ingest;

/// <summary>
/// Keeps one running ffmpeg session per enabled camera, matching reality to the registry.
/// Every few seconds it reconciles: start sessions for newly-enabled cameras, stop them for
/// deleted or disabled ones, and restart any whose source changed. A dead source is retried
/// forever with capped backoff — the same never-give-up shape as the module's telemetry sync.
///
/// So the API doesn't have to notify anything: writing the camera registry is enough.
/// </summary>
public sealed class StreamIngestManager : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(5);

    private readonly CameraRepository _cameras;
    private readonly RecordingIndex _recordings;
    private readonly SnapshotBroadcaster _snapshots;
    private readonly DetectFrameBroadcaster _detectFrames;
    private readonly PreviewRingIndex _previewRing;
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly FfmpegCapabilities _capabilities;
    private readonly string _mediaRoot;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<StreamIngestManager> _logger;

    private readonly Dictionary<string, RunningSession> _running = new();

    /// <summary>
    /// The last fault logged per camera, so a permanently misconfigured one is reported when it
    /// changes rather than every reconcile tick. This loop runs every five seconds in a process
    /// that stays up for months; without the dedup the message explaining what to fix is the thing
    /// that drowns the log.
    /// </summary>
    private readonly Dictionary<string, string> _reportedFaults = new(StringComparer.Ordinal);

    public StreamIngestManager(
        CameraRepository cameras,
        RecordingIndex recordings,
        SnapshotBroadcaster snapshots,
        DetectFrameBroadcaster detectFrames,
        PreviewRingIndex previewRing,
        IOptionsMonitor<ServerOptions> options,
        FfmpegCapabilities capabilities,
        ILoggerFactory loggerFactory)
    {
        _cameras = cameras;
        _recordings = recordings;
        _snapshots = snapshots;
        _detectFrames = detectFrames;
        _previewRing = previewRing;
        _options = options;
        _capabilities = capabilities;

        // The media root is where files already are. Moving it is not something a running server
        // can be talked into, which is why it stays environment-only and is read once here.
        _mediaRoot = options.CurrentValue.Media.Root;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<StreamIngestManager>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DetectFrameReader.WarnIfNotInMemory(_options.CurrentValue.Ingest, _logger);

        using var timer = new PeriodicTimer(ReconcileInterval);
        do
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A transient Mongo blip shouldn't take the manager down; try again next tick.
                _logger.LogError(ex, "Ingest reconcile failed; retrying next tick.");
            }
        }
        while (await TaskWaits.SafeWaitAsync(timer, stoppingToken));

        await StopAllAsync();
    }

    private async Task ReconcileAsync(CancellationToken stoppingToken)
    {
        List<Camera> cameras = await _cameras.ListAsync(stoppingToken);

        // One read per pass, shared across every camera in it, so a settings change applied
        // mid-reconcile cannot start some cameras on the old value and stop others on the new.
        IngestOptions ingest = _options.CurrentValue.Ingest;

        // Same rules as the API's 400, applied to what is already stored — a camera can reach this
        // point without ever having passed them. Rejecting one silently would let an upgrade stop
        // recording a camera with nothing anywhere saying why.
        var desired = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Camera camera in cameras.Where(c => c.Enabled))
        {
            if (CameraRegistryCheck.Fault(camera, ingest, _capabilities) is { } fault)
            {
                if (!_reportedFaults.TryGetValue(camera.Id, out string? reported) || reported != fault)
                {
                    _logger.LogError("Camera {CameraId} will not be ingested: {Fault}", camera.Id, fault);
                    _reportedFaults[camera.Id] = fault;
                }

                continue;
            }

            _reportedFaults.Remove(camera.Id);
            desired[camera.Id] = Signature(camera, ingest);
        }

        // Stop sessions that are gone, disabled, or whose source changed.
        List<KeyValuePair<string, RunningSession>> stopping = _running
            .Where(entry => !desired.TryGetValue(entry.Key, out string? signature)
                || signature != entry.Value.Signature)
            .ToList();

        if (stopping.Count > 0)
        {
            // Together rather than one after another. The signature covers the server-wide ingest
            // settings, so editing one of them moves every camera's signature in the same pass, and
            // a teardown waits on ffmpeg dying and its harvest loops draining — in series that is
            // one camera's wait multiplied by the whole registry, spent out of a shutdown budget
            // that is not generous enough to pay it.
            await Task.WhenAll(stopping.Select(entry => entry.Value.StopAsync()));

            foreach ((string id, _) in stopping)
            {
                _running.Remove(id);
                _snapshots.Forget(id);

                // The watcher clears its own entry on the way out, but a camera being removed
                // outright is the case where it might not: nothing restarts to Reset the files, so
                // an index left behind would name segments that the next Reset deletes.
                _previewRing.Forget(id);
            }
        }

        // Start sessions for anything desired but not running.
        foreach (Camera camera in cameras)
        {
            if (desired.ContainsKey(camera.Id) && !_running.ContainsKey(camera.Id))
            {
                _running[camera.Id] = Supervise(camera, Signature(camera, ingest), ingest);
            }
        }

        // After both, so a camera that is merely restarting is back in _running and keeps its
        // directory rather than being swept and immediately recreated.
        SweepAbandonedFrameDirs(ingest);
    }

    /// <summary>
    /// Removes the detect staging directory of every camera that has no session running.
    ///
    /// <see cref="DetectFrameReader.WatchAsync"/> deletes frames as it reads them, but it only runs
    /// while a session does, and <see cref="DetectFrameReader.Reset"/> only ever empties the
    /// directory of a camera that is <em>starting</em> one. Between them nothing owns the directory
    /// of a camera that has been deleted, disabled, renamed, or left unable to start: whatever
    /// ffmpeg wrote between the reader's last pass and the process dying stays there, on a mount
    /// sized for frames that live milliseconds. Nothing reclaims it, and the next start of that
    /// camera — if it ever comes — is what would.
    ///
    /// <para>Ordinary rather than exceptional, so it is not logged per pass: this runs every
    /// reconcile and finds nothing almost every time. Deleting is best-effort for the reason every
    /// other frame removal is — a directory that will not go is one this tries again in five
    /// seconds.</para>
    /// </summary>
    private void SweepAbandonedFrameDirs(IngestOptions ingest)
    {
        try
        {
            if (!Directory.Exists(ingest.DetectFrameDir))
            {
                return;
            }

            foreach (string directory in Directory.EnumerateDirectories(ingest.DetectFrameDir))
            {
                // The directory is named for the camera, so this is the camera's id.
                if (_running.ContainsKey(Path.GetFileName(directory)))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left for the next pass.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex, "Could not sweep abandoned detect frame directories under '{Path}'.",
                ingest.DetectFrameDir);
        }
    }

    /// <summary>
    /// A change in any of these means the ffmpeg commands must be rebuilt. Every input to the
    /// command line belongs here — anything omitted is a setting the user can change with no
    /// effect until the process happens to die, which is worse than not offering it. That covers
    /// the whole stream list (a role moving between streams changes which one is recorded and
    /// which produces snapshots), each stream's transcode, <c>RecordAudio</c>, which selects the
    /// audio mapping, and <c>Recording</c>, which decides whether the recorder runs at all — and
    /// with it whether the snapshot session has to be started separately.
    ///
    /// <para>It also covers the server-wide ingest settings, which are the same for every camera
    /// but are just as much a part of the command line. Those became changeable at runtime when the
    /// settings page arrived: without them here, shortening the segment length would be a value
    /// stored, reported and visibly in force everywhere except in the ffmpeg actually writing the
    /// segments — the exact silent failure this signature exists to prevent.</para>
    /// </summary>
    internal static string Signature(Camera c, IngestOptions ingest) =>
        string.Join(";", c.Streams.Select(s =>
            $"{s.Name}|{s.Url}|{string.Join(",", s.Roles ?? [])}"
            + $"|{s.Transcode?.Codec}@{s.Transcode?.Bitrate}"))
        + $"|recording={c.Recording}"
        + $"|audio={c.RecordAudio}"
        + $"|{Signature(ingest)}";

    /// <summary>The current backoff ceiling. See the note where the backoff is initialised.</summary>
    private TimeSpan MaxBackoff => TimeSpan.FromSeconds(_options.CurrentValue.Ingest.MaxReconnectSeconds);

    /// <summary>
    /// The ingest settings a session is built from — those that reach a command line, and
    /// <see cref="IngestOptions.StallTimeoutSeconds"/>, which the session reads once when it arms
    /// its watchdog. Both are fixed for the life of a session, so both need one to restart before a
    /// new value means anything.
    ///
    /// Deliberately not every property of <see cref="IngestOptions"/>: the reconnect backoff is read
    /// by the supervisor loop on each retry and is already live, so including it would restart every
    /// camera on the server to apply a value that needed no restart at all.
    /// </summary>
    private static string Signature(IngestOptions ingest) =>
        $"{ingest.FfmpegPath}|{ingest.FfprobePath}|{ingest.HwAccelDevice}|{ingest.Encoder}"
        + $"|{ingest.Bitrate}|{ingest.SegmentSeconds}|{ingest.SnapshotFps}|{ingest.SnapshotMaxMegapixels}"
        + $"|{ingest.DetectFps}|{ingest.DetectFrameWidth}|{ingest.DetectFrameDir}"
        + $"|{ingest.PreviewBufferSeconds}|{ingest.StallTimeoutSeconds}"
        + $"|{string.Join(",", ingest.ResolvedVideoPassthroughCodecs)}"
        + $"|{string.Join(",", ingest.ResolvedAudioPassthroughCodecs)}";

    /// <summary>
    /// Runs a camera's sessions in loops that restart them with capped exponential backoff, so a
    /// camera that's briefly unreachable recovers on its own. The loops end only when the
    /// session's token is cancelled by <see cref="ReconcileAsync"/> or shutdown.
    ///
    /// One session per camera normally; two when detection has a stream of its own, supervised
    /// independently so a failing sub stream costs snapshots and AI but never the recording. A
    /// camera with no record stream runs the snapshot session alone.
    /// </summary>
    private RunningSession Supervise(Camera camera, string signature, IngestOptions ingest)
    {
        var cts = new CancellationTokenSource();

        // One fixed category for every camera, with the id carried as a scope instead. A category
        // per camera can't be targeted by a Logging:LogLevel rule and turns into unbounded label
        // churn in a log aggregator; a scope stays queryable and filters as "Serval.Server.Ingest".
        ILogger sessionLogger = _loggerFactory.CreateLogger("Serval.Server.Ingest.Session");

        // Null for a camera set to keep nothing, by either of the two routes to it: no stream
        // carries the role, or Recording is switched off over one that does. Collapsed here
        // because nothing below this line distinguishes them — the role assignment survives the
        // switch so that turning it back on needs no other decision, and that is the only place
        // the difference lives.
        //
        // Detect is always present — validation requires it — so the task list below is never
        // empty.
        CameraStream? record = camera.Recording ? camera.RecordStream : null;
        CameraStream? detect = camera.DetectStream;

        var tasks = new List<Task>();

        if (record is not null)
        {
            tasks.Add(RunSupervised(
                "record",
                ct => new FfmpegStreamSession(
                    camera, record, ingest, _capabilities, _mediaRoot, _snapshots, _detectFrames,
                    _recordings, sessionLogger).RunAsync(ct),
                camera, sessionLogger, cts));
        }

        // A detect stream of its own means a second, much cheaper process for the snapshots. When
        // the recorded stream carries the detect role too, FfmpegStreamSession produces them
        // instead — the two conditions are complements, which is what keeps the pair from either
        // racing for snapshot.jpg or leaving nobody writing it. With nothing being recorded there
        // is nothing to ride along with, so this is the only session the camera gets — including
        // for a camera whose record stream declares 'detect' and has Recording switched off, where
        // reading the roles alone would leave nobody producing snapshots at all.
        if (detect is not null && record?.Roles.Contains(StreamRole.Detect) != true)
        {
            tasks.Add(RunSupervised(
                "detect",
                ct => new FfmpegSnapshotSession(
                    camera, detect, ingest, _mediaRoot, _snapshots, _detectFrames, _previewRing,
                    sessionLogger).RunAsync(ct),
                camera, sessionLogger, cts));
        }

        return new RunningSession(signature, cts, Task.WhenAll(tasks));
    }

    private Task RunSupervised(
        string role,
        Func<CancellationToken, Task> run,
        Camera camera,
        ILogger sessionLogger,
        CancellationTokenSource cts) =>
        Task.Run(async () =>
        {
            // Covers the sessions too — they log through the same logger. A message template
            // rather than a dictionary: it renders as "CameraId:front-door" for a human reading
            // the console, and still yields a named CameraId field under the JSON formatter.
            using IDisposable? scope = sessionLogger.BeginScope("CameraId:{CameraId}", camera.Id);

            // Read through the monitor on each use rather than captured here: the backoff dials
            // are the two ingest settings that need no session restart to apply, so they are
            // deliberately left out of the signature above and picked up on the next retry instead.
            TimeSpan backoff = TimeSpan.FromSeconds(_options.CurrentValue.Ingest.ReconnectSeconds);

            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await run(cts.Token);
                    // A clean exit resets the backoff.
                    backoff = TimeSpan.FromSeconds(_options.CurrentValue.Ingest.ReconnectSeconds);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    break;
                }
                catch (IngestConfigurationException ex)
                {
                    // Not a dead source — a camera or a server setting that cannot produce a
                    // working ffmpeg. Retrying it every two seconds would bury the one message
                    // that says what to change, so go straight to the cap and wait for an edit;
                    // ReconcileAsync restarts the session as soon as one lands.
                    sessionLogger.LogError(
                        "Camera {CameraId} {Role} stream cannot start: {Reason} This will not fix "
                        + "itself — edit the camera or the server configuration.",
                        camera.Id, role, ex.Message);
                    backoff = MaxBackoff;
                }
                catch (Exception ex)
                {
                    sessionLogger.LogError(ex,
                        "Camera {CameraId} {Role} stream failed; reconnecting in {Backoff}.",
                        camera.Id, role, backoff);
                }

                try
                {
                    await Task.Delay(backoff, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
        });

    /// <summary>
    /// Stops every session at once, on the way out.
    ///
    /// In parallel because this is the whole registry by definition and it is spending the host's
    /// shutdown timeout: a camera whose teardown waits on ffmpeg is a wait every other camera would
    /// otherwise queue behind, and a server killed part-way through leaves the ffmpegs it had not
    /// reached still running — writing frames nothing will ever read.
    /// </summary>
    private async Task StopAllAsync()
    {
        await Task.WhenAll(_running.Values.Select(session => session.StopAsync()));
        _running.Clear();
    }

    private sealed record RunningSession(string Signature, CancellationTokenSource Cts, Task Task)
    {
        public async Task StopAsync()
        {
            Cts.Cancel();
            try { await Task; } catch { /* expected on cancel */ }
            Cts.Dispose();
        }
    }
}
