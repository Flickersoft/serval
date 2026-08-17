using Microsoft.Extensions.Logging.Abstractions;
using Serval.Server.Cameras;
using Serval.Server.Ingest;

namespace Serval.Server.Tests;

/// <summary>
/// The go2rtc sync reconcile is the WebRTC side's equivalent of the ingest manager's reconcile:
/// the camera registry is the source of truth, and one pass makes go2rtc's stream set match it.
/// What matters: enabled cameras with a network source get registered (talk-back off → backchannel
/// disabled at the source), ones that vanish/disable get removed, file (test) cameras are never
/// registered, and a changed source is re-pushed.
/// </summary>
public class Go2RtcSyncWorkerTests
{
    private static Camera Rtsp(string id, bool enabled = true, bool twoWay = false) =>
        Source(id, $"rtsp://cam/{id}", enabled, twoWay);

    private static Camera File(string id) => Source(id, $"/media/{id}.mp4");

    private static Camera Source(string id, string url, bool enabled = true, bool twoWay = false) =>
        new()
        {
            Id = id,
            Name = id,
            Streams =
            [
                new CameraStream
                {
                    Name = "main",
                    Url = url,
                    Roles = [StreamRole.Record, StreamRole.Detect, StreamRole.Live],
                },
            ],
            Enabled = enabled,
            TwoWayAudio = twoWay,
        };

    // Default form: a fresh "registered" memory each call (a cold worker).
    private static Task ReconcileAsync(FakeGo2Rtc go2rtc, params Camera[] cameras) =>
        ReconcileAsync(go2rtc, new Dictionary<string, string>(StringComparer.Ordinal), cameras);

    // Shared-memory form: pass the same map across calls to exercise drift/idempotency.
    private static async Task ReconcileAsync(FakeGo2Rtc go2rtc, IDictionary<string, string> registered, params Camera[] cameras) =>
        await Go2RtcSyncWorker.ReconcileAsync(cameras, go2rtc, registered, NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task Enabled_rtsp_camera_is_registered_with_backchannel_disabled()
    {
        var go2rtc = new FakeGo2Rtc();

        await ReconcileAsync(go2rtc, Rtsp("front"));

        // Talk-back off by default → backchannel disabled at the source so go2rtc doesn't probe it.
        Assert.Equal("rtsp://cam/front#backchannel=0", go2rtc.Streams["front"]);
    }

    [Fact]
    public async Task Talk_back_camera_keeps_the_backchannel_enabled()
    {
        var go2rtc = new FakeGo2Rtc();

        await ReconcileAsync(go2rtc, Rtsp("intercom", twoWay: true));

        // With two-way audio, no #backchannel=0 suffix — go2rtc's default backchannel stays on.
        Assert.Equal("rtsp://cam/intercom", go2rtc.Streams["intercom"]);
    }

    /// <summary>
    /// <c>#backchannel</c> is an option of go2rtc's RTSP source. Appending it to anything else is
    /// meaningless at best, so the suffix is scheme-conditional, not just talk-back-conditional.
    /// </summary>
    [Theory]
    [InlineData("http://cam/flv?port=1935&app=bcs&stream=channel0_ext.bcs")]
    [InlineData("rtmp://cam/live/front")]
    [InlineData("srt://cam:9000")]
    public async Task A_non_rtsp_source_is_registered_verbatim(string url)
    {
        var go2rtc = new FakeGo2Rtc();

        await ReconcileAsync(go2rtc, Source("front", url));

        Assert.Equal(url, go2rtc.Streams["front"]);
    }

    [Fact]
    public async Task The_registered_source_is_the_stream_carrying_the_live_role()
    {
        var go2rtc = new FakeGo2Rtc();
        Camera camera = Rtsp("front");
        camera.Streams =
        [
            new CameraStream
            {
                Name = "main",
                Url = "rtsp://cam/main",
                Roles = [StreamRole.Record, StreamRole.Live],
            },
            new CameraStream { Name = "sub", Url = "rtsp://cam/sub", Roles = [StreamRole.Detect] },
        ];

        await ReconcileAsync(go2rtc, camera);

        Assert.Equal("rtsp://cam/main#backchannel=0", go2rtc.Streams["front"]);
    }

    /// <summary>
    /// Validation requires a live role, so this shape only reaches the worker on a document that
    /// bypassed it. It must register nothing rather than falling back to the recorded stream,
    /// which would put a 4K main stream on WebRTC unasked.
    /// </summary>
    [Fact]
    public async Task A_camera_with_no_live_role_is_not_registered()
    {
        var go2rtc = new FakeGo2Rtc();
        Camera camera = Rtsp("front");
        camera.Streams =
        [
            new CameraStream
            {
                Name = "main",
                Url = "rtsp://cam/main",
                Roles = [StreamRole.Record, StreamRole.Detect],
            },
        ];

        await ReconcileAsync(go2rtc, camera);

        Assert.Empty(go2rtc.Streams);
    }

    [Fact]
    public async Task An_explicit_live_role_moves_the_registered_source()
    {
        var go2rtc = new FakeGo2Rtc();
        Camera camera = Rtsp("front");
        camera.Streams =
        [
            new CameraStream { Name = "main", Url = "rtsp://cam/main", Roles = [StreamRole.Record] },
            new CameraStream
            {
                Name = "sub",
                Url = "rtsp://cam/sub",
                Roles = [StreamRole.Detect, StreamRole.Live],
            },
        ];

        await ReconcileAsync(go2rtc, camera);

        Assert.Equal("rtsp://cam/sub#backchannel=0", go2rtc.Streams["front"]);
    }

    [Fact]
    public async Task File_camera_is_never_registered()
    {
        var go2rtc = new FakeGo2Rtc();

        await ReconcileAsync(go2rtc, File("testcam"));

        Assert.Empty(go2rtc.Streams);
    }

    [Fact]
    public async Task Disabled_camera_is_not_registered()
    {
        var go2rtc = new FakeGo2Rtc();

        await ReconcileAsync(go2rtc, Rtsp("front", enabled: false));

        Assert.Empty(go2rtc.Streams);
    }

    [Fact]
    public async Task Unchanged_camera_is_not_put_again_on_a_second_pass()
    {
        var go2rtc = new FakeGo2Rtc();
        var registered = new Dictionary<string, string>(StringComparer.Ordinal);

        await ReconcileAsync(go2rtc, registered, Rtsp("front")); // put #1
        await ReconcileAsync(go2rtc, registered, Rtsp("front")); // no change → no put

        Assert.Equal(1, go2rtc.PutCount);
    }

    [Fact]
    public async Task Toggling_talk_back_re_registers_the_stream()
    {
        var go2rtc = new FakeGo2Rtc();
        var registered = new Dictionary<string, string>(StringComparer.Ordinal);

        await ReconcileAsync(go2rtc, registered, Rtsp("intercom"));                 // backchannel off
        await ReconcileAsync(go2rtc, registered, Rtsp("intercom", twoWay: true));   // now on → re-put

        Assert.Equal(2, go2rtc.PutCount);
        Assert.Equal("rtsp://cam/intercom", go2rtc.Streams["intercom"]); // suffix dropped
    }

    [Fact]
    public async Task A_stream_missing_from_go2rtc_is_re_registered_even_if_we_thought_we_had_it()
    {
        var go2rtc = new FakeGo2Rtc();
        var registered = new Dictionary<string, string>(StringComparer.Ordinal);

        await ReconcileAsync(go2rtc, registered, Rtsp("front")); // put #1, remembered
        go2rtc.Streams.Clear();                                  // go2rtc restarted, lost its streams
        await ReconcileAsync(go2rtc, registered, Rtsp("front")); // missing again → re-put

        Assert.Equal(2, go2rtc.PutCount);
        Assert.True(go2rtc.Streams.ContainsKey("front"));
    }

    [Fact]
    public async Task Deleted_camera_stream_is_removed()
    {
        var go2rtc = new FakeGo2Rtc();
        go2rtc.Streams["front"] = "rtsp://cam/front";

        // "front" no longer in the registry at all (the camera was deleted).
        await ReconcileAsync(go2rtc /* no cameras */);

        Assert.False(go2rtc.Streams.ContainsKey("front"));
    }

    [Fact]
    public async Task Disabled_camera_stream_is_removed()
    {
        var go2rtc = new FakeGo2Rtc();
        go2rtc.Streams["front"] = "rtsp://cam/front";

        // The camera is still known, but now disabled → its stream should be pruned.
        await ReconcileAsync(go2rtc, Rtsp("front", enabled: false));

        Assert.False(go2rtc.Streams.ContainsKey("front"));
    }

    [Fact]
    public async Task Camera_switched_from_rtsp_to_file_has_its_stream_removed()
    {
        var go2rtc = new FakeGo2Rtc();
        go2rtc.Streams["cam"] = "rtsp://cam/cam";

        // Same id, now a file source → no longer WebRTC-eligible, still a known camera → pruned.
        await ReconcileAsync(go2rtc, File("cam"));

        Assert.False(go2rtc.Streams.ContainsKey("cam"));
    }

    [Fact]
    public async Task Orphan_stream_with_no_camera_is_reaped()
    {
        var go2rtc = new FakeGo2Rtc();
        go2rtc.Streams["orphan"] = "rtsp://somewhere/else";

        // The sidecar is Serval-dedicated, so a stream with no matching desired camera is stale
        // (e.g. left over from a deleted camera) and gets cleaned up; the live camera stays.
        await ReconcileAsync(go2rtc, Rtsp("front"));

        Assert.False(go2rtc.Streams.ContainsKey("orphan"));
        Assert.True(go2rtc.Streams.ContainsKey("front"));
    }

    [Fact]
    public async Task Mixed_registry_converges_in_one_pass()
    {
        var go2rtc = new FakeGo2Rtc();
        go2rtc.Streams["stale"] = "rtsp://cam/stale";   // known camera now disabled → remove
        go2rtc.Streams["keep"] = "rtsp://cam/keep";     // still enabled → leave

        await ReconcileAsync(
            go2rtc,
            Rtsp("keep"),
            Rtsp("stale", enabled: false),
            Rtsp("new"),          // enabled, missing from go2rtc → add
            File("testcam"));     // file → never registered

        Assert.Equal(
            new[] { "keep", "new" },
            go2rtc.Streams.Keys.OrderBy(x => x).ToArray());
    }

    /// <summary>An in-memory stand-in for go2rtc: its stream table is the sidecar's state.</summary>
    private sealed class FakeGo2Rtc : IGo2RtcClient
    {
        public Dictionary<string, string> Streams { get; } = new(StringComparer.Ordinal);
        public int PutCount { get; private set; }

        public Task<IReadOnlySet<string>> ListStreamNamesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(Streams.Keys, StringComparer.Ordinal));

        public Task PutStreamAsync(string name, string src, CancellationToken cancellationToken)
        {
            PutCount++;
            Streams[name] = src;
            return Task.CompletedTask;
        }

        public Task DeleteStreamAsync(string name, CancellationToken cancellationToken)
        {
            Streams.Remove(name);
            return Task.CompletedTask;
        }

        public Task<string> ExchangeSdpAsync(string streamName, string offerSdp, CancellationToken cancellationToken) =>
            Task.FromResult("answer");
    }
}
