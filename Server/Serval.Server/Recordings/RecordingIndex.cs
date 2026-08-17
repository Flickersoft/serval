using MongoDB.Driver;
using Serval.Server.Storage;

namespace Serval.Server.Recordings;

/// <summary>
/// The index of recorded segments in Mongo: ingest adds segments as ffmpeg writes them,
/// playback reads back the segments covering a time range, and retention prunes old ones.
/// </summary>
public sealed class RecordingIndex
{
    private readonly IMongoCollection<RecordingSegment> _segments;

    public RecordingIndex(MongoContext context) => _segments = context.Recordings;

    /// <summary>
    /// Records a segment if it isn't indexed already. Ingest re-reads the playlist on a timer
    /// and re-sees segments it has already stored, so this is idempotent on (camera, filename).
    ///
    /// An upsert rather than a lookup and a conditional insert: the unique index on
    /// (CameraId, FileName) makes Mongo enforce the "if new" part, which turns two round trips
    /// into one and closes the window where two passes could both decide a segment was missing.
    /// The filter's fields are applied on insert, so only the rest need SetOnInsert.
    /// </summary>
    public async Task AddIfNewAsync(RecordingSegment segment, CancellationToken cancellationToken = default)
    {
        await _segments.UpdateOneAsync(
            s => s.CameraId == segment.CameraId && s.FileName == segment.FileName,
            Builders<RecordingSegment>.Update
                .SetOnInsert(s => s.InitFileName, segment.InitFileName)
                .SetOnInsert(s => s.StartedAt, segment.StartedAt)
                .SetOnInsert(s => s.DurationSeconds, segment.DurationSeconds),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
    }

    /// <summary>
    /// Segments overlapping [from, to), ordered by time. A segment overlaps if it starts
    /// before <paramref name="to"/> and ends (start + duration) after <paramref name="from"/>,
    /// so a window landing mid-segment still includes that segment.
    ///
    /// <para>Both ends are exclusive of a segment that merely touches them. A segment starting
    /// exactly at <paramref name="to"/> shares no time with the window at all, and including it
    /// made every clip trimmed to a segment boundary one segment longer than the range asked for
    /// — which is the whole thing that snapping to boundaries is supposed to guarantee.</para>
    /// </summary>
    public async Task<List<RecordingSegment>> InRangeAsync(
        string cameraId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        // Mongo can't compute start+duration in a range filter cheaply, so widen the lower
        // bound by a generous max segment length and refine in memory.
        DateTimeOffset lowerBound = from.AddMinutes(-1);

        List<RecordingSegment> candidates = await _segments
            .Find(s => s.CameraId == cameraId && s.StartedAt >= lowerBound && s.StartedAt < to)
            .SortBy(s => s.StartedAt)
            .ToListAsync(cancellationToken);

        return candidates
            .Where(s => s.StartedAt.AddSeconds(s.DurationSeconds) > from)
            .ToList();
    }

    /// <summary>
    /// The contiguous runs of footage in [from, to] — what a scrubber draws.
    ///
    /// Deliberately not <see cref="InRangeAsync"/> plus a merge in the caller: a 24-hour window is
    /// ~21,600 segments, and materialising every full document to produce two spans is the cost
    /// this route exists to avoid. Only the two fields the merge reads are fetched.
    /// </summary>
    public async Task<List<RecordingSpan>> CoverageAsync(
        string cameraId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        // Same widened lower bound as InRangeAsync, and for the same reason: a segment that started
        // before the window can still cover its first seconds.
        DateTimeOffset lowerBound = from.AddMinutes(-1);

        var candidates = await _segments
            .Find(s => s.CameraId == cameraId && s.StartedAt >= lowerBound && s.StartedAt <= to)
            .SortBy(s => s.StartedAt)
            .Project(s => new { s.StartedAt, s.DurationSeconds })
            .ToListAsync(cancellationToken);

        return RecordingCoverage.Merge(
            candidates.Select(s => (s.StartedAt, s.DurationSeconds)).ToList(), from, to);
    }

    /// <summary>
    /// When the oldest surviving segment for a camera starts, or null if it has none.
    ///
    /// One document, two fields, on the same <c>(CameraId, StartedAt)</c> ordering
    /// <see cref="CoverageAsync"/> already relies on — so this is a sort-and-limit, not a scan.
    /// It exists to give the disk figure a span: "412 GB" answers nothing on its own, and
    /// "7 days · 412 GB · ~59 GB/day" answers the question somebody actually has.
    /// </summary>
    public async Task<DateTimeOffset?> OldestSegmentAtAsync(
        string cameraId, CancellationToken cancellationToken = default)
    {
        List<DateTimeOffset> oldest = await _segments
            .Find(s => s.CameraId == cameraId)
            .SortBy(s => s.StartedAt)
            .Limit(1)
            .Project(s => s.StartedAt)
            .ToListAsync(cancellationToken);

        return oldest.Count > 0 ? oldest[0] : null;
    }

    /// <summary>Deletes index entries for segments that started before <paramref name="cutoff"/>, returning their filenames so the files can be removed too.</summary>
    public async Task<List<RecordingSegment>> DeleteBeforeAsync(
        string cameraId, DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        List<RecordingSegment> expired = await _segments
            .Find(s => s.CameraId == cameraId && s.StartedAt < cutoff)
            .ToListAsync(cancellationToken);

        if (expired.Count > 0)
        {
            await _segments.DeleteManyAsync(
                s => s.CameraId == cameraId && s.StartedAt < cutoff, cancellationToken);
        }

        return expired;
    }
}
