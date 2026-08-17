using Serval.Server.Configuration;

namespace Serval.Server.Vitals;

/// <summary>
/// Turns a sample into the short list of things somebody should be told.
///
/// Three conditions, and each passes the same test: the loss is real, it is invisible, and nothing
/// else in the product surfaces it.
///
///  * <b>Disk low.</b> The one failure here that destroys data silently. Retention is purely
///    age-based, so a full volume triggers no defence at all — ffmpeg's writes start failing per
///    camera inside a retry loop.
///  * <b>Memory near the cgroup limit.</b> The OOM killer gives no warning and leaves no in-app
///    trace; a container that vanished and came back looks like a network blip.
///  * <b>Detection running below full coverage.</b> A host that cannot afford everything detection
///    wanted to examine sheds crops and drops frames rather than stalling — correct behaviour, and
///    indistinguishable from keeping up unless it is said out loud. It prevents somebody comparing
///    this week's events with last week's and concluding the driveway got quieter.
///
/// <b>Not CPU, and not GPU.</b> A pinned GPU during a VAAPI transcode is the encoder working
/// correctly, and neither compose file sets a CPU limit, so there is no ceiling to be near. Both are
/// shown as meters on the settings page, where a number is information; a banner that fires for
/// "working as configured" trains people to stop reading the one that matters.
///
/// Pure, and evaluated here rather than in the App: the thresholds are a property of the deployment,
/// the wording should have exactly one spelling, and a later notification path needs the same
/// judgement with no client running.
/// </summary>
public static class VitalsAlerts
{
    public static IReadOnlyList<VitalsAlert> Evaluate(SystemStats stats, VitalsOptions options)
    {
        var alerts = new List<VitalsAlert>();

        if (DiskAlert(stats.Disk, options) is { } disk)
        {
            alerts.Add(disk);
        }

        if (MemoryAlert(stats.Memory, options) is { } memory)
        {
            alerts.Add(memory);
        }

        if (DetectionAlert(stats.Detection) is { } detection)
        {
            alerts.Add(detection);
        }

        return alerts;
    }

    /// <summary>
    /// Detection examining less than it wanted to.
    ///
    /// <para>The threshold is coverage rather than a rate, because a rate cannot be judged without
    /// knowing what was asked for: ten shed crops a second is nothing on a busy street and a
    /// catastrophe on a still hallway. Ninety-five per cent leaves room for the ordinary case where
    /// a burst of movement briefly outruns the budget and the next second catches up, which is the
    /// scheduler working rather than failing.</para>
    ///
    /// <para>Coverage counts only what the budget refused. Frames dropped further upstream are
    /// reported as their own figure and deliberately do not raise this: at a low frame rate the
    /// handoff discards superseded frames as a matter of course, and alerting on that would fire
    /// constantly on a perfectly healthy server.</para>
    ///
    /// <para>An unmeasured or idle detector raises nothing, for the same reason an unmeasured disk
    /// does: there is no safe direction to guess in, and a banner that fires without evidence is
    /// what teaches people to ignore the one that matters.</para>
    /// </summary>
    private static VitalsAlert? DetectionAlert(DetectionStats detection)
    {
        // A lost lane is checked before coverage, because coverage will not report it. Capacity halving
        // makes the scheduler admit more than the host can do; the excess becomes *dropped frames*, and
        // coverage deliberately counts only what the budget refused. So a device that fell off the bus
        // is invisible here unless it is asked about directly.
        if (detection is { Lanes: { } lanes, HealthyLanes: { } healthy } && healthy < lanes)
        {
            return new VitalsAlert
            {
                Kind = VitalsAlertKinds.DetectionDegraded,
                Severity = healthy == 0 ? "critical" : "warning",
                Message = healthy == 0
                    ? "Object detection has lost every accelerator it was using "
                        + $"({detection.DetectorDegraded ?? "no reason reported"}). Nothing is being "
                        + "examined. Check that the device is still attached — `lsusb` on the host, and "
                        + "`dmesg` for USB resets — then check the cable and the port. Docs/coral.md has "
                        + "the failure modes."
                    : $"Object detection is running on {healthy} of {lanes} accelerators "
                        + $"({detection.DetectorDegraded ?? "no reason reported"}). Throughput has "
                        + "dropped accordingly and the budget has been rescaled, so detections are being "
                        + "shed rather than missed silently. Check the cable, the port and `dmesg` for "
                        + "USB resets; Docs/coral.md has the failure modes.",
            };
        }

        if (detection.Coverage is not { } coverage || coverage >= 0.95)
        {
            return null;
        }

        // The remedy depends on what is running: telling an operator whose Corals are all healthy
        // to "give it an accelerator" is worse than saying nothing.
        bool accelerated = detection.Backend?.StartsWith("edgetpu", StringComparison.Ordinal) == true;

        string remedy = accelerated
            ? "The accelerators are keeping up with what they are given, so this is a shortage of them "
                + "rather than a fault: lower the detection frame rate, reduce the crops per frame, or "
                + "add a device."
            : "Lower the detection frame rate, reduce the crops per frame, or give it a smaller model "
                + "or an accelerator.";

        return new VitalsAlert
        {
            Kind = VitalsAlertKinds.DetectionDegraded,
            Severity = coverage < 0.75 ? "critical" : "warning",
            Message =
                $"Object detection is examining {coverage:P0} of what it wanted to. This host "
                + "cannot keep up with the current frame rate and camera count, so some movement is "
                + $"going unexamined — detections will be missed. {remedy}",
        };
    }

    /// <summary>
    /// Percentage of the volume free, not an absolute byte count: a 4 TB pool and a 40 TB one both
    /// exist and no single number of gigabytes is right for both.
    ///
    /// A volume that could not be measured raises nothing. An unmeasured disk reporting "disk low"
    /// is precisely the failure that teaches people to ignore the banner, and there is no safe
    /// direction to guess in.
    /// </summary>
    private static VitalsAlert? DiskAlert(DiskStats disk, VitalsOptions options)
    {
        if (disk.FreeBytes is not { } free || disk.TotalBytes is not { } total || total <= 0)
        {
            return null;
        }

        double percentFree = 100.0 * free / total;
        string where = disk.MountPoint ?? "the media volume";

        if (percentFree < options.DiskCriticalPercentFree)
        {
            return new VitalsAlert
            {
                Kind = VitalsAlertKinds.DiskCritical,
                Severity = "critical",
                Message = $"Under {Format(options.DiskCriticalPercentFree)}% free on {where}. "
                    + "Recording will start failing — footage is about to be lost.",
            };
        }

        if (percentFree < options.DiskWarnPercentFree)
        {
            return new VitalsAlert
            {
                Kind = VitalsAlertKinds.DiskLow,
                Severity = "warning",
                Message = $"Under {Format(options.DiskWarnPercentFree)}% free on {where}. "
                    + "Nothing deletes footage early to make room, so this only goes one way.",
            };
        }

        return null;
    }

    /// <summary>
    /// Suppressed entirely when no limit is set. "90% of unlimited" is not a condition, and the
    /// server is perfectly entitled to use most of a machine that has not been told to stop it.
    /// </summary>
    private static VitalsAlert? MemoryAlert(MemoryStats memory, VitalsOptions options)
    {
        if (memory.LimitBytes is not { } limit || limit <= 0 || memory.UsedBytes is not { } used)
        {
            return null;
        }

        double percent = 100.0 * used / limit;
        if (percent < options.MemoryWarnPercent)
        {
            return null;
        }

        return new VitalsAlert
        {
            Kind = VitalsAlertKinds.MemoryHigh,
            Severity = "warning",
            Message = $"Serval is using {Format(percent)}% of the memory it is allowed. "
                + "Past the limit the container is killed without warning — raise mem_limit or turn off a model.",
        };
    }

    /// <summary>Whole numbers stay whole: "10%", not "10.0%".</summary>
    private static string Format(double value) =>
        value == Math.Floor(value)
            ? ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
