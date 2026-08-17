using System.Diagnostics;
using Microsoft.Extensions.Options;
using Serval.Server.Cameras;
using Serval.Server.Configuration;

namespace Serval.Server.Vitals;

/// <summary>
/// Keeps <see cref="SystemStatsCollector"/> current, on two cadences that differ by three orders
/// of magnitude in cost.
///
/// The fast one — processor, memory, GPU, volume free space — is four small file reads and one
/// statvfs, every few seconds. The slow one walks each camera's media directory, which is
/// ~150,000 files after a week of four-second segments, and is the only expensive thing in this
/// feature. It runs the full set once at startup so the page has complete figures early, then
/// re-walks one directory per interval, so the steady-state cost is one directory rather than all
/// of them at once.
///
/// <para>Completeness comes before freshness, at startup and afterwards alike: a directory nothing
/// has walked yet is missing from the breakdown, which is a worse answer than one measured an
/// interval ago. <see cref="ScanNextAsync"/> is where the two rules meet.</para>
///
/// Same shape as <see cref="Recordings.RetentionWorker"/>: sweep once at startup, then on a
/// <see cref="PeriodicTimer"/>, with a failure logged and retried next interval rather than
/// bringing the host down.
/// </summary>
public sealed class SystemStatsWorker : PeriodicWorker
{
    private readonly SystemStatsCollector _collector;
    private readonly CameraRepository _cameras;
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly ILogger<SystemStatsWorker> _logger;

    /// <summary>Where the rotation is up to, over the live keys as of the last sweep.</summary>
    private int _next;

    /// <summary>
    /// Every directory this process has walked at least once, which is what
    /// <see cref="DiskScanRotation"/> reads to tell a catch-up from a refresh.
    ///
    /// Attempts rather than results, and kept here rather than read off the collector's figures for
    /// that reason: a directory that cannot be read leaves no figure, and one that keeps failing
    /// would otherwise be first in the queue on every tick forever. Pruned with the figures
    /// themselves, so a camera deleted and registered again is walked again.
    /// </summary>
    private readonly HashSet<string> _walked = new(StringComparer.Ordinal);

    /// <summary>Whether the last tick found vitals switched off, so the log says so once per change.</summary>
    private bool _idle;

    /// <summary>Whether the baseline sample has been taken yet — see <see cref="TickAsync"/>.</summary>
    private bool _started;

    private DateTimeOffset _nextDiskScan;

    public SystemStatsWorker(
        SystemStatsCollector collector,
        CameraRepository cameras,
        IOptionsMonitor<ServerOptions> options,
        ILogger<SystemStatsWorker> logger)
        : base(logger)
    {
        _collector = collector;
        _cameras = cameras;
        _options = options;
        _logger = logger;
    }

    private VitalsOptions Vitals => _options.CurrentValue.Vitals;

    protected override string Activity => "Vitals sample";

    protected override TimeSpan Interval =>
        TimeSpan.FromSeconds(Math.Max(Vitals.SampleSeconds, 1.0));

    /// <summary>
    /// The worker keeps ticking while vitals are switched off rather than returning, because it is
    /// the only thing that could notice them being switched back on.
    /// </summary>
    protected override async Task TickAsync(CancellationToken stoppingToken)
    {
        if (Paused())
        {
            return;
        }

        // The first fast sample has nothing to subtract from, so it establishes the baseline and
        // publishes a CPU figure of "waiting for a second sample" — which the next tick fills in.
        if (!_started)
        {
            _started = true;
            Tick();
            await FullDiskSweepAsync(stoppingToken);
            _nextDiskScan = DateTimeOffset.UtcNow.AddMinutes(Vitals.DiskScanMinutes);
            return;
        }

        Tick();

        if (Vitals.DiskScanMinutes <= 0)
        {
            _collector.ClearDiskDetail();
            return;
        }

        if (DateTimeOffset.UtcNow < _nextDiskScan)
        {
            return;
        }

        _nextDiskScan = DateTimeOffset.UtcNow.AddMinutes(Vitals.DiskScanMinutes);
        await ScanNextAsync(stoppingToken);
    }

    /// <summary>
    /// Whether vitals are switched off, logging the transition rather than the state — this is
    /// called every tick, and a line per tick would be the same sentence forever.
    /// </summary>
    private bool Paused()
    {
        bool paused = !Vitals.Enabled;

        if (paused != _idle)
        {
            _idle = paused;
            _logger.LogInformation(
                paused
                    ? "Vitals are switched off; measurement is paused."
                    : "Vitals are switched on; measurement has resumed.");
        }

        return paused;
    }

    /// <summary>The cheap sample. Exceptions here are the collector's own to swallow — it treats
    /// every probe as optional — so anything reaching this is a bug worth a log line.</summary>
    private void Tick()
    {
        try
        {
            _collector.SampleVolume();
            _collector.SampleFast();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vitals sample failed; retrying next interval.");
        }
    }

    /// <summary>
    /// Every directory, once, at startup. A partial set would make the per-camera figures fail to
    /// add up to the total for the first hour of uptime, which reads as a bug rather than as
    /// progress.
    /// </summary>
    private async Task FullDiskSweepAsync(CancellationToken cancellationToken)
    {
        if (Vitals.DiskScanMinutes <= 0)
        {
            return;
        }

        try
        {
            var elapsed = Stopwatch.StartNew();
            List<DiskScanTarget> targets = await TargetsAsync(cancellationToken);

            await WalkAsync(targets, cancellationToken);

            elapsed.Stop();
            Publish(targets, elapsed.Elapsed.TotalSeconds);

            _logger.LogInformation(
                "Measured {Count} media director{Suffix} in {Seconds:0.0}s.",
                targets.Count, targets.Count == 1 ? "y" : "ies", elapsed.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down mid-walk is not a failure.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initial disk usage scan failed; the per-camera figures will fill in later.");
        }
    }

    /// <summary>
    /// One directory, in rotation — or every directory nothing has walked yet, if there are any.
    /// <see cref="DiskScanRotation"/> makes that choice and says why.
    ///
    /// A camera registered after startup missed <see cref="FullDiskSweepAsync"/>, and under a plain
    /// rotation it waited one interval per directory ahead of it: on a server with six of them the
    /// last arrived over two hours later, and until then the page showed one camera while all six
    /// were recording.
    /// </summary>
    private async Task ScanNextAsync(CancellationToken cancellationToken)
    {
        List<DiskScanTarget> targets = await TargetsAsync(cancellationToken);
        DiskScanTick tick = DiskScanRotation.Next(targets, _walked.Contains, _next);
        _next = tick.Cursor;

        if (tick.Walk.Count == 0)
        {
            return;
        }

        var elapsed = Stopwatch.StartNew();
        await WalkAsync(tick.Walk, cancellationToken);
        elapsed.Stop();

        Publish(targets, elapsed.Elapsed.TotalSeconds);

        if (tick.CatchingUp)
        {
            _logger.LogInformation(
                "Measured {Count} media director{Suffix} registered since the last sweep in {Seconds:0.0}s.",
                tick.Walk.Count, tick.Walk.Count == 1 ? "y" : "ies", elapsed.Elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// Measures each directory and marks it as having had its turn.
    ///
    /// The mark goes on either way. <see cref="SystemStatsCollector.ScanOneAsync"/> swallows an
    /// unreadable directory, and treating that as never-walked would hand it every subsequent tick.
    /// </summary>
    private async Task WalkAsync(
        IEnumerable<DiskScanTarget> targets, CancellationToken cancellationToken)
    {
        foreach (DiskScanTarget target in targets)
        {
            await _collector.ScanOneAsync(target, cancellationToken);
            _walked.Add(target.Key);
        }
    }

    /// <summary>Publishes the figures and forgets any directory that has gone, so a camera deleted
    /// and registered again is walked again rather than being taken for already done.</summary>
    private void Publish(List<DiskScanTarget> targets, double scanSeconds)
    {
        IReadOnlyCollection<string> live = KeysOf(targets);

        _walked.IntersectWith(live);
        _collector.PublishDisk(live, scanSeconds);
    }

    /// <summary>Every camera's directory, plus the ones under the same root that belong to none.</summary>
    private async Task<List<DiskScanTarget>> TargetsAsync(CancellationToken cancellationToken)
    {
        List<Camera> cameras = await _cameras.ListAsync(cancellationToken);
        IReadOnlyList<DiskScanTarget> others = _collector.NonCameraTargets;

        var targets = new List<DiskScanTarget>(cameras.Count + others.Count);
        targets.AddRange(cameras.Select(_collector.TargetFor));
        targets.AddRange(others);
        return targets;
    }

    private static IReadOnlyCollection<string> KeysOf(List<DiskScanTarget> targets) =>
        targets.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
}
