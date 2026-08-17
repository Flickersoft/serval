using System.Globalization;
using Serval.Server.Vitals;

namespace Serval.Server.Tests;

/// <summary>
/// The host's CPU, from procfs — which inside a container describes the whole machine rather than
/// this container, and is worth having for exactly that reason.
/// </summary>
public class ProcStatTests
{
    private const string Stat = """
        cpu  1000 200 300 8000 100 0 50 0 0 0
        cpu0 500 100 150 4000 50 0 25 0 0 0
        cpu1 500 100 150 4000 50 0 25 0 0 0
        intr 12345
        ctxt 67890
        """;

    /// <summary>The per-core lines are already inside the aggregate; summing them too would
    /// double-count every jiffy on the box.</summary>
    [Fact]
    public void The_aggregate_line_is_used_and_the_per_core_ones_are_not()
    {
        HostCpuSample sample = ProcStat.ParseAggregate(Stat)!.Value;

        Assert.Equal(1000 + 200 + 300 + 8000 + 100 + 0 + 50, sample.Total);

        // idle + iowait: a core blocked on a disk read is not doing work, and counting iowait as
        // busy makes a recording NVR look permanently pinned.
        Assert.Equal(8000 + 100, sample.Idle);
    }

    /// <summary>Field count varies by kernel age, so every numeric field present is summed rather
    /// than a fixed number being indexed.</summary>
    [Fact]
    public void An_old_kernels_seven_fields_parse_too()
    {
        HostCpuSample sample = ProcStat.ParseAggregate("cpu  1000 200 300 8000 100 0 50")!.Value;

        Assert.Equal(9650, sample.Total);
        Assert.Equal(8100, sample.Idle);
    }

    [Fact]
    public void A_file_with_no_cpu_line_is_null()
    {
        Assert.Null(ProcStat.ParseAggregate("intr 12345\nctxt 67890"));
        Assert.Null(ProcStat.ParseAggregate(""));
        Assert.Null(ProcStat.ParseAggregate(null));
    }

    [Fact]
    public void Busy_is_the_non_idle_share_of_the_delta()
    {
        var before = new HostCpuSample(Idle: 900, Total: 1000);
        var after = new HostCpuSample(Idle: 1150, Total: 2000);

        // 1000 jiffies passed, 250 of them idle.
        Assert.Equal(75.0, ProcStat.Percent(before, after)!.Value, precision: 4);
    }

    [Fact]
    public void No_time_between_samples_is_null()
    {
        var sample = new HostCpuSample(900, 1000);

        Assert.Null(ProcStat.Percent(sample, sample));
    }

    [Fact]
    public void A_restart_between_samples_is_null()
    {
        Assert.Null(ProcStat.Percent(new HostCpuSample(900, 5000), new HostCpuSample(100, 1000)));
    }

    [Fact]
    public void Load_average_takes_the_first_three_fields()
    {
        Assert.Equal((3.4, 2.9, 2.1), ProcStat.ParseLoadAverage("3.40 2.90 2.10 2/1543 91011"));
    }

    /// <summary>
    /// Parsed under the invariant culture explicitly. A container whose locale uses a comma decimal
    /// separator would otherwise read "3.40" as 340, and a load average of 340 on an eight-core box
    /// looks like a real emergency rather than a parsing bug.
    /// </summary>
    [Fact]
    public void A_comma_decimal_locale_does_not_turn_three_point_four_into_three_hundred_and_forty()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal((3.4, 2.9, 2.1), ProcStat.ParseLoadAverage("3.40 2.90 2.10 2/1543 91011"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("3.40 2.90")]
    [InlineData("a b c 2/1543 91011")]
    public void A_malformed_load_average_is_null(string? loadavg)
    {
        Assert.Null(ProcStat.ParseLoadAverage(loadavg));
    }
}
