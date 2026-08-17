using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Serval.Contracts;

namespace Serval.Server.Alerts;

/// <summary>What kind of detection raised an alert. Both share the queue and both get a video clip.</summary>
public enum AlertKind
{
    /// <summary>An object-detection episode — a <see cref="DetectionDocument"/> with <c>IsAlert</c>.</summary>
    Object,

    /// <summary>A sound event — a <see cref="SoundDocument"/> with <c>IsAlert</c>.</summary>
    Sound,
}

/// <summary>How far an alert's preview clip has got. An alert exists before its clip does.</summary>
public enum AlertClipState
{
    /// <summary>Queued. The footage it needs has not finished being written yet.</summary>
    Pending,

    /// <summary>The clip and its poster are on disk.</summary>
    Ready,

    /// <summary>
    /// There was no footage to cut from — the camera's preview buffer had rolled past, the ring
    /// could not be written for this camera, or a restart happened while the alert was pending.
    ///
    /// Distinct from <see cref="Failed"/> because it is ordinary rather than wrong: an alert on a
    /// camera whose sub stream ffmpeg cannot copy lands here every time, and the screen has a card
    /// for it.
    /// </summary>
    Unavailable,

    /// <summary>Something went wrong cutting it. <see cref="Alert.ClipError"/> says what.</summary>
    Failed,
}

/// <summary>
/// A detection somebody asked to be told about, with the queue state and the preview clip that make
/// it something to look at rather than a row in a telemetry collection.
///
/// <para><b>Why this is not a flag on <see cref="DetectionDocument"/>.</b> Four reasons, and each
/// one alone would be enough. The queue is cross-camera and newest-first, while every telemetry
/// index leads with <c>CameraId</c>. Alerts carry state that changes — read, dismissed — on
/// documents that are otherwise written once and never revised. Object and sound alerts have to
/// interleave in one ordered list, and they live in two collections. And an alert owns files, so it
/// needs a lifetime of its own; nothing prunes telemetry at all today.</para>
///
/// <para><b>What it is not.</b> Not a copy of the detection. The episode goes on living in
/// <c>detections</c> with its full track, and this holds only what the queue draws — which is why
/// <see cref="Box"/> is the peak box rather than the whole path, and why the camera's name is
/// resolved at read time rather than frozen the way a saved clip freezes it. A saved clip is
/// evidence kept forever; an alert is a notification with a lifetime measured in days.</para>
/// </summary>
[BsonIgnoreExtraElements]
public sealed class Alert
{
    /// <summary>
    /// The id of the detection or sound that raised it, reused verbatim.
    ///
    /// This is what makes raising idempotent. An open episode is broadcast once a frame while the
    /// object is still there, so the raise path sees the same alert-worthy episode dozens of times;
    /// with the source id as the key, all but the first are a no-op insert that Mongo rejects on the
    /// _id it already has. It also means an alert can always be traced back to the record it came
    /// from without storing a second reference.
    /// </summary>
    [BsonId]
    public required string Id { get; set; }

    public required string CameraId { get; set; }

    [BsonRepresentation(BsonType.String)]
    public AlertKind Kind { get; set; }

    /// <summary>
    /// When the thing that fired the alert began — the episode's start, or the sound's timestamp.
    /// The instant the queue sorts on, the preview is padded around, and <c>Watch</c> opens the
    /// recording at.
    /// </summary>
    public required DateTimeOffset At { get; set; }

    /// <summary>
    /// The clearest look at it: an episode's <c>PeakFrameAt</c>, or <see cref="At"/> for a sound.
    ///
    /// This is the frame the poster is cut from, which is what the screen labels "the frame it fired
    /// on". Taken when the alert is raised rather than when the episode closes, so it is the peak so
    /// far rather than the peak overall — the alternative is a poster nobody sees for the half hour
    /// a car sits in the driveway.
    /// </summary>
    public required DateTimeOffset PeakAt { get; set; }

    /// <summary>The detector's own class string or the AudioSet phrase, verbatim — <c>person</c>,
    /// <c>car</c>, <c>Glass</c>. The App maps it to a chip and an icon.</summary>
    public required string Label { get; set; }

    /// <summary>
    /// The sentence the alert is announced with, composed once here and stored.
    ///
    /// Server-side and frozen on purpose. Every row in the queue is something that was, or will be,
    /// a notification on somebody's phone, and the two have to read the same words — a title
    /// recomposed by whichever client happens to be drawing it is a title that drifts from the push
    /// that carried it.
    /// </summary>
    public required string Title { get; set; }

    /// <summary>
    /// Where in the frame it was, in normalised fractions — null for a sound, which has no place.
    ///
    /// Normalised is what lets the App draw the same box over a 132x74 row thumbnail and a
    /// full-width hero without knowing either size. Stored rather than burned into the poster so
    /// that the picture stays a picture.
    /// </summary>
    public DetectionBox? Box { get; set; }

    /// <summary>Whether anyone has opened it. Drives the unread dot and the header's count.</summary>
    public bool Read { get; set; }

    /// <summary>
    /// When it was cleared from the queue, or null while it is still in it.
    ///
    /// The row outlives the dismissal rather than being deleted with it, so that the clip on disk
    /// always has something owning it and retention has one rule to apply instead of two.
    /// </summary>
    public DateTimeOffset? DismissedAt { get; set; }

    [BsonRepresentation(BsonType.String)]
    public AlertClipState ClipState { get; set; } = AlertClipState.Pending;

    /// <summary>How long the clip actually turned out to be. Segments only cut on keyframes, so this
    /// is usually longer than the padding asked for, and by however long the camera's GOP is.</summary>
    public double? ClipSeconds { get; set; }

    /// <summary>Why the cut failed, for the App to show instead of a silent absence.</summary>
    public string? ClipError { get; set; }

    /// <summary>
    /// Whether the camera's recording covers <see cref="At"/> — null until that has been decided.
    ///
    /// Decided when the clip is cut rather than when the alert is raised, because at raise time the
    /// segment covering that instant has not been closed, indexed, or in some cases even finished
    /// being received. It is the whole difference between a card that offers to open the recording
    /// and one that only has its preview.
    ///
    /// <para><b>Null is the third state and it is load-bearing.</b> The gap between the raise and
    /// the cut is <see cref="MediaOptions.AlertPostRollSeconds"/> plus a second, and for all of it
    /// the answer is genuinely unknown. A <c>false</c> there would be indistinguishable from a
    /// camera that records nothing, and every reader would draw the same conclusion from it — which
    /// is how a recording camera's newest alerts came to be labelled as not recorded for their first
    /// sixteen seconds. Ask this <c>== true</c> to offer the recording and <c>== false</c> to say
    /// there is none; null means neither, and the card says nothing about it.</para>
    /// </summary>
    public bool? Recorded { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }
}
