using Serval.Ai;
using Serval.Contracts;

namespace Serval.Ai.Tests;

/// <summary>
/// The hand-off to the vision model. What matters is the one-slot behaviour: the model is the
/// slowest thing in the system, so requests must never queue, and the one request that survives
/// must be the newest — a description of the scene as it is now, not as it was when a burst of
/// motion started.
/// </summary>
public class SceneDescriptionServiceTests
{
    private static VisionFrame Frame(int second) =>
        new([0xFF, 0xD8, (byte)second, 0xFF, 0xD9], new DateTimeOffset(2026, 1, 1, 0, 0, second, TimeSpan.Zero));

    private static SceneRequest Request(int second) =>
        new([Frame(second)], SceneTrigger.Motion, MotionScore: 0.1);

    [Fact]
    public void A_pending_request_is_replaced_by_a_newer_one()
    {
        var service = new SceneDescriptionService("cam1");

        service.RequestDescription(Request(1));
        service.RequestDescription(Request(2));
        service.RequestDescription(Request(3));

        // Continuous motion must not leave the model describing the frame that first tripped the
        // gate: the survivor is the newest ask, not the oldest.
        Assert.True(service.Requests.TryRead(out SceneRequest? pending));
        Assert.Equal(3, pending!.Frames[0].CapturedAt.Second);

        Assert.False(service.Requests.TryRead(out _)); // and nothing queued behind it
    }

    [Fact]
    public void Requests_never_queue()
    {
        var service = new SceneDescriptionService("cam1");

        for (int i = 0; i < 100; i++)
        {
            service.RequestDescription(Request(i % 60));
        }

        Assert.True(service.Requests.TryRead(out _));
        Assert.False(service.Requests.TryRead(out _));
    }

    [Fact]
    public void A_drained_slot_accepts_the_next_request()
    {
        var service = new SceneDescriptionService("cam1");

        service.RequestDescription(Request(1));
        Assert.True(service.Requests.TryRead(out _));

        service.RequestDescription(Request(2));
        Assert.True(service.Requests.TryRead(out SceneRequest? second));
        Assert.Equal(2, second!.Frames[0].CapturedAt.Second);
    }

    [Fact]
    public void The_speech_convenience_ignores_an_empty_frame_set()
    {
        var service = new SceneDescriptionService("cam1");

        Assert.False(service.RequestDescription([]));
        Assert.False(service.Requests.TryRead(out _));
    }

    [Fact]
    public void Completing_the_service_refuses_further_requests()
    {
        var service = new SceneDescriptionService("cam1");

        service.Complete();

        // The only false a live service can return: the worker draining it has been shut down.
        Assert.False(service.RequestDescription(Request(1)));
    }

    [Fact]
    public void Latest_is_null_until_a_description_completes()
    {
        var service = new SceneDescriptionService("cam1");
        Assert.Null(service.Latest);

        var described = new SceneDescription("a car in the driveway", DateTimeOffset.UtcNow);
        service.Publish(described);

        Assert.Same(described, service.Latest);
    }
}
