using System.Globalization;

namespace Serval.Server.Vitals;

/// <summary>
/// The container's own resource accounting, read from cgroup v2.
///
/// This is the right source rather than <c>Process.GetCurrentProcess()</c> because the server runs
/// one ffmpeg per camera as a child process — on a busy NVR those children are most of the CPU,
/// and the .NET process counter cannot see any of them. <c>cpu.stat</c> covers every process in
/// the container, which is exactly the number a person means by "what is Serval using".
///
/// There is deliberately no cgroup v1 fallback. Both deployments are Docker on cgroup v2 unified,
/// and a second code path nobody runs is a second code path nobody tests; a missing file reports
/// itself as unavailable instead, which the App already has to render for other reasons.
///
/// Every method here is pure — it takes the file's text, not its path — so the parsing and the
/// percentage arithmetic are pinned by unit tests with no filesystem involved.
/// </summary>
public static class CgroupV2
{
    public const string CpuStatPath = "/sys/fs/cgroup/cpu.stat";
    public const string CpuMaxPath = "/sys/fs/cgroup/cpu.max";
    public const string MemoryCurrentPath = "/sys/fs/cgroup/memory.current";
    public const string MemoryMaxPath = "/sys/fs/cgroup/memory.max";
    public const string MemoryStatPath = "/sys/fs/cgroup/memory.stat";

    /// <summary>
    /// The <c>usage_usec</c> line out of a <c>cpu.stat</c> body — cumulative CPU microseconds
    /// consumed by everything in this cgroup since it was created. Null when the key is absent.
    /// </summary>
    public static long? ParseUsageUsec(string? cpuStat)
    {
        if (string.IsNullOrWhiteSpace(cpuStat))
        {
            return null;
        }

        foreach (string line in cpuStat.Split('\n'))
        {
            ReadOnlySpan<char> span = line.AsSpan().Trim();
            if (!span.StartsWith("usage_usec", StringComparison.Ordinal))
            {
                continue;
            }

            ReadOnlySpan<char> value = span["usage_usec".Length..].Trim();
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long usec))
            {
                return usec;
            }
        }

        return null;
    }

    /// <summary>
    /// The CPU ceiling from <c>cpu.max</c>, in cores. <c>"200000 100000"</c> is 2 cores;
    /// <c>"max 100000"</c> is unlimited and returns null.
    ///
    /// Null is the *production* answer: no shipped compose file (<c>deploy/docker-compose.yml</c>
    /// or the <c>deploy/examples/</c> files) sets a CPU limit, only a memory one. A missing file —
    /// which is what a bare host's cgroup root looks like — means the same thing and is handled by
    /// the caller passing null.
    /// </summary>
    public static double? ParseQuotaCores(string? cpuMax)
    {
        if (string.IsNullOrWhiteSpace(cpuMax))
        {
            return null;
        }

        string[] parts = cpuMax.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long quota)
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long period)
            || quota <= 0
            || period <= 0)
        {
            return null;
        }

        return (double)quota / period;
    }

    /// <summary>
    /// A single-value cgroup byte file — <c>memory.current</c> or <c>memory.max</c>. The literal
    /// <c>"max"</c> means no limit and returns null, which is the same "there is no ceiling to be
    /// near" the alert evaluation needs.
    /// </summary>
    public static long? ParseBytes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes)
            ? bytes
            : null;
    }

    /// <summary>
    /// A named byte counter out of a <c>memory.stat</c> body. Null when the key is absent.
    /// </summary>
    public static long? ParseMemoryStat(string? memoryStat, string key)
    {
        if (string.IsNullOrWhiteSpace(memoryStat) || string.IsNullOrEmpty(key))
        {
            return null;
        }

        foreach (string line in memoryStat.Split('\n'))
        {
            ReadOnlySpan<char> span = line.AsSpan().Trim();
            if (!span.StartsWith(key, StringComparison.Ordinal)
                || span.Length <= key.Length
                || span[key.Length] != ' ')
            {
                // The length and space checks are what stop "file" matching "file_mapped" — every
                // one of those prefixes is a real key in this file, several times over.
                continue;
            }

            if (long.TryParse(
                span[(key.Length + 1)..].Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long bytes))
            {
                return bytes;
            }
        }

        return null;
    }

    /// <summary>
    /// What the container is actually holding: <c>memory.current</c> less the page cache the kernel
    /// can hand back on demand.
    ///
    /// <c>memory.current</c> alone is not that number, and the difference is the whole reason this
    /// exists. A cgroup is charged for the page cache of every file written inside it, and an NVR
    /// writes recordings continuously — so the kernel grows that cache into whatever the limit
    /// leaves spare and then recycles it, parking the cgroup at exactly 100% of its limit forever.
    /// That is healthy behaviour reported as an emergency: memory pressure and a memory *shortage*
    /// look identical from <c>memory.current</c>, and only one of them can OOM-kill the container.
    ///
    /// Deliberately not filesystem-dependent, though only one of the two deployments ever showed it.
    /// ZFS caches in the ARC rather than the page cache, so a TrueNAS host charges its cgroup almost
    /// nothing for the same writes (measured: 4 MiB of <c>inactive_file</c> against 4 GiB on ext4).
    /// Reading <c>memory.current</c> was therefore right on one box and wrong on the other, which is
    /// the least useful place for a bug to be.
    ///
    /// <c>inactive_file</c> is the reclaim-first list, and subtracting it is what <c>docker stats</c>
    /// and cAdvisor both call the working set. Active file pages are left in deliberately: the
    /// kernel is still using them, and a figure that ignored them would understate a genuine squeeze.
    /// Null <paramref name="inactiveFile"/> — a kernel without the key — returns
    /// <paramref name="current"/> unchanged rather than guessing.
    /// </summary>
    public static long WorkingSet(long current, long? inactiveFile) =>
        inactiveFile is { } cache && cache > 0 && cache <= current ? current - cache : current;

    /// <summary>
    /// CPU used between two samples, as a percentage of <paramref name="cores"/>.
    ///
    /// Null rather than a number in the three cases where the arithmetic would be a lie: samples
    /// out of order or identical in wall time (a division by zero), and a counter that went
    /// backwards, which means the container restarted between samples and the delta is meaningless
    /// rather than negative.
    ///
    /// Clamped to [0, 100]. The usage counter and the wall clock are read a few microseconds
    /// apart, so a fully-loaded box can otherwise produce 100.3% — which is arithmetically
    /// explicable and still reads as a bug to everybody who sees it.
    /// </summary>
    public static double? Percent(CpuSample previous, CpuSample current, double cores)
    {
        if (cores <= 0)
        {
            return null;
        }

        long elapsedUsec = current.WallClockUsec - previous.WallClockUsec;
        long usedUsec = current.UsageUsec - previous.UsageUsec;

        if (elapsedUsec <= 0 || usedUsec < 0)
        {
            return null;
        }

        double percent = 100.0 * usedUsec / (elapsedUsec * cores);
        return Math.Clamp(percent, 0.0, 100.0);
    }
}

/// <summary>One reading of the cgroup CPU counter, paired with the wall clock it was taken at.</summary>
public readonly record struct CpuSample(long UsageUsec, long WallClockUsec);
