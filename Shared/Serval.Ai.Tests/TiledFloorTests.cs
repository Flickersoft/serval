using Serval.Ai;

namespace Serval.Ai.Tests;

/// <summary>
/// Covering a frame at the detector's native scale instead of shrinking it into one inference.
///
/// <para>Two failures worth guarding, and they pull in opposite directions. Tile too little and an object
/// on a boundary is cut in two and detected as neither half — silently, since nothing reports a gap
/// between tiles. Tile at all on a deployment that did not ask for it and a working system's detection
/// behaviour changes as a side effect of somebody else's accelerator work.</para>
/// </summary>
public class TiledFloorTests
{
    private static readonly DetectorInput Square = new(320, 320, DetectorLayout.Uint8Nhwc, 1f);

    [Fact]
    public void Tiles_cover_every_pixel_of_the_frame()
    {
        // Total coverage is the point of a floor pass. A gap is a strip of the picture that is never
        // examined, and nothing downstream could tell you it exists.
        IReadOnlyList<FrameRegion> tiles = DetectorShapes.Tiles(1536, 432, 320, 320, 0.2);

        Assert.NotEmpty(tiles);
        Assert.Equal(0, tiles.Min(static t => t.X));
        Assert.Equal(0, tiles.Min(static t => t.Y));
        Assert.Equal(1536, tiles.Max(static t => t.X + t.Width));
        Assert.Equal(432, tiles.Max(static t => t.Y + t.Height));
    }

    [Fact]
    public void Neighbouring_tiles_overlap_by_at_least_what_was_asked_for()
    {
        // The subtle one. Tiles that merely abut leave objects on the seam undetectable, so the spacing
        // has to be tighter than the tile width by the requested share.
        IReadOnlyList<FrameRegion> tiles = DetectorShapes.Tiles(1536, 320, 320, 320, 0.25);

        int[] columns = [.. tiles.Select(static t => t.X).Distinct().Order()];

        Assert.True(columns.Length > 1, "a 1536-wide frame needs more than one 320 tile");

        for (int i = 1; i < columns.Length; i++)
        {
            int overlap = 320 - (columns[i] - columns[i - 1]);
            Assert.True(
                overlap >= 320 * 0.25,
                $"tiles at {columns[i - 1]} and {columns[i]} overlap by {overlap}px, under the 80px asked "
                + "for — an object on that seam is detected as neither half");
        }
    }

    [Fact]
    public void A_frame_smaller_than_a_tile_is_one_tile_and_not_a_tile_hanging_off_the_edge()
    {
        IReadOnlyList<FrameRegion> tiles = DetectorShapes.Tiles(320, 240, 320, 320, 0.2);

        FrameRegion only = Assert.Single(tiles);
        Assert.Equal(new FrameRegion(0, 0, 320, 240), only);
    }

    [Fact]
    public void A_panoramic_tiles_across_and_a_portrait_tiles_down()
    {
        // Both of the odd aspects in a real fleet, and the reason a single fixed input shape hurts them.
        IReadOnlyList<FrameRegion> panoramic = DetectorShapes.Tiles(1536, 432, 320, 320, 0.2);
        IReadOnlyList<FrameRegion> doorbell = DetectorShapes.Tiles(480, 640, 320, 320, 0.2);

        Assert.True(
            panoramic.Select(static t => t.X).Distinct().Count()
                > panoramic.Select(static t => t.Y).Distinct().Count(),
            "a 32:9 frame should need more columns than rows");

        Assert.True(
            doorbell.Select(static t => t.Y).Distinct().Count()
                > doorbell.Select(static t => t.X).Distinct().Count(),
            "a 3:4 frame should need more rows than columns");
    }

    [Fact]
    public void An_extreme_overlap_does_not_hang()
    {
        // A step rounded to zero would loop forever. Clamped rather than rejected, because this arrives
        // from configuration.
        Assert.NotEmpty(DetectorShapes.Tiles(1000, 1000, 320, 320, 5.0));
        Assert.NotEmpty(DetectorShapes.Tiles(1000, 1000, 8, 8, 0.99));
    }

    [Fact]
    public void Tiling_is_off_unless_it_is_asked_for()
    {
        // The compatibility guarantee. A deployment that takes this code without changing a setting must
        // keep the single whole-frame floor pass it has always had — even on a camera squeezed badly
        // enough that tiling would help it.
        var options = new RegionOptions();

        Assert.False(options.TiledFloor);
        Assert.False(options.ShouldTileFloor(1536, 432, Square));
    }

    [Fact]
    public void Tiling_stays_off_on_a_camera_that_is_barely_being_squeezed()
    {
        // Asked for, but not worth it here: a frame arriving near native scale gains nothing from tiles
        // and would pay several inferences for the privilege.
        var options = new RegionOptions { TiledFloor = true };
        var input = new DetectorInput(640, 384, DetectorLayout.FloatNchw, 1f / 255f);

        // 640x360 into 640x384 is scale 1.0 — a shape at the camera's own aspect, gain 1.0, nothing to
        // recover by tiling.
        Assert.False(options.ShouldTileFloor(640, 360, input));

        // The panoramic into the same input is squeezed hard enough to qualify.
        Assert.True(options.ShouldTileFloor(1536, 432, input));

        // The same 16:9 frame into a fixed square input is squeezed on the horizontal instead, and a
        // 1.25x recovery is worth taking because none of it is invented.
        Assert.True(options.ShouldTileFloor(640, 360, Square));
    }

    [Fact]
    public void A_sweep_needs_cropping_because_a_tile_alone_cannot_confirm_a_track()
    {
        // The two option predicates disagree — tiling is asked for and cropping is not — and the planner
        // resolves it toward the whole frame. That is not an oversight.
        //
        // A sweep examines a given pixel once per cycle, so the strips outside the tiles' overlap are
        // seen once every six frames here. ObjectTracker drops a tentative track the moment one frame
        // passes without matching it, and confirmation needs consecutive sightings, so a subject seen
        // that rarely never confirms and never becomes an episode. Cropping is what bridges it: a
        // retention crop is planned for every live track, tentative ones included.
        //
        // Six tiles, so this is the spread schedule. A sweep of at most RegionOptions.SweepAtOnce tiles
        // is run whole on every frame instead, covers every pixel every frame, and needs nothing
        // alongside it — see AcquisitionTests.
        var options = new RegionOptions { Mode = RegionMode.Off, TiledFloor = true };

        Assert.True(options.ShouldTileFloor(1536, 432, Square));
        Assert.False(options.ShouldCrop(1536, 432, Square));

        var start = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        IReadOnlyList<FrameRegion> tiles = DetectorShapes.Tiles(1536, 432, 320, 320, 0.2);
        var planner = new RegionPlanner(options);

        PlannedRegion only = Assert.Single(
            planner.Plan(start, 1536, 432, cropping: false, default, 0, 0, [], tiles));

        Assert.Equal(RegionReason.Floor, only.Reason);
        Assert.Equal(new FrameRegion(0, 0, 1536, 432), only.Region);
    }

    [Fact]
    public void A_swept_subject_is_re_sighted_by_a_retention_crop_on_the_next_frame()
    {
        // The acquisition path the coupling above buys. A tile finds something, the tracker holds it as
        // tentative, and the very next frame plans a crop for it — so its sightings are consecutive and
        // it can reach the confirmation ObjectTracker demands, rather than dying while the sweep is
        // looking at another tile.
        var options = new RegionOptions { TiledFloor = true, MaxPerFrame = 3, PaddingFraction = 0 };
        var start = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
        IReadOnlyList<FrameRegion> tiles = DetectorShapes.Tiles(1536, 432, 320, 320, 0.2);
        var planner = new RegionPlanner(options);

        // Frame one sweeps the first tile.
        Assert.Contains(
            RegionReason.Tile,
            planner.Plan(start, 1536, 432, true, default, 0, 0, [], tiles).Select(r => r.Reason));

        // Frame two, with a tentative track where that tile found something.
        var tentative = new TrackedObject(
            1, "cat", new BoundingBox(0.05f, 0.4f, 0.04f, 0.15f), 0.4f,
            TrackState.Tentative, start, start, 1);

        IReadOnlyList<PlannedRegion> next = planner.Plan(
            start.AddSeconds(0.5), 1536, 432, true, default, 0, 0, [tentative], tiles);

        Assert.Contains(RegionReason.Track, next.Select(r => r.Reason));
    }

    private static IReadOnlyList<PlannedRegion> PlanAt(
        RegionPlanner planner, DateTimeOffset at, IReadOnlyList<FrameRegion>? tiles) =>
        planner.Plan(at, 1536, 432, cropping: true, default, 0, 0, [], tiles);

    [Fact]
    public void A_sweep_emits_one_tile_per_frame_and_covers_the_frame_over_several()
    {
        // One per frame, not the whole sweep at once: the guaranteed part of a frame's cost stays at
        // exactly one inference, which is the flat figure InferenceScheduler budgets against.
        var planner = new RegionPlanner(new RegionOptions { FloorSeconds = 5 });
        IReadOnlyList<FrameRegion> tiles = DetectorShapes.Tiles(1536, 432, 320, 320, 0.2);
        var start = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

        var swept = new List<FrameRegion>();

        for (int i = 0; i < tiles.Count; i++)
        {
            IReadOnlyList<PlannedRegion> planned =
                PlanAt(planner, start.AddMilliseconds(500 * i), tiles);

            PlannedRegion tile = Assert.Single(planned, static p => p.Reason == RegionReason.Tile);
            swept.Add(tile.Region);
        }

        Assert.Equal(tiles, swept);
    }

    [Fact]
    public void A_tile_sweep_never_also_asks_for_the_whole_frame()
    {
        // A sweep replaces the whole-frame pass rather than accompanying it. Both would examine the same
        // area twice, which is the thing the un-tiled floor's "run it alone" rule exists to prevent.
        var planner = new RegionPlanner(new RegionOptions { FloorSeconds = 5 });
        IReadOnlyList<FrameRegion> tiles = DetectorShapes.Tiles(1536, 432, 320, 320, 0.2);
        var start = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

        for (int i = 0; i < tiles.Count + 4; i++)
        {
            IReadOnlyList<PlannedRegion> planned =
                PlanAt(planner, start.AddMilliseconds(500 * i), tiles);

            Assert.DoesNotContain(planned, static p => p.Reason == RegionReason.Floor);
        }
    }

    [Fact]
    public void A_finished_sweep_does_not_restart_until_the_floor_is_due_again()
    {
        // Otherwise the sweep becomes the whole schedule: every frame forever spent on coverage, which is
        // the mistake FloorSeconds exists to prevent on the untiled path too.
        //
        // A long interval on purpose, so the sweep finishes well inside it — see the next test for what
        // happens when it does not.
        var planner = new RegionPlanner(new RegionOptions { FloorSeconds = 30 });
        IReadOnlyList<FrameRegion> tiles = DetectorShapes.Tiles(1536, 432, 320, 320, 0.2);
        var start = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

        // Drain the first sweep at 2 fps.
        for (int i = 0; i < tiles.Count; i++)
        {
            PlanAt(planner, start.AddMilliseconds(500 * i), tiles);
        }

        // Sweep done and the interval has not elapsed: no tile, and the frame is left to motion.
        IReadOnlyList<PlannedRegion> between =
            PlanAt(planner, start.AddMilliseconds(500 * tiles.Count), tiles);
        Assert.DoesNotContain(between, static p => p.Reason == RegionReason.Tile);

        // Past it: the next sweep starts.
        IReadOnlyList<PlannedRegion> next = PlanAt(planner, start.AddSeconds(31), tiles);
        Assert.Contains(next, static p => p.Reason == RegionReason.Tile);
    }

    [Fact]
    public void A_sweep_longer_than_the_floor_interval_runs_back_to_back()
    {
        // Worth pinning because it is the real configuration and it surprised me: a 1536x432 frame needs
        // 12 tiles at a 320 input, which is 6 seconds at 2 fps — longer than the default 5s interval. So
        // that camera's coverage runs continuously rather than in bursts, at exactly one reserved
        // inference a frame.
        //
        // That is the honest reading of the guarantee ("cover the frame every FloorSeconds, or as often
        // as possible if it cannot be done that fast") rather than a bug, but it means enabling tiling on
        // a wide camera is a standing cost and not an occasional one.
        var planner = new RegionPlanner(new RegionOptions { FloorSeconds = 5 });
        IReadOnlyList<FrameRegion> tiles = DetectorShapes.Tiles(1536, 432, 320, 320, 0.2);
        var start = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

        Assert.True(tiles.Count * 0.5 > 5, "this test is only meaningful when the sweep outlasts 5s");

        // Two sweeps' worth of frames, every one of which carries a tile.
        for (int i = 0; i < tiles.Count * 2; i++)
        {
            IReadOnlyList<PlannedRegion> planned =
                PlanAt(planner, start.AddMilliseconds(500 * i), tiles);

            Assert.Contains(planned, static p => p.Reason == RegionReason.Tile);
            Assert.Equal(1, planned.Count(static p => p.Reason == RegionReason.Tile));
        }
    }

    [Fact]
    public void Without_tiles_the_planner_behaves_exactly_as_it_did()
    {
        // The regression net for every deployment that does not opt in: null tiles must produce the
        // whole-frame floor, on the same schedule, unchanged.
        var planner = new RegionPlanner(new RegionOptions { FloorSeconds = 5 });
        var start = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

        PlannedRegion floor = Assert.Single(PlanAt(planner, start, null));

        Assert.Equal(RegionReason.Floor, floor.Reason);
        Assert.Equal(new FrameRegion(0, 0, 1536, 432), floor.Region);
    }

    [Fact]
    public void A_single_tile_is_not_treated_as_a_sweep()
    {
        // A frame that fits the input in one tile has nothing to sweep, and should keep the cheap
        // whole-frame floor rather than an equivalent dressed up as a tile.
        var planner = new RegionPlanner(new RegionOptions { FloorSeconds = 5 });
        var start = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

        PlannedRegion only = Assert.Single(
            planner.Plan(start, 320, 320, cropping: true, default, 0, 0, [],
                DetectorShapes.Tiles(320, 320, 320, 320, 0.2)));

        Assert.Equal(RegionReason.Floor, only.Reason);
    }

    [Fact]
    public void A_tile_is_reserved_by_the_scheduler_rather_than_paid_for_from_the_budget()
    {
        // A tile is a coverage guarantee, so it must not be what a tight budget sheds — and MaxPerFrame
        // must not be able to cap it either.
        var scheduler = new InferenceScheduler(budgetPerSecond: 0.0001);
        var at = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

        IReadOnlyList<PlannedRegion> admitted = scheduler.Admit(
            "camera-1",
            [
                new PlannedRegion(new FrameRegion(0, 0, 320, 320), RegionReason.Tile),
                new PlannedRegion(new FrameRegion(400, 0, 320, 320), RegionReason.Motion),
            ],
            at);

        PlannedRegion kept = Assert.Single(admitted);
        Assert.Equal(RegionReason.Tile, kept.Reason);
    }
}
