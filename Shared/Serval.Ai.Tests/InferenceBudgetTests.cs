using System.Collections.Concurrent;
using System.Diagnostics;

namespace Serval.Ai.Tests;

/// <summary>
/// How many inferences a second the host is credited with, which is what
/// <see cref="InferenceScheduler"/> divides between cameras.
///
/// The failure worth guarding is quiet in both directions. Over-report and the scheduler admits work
/// the host cannot finish, so frames queue and arrive stale. Under-report and it sheds work the host
/// was idle enough to do — detections that never happen on a machine with spare capacity, which
/// looks from outside exactly like a scene where nothing occurred.
/// </summary>
public class InferenceBudgetTests
{
    /// <summary>
    /// A backend that takes a fixed time per call and runs at most <paramref name="concurrency"/> of
    /// them at once — the shape of a real pool, with none of the weights.
    /// </summary>
    private sealed class FakeDetector(int concurrency, TimeSpan perCall) : IObjectDetector
    {
        private readonly SemaphoreSlim _gate = new(concurrency, concurrency);

        public string Description => "fake";

        public DetectorInput InputFor(int frameWidth, int frameHeight) =>
            new(64, 64, DetectorLayout.FloatNchw, 1f);

        public int Concurrency => concurrency;

        public int Calls => _calls;

        public async Task<IReadOnlyList<DetectedObject>> DetectPreparedAsync(
            ReadOnlyMemory<byte> prepared,
            DetectorInput input,
            PreparedFrame frame,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);

            try
            {
                Interlocked.Increment(ref _calls);

                // Blocks rather than delays, because that is what a real inference does: it is
                // synchronous CPU work behind an async signature. Task.Delay would yield the thread
                // and let lanes overlap even when the caller never arranged for them to, which is
                // exactly the mistake this fake has to be able to catch.
                Thread.Sleep(perCall);
                return [];
            }
            finally
            {
                _gate.Release();
            }
        }

        private int _calls;

        public void Dispose() => _gate.Dispose();
    }

    [Fact]
    public async Task A_backend_that_runs_several_at_once_is_credited_for_all_of_them()
    {
        // The whole point of measuring at the backend's own concurrency. Timed one call at a time, a
        // pool of four reports a quarter of what the host can do, and the scheduler then sheds three
        // quarters of the work it could have admitted.
        using var serial = new FakeDetector(1, TimeSpan.FromMilliseconds(20));
        using var pooled = new FakeDetector(4, TimeSpan.FromMilliseconds(20));

        double? one = await InferenceBudget.MeasureAsync(
            serial, samples: 5, TestContext.Current.CancellationToken);
        double? four = await InferenceBudget.MeasureAsync(
            pooled, samples: 5, TestContext.Current.CancellationToken);

        Assert.NotNull(one);
        Assert.NotNull(four);

        // Deliberately loose. The claim is "several at once counts for several", not a precise
        // multiple — scheduling on a busy build agent will not deliver 4.0x and does not need to.
        Assert.True(
            four > one * 2,
            $"a four-way pool measured {four:0.#}/s against a serial {one:0.#}/s; concurrency is "
            + "not reaching the budget");
    }

    /// <summary>
    /// A pool whose lanes are <em>not</em> equal — the shape of two Edge TPUs on different USB
    /// generations, where one measured 5.0 ms a call and its twin on a USB 2.0 port measured 13.4 ms
    /// for the identical model.
    ///
    /// Lanes are rented and returned rather than pinned to a caller, exactly as the real pools do, so
    /// a faster lane comes back sooner and naturally serves more calls.
    /// </summary>
    private sealed class AsymmetricFakeDetector : IObjectDetector
    {
        private readonly ConcurrentBag<TimeSpan> _idle;
        private readonly SemaphoreSlim _gate;
        private readonly int _lanes;

        public AsymmetricFakeDetector(params int[] millisecondsPerLane)
        {
            _lanes = millisecondsPerLane.Length;
            _idle = new ConcurrentBag<TimeSpan>(
                millisecondsPerLane.Select(static ms => TimeSpan.FromMilliseconds(ms)));
            _gate = new SemaphoreSlim(_lanes, _lanes);
        }

        public string Description => "asymmetric";

        public int Concurrency => _lanes;

        public DetectorInput InputFor(int frameWidth, int frameHeight) =>
            new(64, 64, DetectorLayout.FloatNchw, 1f);

        public async Task<IReadOnlyList<DetectedObject>> DetectPreparedAsync(
            ReadOnlyMemory<byte> prepared,
            DetectorInput input,
            PreparedFrame frame,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            _idle.TryTake(out TimeSpan cost);

            try
            {
                Thread.Sleep(cost);
                return [];
            }
            finally
            {
                _idle.Add(cost);
                _gate.Release();
            }
        }

        public void Dispose() => _gate.Dispose();
    }

    [Fact]
    public async Task An_asymmetric_pool_is_credited_for_its_fast_lane_too()
    {
        // A 20ms lane beside a 60ms one delivers 50/s + 16.7/s = 66.7/s in steady state. Giving each
        // lane a fixed share of the runs instead makes the slow one set the wall clock while the fast
        // one idles out the rest of the window, which reports about 33/s — half the truth, and half
        // the budget the scheduler then hands out.
        //
        // Measured on the real thing before this was fixed: two Edge TPUs, one per USB generation,
        // reported 153/s against 288/s of actual pooled throughput.
        using var lopsided = new AsymmetricFakeDetector(20, 60);

        double? rate = await InferenceBudget.MeasureAsync(
            lopsided, samples: 10, TestContext.Current.CancellationToken);

        Assert.NotNull(rate);

        // Loose, and one-sided on purpose: the claim is "well clear of the slow lane's ceiling", not a
        // precise 66.7. A fixed share per lane cannot exceed 33/s here however the scheduler behaves.
        Assert.True(
            rate > 45,
            $"an asymmetric pool measured {rate:0.#}/s; a fixed share per lane caps at about 33/s, "
            + "so the fast lane is not being credited");
    }

    [Fact]
    public async Task Every_lane_is_warmed_before_the_clock_starts()
    {
        // Each session in a pool faults in its own arena and compiles its own kernels. A cold lane
        // inside the timed window reports the host as slower than it is, which is the same
        // under-reporting by a subtler route.
        using var pooled = new FakeDetector(4, TimeSpan.FromMilliseconds(5));

        await InferenceBudget.MeasureAsync(
            pooled, samples: 3, TestContext.Current.CancellationToken);

        // Four warm-up calls, then four lanes x three timed runs.
        Assert.Equal(4 + (4 * 3), pooled.Calls);
    }

    [Fact]
    public async Task A_serial_backend_is_measured_at_about_its_call_time()
    {
        using var serial = new FakeDetector(1, TimeSpan.FromMilliseconds(20));

        double? rate = await InferenceBudget.MeasureAsync(
            serial, samples: 5, TestContext.Current.CancellationToken);

        // 20ms a call is 50/s at most; timer granularity and scheduling only ever make it slower.
        Assert.NotNull(rate);
        Assert.InRange(rate.Value, 10, 55);
    }

    [Fact]
    public async Task A_backend_that_cannot_be_timed_gets_no_budget_rather_than_a_guess()
    {
        // Nothing is throttled in that case. Refusing to start over a measurement would turn a
        // degraded detector into a dead server.
        using var broken = new ThrowingDetector();

        Assert.Null(await InferenceBudget.MeasureAsync(
            broken, samples: 3, TestContext.Current.CancellationToken));
    }

    private sealed class ThrowingDetector : IObjectDetector
    {
        public string Description => "broken";

        public DetectorInput InputFor(int frameWidth, int frameHeight) =>
            new(64, 64, DetectorLayout.FloatNchw, 1f);

        public Task<IReadOnlyList<DetectedObject>> DetectPreparedAsync(
            ReadOnlyMemory<byte> prepared,
            DetectorInput input,
            PreparedFrame frame,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no model");

        public void Dispose()
        {
        }
    }

    [Fact]
    public void A_backend_that_says_nothing_about_concurrency_runs_one_at_a_time()
    {
        // The interface default, so a backend written before pooling existed keeps being timed the
        // way it actually behaves.
        using IObjectDetector broken = new ThrowingDetector();

        Assert.Equal(1, broken.Concurrency);
    }
}
