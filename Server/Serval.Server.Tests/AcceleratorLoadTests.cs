using Serval.Ai;
using Serval.Server.Vitals;

namespace Serval.Server.Tests;

/// <summary>
/// The arithmetic behind the Edge TPU meter: two readings of the detector's cumulative counters, and
/// what the page says about the window between them.
///
/// The rule every one of these is really about is the same one the whole payload is built on — a
/// figure nobody measured is null, never zero. An idle accelerator and one that has not been read
/// twice yet look identical the moment a null becomes a 0.
/// </summary>
public class AcceleratorLoadTests
{
    private static readonly Func<string, string?> NoLinks = static _ => null;

    private static Dictionary<string, DetectorDevice> Previous(params DetectorDevice[] devices) =>
        devices.ToDictionary(device => device.Path, StringComparer.Ordinal);

    [Fact]
    public void The_first_reading_has_no_window_to_measure_across()
    {
        AcceleratorStats stats = AcceleratorLoad.Measure(
            [new DetectorDevice("2-2", true, 100, 0, 1.5)],
            Previous(),
            elapsedSeconds: 0,
            label: "Edge TPU",
            declinedPerSecond: null,
            link: NoLinks);

        Assert.Equal(AcceleratorLoad.WarmingUpReason, stats.UnavailableReason);
        Assert.Null(stats.BusyPercent);
        Assert.Null(stats.InferencesPerSecond);

        // The device is still listed. It exists, and the meter is drawn on that rather than on
        // having a figure yet.
        AcceleratorDeviceStats device = Assert.Single(stats.Devices!);
        Assert.Equal("2-2", device.Name);
        Assert.Null(device.BusyPercent);
        Assert.Null(device.MeanLatencyMs);
    }

    /// <summary>
    /// One device, ten seconds, 600 inferences of 15 ms each. 9 seconds busy out of 10 is 90%, and
    /// the mean latency falls out of the same two numbers.
    /// </summary>
    [Fact]
    public void Busy_time_over_elapsed_time_is_the_percentage()
    {
        AcceleratorStats stats = AcceleratorLoad.Measure(
            [new DetectorDevice("2-2", true, 1_600, 0, 24.0)],
            Previous(new DetectorDevice("2-2", true, 1_000, 0, 15.0)),
            elapsedSeconds: 10,
            label: "Edge TPU",
            declinedPerSecond: 0,
            link: NoLinks);

        Assert.Null(stats.UnavailableReason);
        Assert.Equal(90, stats.BusyPercent);
        Assert.Equal(60, stats.InferencesPerSecond);

        AcceleratorDeviceStats device = Assert.Single(stats.Devices!);
        Assert.Equal(90, device.BusyPercent);
        Assert.Equal(60, device.InferencesPerSecond);
        Assert.Equal(15, device.MeanLatencyMs!.Value, 3);
    }

    /// <summary>
    /// The pooled figure is against every device's time, not one device's — a pair where one is flat
    /// out and the other is idle is a pool at half, because the idle one is capacity going unused.
    ///
    /// This is the meter's whole claim, and getting it wrong the other way (reporting the busiest
    /// device) would show a saturated pool on a host with a spare accelerator.
    /// </summary>
    [Fact]
    public void The_pooled_figure_is_against_every_device()
    {
        AcceleratorStats stats = AcceleratorLoad.Measure(
            [
                new DetectorDevice("2-2", true, 100, 0, 10.0),
                new DetectorDevice("1-1", true, 0, 0, 0.0),
            ],
            Previous(
                new DetectorDevice("2-2", true, 0, 0, 0.0),
                new DetectorDevice("1-1", true, 0, 0, 0.0)),
            elapsedSeconds: 10,
            label: "Edge TPU",
            declinedPerSecond: 0,
            link: NoLinks);

        Assert.Equal(50, stats.BusyPercent);
        Assert.Equal(100, stats.Devices![0].BusyPercent);
        Assert.Equal(0, stats.Devices[1].BusyPercent);

        // Nothing ran on the idle device, so no inference on it has a duration to report.
        Assert.Null(stats.Devices[1].MeanLatencyMs);
        Assert.Equal(0, stats.Devices[1].InferencesPerSecond);
    }

    /// <summary>
    /// A device that stopped answering keeps its row. The pool meter falling is the *only* other
    /// signal, and it is indistinguishable from a quiet afternoon.
    /// </summary>
    [Fact]
    public void A_lost_device_is_still_listed()
    {
        AcceleratorStats stats = AcceleratorLoad.Measure(
            [
                new DetectorDevice("2-2", true, 100, 0, 2.0),
                new DetectorDevice("1-1", false, 40, 3, 1.0),
            ],
            Previous(
                new DetectorDevice("2-2", true, 0, 0, 0.0),
                new DetectorDevice("1-1", true, 40, 3, 1.0)),
            elapsedSeconds: 10,
            label: "Edge TPU",
            declinedPerSecond: 0,
            link: NoLinks);

        Assert.Equal(2, stats.Devices!.Count);

        AcceleratorDeviceStats lost = stats.Devices[1];
        Assert.False(lost.Healthy);
        Assert.Equal("1-1", lost.Name);
        Assert.Equal(3, lost.Failures);

        // It ran nothing this window, which is 0% busy rather than unmeasured — its counters were
        // read twice and both times said the same thing.
        Assert.Equal(0, lost.BusyPercent);
    }

    /// <summary>
    /// Counters that go backwards mean the detector was rebuilt underneath the sampler, not that the
    /// device ran negative work. One window of nulls beats a nonsense figure.
    /// </summary>
    [Fact]
    public void Counters_that_went_backwards_report_nothing_for_one_window()
    {
        AcceleratorStats stats = AcceleratorLoad.Measure(
            [new DetectorDevice("2-2", true, 5, 0, 0.1)],
            Previous(new DetectorDevice("2-2", true, 9_000, 0, 140.0)),
            elapsedSeconds: 10,
            label: "Edge TPU",
            declinedPerSecond: 0,
            link: NoLinks);

        Assert.Equal(AcceleratorLoad.WarmingUpReason, stats.UnavailableReason);
        Assert.Null(stats.BusyPercent);
        Assert.Null(Assert.Single(stats.Devices!).BusyPercent);
    }

    /// <summary>
    /// A window that straddles a missed tick divides real busy time by an interval shorter than the
    /// one it was spent over. A meter cannot be 140% full.
    /// </summary>
    [Fact]
    public void A_short_window_cannot_report_over_a_hundred_percent()
    {
        AcceleratorStats stats = AcceleratorLoad.Measure(
            [new DetectorDevice("2-2", true, 500, 0, 14.0)],
            Previous(new DetectorDevice("2-2", true, 0, 0, 0.0)),
            elapsedSeconds: 10,
            label: "Edge TPU",
            declinedPerSecond: 0,
            link: NoLinks);

        Assert.Equal(100, stats.BusyPercent);
    }

    /// <summary>A new device joins mid-run — it has no previous reading of its own, but the pool
    /// still reports what the device that does have one did.</summary>
    [Fact]
    public void A_device_seen_for_the_first_time_reports_nothing_of_its_own()
    {
        AcceleratorStats stats = AcceleratorLoad.Measure(
            [
                new DetectorDevice("2-2", true, 100, 0, 5.0),
                new DetectorDevice("3-1", true, 12, 0, 0.4),
            ],
            Previous(new DetectorDevice("2-2", true, 0, 0, 0.0)),
            elapsedSeconds: 10,
            label: "Edge TPU",
            declinedPerSecond: 0,
            link: NoLinks);

        Assert.Null(stats.Devices![1].BusyPercent);
        Assert.Null(stats.Devices[1].InferencesPerSecond);

        Assert.Equal(50, stats.Devices[0].BusyPercent);
        Assert.NotNull(stats.BusyPercent);
    }

    [Fact]
    public void The_link_resolver_names_each_device()
    {
        AcceleratorStats stats = AcceleratorLoad.Measure(
            [
                new DetectorDevice("2-2", true, 0, 0, 0.0),
                new DetectorDevice("1-1", true, 0, 0, 0.0),
            ],
            Previous(),
            elapsedSeconds: 0,
            label: "Edge TPU",
            declinedPerSecond: null,
            link: path => path == "2-2" ? "USB 3" : "USB 2");

        Assert.Equal("USB 3", stats.Devices![0].Link);
        Assert.Equal("USB 2", stats.Devices[1].Link);
        Assert.Equal("Edge TPU", stats.Label);
    }
}
