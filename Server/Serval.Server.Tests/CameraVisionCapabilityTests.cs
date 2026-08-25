using Microsoft.Extensions.Logging.Abstractions;
using Serval.Ai;
using Serval.Server.Ai;
using Serval.Server.Cameras;

namespace Serval.Server.Tests;

/// <summary>
/// Which capabilities are enough to make a camera's frames worth watching, and which gate runs
/// once they are.
/// </summary>
public class CameraVisionCapabilityTests
{
    [Theory]
    // The case that was wrong first: the scene-description worker is only registered when a 2.3 GB
    // vision model is on disk, while the detector needs one a couple of hundred times smaller.
    // Requiring the worker meant a host with the detector and no vision model — by far the likelier
    // first deployment — silently never looked at a single frame.
    [InlineData(true, true, false, true, true)]
    [InlineData(true, true, true, false, true)]
    [InlineData(true, true, true, true, true)]
    [InlineData(true, true, false, false, false)]
    [InlineData(false, false, true, true, false)]

    // Each capability pairs with its own model, which is the whole of the split. Describing scenes
    // and looking for objects are asked for separately, so a camera that wants prose and has no
    // vision model to write it is not watched just because a detector happens to be loaded — the
    // old rule reached either model from either flag and ran the detector on it.
    [InlineData(true, false, false, true, false)]
    [InlineData(false, true, false, true, true)]
    [InlineData(false, true, true, true, true)]
    [InlineData(false, true, true, false, false)]
    public void Each_capability_pairs_with_its_own_model(
        bool describes, bool detects, bool hasVisionModel, bool hasDetector, bool expected) =>
        Assert.Equal(
            expected,
            CameraAiCoordinator.WantsVision(describes, detects, hasVisionModel, hasDetector));

    private static CameraVisionPipeline Pipeline(AiOptions ai, IObjectDetector? detector) =>
        new(
            new Camera
            {
                Id = "front-door",
                Name = "Front Door",
                Streams =
                [
                    new CameraStream
                    {
                        Name = "main",
                        Url = "rtsp://cam/main",
                        Roles = [StreamRole.Record, StreamRole.Detect],
                    },
                ],
            },
            ai,
            vision: null,
            detector,
            NullLogger.Instance);

    [Fact]
    public void With_no_detector_the_motion_gate_runs()
    {
        var ai = new AiOptions();
        ai.Detection.Enabled = true;   // enabled, but nothing loaded it

        using CameraVisionPipeline pipeline = Pipeline(ai, detector: null);

        Assert.False(pipeline.UsesDetection);
    }

    [Fact]
    public void A_loaded_detector_that_is_disabled_still_leaves_the_motion_gate_running()
    {
        // Existing deployments must not change behaviour because a model happened to be mounted.
        var ai = new AiOptions();
        ai.Detection.Enabled = false;

        using CameraVisionPipeline pipeline = Pipeline(ai, new FakeDetector());

        Assert.False(pipeline.UsesDetection);
    }

    [Fact]
    public void A_loaded_and_enabled_detector_replaces_the_motion_gate_rather_than_joining_it()
    {
        var ai = new AiOptions();
        ai.Detection.Enabled = true;

        using CameraVisionPipeline pipeline = Pipeline(ai, new FakeDetector());

        Assert.True(pipeline.UsesDetection);
    }

    [Fact]
    public void A_camera_that_switches_detection_off_keeps_the_motion_gate()
    {
        // The server is detecting and a model is loaded; this one camera has opted out. It must
        // land on frame differencing rather than on nothing, so its scene descriptions carry on.
        var global = new AiOptions();
        global.Detection.Enabled = true;

        AiOptions ai = CameraAiOptions.For(
            global, tuning: null, detection: new CameraDetectionTuning { Enabled = false });

        using CameraVisionPipeline pipeline = Pipeline(ai, new FakeDetector());

        Assert.False(pipeline.UsesDetection);
        Assert.True(global.Detection.Enabled);
    }

    [Fact]
    public void A_camera_that_switches_detection_on_under_a_server_that_did_not_load_one_is_inert()
    {
        // The asymmetry worth pinning: the server key decides whether a model is opened at all, so
        // asking for detection on a camera cannot conjure one. The advisory says so; this proves it.
        var global = new AiOptions();
        global.Detection.Enabled = false;

        AiOptions ai = CameraAiOptions.For(
            global, tuning: null, detection: new CameraDetectionTuning { Enabled = true });

        using CameraVisionPipeline pipeline = Pipeline(ai, detector: null);

        Assert.False(pipeline.UsesDetection);
    }

    [Fact]
    public void An_unset_switch_follows_the_server()
    {
        var global = new AiOptions();
        global.Detection.Enabled = true;

        AiOptions ai = CameraAiOptions.For(
            global, tuning: null, detection: new CameraDetectionTuning { MaxFps = 2 });

        using CameraVisionPipeline pipeline = Pipeline(ai, new FakeDetector());

        Assert.True(pipeline.UsesDetection);
    }

    private sealed class FakeDetector : IObjectDetector
    {
        public string Description => "fake";

        public DetectorInput InputFor(int frameWidth, int frameHeight) =>
            new(640, 640, DetectorLayout.FloatNchw);

        public Task<IReadOnlyList<DetectedObject>> DetectPreparedAsync(
            ReadOnlyMemory<byte> prepared,
            DetectorInput input,
            PreparedFrame frame,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DetectedObject>>([]);

        public void Dispose()
        {
        }
    }
}
