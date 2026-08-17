using Serval.Server.Ai;

namespace Serval.Server.Tests;

public class TaskGroupTests
{
    /// <summary>A job that never ends on its own — the shape every real caller passes in.</summary>
    private static Func<CancellationToken, Task> Forever(TaskCompletionSource started) =>
        async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };

    [Fact]
    public async Task A_failing_job_ends_the_others_and_surfaces_its_own_exception()
    {
        // The bug this exists to prevent: under Task.WhenAll, one job throwing would wait on
        // siblings that run until cancelled, so an audio session dying left the whole detector
        // hung and its supervisor none the wiser.
        var siblingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var jobs = new List<Func<CancellationToken, Task>>
        {
            async token =>
            {
                await siblingStarted.Task.WaitAsync(token);
                throw new InvalidOperationException("ffmpeg exited with code 1");
            },
            async token =>
            {
                siblingStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                finally
                {
                    siblingEnded.TrySetResult();
                }
            },
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TaskGroup.RunUntilFirstCompletesAsync(jobs, CancellationToken.None));

        Assert.Equal("ffmpeg exited with code 1", thrown.Message);
        await siblingEnded.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_job_returning_cleanly_also_ends_the_others()
    {
        // A camera with no audio track produces an empty stream and exits 0. That is not a fault,
        // but it does mean the group is finished — the supervisor's cue to back off and retry.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var jobs = new List<Func<CancellationToken, Task>>
        {
            Forever(started),
            async _ =>
            {
                await started.Task;
            },
        };

        await TaskGroup
            .RunUntilFirstCompletesAsync(jobs, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Cancelling_the_caller_token_reports_cancellation_not_a_job_fault()
    {
        // RunSupervisedAsync filters on `when (cancellationToken.IsCancellationRequested)` to tell
        // a shutdown from a crash. Reporting a job's teardown IOException here would send it down
        // the restart path during shutdown.
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var jobs = new List<Func<CancellationToken, Task>>
        {
            Forever(started),
            async token =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    throw new IOException("the pipe was already closed");
                }
            },
        };

        Task group = TaskGroup.RunUntilFirstCompletesAsync(jobs, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => group.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_empty_group_is_a_no_op()
    {
        // CameraAiCoordinator builds its list conditionally; a camera that wants neither half
        // must not deadlock on Task.WhenAny of nothing.
        await TaskGroup
            .RunUntilFirstCompletesAsync([], CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}
