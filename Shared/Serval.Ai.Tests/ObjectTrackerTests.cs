namespace Serval.Ai.Tests;

/// <summary>
/// The tracker against synthetic trajectories, which is the only way to test it honestly: real
/// footage cannot say what the right answer *was*, so a test over it can only assert that today's
/// behaviour is today's behaviour.
///
/// Frames are fed at 2 fps throughout — the rate the defaults are for, and the floor below which
/// association fails outright.
/// </summary>
public class ObjectTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private const double Fps = 2.0;

    private static DateTimeOffset At(int frame) => Start.AddSeconds(frame / Fps);

    private static DetectedObject Person(float x, float y, float score = 0.9f) =>
        new("person", score, new BoundingBox(x, y, 0.05f, 0.12f));

    [Fact]
    public void A_single_sighting_is_never_reported()
    {
        // The whole reason tentative tracks exist. A confident one-frame ghost is indistinguishable
        // from a real arrival on its first frame, and this is what stops it opening an episode.
        var tracker = new ObjectTracker(new TrackingOptions());

        IReadOnlyList<TrackedObject> reported = tracker.Update([Person(0.5f, 0.5f)], At(0));

        Assert.Empty(reported);
    }

    [Fact]
    public void A_ghost_seen_once_is_gone_by_the_next_frame()
    {
        var tracker = new ObjectTracker(new TrackingOptions());

        tracker.Update([Person(0.5f, 0.5f)], At(0));

        Assert.Empty(tracker.Update([], At(1)));
        Assert.Empty(tracker.Confirmed);
    }

    [Fact]
    public void A_subject_walking_across_the_view_stays_one_track()
    {
        // 2.5% of the frame per frame — about a walking pace at 2 fps on a driveway camera. If
        // association were done against the last measurement rather than a prediction this would
        // still pass; what it pins is that nothing fragments over a long, ordinary trajectory.
        var tracker = new ObjectTracker(new TrackingOptions());
        var ids = new HashSet<int>();

        for (int frame = 0; frame < 20; frame++)
        {
            foreach (TrackedObject track in tracker.Update([Person(0.1f + (0.025f * frame), 0.5f)], At(frame)))
            {
                ids.Add(track.Id);
            }
        }

        Assert.Single(ids);
    }

    [Fact]
    public void Confirmation_waits_for_the_configured_seconds_rather_than_a_frame_count()
    {
        // The frame-rate coupling this type exists to remove. At 2 fps a one-second confirmation is
        // the third frame; at 5 fps it would be the sixth, and in both cases one second.
        var tracker = new ObjectTracker(new TrackingOptions { ConfirmSeconds = 1.0 });

        Assert.Empty(tracker.Update([Person(0.5f, 0.5f)], At(0)));
        Assert.Empty(tracker.Update([Person(0.5f, 0.5f)], At(1)));
        Assert.Single(tracker.Update([Person(0.5f, 0.5f)], At(2)));
    }

    [Fact]
    public void A_short_occlusion_is_coasted_through_rather_than_split()
    {
        // Somebody walks behind a parked car for a second and comes out the other side. The old
        // class-level model held this together with AbsenceSeconds hysteresis; here it has to be the
        // coast window, and the identity has to survive it.
        var tracker = new ObjectTracker(new TrackingOptions { CoastSeconds = 2.0 });
        int id = 0;

        for (int frame = 0; frame < 4; frame++)
        {
            foreach (TrackedObject track in tracker.Update([Person(0.2f + (0.03f * frame), 0.5f)], At(frame)))
            {
                id = track.Id;
            }
        }

        // Two frames — one second — behind the obstruction.
        Assert.Equal(TrackState.Coasting, Assert.Single(tracker.Update([], At(4))).State);
        Assert.Equal(TrackState.Coasting, Assert.Single(tracker.Update([], At(5))).State);

        // And out the far side, where the prediction should have kept up with them.
        TrackedObject resumed = Assert.Single(tracker.Update([Person(0.2f + (0.03f * 6), 0.5f)], At(6)));

        Assert.Equal(id, resumed.Id);
        Assert.Equal(TrackState.Confirmed, resumed.State);
    }

    [Fact]
    public void An_occlusion_longer_than_the_coast_window_becomes_a_second_track()
    {
        // The loss side of the trade, asserted rather than hidden: past the coast window the tracker
        // gives up, and one subject becomes two episodes. Tune CoastSeconds against real footage —
        // this is the mistake it decides how often to make.
        var tracker = new ObjectTracker(new TrackingOptions { CoastSeconds = 1.0 });
        int first = 0;

        for (int frame = 0; frame < 4; frame++)
        {
            foreach (TrackedObject track in tracker.Update([Person(0.2f, 0.5f)], At(frame)))
            {
                first = track.Id;
            }
        }

        for (int frame = 4; frame < 10; frame++)
        {
            tracker.Update([], At(frame));
        }

        for (int frame = 10; frame < 13; frame++)
        {
            tracker.Update([Person(0.2f, 0.5f)], At(frame));
        }

        Assert.NotEqual(first, Assert.Single(tracker.Confirmed).Id);
    }

    [Fact]
    public void A_stationary_object_holds_one_identity_for_minutes()
    {
        // A parked car. Nothing moves, so the filter must not drift the box off it, and the track
        // must not age out while it is still being seen every frame.
        var tracker = new ObjectTracker(new TrackingOptions());
        var ids = new HashSet<int>();

        for (int frame = 0; frame < 600; frame++)
        {
            var car = new DetectedObject("car", 0.8f, new BoundingBox(0.3f, 0.4f, 0.2f, 0.15f));

            foreach (TrackedObject track in tracker.Update([car], At(frame)))
            {
                ids.Add(track.Id);
            }
        }

        TrackedObject parked = Assert.Single(tracker.Confirmed);

        Assert.Single(ids);
        Assert.Equal(0.3f, parked.Box.X, 2);
        Assert.Equal(0.4f, parked.Box.Y, 2);
    }

    [Fact]
    public void Two_subjects_walking_apart_keep_their_own_identities()
    {
        var tracker = new ObjectTracker(new TrackingOptions());
        var seen = new Dictionary<int, float>();

        for (int frame = 0; frame < 10; frame++)
        {
            IReadOnlyList<DetectedObject> frameObjects =
                [Person(0.4f - (0.02f * frame), 0.5f), Person(0.6f + (0.02f * frame), 0.5f)];

            foreach (TrackedObject track in tracker.Update(frameObjects, At(frame)))
            {
                seen[track.Id] = track.Box.X;
            }
        }

        Assert.Equal(2, seen.Count);
        Assert.Equal(2, tracker.Confirmed.Count());
    }

    [Fact]
    public void A_different_class_in_the_same_place_is_a_different_track()
    {
        // Association is confined to one label, so a car parking where a person stood is never that
        // person. The cost is the reverse case — a detector flickering between car and truck
        // fragments one object — which is a visible fault rather than a silent identity error.
        var tracker = new ObjectTracker(new TrackingOptions());
        var box = new BoundingBox(0.3f, 0.4f, 0.2f, 0.15f);

        for (int frame = 0; frame < 3; frame++)
        {
            tracker.Update([new DetectedObject("person", 0.9f, box)], At(frame));
        }

        int personId = Assert.Single(tracker.Confirmed).Id;

        for (int frame = 3; frame < 6; frame++)
        {
            tracker.Update([new DetectedObject("car", 0.9f, box)], At(frame));
        }

        Assert.DoesNotContain(tracker.Confirmed, t => t.Label == "car" && t.Id == personId);
    }

    [Fact]
    public void A_box_that_does_not_overlap_starts_a_new_track_rather_than_teleporting_an_old_one()
    {
        // What goes wrong at 1 fps, asserted directly: a subject that moves further than its own
        // box between frames has nothing to associate against, and claiming it did would be worse
        // than admitting it did not.
        var tracker = new ObjectTracker(new TrackingOptions());

        for (int frame = 0; frame < 3; frame++)
        {
            tracker.Update([Person(0.1f, 0.5f)], At(frame));
        }

        tracker.Update([Person(0.8f, 0.5f)], At(3));
        tracker.Update([Person(0.8f, 0.5f)], At(4));
        tracker.Update([Person(0.8f, 0.5f)], At(5));

        Assert.Equal(2, tracker.Confirmed.Count(t => t.Label == "person"));
    }

    [Fact]
    public void The_track_list_cannot_grow_past_its_ceiling()
    {
        var tracker = new ObjectTracker(new TrackingOptions { MaxTracks = 4 });

        for (int frame = 0; frame < 3; frame++)
        {
            IReadOnlyList<DetectedObject> crowd =
                [.. Enumerable.Range(0, 40).Select(i => Person(0.01f * i, 0.02f * i))];

            tracker.Update(crowd, At(frame));
        }

        Assert.True(tracker.Confirmed.Count() <= 4);
    }

    [Fact]
    public void The_estimate_leads_a_moving_subject_rather_than_trailing_it()
    {
        // What the velocity term is for. After a few frames at a steady pace, a frame with no
        // detection should predict the subject forward — a filter that only smoothed would leave the
        // box where it last saw them, and the next real detection would then fail to overlap.
        var tracker = new ObjectTracker(new TrackingOptions());

        for (int frame = 0; frame < 6; frame++)
        {
            tracker.Update([Person(0.2f + (0.03f * frame), 0.5f)], At(frame));
        }

        float lastMeasured = 0.2f + (0.03f * 5);
        TrackedObject coasted = Assert.Single(tracker.Update([], At(6)));

        // Bounded on both sides rather than just "moved forward": a filter with a runaway gain also
        // moves forward, and would put the subject somewhere they could not have reached. One step
        // is 0.03, so the estimate belongs near it and nowhere near double it.
        Assert.InRange(coasted.Box.X - lastMeasured, 0.015f, 0.045f);
    }
}
