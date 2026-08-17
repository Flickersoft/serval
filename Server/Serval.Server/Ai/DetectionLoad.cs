namespace Serval.Server.Ai;

/// <summary>
/// What detection is currently having to skip, and why.
///
/// <para>Every mechanism in the detection path degrades by dropping work rather than falling over:
/// the frame reader drops a backlog, the handoff keeps only the newest, the scheduler sheds crops it
/// cannot afford, the detector declines a frame it is still busy for. That is right — a security
/// recorder must not stall its recording because inference is slow — but each drop is a moment
/// nothing looked at, and unreported it looks identical to keeping up. Somebody comparing
/// yesterday's events with today's would have no way to know the difference was the host rather than
/// the driveway.</para>
///
/// <para>The counters are cumulative and monotonic, with rates computed against the last read. That
/// keeps this to three interlocked increments per camera loop, and leaves the judgement about what
/// counts as degraded in <c>VitalsAlerts</c> with the rest of it.</para>
/// </summary>
public sealed class DetectionLoad
{
    private long _examined;
    private long _shedRegions;

    private long _lastExamined;
    private long _lastShedRegions;
    private DateTimeOffset _lastReadAt = DateTimeOffset.UtcNow;

    private readonly Lock _gate = new();

    /// <summary>Inferences a second the host was measured at and is willing to spend, or null when
    /// the detector could not be timed and nothing is being throttled.</summary>
    public double? BudgetPerSecond { get; set; }

    /// <summary>How that budget is being divided.</summary>
    public int ActiveCameras { get; set; }

    /// <summary>Which detector implementation is running, from <c>IObjectDetector.Description</c>.
    /// On the status page because with more than one backend buildable, "which one is actually running"
    /// becomes a question worth answering without reading the startup log.</summary>
    public string? Backend { get; set; }

    /// <summary>Lanes the detector was built with, and how many can run now. Equal on a CPU backend
    /// always; unequal means a device was lost and capacity fell with it.</summary>
    public int? Lanes { get; set; }

    /// <inheritdoc cref="Lanes"/>
    public int? HealthyLanes { get; set; }

    /// <summary>Why the detector says it is degraded, or null when it is not.</summary>
    public string? DetectorDegraded { get; set; }

    /// <summary>A region was examined.</summary>
    public void Examined(int count = 1) => Interlocked.Add(ref _examined, count);

    /// <summary>A region the planner wanted was not examined, for want of budget.</summary>
    public void ShedRegions(int count) => Interlocked.Add(ref _shedRegions, count);

    /// <summary>
    /// Rates since this was last read, and the totals behind them.
    ///
    /// Reading resets the window, so this has exactly one consumer — the stats sampler. A second
    /// caller would silently halve the first one's rates.
    /// </summary>
    public DetectionLoadSample Read(DateTimeOffset now)
    {
        lock (_gate)
        {
            long examined = Interlocked.Read(ref _examined);
            long shed = Interlocked.Read(ref _shedRegions);

            double seconds = (now - _lastReadAt).TotalSeconds;

            var sample = new DetectionLoadSample(
                BudgetPerSecond,
                ActiveCameras,
                examined,
                shed,
                Rate(examined - _lastExamined, seconds),
                Rate(shed - _lastShedRegions, seconds),
                Backend,
                Lanes,
                HealthyLanes,
                DetectorDegraded);

            _lastExamined = examined;
            _lastShedRegions = shed;
            _lastReadAt = now;

            return sample;
        }

        // A window of no elapsed time has no rate rather than an infinite one.
        static double? Rate(long delta, double seconds) =>
            seconds > 0 ? delta / seconds : null;
    }
}

/// <summary>One reading of <see cref="DetectionLoad"/>.</summary>
public readonly record struct DetectionLoadSample(
    double? BudgetPerSecond,
    int ActiveCameras,
    long ExaminedTotal,
    long ShedRegionsTotal,
    double? ExaminedPerSecond,
    double? ShedRegionsPerSecond,
    string? Backend = null,
    int? Lanes = null,
    int? HealthyLanes = null,
    string? DetectorDegraded = null);
