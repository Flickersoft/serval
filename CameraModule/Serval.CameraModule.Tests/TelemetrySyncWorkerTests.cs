using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Serval.CameraModule.Tests;

public class TelemetrySyncWorkerTests : IDisposable
{
    /// <summary>The worker's own batch size. A drain has to span more than one of these to mean anything.</summary>
    private const int BatchSize = 100;

    private readonly string _dir;
    private readonly string _dbPath;

    public TelemetrySyncWorkerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "camera-module-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "telemetry.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Counts what it was handed and how many calls it took.</summary>
    private sealed class CountingSink : ITelemetrySink
    {
        private readonly TaskCompletionSource _reachedTarget = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _target;
        private int _records;
        private int _calls;

        public CountingSink(int target) => _target = target;

        public int Records => Volatile.Read(ref _records);

        public int Calls => Volatile.Read(ref _calls);

        public Task ReachedTarget => _reachedTarget.Task;

        public Task DeliverAsync(IReadOnlyList<string> jsonRecords, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (Interlocked.Add(ref _records, jsonRecords.Count) >= _target)
            {
                _reachedTarget.TrySetResult();
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Drains_a_backlog_larger_than_one_batch_in_a_single_pass()
    {
        // The bug: one batch of 100 per stream per tick, then sleep. A backlog left by a network
        // outage then trickled out at 100 per interval — 5,000 queued records took minutes to
        // clear over a link that could carry them in seconds.
        const int Backlog = (BatchSize * 3) + 7;

        var options = new CameraModuleOptions();
        options.Output.DatabasePath = _dbPath;

        // Long enough that a second tick cannot rescue a worker that failed to drain: everything
        // observed below has to come out of the very first pass.
        options.Output.SyncIntervalSeconds = 600;

        var repository = new TelemetryRepository(
            Options.Create(options), NullLogger<TelemetryRepository>.Instance);
        repository.InitializeDatabase();

        for (int i = 0; i < Backlog; i++)
        {
            await repository.SaveAsync(new TelemetryRecord
            {
                Id = $"utterance-{i:D4}",
                Timestamp = DateTimeOffset.UtcNow.AddSeconds(i),
                Transcript = "hello world",
                DurationSeconds = 1.5,
            });
        }

        var sink = new CountingSink(Backlog);
        var worker = new TelemetrySyncWorker(
            repository, sink, Options.Create(options), NullLogger<TelemetrySyncWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Task finished = await Task.WhenAny(sink.ReachedTarget, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(
                finished == sink.ReachedTarget,
                $"Only {sink.Records} of {Backlog} records were delivered in the first pass.");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(Backlog, sink.Records);

        // Four batches: three full ones and the short one that says the queue is empty.
        Assert.Equal(4, sink.Calls);

        // And they are actually marked, so a restart does not resend them.
        Assert.Empty(await repository.GetUnsyncedAsync(BatchSize));
    }

    [Fact]
    public async Task Stops_asking_once_a_stream_is_empty()
    {
        var options = new CameraModuleOptions();
        options.Output.DatabasePath = _dbPath;
        options.Output.SyncIntervalSeconds = 600;

        var repository = new TelemetryRepository(
            Options.Create(options), NullLogger<TelemetryRepository>.Instance);
        repository.InitializeDatabase();

        var sink = new CountingSink(target: 1);
        var worker = new TelemetrySyncWorker(
            repository, sink, Options.Create(options), NullLogger<TelemetrySyncWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        // An empty outbox must not reach the sink at all — a drain loop that mistook "no rows"
        // for "keep going" would spin here instead.
        Assert.Equal(0, sink.Calls);
    }
}
