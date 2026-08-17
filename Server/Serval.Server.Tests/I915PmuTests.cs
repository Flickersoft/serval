using Serval.Server.Vitals;

namespace Serval.Server.Tests;

/// <summary>
/// Turning the i915 PMU's sysfs strings into the three numbers a perf_event_open needs, and turning
/// two counter readings into a percentage.
///
/// Every literal below is a real body captured from an Alder Lake-N running Debian 13 on kernel
/// 6.12 — the deployment this was written for. The syscall itself is not testable off Intel
/// hardware, which is exactly why everything up to it is pulled out to here.
/// </summary>
public class I915PmuTests
{
    [Fact]
    public void The_pmu_type_is_a_plain_decimal()
    {
        Assert.Equal(25u, GpuSysfs.ParsePmuType("25\n"));
        Assert.Equal(0u, GpuSysfs.ParsePmuType("0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("i915")]
    [InlineData("-1")]
    public void A_type_that_is_not_a_number_is_no_type(string? body)
    {
        Assert.Null(GpuSysfs.ParsePmuType(body));
    }

    /// <summary>The four engines an Alder Lake-N publishes, exactly as the events files read.</summary>
    [Theory]
    [InlineData("config=0x0\n", 0x0ul)]
    [InlineData("config=0x1000\n", 0x1000ul)]
    [InlineData("config=0x2000\n", 0x2000ul)]
    [InlineData("config=0x3000\n", 0x3000ul)]
    public void An_engine_event_config_is_hex(string body, ulong expected)
    {
        Assert.Equal(expected, GpuSysfs.ParseEventConfig(body));
    }

    [Fact]
    public void A_decimal_config_parses_too()
    {
        Assert.Equal(8192ul, GpuSysfs.ParseEventConfig("config=8192"));
    }

    /// <summary>
    /// i915 writes one term today. perf's format is a comma-separated list, and a kernel that adds
    /// a second term should not turn a working counter into a parse failure.
    /// </summary>
    [Fact]
    public void Only_the_config_term_is_taken()
    {
        Assert.Equal(0x2000ul, GpuSysfs.ParseEventConfig("config=0x2000,umask=0x1\n"));
        Assert.Equal(0x2000ul, GpuSysfs.ParseEventConfig("umask=0x1,config=0x2000"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("0x2000")]
    [InlineData("config=")]
    [InlineData("config=nonsense")]
    public void An_events_file_without_a_usable_config_is_null(string? body)
    {
        Assert.Null(GpuSysfs.ParseEventConfig(body));
    }

    /// <summary>
    /// The counter is one number for the whole device however many CPUs it may be read from, so the
    /// first is the whole job. Summing a mask would double-count.
    /// </summary>
    [Theory]
    [InlineData("0\n", 0)]
    [InlineData("0-3\n", 0)]
    [InlineData("2,6", 2)]
    [InlineData("4", 4)]
    public void The_first_cpu_of_a_mask_wins(string body, int expected)
    {
        Assert.Equal(expected, GpuSysfs.ParseCpuMask(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("none")]
    [InlineData("-1")]
    public void A_mask_that_names_no_cpu_is_null(string? body)
    {
        Assert.Null(GpuSysfs.ParseCpuMask(body));
    }

    /// <summary>Half a second of busy in a five second window is 10%.</summary>
    [Fact]
    public void Busy_nanoseconds_over_the_window_are_a_percentage()
    {
        Assert.Equal(10.0, GpuSysfs.BusyPercent(500_000_000, 5_000_000_000));
        Assert.Equal(100.0, GpuSysfs.BusyPercent(5_000_000_000, 5_000_000_000));
    }

    /// <summary>An idle engine is a real zero here — the counter answered and did not move.</summary>
    [Fact]
    public void A_counter_that_did_not_move_is_zero_rather_than_missing()
    {
        Assert.Equal(0.0, GpuSysfs.BusyPercent(0, 5_000_000_000));
    }

    /// <summary>
    /// The counter and the clock are read a moment apart, so a saturated engine can produce a ratio
    /// a hair over 1.0 without anything being wrong. 101% is not a number to show anyone.
    /// </summary>
    [Fact]
    public void A_ratio_slightly_over_one_clamps()
    {
        Assert.Equal(100.0, GpuSysfs.BusyPercent(5_010_000_000, 5_000_000_000));
    }

    /// <summary>
    /// A backwards delta means the counter was reset underneath us — a driver reload — and a
    /// window with no duration has nothing to divide by. Neither is a zero.
    /// </summary>
    [Theory]
    [InlineData(-1, 5_000_000_000)]
    [InlineData(500_000_000, 0)]
    [InlineData(500_000_000, -1)]
    public void A_window_that_cannot_be_measured_is_null(long deltaNs, long elapsedNs)
    {
        Assert.Null(GpuSysfs.BusyPercent(deltaNs, elapsedNs));
    }

    /// <summary>
    /// The labels are what a person reads under the meter, so they are words rather than the
    /// kernel's ring names. The events are the kernel's, because those are filenames.
    /// </summary>
    [Fact]
    public void The_engine_list_maps_ring_names_to_words()
    {
        Assert.Contains(GpuSysfs.I915EngineEvents, e => e.EventName == "vcs0-busy" && e.Label == "video");
        Assert.Contains(GpuSysfs.I915EngineEvents, e => e.EventName == "rcs0-busy" && e.Label == "render");
        Assert.All(GpuSysfs.I915EngineEvents, e => Assert.EndsWith("-busy", e.EventName));
    }

    /// <summary>
    /// Not an Intel host — this is a development machine or CI — so the only assertion available is
    /// that asking politely returns a sentence rather than throwing. The syscall path is verified on
    /// hardware; this pins that a host without the PMU degrades instead of failing.
    /// </summary>
    [Fact]
    public void Opening_the_counters_where_there_are_none_explains_itself()
    {
        if (Directory.Exists(GpuSysfs.I915PmuRoot))
        {
            return;
        }

        using I915PerfCounters? counters = I915PerfCounters.TryOpen(out string reason);

        Assert.Null(counters);
        Assert.NotEmpty(reason);
    }
}
