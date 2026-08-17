namespace Serval.Server.Vitals;

/// <summary>
/// One fast sample, kept so the App can draw a line rather than a single bar.
///
/// The four figures are exactly the ones the *Server &amp; storage* page meters — container
/// processor share, memory against its limit, GPU utilisation, accelerator utilisation — and nothing
/// else. Everything else on <see cref="SystemStats"/> is either slow-moving (the disk walk runs every
/// fifteen minutes) or not a ratio (load average, driver names), and neither is a thing a sparkline
/// says well.
///
/// <b>Every figure is nullable, and that is load-bearing.</b> A sample taken while the GPU driver
/// published nothing stores null, not zero — the App breaks the line there. A zero would assert an
/// idle GPU, which is a measurement nobody took. This is the same rule
/// <see cref="SystemStats"/> is built on, and a chart is where it is easiest to lose.
/// </summary>
/// <param name="SampledAt">When this sample was taken.</param>
/// <param name="CpuPercent">Container processor share, <see cref="CpuStats.ContainerPercent"/>.</param>
/// <param name="MemoryPercent">Memory against its cgroup limit, <see cref="MemoryStats.Percent"/>.</param>
/// <param name="GpuPercent">Whole-GPU utilisation, <see cref="GpuStats.BusyPercent"/>.</param>
/// <param name="AcceleratorPercent">Pooled accelerator utilisation,
/// <see cref="AcceleratorStats.BusyPercent"/>. Null on every host with no accelerator, which is most
/// of them — and null throughout is exactly what stops the App drawing an empty chart for one.</param>
public readonly record struct VitalsSample(
    DateTimeOffset SampledAt,
    double? CpuPercent,
    double? MemoryPercent,
    double? GpuPercent,
    double? AcceleratorPercent = null);

/// <summary>
/// A fixed-capacity ring of samples, oldest evicted first.
///
/// Its own type rather than two fields on <see cref="SystemStatsCollector"/>, for the reason every
/// other small piece of this folder is — <see cref="ProcStat"/>, <see cref="CgroupV2"/>,
/// <see cref="GpuSysfs"/> — it can be tested on a fresh clone with no Mongo, no procfs and no
/// models fetched, which the collector itself cannot be.
///
/// Locked rather than lock-free. The collector publishes <c>_current</c> by swapping a volatile
/// reference, which is correct for one immutable object and not available to a buffer mutated in
/// place. Contention is not a consideration here: one write per sample interval against a read
/// only while somebody has the server page open.
/// </summary>
public sealed class VitalsRing
{
    private readonly Queue<VitalsSample> _samples = new();
    private readonly Lock _lock = new();

    private int _capacity;

    public VitalsRing(int capacity) => Capacity = capacity;

    /// <summary>
    /// How many samples are kept. Zero means retention is switched off.
    ///
    /// <para>Settable, because the window it is derived from is a setting a user can change while
    /// the server runs. Shrinking drops the oldest samples immediately rather than waiting for the
    /// next one to evict them, and growing keeps everything already retained — so narrowing and
    /// widening the window again costs the history in between, but merely widening it costs
    /// nothing.</para>
    /// </summary>
    public int Capacity
    {
        get => _capacity;
        set
        {
            lock (_lock)
            {
                _capacity = Math.Max(value, 0);
                Trim();
            }
        }
    }

    /// <summary>
    /// How many samples a window of <paramref name="historyMinutes"/> holds at a sample every
    /// <paramref name="sampleSeconds"/> — 720 at the defaults.
    ///
    /// Derived from both dials rather than a constant, because the setting is a *duration*: a
    /// deployment that slows the cadence to save a little work should still keep its hour, not
    /// silently stretch to six of them.
    /// </summary>
    public static int CapacityFor(double historyMinutes, double sampleSeconds) =>
        historyMinutes <= 0
            ? 0
            : (int)Math.Ceiling(historyMinutes * 60.0 / Math.Max(sampleSeconds, 1.0));

    /// <summary>
    /// Files one sample, evicting the oldest once full. Nulls are carried through untouched — a
    /// sample taken while the GPU published nothing stores null, and substituting a zero would be
    /// indistinguishable from an idle GPU by the time it reached the App.
    /// </summary>
    public void Add(VitalsSample sample)
    {
        if (Capacity == 0)
        {
            return;
        }

        lock (_lock)
        {
            _samples.Enqueue(sample);
            Trim();
        }
    }

    /// <summary>Drops the oldest until the buffer fits. Callers hold <see cref="_lock"/>.</summary>
    private void Trim()
    {
        while (_samples.Count > _capacity)
        {
            _samples.Dequeue();
        }
    }

    /// <summary>
    /// The retained samples, oldest first. Copied under the lock so a caller never iterates a
    /// buffer the sampler is appending to mid-response.
    /// </summary>
    public VitalsSample[] Snapshot()
    {
        lock (_lock)
        {
            return [.. _samples];
        }
    }
}

/// <summary>
/// What <c>GET /api/system/stats/history</c> serves: the retained samples, as parallel arrays.
///
/// Arrays rather than a list of objects because this is the one route in the API that is bulk
/// numbers — an hour at the default five-second cadence is 720 points, and repeating four key
/// names per point roughly triples the payload to say nothing new. The arrays are always the same
/// length; index <c>i</c> of each belongs to <see cref="SampledAt"/> index <c>i</c>.
///
/// <b>Timestamps are stored per sample, not inferred.</b> A caller could divide the window by the
/// cadence and assume even spacing, and it would be wrong the first time a tick is missed — a
/// garbage collection pause, a worker exception, a container the host suspended. Every earlier
/// point would then silently shift in time. Sending what was actually recorded costs one array.
///
/// A <c>null</c> in any series is a sample where that figure was unavailable, and must be drawn as
/// a break in the line. Serialisation must leave <c>DefaultIgnoreCondition</c> at <c>Never</c> for
/// the same reason <see cref="SystemStats"/> does — a null quietly dropped becomes a zero at the
/// far end, which is the one thing this whole design refuses to do.
/// </summary>
public sealed record VitalsHistory
{
    /// <summary>When each sample was taken, oldest first.</summary>
    public IReadOnlyList<DateTimeOffset> SampledAt { get; init; } = [];

    public IReadOnlyList<double?> Cpu { get; init; } = [];
    public IReadOnlyList<double?> Memory { get; init; } = [];
    public IReadOnlyList<double?> Gpu { get; init; } = [];
    public IReadOnlyList<double?> Accelerator { get; init; } = [];

    /// <summary>
    /// How long a window the server is willing to keep, from <c>Vitals:HistoryMinutes</c>. The App
    /// draws the axis from this rather than from the samples it happens to have, so a buffer that
    /// is still filling after a restart reads as "twenty minutes into an hour" instead of
    /// stretching twenty minutes across the full width and looking complete.
    /// </summary>
    public double WindowMinutes { get; init; }

    /// <summary>
    /// Why there is no history, when there is none: retention switched off, or vitals switched off
    /// entirely. Null when the arrays are the answer — including when they are empty because the
    /// server started moments ago, which is not a fault and says so by filling in shortly.
    /// </summary>
    public string? UnavailableReason { get; init; }

    /// <summary>The shape served when retention is off, matching <see cref="SystemStats.NotSampled"/>'s
    /// reasoning: a page that renders "not reported" beats one that fails to load.</summary>
    public static VitalsHistory Unavailable(string reason) => new() { UnavailableReason = reason };

    /// <summary>
    /// Transposes samples into the parallel arrays the wire contract uses. The arrays are built from
    /// one sequence in one pass each, so they cannot drift out of alignment — which is the failure
    /// this shape is otherwise exposed to, and the one that would silently attribute a reading to the
    /// wrong instant.
    /// </summary>
    public static VitalsHistory From(IReadOnlyList<VitalsSample> samples, double windowMinutes) => new()
    {
        SampledAt = [.. samples.Select(s => s.SampledAt)],
        Cpu = [.. samples.Select(s => s.CpuPercent)],
        Memory = [.. samples.Select(s => s.MemoryPercent)],
        Gpu = [.. samples.Select(s => s.GpuPercent)],
        Accelerator = [.. samples.Select(s => s.AcceleratorPercent)],
        WindowMinutes = windowMinutes,
    };
}
