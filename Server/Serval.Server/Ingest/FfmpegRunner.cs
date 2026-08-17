using System.Diagnostics;

namespace Serval.Server.Ingest;

/// <summary>
/// Runs one ffmpeg to completion: start it, log everything it says, run the caller's harvesting
/// loops alongside it, watch that it is still producing, and make sure the process tree dies with us.
///
/// Shared by the recording session and the standalone snapshot session, which differ only in
/// their arguments and what they harvest. Like both of them it has no retry — <see cref="RunAsync"/>
/// returns when ffmpeg exits, stalls, or the token cancels, and restart with backoff belongs to
/// <see cref="StreamIngestManager"/>.
/// </summary>
internal static class FfmpegRunner
{
    /// <param name="sideTasks">
    /// Loops that harvest ffmpeg's output while it runs (reading snapshots, indexing segments).
    /// They are cancelled and awaited when it exits, so they never outlive the process.
    /// </param>
    /// <param name="stallTimeout">
    /// How long the stream may fail to advance before ffmpeg is killed and the caller told it
    /// stalled. Zero or less runs without a watchdog, and without asking ffmpeg for progress at all.
    ///
    /// <para>This exists because a dead camera does not reliably kill ffmpeg. A half-open TCP socket
    /// leaves it blocked in a read that never completes: alive, holding the session open, and
    /// invisible to everything keyed on process exit. Watching progress is the only thing that
    /// separates that from a working camera.</para>
    /// </param>
    public static async Task RunAsync(
        string ffmpegPath,
        IReadOnlyList<string> args,
        string workingDirectory,
        string cameraId,
        string description,
        ILogger logger,
        IReadOnlyList<Func<CancellationToken, Task>> sideTasks,
        CancellationToken cancellationToken,
        TimeSpan stallTimeout = default)
    {
        bool watching = stallTimeout > TimeSpan.Zero;

        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            // ffmpeg logs to stderr; all media goes to files. stdout carries the progress stream
            // when we asked for one and is otherwise left un-redirected, because a
            // redirected-but-undrained pipe could block the process.
            RedirectStandardError = true,
            RedirectStandardOutput = watching,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory, // so bare output names land here and manifests stay relative
        };

        // A global option, so it goes ahead of everything the caller built. Deliberately without
        // -stats_period: the default half-second cadence costs a dozen short lines to skim, while
        // that option did not exist before ffmpeg 4.4 and passing it to an older build would fail
        // every camera on the server at once.
        if (watching)
        {
            startInfo.ArgumentList.Add("-progress");
            startInfo.ArgumentList.Add("pipe:1");
        }

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start ffmpeg for camera '{cameraId}'.");
        }

        logger.LogInformation(
            "ffmpeg started for camera {CameraId} (pid {Pid}, {Description}).",
            cameraId, process.Id, description);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stall = new CancellationTokenSource();
        using var exitWait = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, stall.Token);

        var running = new List<Task> { PumpStderrAsync(process, cameraId, logger, linked.Token) };
        running.AddRange(sideTasks.Select(t => t(linked.Token)));

        if (watching)
        {
            var progress = new FfmpegProgress(DateTimeOffset.UtcNow);
            running.Add(PumpProgressAsync(process, progress, linked.Token));
            running.Add(WatchForStallAsync(
                progress, stallTimeout, stall, cameraId, logger, linked.Token));
        }

        bool stalled = false;
        try
        {
            await process.WaitForExitAsync(exitWait.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The watchdog tripped rather than the server shutting down, so ffmpeg is still running
            // and the teardown below is what ends it. Shutdown keeps its old path: the filter leaves
            // that cancellation to propagate, and a race between the two settles as shutdown.
            stalled = true;
        }
        finally
        {
            // ffmpeg exited, stalled, or we were cancelled — stop the helper loops and the process
            // tree. Kill is a SIGKILL on Linux, which is what a wedged ffmpeg needs: it never
            // reaches the handler a polite signal would rely on.
            linked.Cancel();
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }

            await Task.WhenAll(running.Select(Swallow));
        }

        // Ahead of the exit code, which a killed process may not have been reaped for yet — and
        // which would report the kill rather than the reason for it in any case.
        if (stalled)
        {
            throw new InvalidOperationException(
                $"ffmpeg for camera '{cameraId}' stopped producing output and was killed.");
        }

        if (process.ExitCode != 0 && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"ffmpeg for camera '{cameraId}' exited with code {process.ExitCode}.");
        }
    }

    /// <summary>
    /// Ends the run when the stream stops advancing.
    ///
    /// Polled rather than driven off the progress lines themselves, because the failure being
    /// watched for is <em>lines that keep arriving and say nothing new</em> — see
    /// <see cref="FfmpegProgress"/>. Several ticks per timeout, so the timeout decides how long a
    /// stall lasts rather than the tick that notices it.
    /// </summary>
    private static async Task WatchForStallAsync(
        FfmpegProgress progress,
        TimeSpan timeout,
        CancellationTokenSource stall,
        string cameraId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        TimeSpan period = TimeSpan.FromSeconds(
            Math.Clamp(timeout.TotalSeconds / 4, 1, 15));

        using var timer = new PeriodicTimer(period);

        while (await TaskWaits.SafeWaitAsync(timer, cancellationToken))
        {
            TimeSpan silent = progress.SilentFor(DateTimeOffset.UtcNow);
            if (silent < timeout)
            {
                continue;
            }

            logger.LogError(
                "Camera {CameraId}: the stream has not advanced for {Silent:g} — ffmpeg is running "
                + "but producing nothing. Killing it so the session reconnects.",
                cameraId, silent);

            await stall.CancelAsync();
            return;
        }
    }

    private static async Task PumpProgressAsync(
        Process process, FfmpegProgress progress, CancellationToken cancellationToken)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                progress.Observe(line, DateTimeOffset.UtcNow);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task PumpStderrAsync(
        Process process, string cameraId, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                // ffmpeg says everything on stderr; at loglevel warning, anything here is worth seeing.
                logger.LogWarning("[ffmpeg {CameraId}] {Line}", cameraId, line);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task Swallow(Task task)
    {
        try { await task; } catch { /* helper loops end noisily on cancel; not interesting */ }
    }
}
