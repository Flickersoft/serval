using Serval.Server.Recordings;

namespace Serval.Server.Media;

/// <summary>
/// What an export is actually going to contain, worked out before a single byte is written.
///
/// It exists because the answer is not simply "the range that was asked for". A range can cross
/// recording restarts, and the batches either side of one can turn out to be unjoinable; either way
/// the caller has to be told what it is getting while the response headers can still be set. So the
/// decisions are all made here, once, and the writing methods only carry them out.
/// </summary>
/// <param name="Batches">
/// The runs of segments that will be written, in order. Each shares one fMP4 init and is playable
/// only with it; more than one means the export spans a recording restart.
/// </param>
/// <param name="Truncated">
/// Whether footage inside the requested range was left out because it could not be joined to what
/// came before it — a camera that changed codec or resolution across a restart.
/// </param>
/// <param name="From">Where the footage actually starts, which is not where the request asked.</param>
/// <param name="To">Wall-clock end of the last segment included.</param>
/// <param name="DurationSeconds">
/// How long the finished file plays for. Not <c>To - From</c>: the dead air while a recorder
/// reconnected is not in any segment, so it is not in the file either.
/// </param>
public sealed record ExportPlan(
    IReadOnlyList<IReadOnlyList<RecordingSegment>> Batches,
    bool Truncated,
    DateTimeOffset From,
    DateTimeOffset To,
    double DurationSeconds)
{
    /// <summary>Nothing to write. The caller decides whether that is a 404 or a refusal.</summary>
    public bool IsEmpty => Batches.Count == 0;

    /// <summary>How many recording sessions the export spans.</summary>
    public int SessionCount => Batches.Count;

    /// <summary>Every segment across every batch, in order.</summary>
    public IEnumerable<RecordingSegment> Segments => Batches.SelectMany(b => b);
}
