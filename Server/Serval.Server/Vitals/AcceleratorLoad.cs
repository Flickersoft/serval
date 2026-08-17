using Serval.Ai;

namespace Serval.Server.Vitals;

/// <summary>
/// Turns two readings of a detector's cumulative device counters into the figures the status page
/// shows.
///
/// <para>Its own type, and pure, for the reason <see cref="GpuSysfs"/>, <see cref="ProcStat"/> and
/// <see cref="CgroupV2"/> are: it can be tested on a fresh clone with no Coral attached, no models
/// fetched and no Mongo running, which <see cref="SystemStatsCollector"/> cannot be. What is left in
/// the collector is holding the previous reading and reading one file per device.</para>
///
/// <para><b>The detector counts; this divides.</b> A rate only exists between two readings, and the
/// detector has no idea when it was last read — so it reports totals and the sampler divides by the
/// interval between its own two samples. Same instrument as <see cref="I915PerfCounters"/> on the
/// meter above, and the reason both are honest about the window they cover.</para>
/// </summary>
public static class AcceleratorLoad
{
    /// <summary>The same sentence the GPU counters use, and for the same reason — the figure is a
    /// difference between two readings, and the first sample has only one.</summary>
    public const string WarmingUpReason =
        "Warming up — a usage figure is the difference between two counter readings.";

    /// <summary>
    /// One window's figures.
    /// </summary>
    /// <param name="devices">This reading, from <c>IObjectDetector.Health</c>.</param>
    /// <param name="previous">The reading before it, keyed by device path. Empty on the first sample.</param>
    /// <param name="elapsedSeconds">Wall time between the two readings. Zero or less means there is
    /// no window, which every figure here reports as null rather than as zero.</param>
    /// <param name="label">What to call these devices, from the detector.</param>
    /// <param name="declinedPerSecond">Frames a second refused because every device was busy, already
    /// differenced by the caller — it is one counter for the whole pool rather than per device.</param>
    /// <param name="link">What each device is attached over, by path. The one impure input, so the
    /// file read stays in the collector.</param>
    public static AcceleratorStats Measure(
        IReadOnlyList<DetectorDevice> devices,
        IReadOnlyDictionary<string, DetectorDevice> previous,
        double elapsedSeconds,
        string? label,
        double? declinedPerSecond,
        Func<string, string?> link)
    {
        var rows = new List<AcceleratorDeviceStats>(devices.Count);

        double busySeconds = 0;
        long inferences = 0;
        bool measured = false;

        foreach (DetectorDevice device in devices)
        {
            // Absent from the previous reading, or counters that have gone backwards — a device only
            // just opened, or a detector rebuilt underneath us. Either way there is no window to
            // measure across, and the honest answer is that this one is not measured yet. A zero
            // would claim an idle accelerator, which is a measurement nobody took.
            if (elapsedSeconds <= 0
                || !previous.TryGetValue(device.Path, out DetectorDevice before)
                || device.Inferences < before.Inferences
                || device.BusySeconds < before.BusySeconds)
            {
                rows.Add(new AcceleratorDeviceStats
                {
                    Name = device.Path,
                    Healthy = device.Healthy,
                    Link = link(device.Path),
                    Failures = device.Failures,
                });
                continue;
            }

            double busy = device.BusySeconds - before.BusySeconds;
            long ran = device.Inferences - before.Inferences;

            busySeconds += busy;
            inferences += ran;
            measured = true;

            rows.Add(new AcceleratorDeviceStats
            {
                Name = device.Path,
                Healthy = device.Healthy,
                Link = link(device.Path),
                BusyPercent = Percent(busy, elapsedSeconds),
                InferencesPerSecond = ran / elapsedSeconds,
                // Null rather than zero over a window this device ran nothing in: no inference
                // happened, so no inference has a duration to report.
                MeanLatencyMs = ran > 0 ? busy / ran * 1000.0 : null,
                Failures = device.Failures,
            });
        }

        return new AcceleratorStats
        {
            Label = label,
            // Against every device's time rather than one device's, because the pool is the resource:
            // a frame goes to whichever device is idle, and the detection budget is their sum. That is
            // the one place this departs from the GPU meter beside it, which reports its busiest
            // engine because engines are not interchangeable.
            BusyPercent = measured ? Percent(busySeconds, elapsedSeconds * devices.Count) : null,
            InferencesPerSecond = measured ? inferences / elapsedSeconds : null,
            DeclinedPerSecond = declinedPerSecond,
            Devices = rows,
            UnavailableReason = measured ? null : WarmingUpReason,
        };
    }

    /// <summary>
    /// Busy time as a percentage of the time it could have filled.
    ///
    /// Clamped, because a window that straddles a missed tick — a garbage collection pause, a
    /// container the host suspended — divides real busy time by an interval shorter than the one it
    /// was actually spent over, and a meter cannot be 103% full.
    /// </summary>
    private static long Percent(double busy, double over) =>
        over > 0 ? (long)Math.Round(Math.Clamp(busy / over * 100.0, 0, 100)) : 0;
}
