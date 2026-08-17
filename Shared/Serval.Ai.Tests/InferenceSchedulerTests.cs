namespace Serval.Ai.Tests;

/// <summary>
/// Dividing one host's inference capacity between every camera on it.
///
/// The failures here are the ones that only appear under load, which is exactly when nobody is
/// reading logs: one busy camera spending everyone else's budget, a quiet camera banking credit and
/// then monopolising the detector, or — worst — the whole-frame floor being shed to afford another
/// crop of a waving branch, which trades an episode's correctness for recall on noise.
/// </summary>
public class InferenceSchedulerTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly FrameRegion Whole = new(0, 0, 1280, 720);
    private static readonly FrameRegion Crop = new(100, 100, 200, 200);

    private static PlannedRegion Floor() => new(Whole, RegionReason.Floor);

    private static PlannedRegion Motion() => new(Crop, RegionReason.Motion);

    private static List<PlannedRegion> Plan(int floors, int motions)
    {
        List<PlannedRegion> planned = [];
        for (int i = 0; i < floors; i++)
        {
            planned.Add(Floor());
        }

        for (int i = 0; i < motions; i++)
        {
            planned.Add(Motion());
        }

        return planned;
    }

    [Fact]
    public void With_no_budget_everything_is_admitted()
    {
        // The behaviour of a host sized by hand, and what a host whose detector could not be timed
        // falls back to. Nothing throttled is better than everything throttled to a guess.
        var scheduler = new InferenceScheduler(budgetPerSecond: 0);

        Assert.Equal(9, scheduler.Admit("cam", Plan(1, 8), Start).Count);
        Assert.Equal(0, scheduler.Shed);
    }

    [Fact]
    public void The_floor_is_admitted_even_with_no_budget_left()
    {
        // The whole point of reserving it. A frame that is not examined at all is not the same
        // observation as one examined and found empty, and shedding the floor is how the second
        // silently becomes the first — closing the episode of a parked car nothing moved near.
        var scheduler = new InferenceScheduler(budgetPerSecond: 1);

        // Drain the share first.
        scheduler.Admit("cam", Plan(0, 10), Start.AddSeconds(10));

        IReadOnlyList<PlannedRegion> admitted =
            scheduler.Admit("cam", Plan(1, 5), Start.AddSeconds(10));

        Assert.Contains(admitted, region => region.Reason == RegionReason.Floor);
    }

    [Fact]
    public void Motion_crops_are_shed_before_the_floor()
    {
        var scheduler = new InferenceScheduler(budgetPerSecond: 1);
        scheduler.Admit("cam", Plan(0, 10), Start.AddSeconds(10));

        IReadOnlyList<PlannedRegion> admitted =
            scheduler.Admit("cam", Plan(1, 5), Start.AddSeconds(10));

        Assert.Equal(RegionReason.Floor, admitted[0].Reason);
        Assert.True(admitted.Count < 6, "motion crops should have been shed");
        Assert.True(scheduler.Shed > 0);
    }

    [Fact]
    public void A_camera_cannot_spend_more_than_its_share()
    {
        // Ten inferences a second across two cameras is five each, not ten for whoever asks first.
        var scheduler = new InferenceScheduler(budgetPerSecond: 10);

        // Both register, so the share is halved.
        scheduler.Admit("busy", Plan(1, 0), Start);
        scheduler.Admit("quiet", Plan(1, 0), Start);

        // A second of accrual at five a second, then a plan asking for twenty.
        IReadOnlyList<PlannedRegion> admitted =
            scheduler.Admit("busy", Plan(0, 20), Start.AddSeconds(1));

        Assert.True(admitted.Count <= 5, $"admitted {admitted.Count}, which is over the share");
    }

    [Fact]
    public void A_busy_camera_does_not_starve_a_quiet_one()
    {
        // The failure this exists to prevent: a driveway onto a main road proposing crops constantly
        // while the back door, which proposes one, is told there is nothing left.
        var scheduler = new InferenceScheduler(budgetPerSecond: 10);
        scheduler.Admit("busy", Plan(1, 0), Start);
        scheduler.Admit("quiet", Plan(1, 0), Start);

        for (int i = 0; i < 20; i++)
        {
            scheduler.Admit("busy", Plan(0, 10), Start.AddSeconds(1 + (i * 0.05)));
        }

        Assert.NotEmpty(scheduler.Admit("quiet", Plan(0, 2), Start.AddSeconds(2)));
    }

    [Fact]
    public void Quiet_time_does_not_become_unlimited_credit()
    {
        // Without a cap on the bucket, a camera that saw nothing for an hour arrives able to spend
        // an hour of everyone's budget in one frame — which is exactly the spike that would make a
        // host stutter every time a scene finally changed.
        var scheduler = new InferenceScheduler(budgetPerSecond: 10, burstSeconds: 2);
        scheduler.Admit("cam", Plan(1, 0), Start);

        IReadOnlyList<PlannedRegion> admitted =
            scheduler.Admit("cam", Plan(0, 500), Start.AddHours(1));

        Assert.True(admitted.Count <= 20, $"an hour of quiet banked {admitted.Count} inferences");
    }

    [Fact]
    public void A_camera_that_stops_gives_its_share_back()
    {
        // A server that has had twenty cameras over its life must not divide the budget twenty ways
        // forever, leaving the ones still ingesting at a fraction of what the host can do.
        var scheduler = new InferenceScheduler(budgetPerSecond: 10);
        scheduler.Admit("a", Plan(1, 0), Start);
        scheduler.Admit("b", Plan(1, 0), Start);
        scheduler.Admit("c", Plan(1, 0), Start);
        scheduler.Admit("d", Plan(1, 0), Start);

        int shared = scheduler.Admit("a", Plan(0, 20), Start.AddSeconds(1)).Count;

        scheduler.Forget("b");
        scheduler.Forget("c");
        scheduler.Forget("d");

        int alone = scheduler.Admit("a", Plan(0, 20), Start.AddSeconds(3)).Count;

        Assert.True(alone > shared, $"share did not grow back: {shared} then {alone}");
    }

    [Fact]
    public void A_new_camera_starts_empty_rather_than_full()
    {
        // A full bucket on arrival lets a camera fire its whole burst immediately, which is the
        // spike a restart storm produces across every camera at once.
        var scheduler = new InferenceScheduler(budgetPerSecond: 10);

        Assert.Empty(scheduler.Admit("cam", Plan(0, 5), Start));
    }

    [Fact]
    public void An_empty_plan_asks_for_nothing()
    {
        var scheduler = new InferenceScheduler(budgetPerSecond: 10);

        Assert.Empty(scheduler.Admit("cam", [], Start));
        Assert.Equal(0, scheduler.Shed);
    }

    [Fact]
    public void Admission_keeps_the_planners_order()
    {
        // A caller runs what it is given. Reordering would make the log say a detection came from a
        // region it did not.
        var scheduler = new InferenceScheduler(budgetPerSecond: 100);
        scheduler.Admit("cam", Plan(1, 0), Start);

        IReadOnlyList<PlannedRegion> admitted =
            scheduler.Admit("cam", Plan(1, 3), Start.AddSeconds(1));

        Assert.Equal(RegionReason.Floor, admitted[0].Reason);
        Assert.All(admitted.Skip(1), region => Assert.Equal(RegionReason.Motion, region.Reason));
    }
}
