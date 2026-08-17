using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace Serval.Server.Cameras;

/// <summary>
/// One camera's overrides for which non-speech sounds it reports and which of them are alarming.
/// Every field is optional; null means "use the server default", exactly as
/// <see cref="CameraAudioTuning"/> and <see cref="CameraDetectionTuning"/> do.
///
/// <para><b>Which sounds matter is a property of the room, not of the software.</b> A driveway
/// camera wants vehicles and glass; a nursery wants crying and a smoke alarm and emphatically not
/// every car that passes; a workshop hears power tools all day and would produce nothing but noise
/// under either list. One server-wide list of alert labels serves all three badly, and the cost of
/// getting it wrong is asymmetric — the labels that go unreported are silent, while the ones that
/// over-report train people to ignore the alerts that matter.</para>
///
/// <para>The sound path's level gate is <em>not</em> here: it is
/// <see cref="CameraAudioTuning.SoundGateRmsThreshold"/>, alongside the speech gate, because how
/// loud a room is is one fact about the room and splitting it across two objects would invite the
/// two halves to disagree.</para>
///
/// <para><b>Thresholds are <c>double</c> though the options they override are <c>float</c>,</b> for
/// the reason <see cref="CameraAudioTuning"/> gives: narrowing on the way in would make every
/// <c>GET</c> disagree in its last digits with the <c>PUT</c> that produced it.</para>
/// </summary>
public sealed class CameraSoundTuning
{
    /// <summary>
    /// Overrides <c>Serval:Ai:Sound:AlertLabels</c> — which sounds raise an alert here, spelled
    /// exactly as the model spells them. Null inherits; an empty array is rejected at the API
    /// rather than silently meaning "none" or "all".
    /// </summary>
    public string[]? AlertLabels { get; set; }

    /// <summary>
    /// Overrides <c>Serval:Ai:Sound:IgnoredLabels</c> — labels this camera never reports. The
    /// escape hatch for a camera whose location makes one label useless: "Speech" beside a
    /// television, "Vehicle" beside a road.
    /// </summary>
    public string[]? IgnoredLabels { get; set; }

    /// <summary>Overrides <c>Serval:Ai:Sound:MinConfidence</c> — how sure the model must be before
    /// this camera writes a sound down at all.</summary>
    public double? MinConfidence { get; set; }

    /// <summary>Overrides <c>Serval:Ai:Sound:AlertMinConfidence</c>, the higher bar for claiming an
    /// alert rather than recording an event.</summary>
    public double? AlertMinConfidence { get; set; }

    /// <summary>
    /// Overrides <c>Serval:Ai:Sound:CooldownSeconds</c> — the minimum gap between two records of the
    /// same sound here. Worth raising on a camera somewhere permanently noisy, where the gap is the
    /// only thing between the deployment and a record every few seconds forever.
    /// </summary>
    public double? CooldownSeconds { get; set; }

    /// <summary>Overrides <c>Serval:Ai:Sound:AlertCooldownSeconds</c>, the shorter gap alerts are
    /// held to on the grounds that a repeated alarm is information.</summary>
    public double? AlertCooldownSeconds { get; set; }
}
