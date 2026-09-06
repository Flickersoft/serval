using System.Diagnostics;

namespace Serval.Server;

/// <summary>
/// The teardown every process this server starts needs, in one place.
///
/// In the root namespace because the callers are spread across ingest, clip export and the AI
/// audio tap, and none of them should have to reference another's namespace to end a process it
/// started.
/// </summary>
internal static class ChildProcess
{
    /// <summary>
    /// Ends a process and everything it started, if it is still running.
    ///
    /// This exists because <see cref="Process.Dispose"/> does not do it: disposing releases the
    /// handle and leaves the child running, so a helper abandoned on a timeout survives the code
    /// that was waiting for it and is never reaped. On a retry loop that is one leaked process per
    /// attempt for as long as the source stays wedged.
    ///
    /// <see cref="Process.Kill(bool)"/> is a SIGKILL on Linux, which is what a wedged ffmpeg needs:
    /// it never reaches the handler a polite signal would rely on. The whole tree, because ffmpeg
    /// spawns its own workers and killing only the parent orphans them onto init.
    /// </summary>
    /// <param name="logger">
    /// Where a kill that genuinely failed is reported — a process this server started that is still
    /// out there is worth a line. Optional only for the startup capability probe, which runs before
    /// the host is built and has no logger to hand.
    /// </param>
    public static void Kill(Process process, ILogger? logger = null)
    {
        // HasExited is inside the try: it throws for a process that never started and for one
        // already disposed, which are two more ways of being gone.
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception) when (HasGone(process))
        {
            // The ordinary case, and the reason this is a filter rather than a bare catch: the
            // process ended on its own between the check and the signal. Asking again settles it
            // without naming a kernel error code, which is the part that differs by platform.
            // Anything the process outlives falls through to the warning below.
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex,
                "Could not end process {Pid}; it may still be running. Serval will not try again.",
                PidOf(process));
        }
    }

    /// <summary>
    /// Whether there is no longer a running process here — exited, never started, or disposed.
    /// Every one of those means the kill has nothing left to do.
    /// </summary>
    private static bool HasGone(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // No process is associated with this object: it was never started, or it has been
            // disposed — ObjectDisposedException derives from this one, so both land here.
            return true;
        }
    }

    /// <summary>The pid for a log line, or null when the object cannot supply one.</summary>
    private static int? PidOf(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// How long a short-lived helper reading local media — a frame, a duration, a stream layout —
    /// is given before it is abandoned and killed.
    ///
    /// Generous, because it bounds a failure rather than describing normal work: these finish in
    /// well under a second on a healthy disk, and the value only matters when the media is on a
    /// mount that has stopped answering. Cancelling the caller stays the faster path.
    /// </summary>
    public static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(60);
}
