using Microsoft.Extensions.Options;
using Serval.Server.Cameras;
using Serval.Server.Configuration;

namespace Serval.Server.Ingest;

/// <summary>
/// Mirrors the camera registry into the go2rtc sidecar: every enabled camera with a live stream
/// should have a go2rtc stream named after its id, and nothing else should. It reconciles on a
/// timer, the same registry-is-the-source-of-truth shape as <see cref="StreamIngestManager"/> — so
/// adding or disabling a camera is all it takes to gain or lose its WebRTC live view, with no
/// explicit wiring.
///
/// go2rtc pulls each source <em>lazily</em> (only when a viewer connects), so registering a stream
/// here costs nothing until someone opens the focused view — the always-on ffmpeg→HLS recording
/// remains the only constant consumer of the camera. File-source (test) cameras are skipped:
/// there is nothing for go2rtc to pull.
/// </summary>
public sealed class Go2RtcSyncWorker : PeriodicWorker
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(15);

    private readonly CameraRepository _cameras;
    private readonly IGo2RtcClient _go2rtc;
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly ILogger<Go2RtcSyncWorker> _logger;

    // The source string we last registered per camera id. Lets reconcile notice when a camera's
    // source changes (a new live-stream URL, or talk-back toggled, which flips the backchannel
    // suffix) and re-register it — go2rtc only tells us stream names exist, not their sources.
    private readonly Dictionary<string, string> _registered = new(StringComparer.Ordinal);

    /// <summary>Whether the last tick found WebRTC off, so the log records the change, not the state.</summary>
    private bool _idle;

    public Go2RtcSyncWorker(
        CameraRepository cameras,
        IGo2RtcClient go2rtc,
        IOptionsMonitor<ServerOptions> options,
        ILogger<Go2RtcSyncWorker> logger)
        : base(logger)
    {
        _cameras = cameras;
        _go2rtc = go2rtc;
        _options = options;
        _logger = logger;
    }

    protected override string Activity => "go2rtc reconcile";

    /// <summary>A go2rtc restart or a Mongo blip is routine; the next tick retries.</summary>
    protected override LogLevel FailureLevel => LogLevel.Warning;

    protected override TimeSpan Interval => ReconcileInterval;

    protected override async Task TickAsync(CancellationToken stoppingToken)
    {
        // Checked every tick rather than once at startup, so switching WebRTC on is a
        // setting rather than a redeploy. The worker keeps ticking while it is off — it is
        // the only thing that could notice it coming back — but does no work and talks to
        // nothing, which matters when there is no sidecar to talk to.
        if (Idle())
        {
            return;
        }

        List<Camera> cameras = await _cameras.ListAsync(stoppingToken);
        await ReconcileAsync(cameras, _go2rtc, _registered, _logger, stoppingToken);
    }

    /// <summary>
    /// Whether WebRTC is switched off, logging only when that changes. Clears the registered map on
    /// the way down, so switching back on re-pushes every stream rather than trusting a memory of
    /// what go2rtc held before — the sidecar may well have restarted in between.
    /// </summary>
    private bool Idle()
    {
        bool idle = !_options.CurrentValue.WebRtc.Enabled;

        if (idle != _idle)
        {
            _idle = idle;
            if (idle)
            {
                _registered.Clear();
            }

            _logger.LogInformation(
                idle
                    ? "WebRTC is switched off; the go2rtc sync worker is idle."
                    : "WebRTC is switched on; the go2rtc sync worker has resumed.");
        }

        return idle;
    }

    /// <summary>
    /// Brings go2rtc's stream set in line with the desired set for one snapshot of the registry:
    /// register (or re-register) a stream for every eligible camera whose source go2rtc is missing
    /// or has stale, and delete every stream we no longer want. <paramref name="registered"/>
    /// is the caller-owned memory of what source each stream was last given, so a changed source is
    /// re-pushed. Static and pure over its inputs, the client, and that map — so tests drive it
    /// directly with a fake client and a camera list, no worker instance and no Mongo.
    /// </summary>
    internal static async Task ReconcileAsync(
        IReadOnlyList<Camera> cameras,
        IGo2RtcClient go2rtc,
        IDictionary<string, string> registered,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var desired = cameras
            .Where(IsWebRtcEligible)
            .ToDictionary(c => c.Id, SourceFor, StringComparer.Ordinal);

        IReadOnlySet<string> existing = await go2rtc.ListStreamNamesAsync(cancellationToken);

        // Register a stream when go2rtc doesn't have it, or when its source changed since we last
        // pushed it (new live-stream URL, or talk-back toggled → the backchannel suffix flipped).
        // PUT is a replace, so re-pushing a changed source is all it takes; a restart re-adds it.
        foreach ((string id, string src) in desired)
        {
            bool missing = !existing.Contains(id);
            bool drifted = !registered.TryGetValue(id, out string? last) || last != src;
            if (missing || drifted)
            {
                await go2rtc.PutStreamAsync(id, src, cancellationToken);
                registered[id] = src;
                logger.LogInformation("Registered go2rtc stream for camera {CameraId}.", id);
            }
        }

        // Remove every stream that isn't currently desired — a camera that was deleted, disabled,
        // or switched to a file source. The sidecar is dedicated to Serval (its config declares no
        // streams; every one present was put here by this worker), so anything not in `desired` is
        // ours to reap. This is what cleans up a deleted camera, whose id is gone from the registry.
        foreach (string name in existing)
        {
            if (!desired.ContainsKey(name))
            {
                await go2rtc.DeleteStreamAsync(name, cancellationToken);
                registered.Remove(name);
                logger.LogInformation("Removed go2rtc stream {StreamName}.", name);
            }
        }
    }

    /// <summary>
    /// The go2rtc source for a camera: its live stream's URL, with <c>#backchannel=0</c> appended
    /// unless talk-back is enabled. go2rtc probes the backchannel by default and that probe breaks
    /// some cameras, so the safe default is to disable it and opt in per camera via
    /// <c>TwoWayAudio</c>.
    ///
    /// The suffix is an option of go2rtc's RTSP source specifically, so it is only appended to an
    /// RTSP URL — on an HTTP-FLV or SRT source it would be meaningless at best.
    ///
    /// Note the codec is passed through untouched: go2rtc negotiates per viewer, so an HEVC stream
    /// reaches a browser that advertises H265 as-is and is transcoded only for one that doesn't.
    /// </summary>
    internal static string SourceFor(Camera camera)
    {
        string url = camera.LiveStream!.Url;
        return camera.TwoWayAudio || !SourceArguments.IsRtsp(url) ? url : url + "#backchannel=0";
    }

    /// <summary>
    /// WebRTC live is for enabled cameras with a real network source. It follows the camera's
    /// <c>Live</c> role, which is assigned explicitly to a stream and resolves to nothing else —
    /// so this is independent of whether the camera records at all. Test file cameras are skipped:
    /// go2rtc has nothing to pull.
    /// </summary>
    private static bool IsWebRtcEligible(Camera camera) =>
        camera.Enabled
        && camera.LiveStream is { Url: var url }
        && !string.IsNullOrWhiteSpace(url)
        && !SourceArguments.IsFile(url);
}
