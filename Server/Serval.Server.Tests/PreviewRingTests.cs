using Microsoft.Extensions.Logging.Abstractions;
using Serval.Server.Configuration;
using Serval.Server.Ingest;
using Serval.Server.Recordings;

namespace Serval.Server.Tests;

/// <summary>
/// The rolling detect-stream buffer alert previews are cut from.
///
/// Two things are worth pinning here and neither shows up until much later. The command line is one:
/// this is the only ffmpeg output in Serval that deliberately deletes what it wrote, and the flag
/// that does it sits one word away from the recording output that must never delete anything. The
/// arithmetic is the other: a ring segment's start time is what decides whether an alert's clip
/// contains the moment it fired, and it is computed across passes from a playlist that is losing
/// entries off the front while it is read.
/// </summary>
public class PreviewRingTests
{
    private static IngestOptions Options(double buffer = 90.0, double segment = 4.0) =>
        new() { PreviewBufferSeconds = buffer, SegmentSeconds = segment };

    private static PreviewRingPlan? Plan(string codec, IngestOptions? options = null) =>
        PreviewRing.Plan("cam-1", "20260811-120000", codec, options ?? Options(), NullLogger.Instance);

    private static List<string> Args(PreviewRingPlan plan, double segmentSeconds = 4.0) =>
        [.. PreviewRing.OutputArgs("0:v", plan, segmentSeconds)];

    // --- planning -----------------------------------------------------------------------------

    [Fact]
    public void A_copyable_stream_gets_a_ring()
    {
        PreviewRingPlan? plan = Plan("h264");

        Assert.NotNull(plan);
        Assert.Equal("preview-init-20260811-120000.mp4", plan.InitFileName);
    }

    [Fact]
    public void A_stream_that_cannot_be_copied_gets_no_ring_rather_than_an_exception()
    {
        // The difference from IngestPlanner, and the point of having a separate planner at all: a
        // recording that cannot be written is a hard error, but a preview buffer that cannot be
        // written must not cost this camera its detection, its wall tile and its vision model.
        Assert.Null(Plan("mjpeg"));
    }

    [Fact]
    public void A_source_that_would_not_say_what_it_sends_gets_no_ring()
    {
        Assert.Null(Plan(""));
    }

    [Fact]
    public void Zero_buffer_seconds_turns_the_ring_off()
    {
        Assert.Null(Plan("h264", Options(buffer: 0)));
    }

    [Fact]
    public void The_segment_count_covers_the_requested_buffer()
    {
        Assert.Equal(23, Plan("h264", Options(buffer: 90, segment: 4))!.SegmentCount);
    }

    [Fact]
    public void The_ring_is_never_shorter_than_two_segments()
    {
        // One segment is less than the one being written, so such a ring would never hold a
        // complete window at all.
        Assert.Equal(2, Plan("h264", Options(buffer: 1, segment: 10))!.SegmentCount);
    }

    [Fact]
    public void Hevc_is_tagged_so_the_clip_plays_where_hevc_plays_at_all()
    {
        Assert.Equal("hvc1", Plan("hevc")!.CodecTag);
        Assert.Null(Plan("h264")!.CodecTag);
    }

    // --- the command line ---------------------------------------------------------------------

    [Fact]
    public void The_ring_prunes_itself()
    {
        // The inverse of the recording output, one word apart. Losing this flag turns a bounded
        // buffer into an unbounded second copy of every camera, in a directory the retention sweep
        // is built never to touch.
        List<string> args = Args(Plan("h264")!);

        int flags = args.IndexOf("-hls_flags");
        Assert.True(flags >= 0);
        Assert.Contains("delete_segments", args[flags + 1]);
        Assert.Contains("independent_segments", args[flags + 1]);

        int size = args.IndexOf("-hls_list_size");
        Assert.True(size >= 0);
        Assert.NotEqual("0", args[size + 1]);
    }

    [Fact]
    public void Video_is_copied_and_never_encoded()
    {
        List<string> args = Args(Plan("h264")!);

        int codec = args.IndexOf("-c:v");
        Assert.Equal("copy", args[codec + 1]);
    }

    [Fact]
    public void Audio_is_optional_and_always_aac()
    {
        // Optional so a camera with no audio track still buffers; always AAC because the alternative
        // is probing for a codec this session otherwise never asks about, and then handling G.711,
        // which fMP4 cannot carry at all.
        List<string> args = Args(Plan("h264")!);

        int map = args.LastIndexOf("-map");
        Assert.Equal("0:a?", args[map + 1]);

        int codec = args.IndexOf("-c:a");
        Assert.Equal("aac", args[codec + 1]);
    }

    [Fact]
    public void Every_file_it_writes_carries_the_preview_prefix()
    {
        // What keeps the ring out of the recording's way: nothing named this is ever put in the
        // recording index, and the retention sweep only deletes filenames that index handed it.
        PreviewRingPlan plan = Plan("h264")!;
        List<string> args = Args(plan);

        Assert.StartsWith(PreviewRing.FilePrefix, plan.InitFileName);
        Assert.StartsWith(PreviewRing.FilePrefix, args[args.IndexOf("-hls_segment_filename") + 1]);
        Assert.Equal(PreviewRing.PlaylistName, args[^1]);
    }

    [Fact]
    public void Segments_are_stamped_with_the_session_that_wrote_them()
    {
        List<string> args = Args(Plan("h264")!);

        Assert.Equal(
            "preview-20260811-120000-%05d.m4s", args[args.IndexOf("-hls_segment_filename") + 1]);
    }

    // --- the index ----------------------------------------------------------------------------

    private static readonly DateTimeOffset Anchor = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    private static RecordingSegment Segment(string name, double offset, double duration) => new()
    {
        CameraId = "cam-1",
        FileName = name,
        InitFileName = "preview-init-20260811-120000.mp4",
        StartedAt = Anchor.AddSeconds(offset),
        DurationSeconds = duration,
    };

    [Fact]
    public void The_index_returns_the_segments_a_window_overlaps()
    {
        var index = new PreviewRingIndex();
        index.Begin("cam-1", "preview-init-20260811-120000.mp4");
        index.Add("cam-1", Segment("a", 0, 4));
        index.Add("cam-1", Segment("b", 4, 4));
        index.Add("cam-1", Segment("c", 8, 4));

        // A window starting inside 'a' and ending inside 'c' needs all three: segments only cut on
        // keyframes, so the padding asked for lands mid-segment nearly every time.
        IReadOnlyList<RecordingSegment> run =
            index.InRange("cam-1", Anchor.AddSeconds(2), Anchor.AddSeconds(9));

        Assert.Equal(["a", "b", "c"], run.Select(s => s.FileName));
    }

    [Fact]
    public void A_segment_that_merely_touches_an_end_is_not_included()
    {
        var index = new PreviewRingIndex();
        index.Begin("cam-1", "preview-init-20260811-120000.mp4");
        index.Add("cam-1", Segment("a", 0, 4));
        index.Add("cam-1", Segment("b", 4, 4));

        IReadOnlyList<RecordingSegment> run = index.InRange("cam-1", Anchor, Anchor.AddSeconds(4));

        Assert.Equal(["a"], run.Select(s => s.FileName));
    }

    [Fact]
    public void Pruned_segments_leave_the_index_with_the_files()
    {
        // Without this the worker hands ffmpeg names of segments that are gone, which it tolerates
        // by skipping them — producing a clip quietly missing its first seconds rather than one that
        // is honestly too short.
        var index = new PreviewRingIndex();
        index.Begin("cam-1", "preview-init-20260811-120000.mp4");
        index.Add("cam-1", Segment("a", 0, 4));
        index.Add("cam-1", Segment("b", 4, 4));

        index.Retain("cam-1", new HashSet<string> { "b" });

        Assert.Empty(index.InRange("cam-1", Anchor, Anchor.AddSeconds(4)));
        Assert.Single(index.InRange("cam-1", Anchor.AddSeconds(4), Anchor.AddSeconds(8)));
    }

    [Fact]
    public void A_camera_with_no_session_has_no_ring()
    {
        var index = new PreviewRingIndex();

        Assert.False(index.HasRing("cam-1"));
        Assert.Empty(index.InRange("cam-1", Anchor, Anchor.AddSeconds(60)));

        index.Begin("cam-1", "preview-init-20260811-120000.mp4");
        Assert.True(index.HasRing("cam-1"));

        index.Forget("cam-1");
        Assert.False(index.HasRing("cam-1"));
    }

    [Fact]
    public void Beginning_a_session_discards_the_previous_one()
    {
        // A ring never spans two sessions the way a recording index does: the older half has already
        // been deleted, and the init segment those segments need has been replaced.
        var index = new PreviewRingIndex();
        index.Begin("cam-1", "preview-init-20260811-120000.mp4");
        index.Add("cam-1", Segment("a", 0, 4));

        index.Begin("cam-1", "preview-init-20260811-130000.mp4");

        Assert.Empty(index.InRange("cam-1", Anchor, Anchor.AddSeconds(60)));
    }
}
