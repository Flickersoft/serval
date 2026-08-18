using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serval.Server.Configuration;
using Serval.Server.Ingest;
using Serval.Server.Snapshots;

namespace Serval.Server.Tests;

/// <summary>
/// Staging raw frames through files, which is the transport choice this design turns on.
///
/// The alternative was a pipe, and it is a trap worth restating: ffmpeg's output loop is effectively
/// single-threaded across outputs and a Linux pipe holds 64 KiB against a frame of well over a
/// megabyte, so a reader that stalled would block ffmpeg's write — and this output rides on the
/// process recording the camera. Files cannot do that, at the cost of a completeness check and a
/// backlog bound. Both are tested here.
/// </summary>
public class DetectFrameReaderTests
{
    private const int Width = 16;
    private const int Height = 8;

    private static int FrameBytes => DetectFrameReader.FrameBytes(Width, Height);

    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"serval-detect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFrame(string dir, long index, int? bytes = null) =>
        File.WriteAllBytes(
            Path.Combine(dir, $"frame-{index}.yuv"), new byte[bytes ?? FrameBytes]);

    private static readonly DateTimeOffset SessionStart =
        new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Runs a reader over <paramref name="dir"/> for the duration of <paramref name="body"/>.
    ///
    /// The handoff is latest-wins with a single slot, so a caller that wants to observe successive
    /// frames has to let each one be taken before writing the next — which is what a camera does
    /// anyway, one frame per period.
    /// </summary>
    private static async Task WithReaderAsync(
        string dir,
        DetectFrameBroadcaster frames,
        Func<DetectFrameBroadcaster.Subscription, CancellationToken, Task> body,
        int backlog = 8)
    {
        using DetectFrameBroadcaster.Subscription subscription = frames.Subscribe("cam");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Task watching = DetectFrameReader.WatchAsync(
            dir, "cam", Width, Height, fps: 50, backlog, SessionStart, frames,
            NullLogger.Instance, cts.Token);

        try
        {
            await body(subscription, cts.Token);
        }
        finally
        {
            await cts.CancelAsync();
            try { await watching; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task A_frame_is_dated_by_its_index_rather_than_by_when_it_was_read()
    {
        // The index is the frame's position in the stream, so the timestamp means the moment the
        // camera saw it — not the moment we got round to the file. Reading the wall clock here was
        // measured ten seconds behind the footage on a real camera, which is long enough for someone
        // to leave the frame before the box describing them is drawn on it.
        string dir = NewDir();
        try
        {
            await WithReaderAsync(dir, new DetectFrameBroadcaster(), async (subscription, token) =>
            {
                foreach (long index in new[] { 9L, 10L, 11L })
                {
                    WriteFrame(dir, index);

                    DetectFrame frame = await subscription.ReadAsync(token);
                    try
                    {
                        // Anchored on the first index seen, then exactly 1/fps apart.
                        Assert.Equal(
                            (index - 9) * 0.02,
                            (frame.CapturedAt - SessionStart).TotalSeconds,
                            3);

                        Assert.Equal(Width, frame.Width);
                        Assert.Equal(Height, frame.Height);
                    }
                    finally
                    {
                        frame.Return();
                    }
                }
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Several_frames_at_once_collapse_to_the_newest()
    {
        // A detector that has fallen behind gains nothing from the frame it missed and everything
        // from the one in front of it. Queueing them would only add latency to every frame after.
        var frames = new DetectFrameBroadcaster();
        using DetectFrameBroadcaster.Subscription subscription = frames.Subscribe("cam");

        for (int i = 0; i < 3; i++)
        {
            frames.Publish(
                DetectFrame.Rent("cam", Width, Height, SessionStart.AddSeconds(i * 0.02)));
        }

        DetectFrame newest = await subscription.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0.04, (newest.CapturedAt - SessionStart).TotalSeconds, 3);
    }

    [Fact]
    public async Task A_partly_written_frame_waits_rather_than_being_published_torn()
    {
        // ffmpeg writes a frame over some interval, and the sweep will land in the middle of one.
        // Reading it anyway would hand the detector half a picture and half a buffer of zeroes.
        string dir = NewDir();
        try
        {
            WriteFrame(dir, 0, bytes: FrameBytes / 2);
            WriteFrame(dir, 1);

            var frames = new DetectFrameBroadcaster();
            using DetectFrameBroadcaster.Subscription subscription = frames.Subscribe("cam");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            Task watching = DetectFrameReader.WatchAsync(
                dir, "cam", Width, Height, fps: 50, backlog: 8, DateTimeOffset.UtcNow, frames,
                NullLogger.Instance, cts.Token);

            // Frame 1 is complete but sits behind an incomplete frame 0, and must not overtake it —
            // publishing out of order is exactly what the index ordering exists to prevent.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await subscription.ReadAsync(cts.Token));

            try { await watching; } catch (OperationCanceledException) { }

            Assert.True(File.Exists(Path.Combine(dir, "frame-0.yuv")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_published_frame_is_deleted_so_the_directory_stays_bounded()
    {
        string dir = NewDir();
        try
        {
            await WithReaderAsync(dir, new DetectFrameBroadcaster(), async (subscription, token) =>
            {
                WriteFrame(dir, 0);
                (await subscription.ReadAsync(token)).Return();

                // The reader hands the frame over before it unlinks the file, so holding one says
                // nothing about whether the sweep has got there yet. Waiting for it is the
                // assertion; the token WithReaderAsync bounds is what makes a delete that never
                // comes a failure with the leftovers named rather than a hang.
                while (Directory.EnumerateFiles(dir, "frame-*.yuv").Any()
                       && !token.IsCancellationRequested)
                {
                    await Task.Delay(20, CancellationToken.None);
                }

                Assert.Empty(Directory.EnumerateFiles(dir, "frame-*.yuv"));
            });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_backlog_drops_the_oldest_frames_rather_than_growing()
    {
        // Nothing upstream can be made to wait — that is the point of writing files — so falling
        // behind has to cost the oldest frames rather than memory.
        string dir = NewDir();
        try
        {
            for (long index = 0; index < 20; index++)
            {
                WriteFrame(dir, index);
            }

            await WithReaderAsync(
                dir,
                new DetectFrameBroadcaster(),
                async (subscription, token) =>
                {
                    DetectFrame frame = await subscription.ReadAsync(token);
                    try
                    {
                        // Sixteen of the twenty are past the backlog and never read. Whichever
                        // survivor arrives is dated as itself and not as the session's first frame,
                        // because the clock is shown every index it sees — including the dropped
                        // ones, or the anchor would move and date everything after it early.
                        Assert.True(
                            (frame.CapturedAt - SessionStart).TotalSeconds >= 0.32 - 0.001,
                            $"expected a frame at or past index 16, got {frame.CapturedAt:O}");
                    }
                    finally
                    {
                        frame.Return();
                    }
                },
                backlog: 4);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_pass_that_throws_costs_one_pass_rather_than_the_reader()
    {
        // This loop is the only thing deleting what ffmpeg writes, and it runs as a side task whose
        // exceptions FfmpegRunner discards. A throw escaping it would end the sweep silently and
        // leave the session filling tmpfs at DetectFps until the mount ran out — the backlog bound
        // only holds while the loop that enforces it is still running.
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Directory permissions are the lever this uses to force the throw.");
        }
        else
        {
            await ReaderSurvivesAnUnreadableDirectoryAsync();
        }
    }

    /// <summary>The Linux half of the test above, separated so the mode calls sit under a platform
    /// guard the analyzer can see.</summary>
    [SupportedOSPlatform("linux")]
    private static async Task ReaderSurvivesAnUnreadableDirectoryAsync()
    {
        const UnixFileMode Usable =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        string dir = NewDir();
        try
        {
            // Unreadable, so the sweep's own EnumerateFiles throws where nothing catches it by type.
            File.SetUnixFileMode(dir, UnixFileMode.None);

            var frames = new DetectFrameBroadcaster();
            using DetectFrameBroadcaster.Subscription subscription = frames.Subscribe("cam");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            Task watching = DetectFrameReader.WatchAsync(
                dir, "cam", Width, Height, fps: 50, backlog: 8, SessionStart, frames,
                NullLogger.Instance, cts.Token);

            // Long enough for several passes to fail before the directory is readable again.
            await Task.Delay(150, TestContext.Current.CancellationToken);
            Assert.False(watching.IsCompleted, "the reader gave up on the first bad pass");

            File.SetUnixFileMode(dir, Usable);
            WriteFrame(dir, 0);

            // Still sweeping: it picks the frame up and deletes it like any other.
            DetectFrame frame = await subscription.ReadAsync(cts.Token);
            frame.Return();

            await cts.CancelAsync();
            try { await watching; } catch (OperationCanceledException) { }

            Assert.Empty(Directory.EnumerateFiles(dir, "frame-*.yuv"));
        }
        finally
        {
            File.SetUnixFileMode(dir, Usable);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task With_nobody_subscribed_frames_are_discarded_unread()
    {
        // Ingest produces these whenever DetectFps is positive, but a host with no detection model
        // has nothing that wants them, and they must not pile up on tmpfs.
        string dir = NewDir();
        try
        {
            WriteFrame(dir, 0);
            WriteFrame(dir, 1);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            Task watching = DetectFrameReader.WatchAsync(
                dir, "cam", Width, Height, fps: 50, backlog: 8, DateTimeOffset.UtcNow,
                new DetectFrameBroadcaster(), NullLogger.Instance, cts.Token);

            while (Directory.EnumerateFiles(dir, "frame-*.yuv").Any()
                   && !cts.Token.IsCancellationRequested)
            {
                await Task.Delay(20, CancellationToken.None);
            }

            await cts.CancelAsync();
            try { await watching; } catch (OperationCanceledException) { }

            Assert.Empty(Directory.EnumerateFiles(dir, "frame-*.yuv"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_second_subscriber_for_one_camera_is_refused()
    {
        // These buffers are pooled and owned. Two subscribers would mean two claims on one array and
        // a return while the other was still reading it.
        var frames = new DetectFrameBroadcaster();
        using DetectFrameBroadcaster.Subscription first = frames.Subscribe("cam");

        Assert.Throws<InvalidOperationException>(() => frames.Subscribe("cam"));
    }

    [Fact]
    public void Unsubscribing_frees_the_camera_for_a_restarted_session()
    {
        var frames = new DetectFrameBroadcaster();
        frames.Subscribe("cam").Dispose();

        using DetectFrameBroadcaster.Subscription second = frames.Subscribe("cam");
        Assert.True(frames.HasSubscriber("cam"));
    }

    [Theory]
    [InlineData(1920, 1080, 1280, 1280, 720)]
    [InlineData(640, 360, 1280, 640, 360)]      // never upscaled
    [InlineData(1920, 1080, 0, 1920, 1080)]     // no cap
    [InlineData(1000, 563, 500, 500, 282)]      // both axes forced even
    public void A_frame_size_follows_the_source_aspect_and_stays_even(
        int sourceWidth, int sourceHeight, int maxWidth, int expectedWidth, int expectedHeight)
    {
        (int width, int height) =
            DetectFrameReader.FrameSize(sourceWidth, sourceHeight, maxWidth);

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
        Assert.Equal(0, width % 2);
        Assert.Equal(0, height % 2);
    }

    [Fact]
    public void A_source_that_will_not_report_its_size_gets_no_detect_output()
    {
        // Frames of unknown size cannot be told apart from frames still being written. Recording is
        // unaffected — a probe failure must never cost footage.
        var options = new IngestOptions();

        Assert.Null(DetectFrameReader.Plan(
            "cam", new VideoProbe("h264", null, null), options, NullLogger.Instance));
    }

    [Fact]
    public void A_non_positive_frame_rate_turns_the_detect_output_off()
    {
        var options = new IngestOptions { DetectFps = 0 };

        Assert.Null(DetectFrameReader.Plan(
            "cam", new VideoProbe("h264", 1920, 1080), options, NullLogger.Instance));
    }

    [Fact]
    public void A_plan_stages_frames_per_camera_outside_the_media_root()
    {
        // Per camera so one reader's reset cannot discard another's frames, and off the media root
        // because these belong on tmpfs rather than the disk holding the recordings.
        var options = new IngestOptions { DetectFrameDir = "/dev/shm/serval/detect" };

        DetectFramePlan plan = Assert.IsType<DetectFramePlan>(DetectFrameReader.Plan(
            "front-door", new VideoProbe("h264", 1920, 1080), options, NullLogger.Instance));

        Assert.Equal(Path.Combine("/dev/shm/serval/detect", "front-door"), plan.Directory);
        Assert.Equal(1920, plan.Width);
        Assert.Equal(1080, plan.Height);
    }

    [Fact]
    public void A_directory_on_tmpfs_is_not_warned_about()
    {
        // The check this guards asked Directory.GetDirectoryRoot for the filesystem, which on Linux
        // answers "/" for every absolute path — so a directory on a perfectly good tmpfs was
        // reported as whatever the root filesystem happened to be. Running the Server produced
        // "'/dev/shm/serval' is on a btrfs filesystem rather than tmpfs", which is advice about the
        // wrong mount, on the one configuration the setting is meant to encourage.
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/dev/shm"))
        {
            Assert.Skip("No /dev/shm to check against.");
        }

        string dir = $"/dev/shm/serval-mount-check-{Guid.NewGuid():N}";
        var logger = new CapturingLogger();

        try
        {
            DetectFrameReader.WarnIfNotInMemory(
                new IngestOptions { DetectFps = 2, DetectFrameDir = dir }, logger);

            Assert.DoesNotContain(logger.Warnings, w => w.Contains("rather than tmpfs"));
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
    public void A_directory_on_a_real_disk_is_warned_about()
    {
        // The other half: the warning must still fire where it is deserved, or the fix above would
        // have been "stop warning at all".
        var logger = new CapturingLogger();
        string dir = Path.Combine(Path.GetTempPath(), $"serval-mount-{Guid.NewGuid():N}");

        // Only meaningful where the temp directory is not itself in memory, which varies by host.
        var temp = new DriveInfo(Path.GetTempPath());
        if (temp.DriveFormat is "tmpfs" or "ramfs")
        {
            Assert.Skip("This host's temp directory is already in memory.");
        }

        try
        {
            DetectFrameReader.WarnIfNotInMemory(
                new IngestOptions { DetectFps = 2, DetectFrameDir = dir }, logger);

            Assert.Contains(logger.Warnings, w => w.Contains("rather than tmpfs"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    [Fact]
    public void The_poll_period_stays_ahead_of_the_frame_rate()
    {
        // Polling at exactly the frame period would add up to a whole frame of latency. A quarter of
        // it costs a directory sweep, which on tmpfs is microseconds.
        Assert.True(DetectFrameReader.PollPeriod(5).TotalMilliseconds <= 50);
        Assert.True(DetectFrameReader.PollPeriod(1).TotalMilliseconds <= 250);

        // ...but never a spin, however the frame rate is misconfigured.
        Assert.True(DetectFrameReader.PollPeriod(10_000).TotalMilliseconds >= 25);
    }
}
