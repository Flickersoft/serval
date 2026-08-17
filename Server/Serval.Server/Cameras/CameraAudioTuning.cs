using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace Serval.Server.Cameras;

/// <summary>
/// One camera's overrides for the thresholds that decide what its audio pipeline bothers to
/// listen to. Every field is optional; null means "use the server default" exactly as
/// <see cref="Camera.RetentionDays"/> does.
///
/// <para><b>Per-camera rather than a corrected global default</b>, because the right value is a
/// property of the room. An indoor camera and the outdoor one on the same server disagree by more
/// than an order of magnitude, and a threshold right for either destroys the other's speech
/// entirely. The measurements are in <c>Docs/detection.md</c> under *The sound gate's threshold is
/// per camera*.</para>
///
/// <para>The fault is hard to see because sound tagging keeps working throughout: a two-second
/// sound segment only needs the gate to crack open once, where an utterance needs it open
/// continuously from the first word to the trailing silence.</para>
///
/// <para><b>These are <c>double</c>, though everything they override is <c>float</c>.</b> The
/// narrowing happens in <see cref="Ai.CameraAiOptions.For"/>, where the value reaches a model that
/// wants a float. A <c>float</c> property would narrow on the way in, so the value read back by
/// <c>GET</c> would differ in its last few digits from the one just sent by <c>PUT</c>, and a client
/// comparing the two would conclude — correctly and forever — that its change had not been
/// saved.</para>
/// </summary>
public sealed class CameraAudioTuning
{
    /// <summary>
    /// Overrides <c>Serval:Ai:AudioGate:RmsThreshold</c> — the level below which this camera's
    /// audio is treated as silence and never reaches the speech detector.
    /// </summary>
    public double? SpeechGateRmsThreshold { get; set; }

    /// <summary>
    /// Overrides <c>Serval:Ai:Vad:Threshold</c> — Silero's speech probability, above which a
    /// window counts as speech. Raise it in a room where the television is mistaken for talking.
    /// </summary>
    public double? VadThreshold { get; set; }

    /// <summary>
    /// Overrides <c>Serval:Ai:Sound:Gate:RmsThreshold</c>. Separate from
    /// <see cref="SpeechGateRmsThreshold"/> because the two branches are independent and want
    /// different answers: sound tagging is looking for a glass break at the far end of a room,
    /// speech for a voice near the camera.
    /// </summary>
    public double? SoundGateRmsThreshold { get; set; }
}
