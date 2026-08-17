using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using Serval.Ai;

namespace Serval.Server.Cameras;

/// <summary>
/// One camera's overrides for what its object detector looks for and what is worth waking the
/// vision model over. Every field is optional; null means "use the server default", exactly as
/// <see cref="CameraAudioTuning"/> does.
///
/// <para>A sibling of that type rather than fields on it, because vision knobs on something called
/// <c>AudioTuning</c> would be a lie that outlives whoever wrote it. The two are resolved together
/// in <see cref="Ai.CameraAiOptions.For"/>.</para>
///
/// <para><see cref="Masks"/> is the field with no global equivalent worth setting. Where a property
/// line runs is a fact about one camera, and it is not a threshold problem: a driveway camera that
/// also sees the public road detects every passing car perfectly correctly, and no confidence floor
/// distinguishes those from the one pulling in. Only geometry does.</para>
///
/// <para><b>Thresholds here are <c>double</c> though the options they override are <c>float</c>,</b>
/// for the reason <see cref="CameraAudioTuning"/> gives: narrowing on the way in would make every
/// <c>GET</c> disagree in its last digits with the <c>PUT</c> that produced it.</para>
/// </summary>
public sealed class CameraDetectionTuning
{
    /// <summary>
    /// Overrides <c>Serval:Ai:Detection:Classes</c> — which of the model's classes this camera
    /// records at all. Null inherits; an empty array is rejected at the API rather than silently
    /// meaning "everything" or "nothing".
    /// </summary>
    public string[]? Classes { get; set; }

    /// <summary>
    /// Overrides <c>Serval:Ai:Detection:DescribeClasses</c> — which of those are worth a scene
    /// description. Usually much shorter than <see cref="Classes"/>: knowing a car has been on the
    /// driveway since 18:00 is worth recording, and not worth seconds of inference to be told.
    /// </summary>
    public string[]? DescribeClasses { get; set; }

    /// <summary>Overrides <c>Serval:Ai:Detection:ScoreThreshold</c>. Raise it on a camera whose
    /// view produces confident nonsense — foliage at range, reflections at night.</summary>
    public double? ScoreThreshold { get; set; }

    /// <summary>
    /// Overrides <c>Serval:Ai:Detection:MinObjectFraction</c> — how small a thing this camera is
    /// willing to call an object, as a fraction of its frame's area. Per camera because it is a
    /// statement about the view: a porch where everything is close by can afford a floor that would
    /// blind a drive watching the road.
    /// </summary>
    public double? MinObjectFraction { get; set; }

    /// <summary>
    /// Overrides <c>Serval:Ai:Detection:Tracking:ConfirmSeconds</c>. The most useful knob for a
    /// camera that ghosts: raising it costs latency before anything is reported and removes almost
    /// every false positive, because a ghost rarely repeats in the same place while a real person
    /// stays put.
    /// </summary>
    public double? TrackConfirmSeconds { get; set; }

    /// <summary>
    /// Overrides <c>Serval:Ai:Detection:Tracking:CoastSeconds</c> — how long this camera keeps
    /// predicting where something went after losing sight of it. A view with a pillar, a parked van
    /// or a doorway in the middle of it earns a longer one, because the alternative is one subject
    /// walking past being written down as two.
    /// </summary>
    public double? TrackCoastSeconds { get; set; }

    /// <summary>
    /// Regions of this camera's view to ignore, as polygons in normalised coordinates. Null
    /// inherits the (usually empty) server default; an empty array means this camera explicitly
    /// masks nothing.
    /// </summary>
    public DetectionMask[]? Masks { get; set; }

    /// <summary>
    /// Overrides <c>Serval:Ai:Detection:AlertClasses</c> — which of what this camera records is
    /// worth raising an alert about. Almost always a per-camera question: a person in the garden at
    /// night and a person in the hallway are the same detection and not the same news.
    /// </summary>
    public string[]? AlertClasses { get; set; }

    /// <summary>Overrides <c>Serval:Ai:Detection:AlertMinConfidence</c>, for a camera whose view
    /// earns a higher bar before it is allowed to claim an alert.</summary>
    public double? AlertMinConfidence { get; set; }

    /// <summary>
    /// Overrides <c>Serval:Ai:Detection:MaxFps</c> — how often this camera is examined at all. The
    /// per-camera cost lever: a busy driveway can be worth looking at every frame while a spare room
    /// is worth one every ten seconds, and spending the same budget on both is what makes the total
    /// too expensive.
    /// </summary>
    public double? MaxFps { get; set; }

    /// <summary>
    /// Overrides <c>Serval:Ai:Detection:MinMovementFraction</c>. How far something must shift to
    /// count as moving depends on how much of the frame a person occupies, which is a fact about
    /// where the camera is pointed and how far away things are.
    /// </summary>
    public double? MinMovementFraction { get; set; }

    /// <summary>Overrides <c>Serval:Ai:Detection:AbsenceSeconds</c> — how long something must be out
    /// of sight before this camera calls it gone.</summary>
    public double? AbsenceSeconds { get; set; }

    /// <summary>
    /// Overrides <c>Serval:Ai:Detection:NoveltySeconds</c> — how long something must have been away
    /// before turning up counts as arriving. The setting that decides what this camera treats as
    /// furniture, which is why the right value for a driveway and for a living room are not close.
    /// </summary>
    public double? NoveltySeconds { get; set; }

}
