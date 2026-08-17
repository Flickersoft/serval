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
    // The case that was wrong: the scene-description worker is only registered when a 2.3 GB
    // vision model is on disk, while the detector needs one a couple of hundred times smaller.
    // Requiring the worker meant a host with the detector and no vision model — by far the likelier
    // first deployment — silently never looked at a single frame.
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, true, false)]
    public void Either_capability_alone_is_enough_to_watch_a_camera(
        bool aiVision, bool hasVisionModel, bool hasDetector, bool expected) =>
        Assert.Equal(expected, CameraAiCoordinator.WantsVision(aiVision, hasVisionModel, hasDetector));

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
