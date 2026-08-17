using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Serval.Contracts;

namespace Serval.Server.Clips;

/// <summary>How far a clip has got. A clip exists in Mongo before its bytes do.</summary>
public enum ClipState
{
    /// <summary>Accepted and queued; ffmpeg is running or about to. Not listed and not playable.</summary>
    Writing,

    /// <summary>The file, the poster and the frozen documents are all on disk.</summary>
    Ready,

    /// <summary>Something went wrong; <see cref="SavedClip.Error"/> says what. The directory is gone.</summary>
    Failed,
}

/// <summary>
/// A clip the user asked to keep: its own MP4, its own poster frame, and a copy of the telemetry
/// that was true of its window.
///
/// The copying is the whole feature. Recordings roll off — seven days by default — and the
/// documents describing them do not roll off in step, so a clip that merely pointed at a time range
/// would become an unplayable row with a transcript attached. Everything needed to show
/// <em>and play</em> a clip years later is either in this document or in its directory, and neither
/// is reachable from the retention sweep.
///
/// The consequence to keep in mind when editing: nothing here is a live view of anything. A camera
/// renamed after the fact still reads by its old name inside a clip, because that is what the clip
/// was of.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class SavedClip
{
    [BsonId]
    public ObjectId Id { get; set; }

    public required string CameraId { get; set; }

    /// <summary>The camera's name when the clip was taken, kept because the camera may be renamed
    /// or deleted and the clip still has to say where it came from.</summary>
    public required string CameraName { get; set; }

    /// <summary>What the user called it. Free text, and the first thing search matches.</summary>
    public required string Name { get; set; }

    /// <summary>Username of whoever saved it. Everyone can see every clip; only this person and an
    /// Admin may rename or delete one.</summary>
    public required string SavedBy { get; set; }

    /// <summary>
    /// What the file actually covers, which is a whole number of recording segments.
    ///
    /// The trim UI snaps to segment boundaries precisely so these are the times the user chose
    /// rather than an approximation of them — there is no "asked for" stored separately because
    /// there is no difference to record.
    /// </summary>
    public required DateTimeOffset From { get; set; }

    public required DateTimeOffset To { get; set; }

    public required DateTimeOffset SavedAt { get; set; }

    public double DurationSeconds { get; set; }

    /// <summary>Size of clip.mp4 once written. Zero while <see cref="ClipState.Writing"/>.</summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Stored by name rather than by ordinal, which is the driver's default. As an ordinal, the
    /// declaration order of <see cref="ClipState"/> would be a storage format: inserting a member
    /// would silently re-label every stored clip, with nothing to compile against and no read error.
    /// </summary>
    [BsonRepresentation(BsonType.String)]
    public ClipState State { get; set; } = ClipState.Writing;

    /// <summary>Why it failed, for the App to show instead of a silent absence.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// One sentence describing what happens across the clip, from a vision pass over frames
    /// sampled along it. Null until that lands, and permanently null on a server with no vision
    /// model — the App omits the block rather than showing an empty one.
    /// </summary>
    public string? Summary { get; set; }

    /// <summary>
    /// Lowercased name plus every word spoken in the clip, so "search names and what was said" is
    /// one indexed field rather than a regex across an embedded document tree.
    ///
    /// Rebuilt on rename. Denormalised on purpose: the speech it contains is frozen, so there is no
    /// second writer that could put this out of step.
    /// </summary>
    public string SearchText { get; set; } = "";

    public ClipDocuments Documents { get; set; } = new();
}

/// <summary>
/// The telemetry that was true of a clip's window, copied at save time.
///
/// Stored ascending, unlike every live query in <c>TelemetryRepository</c>, which sorts descending
/// and truncates at a limit. Reading a window newest-first and keeping the first N drops the
/// <em>oldest</em> records — on a busy window that is the beginning of the clip, which is the part
/// a viewer needs first.
/// </summary>
public sealed class ClipDocuments
{
    public List<DetectionDocument> Detections { get; set; } = [];
    public List<SceneDocument> Scenes { get; set; } = [];
    public List<UtteranceDocument> Utterances { get; set; } = [];
    public List<ConversationTranscriptDocument> ConversationTranscripts { get; set; } = [];
    public List<SoundDocument> Sounds { get; set; } = [];
}
