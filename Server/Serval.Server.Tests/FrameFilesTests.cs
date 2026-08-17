using Serval.Server.Ingest;

namespace Serval.Server.Tests;

/// <summary>
/// Reading a frame's own position out of its filename, which is what dates every detection.
///
/// The value this replaces was the wall clock at the moment the Server polled the file — the
/// camera's buffering, RTSP, ffmpeg's filter and encode, the write and a poll period all inside it.
/// Measured against a real camera that ran ten seconds behind the footage, which is long enough for
/// a person to leave the frame before the box describing them is drawn on it.
///
/// Exercised through both naming schemes that use it — the dashboard's JPEGs and detection's raw
/// frames — because the two are dated on one media clock and a consumer draws one over the other.
/// </summary>
public class FrameFilesTests
{
    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"serval-frames-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string dir, string name) =>
        File.WriteAllBytes(Path.Combine(dir, name), [0xFF, 0xD8, 0x00, 0xFF, 0xD9]);

    [Theory]
    [InlineData("snap-0.jpg", "snap-", ".jpg", 0L)]
    [InlineData("snap-7.jpg", "snap-", ".jpg", 7L)]
    [InlineData("snap-123456.jpg", "snap-", ".jpg", 123456L)]
    [InlineData("frame-42.yuv", "frame-", ".yuv", 42L)]
    public void An_index_is_read_from_the_name(
        string fileName, string prefix, string extension, long expected) =>
        Assert.Equal(expected, FrameFiles.IndexOf(fileName, prefix, extension));

    [Theory]
    [InlineData("snapshot.jpg")]          // no index at all
    [InlineData("snap-.jpg")]
    [InlineData("snap-12.png")]
    [InlineData("snap--4.jpg")]           // negative is not a frame position
    [InlineData("seg-20260804-161337-00000.m4s")]
    public void Anything_else_is_not_a_frame(string fileName) =>
        Assert.Null(FrameFiles.IndexOf(fileName, "snap-", ".jpg"));

    [Fact]
    public void A_raw_frame_is_not_mistaken_for_a_snapshot()
    {
        // The two outputs can share a directory in principle, and a reader picking up the other's
        // frames would publish pictures of the wrong size against the right clock.
        Assert.Null(FrameFiles.IndexOf("frame-9.yuv", "snap-", ".jpg"));
        Assert.Null(FrameFiles.IndexOf("snap-9.jpg", "frame-", ".yuv"));
    }

    [Fact]
    public void Frames_are_ordered_numerically_and_not_as_text()
    {
        // "snap-10" sorts before "snap-9" as a string. Publishing in that order would hand the
        // detection policy a clock that goes backwards, and an episode's track with it.
        string dir = NewDir();
        try
        {
            foreach (int i in new[] { 9, 10, 2, 100, 11 })
            {
                Write(dir, $"snap-{i}.jpg");
            }

            Assert.Equal(
                [2L, 9L, 10L, 11L, 100L],
                FrameFiles.Pending(dir, "snap-", ".jpg").Select(f => f.Index));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Files_that_are_not_frames_are_left_alone()
    {
        string dir = NewDir();
        try
        {
            Write(dir, "snap-3.jpg");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "hello");

            Assert.Equal(3L, Assert.Single(FrameFiles.Pending(dir, "snap-", ".jpg")).Index);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_reset_clears_what_a_previous_session_left()
    {
        // Frames from the last run are numbered on *its* timeline, so republishing them dates old
        // pictures to this session's opening seconds — the same mistake the recording index makes
        // if it adopts a stale playlist.
        string dir = NewDir();
        try
        {
            Write(dir, "snap-1400.jpg");
            Write(dir, "snap-1401.jpg");

            FrameFiles.Reset(dir, "snap-", ".jpg");

            Assert.Empty(FrameFiles.Pending(dir, "snap-", ".jpg"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_reset_creates_the_directory_when_there_is_none()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"serval-frames-{Guid.NewGuid():N}");
        try
        {
            FrameFiles.Reset(dir, "snap-", ".jpg");

            Assert.True(Directory.Exists(dir));
            Assert.Empty(FrameFiles.Pending(dir, "snap-", ".jpg"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void The_first_frame_to_arrive_anchors_the_clock()
    {
        // ffmpeg numbers from the start of its own timeline, which is not always where the media
        // begins: a source whose timestamps start late has the fps filter counting from the gap.
        // Taking the first index as the session start keeps the spacing exact and puts frame one
        // where the recorder's first segment starts.
        var start = new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);
        var clock = new FrameClock(start, fps: 5.0);

        Assert.Equal(start, clock.At(37));
        Assert.Equal(start.AddSeconds(0.2), clock.At(38));
        Assert.Equal(start.AddSeconds(1.0), clock.At(42));
    }

    [Fact]
    public void A_frame_is_dated_by_its_index_and_not_by_when_it_was_read()
    {
        // The whole point: a reader that falls behind must not move the timestamp, or a box lands
        // on the wrong frame of footage.
        var start = DateTimeOffset.UtcNow;
        var prompt = new FrameClock(start, fps: 5.0);
        var late = new FrameClock(start, fps: 5.0);

        Assert.Equal(prompt.At(0), late.At(0));
        Assert.Equal(prompt.At(50), late.At(50));
    }
}
