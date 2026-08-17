using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Serval.Server.Ingest;

namespace Serval.Server.Tests;

/// <summary>
/// The failure the watchdog exists for, run end to end against a real ffmpeg.
///
/// A camera that drops its connection makes ffmpeg exit, and the reconnect has always handled that.
/// A half-open TCP socket does something else: it leaves ffmpeg blocked in a read that never
/// completes, so the process stays up, the session stays held open, and every layer keyed on process
/// exit waits with it. Observed on a real camera for over five hours while the dashboard showed it
/// recording.
///
/// <para>A FIFO held open by a writer that has stopped sending is that socket, exactly — data, then
/// silence, and never an EOF. It is also the setup that revealed ffmpeg goes on printing progress
/// throughout, which is why <see cref="FfmpegProgress"/> reads the position rather than the pulse.
/// These are slow by this assembly's standards and they earn it: the failure they guard is silent,
/// costs footage, and cannot be reproduced without a process that really hangs.</para>
/// </summary>
public class IngestStallWatchdogTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"serval-stall-{Guid.NewGuid():N}");

    public IngestStallWatchdogTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static bool CanRunPosixTools =>
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private static bool FfmpegIsAvailable
    {
        get
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo("ffmpeg")
                {
                    ArgumentList = { "-version" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                probe!.WaitForExit(5000);
                return probe.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    private static Task RunAsync(
        string path,
        IReadOnlyList<string> args,
        string workingDirectory,
        TimeSpan stallTimeout = default) =>
        FfmpegRunner.RunAsync(
            path, args, workingDirectory, "front-door", "record",
            NullLogger.Instance, sideTasks: [], CancellationToken.None, stallTimeout);

    /// <summary>A few seconds of real mpegts, so ffmpeg has something to probe and then lose.</summary>
    private string MakeClip()
    {
        string clip = Path.Combine(_root, "clip.ts");
        using var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg")
        {
            ArgumentList =
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-t", "3", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=10",
                "-c:v", "libx264", "-preset", "ultrafast", "-g", "10",
                "-f", "mpegts", clip,
            },
            RedirectStandardError = true,
        });

        ffmpeg!.WaitForExit(30_000);
        return clip;
    }

    private string MakeFifo()
    {
        string fifo = Path.Combine(_root, "feed.ts");
        using var mkfifo = Process.Start(new ProcessStartInfo("mkfifo") { ArgumentList = { fifo } });
        mkfifo!.WaitForExit(5000);
        return fifo;
    }

    /// <summary>
    /// The whole point: a source that goes quiet without closing is killed, and the caller is told
    /// why rather than being handed a process that would have hung indefinitely.
    /// </summary>
    [Fact]
    public async Task A_stream_that_stops_without_closing_is_killed_and_reported()
    {
        if (!CanRunPosixTools || !FfmpegIsAvailable)
        {
            Assert.Skip("Needs a POSIX mkfifo and ffmpeg on PATH.");
        }

        byte[] clip = await File.ReadAllBytesAsync(MakeClip(), TestContext.Current.CancellationToken);
        string fifo = MakeFifo();

        // Opened read-write so the open itself does not block waiting for a reader, and so ffmpeg
        // never sees an EOF once the writing stops. This is the half-open socket in miniature.
        await using var holder = new FileStream(fifo, FileMode.Open, FileAccess.ReadWrite);

        // Written in the background: the FIFO buffer is far smaller than the clip, so this only
        // drains as ffmpeg reads. When it finishes, the stream simply stops — the handle stays open.
        CancellationToken token = TestContext.Current.CancellationToken;
        Task feeding = Task.Run(
            async () =>
            {
                await holder.WriteAsync(clip, token);
                await holder.FlushAsync(token);
            },
            token);

        var elapsed = Stopwatch.StartNew();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(
                "ffmpeg",
                [
                    "-nostdin", "-hide_banner", "-loglevel", "error",
                    "-f", "mpegts", "-i", fifo,
                    "-f", "null", "-",
                ],
                _root,
                TimeSpan.FromSeconds(3)));

        elapsed.Stop();
        await feeding;

        // Names the camera and the reason: this is the supervisor's only account of why a session
        // was replaced.
        Assert.Contains("front-door", error.Message);
        Assert.Contains("stopped producing output", error.Message);

        // It gave up rather than waiting on a process that would never have ended by itself.
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(45),
            $"the runner took {elapsed.Elapsed} to give up on a stalled stream");
    }

    /// <summary>
    /// The other half, and the one a watchdog gets wrong expensively: a camera that is working must
    /// be left alone. The timeout here is shorter than the run, so anything that mistook steady
    /// output for silence would kill this.
    /// </summary>
    [Fact]
    public async Task A_healthy_stream_runs_to_completion_untouched()
    {
        if (!FfmpegIsAvailable)
        {
            Assert.Skip("Needs ffmpeg on PATH.");
        }

        await RunAsync(
            "ffmpeg",
            [
                "-nostdin", "-hide_banner", "-loglevel", "error",
                "-re", "-t", "6", "-f", "lavfi", "-i", "testsrc=size=320x240:rate=10",
                "-f", "null", "-",
            ],
            _root,
            TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// Shutdown keeps its own path, and specifically while the watchdog is armed. Both arrive as
    /// cancellation, and only the originating token separates "this camera is broken, replace it"
    /// from "the server is stopping" — which the supervisor reads to decide between reconnecting and
    /// breaking out of its loop.
    /// </summary>
    [Fact]
    public async Task Shutdown_is_still_a_cancellation_rather_than_a_stall()
    {
        if (!FfmpegIsAvailable)
        {
            Assert.Skip("Needs ffmpeg on PATH.");
        }

        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FfmpegRunner.RunAsync(
                "ffmpeg",
                [
                    "-nostdin", "-hide_banner", "-loglevel", "error",
                    "-re", "-t", "120", "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10",
                    "-f", "null", "-",
                ],
                _root, "front-door", "record",
                NullLogger.Instance, sideTasks: [], shutdown.Token, TimeSpan.FromMinutes(5)));
    }

    /// <summary>
    /// An ordinary failure still reports its exit code. The stall check sits in front of that read
    /// and returns early, so this confirms it does not shadow the case it was inserted above.
    /// </summary>
    [Fact]
    public async Task A_process_that_exits_badly_still_reports_its_code()
    {
        if (!CanRunPosixTools)
        {
            Assert.Skip("Needs a POSIX shell.");
        }

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync("sh", ["-c", "exit 3"], _root));

        Assert.Contains("exited with code 3", error.Message);
    }

    /// <summary>
    /// With no timeout the runner asks ffmpeg for no progress stream and leaves stdout alone, which
    /// is what keeps an unwatched process from blocking on a pipe nobody drains.
    /// </summary>
    [Fact]
    public async Task An_unwatched_process_still_runs_and_exits_cleanly()
    {
        if (!CanRunPosixTools)
        {
            Assert.Skip("Needs a POSIX shell.");
        }

        await RunAsync("sh", ["-c", "echo plenty of output on stdout; exit 0"], _root);
    }
}
