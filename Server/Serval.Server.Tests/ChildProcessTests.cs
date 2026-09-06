using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Serval.Server;

namespace Serval.Server.Tests;

/// <summary>
/// The teardown invariant that went missing at four of nine call sites, tested once.
///
/// The cost of it being absent was measured rather than imagined: a camera that stopped answering
/// without closing its connection left one abandoned ffprobe per attempt, and a single incident on
/// a seven-camera server accumulated thirty of them holding 1.6 GB. Every one was a process the
/// code that started it believed it had disposed.
/// </summary>
public class ChildProcessTests
{
    private static bool CanRunPosixTools =>
        OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private static Process Start(string script) =>
        Process.Start(new ProcessStartInfo("sh")
        {
            ArgumentList = { "-c", script },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;

    /// <summary>
    /// The property <c>entireProcessTree</c> buys. ffmpeg spawns workers of its own, so killing
    /// only the process this server started would leave them reparented onto init and running.
    /// </summary>
    [Fact]
    public async Task Killing_a_process_takes_the_children_it_started_with_it()
    {
        if (!CanRunPosixTools)
        {
            Assert.Skip("Needs a POSIX shell.");
        }

        // Prints the grandchild's pid, then keeps the parent alive so the tree really is a tree.
        using Process parent = Start("sleep 300 & echo $!; wait");
        string firstLine =
            await parent.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken) ?? "";
        Assert.True(int.TryParse(firstLine.Trim(), out int grandchild), $"got '{firstLine}'");

        ChildProcess.Kill(parent);
        await parent.WaitForExitAsync(TestContext.Current.CancellationToken);

        // The kill is asynchronous in the kernel; give the reap a moment rather than racing it.
        bool gone = false;
        for (int i = 0; i < 50 && !gone; i++)
        {
            gone = !ProcessIsAlive(grandchild);
            if (!gone)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }

        Assert.True(gone, $"pid {grandchild} outlived the kill.");
    }

    [Fact]
    public async Task A_process_that_would_never_exit_is_gone_after_the_kill()
    {
        if (!CanRunPosixTools)
        {
            Assert.Skip("Needs a POSIX shell.");
        }

        using Process process = Start("sleep 300");
        Assert.False(process.HasExited);

        ChildProcess.Kill(process);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(process.HasExited);
    }

    /// <summary>
    /// The race every one of the nine call sites swallows: the process ended between the check and
    /// the kill, or never started at all. Both mean there is nothing to do, not something to report.
    /// </summary>
    [Fact]
    public async Task Killing_a_process_that_has_already_exited_is_not_an_error()
    {
        if (!CanRunPosixTools)
        {
            Assert.Skip("Needs a POSIX shell.");
        }

        using Process process = Start("exit 0");
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        ChildProcess.Kill(process);
        ChildProcess.Kill(process);
    }

    [Fact]
    public void Killing_a_process_that_never_started_is_not_an_error()
    {
        using var process = new Process();
        var logger = new CountingLogger();

        ChildProcess.Kill(process, logger);

        // Nothing was started, so nothing failed: this must not look like a process left running.
        Assert.Equal(0, logger.Warnings);
    }

    /// <summary>
    /// The half of the filter that can be forced safely. Warning on the ordinary race would put a
    /// line in the log every time a helper finished normally, which is the failure mode that makes
    /// people stop reading warnings.
    ///
    /// <para>The other half — a kill that fails with the process still running — has no safe
    /// trigger here: the reliable way to earn a permission failure is to signal a process this user
    /// does not own, and the one that is always present is pid 1.</para>
    /// </summary>
    [Fact]
    public async Task A_process_that_ended_on_its_own_is_not_reported_as_a_failure()
    {
        if (!CanRunPosixTools)
        {
            Assert.Skip("Needs a POSIX shell.");
        }

        using Process process = Start("exit 0");
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var logger = new CountingLogger();
        ChildProcess.Kill(process, logger);

        Assert.Equal(0, logger.Warnings);
    }

    private sealed class CountingLogger : ILogger
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

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
                Warnings++;
            }
        }
    }

    private static bool ProcessIsAlive(int pid)
    {
        try
        {
            using Process found = Process.GetProcessById(pid);

            // A killed child whose parent has not reaped it stays in the table as a zombie, which
            // is exited for every purpose this asserts.
            return !found.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
