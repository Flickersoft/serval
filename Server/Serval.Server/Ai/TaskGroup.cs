using System.Runtime.ExceptionServices;

namespace Serval.Server.Ai;

/// <summary>
/// Runs jobs that are only meaningful together.
///
/// <see cref="Task.WhenAll(Task[])"/> is the wrong shape for a set of loops that each run until
/// cancelled: it does not surface a failure until <em>every</em> task has finished, so one job
/// throwing leaves the group waiting on siblings that will never return on their own. A supervisor
/// watching the group then sees a task that is still running and concludes all is well, while the
/// subsystem that actually failed stays dead.
///
/// Here the first job to finish — faulted, cancelled or cleanly — ends the rest, and the original
/// fault is what comes back out. Cancellations induced by that teardown are not the story and are
/// discarded; a cancellation of the caller's own token still propagates, so a supervisor can tell
/// "we were told to stop" from "something broke".
/// </summary>
public static class TaskGroup
{
    /// <summary>
    /// Starts every job against a token linked to <paramref name="cancellationToken"/>, and cancels
    /// that link as soon as any one of them completes.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled.
    /// </exception>
    public static async Task RunUntilFirstCompletesAsync(
        IReadOnlyList<Func<CancellationToken, Task>> jobs,
        CancellationToken cancellationToken)
    {
        if (jobs.Count == 0)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var running = new List<Task>(jobs.Count);
        try
        {
            foreach (Func<CancellationToken, Task> job in jobs)
            {
                running.Add(job(linked.Token));
            }
        }
        catch (Exception ex)
        {
            // A job that threw before it even returned a task. The ones already started still
            // have to be stopped and observed, so fold it in as though it had faulted.
            running.Add(Task.FromException(ex));
        }

        try
        {
            await Task.WhenAny(running);
        }
        finally
        {
            // Whatever happened to the first one, the others exist only to accompany it.
            await linked.CancelAsync();
        }

        List<Exception>? failures = null;

        foreach (Task job in running)
        {
            try
            {
                await job;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A sibling unwinding because we just cancelled it. Not a fault, and never the
                // cause — the job that finished first is.
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        // The caller asked us to stop; say so rather than reporting whatever the jobs threw on
        // the way down, so a supervisor's `when (cancellationToken.IsCancellationRequested)`
        // filter still recognises a clean shutdown.
        cancellationToken.ThrowIfCancellationRequested();

        if (failures is null)
        {
            return;
        }

        // One fault is the common case and is rethrown as itself, so callers can filter on its
        // type. Several at once only happens when jobs fail concurrently during teardown.
        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Throw(failures[0]);
        }

        throw new AggregateException(failures);
    }
}
