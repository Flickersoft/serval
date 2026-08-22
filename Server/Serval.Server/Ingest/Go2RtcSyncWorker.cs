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

    // The sources we last registered per camera id. Lets reconcile notice when a camera's sources
    // change (a new live-stream URL, or talk-back toggled, which flips the backchannel suffix) and
    // re-register it — go2rtc only tells us stream names exist, not what they draw on.
    private readonly Dictionary<string, IReadOnlyList<string>> _registered = new(StringComparer.Ordinal);

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
    /// register (or re-register) a stream for every eligible camera whose sources go2rtc is missing
    /// or has stale, and delete every stream we no longer want. <paramref name="registered"/>
    /// is the caller-owned memory of what sources each stream was last given, so a change is
    /// re-pushed. Static and pure over its inputs, the client, and that map — so tests drive it
    /// directly with a fake client and a camera list, no worker instance and no Mongo.
    /// </summary>
    internal static async Task ReconcileAsync(
        IReadOnlyList<Camera> cameras,
        IGo2RtcClient go2rtc,
        IDictionary<string, IReadOnlyList<string>> registered,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var desired = cameras
            .Where(IsWebRtcEligible)
            .ToDictionary(c => c.Id, SourcesFor, StringComparer.Ordinal);

        IReadOnlySet<string> existing = await go2rtc.ListStreamNamesAsync(cancellationToken);

        // Register a stream when go2rtc doesn't have it, or when its sources changed since we last
        // pushed them (new live-stream URL, or talk-back toggled → the backchannel suffix flipped).
        // PUT is a replace, so re-pushing is all it takes; a restart re-adds it.
        foreach ((string id, IReadOnlyList<string> sources) in desired)
        {
            bool missing = !existing.Contains(id);
            bool drifted = !registered.TryGetValue(id, out IReadOnlyList<string>? last)
                || !last.SequenceEqual(sources, StringComparer.Ordinal);
            if (missing || drifted)
            {
                await go2rtc.PutStreamAsync(id, sources, cancellationToken);
                registered[id] = sources;
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
    /// The sources a camera's go2rtc stream draws on: the camera itself, and a rendering of its
    /// audio into every codec a WebRTC consumer may ask for.
    ///
    /// <para><b>Why the second one exists.</b> Cameras send AAC, which WebRTC cannot carry. A
    /// consumer that negotiates an audio m-line against a passthrough source therefore gets one
    /// that is answered and then never filled — and a player waiting on a track that never arrives
    /// shows nothing, while the video beside it flows perfectly. Nothing on either side reports a
    /// fault. Rendering the audio into codecs WebRTC does carry is what lets go2rtc answer that
    /// m-line honestly.</para>
    ///
    /// <para><b>Why all three, and not Opus alone.</b> go2rtc will answer an m-line with any codec
    /// it believes it can produce, and it believes that of G.711 whether or not a source actually
    /// supplies it. So a consumer offering <c>opus,PCMU,PCMA</c> — which the Google Home app on a
    /// phone does — could be answered <c>PCMU</c> and then handed silence, reproducing the exact
    /// fault above on a surface where Opus alone looked sufficient. It is per-negotiation, which is
    /// why it presents as a camera that worked and then stopped. With all three offered, go2rtc
    /// picks Opus and can honour the other two if it ever does not.</para>
    ///
    /// <para><b>It costs nothing until something asks for it.</b> go2rtc negotiates per consumer
    /// across all of a stream's sources and starts each one only when a track it supplies is
    /// actually wanted, so a viewer who takes video alone never launches it. The source names this
    /// stream rather than the camera, so it draws on the session go2rtc already holds — pointing it
    /// at the camera instead opens a second RTSP session, which cameras that cap concurrent
    /// sessions refuse, and the picture cuts out on a cycle as the transcode is relaunched.</para>
    ///
    /// <para>Talk-back is unaffected: the camera's backchannel belongs to the first source, which
    /// still holds the RTSP session itself, and go2rtc's rule that only one source may claim a
    /// backchannel is satisfied because this one never opens a camera session at all.</para>
    ///
    /// <para>A camera with no audio at all simply fails this source, leaving the m-line unfilled —
    /// exactly what a lone passthrough source does today, so there is nothing to guard against.</para>
    /// </summary>
    internal static IReadOnlyList<string> SourcesFor(Camera camera) =>
        [SourceFor(camera), $"ffmpeg:{camera.Id}#audio=opus#audio=pcmu#audio=pcma"];

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
    ///
    /// <para><b>Internal because the Google Home integration must ask the same question.</b> The
    /// set of cameras it offers Google has to be exactly the set registered here — a camera Google
    /// knows about but go2rtc has no stream for is not a degraded experience, it is a stream
    /// request that succeeds and then never connects, with the failure landing on a display in
    /// somebody's kitchen. Two filters that merely agree today would drift; there is one, and
    /// <c>GoogleHomeSyncTests</c> drives a single camera list through both to prove it.</para>
    /// </summary>
    internal static bool IsWebRtcEligible(Camera camera) =>
        camera.Enabled
        && camera.LiveStream is { Url: var url }
        && !string.IsNullOrWhiteSpace(url)
        && !SourceArguments.IsFile(url);
}
