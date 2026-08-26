using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Serval.Server.Configuration;
using Serval.Server.Media;
using Serval.Server.Recordings;

namespace Serval.Server.Tests;

/// <summary>
/// Exporting a range that crosses a recording restart, against real ffmpeg and real files.
///
/// The rest of the exporter's tests are pure, and this one cannot be. What is being checked is not
/// a decision but a mechanism: whether several batches of fMP4, each undecodable without its own
/// init, actually become one file that plays straight through. Every part of that is ffmpeg's
/// behaviour rather than ours — that it will read a batch from a named pipe at all, that it lines
/// the batches up where the playlist says, and that <c>-c copy</c> survives the join. None of it is
/// knowable without running it.
///
/// The failure this guards against is a quiet one. Get the playlist wrong and ffmpeg still exits
/// zero and still writes an MP4; it is simply the wrong length, with the second batch crushed into
/// a fraction of a second. So these assert on the finished file's duration and timeline, not on an
/// exit code.
/// </summary>
public class ClipExporterJoinTests : IDisposable
{
    private const string CameraId = "front-door";

    private readonly string _root = Directory.CreateTempSubdirectory("serval-join-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string MediaRoot => Path.Combine(_root, "media");

    private string CameraDir => Path.Combine(MediaRoot, CameraId);

    private ClipExporter Exporter() => new(
        Options.Create(new ServerOptions
        {
            Media = new MediaOptions { Root = MediaRoot },
            Ingest = new IngestOptions { ExportPipeDir = Path.Combine(_root, "pipes") },
        }),
        NullLogger<ClipExporter>.Instance);

    [Fact]
    public async Task Two_recording_sessions_become_one_file_of_both_their_lengths()
    {
        if (!Ffmpeg.IsAvailable)
        {
            Assert.Skip("ffmpeg is not on PATH.");
        }

        List<RecordingSegment> first = Record("a", seconds: 6);
        List<RecordingSegment> second = Record("b", seconds: 6);
        List<RecordingSegment> all = [.. first, .. second];

        ClipExporter exporter = Exporter();
        ExportPlan plan = await exporter.PlanAsync(CameraId, all, TestContext.Current.CancellationToken);

        Assert.Equal(2, plan.SessionCount);
        Assert.False(plan.Truncated);

        string output = Path.Combine(_root, "joined.mp4");
        await exporter.WriteFileAsync(CameraId, plan, output, TestContext.Current.CancellationToken);

        // Both sessions' worth, not one. The whole point: before joining this file was six seconds.
        double expected = all.Sum(s => s.DurationSeconds);
        Assert.InRange(Duration(output), expected - 0.5, expected + 0.5);

        Assert.Equal("h264", VideoCodec(output));
    }

    [Fact]
    public async Task The_joined_timeline_never_runs_backwards()
    {
        if (!Ffmpeg.IsAvailable)
        {
            Assert.Skip("ffmpeg is not on PATH.");
        }

        List<RecordingSegment> all = [.. Record("a", seconds: 6), .. Record("b", seconds: 6)];

        ClipExporter exporter = Exporter();
        string output = Path.Combine(_root, "joined.mp4");

        await exporter.WriteFileAsync(
            CameraId,
            await exporter.PlanAsync(CameraId, all, TestContext.Current.CancellationToken),
            output,
            TestContext.Current.CancellationToken);

        // A batch that restarts the clock is the specific way this breaks, and it breaks silently:
        // ffmpeg forces the timestamps forward a tick at a time and writes a file that plays wrong.
        double[] times = PacketTimes(output);

        Assert.NotEmpty(times);
        for (int i = 1; i < times.Length; i++)
        {
            Assert.True(
                times[i] >= times[i - 1],
                $"packet {i} is at {times[i]:0.###}s, behind {times[i - 1]:0.###}s before it.");
        }

        // And the join is somewhere in the middle rather than the second batch being swallowed.
        Assert.True(times[^1] > 10.0, $"the joined file ends at {times[^1]:0.###}s.");
    }

    [Fact]
    public async Task A_single_session_still_takes_the_path_that_never_touched_a_pipe()
    {
        if (!Ffmpeg.IsAvailable)
        {
            Assert.Skip("ffmpeg is not on PATH.");
        }

        List<RecordingSegment> only = Record("a", seconds: 6);

        ClipExporter exporter = Exporter();
        ExportPlan plan = await exporter.PlanAsync(CameraId, only, TestContext.Current.CancellationToken);

        Assert.Equal(1, plan.SessionCount);

        string output = Path.Combine(_root, "single.mp4");
        await exporter.WriteFileAsync(CameraId, plan, output, TestContext.Current.CancellationToken);

        Assert.InRange(Duration(output), 5.5, 6.5);

        // Nothing was created to clean up, and nothing was left behind either.
        Assert.False(Directory.Exists(Path.Combine(_root, "pipes")));
    }

    [Fact]
    public async Task The_pipes_are_gone_once_the_export_is_written()
    {
        if (!Ffmpeg.IsAvailable)
        {
            Assert.Skip("ffmpeg is not on PATH.");
        }

        List<RecordingSegment> all = [.. Record("a", seconds: 6), .. Record("b", seconds: 6)];

        ClipExporter exporter = Exporter();
        await exporter.WriteFileAsync(
            CameraId,
            await exporter.PlanAsync(CameraId, all, TestContext.Current.CancellationToken),
            Path.Combine(_root, "joined.mp4"),
            TestContext.Current.CancellationToken);

        string pipes = Path.Combine(_root, "pipes");

        // An export that leaves its scratch behind would fill a tmpfs one restart at a time.
        Assert.True(
            !Directory.Exists(pipes) || Directory.GetDirectories(pipes).Length == 0,
            "the export left its pipes behind.");
    }

    [Fact]
    public async Task A_streamed_export_of_several_sessions_is_a_playable_file()
    {
        if (!Ffmpeg.IsAvailable)
        {
            Assert.Skip("ffmpeg is not on PATH.");
        }

        List<RecordingSegment> all = [.. Record("a", seconds: 6), .. Record("b", seconds: 6)];

        ClipExporter exporter = Exporter();
        string output = Path.Combine(_root, "streamed.mp4");

        // The response shape rather than the file one: fragmented, no length up front, and written
        // to something that cannot seek. That is what the download route hands the browser.
        await using (FileStream destination = File.Create(output))
        {
            await exporter.WriteAsync(
                CameraId,
                await exporter.PlanAsync(CameraId, all, TestContext.Current.CancellationToken),
                destination,
                TestContext.Current.CancellationToken);
        }

        double expected = all.Sum(s => s.DurationSeconds);
        Assert.InRange(Duration(output), expected - 0.5, expected + 0.5);
    }

    /// <summary>
    /// Records one session into the camera directory exactly as the recorder would, and returns its
    /// segments with the real durations out of the playlist rather than the ones asked for.
    /// </summary>
    private List<RecordingSegment> Record(string stamp, int seconds)
    {
        Directory.CreateDirectory(CameraDir);

        string init = $"init-{stamp}.mp4";

        Run("ffmpeg",
        [
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", $"testsrc=d={seconds}:s=128x72:r=10",
            "-f", "lavfi", "-i", $"sine=d={seconds}",
            "-c:v", "libx264", "-g", "20", "-c:a", "aac",
            "-f", "hls",
            "-hls_segment_type", "fmp4",
            "-hls_time", "2",
            "-hls_list_size", "0",
            "-hls_flags", "independent_segments",
            "-hls_fmp4_init_filename", init,
            "-hls_segment_filename", $"seg-{stamp}-%05d.m4s",
            $"{stamp}.m3u8",
        ], CameraDir);

        // The playlist is the source of truth for how long a segment really is: under -c:v copy a
        // segment is as long as the GOP made it, not as long as -hls_time asked for. Reading it
        // back is what the ingest session does, and the durations feed the join.
        var segments = new List<RecordingSegment>();
        DateTimeOffset startedAt = new(2026, 8, 2, 14, 0, 0, TimeSpan.Zero);
        double? pending = null;

        foreach (string line in File.ReadAllLines(Path.Combine(CameraDir, $"{stamp}.m3u8")))
        {
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                pending = double.Parse(
                    line["#EXTINF:".Length..].TrimEnd(','), CultureInfo.InvariantCulture);
            }
            else if (pending is { } duration && line.EndsWith(".m4s", StringComparison.Ordinal))
            {
                segments.Add(new RecordingSegment
                {
                    Id = ObjectId.GenerateNewId(),
                    CameraId = CameraId,
                    FileName = line.Trim(),
                    InitFileName = init,
                    StartedAt = startedAt,
                    DurationSeconds = duration,
                });

                startedAt = startedAt.AddSeconds(duration);
                pending = null;
            }
        }

        Assert.NotEmpty(segments);
        return segments;
    }

    private static double Duration(string path) => double.Parse(
        Probe(["-show_entries", "format=duration", "-of", "csv=p=0", path]).Trim(),
        CultureInfo.InvariantCulture);

    private static string VideoCodec(string path) => Probe(
        ["-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0", path]).Trim();

    private static double[] PacketTimes(string path) =>
    [
        .. Probe(["-select_streams", "v:0", "-show_entries", "packet=dts_time", "-of", "csv=p=0", path])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().TrimEnd(','))
            .Where(l => double.TryParse(l, CultureInfo.InvariantCulture, out _))
            .Select(l => double.Parse(l, CultureInfo.InvariantCulture)),
    ];

    private static string Probe(string[] arguments) =>
        Run("ffprobe", ["-v", "error", .. arguments], workingDirectory: null);

    private static string Run(string file, string[] arguments, string? workingDirectory)
    {
        var startInfo = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (workingDirectory is not null)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {file}.");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"{file} exited {process.ExitCode}: {stderr}");
        return stdout;
    }

    private static class Ffmpeg
    {
        public static bool IsAvailable { get; } = Probe();

        private static bool Probe()
        {
            try
            {
                using Process? process = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });

                if (process is null)
                {
                    return false;
                }

                process.WaitForExit(5000);
                return process.HasExited && process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
