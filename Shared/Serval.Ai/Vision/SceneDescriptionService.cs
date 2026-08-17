using System.Threading.Channels;
using Serval.Contracts;

namespace Serval.Ai;

/// <summary>A description and when it was produced, so consumers can judge staleness.</summary>
public sealed record SceneDescription(string Text, DateTimeOffset DescribedAt);

/// <summary>
/// One pending ask for a description, carrying the frames to look at and why we are looking.
///
/// The frames travel with the request rather than being fetched when the worker gets round to it.
/// A motion-triggered request is about a specific moment; by the time the model is free the ring
/// may hold entirely different frames, and the description would be of a scene nobody asked about.
/// </summary>
public sealed record SceneRequest(
    IReadOnlyList<VisionFrame> Frames,
    string Trigger,
    double? MotionScore)
{
    /// <summary>Seconds between the oldest and newest frame. 0 for a single frame.</summary>
    public double SpanSeconds => Frames.Count < 2
        ? 0
        : (Frames[^1].CapturedAt - Frames[0].CapturedAt).TotalSeconds;
}

/// <summary>
/// The hand-off between everything cheap and the vision model.
///
/// Vision is roughly a thousand times slower than transcription — SenseVoice runs at rtf 0.02, a
/// description takes seconds on a desktop and tens of seconds on the Pi — so no caller ever waits
/// for it. Something <em>requests</em> a description and separately reads whatever one is available.
///
/// The consequence is deliberate and visible: the description attached to an utterance may have
/// been triggered by an earlier one. Every description carries a timestamp and the output records
/// `vision_age_seconds`, so the staleness is reported rather than hidden.
///
/// One instance per camera.
/// </summary>
public sealed class SceneDescriptionService
{
    // Capacity 1 + DropOldest: while a description is running, a further request replaces the one
    // already waiting rather than queueing behind it. Unbounded, a busy scene means continuous
    // inference and a queue that can only grow. Dropping the *older* one is what makes the survivor
    // useful — under continuous motion the model should describe the scene as it is now.
    private readonly Channel<SceneRequest> _requests = Channel.CreateBounded<SceneRequest>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private SceneDescription? _latest;

    public SceneDescriptionService(string? cameraId = null)
    {
        CameraId = cameraId;
    }

    /// <summary>Which camera this describes, or null on a single-camera host.</summary>
    public string? CameraId { get; }

    public ChannelReader<SceneRequest> Requests => _requests.Reader;

    /// <summary>
    /// Asks for a description. Never blocks; replaces a request that is still waiting, so the
    /// pending ask is always the most recent one. Returns false only once <see cref="Complete"/>
    /// has been called.
    /// </summary>
    public bool RequestDescription(SceneRequest request) => _requests.Writer.TryWrite(request);

    /// <summary>Convenience for the speech path, which enriches an utterance with whatever is to hand.</summary>
    public bool RequestDescription(IReadOnlyList<VisionFrame> frames) =>
        frames.Count > 0 && RequestDescription(new SceneRequest(frames, SceneTrigger.Speech, MotionScore: null));

    public void Publish(SceneDescription description) => Volatile.Write(ref _latest, description);

    /// <summary>The most recent description, or null if none has completed yet.</summary>
    public SceneDescription? Latest => Volatile.Read(ref _latest);

    /// <summary>Stops the worker draining this service, so a host can shut one camera down cleanly.</summary>
    public void Complete() => _requests.Writer.TryComplete();
}
