using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Serval.Server.Recordings;

/// <summary>
/// One recorded segment on disk, indexed so playback can find the segments covering a time range
/// and retention can prune by age. The bytes live on the filesystem; this is only the index.
///
/// Segments are the atom of recording: a VOD playlist for any window is just the ordered
/// segments whose <see cref="StartedAt"/> falls in it.
/// </summary>
[BsonIgnoreExtraElements] // tolerate fields a newer schema may add, rather than crash reads
public sealed class RecordingSegment
{
    [BsonId]
    public ObjectId Id { get; set; }

    public required string CameraId { get; set; }

    /// <summary>Segment filename, relative to the camera's media directory.</summary>
    public required string FileName { get; set; }

    /// <summary>
    /// The fMP4 init segment this media segment depends on. Unlike self-contained MPEG-TS, a
    /// fMP4 segment is undecodable without its init, and each ffmpeg session writes a fresh
    /// one — so playback across a restart must switch init, which VOD expresses as a new Period.
    /// </summary>
    public required string InitFileName { get; set; }

    /// <summary>
    /// Wall-clock start of this segment: its session's start plus the real duration of everything
    /// the session recorded before it. Not its index times the nominal segment length — under
    /// <c>-c:v copy</c> a segment is as long as the camera's GOP made it.
    ///
    /// <para>Written once and never revised (<c>AddIfNewAsync</c> uses <c>SetOnInsert</c>), so
    /// whichever session indexes a filename first decides this permanently.</para>
    /// </summary>
    public required DateTimeOffset StartedAt { get; set; }

    /// <summary>Segment length in seconds, as reported in the HLS playlist (#EXTINF).</summary>
    public required double DurationSeconds { get; set; }
}
