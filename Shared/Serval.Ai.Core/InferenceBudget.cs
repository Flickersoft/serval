using System.Diagnostics;

namespace Serval.Ai;

/// <summary>
/// Works out how many inferences a second this host can actually manage, by running some.
///
/// <para>Measured rather than configured: the honest answer differs by more than an order of
/// magnitude across the backends this is written for — a 640² model on an N100's cores, the same
/// model on a desktop's, a 320² graph on an accelerator — so no default is right for two of them,
/// and sizing it by hand means re-measuring on every hardware, model or provider change. Costs a
/// second or so of startup on a path that already loads hundreds of megabytes of weights.</para>
/// </summary>
public static class InferenceBudget
{
    /// <summary>
    /// Times the detector on blank frames and reports how many it manages per second.
    ///
    /// <para>Blank input on purpose: what is being measured is the graph, not the scene. A real
    /// frame would put a scene-dependent number of boxes through postprocessing and make the answer
    /// depend on what the camera happened to be looking at at startup.</para>
    ///
    /// <para>The first run is discarded — arena allocation, kernel compilation and page faults are
    /// one-off costs no subsequent frame pays.</para>
    ///
    /// <para><b>Timed at the backend's own <see cref="IObjectDetector.Concurrency"/>, not one call
    /// at a time.</b> A backend that runs four at once delivers about four times what serial timing
    /// suggests, and the scheduler needs the rate the pipeline will actually see or it sheds work
    /// the host was idle enough to do.</para>
    /// </summary>
    /// <param name="detector">The backend to time. Its declared input shape decides the buffer.</param>
    /// <param name="samples">Timed runs per concurrent slot, after the discarded warm-up.</param>
    /// <returns>Inferences a second, or null if the detector could not be timed at all — in which
    /// case there is no budget rather than a guessed one, and nothing is throttled.</returns>
    public static async Task<double?> MeasureAsync(
        IObjectDetector detector,
        int samples = 5,
        CancellationToken cancellationToken = default)
    {
        // One shape stands in for all of them. Cameras resolve to different shapes, but they resolve
        // to shapes of the same pixel count, and measured across aspects from 3.75 to 0.27 the cost
        // per megapixel is flat — so this number is the host's capacity whatever it is pointed at,
        // and the scheduler needs no per-camera model. 16:9 because it is the commonest, not because
        // it is special.
        DetectorInput input = detector.InputFor(1920, 1080);

        var blank = new byte[input.ByteLength];
        PreparedFrame geometry = PreparedFrame.Whole(input.Width, input.Height, 1f);
        int lanes = Math.Max(detector.Concurrency, 1);
        int runs = Math.Max(samples, 1);

        try
        {
            // Running the lanes at all is load-bearing. An uncontended SemaphoreSlim.WaitAsync
            // completes synchronously and the inference behind it is synchronous work, so a lane
            // started on this thread runs to completion before the next begins — WhenAll over those
            // would time the pool one call at a time.
            //
            // Every lane is warmed, not just one: each session faults in its own arena and compiles
            // its own kernels, and a cold lane inside the timed window under-reports the host.
            await Task.WhenAll(Enumerable.Range(0, lanes).Select(_ => Lane(
                () => detector.DetectPreparedAsync(blank, input, geometry, cancellationToken),
                cancellationToken))).ConfigureAwait(false);

            long started = Stopwatch.GetTimestamp();

            // Lanes share one counter rather than each running a fixed count, because the lanes are
            // not necessarily equal. Under fixed counts the slowest lane sets the wall clock while
            // the fast ones idle out the rest of the window, and the reported figure collapses
            // towards lanes x the slowest — where the throughput the pipeline actually sees is the
            // *sum* of the lanes. Measured on two Edge TPUs, one on USB 3 and one on USB 2, fixed
            // counts reported 153/s against 288/s of real throughput.
            //
            // A shared counter lets a fast lane take more work, so the window ends when the total is
            // done and the wall clock measures aggregate throughput. For equal lanes the two are
            // identical, which is why this cannot regress a symmetric host.
            int total = runs * lanes;
            int claimed = -1;

            await Task.WhenAll(Enumerable.Range(0, lanes).Select(_ => Lane(
                async () =>
                {
                    while (Interlocked.Increment(ref claimed) < total)
                    {
                        await detector.DetectPreparedAsync(blank, input, geometry, cancellationToken)
                            .ConfigureAwait(false);
                    }
                },
                cancellationToken))).ConfigureAwait(false);

            double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;

            return seconds > 0 ? total / seconds : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A backend that will not run a blank frame has bigger problems than its budget, and the
            // first real frame will report them with a stack trace. Refusing to start over a
            // measurement would turn a degraded detector into a dead server.
            return null;
        }
    }

    /// <summary>
    /// Runs one lane on a thread of its own, for as long as the lane lasts.
    ///
    /// <para>A dedicated thread rather than the pool, because a lane holds its thread instead of
    /// yielding it: the wait is uncontended and the inference behind it is synchronous. Pool threads
    /// would make this a reading of how busy the pool was rather than of what the backend can do —
    /// the pool's minimum worker count is the core count, replacements for blocked workers arrive
    /// about one a second, and the measurement runs at startup, which is when models are loading,
    /// Mongo is connecting and an ffmpeg is being spawned per camera. Lanes that cannot all get
    /// threads at once serialise, and a pooled backend then measures at its serial rate.</para>
    ///
    /// <para>That error runs one way — starvation can serialise lanes but cannot invent parallelism —
    /// and the budget is taken once and never revisited, so a squeeze lasting a fraction of a second
    /// throttles detection for the life of the process. It bites hardest where pooling is the point:
    /// an accelerator's call blocks on the device rather than the CPU, which is both why its lanes
    /// should overlap and why they starve the pool while they do. Costs one thread per lane —
    /// <see cref="IObjectDetector.Concurrency"/>, single digits in practice — for the length of a
    /// measurement that already sits behind hundreds of megabytes of weights.</para>
    /// </summary>
    private static Task Lane(Func<Task> body, CancellationToken cancellationToken) =>
        Task.Factory.StartNew(
            body,
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

    /// <summary>
    /// The share of measured capacity detection is allowed to use.
    ///
    /// Deliberately well under all of it. The detector is not alone on the host — ffmpeg is decoding
    /// every camera, the vision model wants seconds of CPU at a time, Mongo is being written to — so
    /// scheduling to the last inference the host can manage makes detection the thing that delays
    /// everything else. Half leaves the machine responsive and still admits several times what a
    /// whole-frame-per-second design ever asked for.
    /// </summary>
    public const double DefaultUtilisation = 0.5;
}
