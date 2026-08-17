namespace Serval.Ai.Tests;

/// <summary>
/// Where the detector is pointed on a given frame.
///
/// The two failures worth guarding are opposite. Proposing too much makes the per-frame cost
/// unpredictable, so a windy hedge starves every other camera. Proposing too little — or, worse,
/// proposing nothing and letting that read as "nothing is there" — closes episodes for objects that
/// never left, which is the failure a plain motion gate has and the reason this is a planner rather
/// than a gate.
/// </summary>
public class RegionPlannerTests
{
    private const int Grid = 64;
    private const int GridHeight = 48;
    private const int FrameWidth = 1280;
    private const int FrameHeight = 720;

    private static readonly DateTimeOffset Start =
        new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    private static RegionOptions Options() => new()
    {
        Mode = RegionMode.On,
        FloorSeconds = 5,
        MaxPerFrame = 3,
        MinCells = 4,
        PaddingFraction = 0,
    };

    /// <summary>A changed-cell mask with one solid rectangle of motion in grid coordinates.</summary>
    private static byte[] Moving(int x, int y, int width, int height)
    {
        var cells = new byte[Grid * GridHeight];

        for (int row = y; row < y + height; row++)
        {
            for (int column = x; column < x + width; column++)
            {
                cells[(row * Grid) + column] = 1;
            }
        }

        return cells;
    }

    private static byte[] Still() => new byte[Grid * GridHeight];

    [Fact]
    public void The_first_frame_examines_the_whole_frame()
    {
        // A camera that has just started has no reference frame and so no motion, but it may well be
        // looking at a car that is already parked. Nothing else would ever find it.
        var planner = new RegionPlanner(Options());

        PlannedRegion only = Assert.Single(
            planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight));

        Assert.Equal(RegionReason.Floor, only.Reason);
        Assert.Equal(new FrameRegion(0, 0, FrameWidth, FrameHeight), only.Region);
    }

    [Fact]
    public void A_still_frame_between_floors_is_examined_not_at_all()
    {
        // Not "examined and found empty". The distinction is the whole design: an empty observation
        // starts the clock on closing every open episode, so reporting one here would say a parked
        // car left because nothing moved near it.
        var planner = new RegionPlanner(Options());
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        Assert.Empty(planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true, Still(), Grid, GridHeight));
    }

    [Fact]
    public void The_whole_frame_comes_back_round_on_the_floor_interval()
    {
        var planner = new RegionPlanner(Options());
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        Assert.Empty(planner.Plan(
            Start.AddSeconds(4.9), FrameWidth, FrameHeight, true, Still(), Grid, GridHeight));

        PlannedRegion only = Assert.Single(planner.Plan(
            Start.AddSeconds(5), FrameWidth, FrameHeight, true, Still(), Grid, GridHeight));

        Assert.Equal(RegionReason.Floor, only.Reason);
    }

    [Fact]
    public void Motion_between_floors_is_examined_as_a_crop()
    {
        var planner = new RegionPlanner(Options());
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        PlannedRegion only = Assert.Single(planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true,
            Moving(8, 8, 4, 4), Grid, GridHeight));

        Assert.Equal(RegionReason.Motion, only.Reason);

        // Grid cells 8..11 of 64 across a 1280px frame are x 160..240 — but a crop that narrow was
        // measured to find nothing at all, so it is grown around that centre. What has to hold is
        // that the crop still contains what moved and is still smaller than the whole frame.
        Assert.InRange(200, only.Region.X, only.Region.X + only.Region.Width);
        Assert.True(only.Region.Width < FrameWidth, "a crop that is the whole frame buys nothing");
    }

    [Fact]
    public void A_crop_magnifies_a_small_subject_without_starving_it_of_context()
    {
        // The argument for regions, and the measured limit on it. A subject occupying a fortieth of
        // the frame is a handful of pixels once the whole frame is squeezed into the model's input;
        // in its own crop it arrives several times larger. But cropping all the way down to the
        // subject was measured to return *nothing* — a detector needs the object whole and something
        // around it — so the magnification is deliberately bounded.
        var planner = new RegionPlanner(Options());
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        PlannedRegion only = Assert.Single(planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true,
            Moving(30, 20, 3, 3), Grid, GridHeight));

        double gain = (double)FrameWidth / only.Region.Width;
        Assert.True(gain >= 3, $"a crop of a small subject only magnified it {gain:0.0}x");
        Assert.True(gain <= 8, $"a crop magnifying {gain:0.0}x is too tight to recognise anything in");
    }

    [Fact]
    public void Separate_movers_get_separate_crops()
    {
        var planner = new RegionPlanner(Options());
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        // Two clusters, far apart, so nothing merges them.
        byte[] cells = Moving(4, 4, 4, 4);
        byte[] second = Moving(40, 30, 4, 4);
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i] |= second[i];
        }

        IReadOnlyList<PlannedRegion> planned = planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true, cells, Grid, GridHeight);

        Assert.Equal(2, planned.Count);
        Assert.All(planned, region => Assert.Equal(RegionReason.Motion, region.Reason));
    }

    [Fact]
    public void Noise_below_the_cluster_floor_proposes_nothing()
    {
        // A single changed cell is sensor noise, a leaf or rain. Cutting a crop for each one is how
        // a windy hedge spends the whole inference budget.
        var planner = new RegionPlanner(Options());
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        Assert.Empty(planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true,
            Moving(10, 10, 1, 1), Grid, GridHeight));
    }

    [Fact]
    public void The_number_of_crops_per_frame_is_capped()
    {
        // What makes the per-frame cost a number that can be computed in advance.
        RegionOptions options = Options();
        options.MaxPerFrame = 2;
        var planner = new RegionPlanner(options);
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        var cells = new byte[Grid * GridHeight];
        for (int i = 0; i < 8; i++)
        {
            byte[] blob = Moving(2 + (i * 7), 4, 3, 3);
            for (int j = 0; j < cells.Length; j++)
            {
                cells[j] |= blob[j];
            }
        }

        Assert.True(
            planner.Plan(Start.AddSeconds(1), FrameWidth, FrameHeight, true, cells, Grid, GridHeight)
                .Count <= 2);
    }

    [Fact]
    public void With_crops_off_the_whole_frame_is_examined_every_frame()
    {
        // This is the guarantee that makes regions safe to turn off: the behaviour is exactly a
        // detector with no region support, examining whole frames as fast as it is fed them.
        //
        // Emphatically *not* one frame every FloorSeconds. The floor is a minimum guarantee for a
        // scheme that would otherwise only look where something moved; read as the whole schedule it
        // caps detection at 0.2 fps, which is far below the rate at which consecutive boxes still
        // overlap — so the tracker associates nothing, every object becomes a one-frame episode, and
        // the entire reason for raising the frame rate is gone. Measured against two real cameras it
        // produced exactly that: two episodes in two minutes, each one frame long.
        var planner = new RegionPlanner(Options());

        foreach (double second in new[] { 0.0, 0.5, 1.0, 1.5, 2.0 })
        {
            PlannedRegion only = Assert.Single(planner.Plan(
                Start.AddSeconds(second), FrameWidth, FrameHeight, false,
                Moving(8, 8, 8, 8), Grid, GridHeight));

            Assert.Equal(RegionReason.Floor, only.Reason);
            Assert.Equal(new FrameRegion(0, 0, FrameWidth, FrameHeight), only.Region);
        }
    }

    [Fact]
    public void With_crops_off_motion_neither_adds_nor_withholds_anything()
    {
        // Motion cannot propose a sub-region without crops, so it has nothing to add — but it must
        // not subtract either. A perfectly still scene is examined exactly as often as a busy one,
        // which is what keeps presence a measurement rather than an inference from silence.
        var planner = new RegionPlanner(Options());
        var still = new byte[Grid * GridHeight];

        Assert.Single(planner.Plan(
            Start, FrameWidth, FrameHeight, false, still, Grid, GridHeight));
        Assert.Single(planner.Plan(
            Start.AddSeconds(0.5), FrameWidth, FrameHeight, false, still, Grid, GridHeight));
    }

    [Theory]
    [InlineData(1280, 720, 320, 192, RegionMode.Auto, true)]    // 4.0x — clearly worth it
    [InlineData(1280, 720, 640, 384, RegionMode.Auto, true)]    // 2.0x
    [InlineData(720, 405, 640, 384, RegionMode.Auto, false)]    // 1.1x — worth nothing
    [InlineData(640, 360, 640, 384, RegionMode.Auto, false)]    // 1.0x
    [InlineData(720, 405, 640, 384, RegionMode.On, true)]       // asked for anyway
    [InlineData(1280, 720, 320, 192, RegionMode.Off, false)]    // declined anyway
    // A 3:4 doorbell into a landscape input: 0.75x across but 1.67x down, and the squeezed axis is
    // the one that decides. Reading width alone declines to crop the camera that needs it most.
    [InlineData(480, 640, 640, 384, RegionMode.Auto, true)]     // 1.67x on the vertical
    // The same doorbell into a shape at its own aspect needs no crop to see properly.
    [InlineData(480, 640, 416, 576, RegionMode.Auto, false)]    // 1.15x
    public void Auto_decides_from_the_frame_to_input_ratio(
        int frameWidth, int frameHeight, int inputWidth, int inputHeight,
        RegionMode mode, bool expected)
    {
        // The ratio is the only thing that matters, and it has nothing to do with which backend is
        // present: it is how much larger a distant subject arrives in a native-resolution crop than
        // in a shrunk whole frame.
        var options = new RegionOptions { Mode = mode };
        var input = new DetectorInput(inputWidth, inputHeight, DetectorLayout.FloatNchw);

        Assert.Equal(expected, options.ShouldCrop(frameWidth, frameHeight, input));
    }

    [Fact]
    public void A_tiny_cluster_is_grown_to_something_recognisable()
    {
        // Measured, not assumed. A 200x200 crop taken tightly around a landscaper beside a truck —
        // at native 4K, subject a comfortable 53 pixels tall — returned nothing at all: not the
        // person, and not the truck filling half of it. A larger crop of the same scene found the
        // truck at 0.84. A detector needs the whole of an object and some of its surroundings, so
        // magnification past that point destroys more evidence than it recovers.
        RegionOptions options = Options();
        options.MinSizeFraction = 0.25;
        var planner = new RegionPlanner(options);
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        PlannedRegion only = Assert.Single(planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true,
            Moving(30, 20, 2, 2), Grid, GridHeight));

        Assert.True(
            only.Region.Width >= FrameWidth / 4,
            $"crop was {only.Region.Width}px wide, too tight to recognise anything in");
        Assert.True(only.Region.Height >= FrameHeight / 4);

        // ...and still a crop, or it would have bought nothing over the whole frame.
        Assert.True(only.Region.Width < FrameWidth);
    }

    [Fact]
    public void A_grown_crop_still_contains_what_moved()
    {
        // Growing around the cluster's own centre rather than from a corner, so the subject does not
        // end up on an edge of the very crop that was cut for it.
        RegionOptions options = Options();
        options.MinSizeFraction = 0.3;
        var planner = new RegionPlanner(options);
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        PlannedRegion only = Assert.Single(planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true,
            Moving(30, 20, 2, 2), Grid, GridHeight));

        // Cells 30-31 of 64 across, 20-21 of 48 down.
        int movedX = 30 * FrameWidth / Grid;
        int movedY = 20 * FrameHeight / GridHeight;

        Assert.InRange(movedX, only.Region.X, only.Region.X + only.Region.Width);
        Assert.InRange(movedY, only.Region.Y, only.Region.Y + only.Region.Height);
    }

    [Fact]
    public void A_grown_crop_at_the_frame_edge_slides_inside_rather_than_clipping()
    {
        // A subject at the edge deserves the same context as one in the middle.
        RegionOptions options = Options();
        options.MinSizeFraction = 0.3;
        var planner = new RegionPlanner(options);
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        PlannedRegion only = Assert.Single(planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true,
            Moving(0, 0, 2, 2), Grid, GridHeight));

        Assert.Equal(0, only.Region.X);
        Assert.Equal(0, only.Region.Y);
        Assert.True(only.Region.Width >= (int)(FrameWidth * 0.3) - 1);
        Assert.True(only.Region.Height >= (int)(FrameHeight * 0.3) - 1);
    }

    [Fact]
    public void A_padded_crop_stays_inside_the_frame()
    {
        // Motion at the very edge pads outwards past the picture; a crop that ran off it would be
        // read out of bounds or clamped into a different shape than the geometry claimed.
        RegionOptions options = Options();
        options.PaddingFraction = 0.1;
        var planner = new RegionPlanner(options);
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        PlannedRegion only = Assert.Single(planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true,
            Moving(0, 0, 4, 4), Grid, GridHeight));

        Assert.True(only.Region.X >= 0);
        Assert.True(only.Region.Y >= 0);
        Assert.True(only.Region.X + only.Region.Width <= FrameWidth);
        Assert.True(only.Region.Y + only.Region.Height <= FrameHeight);
    }

    // --- Tracked objects, the third proposal source ---------------------------------------------
    //
    // Retention, where motion is discovery and the floor is acquisition. Without these a stationary
    // object is examined once every FloorSeconds, which is far too rarely to hold a track: measured
    // on five real cameras it left every parked car's episode being stitched back together by
    // ObjectEventPolicy's rejoin, a backstop standing in for a proposal source that did not exist.

    /// <summary>One track, boxed in frame fractions as the tracker reports them.</summary>
    private static TrackedObject Track(
        float x, float y, TrackState state = TrackState.Confirmed) =>
        new(1, "person", new BoundingBox(x, y, 0.1f, 0.2f), 0.9f, state, Start, Start, 3);

    [Fact]
    public void A_tentative_track_is_looked_for_too()
    {
        // The one the planner most needs another look at. A tentative track is an open question, and
        // a crop is what settles it — planning from confirmed tracks alone means a new object gets no
        // crop until something else happens to find it again, so it can only ever be confirmed on a
        // whole-frame floor pass seconds later.
        var planner = new RegionPlanner(Options());
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        PlannedRegion only = Assert.Single(planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true, Still(), Grid, GridHeight,
            [Track(0.4f, 0.4f, TrackState.Tentative)]));

        Assert.Equal(RegionReason.Track, only.Reason);
    }

    [Fact]
    public void A_coasting_track_is_looked_for_where_it_is_predicted_to_be()
    {
        // Coasting means the detector lost it and the filter is carrying it forward. Re-examining
        // where it is going is what recovers the track before CoastSeconds runs out.
        var planner = new RegionPlanner(Options());
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        PlannedRegion only = Assert.Single(planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true, Still(), Grid, GridHeight,
            [Track(0.4f, 0.4f, TrackState.Coasting)]));

        Assert.Equal(RegionReason.Track, only.Reason);
    }

    [Fact]
    public void A_tracked_object_is_looked_for_on_a_frame_where_nothing_moved()
    {
        // The whole point. A parked car, a person standing still, anything the detector already
        // found: motion proposes nothing for them and the floor is seconds away.
        var planner = new RegionPlanner(Options());
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        PlannedRegion only = Assert.Single(planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true,
            Still(), Grid, GridHeight, [Track(0.4f, 0.4f)]));

        Assert.Equal(RegionReason.Track, only.Reason);

        // Around where the track is, not the whole frame — the magnification is the reason to bother.
        Assert.True(only.Region.Width < FrameWidth);
    }

    [Fact]
    public void A_tracked_object_is_looked_for_every_frame_not_on_the_floor_interval()
    {
        var planner = new RegionPlanner(Options());
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        foreach (double second in new[] { 0.5, 1.0, 1.5, 2.0, 2.5 })
        {
            Assert.Single(planner.Plan(
                Start.AddSeconds(second), FrameWidth, FrameHeight, true,
                Still(), Grid, GridHeight, [Track(0.4f, 0.4f)]));
        }
    }

    [Fact]
    public void Tracks_standing_together_share_one_crop()
    {
        // Three people in a huddle is one patch of ground. A crop each would spend the whole
        // per-frame budget on near-identical views and leave nothing for motion.
        var planner = new RegionPlanner(Options());
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        IReadOnlyList<PlannedRegion> planned = planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true, Still(), Grid, GridHeight,
            [Track(0.40f, 0.40f), Track(0.42f, 0.40f), Track(0.44f, 0.40f)]);

        Assert.Single(planned);
    }

    [Fact]
    public void Tracks_far_apart_get_their_own_crops()
    {
        var planner = new RegionPlanner(Options());
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        IReadOnlyList<PlannedRegion> planned = planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true, Still(), Grid, GridHeight,
            [Track(0.05f, 0.05f), Track(0.80f, 0.70f)]);

        Assert.Equal(2, planned.Count);
        Assert.All(planned, p => Assert.Equal(RegionReason.Track, p.Reason));
    }

    [Fact]
    public void Retention_is_served_before_discovery_when_the_budget_is_tight()
    {
        // Losing a track fragments an episode that already exists; missing a motion cluster costs at
        // worst a later acquisition, which the floor guarantees anyway.
        RegionOptions options = Options();
        options.MaxPerFrame = 2;
        var planner = new RegionPlanner(options);
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        IReadOnlyList<PlannedRegion> planned = planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true,
            Moving(50, 40, 6, 6), Grid, GridHeight,
            [Track(0.05f, 0.05f), Track(0.40f, 0.40f)]);

        Assert.Equal(2, planned.Count);
        Assert.All(planned, p => Assert.Equal(RegionReason.Track, p.Reason));
    }

    [Fact]
    public void Motion_still_gets_a_look_when_there_is_room_for_it()
    {
        var planner = new RegionPlanner(Options());
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        IReadOnlyList<PlannedRegion> planned = planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true,
            Moving(50, 40, 6, 6), Grid, GridHeight, [Track(0.05f, 0.05f)]);

        Assert.Contains(planned, p => p.Reason == RegionReason.Track);
        Assert.Contains(planned, p => p.Reason == RegionReason.Motion);
    }

    [Fact]
    public void A_floor_frame_runs_the_whole_frame_and_nothing_else()
    {
        // The floor already examines everything a crop would, so crops on the same frame only add
        // magnification — and giving that up is what keeps the expensive frame a flat, predictable
        // one the scheduler can budget for exactly.
        var planner = new RegionPlanner(Options());

        PlannedRegion only = Assert.Single(planner.Plan(
            Start, FrameWidth, FrameHeight, true, Moving(50, 40, 6, 6), Grid, GridHeight,
            [Track(0.05f, 0.05f), Track(0.80f, 0.70f)]));

        Assert.Equal(RegionReason.Floor, only.Reason);
        Assert.Equal(new FrameRegion(0, 0, FrameWidth, FrameHeight), only.Region);
    }

    [Fact]
    public void Tracks_propose_nothing_with_crops_off()
    {
        // Without crops the region a track would propose is the whole frame, which is already what
        // every frame runs. Proposing it again would double the work to no effect.
        var planner = new RegionPlanner(Options());

        Assert.Single(planner.Plan(
            Start, FrameWidth, FrameHeight, false, Still(), Grid, GridHeight,
            [Track(0.05f, 0.05f), Track(0.80f, 0.70f)]));
    }

    // --- Masks, applied here rather than only after inference ------------------------------------
    //
    // A shape the operator has said to ignore is a place the detector should never be pointed. The
    // cost of not knowing that is not a filter running late: a car parked on a masked road holds a
    // track, and a track buys a retention crop on every frame for as long as it lives, out of the
    // same MaxPerFrame everything else is competing for.
    //
    // The floor is deliberately never masked. It is the guarantee that presence is a measurement
    // rather than an inference from silence, and a mask is about what is reported, not about which
    // pixels are looked at.

    /// <summary>A rectangular mask in normalised coordinates.</summary>
    private static DetectionMask Mask(
        double left, double top, double right, double bottom, params string[] classes) =>
        new()
        {
            Points = [left, top, right, top, right, bottom, left, bottom],
            Classes = classes.Length == 0 ? null : classes,
        };

    [Fact]
    public void A_track_standing_inside_a_mask_gets_no_retention_crop()
    {
        // The parked car on the public road: without this it costs an inference every frame,
        // forever, for an object the policy throws away every frame, forever.
        var planner = new RegionPlanner(Options(), [Mask(0.2, 0.5, 0.8, 0.9)]);
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        Assert.Empty(planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true, Still(), Grid, GridHeight,
            [Track(0.4f, 0.4f)]));

        Assert.Equal(1, planner.MaskedTracks);
    }

    [Fact]
    public void A_track_beside_a_mask_is_still_looked_for()
    {
        var planner = new RegionPlanner(Options(), [Mask(0.02, 0.5, 0.3, 0.9)]);
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        PlannedRegion only = Assert.Single(planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true, Still(), Grid, GridHeight,
            [Track(0.4f, 0.4f)]));

        Assert.Equal(RegionReason.Track, only.Reason);
        Assert.Equal(0, planner.MaskedTracks);
    }

    [Fact]
    public void A_track_whose_feet_are_inside_but_whose_middle_is_above_gets_no_crop()
    {
        // The same ground point ObjectEventPolicy tests, and for the same reason: someone walking
        // behind a masked hedge has a box whose centre clears it while their feet are firmly inside.
        // The two must agree, or the planner withholds a crop for a subject the policy would report.
        var planner = new RegionPlanner(Options(), [Mask(0.2, 0.55, 0.8, 0.95)]);
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        TrackedObject track = Track(0.4f, 0.4f);
        Assert.False(track.Box.Y + (track.Box.Height / 2) > 0.55f, "the middle clears the mask");

        Assert.Empty(planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true, Still(), Grid, GridHeight, [track]));
    }

    [Fact]
    public void A_motion_cluster_wholly_inside_a_mask_earns_no_crop()
    {
        var planner = new RegionPlanner(Options(), [Mask(0.05, 0.05, 0.6, 0.6)]);
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        Assert.Empty(planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true,
            Moving(16, 12, 4, 4), Grid, GridHeight));

        Assert.Equal(1, planner.MaskedRegions);
    }

    [Fact]
    public void A_motion_cluster_straddling_the_mask_edge_is_still_examined()
    {
        // Someone stepping off the road onto the drive. The cluster is not an object and has no
        // ground point of its own, so the only honest answer is to look and let the policy decide —
        // a crop wrongly withheld is a subject silently never detected.
        var planner = new RegionPlanner(Options(), [Mask(0.05, 0.05, 0.3, 0.6)]);
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        PlannedRegion only = Assert.Single(planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true,
            Moving(16, 12, 4, 4), Grid, GridHeight));

        Assert.Equal(RegionReason.Motion, only.Reason);
        Assert.Equal(0, planner.MaskedRegions);
    }

    [Fact]
    public void A_class_scoped_mask_changes_nothing_the_planner_does()
    {
        // It cannot: the label it needs does not exist until the model has run. Those masks stay
        // entirely a matter for ObjectEventPolicy, and the mask editor says so.
        var planner = new RegionPlanner(Options(), [Mask(0.2, 0.5, 0.8, 0.9, "car")]);
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        PlannedRegion only = Assert.Single(planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true, Still(), Grid, GridHeight,
            [Track(0.4f, 0.4f)]));

        Assert.Equal(RegionReason.Track, only.Reason);
        Assert.Equal(0, planner.MaskedTracks);
    }

    [Fact]
    public void A_mask_too_small_to_enclose_anything_is_ignored()
    {
        var planner = new RegionPlanner(
            Options(), [new DetectionMask { Points = [0.2, 0.5, 0.8, 0.9] }]);
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        Assert.Single(planner.Plan(
            Start.AddSeconds(1), FrameWidth, FrameHeight, true, Still(), Grid, GridHeight,
            [Track(0.4f, 0.4f)]));
    }

    [Fact]
    public void A_masked_view_is_still_examined_on_the_floor_interval()
    {
        // Deliberate. Masking is about what gets reported, and the floor is what keeps "nothing is
        // there" a thing that was measured — including for the unmasked rest of the frame, which the
        // floor is the only proposal source that covers.
        var planner = new RegionPlanner(Options(), [Mask(0.0, 0.0, 1.0, 1.0)]);
        planner.Plan(Start, FrameWidth, FrameHeight, true, Still(), Grid, GridHeight);

        PlannedRegion only = Assert.Single(planner.Plan(
            Start.AddSeconds(6), FrameWidth, FrameHeight, true, Still(), Grid, GridHeight));

        Assert.Equal(RegionReason.Floor, only.Reason);
    }

    // ---- The size ceiling ----
    //
    // A region has always had a floor under its size, so a crop is never too tight to recognise
    // anything in, and until this had no ceiling over it. Unbounded, unions chain: measured over four
    // minutes on a 1536x432 panoramic, every one of 479 retention crops that found something was
    // between 1244 and 1536 pixels wide, where one tracked object should have produced 384. At that
    // scale the detector invents objects, which start tracks, which propose crops that merge — so the
    // runaway sustains itself rather than passing.

    private const int WideWidth = 1536;
    private const int WideHeight = 432;

    /// <summary>The bound a 320² input produces at the default half-scale floor.</summary>
    private static readonly (int Width, int Height) Bound = (640, 640);

    private static RegionOptions Roomy() => new()
    {
        Mode = RegionMode.On,
        FloorSeconds = 5,
        MaxPerFrame = 100,
        MinCells = 4,
        PaddingFraction = 0,
    };

    [Fact]
    public void No_planned_region_is_ever_larger_than_the_bound()
    {
        // The invariant the whole change exists for, asserted over both merge passes at once: several
        // tracks spread across a wide frame, and motion everywhere as well.
        var planner = new RegionPlanner(Roomy());
        planner.Plan(Start, WideWidth, WideHeight, true, Still(), Grid, GridHeight);

        IReadOnlyList<PlannedRegion> planned = planner.Plan(
            Start.AddSeconds(1),
            WideWidth,
            WideHeight,
            true,
            Moving(0, 0, Grid, GridHeight),
            Grid,
            GridHeight,
            [Track(0.1f, 0.6f), Track(0.45f, 0.6f), Track(0.8f, 0.6f)],
            null,
            Bound);

        Assert.NotEmpty(planned);
        Assert.All(planned, region =>
        {
            Assert.True(
                region.Region.Width <= Bound.Width && region.Region.Height <= Bound.Height,
                $"{region.Reason} region {region.Region.Width}x{region.Region.Height} exceeds the bound.");
        });
    }

    [Fact]
    public void Crops_that_would_merge_into_an_oversized_region_stay_separate()
    {
        // The back yard's failure in miniature. Two tracks close enough that their crops overlap, far
        // enough apart that the union spans more than the detector can be shown at a fair scale. Left
        // to merge they become one shrunken look at both; refused, each subject keeps its own.
        var planner = new RegionPlanner(Roomy());
        planner.Plan(Start, WideWidth, WideHeight, true, Still(), Grid, GridHeight);

        IReadOnlyList<PlannedRegion> planned = planner.Plan(
            Start.AddSeconds(1),
            WideWidth,
            WideHeight,
            true,
            Still(),
            Grid,
            GridHeight,
            // Far enough apart that the union spans 691 px, past the 640 bound; close enough that the
            // two 384-wide crops still overlap, so the old code would have folded them.
            [Track(0.30f, 0.6f), Track(0.50f, 0.6f)],
            null,
            Bound);

        Assert.Equal(2, planned.Count);
        Assert.All(planned, region => Assert.Equal(RegionReason.Track, region.Reason));
        Assert.All(planned, region => Assert.True(region.Region.Width <= Bound.Width));
    }

    [Fact]
    public void Crops_that_still_fit_merge_exactly_as_they_always_did()
    {
        // The bound must not cost the merge its reason for existing. Two subjects standing together
        // are still one crop, so a group does not spend the frame's whole budget on near-identical
        // views of the same patch of ground.
        var planner = new RegionPlanner(Roomy());
        planner.Plan(Start, WideWidth, WideHeight, true, Still(), Grid, GridHeight);

        PlannedRegion only = Assert.Single(planner.Plan(
            Start.AddSeconds(1),
            WideWidth,
            WideHeight,
            true,
            Still(),
            Grid,
            GridHeight,
            [Track(0.40f, 0.6f), Track(0.42f, 0.6f)],
            null,
            Bound));

        Assert.Equal(RegionReason.Track, only.Reason);
    }

    [Fact]
    public void A_region_born_oversized_is_cut_into_pieces_that_cover_it()
    {
        // Nothing to un-merge here: one cluster of changed cells covering the scene. The choice is to
        // look at it badly or to look at it in pieces, and the pieces have to actually cover it or the
        // motion that earned the look goes unexamined.
        var planner = new RegionPlanner(Roomy());
        planner.Plan(Start, WideWidth, WideHeight, true, Still(), Grid, GridHeight);

        IReadOnlyList<PlannedRegion> planned = planner.Plan(
            Start.AddSeconds(1),
            WideWidth,
            WideHeight,
            true,
            Moving(0, 0, Grid, GridHeight),
            Grid,
            GridHeight,
            [],
            null,
            Bound);

        Assert.True(planned.Count > 1);
        Assert.All(planned, region => Assert.Equal(RegionReason.Motion, region.Reason));
        Assert.All(planned, region => Assert.True(region.Region.Width <= Bound.Width));

        Assert.Equal(0, planned.Min(region => region.Region.X));
        Assert.Equal(WideWidth, planned.Max(region => region.Region.X + region.Region.Width));
        Assert.Equal(0, planned.Min(region => region.Region.Y));
        Assert.Equal(WideHeight, planned.Max(region => region.Region.Y + region.Region.Height));
    }

    [Fact]
    public void Pieces_of_a_cut_region_stay_sheddable()
    {
        // Not cosmetic. InferenceScheduler reserves the floor and sweep tiles outside the budget and
        // never sheds them, on the strength of each being one inference per camera per frame. A piece
        // labelled Tile because it is tile-shaped would let one camera emit several unsheddable
        // inferences in a frame and break that guarantee.
        var planner = new RegionPlanner(Roomy());
        planner.Plan(Start, WideWidth, WideHeight, true, Still(), Grid, GridHeight);

        IReadOnlyList<PlannedRegion> planned = planner.Plan(
            Start.AddSeconds(1),
            WideWidth,
            WideHeight,
            true,
            Moving(0, 0, Grid, GridHeight),
            Grid,
            GridHeight,
            [],
            null,
            Bound);

        Assert.DoesNotContain(
            planned, region => region.Reason is RegionReason.Floor or RegionReason.Tile);
    }

    [Fact]
    public void The_per_frame_cap_counts_pieces_rather_than_crops()
    {
        // MaxPerFrame exists to make a frame's cost predictable, and a piece costs an inference
        // exactly as a whole crop does. Counting the crop instead would let one oversized cluster
        // spend several times the budget the operator set.
        var planner = new RegionPlanner(Options());
        planner.Plan(Start, WideWidth, WideHeight, true, Still(), Grid, GridHeight);

        IReadOnlyList<PlannedRegion> planned = planner.Plan(
            Start.AddSeconds(1),
            WideWidth,
            WideHeight,
            true,
            Moving(0, 0, Grid, GridHeight),
            Grid,
            GridHeight,
            [],
            null,
            Bound);

        Assert.Equal(3, planned.Count);
    }

    [Fact]
    public void A_camera_whose_frame_already_fits_plans_exactly_as_it_did()
    {
        // The self-scoping property, and the reason this needs no per-backend branch. A 640x360 sub
        // stream into a 320² input is a whole region at half scale, so nothing here can ever bite it;
        // only an ultrawide reaches the bound at all.
        var bounded = new RegionPlanner(Roomy());
        var unbounded = new RegionPlanner(Roomy());

        bounded.Plan(Start, 640, 360, true, Still(), Grid, GridHeight);
        unbounded.Plan(Start, 640, 360, true, Still(), Grid, GridHeight);

        IReadOnlyList<TrackedObject> tracks = [Track(0.1f, 0.6f), Track(0.5f, 0.6f)];

        IReadOnlyList<PlannedRegion> withBound = bounded.Plan(
            Start.AddSeconds(1), 640, 360, true, Moving(4, 4, 20, 12), Grid, GridHeight,
            tracks, null, Bound);

        IReadOnlyList<PlannedRegion> without = unbounded.Plan(
            Start.AddSeconds(1), 640, 360, true, Moving(4, 4, 20, 12), Grid, GridHeight,
            tracks, null, null);

        Assert.Equal(without, withBound);
    }

    [Theory]
    [InlineData(320, 320, 0.5, 640, 640)]
    [InlineData(640, 384, 0.5, 1280, 768)]
    [InlineData(320, 320, 0.25, 1280, 1280)]
    public void The_bound_is_the_input_divided_by_the_scale_floor(
        int inputWidth, int inputHeight, double scale, int expectedWidth, int expectedHeight)
    {
        var options = new RegionOptions { MinRegionScale = scale };

        (int Width, int Height)? max = options.MaxRegion(
            new DetectorInput(inputWidth, inputHeight, DetectorLayout.Uint8Nhwc, 1f));

        Assert.Equal((expectedWidth, expectedHeight), max);
    }

    [Fact]
    public void A_scale_floor_of_zero_switches_the_bound_off()
    {
        // The operator's escape hatch, and what keeps this a policy rather than a rule.
        var options = new RegionOptions { MinRegionScale = 0 };

        Assert.Null(options.MaxRegion(new DetectorInput(320, 320, DetectorLayout.Uint8Nhwc, 1f)));
    }

    // ---- Crops shaped to the detector's aspect ----
    //
    // A crop is sized from the frame and then fitted into an input of another shape, and the remainder
    // is grey. On the panoramic that was 72 % of the model's field. Growing the slack axis costs no
    // resolution, because the scale is set by the axis squeezed hardest, and no extra inference.

    /// <summary>A square input, as the Edge TPU's 320² is.</summary>
    private const double Square = 1.0;

    [Fact]
    public void A_crop_is_grown_to_the_detectors_aspect()
    {
        // 384x108 is what one track on this camera produces. Into a square input it reaches the model
        // as a 320x90 band on grey; grown to 384x384 it fills the input at the identical 0.83x.
        var planner = new RegionPlanner(Roomy());
        planner.Plan(Start, WideWidth, WideHeight, true, Still(), Grid, GridHeight);

        PlannedRegion only = Assert.Single(planner.Plan(
            Start.AddSeconds(1), WideWidth, WideHeight, true, Still(), Grid, GridHeight,
            [Track(0.4f, 0.4f)], null, Bound, Square));

        Assert.Equal(384, only.Region.Width);
        Assert.Equal(384, only.Region.Height);
    }

    [Fact]
    public void Shaping_never_costs_scale()
    {
        // The property that makes this free, asserted rather than argued: the fit of the shaped crop
        // into the input is no worse than the fit of the crop it grew from.
        var planner = new RegionPlanner(Roomy());
        planner.Plan(Start, WideWidth, WideHeight, true, Still(), Grid, GridHeight);

        PlannedRegion shaped = Assert.Single(planner.Plan(
            Start.AddSeconds(1), WideWidth, WideHeight, true, Still(), Grid, GridHeight,
            [Track(0.4f, 0.4f)], null, Bound, Square));

        // 320² into 384x108 fits at 0.83x across and 2.96x down; across is what binds, and shaping
        // leaves it untouched.
        double before = Math.Min(320.0 / 384, 320.0 / 108);
        double after = Math.Min(320.0 / shaped.Region.Width, 320.0 / shaped.Region.Height);

        Assert.Equal(before, after, 6);
    }

    [Fact]
    public void Shaping_cannot_push_a_crop_past_the_bound()
    {
        // The two changes have to compose: the bound has the input's aspect, so growing the slack axis
        // to that ratio lands at most on the bound's own value for it. Asserted with motion everywhere
        // and tracks spread out, where both mechanisms are working at once.
        var planner = new RegionPlanner(Roomy());
        planner.Plan(Start, WideWidth, WideHeight, true, Still(), Grid, GridHeight);

        IReadOnlyList<PlannedRegion> planned = planner.Plan(
            Start.AddSeconds(1), WideWidth, WideHeight, true, Moving(0, 0, Grid, GridHeight),
            Grid, GridHeight, [Track(0.1f, 0.6f), Track(0.5f, 0.6f), Track(0.85f, 0.6f)],
            null, Bound, Square);

        Assert.NotEmpty(planned);
        Assert.All(planned, region => Assert.True(
            region.Region.Width <= Bound.Width && region.Region.Height <= Bound.Height,
            $"{region.Region.Width}x{region.Region.Height} exceeds the bound."));
    }

    [Fact]
    public void A_crop_is_never_shrunk_to_reach_the_aspect()
    {
        // Shrinking would cut away the subject the crop was taken for. On a frame with no room to grow
        // — 432 tall against a crop already 1244 wide — the answer is to leave it alone, not to trim.
        var planner = new RegionPlanner(Roomy());
        planner.Plan(Start, WideWidth, WideHeight, true, Still(), Grid, GridHeight);

        IReadOnlyList<PlannedRegion> planned = planner.Plan(
            Start.AddSeconds(1), WideWidth, WideHeight, true, Moving(0, 0, Grid, GridHeight),
            Grid, GridHeight, [], null, null, Square);

        Assert.All(planned, region => Assert.True(region.Region.Height <= WideHeight));
        Assert.All(planned, region => Assert.True(region.Region.Width >= 1));
    }

    [Fact]
    public void A_crop_already_at_the_detectors_aspect_is_untouched()
    {
        // The no-op that makes this need no per-backend branch, in its own right: a camera whose input
        // already tracks its aspect has nothing here to gain and nothing to lose.
        var shapedPlanner = new RegionPlanner(Roomy());
        var plainPlanner = new RegionPlanner(Roomy());

        shapedPlanner.Plan(Start, 640, 360, true, Still(), Grid, GridHeight);
        plainPlanner.Plan(Start, 640, 360, true, Still(), Grid, GridHeight);

        IReadOnlyList<TrackedObject> tracks = [Track(0.4f, 0.4f)];

        IReadOnlyList<PlannedRegion> shaped = shapedPlanner.Plan(
            Start.AddSeconds(1), 640, 360, true, Still(), Grid, GridHeight,
            tracks, null, null, 640.0 / 360);

        IReadOnlyList<PlannedRegion> plain = plainPlanner.Plan(
            Start.AddSeconds(1), 640, 360, true, Still(), Grid, GridHeight,
            tracks, null, null, 0);

        Assert.Equal(plain, shaped);
    }
}

/// <summary>
/// Lets the tests that predate tracked-object regions read as they always did. A call with no track
/// list is a frame on which the tracker believed nothing was present.
/// </summary>
internal static class RegionPlannerTestExtensions
{
    public static IReadOnlyList<PlannedRegion> Plan(
        this RegionPlanner planner,
        DateTimeOffset now,
        int frameWidth,
        int frameHeight,
        bool cropping,
        ReadOnlySpan<byte> changedCells,
        int gridWidth,
        int gridHeight) =>
        planner.Plan(
            now, frameWidth, frameHeight, cropping, changedCells, gridWidth, gridHeight, []);
}
