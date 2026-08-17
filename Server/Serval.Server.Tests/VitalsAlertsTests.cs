using Serval.Server.Configuration;
using Serval.Server.Vitals;

namespace Serval.Server.Tests;

/// <summary>
/// What the server decides is worth telling somebody about.
///
/// The negative cases below carry as much weight as the positive ones. An alert that fires when it
/// should not is not a smaller bug than one that fails to fire — it is the mechanism by which the
/// banner stops being read, and then the one that matters goes unnoticed too.
/// </summary>
public class VitalsAlertsTests
{
    private static readonly VitalsOptions Defaults = new();

    private static SystemStats WithDisk(long? free, long? total) => new()
    {
        Disk = new DiskStats { MountPoint = "/media", FreeBytes = free, TotalBytes = total },
    };

    private static SystemStats WithMemory(long? used, long? limit) => new()
    {
        Memory = new MemoryStats { UsedBytes = used, LimitBytes = limit },
    };

    [Fact]
    public void Four_percent_free_is_critical_and_not_also_a_warning()
    {
        IReadOnlyList<VitalsAlert> alerts = VitalsAlerts.Evaluate(WithDisk(4, 100), Defaults);

        VitalsAlert alert = Assert.Single(alerts);
        Assert.Equal(VitalsAlertKinds.DiskCritical, alert.Kind);
        Assert.Equal("critical", alert.Severity);
        Assert.Contains("/media", alert.Message);
    }

    [Fact]
    public void Eight_percent_free_is_a_warning()
    {
        VitalsAlert alert = Assert.Single(VitalsAlerts.Evaluate(WithDisk(8, 100), Defaults));

        Assert.Equal(VitalsAlertKinds.DiskLow, alert.Kind);
        Assert.Equal("warning", alert.Severity);
    }

    /// <summary>The boundary is "below", stated once here so it cannot drift.</summary>
    [Fact]
    public void Exactly_at_the_threshold_is_not_an_alert()
    {
        Assert.Empty(VitalsAlerts.Evaluate(WithDisk(10, 100), Defaults));
        Assert.Empty(VitalsAlerts.Evaluate(WithDisk(50, 100), Defaults));
    }

    /// <summary>
    /// An unmeasured disk must never say "disk low". There is no safe direction to guess in, and a
    /// warning raised on a missing measurement is precisely what teaches people to ignore the
    /// banner.
    /// </summary>
    [Fact]
    public void A_disk_that_could_not_be_measured_raises_nothing()
    {
        Assert.Empty(VitalsAlerts.Evaluate(WithDisk(null, 100), Defaults));
        Assert.Empty(VitalsAlerts.Evaluate(WithDisk(4, null), Defaults));
        Assert.Empty(VitalsAlerts.Evaluate(WithDisk(4, 0), Defaults));
        Assert.Empty(VitalsAlerts.Evaluate(new SystemStats(), Defaults));
    }

    [Fact]
    public void Memory_near_its_limit_is_a_warning()
    {
        VitalsAlert alert = Assert.Single(VitalsAlerts.Evaluate(WithMemory(94, 100), Defaults));

        Assert.Equal(VitalsAlertKinds.MemoryHigh, alert.Kind);
        Assert.Equal("warning", alert.Severity);
    }

    [Fact]
    public void Memory_well_under_its_limit_is_nothing()
    {
        Assert.Empty(VitalsAlerts.Evaluate(WithMemory(2_617_245_696, 8_589_934_592), Defaults));
    }

    /// <summary>"90% of unlimited" is not a condition, and a server told to use the whole machine
    /// is entitled to.</summary>
    [Fact]
    public void Memory_with_no_limit_set_raises_nothing_however_high_it_goes()
    {
        Assert.Empty(VitalsAlerts.Evaluate(WithMemory(64_000_000_000, null), Defaults));
        Assert.Empty(VitalsAlerts.Evaluate(WithMemory(null, 8_589_934_592), Defaults));
    }

    /// <summary>
    /// Pinned deliberately so a future edit has to argue with it. A pinned GPU during a VAAPI
    /// transcode is the encoder doing its job, and neither compose file sets a CPU limit, so there
    /// is no ceiling for CPU to be near. Both are meters on the settings page; neither is an alert.
    /// </summary>
    [Fact]
    public void A_completely_pinned_processor_and_gpu_raise_no_alerts_at_all()
    {
        var stats = new SystemStats
        {
            Cpu = new CpuStats { ContainerPercent = 100, HostPercent = 100, Cores = 8, LoadAverage = [40, 39, 38] },
            Gpu = new GpuStats { BusyPercent = 100, Driver = "amdgpu", HostWide = true },
            Disk = new DiskStats { MountPoint = "/media", FreeBytes = 900, TotalBytes = 1000 },
            Memory = new MemoryStats { UsedBytes = 1, LimitBytes = 100 },
        };

        Assert.Empty(VitalsAlerts.Evaluate(stats, Defaults));
    }

    [Fact]
    public void Disk_and_memory_can_both_fire_at_once()
    {
        var stats = new SystemStats
        {
            Disk = new DiskStats { MountPoint = "/media", FreeBytes = 3, TotalBytes = 100 },
            Memory = new MemoryStats { UsedBytes = 95, LimitBytes = 100 },
        };

        IReadOnlyList<VitalsAlert> alerts = VitalsAlerts.Evaluate(stats, Defaults);

        Assert.Equal(2, alerts.Count);
        Assert.Contains(alerts, a => a.Kind == VitalsAlertKinds.DiskCritical);
        Assert.Contains(alerts, a => a.Kind == VitalsAlertKinds.MemoryHigh);
    }

    [Fact]
    public void Thresholds_come_from_configuration()
    {
        var strict = new VitalsOptions { DiskWarnPercentFree = 50, DiskCriticalPercentFree = 25 };

        Assert.Equal(VitalsAlertKinds.DiskLow, VitalsAlerts.Evaluate(WithDisk(40, 100), strict).Single().Kind);
        Assert.Equal(VitalsAlertKinds.DiskCritical, VitalsAlerts.Evaluate(WithDisk(20, 100), strict).Single().Kind);
    }

    /// <summary>Whole thresholds read as whole numbers — "10%", not "10.0%".</summary>
    [Fact]
    public void The_message_names_the_threshold_without_a_trailing_zero()
    {
        Assert.Contains("10% free", VitalsAlerts.Evaluate(WithDisk(8, 100), Defaults).Single().Message);
    }

    private static SystemStats WithDetection(DetectionStats detection) => new() { Detection = detection };

    [Fact]
    public void A_healthy_detector_with_full_coverage_raises_nothing()
    {
        Assert.Empty(VitalsAlerts.Evaluate(
            WithDetection(new DetectionStats
            {
                Backend = "edgetpu/model",
                Lanes = 2,
                HealthyLanes = 2,
                Coverage = 1.0,
            }),
            Defaults));
    }

    [Fact]
    public void A_lost_accelerator_is_reported_even_though_coverage_is_perfect()
    {
        // The failure this whole mechanism exists for. Losing a device halves capacity, the scheduler
        // rescales, and the work that no longer fits is *shed* — but before the rescale it becomes
        // dropped frames, which coverage deliberately ignores. Coverage can therefore read 1.00 on a host
        // that has lost half its detection, so the lane count has to be asked about directly.
        VitalsAlert alert = Assert.Single(VitalsAlerts.Evaluate(
            WithDetection(new DetectionStats
            {
                Backend = "edgetpu/model",
                Lanes = 2,
                HealthyLanes = 1,
                Coverage = 1.0,
                DetectorDegraded = "1 of 2 Edge TPU(s) stopped responding: 1-1",
            }),
            Defaults));

        Assert.Equal(VitalsAlertKinds.DetectionDegraded, alert.Kind);
        Assert.Equal("warning", alert.Severity);
        Assert.Contains("1 of 2", alert.Message);
        Assert.Contains("1-1", alert.Message);
    }

    [Fact]
    public void Losing_every_accelerator_is_critical_whatever_coverage_says()
    {
        VitalsAlert alert = Assert.Single(VitalsAlerts.Evaluate(
            WithDetection(new DetectionStats
            {
                Backend = "edgetpu/model",
                Lanes = 2,
                HealthyLanes = 0,
                Coverage = 1.0,
            }),
            Defaults));

        Assert.Equal("critical", alert.Severity);
        Assert.Contains("lost every accelerator", alert.Message);
    }

    [Fact]
    public void A_cpu_backend_never_reports_a_lost_lane()
    {
        // AvailableConcurrency defaults to Concurrency and a thread pool does not lose lanes, so this
        // branch has to stay silent on the deployment that has no accelerator at all.
        Assert.Empty(VitalsAlerts.Evaluate(
            WithDetection(new DetectionStats
            {
                Backend = "onnx/cpu model",
                Lanes = 4,
                HealthyLanes = 4,
                Coverage = 0.99,
            }),
            Defaults));
    }

    [Fact]
    public void The_remedy_for_low_coverage_on_an_accelerator_does_not_suggest_getting_one()
    {
        // What this used to say, unconditionally: "give it a smaller model or an accelerator". Told to an
        // operator whose accelerators are all healthy and simply outnumbered, that is worse than silence.
        VitalsAlert alert = Assert.Single(VitalsAlerts.Evaluate(
            WithDetection(new DetectionStats
            {
                Backend = "edgetpu/model",
                Lanes = 2,
                HealthyLanes = 2,
                Coverage = 0.80,
            }),
            Defaults));

        Assert.DoesNotContain("or an accelerator", alert.Message);
        Assert.Contains("add a device", alert.Message);
    }

    [Fact]
    public void The_remedy_for_low_coverage_on_the_cpu_still_suggests_an_accelerator()
    {
        // The advice that was always right for this case, and must not have been lost in making the other
        // one conditional.
        VitalsAlert alert = Assert.Single(VitalsAlerts.Evaluate(
            WithDetection(new DetectionStats { Backend = "onnx/cpu model", Coverage = 0.80 }),
            Defaults));

        Assert.Contains("accelerator", alert.Message);
    }

    [Fact]
    public void A_detector_that_reports_no_lane_counts_is_judged_on_coverage_alone()
    {
        // An older payload, or a backend that says nothing about lanes. Nullable both ways, so absence
        // must not read as zero healthy lanes and raise a critical alert on a working host.
        VitalsAlert alert = Assert.Single(VitalsAlerts.Evaluate(
            WithDetection(new DetectionStats { Coverage = 0.80 }),
            Defaults));

        Assert.Equal("warning", alert.Severity);
        Assert.DoesNotContain("accelerator", alert.Message[..40]);
    }
}
