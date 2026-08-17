using System.Globalization;

namespace Serval.Server.Vitals;

/// <summary>
/// The whole machine's CPU, from procfs.
///
/// Worth having alongside the cgroup figure because procfs is *not* namespaced for these files:
/// inside the container <c>/proc/stat</c> and <c>/proc/loadavg</c> describe the host, Mongo and
/// go2rtc included. So the two numbers answer genuinely different questions — "what is Serval
/// using" versus "how busy is the box" — and on a NAS running other apps beside this one, the
/// second is often the one that explains what somebody is seeing.
///
/// Pure, like <see cref="CgroupV2"/>, and for the same reason.
/// </summary>
public static class ProcStat
{
    public const string StatPath = "/proc/stat";
    public const string LoadAveragePath = "/proc/loadavg";

    /// <summary>
    /// The aggregate <c>cpu </c> line — the first one, which sums every core — as jiffies spent
    /// idle and jiffies in total. The per-core <c>cpu0</c>, <c>cpu1</c>… lines that follow are
    /// deliberately not summed: that would double-count the aggregate that is already there.
    ///
    /// Field count varies by kernel age (7 fields on old ones, 10 or 11 on current), so every
    /// numeric field present is summed rather than a fixed number being indexed. Idle is
    /// <c>idle + iowait</c>: a core blocked on a disk read is not doing work, and counting iowait
    /// as busy makes a recording NVR look permanently pinned.
    /// </summary>
    public static HostCpuSample? ParseAggregate(string? procStat)
    {
        if (string.IsNullOrWhiteSpace(procStat))
        {
            return null;
        }

        foreach (string line in procStat.Split('\n'))
        {
            string[] parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // "cpu" exactly — "cpu0" is a per-core line and is already inside this total.
            if (parts.Length < 5 || parts[0] != "cpu")
            {
                continue;
            }

            long total = 0;
            long idle = 0;

            for (int i = 1; i < parts.Length; i++)
            {
                if (!long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out long field))
                {
                    return null;
                }

                total += field;

                // Fields 4 and 5 (1-based after the label) are idle and iowait.
                if (i is 4 or 5)
                {
                    idle += field;
                }
            }

            return new HostCpuSample(idle, total);
        }

        return null;
    }

    /// <summary>
    /// Busy percentage between two <c>/proc/stat</c> readings. Null when no time passed, or when
    /// the counters moved backwards — the same restart case <see cref="CgroupV2.Percent"/> guards.
    /// </summary>
    public static double? Percent(HostCpuSample previous, HostCpuSample current)
    {
        long totalDelta = current.Total - previous.Total;
        long idleDelta = current.Idle - previous.Idle;

        if (totalDelta <= 0 || idleDelta < 0)
        {
            return null;
        }

        double percent = 100.0 * (totalDelta - idleDelta) / totalDelta;
        return Math.Clamp(percent, 0.0, 100.0);
    }

    /// <summary>
    /// The one, five and fifteen minute load averages out of <c>/proc/loadavg</c>, whose remaining
    /// fields (running/total processes, last pid) are of no interest here.
    ///
    /// Parsed under the invariant culture explicitly. A container whose locale uses a comma decimal
    /// separator would otherwise read <c>3.40</c> as <c>340</c>, and a load average of 340 on an
    /// 8-core box is the kind of wrong that looks like a real emergency.
    /// </summary>
    public static (double One, double Five, double Fifteen)? ParseLoadAverage(string? loadavg)
    {
        if (string.IsNullOrWhiteSpace(loadavg))
        {
            return null;
        }

        string[] parts = loadavg.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double one)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double five)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double fifteen))
        {
            return null;
        }

        return (one, five, fifteen);
    }
}

/// <summary>One reading of the host's aggregate CPU counters, in jiffies.</summary>
public readonly record struct HostCpuSample(long Idle, long Total);
