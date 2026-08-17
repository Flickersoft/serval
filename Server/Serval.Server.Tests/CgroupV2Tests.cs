using Serval.Server.Vitals;

namespace Serval.Server.Tests;

/// <summary>
/// The container's own CPU and memory accounting. Pinned here rather than behind a container
/// because every one of these is a string-to-number decision plus one division, and the ways they
/// go wrong — a restart making the delta negative, a percentage of 100.3, a "max" quota parsed as
/// a number — are all reachable from a file body.
/// </summary>
public class CgroupV2Tests
{
    /// <summary>A real cpu.stat body, verbatim.</summary>
    private const string CpuStat = """
        usage_usec 113416298204
        user_usec 48455409189
        system_usec 64960889014
        nice_usec 7981521954
        core_sched.force_idle_usec 0
        nr_periods 0
        nr_throttled 0
        throttled_usec 0
        nr_bursts 0
        burst_usec 0
        """;

    [Fact]
    public void Usage_is_read_out_of_the_whole_file()
    {
        Assert.Equal(113416298204, CgroupV2.ParseUsageUsec(CpuStat));
    }

    [Fact]
    public void Usage_is_null_when_the_key_is_absent()
    {
        Assert.Null(CgroupV2.ParseUsageUsec("nr_periods 0\nnr_throttled 0"));
        Assert.Null(CgroupV2.ParseUsageUsec(""));
        Assert.Null(CgroupV2.ParseUsageUsec(null));
    }

    /// <summary>
    /// The production branch: no shipped compose file (deploy/docker-compose.yml or the
    /// deploy/examples/ files) sets a CPU limit — only a memory one — so "max" is what this
    /// actually reads on those deployments, and the denominator falls back to the host's core count.
    /// </summary>
    [Fact]
    public void No_quota_is_null_rather_than_a_number()
    {
        Assert.Null(CgroupV2.ParseQuotaCores("max 100000"));
    }

    [Theory]
    [InlineData("200000 100000", 2.0)]
    [InlineData("100000 100000", 1.0)]
    [InlineData("50000 100000", 0.5)]
    public void A_quota_reads_as_cores(string cpuMax, double expected)
    {
        Assert.Equal(expected, CgroupV2.ParseQuotaCores(cpuMax));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("200000")]
    [InlineData("nonsense here")]
    [InlineData("0 100000")]
    [InlineData("200000 0")]
    public void A_malformed_quota_is_null(string? cpuMax)
    {
        Assert.Null(CgroupV2.ParseQuotaCores(cpuMax));
    }

    [Fact]
    public void Memory_max_of_max_means_no_limit()
    {
        Assert.Null(CgroupV2.ParseBytes("max"));
        Assert.Null(CgroupV2.ParseBytes("max\n"));
    }

    [Fact]
    public void Memory_reads_as_bytes()
    {
        Assert.Equal(8589934592, CgroupV2.ParseBytes("8589934592\n"));
        Assert.Equal(2617245696, CgroupV2.ParseBytes("2617245696"));
        Assert.Null(CgroupV2.ParseBytes("not a number"));
    }

    [Fact]
    public void One_busy_core_of_sixteen_is_six_and_a_quarter_percent()
    {
        var before = new CpuSample(UsageUsec: 0, WallClockUsec: 0);
        var after = new CpuSample(UsageUsec: 1_000_000, WallClockUsec: 1_000_000);

        Assert.Equal(6.25, CgroupV2.Percent(before, after, cores: 16)!.Value, precision: 4);
    }

    [Fact]
    public void Eight_busy_cores_of_sixteen_is_half()
    {
        var before = new CpuSample(0, 0);
        var after = new CpuSample(8_000_000, 1_000_000);

        Assert.Equal(50.0, CgroupV2.Percent(before, after, cores: 16)!.Value, precision: 4);
    }

    /// <summary>
    /// The counters and the wall clock are read microseconds apart, so a fully-loaded box can
    /// otherwise produce 100.3% — arithmetically explicable and indistinguishable from a bug to
    /// everyone who sees it.
    /// </summary>
    [Fact]
    public void A_fully_loaded_box_never_reports_over_a_hundred()
    {
        var before = new CpuSample(0, 0);
        var after = new CpuSample(16_030_000, 1_000_000);

        Assert.Equal(100.0, CgroupV2.Percent(before, after, cores: 16));
    }

    [Fact]
    public void No_time_between_samples_is_null_rather_than_a_division_by_zero()
    {
        var sample = new CpuSample(1_000, 5_000);

        Assert.Null(CgroupV2.Percent(sample, sample, cores: 8));
    }

    /// <summary>A counter that went backwards means the container restarted between samples. The
    /// delta is meaningless, not negative.</summary>
    [Fact]
    public void A_restart_between_samples_is_null_rather_than_negative()
    {
        var before = new CpuSample(9_000_000, 0);
        var after = new CpuSample(1_000, 1_000_000);

        Assert.Null(CgroupV2.Percent(before, after, cores: 8));
    }

    [Fact]
    public void Zero_cores_is_null()
    {
        Assert.Null(CgroupV2.Percent(new CpuSample(0, 0), new CpuSample(1, 1), cores: 0));
    }

    /// <summary>
    /// A real body, abbreviated but with the key order and the neighbouring keys intact — the
    /// prefix collisions are the whole difficulty of parsing this file.
    /// </summary>
    private const string MemoryStat = """
        anon 2416640000
        file 8241152000
        kernel 167772160
        slab 150601728
        sock 0
        shmem 3882123264
        file_mapped 54132736
        file_dirty 4096000
        file_writeback 2048000
        inactive_anon 3880026112
        active_anon 2412544
        inactive_file 4262461440
        active_file 655360
        unevictable 0
        """;

    [Fact]
    public void A_memory_stat_key_reads_its_own_value()
    {
        Assert.Equal(2416640000, CgroupV2.ParseMemoryStat(MemoryStat, "anon"));
        Assert.Equal(4262461440, CgroupV2.ParseMemoryStat(MemoryStat, "inactive_file"));
        Assert.Equal(0, CgroupV2.ParseMemoryStat(MemoryStat, "sock"));
    }

    /// <summary>
    /// The bug this guards is a prefix match: <c>file</c> is a key, and so are <c>file_mapped</c>,
    /// <c>file_dirty</c> and <c>file_writeback</c>. Reading the wrong one understates the cache by
    /// most of it, which quietly restores the very 100%-forever reading the subtraction exists to
    /// remove. Same trap for <c>anon</c> against <c>inactive_anon</c>.
    /// </summary>
    [Fact]
    public void A_key_does_not_match_a_longer_key_that_starts_with_it()
    {
        Assert.Equal(8241152000, CgroupV2.ParseMemoryStat(MemoryStat, "file"));
        Assert.Equal(54132736, CgroupV2.ParseMemoryStat(MemoryStat, "file_mapped"));
        Assert.Equal(2416640000, CgroupV2.ParseMemoryStat(MemoryStat, "anon"));
        Assert.Equal(3880026112, CgroupV2.ParseMemoryStat(MemoryStat, "inactive_anon"));
    }

    [Fact]
    public void An_absent_or_unreadable_memory_stat_key_is_null()
    {
        Assert.Null(CgroupV2.ParseMemoryStat(MemoryStat, "zswap"));
        Assert.Null(CgroupV2.ParseMemoryStat(MemoryStat, ""));
        Assert.Null(CgroupV2.ParseMemoryStat(null, "anon"));
        Assert.Null(CgroupV2.ParseMemoryStat("inactive_file not a number", "inactive_file"));
    }

    /// <summary>
    /// A reading measured on a real host: 10,236 MiB charged against a 10,240 MiB limit, of which
    /// 4,064 MiB is reclaimable cache. The working set is 6,172 MiB — 60% rather than 100%.
    /// </summary>
    [Fact]
    public void The_working_set_excludes_reclaimable_cache()
    {
        const long current = 10_236L * 1024 * 1024;
        const long cache = 4_064L * 1024 * 1024;

        Assert.Equal((10_236L - 4_064) * 1024 * 1024, CgroupV2.WorkingSet(current, cache));
    }

    /// <summary>
    /// No cache figure means no subtraction. A kernel without the key gets the raw number rather
    /// than a guess — the pre-existing behaviour, which was only ever wrong by the cache.
    /// </summary>
    [Fact]
    public void A_missing_cache_figure_leaves_the_current_value_alone()
    {
        Assert.Equal(4096, CgroupV2.WorkingSet(4096, null));
        Assert.Equal(4096, CgroupV2.WorkingSet(4096, 0));
    }

    /// <summary>
    /// The two files are read a moment apart, so a cache figure larger than the total is possible
    /// and means the samples disagree — not that the container is holding negative memory.
    /// </summary>
    [Fact]
    public void A_cache_figure_larger_than_the_total_does_not_go_negative()
    {
        Assert.Equal(1000, CgroupV2.WorkingSet(1000, 2000));
    }
}
