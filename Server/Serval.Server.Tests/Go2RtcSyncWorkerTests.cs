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
        ReconcileAsync(go2rtc, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal), cameras);

    // Shared-memory form: pass the same map across calls to exercise drift/idempotency.
    private static async Task ReconcileAsync(
        FakeGo2Rtc go2rtc, IDictionary<string, IReadOnlyList<string>> registered, params Camera[] cameras) =>
        await Go2RtcSyncWorker.ReconcileAsync(cameras, go2rtc, registered, NullLogger.Instance, CancellationToken.None);

    // ------------------------------------------------- the transcoded audio source

    /// <summary>
    /// A camera is registered as two sources, in this order: the camera, then a rendering of its
    /// audio. Cameras send AAC, which WebRTC cannot carry, so a consumer that negotiates audio
    /// against the camera alone gets an m-line that is answered and never filled — and a player
    /// waiting on a track that never arrives shows nothing at all, with no error anywhere.
    /// </summary>
    [Fact]
    public async Task A_camera_offers_a_transcoded_audio_source_beside_itself()
    {
        var go2rtc = new FakeGo2Rtc();

        await ReconcileAsync(go2rtc, Rtsp("front"));

        Assert.Equal(
            ["rtsp://cam/front#backchannel=0", "ffmpeg:front#audio=opus#audio=pcmu#audio=pcma"],
            go2rtc.Streams["front"]);
    }

    /// <summary>
    /// All three WebRTC audio codecs, not Opus alone.
    ///
    /// <para>go2rtc answers an m-line with any codec it believes it can produce, and it believes
    /// that of G.711 whether or not a source supplies it. The Google Home app on a phone offers
    /// <c>opus,PCMU,PCMA</c> and was answered <c>PCMU</c> against an Opus-only stream — the same
    /// answered-and-never-filled fault, on a surface where Opus alone looked sufficient. It is
    /// decided per negotiation, so it presents as a camera that worked and then stopped.</para>
    /// </summary>
    [Theory]
    [InlineData("opus")]
    [InlineData("pcmu")]
    [InlineData("pcma")]
    public async Task Every_webrtc_audio_codec_a_consumer_may_pick_is_supplied(string codec)
    {
        var go2rtc = new FakeGo2Rtc();

        await ReconcileAsync(go2rtc, Rtsp("front"));

        Assert.Contains($"#audio={codec}", go2rtc.Streams["front"][1], StringComparison.Ordinal);
    }

    /// <summary>
    /// The Opus source names the camera's <em>stream</em>, not its URL, so it draws on the session
    /// go2rtc already holds. Pointing it at the camera opens a second RTSP session, which cameras
    /// that cap concurrent sessions refuse — and the symptom is the picture cutting out on a cycle
    /// as the transcode is relaunched, not an error.
    /// </summary>
    [Fact]
    public async Task The_opus_source_draws_on_the_stream_rather_than_the_camera()
    {
        var go2rtc = new FakeGo2Rtc();

        await ReconcileAsync(go2rtc, Rtsp("front"));

        Assert.DoesNotContain("rtsp://", go2rtc.Streams["front"][1]);
    }

    /// <summary>
    /// Talk-back rides the camera's own backchannel, so the camera has to be the first source and
    /// keep holding the RTSP session itself. The Opus source opens no camera session at all, which
    /// is also what satisfies go2rtc's rule that only one source may claim a backchannel.
    /// </summary>
    [Fact]
    public async Task Talk_back_stays_on_the_camera_source()
    {
        var go2rtc = new FakeGo2Rtc();

        await ReconcileAsync(go2rtc, Rtsp("front", twoWay: true));

        Assert.Equal("rtsp://cam/front", go2rtc.Streams["front"][0]);
        Assert.DoesNotContain("backchannel", go2rtc.Streams["front"][1]);
    }

    [Fact]
    public async Task Enabled_rtsp_camera_is_registered_with_backchannel_disabled()
    {
        var go2rtc = new FakeGo2Rtc();

        await ReconcileAsync(go2rtc, Rtsp("front"));

        // Talk-back off by default → backchannel disabled at the source so go2rtc doesn't probe it.
        Assert.Equal("rtsp://cam/front#backchannel=0", go2rtc.SourceOf("front"));
    }

    [Fact]
    public async Task Talk_back_camera_keeps_the_backchannel_enabled()
    {
        var go2rtc = new FakeGo2Rtc();

        await ReconcileAsync(go2rtc, Rtsp("intercom", twoWay: true));

        // With two-way audio, no #backchannel=0 suffix — go2rtc's default backchannel stays on.
        Assert.Equal("rtsp://cam/intercom", go2rtc.SourceOf("intercom"));
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

        Assert.Equal(url, go2rtc.SourceOf("front"));
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

        Assert.Equal("rtsp://cam/main#backchannel=0", go2rtc.SourceOf("front"));
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

        Assert.Equal("rtsp://cam/sub#backchannel=0", go2rtc.SourceOf("front"));
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
        var registered = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        await ReconcileAsync(go2rtc, registered, Rtsp("front")); // put #1
        await ReconcileAsync(go2rtc, registered, Rtsp("front")); // no change → no put

        Assert.Equal(1, go2rtc.PutCount);
    }

    [Fact]
    public async Task Toggling_talk_back_re_registers_the_stream()
    {
        var go2rtc = new FakeGo2Rtc();
        var registered = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        await ReconcileAsync(go2rtc, registered, Rtsp("intercom"));                 // backchannel off
        await ReconcileAsync(go2rtc, registered, Rtsp("intercom", twoWay: true));   // now on → re-put

        Assert.Equal(2, go2rtc.PutCount);
        Assert.Equal("rtsp://cam/intercom", go2rtc.SourceOf("intercom")); // suffix dropped
    }

    [Fact]
    public async Task A_stream_missing_from_go2rtc_is_re_registered_even_if_we_thought_we_had_it()
    {
        var go2rtc = new FakeGo2Rtc();
        var registered = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

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
        go2rtc.Streams["front"] = ["rtsp://cam/front"];

        // "front" no longer in the registry at all (the camera was deleted).
        await ReconcileAsync(go2rtc /* no cameras */);

        Assert.False(go2rtc.Streams.ContainsKey("front"));
    }

    [Fact]
    public async Task Disabled_camera_stream_is_removed()
    {
        var go2rtc = new FakeGo2Rtc();
        go2rtc.Streams["front"] = ["rtsp://cam/front"];

        // The camera is still known, but now disabled → its stream should be pruned.
        await ReconcileAsync(go2rtc, Rtsp("front", enabled: false));

        Assert.False(go2rtc.Streams.ContainsKey("front"));
    }

    [Fact]
    public async Task Camera_switched_from_rtsp_to_file_has_its_stream_removed()
    {
        var go2rtc = new FakeGo2Rtc();
        go2rtc.Streams["cam"] = ["rtsp://cam/cam"];

        // Same id, now a file source → no longer WebRTC-eligible, still a known camera → pruned.
        await ReconcileAsync(go2rtc, File("cam"));

        Assert.False(go2rtc.Streams.ContainsKey("cam"));
    }

    [Fact]
    public async Task Orphan_stream_with_no_camera_is_reaped()
    {
        var go2rtc = new FakeGo2Rtc();
        go2rtc.Streams["orphan"] = ["rtsp://somewhere/else"];

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
        go2rtc.Streams["stale"] = ["rtsp://cam/stale"];   // known camera now disabled → remove
        go2rtc.Streams["keep"] = ["rtsp://cam/keep"];     // still enabled → leave

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
        public Dictionary<string, IReadOnlyList<string>> Streams { get; } = new(StringComparer.Ordinal);
        public int PutCount { get; private set; }

        /// <summary>
        /// The camera's own source. It is always the first of a stream's sources — the Opus one
        /// after it names this stream, so it can only come second. See <c>SourcesFor</c>.
        /// </summary>
        public string SourceOf(string name) => Streams[name][0];

        public Task<IReadOnlySet<string>> ListStreamNamesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(Streams.Keys, StringComparer.Ordinal));

        public Task PutStreamAsync(string name, IReadOnlyList<string> sources, CancellationToken cancellationToken)
        {
            PutCount++;
            Streams[name] = sources;
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
