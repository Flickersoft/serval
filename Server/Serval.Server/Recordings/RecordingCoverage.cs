namespace Serval.Server.Recordings;

/// <summary>One contiguous run of recorded footage.</summary>
public readonly record struct RecordingSpan(DateTimeOffset From, DateTimeOffset To);

/// <summary>
/// Collapses the segment index into the runs of footage it represents — pure, no I/O, the same
/// shape as <see cref="HlsPlaylist"/> and testable for the same reason.
///
/// This exists because <c>/recordings</c> answers the wrong question for a scrubber. It returns one
/// row per segment: measured against a live camera, two hours is 1798 rows and 208 KB, so a day is
/// ~21,600 rows and megabytes of JSON — an expensive way to learn that the camera recorded all day.
/// Merged, the same day is typically one to three spans.
///
/// The merge is cheap because ingest makes it so. <c>FfmpegStreamSession.IndexSegmentsAsync</c>
/// derives each segment's start by accumulating <c>#EXTINF</c> from the session start, so within a
/// session the joints are contiguous by construction — measured across those same 1798 segments,
/// zero gaps and zero overlaps. A gap larger than <see cref="DefaultTolerance"/> therefore always
/// means a real ffmpeg restart or a hole retention has punched, never arithmetic drift.
/// </summary>
public static class RecordingCoverage
{
    /// <summary>
    /// How far apart two segments may sit and still count as continuous. Generous relative to the
    /// microsecond-exact joints ingest produces, and far below the shortest real gap.
    /// </summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The runs of footage <paramref name="segments"/> covers, clipped to
    /// <paramref name="from"/>..<paramref name="to"/> and ordered by time.
    ///
    /// A change of init segment does <em>not</em> split a span. Coverage answers "is there footage
    /// here", not "can one decoder run across it" — the playlist's <c>EXT-X-DISCONTINUITY</c>
    /// already carries the latter, and splitting here would draw a hairline gap in the scrubber
    /// where no time was actually lost.
    /// </summary>
    public static List<RecordingSpan> Merge(
        IReadOnlyList<RecordingSegment> segments,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan? tolerance = null) =>
        Merge(
            segments.Select(s => (s.StartedAt, s.DurationSeconds)).ToList(),
            from, to, tolerance);

    /// <summary>
    /// The same merge over just the two fields it reads, so <see cref="RecordingIndex"/> can
    /// project a day's segments out of Mongo without materialising ~21,600 whole documents.
    /// </summary>
    public static List<RecordingSpan> Merge(
        IReadOnlyList<(DateTimeOffset StartedAt, double DurationSeconds)> segments,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan? tolerance = null)
    {
        var spans = new List<RecordingSpan>();
        if (segments.Count == 0 || to <= from)
        {
            return spans;
        }

        TimeSpan slack = tolerance ?? DefaultTolerance;

        // Sorted here rather than assumed: RecordingIndex does sort, but a pure function that
        // silently depends on its caller's ordering is a trap for the next caller.
        var ordered = segments.OrderBy(s => s.StartedAt).ToList();

        DateTimeOffset spanStart = ordered[0].StartedAt;
        DateTimeOffset spanEnd = spanStart.AddSeconds(ordered[0].DurationSeconds);

        foreach ((DateTimeOffset StartedAt, double DurationSeconds) segment in ordered.Skip(1))
        {
            DateTimeOffset end = segment.StartedAt.AddSeconds(segment.DurationSeconds);

            if (segment.StartedAt <= spanEnd + slack)
            {
                // Overlapping segments would otherwise shorten the span, so only ever extend.
                if (end > spanEnd)
                {
                    spanEnd = end;
                }

                continue;
            }

            Add(spans, spanStart, spanEnd, from, to);
            spanStart = segment.StartedAt;
            spanEnd = end;
        }

        Add(spans, spanStart, spanEnd, from, to);
        return spans;
    }

    /// <summary>
    /// Clips a span to the requested window and keeps it if anything survives.
    ///
    /// The clip is load-bearing: <see cref="RecordingIndex.InRangeAsync"/> deliberately returns the
    /// segment straddling <paramref name="from"/>, whose start is before the window, and a scrubber
    /// given a span that begins off its left edge would paint outside its own track.
    /// </summary>
    private static void Add(
        List<RecordingSpan> spans,
        DateTimeOffset start, DateTimeOffset end,
        DateTimeOffset from, DateTimeOffset to)
    {
        DateTimeOffset clippedStart = start < from ? from : start;
        DateTimeOffset clippedEnd = end > to ? to : end;

        if (clippedEnd > clippedStart)
        {
            spans.Add(new RecordingSpan(clippedStart, clippedEnd));
        }
    }
}
