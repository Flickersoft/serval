using Serval.Server.Ingest;

namespace Serval.Server.Tests;

/// <summary>
/// The discovery the whole watchdog design rests on: <b>a stalled ffmpeg keeps heartbeating.</b>
///
/// Measured against a source cut off mid-stream with its pipe held open — the shape a half-open RTSP
/// socket takes — ffmpeg printed a full progress block every second for fifteen seconds after the
/// data stopped, each one saying <c>progress=continue</c>, each one carrying the same frozen
/// <c>out_time_us</c>. A watchdog keyed on the blocks arriving would have waited forever, which is
/// worse than having none, because it looks like protection.
///
/// So the only thing that counts as progress is <c>out_time</c> going up. These tests replay the
/// observed traffic to hold that line.
/// </summary>
public class FfmpegProgressTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    /// <summary>One block as ffmpeg actually writes it, in the observed key order.</summary>
    private static string[] Block(long outTimeUs) =>
        [
            "frame=30",
            "fps=10.00",
            "stream_0_0_q=-1.0",
            "bitrate=N/A",
            "total_size=N/A",
            $"out_time_us={outTimeUs}",
            $"out_time_ms={outTimeUs}",
            "out_time=00:00:10.400000",
            "dup_frames=0",
            "drop_frames=0",
            "speed=1.0x",
            "progress=continue",
        ];

    private static void Feed(FfmpegProgress progress, long outTimeUs, DateTimeOffset at)
    {
        foreach (string line in Block(outTimeUs))
        {
            progress.Observe(line, at);
        }
    }

    /// <summary>
    /// The incident, replayed: fifteen well-formed blocks arriving on schedule, none of them
    /// evidence of anything. Silence is measured from the last block that moved, not the last that
    /// arrived.
    /// </summary>
    [Fact]
    public void Blocks_that_keep_arriving_with_a_frozen_out_time_are_not_progress()
    {
        var progress = new FfmpegProgress(Start);
        Feed(progress, 10_400_000, Start.AddSeconds(1));

        for (int second = 2; second <= 16; second++)
        {
            Feed(progress, 10_400_000, Start.AddSeconds(second));
        }

        // Fifteen heartbeats later, the stream last moved at second one.
        Assert.Equal(TimeSpan.FromSeconds(15), progress.SilentFor(Start.AddSeconds(16)));
    }

    /// <summary>And the healthy case it has to be distinguished from.</summary>
    [Fact]
    public void An_advancing_out_time_keeps_the_stream_current()
    {
        var progress = new FfmpegProgress(Start);

        for (int second = 1; second <= 10; second++)
        {
            Feed(progress, second * 1_000_000L, Start.AddSeconds(second));
            Assert.Equal(TimeSpan.Zero, progress.SilentFor(Start.AddSeconds(second)));
        }
    }

    /// <summary>
    /// Before the first frame the clock runs from the launch, so a source that never produces
    /// anything is caught by the same timeout as one that stops later.
    /// </summary>
    [Fact]
    public void A_process_that_never_reports_is_silent_from_the_start()
    {
        var progress = new FfmpegProgress(Start);

        Assert.Equal(TimeSpan.FromSeconds(90), progress.SilentFor(Start.AddSeconds(90)));
    }

    /// <summary>
    /// <c>out_time_us=N/A</c> appears before the first frame is muxed. It is not a number and not
    /// progress, and must not be read as a zero that then blocks every real value behind it.
    /// </summary>
    [Fact]
    public void A_not_available_out_time_is_not_progress()
    {
        var progress = new FfmpegProgress(Start);

        Assert.False(progress.Observe("out_time_us=N/A", Start.AddSeconds(1)));
        Assert.Equal(TimeSpan.FromSeconds(1), progress.SilentFor(Start.AddSeconds(1)));

        Assert.True(progress.Observe("out_time_us=500000", Start.AddSeconds(2)));
    }

    [Theory]
    [InlineData("out_time_us=3000000", 3_000_000L)]
    [InlineData("out_time_ms=3000000", 3_000_000L)]
    [InlineData("out_time=00:00:03.000000", null)]
    [InlineData("frame=30", null)]
    [InlineData("progress=continue", null)]
    [InlineData("speed=1.54e+03x", null)]
    [InlineData("bitrate=N/A", null)]
    public void Only_the_output_position_keys_are_read(string line, long? expected) =>
        Assert.Equal(expected, FfmpegProgress.OutTimeOf(line));

    /// <summary>
    /// The mark only moves forward. A block reporting an earlier position — a stream reset, or the
    /// two spellings disagreeing about units in some build — must not park the watchdog on a value
    /// nothing can beat.
    /// </summary>
    [Fact]
    public void Progress_never_moves_backwards()
    {
        var progress = new FfmpegProgress(Start);

        Assert.True(progress.Observe("out_time_us=10000000", Start.AddSeconds(1)));
        Assert.False(progress.Observe("out_time_us=2000000", Start.AddSeconds(2)));

        Assert.Equal(TimeSpan.FromSeconds(4), progress.SilentFor(Start.AddSeconds(5)));
    }
}
