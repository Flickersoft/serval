namespace Serval.Server;

/// <summary>
/// A hosted worker that runs one tick per interval, forever: the first tick immediately at
/// startup, a failed tick logged and retried rather than killing the worker, and the interval
/// re-read before every tick so a settings change takes effect without a restart.
/// </summary>
public abstract class PeriodicWorker : BackgroundService
{
    private readonly ILogger _logger;

    protected PeriodicWorker(ILogger logger) => _logger = logger;

    /// <summary>How long to wait between ticks. Read before every tick, so it can track a setting.</summary>
    protected abstract TimeSpan Interval { get; }

    /// <summary>One unit of work.</summary>
    protected abstract Task TickAsync(CancellationToken stoppingToken);

    /// <summary>Named in the retry log line, so a failing worker says which one it is.</summary>
    protected virtual string Activity => GetType().Name;

    /// <summary>
    /// How loudly a failed tick is logged. Error by default; a worker whose failures are routine —
    /// a sidecar that may simply not be running — turns it down.
    /// </summary>
    protected virtual LogLevel FailureLevel => LogLevel.Error;

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                timer.Period = Interval;
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Log(FailureLevel, ex, "{Activity} failed; retrying next interval.", Activity);
            }
        }
        while (await TaskWaits.SafeWaitAsync(timer, stoppingToken));
    }
}

/// <summary>
/// Waits that treat cancellation as an answer rather than an exception, for loops whose exit
/// condition is "keep going?".
/// </summary>
public static class TaskWaits
{
    /// <summary>False on cancellation, so a wait-controlled loop ends without a throw.</summary>
    public static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
