using MongoDB.Bson;
using Serval.Server.Media;
using Serval.Server.Recordings;

namespace Serval.Server.Tests;

/// <summary>
/// Which segments can go in one exported clip. No ffmpeg and no filesystem — the decision is pure,
/// and it is the part that matters: an fMP4 segment is undecodable without the init it was written
/// with, so a run that crosses an ffmpeg restart produces a file that plays and then breaks.
///
/// This is also what the clip route's headers are computed from, before a byte of the body is
/// written, so the client learns it asked for more than it got.
/// </summary>
public class ClipExporterTests
{
    private static RecordingSegment Segment(string init, int index) => new()
    {
        Id = ObjectId.GenerateNewId(),
        CameraId = "front-door",
        FileName = $"seg-{index:D5}.m4s",
        InitFileName = init,
        StartedAt = new DateTimeOffset(2026, 8, 2, 14, 0, 0, TimeSpan.Zero).AddSeconds(index * 4),
        DurationSeconds = 4,
    };

    [Fact]
    public void One_session_exports_whole()
    {
        RecordingSegment[] segments =
        [
            Segment("init-a.mp4", 0),
            Segment("init-a.mp4", 1),
            Segment("init-a.mp4", 2),
        ];

        Assert.Equal(3, ClipExporter.LeadingRun(segments).Count);
    }

    [Fact]
    public void A_restart_partway_stops_the_clip_at_the_boundary()
    {
        RecordingSegment[] segments =
        [
            Segment("init-a.mp4", 0),
            Segment("init-a.mp4", 1),
            Segment("init-b.mp4", 2),
            Segment("init-b.mp4", 3),
        ];

        IReadOnlyList<RecordingSegment> run = ClipExporter.LeadingRun(segments);

        Assert.Equal(2, run.Count);
        Assert.All(run, s => Assert.Equal("init-a.mp4", s.InitFileName));
    }

    [Fact]
    public void A_restart_on_the_second_segment_still_yields_a_playable_first()
    {
        RecordingSegment[] segments = [Segment("init-a.mp4", 0), Segment("init-b.mp4", 1)];

        Assert.Single(ClipExporter.LeadingRun(segments));
    }

    [Fact]
    public void No_segments_is_empty_rather_than_an_exception()
    {
        // The route 404s before ever getting here, but a helper that indexes [0] unguarded is one
        // refactor away from being called somewhere that does not.
        Assert.Empty(ClipExporter.LeadingRun([]));
    }

    [Fact]
    public void The_run_keeps_its_order_so_the_reported_window_is_its_ends()
    {
        // The clip route reports X-Serval-Clip-From/-To off run[0] and run[^1], so the order here
        // is load-bearing rather than incidental.
        RecordingSegment[] segments =
        [
            Segment("init-a.mp4", 0),
            Segment("init-a.mp4", 1),
            Segment("init-b.mp4", 2),
        ];

        IReadOnlyList<RecordingSegment> run = ClipExporter.LeadingRun(segments);

        Assert.Equal(segments[0].StartedAt, run[0].StartedAt);
        Assert.Equal(segments[1].StartedAt, run[^1].StartedAt);
        Assert.Equal(
            segments[1].StartedAt.AddSeconds(4),
            run[^1].StartedAt.AddSeconds(run[^1].DurationSeconds));
    }
}
