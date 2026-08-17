using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serval.Contracts;

namespace Serval.CameraModule;

/// <summary>
/// Delivers telemetry somewhere durable. Implementations must only return successfully
/// once the data is safely written — the outbox marks records delivered on return.
///
/// Records arrive already serialized. Some are stored as columns and turned into documents on the
/// way out, others are stored as the JSON they will be sent as; a sink has no business knowing
/// which, and handing it JSON keeps the two indistinguishable at the boundary.
/// </summary>
public interface ITelemetrySink
{
    Task DeliverAsync(IReadOnlyList<string> jsonRecords, CancellationToken cancellationToken);
}

/// <summary>Appends JSON Lines to the filesystem: the offline delivery target.</summary>
public sealed class FileTelemetrySink : ITelemetrySink
{
    private readonly string _path;
    private readonly ILogger<FileTelemetrySink> _logger;

    public FileTelemetrySink(IOptions<CameraModuleOptions> options, ILogger<FileTelemetrySink> logger)
    {
        _logger = logger;
        _path = options.Value.Output.JsonlPath;

        string? directory = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async Task DeliverAsync(IReadOnlyList<string> jsonRecords, CancellationToken cancellationToken)
    {
        await File.AppendAllLinesAsync(_path, jsonRecords, cancellationToken);
        _logger.LogInformation("Wrote {Count} record(s) to {Path}", jsonRecords.Count, _path);
    }
}

/// <summary>
/// Moves records from the outbox to the sink, then marks them delivered. Records are only
/// marked after the sink returns, so a crash mid-delivery replays rather than loses.
/// </summary>
public sealed class TelemetrySyncWorker : BackgroundService
{
    private const int BatchSize = 100;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    /// <summary>How long the shutdown flush may spend draining before it gives up.</summary>
    private static readonly TimeSpan ShutdownFlushBudget = TimeSpan.FromSeconds(10);

    private readonly TelemetryRepository _repository;
    private readonly ITelemetrySink _sink;
    private readonly OutputOptions _options;
    private readonly ILogger<TelemetrySyncWorker> _logger;

    public TelemetrySyncWorker(
        TelemetryRepository repository,
        ITelemetrySink sink,
        IOptions<CameraModuleOptions> options,
        ILogger<TelemetrySyncWorker> logger)
    {
        _repository = repository;
        _sink = sink;
        _options = options.Value.Output;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.SyncIntervalSeconds);
        TimeSpan backoff = TimeSpan.Zero;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FlushAsync(stoppingToken);
                backoff = TimeSpan.Zero;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Records stay unsynced, so they retry on the next pass. Backoff keeps a
                // persistent failure (full disk, dead endpoint) from spamming.
                backoff = backoff == TimeSpan.Zero
                    ? interval
                    : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));

                _logger.LogError(ex, "Telemetry delivery failed; retrying in {Backoff}.", backoff);
            }

            try
            {
                await Task.Delay(backoff == TimeSpan.Zero ? interval : backoff, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Best-effort final flush so a clean shutdown doesn't strand delivered-ready records.
        // Time-boxed rather than run to completion: the flush now drains a whole backlog, and a
        // module that has been offline for a day must not hold up its own shutdown to deliver it.
        // Anything left is still in the outbox and goes out on the next start.
        using var final = new CancellationTokenSource(ShutdownFlushBudget);
        try
        {
            await FlushAsync(final.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Final telemetry flush ran out of time; the remainder goes out on the next start.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Final telemetry flush failed; records remain queued for next start.");
        }
    }

    /// <summary>
    /// Delivers both output streams. They are independent by design — utterances carry live
    /// speaker guesses, everything else arrives on its own schedule — so each is marked synced
    /// separately and a failure in one cannot lose the other.
    ///
    /// Each stream is drained to empty before the next is tried, rather than one batch per stream
    /// per tick. A module that has been offline comes back with thousands of queued records, and a
    /// batch every <c>SyncIntervalSeconds</c> would take minutes to work through a backlog the
    /// link can carry in seconds.
    /// </summary>
    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        await DrainAsync(FlushUtterancesAsync, cancellationToken);
        await DrainAsync(FlushRecordsAsync, cancellationToken);
    }

    /// <summary>
    /// Repeats a batch delivery until it comes back short, which is how a stream says it has
    /// nothing left. Cancellation is checked between batches so shutdown does not have to sit
    /// through a long backlog; the batches already delivered stay marked, so the next start
    /// resumes rather than repeats.
    /// </summary>
    private static async Task DrainAsync(
        Func<CancellationToken, Task<int>> flushBatch, CancellationToken cancellationToken)
    {
        int delivered;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            delivered = await flushBatch(cancellationToken);
        }
        while (delivered == BatchSize);
    }

    /// <summary>Delivers one batch and returns how many records it held.</summary>
    private async Task<int> FlushUtterancesAsync(CancellationToken cancellationToken)
    {
        List<TelemetryRecord> records = await _repository.GetUnsyncedAsync(BatchSize, cancellationToken);
        if (records.Count == 0)
        {
            return 0;
        }

        await _sink.DeliverAsync(
            records.Select(r => TelemetryJson.Serialize(r.ToDocument())).ToList(), cancellationToken);

        await _repository.MarkSyncedAsync(
            records.Select(r => r.Id).ToList(), _options.DeleteAfterSync, cancellationToken);

        return records.Count;
    }

    /// <summary>Everything that is not an utterance, stored and forwarded as JSON.</summary>
    private async Task<int> FlushRecordsAsync(CancellationToken cancellationToken)
    {
        List<(string Id, string Json)> records =
            await _repository.GetUnsyncedRecordsAsync(BatchSize, cancellationToken);

        if (records.Count == 0)
        {
            return 0;
        }

        await _sink.DeliverAsync(records.Select(r => r.Json).ToList(), cancellationToken);

        await _repository.MarkRecordsSyncedAsync(
            records.Select(r => r.Id).ToList(), _options.DeleteAfterSync, cancellationToken);

        return records.Count;
    }
}
