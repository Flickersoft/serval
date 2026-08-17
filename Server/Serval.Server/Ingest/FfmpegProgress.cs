using System.Globalization;

namespace Serval.Server.Ingest;

/// <summary>
/// Whether ffmpeg is still moving, read from its own <c>-progress</c> stream.
///
/// <b>The heartbeat alone means nothing.</b> A stalled ffmpeg goes on printing a progress block on
/// schedule — measured against a source cut off mid-stream, it emitted <c>progress=continue</c>
/// every second for fifteen seconds with <c>out_time_us</c> frozen at the same value. A watchdog
/// waiting for the blocks to stop would therefore have waited forever, which is worse than no
/// watchdog at all, because it would look like one. What proves the stream is advancing is
/// <c>out_time</c> going up, and only that.
///
/// This is preferred over watching what ffmpeg writes to disk because it asks the producer directly.
/// A file or directory timestamp is a step removed and drags in the filesystem's own semantics —
/// fine on ext4 or ZFS, but attribute caching on an NFS or FUSE-backed media root can hold a stale
/// timestamp for as long as the stall timeout itself. A pipe has no such behaviour anywhere.
/// </summary>
internal sealed class FfmpegProgress
{
    private readonly Lock _gate = new();
    private long _outTime = -1;
    private DateTimeOffset _lastAdvance;

    /// <param name="startedAt">
    /// The clock's origin, which is the launch rather than the first block: a source that never
    /// produces a frame is as stalled as one that stops later, and is caught by the same timeout.
    /// </param>
    public FfmpegProgress(DateTimeOffset startedAt) => _lastAdvance = startedAt;

    /// <summary>Feeds one line of ffmpeg's progress output. Returns true when it moved the stream on.</summary>
    public bool Observe(string line, DateTimeOffset now)
    {
        if (OutTimeOf(line) is not { } outTime)
        {
            return false;
        }

        lock (_gate)
        {
            if (outTime <= _outTime)
            {
                return false;
            }

            _outTime = outTime;
            _lastAdvance = now;
            return true;
        }
    }

    /// <summary>
    /// How long the stream has not advanced. Never negative, so a clock stepped backwards under the
    /// process cannot report a large positive span and trip the watchdog.
    /// </summary>
    public TimeSpan SilentFor(DateTimeOffset now)
    {
        lock (_gate)
        {
            TimeSpan silent = now - _lastAdvance;
            return silent < TimeSpan.Zero ? TimeSpan.Zero : silent;
        }
    }

    /// <summary>
    /// The output position a progress line carries, or null for the dozen other keys in each block.
    ///
    /// Both spellings are read because the block carries both, and a build that disagrees about
    /// their units cannot cause trouble: only increases count, so the pair is followed as one
    /// monotonic series rather than compared against each other. <c>N/A</c>, which appears before
    /// the first frame, parses as nothing and is simply not progress.
    /// </summary>
    internal static long? OutTimeOf(string line)
    {
        const string Microseconds = "out_time_us=";
        const string Milliseconds = "out_time_ms=";

        string? value =
            line.StartsWith(Microseconds, StringComparison.Ordinal) ? line[Microseconds.Length..]
            : line.StartsWith(Milliseconds, StringComparison.Ordinal) ? line[Milliseconds.Length..]
            : null;

        return value is not null
            && long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : null;
    }
}
