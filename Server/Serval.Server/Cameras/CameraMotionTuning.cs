using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace Serval.Server.Cameras;

/// <summary>
/// One camera's overrides for the frame-difference movement gate. Every field is optional; null
/// means "use the server default".
///
/// <para><b>Worth having even though the movement gate is the fallback.</b>
/// <c>CameraVisionPipeline</c> runs the object detector <em>or</em> this, never both — so on a
/// server with detection switched on, none of this is reached. It is reached on every server where
/// detection is off, which includes any deployment that has not put a detection model in place, and
/// there it is the only thing deciding whether the description model runs at all.</para>
///
/// <para>And when it is running, its right values are as local as the audio thresholds': a camera
/// facing a tree needs a higher fraction than one facing a hallway, and a camera watching a
/// television needs one higher still. A single server-wide figure is tuned for whichever camera
/// complained most recently.</para>
///
/// <para>The comparison size is deliberately not here. It trades accuracy for processor time
/// identically on every camera, which makes it a cost decision for the server rather than a fact
/// about a room.</para>
/// </summary>
public sealed class CameraMotionTuning
{
    /// <summary>
    /// Overrides <c>Serval:Ai:Motion:PixelDelta</c> — how much one point of the picture must change
    /// in brightness to count. Raise it on a noisy sensor or a camera whose night mode is grainy.
    /// </summary>
    public int? PixelDelta { get; set; }

    /// <summary>
    /// Overrides <c>Serval:Ai:Motion:MinChangedFraction</c> — how much of the picture must change
    /// before this camera calls it movement. The knob to reach for when foliage keeps waking the
    /// describer, or when someone at the far end of a garden is being missed.
    /// </summary>
    public double? MinChangedFraction { get; set; }

    /// <summary>
    /// Overrides <c>Serval:Ai:Motion:MaxChangedFraction</c> — the ceiling above which a change is
    /// treated as the lights or the infrared filter rather than as movement. Worth lowering on a
    /// camera pointed at something that legitimately fills the frame, such as a doorway.
    /// </summary>
    public double? MaxChangedFraction { get; set; }
}
