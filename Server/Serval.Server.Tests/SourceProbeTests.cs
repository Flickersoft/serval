using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Serval.Server.Ingest;

namespace Serval.Server.Tests;

/// <summary>
/// The leak, reproduced without a camera.
///
/// A probe abandoned on its deadline used to leave the process running: the timeout cancelled the
/// await, and disposing a <see cref="Process"/> releases the handle without ending the child. On a
/// supervisor that retries a wedged source that is one orphan per attempt, each holding its
/// connection open — thirty of them, and 1.6 GB, from a single incident on a real server.
///
/// <para>Slow, and it earns it the way <see cref="IngestStallWatchdogTests"/> does: a process that
/// really hangs is the only way to tell a bounded wait from an unbounded one, and the failure it
/// guards is silent.</para>
/// </summary>
public class SourceProbeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"serval-probe-{Guid.NewGuid():N}");

    public SourceProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static bool CanRunPosixTools =>
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>
    /// A stand-in for the camera that fails both ways the real one did at once: it ignores every
    /// argument it is handed, so it never exits on its own, and it floods stdout, so a probe that
    /// drained neither pipe would block here too.
    /// </summary>
    private string WedgedProbe()
    {
        string path = Path.Combine(_root, "wedged-ffprobe");
        File.WriteAllText(path, "#!/bin/sh\nwhile :; do echo y; done\n");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return path;
    }

    private static int LiveChildren(string path) =>
        Process.GetProcesses().Count(p =>
        {
            try
            {
                return p.ProcessName is "sh" or "wedged-ffprobe" && !p.HasExited;
            }
            catch
            {
                return false;
            }
        });

    [Fact]
    public async Task A_source_that_never_answers_is_given_up_on_and_leaves_nothing_running()
    {
        if (!CanRunPosixTools)
        {
            Assert.Skip("Needs a POSIX shell.");
        }

        string ffprobe = WedgedProbe();
        int before = LiveChildren(ffprobe);
        var elapsed = Stopwatch.StartNew();

        VideoProbe probe = await SourceProbe.VideoAsync(
            "rtsp://camera.invalid/stream",
            ffprobe,
            "cam-1",
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        elapsed.Stop();

        // A source that says nothing is a null answer, not an exception: the caller decides whether
        // it can build a session without one.
        Assert.Null(probe.Codec);
        Assert.Null(probe.Width);
        Assert.Null(probe.Height);

        // Bounded by the probe's own deadline, with nothing else cancelling it. Compared generously
        // against the deadline rather than to it, so this measures that a bound exists rather than
        // what it is set to.
        Assert.True(
            elapsed.Elapsed < SourceArguments.ProbeTimeout + TimeSpan.FromSeconds(30),
            $"took {elapsed.Elapsed.TotalSeconds:0.0}s, so nothing bounded it.");

        // The kill happens inside the probe; the kernel reaps asynchronously.
        int after = before;
        for (int i = 0; i < 50; i++)
        {
            after = LiveChildren(ffprobe);
            if (after <= before)
            {
                break;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.True(after <= before, $"left a probe behind: {before} before, {after} after.");
    }

    [Fact]
    public async Task A_probe_that_cannot_start_answers_null_rather_than_throwing()
    {
        VideoProbe probe = await SourceProbe.VideoAsync(
            "rtsp://camera.invalid/stream",
            Path.Combine(_root, "no-such-ffprobe"),
            "cam-1",
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.Null(probe.Codec);
    }
}
