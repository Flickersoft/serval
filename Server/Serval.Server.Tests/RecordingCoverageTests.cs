using Serval.Server.Recordings;

namespace Serval.Server.Tests;

/// <summary>
/// Coverage is the scrubber's ground truth: it is what says "there is footage here", and getting
/// it wrong means either a solid day drawn as 20,000 hairlines or a hole that is not there. The
/// merge is the whole of that logic, so it is pinned here rather than behind a database.
/// </summary>
public class RecordingCoverageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 24, 18, 0, 0, TimeSpan.Zero);

    private static RecordingSegment Seg(DateTimeOffset start, double dur = 4.0, string init = "init-s1.mp4") => new()
    {
        CameraId = "cam1",
        FileName = $"seg-{start:HHmmss}.m4s",
        InitFileName = init,
        StartedAt = start,
        DurationSeconds = dur,
    };

    /// <summary>A run of back-to-back segments, exactly as ingest indexes them.</summary>
    private static List<RecordingSegment> Run(DateTimeOffset start, int count, double dur = 4.0, string init = "init-s1.mp4")
    {
        var segments = new List<RecordingSegment>();
        DateTimeOffset at = start;

        for (int i = 0; i < count; i++)
        {
            segments.Add(Seg(at, dur, init));
            at = at.AddSeconds(dur);
        }

        return segments;
    }

    [Fact]
    public void Contiguous_segments_merge_into_one_span()
    {
        // The measured reality: a live camera's 1798 segments over two hours had zero gaps, because
        // ingest derives each start by accumulating EXTINF from the session start.
        List<RecordingSegment> segments = Run(T0, 1798, dur: 4.0);

        List<RecordingSpan> spans = RecordingCoverage.Merge(segments, T0, T0.AddHours(3));

        Assert.Single(spans);
        Assert.Equal(T0, spans[0].From);
        Assert.Equal(T0.AddSeconds(1798 * 4.0), spans[0].To);
    }

    [Fact]
    public void A_gap_larger_than_the_tolerance_splits_the_span()
    {
        // ffmpeg died and was restarted five minutes later: two runs, and the hole between them is
        // real footage that does not exist.
        List<RecordingSegment> segments =
        [
            .. Run(T0, 3),
            .. Run(T0.AddSeconds(12).AddMinutes(5), 3, init: "init-s2.mp4"),
        ];

        List<RecordingSpan> spans = RecordingCoverage.Merge(segments, T0, T0.AddHours(1));

        Assert.Equal(2, spans.Count);
        Assert.Equal(T0.AddSeconds(12), spans[0].To);
        Assert.Equal(T0.AddSeconds(12).AddMinutes(5), spans[1].From);
    }

    [Fact]
    public void Sub_second_jitter_does_not_split()
    {
        // Otherwise one continuous day draws as thousands of hairlines with slivers between them.
        List<RecordingSegment> segments =
        [
            Seg(T0, dur: 4.0),
            Seg(T0.AddMilliseconds(4200)),   // 200 ms late
            Seg(T0.AddMilliseconds(8100)),   // and back again
        ];

        Assert.Single(RecordingCoverage.Merge(segments, T0, T0.AddHours(1)));
    }

    [Fact]
    public void An_init_change_with_no_time_gap_stays_one_span()
    {
        // Pins the decision: coverage is about footage, not decodability. The playlist's
        // EXT-X-DISCONTINUITY already tells a player to reset — no time was lost here, so the
        // scrubber must not draw a gap.
        List<RecordingSegment> segments =
        [
            .. Run(T0, 3, init: "init-s1.mp4"),
            .. Run(T0.AddSeconds(12), 3, init: "init-s2.mp4"),
        ];

        Assert.Single(RecordingCoverage.Merge(segments, T0, T0.AddHours(1)));
    }

    [Fact]
    public void Spans_are_clipped_to_the_requested_window()
    {
        // InRangeAsync deliberately returns the segment straddling `from`, so the raw span starts
        // before the window. Unclipped, a scrubber would paint outside its own track.
        List<RecordingSegment> segments = Run(T0, 100);
        DateTimeOffset from = T0.AddSeconds(10);
        DateTimeOffset to = T0.AddSeconds(50);

        List<RecordingSpan> spans = RecordingCoverage.Merge(segments, from, to);

        Assert.Single(spans);
        Assert.Equal(from, spans[0].From);
        Assert.Equal(to, spans[0].To);
    }

    [Fact]
    public void Segments_out_of_order_are_still_merged_correctly()
    {
        List<RecordingSegment> segments = [Seg(T0.AddSeconds(8)), Seg(T0), Seg(T0.AddSeconds(4))];

        List<RecordingSpan> spans = RecordingCoverage.Merge(segments, T0, T0.AddHours(1));

        Assert.Single(spans);
        Assert.Equal(T0, spans[0].From);
        Assert.Equal(T0.AddSeconds(12), spans[0].To);
    }

    [Fact]
    public void Overlapping_segments_never_shorten_a_span()
    {
        // A long segment followed by one that starts inside it: the span must end at the later of
        // the two ends, not at whichever came last in the list.
        List<RecordingSegment> segments = [Seg(T0, dur: 30.0), Seg(T0.AddSeconds(4), dur: 4.0)];

        List<RecordingSpan> spans = RecordingCoverage.Merge(segments, T0, T0.AddHours(1));

        Assert.Single(spans);
        Assert.Equal(T0.AddSeconds(30), spans[0].To);
    }

    [Fact]
    public void A_single_segment_yields_a_span_of_its_own_duration()
    {
        List<RecordingSpan> spans = RecordingCoverage.Merge([Seg(T0, dur: 6.5)], T0, T0.AddHours(1));

        Assert.Single(spans);
        Assert.Equal(T0.AddSeconds(6.5), spans[0].To);
    }

    [Fact]
    public void An_empty_index_yields_no_spans()
    {
        Assert.Empty(RecordingCoverage.Merge(new List<RecordingSegment>(), T0, T0.AddHours(1)));
    }

    [Fact]
    public void Segments_entirely_outside_the_window_are_dropped()
    {
        List<RecordingSegment> segments = Run(T0, 3);

        Assert.Empty(RecordingCoverage.Merge(segments, T0.AddHours(1), T0.AddHours(2)));
    }
}
