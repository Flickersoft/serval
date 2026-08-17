using System.Text.Json;
using System.Text.Json.Serialization;

namespace Serval.Contracts;

/// <summary>
/// Anything published to a sink. All record types share one stream, discriminated by <c>type</c>.
/// </summary>
public interface IOutputRecord
{
    [JsonPropertyName("type")]
    string Type { get; }

    /// <summary>Owned by the Server, which assigns it from the ingest URL — absent from what a
    /// module sends and present in what a client reads back.</summary>
    string? CameraId { get; set; }

    /// <inheritdoc cref="CameraId"/>
    DateTimeOffset? ReceivedAt { get; set; }
}

/// <summary>
/// Canonical serialization settings for everything on the wire. Shared so the producer and the
/// consumer cannot drift: null fields are omitted rather than written, because an absent
/// <c>emotion</c> means undetermined and <c>"emotion": null</c> invites a reader to treat it as a
/// value.
/// </summary>
public static class TelemetryJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>
    /// Serializes a record as its concrete type.
    ///
    /// Always go through this rather than <c>JsonSerializer.Serialize(record, Options)</c>: passing
    /// an <see cref="IOutputRecord"/> to the generic overload makes System.Text.Json emit only the
    /// interface's members, and every record silently collapses to nothing but its <c>type</c>.
    /// </summary>
    public static string Serialize(IOutputRecord record) =>
        JsonSerializer.Serialize(record, record.GetType(), Options);

    /// <summary>Stamped on every record. Written for a consumer's benefit; nothing here reads it.</summary>
    public const int SchemaVersion = 7;
}

/// <summary>
/// Which pipeline produced a record. Two cameras can be described by the same code running in two
/// places — an edge module, or the Server on behalf of a camera that has no module — and a consumer
/// that cannot tell them apart cannot explain why coverage differs.
/// </summary>
public static class TelemetrySource
{
    public const string Module = "module";
    public const string Server = "server";
}

/// <summary>What caused a scene description to be produced.</summary>
public static class SceneTrigger
{
    /// <summary>The frame-difference gate saw the scene change.</summary>
    public const string Motion = "motion";

    /// <summary>Someone spoke, and the description is enrichment for that utterance.</summary>
    public const string Speech = "speech";

    /// <summary>Neither — a periodic look, e.g. the first frame after startup.</summary>
    public const string Periodic = "periodic";

    /// <summary>
    /// The object detector opened an episode — something worth describing arrived.
    ///
    /// Only on the *opening* of one, never on the frames that follow it. A description is seconds
    /// of inference behind a global floor, so a car parked in view asking once a second would hold
    /// the single worker permanently and starve every other camera.
    /// </summary>
    public const string Object = "object";
}

/// <summary>
/// The published shape of one utterance.
///
/// <c>camera_id</c> and <c>received_at</c> are owned by the Server: an edge module has no identity
/// of its own and the Server assigns one from the ingest URL. They are absent from what a module
/// sends and present in what a client reads back.
///
/// There is deliberately no <c>vision</c> field. Every completed description is published as its
/// own <see cref="SceneDocument"/>, including the speech-triggered ones (that is what
/// <see cref="SceneTrigger.Speech"/> marks), so a copy here would be duplication. A consumer
/// wanting scene context for an utterance correlates on <c>timestamp</c> against scene records and
/// chooses its own window, rather than inheriting a staleness judgement frozen at write time.
/// </summary>
public sealed class UtteranceDocument : IOutputRecord
{
    [JsonPropertyName("type")]
    public string Type => "utterance";

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = TelemetryJson.SchemaVersion;

    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("camera_id")]
    public string? CameraId { get; set; }

    [JsonPropertyName("received_at")]
    public DateTimeOffset? ReceivedAt { get; set; }

    /// <summary>Joins this utterance to its conversation's diarization and transcript records.</summary>
    [JsonPropertyName("conversation_id")]
    public string? ConversationId { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("transcript")]
    public string Transcript { get; set; } = "";

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("emotion")]
    public string? Emotion { get; set; }

    [JsonPropertyName("audio_event")]
    public string? AudioEvent { get; set; }

    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; set; }

    /// <summary>
    /// Live, best-effort speaker label, valid only within this conversation. Absent when no
    /// label could be justified.
    /// </summary>
    [JsonPropertyName("speaker")]
    public string? Speaker { get; set; }

    /// <summary>
    /// Always "live" when a speaker is present. Stated explicitly so a consumer cannot mistake
    /// this best-effort guess for the considered answer in the conversation's diarization
    /// record — the two are produced differently and are never reconciled.
    /// </summary>
    [JsonPropertyName("speaker_source")]
    public string? SpeakerSource { get; set; }

    /// <summary><see cref="TelemetrySource"/>.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = TelemetrySource.Module;
}

/// <summary>One speaker turn found by the after-the-fact pass. Times are seconds from the conversation start.</summary>
public sealed class DiarizationSegment
{
    [JsonPropertyName("start")]
    public double Start { get; set; }

    [JsonPropertyName("end")]
    public double End { get; set; }

    [JsonPropertyName("speaker")]
    public int Speaker { get; set; }
}

/// <summary>
/// The after-the-fact speaker picture for a whole conversation.
///
/// Published as its own record and deliberately never merged with the live per-utterance
/// labels: the two are produced by different methods with different tradeoffs, so downstream
/// joins on <c>conversation_id</c> and decides which to trust. That also means this half can
/// be swapped for a better model without disturbing the live half.
///
/// Segment times are seconds from <c>started_at</c>. Since every utterance carries a
/// wall-clock timestamp, the two streams align by subtraction — no offset table required.
///
/// Speaker numbers are scoped to this conversation and mean nothing across conversations.
/// </summary>
public sealed class DiarizationDocument : IOutputRecord
{
    [JsonPropertyName("type")]
    public string Type => "diarization";

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = TelemetryJson.SchemaVersion;

    [JsonPropertyName("conversation_id")]
    public required string ConversationId { get; set; }

    [JsonPropertyName("camera_id")]
    public string? CameraId { get; set; }

    [JsonPropertyName("received_at")]
    public DateTimeOffset? ReceivedAt { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("audio_seconds")]
    public double AudioSeconds { get; set; }

    [JsonPropertyName("speaker_count")]
    public int SpeakerCount { get; set; }

    [JsonPropertyName("segments")]
    public List<DiarizationSegment> Segments { get; set; } = [];

    /// <summary><see cref="TelemetrySource"/>.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = TelemetrySource.Module;
}

/// <summary>One speaker's turn with the words attributed to it.</summary>
public sealed class TranscriptTurn
{
    [JsonPropertyName("start")]
    public double Start { get; set; }

    [JsonPropertyName("end")]
    public double End { get; set; }

    [JsonPropertyName("speaker")]
    public int Speaker { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    /// <summary>
    /// How the audio behind <em>this speaker's</em> words sounded — the same vocabulary as
    /// <see cref="UtteranceDocument.Emotion"/>, and null on the same terms: absent means nothing
    /// could be attributed, never a neutral default.
    ///
    /// Resolved here rather than left to a reader, because a reader cannot do it. Turn times are
    /// seconds from <c>started_at</c> while an utterance's timestamp is when the VAD <em>emitted</em>
    /// it — after the speech plus the trailing silence it waited through — so lining the two up needs
    /// the VAD's own minimum-silence setting, which never leaves the module. See
    /// <c>ConversationReprocessor.SpanOf</c>.
    ///
    /// Where one utterance straddled a speaker change this is better than anything a join could
    /// produce: that audio is re-transcribed per speaker anyway, so each turn carries the emotion of
    /// its own piece instead of one reading copied across both voices.
    /// </summary>
    [JsonPropertyName("emotion")]
    public string? Emotion { get; set; }
}

/// <summary>
/// The settled account of a finished conversation: its diarized turns with the words attributed to
/// each one.
///
/// This exists because the live stream cannot produce it. The VAD splits audio on silence, not on
/// speaker change, so a live utterance holding two voices gets one speaker label at every possible
/// threshold — the transcript is right and the attribution is wrong, and no amount of tuning fixes
/// it. The offline pass re-derives the turns from the whole conversation at once and reassigns the
/// existing text to them.
///
/// Live utterance records are left exactly as they were published. This is an additional, better
/// view, not a correction applied in place — a consumer that already showed a transcript in
/// realtime should not have it silently rewritten underneath.
/// </summary>
public sealed class ConversationTranscriptDocument : IOutputRecord
{
    [JsonPropertyName("type")]
    public string Type => "conversation_transcript";

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = TelemetryJson.SchemaVersion;

    [JsonPropertyName("conversation_id")]
    public required string ConversationId { get; set; }

    [JsonPropertyName("camera_id")]
    public string? CameraId { get; set; }

    [JsonPropertyName("received_at")]
    public DateTimeOffset? ReceivedAt { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("audio_seconds")]
    public double AudioSeconds { get; set; }

    [JsonPropertyName("speaker_count")]
    public int SpeakerCount { get; set; }

    /// <summary>The whole conversation as flowing text, turns joined in order. Convenience for search and display.</summary>
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("turns")]
    public List<TranscriptTurn> Turns { get; set; } = [];

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// How many turns needed their audio re-transcribed because a live utterance straddled a
    /// speaker change. Reported rather than hidden: if this is a large share of the turns, the
    /// diarization threshold or the VAD settings want re-measuring.
    /// </summary>
    [JsonPropertyName("retranscribed_turns")]
    public int RetranscribedTurns { get; set; }

    /// <summary><see cref="TelemetrySource"/>.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = TelemetrySource.Module;
}

/// <summary>
/// A scene description that stands on its own, rather than riding along on an utterance.
///
/// Needed the moment vision stopped being gated on speech: a motion-triggered description happens
/// when nobody is talking, so there is no utterance to attach it to.
///
/// This is the *only* place a description is published. Every completed inference produces one of
/// these whatever triggered it, so correlating by <c>timestamp</c> is how any record — an
/// utterance, a sound — acquires scene context.
/// </summary>
public sealed class SceneDocument : IOutputRecord
{
    [JsonPropertyName("type")]
    public string Type => "scene";

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = TelemetryJson.SchemaVersion;

    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("camera_id")]
    public string? CameraId { get; set; }

    [JsonPropertyName("received_at")]
    public DateTimeOffset? ReceivedAt { get; set; }

    /// <summary>When the newest frame in the description was captured.</summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary><see cref="SceneTrigger"/>.</summary>
    [JsonPropertyName("trigger")]
    public string Trigger { get; set; } = SceneTrigger.Motion;

    /// <summary>
    /// Fraction of pixels the motion gate saw change. Absent when the description was not motion
    /// triggered. Recorded because it is the only way to tell, after the fact, whether a threshold
    /// is set sensibly for a given camera.
    /// </summary>
    [JsonPropertyName("motion_score")]
    public double? MotionScore { get; set; }

    /// <summary>How many frames the model was shown. 1 means no movement could be inferred.</summary>
    [JsonPropertyName("frame_count")]
    public int FrameCount { get; set; }

    /// <summary>Seconds between the oldest and newest frame shown. 0 for a single frame.</summary>
    [JsonPropertyName("frame_span_seconds")]
    public double FrameSpanSeconds { get; set; }

    /// <summary><see cref="TelemetrySource"/>.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = TelemetrySource.Module;
}

/// <summary>
/// One AudioSet class the tagger scored, named exactly as the model names it.
/// </summary>
public sealed class SoundAlternate
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }
}

/// <summary>
/// A non-speech sound: a car horn, breaking glass, a dog, a door.
///
/// Its own record type rather than a field on <see cref="UtteranceDocument"/>: there is no utterance
/// for it to ride on. Sound detection runs parallel to speech over the same audio, gated on level
/// alone, where the speech path is gated on a VAD that rejects everything not speech. The two
/// overlap freely — a conversation with a dog barking over it produces an utterance <em>and</em> a
/// sound.
///
/// <c>label</c> is the model's own AudioSet string, verbatim and un-renamed — "Vehicle horn, car
/// horn, honking", not a tidied slug. Grouping labels into categories is a presentation decision,
/// and a consumer that makes it locally can change its mind without a schema change. The full
/// scored shortlist rides along in <c>alternates</c> for the same reason: thresholds can be
/// re-derived later from what was actually stored.
///
/// There is no <c>vision</c> field, deliberately — see <see cref="UtteranceDocument"/>.
/// </summary>
public sealed class SoundDocument : IOutputRecord
{
    [JsonPropertyName("type")]
    public string Type => "sound";

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = TelemetryJson.SchemaVersion;

    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("camera_id")]
    public string? CameraId { get; set; }

    [JsonPropertyName("received_at")]
    public DateTimeOffset? ReceivedAt { get; set; }

    /// <summary>When the sound started — the beginning of the segment, not when it was classified.</summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>The winning AudioSet label, as the model spells it.</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    /// <summary>The scored shortlist, best first. The winner is at index 0.</summary>
    [JsonPropertyName("alternates")]
    public List<SoundAlternate> Alternates { get; set; } = [];

    /// <summary>
    /// Whether this label is one the operator asked to be told about.
    ///
    /// Non-nullable so it is always written: a consumer reading absence as "not an alert" would be
    /// right by accident rather than by contract, and this is the one field here that a person is
    /// woken up by.
    /// </summary>
    [JsonPropertyName("is_alert")]
    public bool IsAlert { get; set; }

    /// <summary>Length of the audio the classification was made from.</summary>
    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds { get; set; }

    /// <summary><see cref="TelemetrySource"/>.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = TelemetrySource.Module;
}

/// <summary>Where one object was in the frame, as a fraction of the frame's own dimensions.
/// Normalised rather than in pixels so the same box is correct drawn over the snapshot, a
/// full-resolution still, or a scaled-down wall tile.</summary>
public sealed class DetectionBox
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }

    /// <summary>
    /// How confident the detector was about <em>this</em> object.
    ///
    /// Per box rather than per episode, because an episode holds one box for each of its class in
    /// shot and they are not seen equally well — a person at the gate and a person half behind a
    /// hedge arrive in the same record. <see cref="DetectionDocument.PeakConfidence"/> is the best
    /// any of them reached, which is the right number for deciding whether the episode is an alert
    /// and the wrong one to write beside every box.
    /// </summary>
    [JsonPropertyName("score")]
    public double Score { get; set; }
}

/// <summary>
/// Where the object was, from <see cref="At"/> until the next sample in the track.
///
/// Run-length encoded: a sample is only written when the box has actually moved, so a car parked
/// for ten minutes is one entry rather than six hundred. A consumer holds each sample until the
/// following one, and the last until the episode's <see cref="DetectionDocument.EndedAt"/>.
///
/// A null <see cref="Box"/> is a gap — the object was looked for, not found, and no longer
/// predictable, while the episode stayed open waiting to see whether it had really left. Without it
/// the run-length rule would hold a box over footage in which nothing is there.
/// </summary>
public sealed class DetectionTrackSample
{
    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }

    [JsonPropertyName("box")]
    public DetectionBox? Box { get; set; }
}

/// <summary>
/// One object present in front of one camera for a stretch of time — "this person, from 14:02:11 to
/// 14:02:53" — rather than one frame it was seen in.
///
/// One row per object, not per class: three people in shot is three of these, each with its own
/// start, duration and path, because the tracker tells them apart across frames.
///
/// Its own record type rather than a field on <see cref="SceneDocument"/>: there is no description
/// for it to ride on. A description is produced on a global floor of several seconds and only when a
/// 2 GB vision model is loaded, where a detection happens at a specific instant and needs a model a
/// hundred times smaller. Welding them would make every duration here wrong by however long the
/// description was delayed, and detection undeployable without the vision model.
///
/// Written twice under the same <see cref="Id"/> — once when the episode opens, with
/// <see cref="EndedAt"/> null, and once when it closes — so the second replaces the first. Storing
/// one record per frame instead would be 86,400 a day per camera against roughly twenty.
/// </summary>
public sealed class DetectionDocument : IOutputRecord
{
    [JsonPropertyName("type")]
    public string Type => "detection";

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = TelemetryJson.SchemaVersion;

    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("camera_id")]
    public string? CameraId { get; set; }

    [JsonPropertyName("received_at")]
    public DateTimeOffset? ReceivedAt { get; set; }

    /// <summary>When the object was first seen — not when it was confirmed. The confirmation
    /// requirement is how certainty is earned, not a claim about when the thing arrived.</summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>When it was last seen. Null while it is still there, which is what makes a live
    /// consumer able to say "currently present" rather than inferring it from a stale clock.</summary>
    [JsonPropertyName("ended_at")]
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>The model's own class string, verbatim and un-renamed — same rule as
    /// <see cref="SoundDocument.Label"/>, and for the same reason.</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    /// <summary>The best confidence this object was seen at across the whole episode, not the
    /// latest.</summary>
    [JsonPropertyName("peak_confidence")]
    public double PeakConfidence { get; set; }

    /// <summary>When that best look happened, so a consumer can fetch that exact snapshot rather
    /// than whatever the camera is showing now.</summary>
    [JsonPropertyName("peak_frame_at")]
    public DateTimeOffset PeakFrameAt { get; set; }

    /// <summary>Frames the object was actually detected in — frames it was only predicted to be
    /// present on do not count. Against the episode's duration this is how a consumer can tell a
    /// solid sighting from an intermittent one.</summary>
    [JsonPropertyName("frame_count")]
    public int FrameCount { get; set; }

    /// <summary>Where it was on the peak frame, with the score it was seen at.</summary>
    [JsonPropertyName("best_box")]
    public DetectionBox? BestBox { get; set; }

    /// <summary>
    /// Where it was as it went, run-length encoded — see <see cref="DetectionTrackSample"/>.
    ///
    /// This is the field footage wants, where <see cref="BestBox"/> is the field a still wants: the
    /// peak frame is the clearest single look at the object and is in the wrong place for every
    /// other second of a walk across the view. Storing it does not make this one record per frame —
    /// it stays one record per episode, which is the ratio that matters.
    ///
    /// Null where no geometry was kept, which a consumer should read as "nothing to draw" rather
    /// than "it never moved".
    /// </summary>
    [JsonPropertyName("track")]
    public List<DetectionTrackSample>? Track { get; set; }

    /// <summary>
    /// Whether this is one the operator asked to be told about, decided when the episode opened
    /// and never revised. Non-nullable so it is always written, following
    /// <see cref="SoundDocument.IsAlert"/>: this is the field a person gets woken up by, and a
    /// consumer reading absence as "not an alert" would be right by accident rather than by
    /// contract.
    /// </summary>
    [JsonPropertyName("is_alert")]
    public bool IsAlert { get; set; }

    /// <summary><see cref="TelemetrySource"/>.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = TelemetrySource.Server;
}
