using Serval.Ai;

namespace Serval.Ai.Tests;

/// <summary>
/// Whether a subject that is plainly there ever becomes an episode.
///
/// <para>Everything else about regions is measured one component at a time: the planner is asked what it
/// proposes, the tracker is asked what it confirms. Both can be right while the system detects nothing,
/// because the property that matters spans them — <b>a subject must be examined on enough consecutive
/// frames for a track to confirm.</b> <see cref="ObjectTracker"/> drops a tentative track the moment one
/// frame passes without matching it and will not confirm before
/// <see cref="TrackingOptions.ConfirmSeconds"/>, so a schedule that looks somewhere else in between
/// produces no episodes at all, however good each individual look was.</para>
///
/// <para>The failure is silent and total: a floor that reaches a subject only every few frames leaves
/// every component passing its own tests while the camera records nothing at all.</para>
///
/// <para>These drive the real planner and the real tracker together over the geometries the shipped
/// deployments run, with a subject that never moves — the hardest case, because it generates no motion
/// for a crop to follow and depends entirely on the guaranteed floor.</para>
/// </summary>
public class AcquisitionTests
{
    private const double Fps = 2.0;

    /// <summary>
    /// Every camera geometry in <c>docker-compose.intel-coral.yml</c>, with how long its floor is allowed
    /// to take to believe a subject that arrives and then stands still.
    ///
    /// <para>Two budgets, because there are two kinds of floor and they promise different things. A
    /// <b>continuous</b> floor — the whole frame every frame, or a sweep short enough to run whole —
    /// examines the subject from the moment it arrives, so it owes an answer within
    /// <see cref="TrackingOptions.ConfirmSeconds"/> and a frame or two of slack. An <b>interval</b>
    /// floor sweeps a tile at a time and returns to any given tile once per
    /// <see cref="RegionOptions.FloorSeconds"/>, so in the worst case the subject waits most of that
    /// interval out; between sweeps it is motion crops or nothing. That is the trade a camera makes by
    /// being squeezed hard enough to need many tiles, and the panoramic has run it since it was
    /// installed.</para>
    ///
    /// <para>The distinction is the whole point: a configuration change can move a camera from the
    /// first kind to the second without anything else looking different.</para>
    /// </summary>
    public static TheoryData<string, int, int, int, int, double> Geometries() => new()
    {
        { "16:9 sub stream into a square accelerator", 640, 360, 512, 512, 2.0 },
        { "doorbell, portrait, into a square accelerator", 480, 640, 512, 512, 2.0 },
        { "a camera the detector already matches", 640, 360, 640, 384, 2.0 },
        { "panoramic into a square accelerator", 1536, 432, 512, 512, 6.0 },
    };

    [Theory]
    [MemberData(nameof(Geometries))]
    public void A_still_subject_anywhere_in_the_frame_becomes_an_episode(
        string geometry, int frameWidth, int frameHeight, int inputWidth, int inputHeight,
        double budgetSeconds)
    {
        // Across the frame including both edges, because the edges are exactly what a sweep covers
        // least often — a tile hangs flush to each end, so the outermost strips are in one tile only.
        foreach (double at in new[] { 0.02, 0.25, 0.5, 0.75, 0.95 })
        {
            // Arriving partway through, at a moment falling *after* a sweep has completed rather than
            // at the start of one. A subject present from the first frame is caught by the opening
            // sweep whatever the schedule is, which hides the fault this exists to catch.
            (bool confirmed, double after) =
                Follow(frameWidth, frameHeight, inputWidth, inputHeight, at, appearsAt: 1.5);

            Assert.True(
                confirmed,
                $"{geometry}: a subject at {at:P0} across the frame was never confirmed in 30 s, so it "
                + "never became an episode — the schedule never examined it on enough consecutive "
                + $"frames. Frame {frameWidth}x{frameHeight} into {inputWidth}x{inputHeight}.");

            Assert.True(
                after <= budgetSeconds,
                $"{geometry}: a subject at {at:P0} across the frame took {after:0.0} s to be believed "
                + $"after arriving, against a budget of {budgetSeconds:0.0} s. A camera on the "
                + "continuous budget that slips to the interval one has had its floor thinned — "
                + "whatever else changed, it now misses anything that passes through in a few seconds.");
        }
    }

    /// <summary>
    /// Runs the planner and the tracker against each other for 30 seconds and reports how long after the
    /// subject arrived it was first believed. The subject is only ever detected in a region that wholly
    /// contains it, which is the honest simplification: a detector given a crop that cuts an object in
    /// half rarely returns the whole object, and never returns the box this asserts on.
    /// </summary>
    /// <param name="appearsAt">Seconds into the run before the subject exists at all. Nothing is
    /// detectable before it, and the clock this returns starts here.</param>
    private static (bool Confirmed, double After) Follow(
        int frameWidth, int frameHeight, int inputWidth, int inputHeight, double across,
        double appearsAt = 0)
    {
        var input = new DetectorInput(inputWidth, inputHeight, DetectorLayout.Uint8Nhwc, 1f);
        var regions = new RegionOptions { TiledFloor = true };
        var tracking = new TrackingOptions();

        var planner = new RegionPlanner(regions);
        var tracker = new ObjectTracker(tracking);

        bool cropping = regions.ShouldCrop(frameWidth, frameHeight, input);
        IReadOnlyList<FrameRegion>? tiles = regions.ShouldTileFloor(frameWidth, frameHeight, input)
            ? DetectorShapes.Tiles(
                frameWidth, frameHeight, input.Width, input.Height, regions.TileOverlapFraction)
            : null;

        // A subject about a tenth of the frame across, standing still on the ground line.
        int width = Math.Max(16, frameWidth / 10);
        int height = Math.Max(16, frameHeight / 5);
        int x = Math.Clamp((int)(frameWidth * across) - (width / 2), 0, frameWidth - width);
        int y = (frameHeight - height) / 2;

        var start = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
        int frames = (int)(30 * Fps);

        for (int f = 0; f < frames; f++)
        {
            DateTimeOffset now = start.AddSeconds(f / Fps);

            IReadOnlyList<PlannedRegion> planned = planner.Plan(
                now,
                frameWidth,
                frameHeight,
                cropping,
                // Nothing moves, so nothing suggests where to look. Acquisition is the floor's job.
                ReadOnlySpan<byte>.Empty,
                0,
                0,
                [.. tracker.Live],
                tiles,
                regions.MaxRegion(input),
                (double)input.Width / input.Height,
                regions.MinRegion(input));

            List<DetectedObject> found = [];

            foreach (PlannedRegion region in planned)
            {
                if (f / Fps < appearsAt)
                {
                    break;
                }

                if (region.Region.X <= x
                    && region.Region.Y <= y
                    && region.Region.X + region.Region.Width >= x + width
                    && region.Region.Y + region.Region.Height >= y + height)
                {
                    found.Add(new DetectedObject(
                        "person",
                        0.9f,
                        new BoundingBox(
                            (float)x / frameWidth,
                            (float)y / frameHeight,
                            (float)width / frameWidth,
                            (float)height / frameHeight)));
                }
            }

            // Exactly what the pipeline does with a frame's regions before the tracker sees them.
            tracker.Update(OverlappingRegions.Fold(found), now);

            if (tracker.Live.Any(static t => t.State == TrackState.Confirmed))
            {
                return (true, (f / Fps) - appearsAt);
            }
        }

        return (false, 30);
    }

    [Fact]
    public void A_sweep_short_enough_to_run_whole_covers_the_frame_on_every_frame()
    {
        // The property the fix rests on, stated directly: with a sweep of at most SweepAtOnce tiles,
        // no frame examines less than the whole picture, so the cadence is the one a single shrunken
        // pass gave and the scale is better. Asserted without the tracker so a failure says which of
        // the two halves broke.
        var regions = new RegionOptions { TiledFloor = true };
        var input = new DetectorInput(512, 512, DetectorLayout.Uint8Nhwc, 1f);
        var planner = new RegionPlanner(regions);

        IReadOnlyList<FrameRegion> tiles =
            DetectorShapes.Tiles(640, 360, 512, 512, regions.TileOverlapFraction);

        Assert.Equal(2, tiles.Count);

        var start = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

        for (int f = 0; f < 40; f++)
        {
            IReadOnlyList<PlannedRegion> planned = planner.Plan(
                start.AddSeconds(f / Fps), 640, 360,
                regions.ShouldCrop(640, 360, input),
                ReadOnlySpan<byte>.Empty, 0, 0, [], tiles,
                regions.MaxRegion(input), 1.0, regions.MinRegion(input));

            Assert.Equal(2, planned.Count(static p => p.Reason == RegionReason.Tile));

            for (int column = 0; column < 640; column++)
            {
                Assert.Contains(
                    planned,
                    p => p.Region.X <= column && p.Region.X + p.Region.Width > column);
            }
        }
    }

    [Fact]
    public void A_sweep_too_long_to_span_a_confirmation_cannot_acquire_a_still_subject()
    {
        // A known gap, pinned so it is visible rather than discovered. 1080p into a 640x384 input tiles
        // four ways in each axis, so a spread sweep returns to any given tile once every sixteen frames
        // — eight seconds apart, and never twice running. A subject that never moves generates no motion
        // for a crop to follow, so nothing else looks either, and it is never confirmed.
        //
        // Shipped deployments do not meet this: TiledFloor is off unless asked for, and the geometries
        // that ask for it are in the theory above. What closes it for a camera that hits it is
        // RegionOptions.SweepAtOnce, at the cost of one reserved inference per tile per frame.
        //
        // If this test starts failing, the gap has been closed — delete it and move the geometry up.
        (bool confirmed, _) = Follow(1920, 1080, 640, 384, 0.02, appearsAt: 1.5);

        Assert.False(
            confirmed,
            "a long spread sweep now acquires a still subject at the frame edge, which it could not "
            + "before — the sweep schedule has changed and this limitation no longer holds");
    }

    [Fact]
    public void Turning_crops_on_must_never_thin_the_guarantee_below_a_confirmation()
    {
        // The rule that was broken, as a rule rather than as one camera's numbers: whatever the floor
        // becomes when cropping is switched on, it must still be able to confirm a still subject on its
        // own, because crops follow movement and a still subject makes none.
        var input = new DetectorInput(512, 512, DetectorLayout.Uint8Nhwc, 1f);
        var regions = new RegionOptions { TiledFloor = true, Mode = RegionMode.On };

        Assert.True(regions.ShouldCrop(640, 360, input), "Mode.On must crop, or this proves nothing");

        (bool confirmed, _) = Follow(640, 360, 512, 512, 0.5, appearsAt: 1.5);

        Assert.True(confirmed, "a still subject in the middle of the frame was never confirmed");
    }
}
