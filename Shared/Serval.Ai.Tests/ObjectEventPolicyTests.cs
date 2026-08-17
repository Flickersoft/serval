using Serval.Ai;

namespace Serval.Ai.Tests;

/// <summary>
/// Pins every decision that turns followed objects into stored records. The detector is model
/// configuration that needs weights on disk and the tracker has its own tests; this is where the
/// judgement lives, and all of it is testable with a list of labels and a clock the test controls.
///
/// <para>Most of these hand the policy tracks directly rather than running a real
/// <see cref="ObjectTracker"/>, so that a test about episodes fails for a reason about episodes.
/// The handful that genuinely depend on the two working together are gathered at the end.</para>
/// </summary>
public class ObjectEventPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static DetectionOptions Options(Action<DetectionOptions>? configure = null)
    {
        var options = new DetectionOptions();
        configure?.Invoke(options);
        return options;
    }

    private static ObjectEventPolicy Policy(Action<DetectionOptions>? configure = null)
    {
        int n = 0;
        return new ObjectEventPolicy(Options(configure), () => $"id-{++n}");
    }

    /// <summary>One confirmed track, wherever the test does not care where it is.</summary>
    private static TrackedObject Seen(
        int id,
        string label,
        float score,
        DateTimeOffset? since = null) =>
        new(id, label, new BoundingBox(0.1f, 0.2f, 0.3f, 0.4f), score, TrackState.Confirmed,
            since ?? T0, since ?? T0, 2);

    /// <summary>
    /// One confirmed track that has been followed for a while: <paramref name="hits"/> sightings
    /// since <paramref name="since"/>, for the tests about an episode opening on a track that
    /// already has a history.
    /// </summary>
    private static TrackedObject Long(
        int id,
        string label,
        float score,
        DateTimeOffset since,
        int hits) =>
        new(id, label, new BoundingBox(0.1f, 0.2f, 0.3f, 0.4f), score, TrackState.Confirmed,
            since, since, hits);

    /// <summary>One confirmed track at a place the test chose.</summary>
    private static TrackedObject At(
        int id,
        string label,
        float score,
        float x,
        float y,
        TrackState state = TrackState.Confirmed,
        DateTimeOffset? since = null) =>
        new(id, label, new BoundingBox(x, y, 0.05f, 0.10f), score, state,
            since ?? T0, since ?? T0, 2);

    [Fact]
    public void A_confirmed_track_opens_an_episode_on_the_frame_the_policy_first_sees_it()
    {
        // The tracker has already required two sightings and ConfirmSeconds of them. Asking for
        // more evidence here would be the same gate twice, and would delay every record by it.
        var policy = Policy();

        IReadOnlyList<ObjectEpisode> live = policy.Observe([Seen(1, "person", 0.9f)], T0).Live;

        ObjectEpisode episode = Assert.Single(live);
        Assert.Equal("person", episode.Label);
        Assert.Null(episode.EndedAt);
        Assert.True(policy.HasOpenEpisodes);
    }

    [Fact]
    public void An_episode_is_dated_from_when_the_object_turned_up_not_from_its_confirmation()
    {
        // Confirmation is how certainty is earned, not a claim about when the thing arrived. Dating
        // the episode from the frame that satisfied it would make every start late by
        // ConfirmSeconds — the same error as ending one when the absence window expired instead of
        // at the last sighting.
        var policy = Policy();

        ObjectEpisode episode = policy.Observe(
            [Seen(1, "person", 0.8f, since: T0)],
            T0.AddSeconds(2)).Live[0];

        Assert.Equal(T0, episode.StartedAt);
    }

    [Fact]
    public void Presence_in_the_middle_of_an_episode_publishes_nothing()
    {
        // The whole point of episodes: a person standing there for a minute is one record, not
        // sixty. Volume is three orders of magnitude, so this is the load-bearing assertion.
        //
        // It is also what separates the two lists. Every one of those frames reports where the
        // person is, because something has to draw a box around them; none of them is a record,
        // because nothing about the episode has changed.
        var policy = Policy();
        policy.Observe([Seen(1, "person", 0.8f)], T0);

        for (int i = 1; i < 60; i++)
        {
            ObjectObservation observed =
                policy.Observe([Seen(1, "person", 0.85f)], T0.AddSeconds(i));

            Assert.Empty(observed.Published);
            Assert.Single(observed.Live);
        }

        Assert.Equal(1, policy.Opened);
        Assert.Equal(0, policy.Closed);
    }

    [Fact]
    public void A_brief_absence_does_not_close_the_episode()
    {
        // A person turning sideways drops below the floor constantly. Without hysteresis each
        // flicker would end one episode and start another.
        var policy = Policy(o => o.AbsenceSeconds = 5.0);
        policy.Observe([Seen(1, "person", 0.8f)], T0);
        policy.Observe([Seen(1, "person", 0.9f)], T0.AddSeconds(1));

        Assert.Empty(policy.Observe([], T0.AddSeconds(3)).Published);
        Assert.Empty(policy.Observe([Seen(1, "person", 0.9f)], T0.AddSeconds(4)).Published);
        Assert.Equal(0, policy.Closed);
    }

    [Fact]
    public void Absence_past_the_window_closes_the_episode_at_the_last_sighting()
    {
        // Ends when it was last seen, not when the wait expired. Otherwise every episode's
        // duration is inflated by AbsenceSeconds.
        var policy = Policy(o => o.AbsenceSeconds = 5.0);
        policy.Observe([Seen(1, "person", 0.8f)], T0);
        policy.Observe([Seen(1, "person", 0.9f)], T0.AddSeconds(1));

        IReadOnlyList<ObjectEpisode> published = policy.Observe([], T0.AddSeconds(7)).Published;

        ObjectEpisode episode = Assert.Single(published);
        Assert.Equal(T0.AddSeconds(1), episode.EndedAt);
        Assert.False(policy.HasOpenEpisodes);
    }

    [Fact]
    public void An_episode_keeps_its_id_across_open_and_close()
    {
        // The two writes are one record. A new id on close would double every episode in storage.
        var policy = Policy(o => o.AbsenceSeconds = 5.0);
        ObjectEpisode opened = policy.Observe([Seen(1, "person", 0.9f)], T0).Live[0];

        ObjectEpisode closed = policy.Observe([], T0.AddSeconds(30)).Published[0];

        Assert.Equal(opened.Id, closed.Id);
    }

    [Fact]
    public void The_peak_sighting_and_its_moment_survive_to_the_closed_record()
    {
        // The per-frame detail is summarised rather than discarded: a consumer can go back to the
        // exact snapshot the best look came from.
        var policy = Policy(o => o.AbsenceSeconds = 5.0);
        policy.Observe([At(1, "person", 0.6f, 0.1f, 0.5f)], T0);
        policy.Observe([At(1, "person", 0.7f, 0.2f, 0.5f)], T0.AddSeconds(1));
        policy.Observe([At(1, "person", 0.97f, 0.5f, 0.5f)], T0.AddSeconds(2));
        policy.Observe([At(1, "person", 0.7f, 0.6f, 0.5f)], T0.AddSeconds(3));

        ObjectEpisode closed = policy.Observe([], T0.AddSeconds(30)).Published[0];

        Assert.Equal(0.97f, closed.PeakConfidence);
        Assert.Equal(T0.AddSeconds(2), closed.PeakFrameAt);
        Assert.Equal(0.5f, closed.BestBox!.Value.Box.X);

        // Five, not four: the helper's tracks arrive with two sightings already behind them, and
        // FrameCount counts every frame the object was detected on rather than only the ones after
        // the policy was told about it.
        Assert.Equal(5, closed.FrameCount);
    }

    // --- One object, not one class --------------------------------------------------------------

    [Fact]
    public void Three_people_are_three_episodes()
    {
        // The headline of following objects rather than classes. Each has its own start, its own
        // duration and its own path, which is what a feed row has to be about for any of them to
        // mean anything.
        var policy = Policy();

        IReadOnlyList<ObjectEpisode> live = policy.Observe(
            [
                At(1, "person", 0.8f, 0.1f, 0.5f),
                At(2, "person", 0.7f, 0.5f, 0.5f),
                At(3, "person", 0.9f, 0.8f, 0.5f),
            ],
            T0).Live;

        Assert.Equal(3, live.Count);
        Assert.Equal(3, live.Select(e => e.Id).Distinct().Count());
        Assert.All(live, e => Assert.Equal("person", e.Label));
        Assert.All(live, e => Assert.NotNull(e.BestBox));
        Assert.Equal(3, policy.Opened);
    }

    [Fact]
    public void An_episode_not_reported_this_frame_still_says_where_it_last_was()
    {
        // Between CoastSeconds and AbsenceSeconds the tracker has stopped predicting and the record
        // has not given up, so the episode has no measurement to offer. It offers the last position
        // known instead, paired with a null track sample — two facts, so a consumer can draw where
        // the subject was *and* show that nothing was seen there this frame. Sending no box at all
        // made those indistinguishable and left the overlay blank while the row still read
        // "still there".
        var policy = Policy(o => o.AbsenceSeconds = 30.0);

        policy.Observe([At(1, "person", 0.9f, 0.4f, 0.5f)], T0);

        ObjectEpisode orphaned = Assert.Single(
            policy.Observe([], T0.AddSeconds(5)).Live);

        Assert.Null(orphaned.EndedAt);
        Assert.NotNull(orphaned.BestBox);
        Assert.Equal(0.4f, orphaned.BestBox!.Value.Box.X);

        // The sample is the measurement, and there was none.
        Assert.Null(Assert.Single(orphaned.Track).Box);
    }

    [Fact]
    public void A_reported_episode_says_the_same_box_twice_over()
    {
        // The other side of the pair: on a frame the object *was* seen, the position and the sample
        // agree, which is what makes a null sample mean something.
        var policy = Policy();

        ObjectEpisode live = Assert.Single(
            policy.Observe([At(1, "person", 0.9f, 0.4f, 0.5f)], T0).Live);

        Assert.Equal(0.4f, live.BestBox!.Value.Box.X);
        Assert.Equal(0.4f, Assert.Single(live.Track).Box!.Value.Box.X);
    }

    [Fact]
    public void A_last_known_position_does_not_outlive_the_absence_window()
    {
        // The bound on the whole idea: the box persists for as long as the record insists the
        // subject has not left, and not one frame longer. Past AbsenceSeconds the episode closes
        // and stops appearing in Live at all, so there is nothing left to draw.
        var policy = Policy(o => o.AbsenceSeconds = 5.0);

        policy.Observe([At(1, "person", 0.9f, 0.4f, 0.5f)], T0);

        Assert.Single(policy.Observe([], T0.AddSeconds(4)).Live);

        ObjectObservation gone = policy.Observe([], T0.AddSeconds(6));

        Assert.Empty(gone.Live);
        Assert.NotNull(Assert.Single(gone.Published).EndedAt);
    }

    [Fact]
    public void One_of_several_leaving_closes_only_its_own_episode()
    {
        // Three people, one walks off. The other two are unaffected — under class-level episodes
        // there was one record for all three and nothing could express this at all.
        var policy = Policy(o => o.AbsenceSeconds = 5.0);

        TrackedObject[] three =
        [
            At(1, "person", 0.9f, 0.1f, 0.5f),
            At(2, "person", 0.8f, 0.5f, 0.5f),
            At(3, "person", 0.7f, 0.8f, 0.5f),
        ];

        policy.Observe(three, T0);
        policy.Observe(three, T0.AddSeconds(1));

        // Track 3 is gone; the others carry on standing where they were.
        for (int i = 2; i < 10; i++)
        {
            policy.Observe([three[0], three[1]], T0.AddSeconds(i));
        }

        Assert.Equal(1, policy.Closed);
        Assert.Equal(2, policy.Observe([three[0], three[1]], T0.AddSeconds(10)).Live.Count);
    }

    [Fact]
    public void Each_episode_carries_only_its_own_object()
    {
        // Two people walking in opposite directions. Each track's samples follow that person, so a
        // consumer drawing one episode draws one box that goes one way.
        var policy = Policy();

        for (int i = 0; i < 5; i++)
        {
            policy.Observe(
                [
                    At(1, "person", 0.9f, 0.1f + (0.1f * i), 0.5f),
                    At(2, "person", 0.8f, 0.9f - (0.1f * i), 0.5f),
                ],
                T0.AddSeconds(i));
        }

        IReadOnlyList<ObjectEpisode> closed = policy.Finalise(T0.AddSeconds(5));

        Assert.Equal(2, closed.Count);
        Assert.All(closed, e => Assert.All(e.Track, s => Assert.NotNull(s.Box)));

        ObjectEpisode rightwards = closed.Single(e => e.Track[0].Box!.Value.Box.X < 0.5f);
        Assert.Equal(
            [0.1f, 0.2f, 0.3f, 0.4f, 0.5f],
            [.. rightwards.Track.Select(s => s.Box!.Value.Box.X)]);
    }

    [Fact]
    public void Different_labels_are_independent_episodes()
    {
        var policy = Policy();

        IReadOnlyList<ObjectEpisode> live = policy.Observe(
            [Seen(1, "person", 0.9f), Seen(2, "car", 0.9f)], T0).Live;

        Assert.Equal(2, live.Count);
        Assert.Contains(live, e => e.Label == "person");
        Assert.Contains(live, e => e.Label == "car");
    }

    // --- Rejoining ------------------------------------------------------------------------------

    [Fact]
    public void An_object_reacquired_where_it_was_rejoins_its_episode()
    {
        // The measurement this whole design has to keep: a distant static object sits on its
        // confidence threshold and drops out for a few frames at a time all day. Every
        // re-acquisition is a new track id, so without this each flicker would be a fresh episode
        // — the 263-episodes-instead-of-36 failure, back again through a different door.
        var policy = Policy(o => o.AbsenceSeconds = 30);

        policy.Observe([At(1, "car", 0.9f, 0.30f, 0.40f)], T0);
        policy.Observe([], T0.AddSeconds(1));
        policy.Observe([], T0.AddSeconds(2));

        // Same place, new id — the tracker gave up and re-acquired it.
        ObjectObservation back = policy.Observe([At(2, "car", 0.9f, 0.30f, 0.40f)], T0.AddSeconds(3));

        Assert.Empty(back.Published);
        Assert.Equal(1, policy.Opened);
        Assert.Equal(1, policy.Rejoined);
        Assert.Single(back.Live);
    }

    [Fact]
    public void An_object_reappearing_somewhere_else_gets_its_own_episode()
    {
        // The other half. Someone who walked out of shot and came back is a second visit, and the
        // record should say so — which is exactly what per-object episodes are for.
        var policy = Policy(o => o.AbsenceSeconds = 30);

        policy.Observe([At(1, "person", 0.9f, 0.10f, 0.50f)], T0);
        policy.Observe([], T0.AddSeconds(1));

        policy.Observe([At(2, "person", 0.9f, 0.80f, 0.50f)], T0.AddSeconds(2));

        Assert.Equal(2, policy.Opened);
        Assert.Equal(0, policy.Rejoined);
    }

    [Fact]
    public void A_rejoined_episode_keeps_its_start_and_its_id()
    {
        // The point of rejoining rather than opening: the record has to stay one record, covering
        // the whole time the object was there.
        var policy = Policy(o => o.AbsenceSeconds = 30);

        ObjectEpisode opened = policy.Observe([At(1, "car", 0.9f, 0.30f, 0.40f)], T0).Live[0];
        policy.Observe([], T0.AddSeconds(1));
        policy.Observe([At(2, "car", 0.9f, 0.30f, 0.40f)], T0.AddSeconds(2));

        ObjectEpisode closed = Assert.Single(policy.Finalise(T0.AddSeconds(3)));

        Assert.Equal(opened.Id, closed.Id);
        Assert.Equal(T0, closed.StartedAt);
    }

    [Fact]
    public void Rejoining_never_steals_an_episode_that_is_still_being_followed()
    {
        // Two people standing next to each other, one of whom the tracker re-acquires. Its new
        // track must not adopt the episode belonging to the one that was never lost, however much
        // the two boxes overlap.
        var policy = Policy(o => o.AbsenceSeconds = 30);

        TrackedObject standing = At(1, "person", 0.9f, 0.30f, 0.40f);

        policy.Observe([standing, At(2, "person", 0.8f, 0.31f, 0.40f)], T0);
        Assert.Equal(2, policy.Opened);

        // Track 2 dies and comes back as track 3, overlapping both.
        ObjectObservation back = policy.Observe(
            [standing, At(3, "person", 0.8f, 0.31f, 0.40f)],
            T0.AddSeconds(1));

        Assert.Equal(2, back.Live.Count);
        Assert.Equal(2, policy.Opened);
        Assert.Equal(1, policy.Rejoined);
    }

    [Fact]
    public void A_rejoin_only_matches_the_same_class()
    {
        // A car leaving and a person standing where it was are two things, not one thing that
        // changed species.
        var policy = Policy(o => o.AbsenceSeconds = 30);

        policy.Observe([At(1, "car", 0.9f, 0.30f, 0.40f)], T0);
        policy.Observe([], T0.AddSeconds(1));
        policy.Observe([At(2, "person", 0.9f, 0.30f, 0.40f)], T0.AddSeconds(2));

        Assert.Equal(2, policy.Opened);
        Assert.Equal(0, policy.Rejoined);
    }

    // --- Coasting -------------------------------------------------------------------------------

    [Fact]
    public void A_coasting_frame_keeps_the_episode_open_without_counting_as_evidence()
    {
        // A coasting track is where the filter predicts, not where anything was seen. It has to
        // hold the episode open — that is what carries somebody behind a parked car — but counting
        // it would inflate FrameCount, which is how a consumer tells a solid sighting from an
        // intermittent one.
        var policy = Policy();

        policy.Observe([At(1, "person", 0.9f, 0.10f, 0.50f)], T0);
        policy.Observe([At(1, "person", 0.9f, 0.20f, 0.50f, TrackState.Coasting)], T0.AddSeconds(1));
        policy.Observe([At(1, "person", 0.9f, 0.30f, 0.50f, TrackState.Coasting)], T0.AddSeconds(2));

        ObjectEpisode closed = Assert.Single(policy.Finalise(T0.AddSeconds(3)));

        // Two — the sightings the tracker confirmed on — and neither of the two coasting frames.
        Assert.Equal(2, closed.FrameCount);

        // Ends at the last frame it was actually seen on, so the absence window is counted from
        // there rather than from wherever the prediction ran out.
        Assert.Equal(T0, closed.EndedAt);
    }

    [Fact]
    public void A_coasting_frame_cannot_set_the_peak()
    {
        // The peak names a snapshot a consumer can go and look at. Dating it to a frame nobody
        // looked at the object in would send them to a picture that does not show what it claims.
        var policy = Policy();

        policy.Observe([At(1, "person", 0.5f, 0.10f, 0.50f)], T0);
        policy.Observe([At(1, "person", 0.99f, 0.20f, 0.50f, TrackState.Coasting)], T0.AddSeconds(1));

        ObjectEpisode closed = Assert.Single(policy.Finalise(T0.AddSeconds(2)));

        Assert.Equal(0.5f, closed.PeakConfidence);
        Assert.Equal(T0, closed.PeakFrameAt);
    }

    [Fact]
    public void A_coasting_position_is_still_drawn()
    {
        // Within the coast window there genuinely is an estimate, and a box that blinks out every
        // time the detector misses a frame is worse to watch than one that carries on for a second.
        var policy = Policy();

        policy.Observe([At(1, "person", 0.9f, 0.10f, 0.50f)], T0);
        ObjectEpisode live = policy
            .Observe([At(1, "person", 0.9f, 0.30f, 0.50f, TrackState.Coasting)], T0.AddSeconds(1))
            .Live[0];

        Assert.Equal(0.30f, Assert.Single(live.Track).Box!.Value.Box.X);
    }

    // --- Filtering ------------------------------------------------------------------------------

    [Fact]
    public void A_class_outside_the_allowlist_never_reaches_an_episode()
    {
        // COCO has 80 classes and a security camera reporting a toaster is reporting a mistake.
        var policy = Policy(o => o.Classes = ["person"]);

        policy.Observe([Seen(1, "toaster", 0.99f)], T0);
        Assert.Empty(policy.Observe([Seen(1, "toaster", 0.99f)], T0.AddSeconds(1)).Live);
        Assert.Equal(2, policy.SuppressedByClass);
    }

    [Fact]
    public void A_track_under_this_cameras_floor_is_not_a_sighting()
    {
        var policy = Policy(o => o.ScoreThreshold = 0.5f);

        policy.Observe([Seen(1, "person", 0.4f)], T0);
        Assert.Empty(policy.Observe([Seen(1, "person", 0.4f)], T0.AddSeconds(1)).Live);
        Assert.Equal(2, policy.BelowThreshold);
        Assert.Equal(0, policy.Opened);
    }

    [Fact]
    public void A_track_smaller_than_the_size_floor_is_dropped_however_sure_the_model_is()
    {
        // The failure this exists for, from a real drive: a lamp post 6.7 x 13.5 px in a 1920-wide
        // frame, reported as a person five times at up to 0.41 once a region crop had magnified it.
        // Confidence is the wrong tool — crops make the model *more* certain of a speck — so the
        // gate has to be geometric and has to run whatever the score says.
        var policy = Policy(o => o.MinObjectFraction = 0.0001);

        var lampPost = new TrackedObject(
            1, "person", new BoundingBox(0.8937f, 0.1467f, 0.0035f, 0.0125f), 0.41f,
            TrackState.Confirmed, T0, T0, 3);

        Assert.Empty(policy.Observe([lampPost], T0).Live);
        Assert.Equal(1, policy.TooSmall);
        Assert.Equal(0, policy.Opened);

        // And not counted against the confidence floor, which it never reached.
        Assert.Equal(0, policy.BelowThreshold);
    }

    [Fact]
    public void The_distant_car_the_crops_existed_to_find_still_gets_through()
    {
        // The other half, and the reason the floor is a fraction rather than a flat "ignore small
        // things". On the same footage a distant car covered 0.000496 of the frame against the lamp
        // post's 0.000044 — an order of magnitude apart, so one floor separates them cleanly.
        var policy = Policy(o => o.MinObjectFraction = 0.0001);

        var distantCar = new TrackedObject(
            1, "car", new BoundingBox(0.9762f, 0.1723f, 0.0237f, 0.0209f), 0.49f,
            TrackState.Confirmed, T0, T0, 3);

        Assert.Single(policy.Observe([distantCar], T0).Live);
        Assert.Equal(0, policy.TooSmall);
    }

    [Fact]
    public void No_size_floor_is_the_default_and_drops_nothing()
    {
        // How much distance a camera gives up is the operator's call, not a default: a porch and a
        // drive watching the road disagree completely about what is too small to care about.
        var policy = Policy();

        var speck = new TrackedObject(
            1, "person", new BoundingBox(0.5f, 0.5f, 0.0035f, 0.0125f), 0.41f,
            TrackState.Confirmed, T0, T0, 3);

        Assert.Single(policy.Observe([speck], T0).Live);
        Assert.Equal(0, policy.TooSmall);
    }

    [Fact]
    public void A_score_that_dips_below_the_floor_for_a_frame_does_not_fragment_the_episode()
    {
        // The track survives — it is the policy's own floor that rejected it — so when the score
        // recovers the same id arrives back and finds its own episode still open.
        var policy = Policy(o => { o.ScoreThreshold = 0.5f; o.AbsenceSeconds = 30; });

        policy.Observe([Seen(1, "person", 0.9f)], T0);
        policy.Observe([Seen(1, "person", 0.4f)], T0.AddSeconds(1));
        policy.Observe([Seen(1, "person", 0.9f)], T0.AddSeconds(2));

        Assert.Equal(1, policy.Opened);
        Assert.Equal(0, policy.Rejoined);
        Assert.Equal(0, policy.Closed);
    }

    // --- A track that outlives its own episode ---------------------------------------------------

    [Fact]
    public void A_second_episode_on_a_living_track_is_dated_from_the_first_ones_close()
    {
        // An object sitting exactly on the camera's floor is followed by the tracker on the
        // sub-floor detections this policy discards, so its episode closes while its track lives
        // on. Dating the next episode from the track's birth backdates it — which is how one
        // blanket became a hundred and ninety-five records all claiming the same start.
        var policy = Policy(o =>
        {
            o.ScoreThreshold = 0.6f;
            o.AbsenceSeconds = 10.0;
        });

        policy.Observe([Long(1, "person", 0.65f, T0, hits: 2)], T0);
        policy.Observe([Long(1, "person", 0.65f, T0, hits: 12)], T0.AddSeconds(5));

        // Under the floor: the policy stops seeing it, the tracker does not.
        ObjectEpisode first = policy
            .Observe([Long(1, "person", 0.55f, T0, hits: 40)], T0.AddSeconds(20))
            .Published[0];

        Assert.Equal(T0, first.StartedAt);
        Assert.Equal(T0.AddSeconds(5), first.EndedAt);

        // Back over the floor on the same track, which this policy has no state for any more.
        DateTimeOffset back = T0.AddSeconds(40);
        ObjectEpisode second = policy.Observe([Long(1, "person", 0.65f, T0, hits: 60)], back).Live[0];

        Assert.Equal(1, policy.Continued);
        Assert.NotEqual(first.Id, second.Id);

        // Dated from where the first one ended, not from where the track was born.
        Assert.Equal(first.EndedAt, second.StartedAt);
        Assert.NotEqual(T0, second.StartedAt);

        // And describing only its own stretch. A continuation seeds nothing, so this counts
        // the one frame it has actually been seen on rather than the track's sixty.
        Assert.Equal(1, second.FrameCount);
    }

    [Fact]
    public void A_track_the_policy_has_never_seen_is_still_dated_from_when_it_turned_up()
    {
        // The continuation rule must not reach a genuinely new track. Nothing has closed on this
        // id, so the episode is dated from the track's own first sighting as it always was.
        var policy = Policy();

        ObjectEpisode episode = policy
            .Observe([Long(1, "person", 0.9f, T0, hits: 5)], T0.AddSeconds(3)).Live[0];

        Assert.Equal(0, policy.Continued);
        Assert.Equal(T0, episode.StartedAt);

        // Four sightings credited from the tracker plus this frame: a genuinely new track still
        // gets the evidence that earned its confirmation.
        Assert.Equal(5, episode.FrameCount);
    }

    [Fact]
    public void A_continuation_is_remembered_for_longer_than_a_departure()
    {
        // The two windows answer different questions. A departure stops mattering once something
        // has been gone long enough to be news again; whether a track already had an episode stops
        // mattering only when the track dies, which is not observable from here.
        var policy = Policy(o =>
        {
            o.ScoreThreshold = 0.6f;
            o.AbsenceSeconds = 5.0;
            o.NoveltySeconds = 30.0;
            o.MaxEpisodeSeconds = 3600.0;
        });

        // Watching starts here, so a track born later has been observably absent and can arrive.
        policy.Observe([], T0);

        DateTimeOffset born = T0.AddSeconds(100);
        policy.Observe([Long(1, "person", 0.65f, born, hits: 2)], born);
        policy.Observe([Long(1, "person", 0.65f, born, hits: 12)], born.AddSeconds(5));

        ObjectEpisode first = policy
            .Observe([Long(1, "person", 0.5f, born, hits: 20)], born.AddSeconds(15)).Published[0];

        Assert.Equal(born.AddSeconds(5), first.EndedAt);

        // Well past NoveltySeconds, so the departure has been forgotten and this counts as an
        // arrival — but the dating still has to come from the episode that closed.
        ObjectEpisode second = policy
            .Observe([Long(1, "person", 0.65f, born, hits: 90)], born.AddSeconds(300)).Live[0];

        Assert.True(second.IsArrival);
        Assert.Equal(1, policy.Continued);
        Assert.Equal(first.EndedAt, second.StartedAt);
    }

    // --- Hard cut -------------------------------------------------------------------------------

    [Fact]
    public void Something_that_never_leaves_is_cut_and_reopened()
    {
        // A car parked in view is genuinely present for days. Without the cut it is one episode
        // that never closes, and so never becomes a complete record of anything.
        var policy = Policy(o => o.MaxEpisodeSeconds = 10.0);
        ObjectEpisode first = policy.Observe([Seen(1, "car", 0.9f)], T0).Live[0];

        ObjectObservation observed = policy.Observe([Seen(1, "car", 0.9f)], T0.AddSeconds(11));

        ObjectEpisode closed = Assert.Single(observed.Published);
        ObjectEpisode continuation = Assert.Single(observed.Live);

        Assert.Equal(first.Id, closed.Id);
        Assert.NotNull(closed.EndedAt);
        Assert.Null(continuation.EndedAt);
        Assert.NotEqual(first.Id, continuation.Id);

        // The continuation is bookkeeping. Nothing turned up — the clock ran out — so it must not
        // ask for a description. Measured against real footage, treating these as events made the
        // object gate noisier than the motion gate it replaces.
        Assert.False(continuation.IsArrival);
    }

    [Fact]
    public void The_hard_cut_waits_for_a_frame_the_object_was_actually_seen_on()
    {
        // The cut asserts the object is still there, and a coasting track is a prediction rather
        // than that assertion. Cutting on one would open a continuation whose peak — the frame a
        // consumer is told to go and look at — is a frame nobody looked at the object in.
        var policy = Policy(o => o.MaxEpisodeSeconds = 5);

        policy.Observe([At(1, "car", 0.9f, 0.30f, 0.40f)], T0);

        ObjectObservation coasting = policy.Observe(
            [At(1, "car", 0.99f, 0.30f, 0.40f, TrackState.Coasting)],
            T0.AddSeconds(6));

        Assert.Empty(coasting.Published);
        Assert.Equal(1, policy.Opened);

        ObjectObservation seen =
            policy.Observe([At(1, "car", 0.5f, 0.30f, 0.40f)], T0.AddSeconds(7));

        ObjectEpisode closed = Assert.Single(seen.Published);
        Assert.Equal(0.9f, closed.PeakConfidence);
        Assert.Equal(0.5f, Assert.Single(seen.Live).PeakConfidence);
    }

    [Fact]
    public void A_reopened_episode_does_not_need_confirming_again()
    {
        // The thing is demonstrably still there — making it re-qualify would leave a gap in the
        // record for something that never left.
        var policy = Policy(o => o.MaxEpisodeSeconds = 10.0);
        policy.Observe([Seen(1, "car", 0.9f)], T0);

        IReadOnlyList<ObjectEpisode> live = policy.Observe([Seen(1, "car", 0.9f)], T0.AddSeconds(11)).Live;

        Assert.Null(Assert.Single(live).EndedAt);
        Assert.True(policy.HasOpenEpisodes);
    }

    [Fact]
    public void Finalise_closes_what_is_open_so_a_restart_strands_nothing()
    {
        // Without this every deploy leaves records claiming, forever, that someone is still there.
        var policy = Policy();
        policy.Observe([Seen(1, "person", 0.9f)], T0);

        ObjectEpisode closed = Assert.Single(policy.Finalise(T0.AddSeconds(5)));

        Assert.NotNull(closed.EndedAt);
        Assert.False(policy.HasOpenEpisodes);
        Assert.Empty(policy.Finalise(T0.AddSeconds(6)));
    }

    // --- Alerts ---------------------------------------------------------------------------------

    [Fact]
    public void An_alert_class_below_its_own_floor_opens_an_ordinary_episode()
    {
        // A false alert costs trust in every alert after it, so the bar for claiming one is above
        // the bar for recording one.
        var policy = Policy(o =>
        {
            o.AlertClasses = ["person"];
            o.AlertMinConfidence = 0.9f;
            o.ScoreThreshold = 0.25f;
        });

        ObjectEpisode episode = policy.Observe([Seen(1, "person", 0.5f)], T0).Live[0];

        Assert.False(episode.IsAlert);
    }

    [Fact]
    public void Only_an_arrival_raises_an_alert()
    {
        // Presence is not news, and an alert on it is the whole feed: a bedroom camera that decides
        // a blanket is a person raises one every time the score crosses the floor, for as long as
        // the bed is made. The policy already works out whether anything actually turned up.
        var policy = Policy(o =>
        {
            o.AlertClasses = ["person"];
            o.AlertMinConfidence = 0.6f;
            o.NoveltySeconds = 60.0;
        });

        // In shot from the moment this camera was first watched, so no absence was ever observed
        // for it.
        ObjectEpisode inventory = policy
            .Observe([At(1, "person", 0.95f, 0.10f, 0.20f)], T0).Live[0];

        Assert.False(inventory.IsArrival);
        Assert.False(inventory.IsAlert);

        // Turning up well after watching began, somewhere nothing of its class just left from.
        DateTimeOffset later = T0.AddSeconds(120);
        ObjectEpisode arrival = Assert.Single(
            policy.Observe([At(2, "person", 0.95f, 0.70f, 0.60f, since: later)], later).Live);

        Assert.True(arrival.IsArrival);
        Assert.True(arrival.IsAlert);
    }

    [Fact]
    public void An_alert_is_decided_at_open_and_does_not_change_later()
    {
        // A record that quietly became an alert after someone read it would be worse than one that
        // never claimed to be.
        var policy = Policy(o =>
        {
            o.AlertClasses = ["person"];
            o.AlertMinConfidence = 0.9f;
            o.AbsenceSeconds = 5.0;
        });

        policy.Observe([Seen(1, "person", 0.5f)], T0);
        policy.Observe([Seen(1, "person", 0.99f)], T0.AddSeconds(2));

        ObjectEpisode closed = policy.Observe([], T0.AddSeconds(30)).Published[0];

        Assert.False(closed.IsAlert);
        Assert.Equal(0.99f, closed.PeakConfidence);
    }

    [Fact]
    public void A_per_camera_allowlist_never_writes_through_to_the_shared_defaults()
    {
        // Copy() clones the arrays rather than aliasing them, so tuning one camera cannot retune
        // every other one — the hazard AudioGateOptions.Copy exists for.
        var global = new DetectionOptions { Classes = ["person", "car"] };
        string[] before = [.. global.Classes];

        DetectionOptions camera = global.Copy();
        camera.Classes[0] = "giraffe";

        Assert.Equal(before, global.Classes);
        Assert.NotEqual(global.Classes[0], camera.Classes[0]);
    }

    [Fact]
    public void A_per_camera_tracking_setting_never_writes_through_either()
    {
        var global = new DetectionOptions();
        double before = global.Tracking.ConfirmSeconds;

        DetectionOptions camera = global.Copy();
        camera.Tracking.ConfirmSeconds = before + 5;

        Assert.Equal(before, global.Tracking.ConfirmSeconds);
    }

    // --- Arrival vs presence ------------------------------------------------------------------
    //
    // Measured against 88 minutes of real footage, a policy that treated every opened episode as
    // something to describe asked for 47 descriptions where the motion gate it replaces asked for
    // 7. The cause was not detection quality: a driveway sees a parked car in 2648 of 2648 frames
    // and a living room sees a couch in 2645 of 2645. These pin the rule that fixed it.

    [Fact]
    public void Scenery_the_camera_opened_on_is_inventory_rather_than_an_arrival()
    {
        // A car already parked in shot when watching began. It never turned up, so there is no
        // moment to describe, and it must not ask for one.
        var policy = Policy();

        ObjectEpisode episode = policy.Observe([Seen(1, "car", 0.9f)], T0).Live[0];

        Assert.False(episode.IsArrival);
        Assert.Equal(1, policy.Opened);
        Assert.Equal(0, policy.Arrivals);
    }

    [Fact]
    public void Something_first_seen_after_being_observably_absent_is_an_arrival()
    {
        // The other half of the same rule. By the time this person appears the camera has watched
        // an empty scene for ten minutes, so their appearance is a transition and worth describing.
        var policy = Policy(o => o.NoveltySeconds = 120);

        policy.Observe([], T0);
        ObjectEpisode episode = policy.Observe(
            [Seen(1, "person", 0.9f, since: T0.AddSeconds(600))],
            T0.AddSeconds(601)).Live[0];

        Assert.True(episode.IsArrival);
        Assert.Equal(1, policy.Arrivals);
    }

    [Fact]
    public void An_object_that_leaves_and_returns_much_later_arrives_again()
    {
        var policy = Policy(o => { o.NoveltySeconds = 120; o.AbsenceSeconds = 5; });

        policy.Observe([], T0);
        policy.Observe([At(1, "car", 0.9f, 0.1f, 0.5f, since: T0.AddSeconds(300))], T0.AddSeconds(300));
        policy.Observe([], T0.AddSeconds(400));

        ObjectEpisode again = policy.Observe(
            [At(2, "car", 0.9f, 0.8f, 0.5f, since: T0.AddSeconds(900))],
            T0.AddSeconds(900)).Live[0];

        Assert.True(again.IsArrival);
        Assert.Equal(2, policy.Arrivals);
    }

    [Fact]
    public void A_flickering_object_never_re_announces_itself_even_when_it_cannot_rejoin()
    {
        // Belt and braces on the noisiest failure there is. Rejoining stops most flicker becoming
        // new episodes; where it cannot — a gap past the absence window — the class-level novelty
        // rule is what stops the new episode claiming to be news.
        var policy = Policy(o => { o.NoveltySeconds = 120; o.AbsenceSeconds = 5; });

        policy.Observe([], T0);
        policy.Observe([At(1, "car", 0.9f, 0.30f, 0.40f, since: T0.AddSeconds(600))], T0.AddSeconds(600));
        Assert.Equal(1, policy.Arrivals);

        for (int cycle = 0; cycle < 5; cycle++)
        {
            int t = 610 + (cycle * 30);
            policy.Observe([], T0.AddSeconds(t));
            policy.Observe([], T0.AddSeconds(t + 6));      // past AbsenceSeconds: the episode closes
            policy.Observe(
                [At(2 + cycle, "car", 0.9f, 0.30f, 0.40f, since: T0.AddSeconds(t + 8))],
                T0.AddSeconds(t + 8));
        }

        // Five more episodes, because it really did drop out and come back five times — but no
        // further arrivals, because it was never gone long enough to be news again.
        Assert.Equal(6, policy.Opened);
        Assert.Equal(1, policy.Arrivals);
    }

    [Fact]
    public void A_long_running_presence_produces_no_arrivals_however_many_times_it_is_cut()
    {
        // The 47-descriptions failure, in miniature: an hour of a parked car under a short hard cut.
        var policy = Policy(o => { o.MaxEpisodeSeconds = 300; o.NoveltySeconds = 120; });

        for (int t = 0; t < 3600; t++)
        {
            policy.Observe([Seen(1, "car", 0.9f)], T0.AddSeconds(t));
        }

        Assert.True(policy.Opened > 1, "the hard cut should still produce bookkeeping records");
        Assert.Equal(0, policy.Arrivals);
    }

    // --- Movement, describe-classes and masking -----------------------------------------------

    [Fact]
    public void A_stationary_object_never_asks_for_a_description()
    {
        // The couch, and the parked car. Present in every frame forever, and never news.
        var policy = Policy(o => { o.Classes = ["car"]; o.DescribeClasses = ["car"]; });

        for (int t = 0; t < 300; t++)
        {
            policy.Observe([At(1, "car", 0.9f, 0.30f, 0.40f)], T0.AddSeconds(t));
        }

        Assert.Equal(0, policy.Described);
    }

    [Fact]
    public void Something_already_in_shot_that_starts_moving_does_ask()
    {
        // The case arrival alone gets wrong: a car parked since before watching began, driving off.
        // It never arrived, so only movement can catch it.
        var policy = Policy(o =>
        {
            o.Classes = ["car"];
            o.DescribeClasses = ["car"];
            o.MinMovementFraction = 0.02;
        });

        for (int t = 0; t < 60; t++)
        {
            policy.Observe([At(1, "car", 0.9f, 0.30f, 0.40f)], T0.AddSeconds(t));
        }

        Assert.Equal(0, policy.Described);

        ObjectObservation moving =
            policy.Observe([At(1, "car", 0.9f, 0.45f, 0.40f)], T0.AddSeconds(60));

        DescriptionTrigger trigger = Assert.Single(moving.Triggers);
        Assert.Equal("car", trigger.Label);
        Assert.Equal(DescribeReason.Movement, trigger.Reason);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(5.0)]
    public void The_same_speed_is_movement_at_every_detect_rate(double fps)
    {
        // A car crossing the frame at 5% of its width per second is moving, and whether the
        // detector is looking once a second or five times cannot be what decides that. Measured per
        // frame it would be: at 5 fps the same car shifts 1% between frames, under a 2% threshold,
        // and movement simply stops being reported with nothing saying so.
        var policy = Policy(o =>
        {
            o.Classes = ["car"];
            o.DescribeClasses = ["car"];
            o.MinMovementFraction = 0.02;
        });

        const double speed = 0.05;
        double step = 1.0 / fps;

        // Long enough to be settled and past any arrival, so only movement can trigger.
        for (int i = 0; i < (int)(60 * fps); i++)
        {
            policy.Observe([At(1, "car", 0.9f, 0.30f, 0.40f)], T0.AddSeconds(i * step));
        }

        long before = policy.Described;

        ObjectObservation moving = policy.Observe(
            [At(1, "car", 0.9f, (float)(0.30 + (speed * step)), 0.40f)],
            T0.AddSeconds(60));

        Assert.Equal(DescribeReason.Movement, Assert.Single(moving.Triggers).Reason);
        Assert.Equal(before + 1, policy.Described);
    }

    [Fact]
    public void Box_jitter_on_a_static_object_is_not_movement()
    {
        // Boxes wobble by a fraction of a percent on a static object at this resolution. Treating
        // that as movement would put every piece of furniture back in the description queue.
        var policy = Policy(o =>
        {
            o.Classes = ["car"];
            o.DescribeClasses = ["car"];
            o.MinMovementFraction = 0.02;
        });

        for (int t = 0; t < 200; t++)
        {
            float jitter = (t % 2 == 0) ? 0.002f : -0.002f;
            policy.Observe([At(1, "car", 0.9f, 0.30f + jitter, 0.40f)], T0.AddSeconds(t));
        }

        Assert.Equal(0, policy.Described);
    }

    [Fact]
    public void Movement_is_only_measured_between_consecutive_frames()
    {
        // An object that vanishes and comes back somewhere else has not been observed moving — it
        // has been observed twice. Calling that movement would fire on every reappearance.
        var policy = Policy(o =>
        {
            o.Classes = ["car"];
            o.DescribeClasses = ["car"];
            o.NoveltySeconds = 100000;   // rule arrival out, to isolate movement
        });

        policy.Observe([At(1, "car", 0.9f, 0.30f, 0.40f)], T0);
        policy.Observe([At(1, "car", 0.9f, 0.30f, 0.40f)], T0.AddSeconds(1));
        policy.Observe([], T0.AddSeconds(2));
        policy.Observe([At(1, "car", 0.9f, 0.80f, 0.40f)], T0.AddSeconds(3));

        Assert.Equal(0, policy.Described);
    }

    [Fact]
    public void A_class_worth_recording_is_not_always_worth_describing()
    {
        // Knowing a car has been on the driveway since 18:00 is worth writing down. Spending
        // seconds of inference to be told about it is not.
        var policy = Policy(o =>
        {
            o.Classes = ["person", "car"];
            o.DescribeClasses = ["person"];
        });

        policy.Observe([], T0);
        policy.Observe(
            [At(1, "car", 0.9f, 0.30f, 0.40f, since: T0.AddSeconds(600))],
            T0.AddSeconds(600));

        Assert.Equal(1, policy.Opened);          // still recorded
        Assert.Equal(0, policy.Described);       // but not described
        Assert.True(policy.SuppressedByDescribeClass > 0);
    }

    [Fact]
    public void One_person_crossing_the_view_is_one_description_not_sixty()
    {
        // Without a cooldown, continuous movement is a description every second — which on a
        // serialised worker is the whole camera's budget spent on one person walking.
        var policy = Policy(o =>
        {
            o.Classes = ["person"];
            o.DescribeClasses = ["person"];
            o.DescribeCooldownSeconds = 60;
        });

        policy.Observe([], T0);
        for (int t = 0; t < 30; t++)
        {
            policy.Observe(
                [At(1, "person", 0.9f, 0.02f + (t * 0.03f), 0.50f, since: T0.AddSeconds(600))],
                T0.AddSeconds(600 + t));
        }

        Assert.Equal(1, policy.Described);
        Assert.True(policy.SuppressedByCooldown > 0);
    }

    [Fact]
    public void Three_people_arriving_together_are_three_records_and_one_description()
    {
        // Where per-object episodes could have gone badly wrong. Each of them is genuinely a
        // separate thing to store; describing the scene three times over would be the noisiest
        // possible reading of that, and a description describes a scene rather than an object.
        var policy = Policy(o =>
        {
            o.Classes = ["person"];
            o.DescribeClasses = ["person"];
            o.NoveltySeconds = 120;
        });

        policy.Observe([], T0);
        ObjectObservation observed = policy.Observe(
            [
                At(1, "person", 0.7f, 0.1f, 0.5f, since: T0.AddSeconds(600)),
                At(2, "person", 0.9f, 0.4f, 0.5f, since: T0.AddSeconds(600)),
                At(3, "person", 0.8f, 0.7f, 0.5f, since: T0.AddSeconds(600)),
            ],
            T0.AddSeconds(600));

        Assert.Equal(3, observed.Live.Count);
        Assert.Equal(3, policy.Arrivals);

        DescriptionTrigger trigger = Assert.Single(observed.Triggers);
        Assert.Equal(DescribeReason.Arrival, trigger.Reason);

        // The most confident of them, so which one is quoted depends on the frame rather than on
        // the order the tracker listed them in.
        Assert.Equal(0.9f, trigger.Confidence);
    }

    [Fact]
    public void A_second_person_turning_up_while_the_first_is_there_is_an_arrival()
    {
        // Novelty is asked of the object, not of its class, and this is why. A camera watching one
        // person for ten minutes has a "person" present in every frame, so "has any person been
        // absent for two minutes" answers no — and a second person walking in would be recorded and
        // never flagged as news. On a drive with a car permanently parked on it, that is every
        // arriving car.
        var policy = Policy(o => o.NoveltySeconds = 120);

        for (int t = 0; t < 600; t++)
        {
            policy.Observe([At(1, "person", 0.95f, 0.10f, 0.50f)], T0.AddSeconds(t));
        }

        Assert.Equal(0, policy.Arrivals);

        ObjectObservation observed = policy.Observe(
            [
                At(1, "person", 0.95f, 0.10f, 0.50f),
                At(2, "person", 0.60f, 0.80f, 0.50f, since: T0.AddSeconds(600)),
            ],
            T0.AddSeconds(600));

        Assert.Equal(2, observed.Live.Count);
        Assert.Equal(2, policy.Opened);
        Assert.Equal(1, policy.Arrivals);
    }

    [Fact]
    public void Something_re_acquired_where_it_left_from_is_not_an_arrival()
    {
        // The other side of asking the object rather than the class, and what keeps the measurement
        // honest. Once the gap outlasts AbsenceSeconds the episode really does close, so the return
        // really is a new object as far as identity goes — and it must still not be news, or a
        // distant thing flickering across its confidence threshold announces itself all day.
        var policy = Policy(o => { o.NoveltySeconds = 120; o.AbsenceSeconds = 5; });

        policy.Observe([], T0);
        policy.Observe(
            [At(1, "car", 0.9f, 0.30f, 0.40f, since: T0.AddSeconds(600))],
            T0.AddSeconds(600));

        Assert.Equal(1, policy.Arrivals);

        // Gone long enough to close, then back in the same place under a new id.
        policy.Observe([], T0.AddSeconds(610));
        policy.Observe(
            [At(2, "car", 0.9f, 0.30f, 0.40f, since: T0.AddSeconds(620))],
            T0.AddSeconds(620));

        Assert.Equal(2, policy.Opened);
        Assert.Equal(1, policy.Closed);
        Assert.Equal(1, policy.Arrivals);
    }

    [Fact]
    public void The_memory_of_where_something_left_expires_with_novelty()
    {
        // A car that leaves and comes back to the same spot three minutes later has been gone long
        // enough for its return to mean something — which is exactly what NoveltySeconds says. The
        // suppression above is for things that never really went away, so it has to expire on the
        // same clock rather than outlive it.
        var policy = Policy(o => { o.NoveltySeconds = 120; o.AbsenceSeconds = 5; });

        policy.Observe([], T0);
        policy.Observe(
            [At(1, "car", 0.9f, 0.30f, 0.40f, since: T0.AddSeconds(600))],
            T0.AddSeconds(600));
        policy.Observe([], T0.AddSeconds(610));

        policy.Observe(
            [At(2, "car", 0.9f, 0.30f, 0.40f, since: T0.AddSeconds(800))],
            T0.AddSeconds(800));

        Assert.Equal(2, policy.Arrivals);
    }

    [Fact]
    public void A_detection_standing_inside_a_masked_region_is_dropped_entirely()
    {
        // The public road past the driveway. The detector is right about those cars; they are just
        // not this camera's business, and no confidence threshold expresses that.
        var policy = Policy(o =>
        {
            o.Classes = ["car"];
            o.DescribeClasses = ["car"];
            o.Masks = [new DetectionMask { Name = "road", Points = [0.0, 0.0, 1.0, 0.0, 1.0, 0.30, 0.0, 0.30] }];
        });

        policy.Observe([], T0);
        // Box at y 0.10 height 0.10 -> feet at 0.20, inside the masked strip.
        policy.Observe([At(1, "car", 0.95f, 0.4f, 0.10f)], T0.AddSeconds(600));
        policy.Observe([At(1, "car", 0.95f, 0.4f, 0.10f)], T0.AddSeconds(601));

        Assert.Equal(0, policy.Opened);
        Assert.Equal(2, policy.SuppressedByMask);
    }

    [Fact]
    public void Masking_tests_the_feet_of_a_box_not_its_middle()
    {
        // Someone walking in front of a masked hedge has a box whose centre is above it and whose
        // feet are inside. Testing the centre would let exactly the traffic a mask exists to remove
        // straight back through.
        var mask = new DetectionMask { Points = [0.0, 0.60, 1.0, 0.60, 1.0, 1.0, 0.0, 1.0] };

        // Box spans y 0.40..0.75: centre 0.575 is outside the mask, feet 0.75 are inside.
        Assert.False(mask.Contains(0.5, 0.575));
        Assert.True(mask.Contains(0.5, 0.75));
    }

    [Fact]
    public void The_box_overload_tests_the_same_point_the_policy_does()
    {
        // RegionPlanner declines to spend a crop on a track this returns true for, so the two must
        // agree exactly rather than approximately: a planner that skipped a box the policy would
        // have kept is a subject silently never detected, with nothing anywhere saying so.
        var mask = new DetectionMask { Points = [0.0, 0.60, 1.0, 0.60, 1.0, 1.0, 0.0, 1.0] };

        foreach (float y in new[] { 0.1f, 0.3f, 0.39f, 0.4f, 0.41f, 0.6f, 0.8f })
        {
            var box = new BoundingBox(0.4f, y, 0.2f, 0.2f);

            Assert.Equal(
                mask.Contains(box.X + (box.Width / 2), box.Y + box.Height),
                mask.Contains(box));
        }
    }

    [Fact]
    public void A_mask_naming_classes_silences_only_those()
    {
        // The pavement along the drive. Vehicles on it are traffic; the person walking along it is
        // the thing the camera is there for, and they stand in exactly the same place.
        var policy = Policy(o =>
        {
            o.Classes = ["car", "person"];
            o.Masks =
            [
                new DetectionMask
                {
                    Name = "pavement",
                    Classes = ["car", "truck"],
                    Points = [0.0, 0.0, 1.0, 0.0, 1.0, 0.30, 0.0, 0.30],
                },
            ];
        });

        // Both stand with their feet at 0.20, inside the shape.
        TrackedObject[] both =
        [
            At(1, "car", 0.95f, 0.4f, 0.10f),
            At(2, "person", 0.95f, 0.6f, 0.10f),
        ];

        policy.Observe([], T0);
        policy.Observe(both, T0.AddSeconds(600));
        policy.Observe(both, T0.AddSeconds(601));

        // The car is dropped on both frames; the person is reported.
        Assert.Equal(2, policy.SuppressedByMask);
        Assert.Equal(1, policy.Opened);
    }

    [Fact]
    public void A_mask_naming_no_classes_still_silences_everything()
    {
        // Null and empty both mean "everything". A mask written before class filters existed, and
        // one whose filter was cleared, have to keep meaning what they meant.
        Assert.True(new DetectionMask().Applies("person"));
        Assert.True(new DetectionMask { Classes = [] }.Applies("person"));
        Assert.True(new DetectionMask { Classes = ["car"] }.Applies("car"));
        Assert.False(new DetectionMask { Classes = ["car"] }.Applies("person"));
    }

    [Fact]
    public void A_mask_class_filter_is_matched_ordinally()
    {
        // Against the model's own label strings, which are fixed ASCII from its label file.
        Assert.False(new DetectionMask { Classes = ["Car"] }.Applies("car"));
    }

    [Fact]
    public void A_mask_with_too_few_points_is_ignored_rather_than_masking_everything()
    {
        var policy = Policy(o =>
        {
            o.Classes = ["person"];
            o.Masks = [new DetectionMask { Name = "typo", Points = [0.1, 0.1, 0.9, 0.9] }];
        });

        policy.Observe([], T0);
        policy.Observe([At(1, "person", 0.9f, 0.5f, 0.5f)], T0.AddSeconds(600));

        Assert.Equal(0, policy.SuppressedByMask);
        Assert.Equal(1, policy.Opened);
    }

    // ------------------------------------------------------------------- track

    [Fact]
    public void A_stationary_object_records_one_track_sample_however_long_it_stays()
    {
        // The whole point of run-length encoding it. A car parked for ten minutes is examined six
        // hundred times and is in the same place every time; storing that as six hundred samples
        // would be the per-frame storage the episode exists to avoid.
        var policy = Policy();

        for (int i = 0; i < 60; i++)
        {
            policy.Observe([At(1, "person", 0.9f, 0.5f, 0.5f)], T0.AddSeconds(i));
        }

        IReadOnlyList<TrackSample> track = policy.Finalise(T0.AddSeconds(60))[0].Track;

        TrackSample only = Assert.Single(track);
        Assert.Equal(T0, only.At);
        Assert.Equal(0.5f, only.Box!.Value.Box.X);
    }

    [Fact]
    public void Two_of_something_standing_still_never_resample_each_other()
    {
        // Two people standing perfectly still is two episodes, each with one sample. Nothing here
        // has to decide which box belongs to which of them, because the tracker already did.
        var policy = Policy();

        for (int i = 0; i < 20; i++)
        {
            bool flip = i % 2 == 0;
            policy.Observe(
                [
                    At(1, "person", flip ? 0.9f : 0.7f, 0.10f, 0.5f),
                    At(2, "person", flip ? 0.7f : 0.9f, 0.80f, 0.5f),
                ],
                T0.AddSeconds(i));
        }

        IReadOnlyList<ObjectEpisode> closed = policy.Finalise(T0.AddSeconds(20));

        Assert.Equal(2, closed.Count);
        Assert.All(closed, e => Assert.Single(e.Track));
    }

    [Fact]
    public void A_moving_object_records_a_sample_wherever_it_went()
    {
        var policy = Policy();

        for (int i = 0; i < 5; i++)
        {
            policy.Observe([At(1, "person", 0.9f, 0.1f + (0.1f * i), 0.5f)], T0.AddSeconds(i));
        }

        IReadOnlyList<TrackSample> track = policy.Finalise(T0.AddSeconds(5))[0].Track;

        Assert.Equal(5, track.Count);
        Assert.Equal([0.1f, 0.2f, 0.3f, 0.4f, 0.5f], [.. track.Select(s => s.Box!.Value.Box.X)]);
        Assert.Equal(T0.AddSeconds(4), track[^1].At);
    }

    [Fact]
    public void A_box_that_grows_without_moving_is_still_recorded()
    {
        // Someone walking straight at the camera holds their centre still while their box doubles.
        // The movement gate can afford to miss that; a track that did would replay them frozen at
        // the size they arrived, which is why this tests edges rather than centres.
        var policy = Policy();

        policy.Observe(
            [new TrackedObject(1, "person", new BoundingBox(0.45f, 0.45f, 0.10f, 0.10f), 0.9f,
                TrackState.Confirmed, T0, T0, 2)],
            T0);
        policy.Observe(
            [new TrackedObject(1, "person", new BoundingBox(0.40f, 0.40f, 0.20f, 0.20f), 0.9f,
                TrackState.Confirmed, T0, T0.AddSeconds(1), 3)],
            T0.AddSeconds(1));

        IReadOnlyList<TrackSample> track = policy.Finalise(T0.AddSeconds(2))[0].Track;

        Assert.Equal(2, track.Count);
        Assert.Equal(0.20f, track[1].Box!.Value.Box.Width);
    }

    [Fact]
    public void An_absence_inside_an_episode_records_one_gap_and_not_one_per_frame()
    {
        // The episode stays open across the absence window, so without a marker the run-length
        // rule holds the last box over footage in which nothing is there. One marker covers the
        // whole gap — repeating it every frame would undo the encoding.
        var policy = Policy(o => o.AbsenceSeconds = 30);

        policy.Observe([At(1, "person", 0.9f, 0.5f, 0.5f)], T0);
        policy.Observe([At(1, "person", 0.9f, 0.5f, 0.5f)], T0.AddSeconds(1));

        for (int i = 2; i < 8; i++)
        {
            policy.Observe([], T0.AddSeconds(i));
        }

        // Re-acquired in the same place, so it rejoins and the gap sits inside one episode — which
        // is the shape a consumer has to be able to draw: box, nothing, box.
        policy.Observe([At(2, "person", 0.9f, 0.5f, 0.5f)], T0.AddSeconds(8));

        ObjectEpisode episode = Assert.Single(policy.Finalise(T0.AddSeconds(9)));
        IReadOnlyList<TrackSample> track = episode.Track;

        Assert.Equal(3, track.Count);
        Assert.Equal(0.5f, track[0].Box!.Value.Box.X);
        Assert.Null(track[1].Box);
        Assert.Equal(T0.AddSeconds(2), track[1].At);
        Assert.Equal(T0.AddSeconds(8), track[2].At);
        Assert.Equal(0.5f, track[2].Box!.Value.Box.X);
    }

    [Fact]
    public void A_second_episodes_track_starts_clean_and_never_opens_with_a_gap()
    {
        // A gap before the first sighting would describe time the episode does not cover, and the
        // previous episode's samples would date before this one's start.
        var policy = Policy(o => o.AbsenceSeconds = 2);

        policy.Observe([At(1, "person", 0.9f, 0.1f, 0.5f)], T0);
        policy.Observe([], T0.AddSeconds(2));
        policy.Observe([], T0.AddSeconds(4));

        policy.Observe([At(2, "person", 0.9f, 0.7f, 0.5f)], T0.AddSeconds(10));

        IReadOnlyList<TrackSample> track = policy.Finalise(T0.AddSeconds(12))[0].Track;

        TrackSample first = track[0];
        Assert.NotNull(first.Box);
        Assert.Equal(T0.AddSeconds(10), first.At);
        Assert.Equal(0.7f, first.Box!.Value.Box.X);
    }

    [Fact]
    public void A_live_snapshot_says_where_things_are_now_and_not_where_they_have_been()
    {
        // Live snapshots arrive once a frame for as long as something is there. Handing each one
        // the accumulated track would re-send up to TrackMaxSamples samples a second to say where
        // one person is standing; one sample holding the current box says exactly that.
        //
        // The box is this frame's rather than the episode's best, for the same reason: the peak
        // frame is where the object looked *clearest*, which is not where it is.
        var policy = Policy();

        policy.Observe([At(1, "person", 0.9f, 0.1f, 0.5f)], T0);
        policy.Observe([At(1, "person", 0.9f, 0.4f, 0.5f)], T0.AddSeconds(1));
        ObjectEpisode live =
            policy.Observe([At(1, "person", 0.9f, 0.7f, 0.5f)], T0.AddSeconds(2)).Live[0];

        TrackSample only = Assert.Single(live.Track);
        Assert.Equal(T0.AddSeconds(2), only.At);
        Assert.Equal(0.7f, only.Box!.Value.Box.X);
        Assert.Equal(0.7f, live.BestBox!.Value.Box.X);

        // The record that eventually lands in storage kept all of it, and its best box is still
        // the peak frame's — which here is the first, since nothing ever scored higher.
        ObjectEpisode closed = Assert.Single(policy.Finalise(T0.AddSeconds(3)));

        Assert.Equal(3, closed.Track.Count);
        Assert.Equal(0.1f, closed.BestBox!.Value.Box.X);
    }

    [Fact]
    public void The_hard_cut_starts_the_continuation_on_a_fresh_track()
    {
        // The continuation is a new record covering a new stretch of time. Carrying the previous
        // episode's samples into it would date them before its own start.
        var policy = Policy(o => o.MaxEpisodeSeconds = 5);

        policy.Observe([At(1, "person", 0.9f, 0.1f, 0.5f)], T0);
        policy.Observe([At(1, "person", 0.9f, 0.2f, 0.5f)], T0.AddSeconds(1));

        ObjectEpisode closed = Assert.Single(
            policy.Observe([At(1, "person", 0.9f, 0.9f, 0.5f)], T0.AddSeconds(6)).Published);

        // Three: the cut happens after the frame is recorded, so the sighting that tripped it is
        // part of the episode it ends — which is also the instant that episode's EndedAt names.
        Assert.Equal(T0.AddSeconds(6), closed.EndedAt);
        Assert.Equal(3, closed.Track.Count);

        // Read the continuation as the record it becomes rather than as a live snapshot, whose
        // single sample would look right whether or not the fresh track was really fresh.
        ObjectEpisode continuation = Assert.Single(policy.Finalise(T0.AddSeconds(7)));

        Assert.NotEqual(closed.Id, continuation.Id);

        TrackSample only = Assert.Single(continuation.Track);
        Assert.Equal(T0.AddSeconds(6), only.At);
        Assert.Equal(0.9f, only.Box!.Value.Box.X);
    }

    [Fact]
    public void A_track_past_its_cap_is_thinned_rather_than_truncated()
    {
        // Losing the tail would leave the rest of the episode replaying without a box. Halving the
        // interval keeps the whole span covered, coarsely.
        var policy = Policy(o =>
        {
            o.TrackMaxSamples = 20;
            o.TrackMinMovementFraction = 0.0001;
        });

        for (int i = 0; i < 200; i++)
        {
            policy.Observe([At(1, "person", 0.9f, 0.001f * i, 0.5f)], T0.AddSeconds(i));
        }

        IReadOnlyList<TrackSample> track = policy.Finalise(T0.AddSeconds(200))[0].Track;

        Assert.InRange(track.Count, 2, 20);
        Assert.Equal(T0, track[0].At);
        Assert.Equal(T0.AddSeconds(199), track[^1].At);
    }

    [Fact]
    public void Jitter_below_the_track_threshold_does_not_add_samples()
    {
        // Boxes wobble by a fraction of a percent on a static object. Recording that would turn
        // every parked car back into one sample a second.
        var policy = Policy(o => o.TrackMinMovementFraction = 0.01);

        for (int i = 0; i < 20; i++)
        {
            policy.Observe(
                [At(1, "person", 0.9f, 0.5f + (i % 2 == 0 ? 0f : 0.002f), 0.5f)],
                T0.AddSeconds(i));
        }

        Assert.Single(policy.Finalise(T0.AddSeconds(20))[0].Track);
    }

    // --- Through a real tracker -----------------------------------------------------------------
    //
    // The seam the whole design rests on: confirmation lives in the tracker and episodes live here,
    // so a few tests have to prove the two agree about what happened.

    private static ObjectEventPolicy Together(
        out ObjectTracker tracker,
        Action<DetectionOptions>? configure = null)
    {
        DetectionOptions options = Options(configure);
        tracker = new ObjectTracker(options.Tracking);
        int n = 0;
        return new ObjectEventPolicy(options, () => $"id-{++n}");
    }

    private static ObjectObservation Step(
        ObjectTracker tracker,
        ObjectEventPolicy policy,
        DateTimeOffset at,
        params DetectedObject[] detections) =>
        policy.Observe(tracker.Update(detections, at), at);

    [Fact]
    public void A_one_frame_ghost_never_becomes_a_record()
    {
        // The dominant failure of a small detector, and the reason confirmation exists at all. An
        // infrared-lit bush read as a person at 0.9 confidence, once, must leave nothing behind.
        ObjectEventPolicy policy = Together(out ObjectTracker tracker);

        Step(tracker, policy, T0, new DetectedObject("person", 0.9f, new BoundingBox(0.4f, 0.4f, 0.1f, 0.2f)));
        Step(tracker, policy, T0.AddSeconds(0.5));
        Step(tracker, policy, T0.AddSeconds(1.0));

        Assert.Equal(0, policy.Opened);
        Assert.Equal(1, tracker.Ghosts);
        Assert.False(policy.HasOpenEpisodes);
    }

    [Fact]
    public void Someone_really_there_is_confirmed_and_recorded_once()
    {
        // The other half: a person walking across the view at 2 fps is one episode, dated from the
        // frame they first appeared on rather than from the one that confirmed them.
        ObjectEventPolicy policy = Together(out ObjectTracker tracker);

        for (int i = 0; i < 20; i++)
        {
            Step(
                tracker,
                policy,
                T0.AddSeconds(i * 0.5),
                new DetectedObject(
                    "person", 0.9f, new BoundingBox(0.05f + (i * 0.02f), 0.4f, 0.10f, 0.25f)));
        }

        ObjectEpisode closed = Assert.Single(policy.Finalise(T0.AddSeconds(10)));

        Assert.Equal(1, policy.Opened);
        Assert.Equal(0, tracker.Ghosts);
        Assert.Equal(T0, closed.StartedAt);
    }

    [Fact]
    public void A_missed_frame_in_the_middle_does_not_split_the_record()
    {
        // The detector losing somebody for one frame is not somebody leaving. The track coasts, so
        // the episode never even learns that anything was missed.
        ObjectEventPolicy policy = Together(out ObjectTracker tracker);

        for (int i = 0; i < 12; i++)
        {
            DetectedObject[] found = i == 6
                ? []
                : [new DetectedObject(
                    "person", 0.9f, new BoundingBox(0.05f + (i * 0.02f), 0.4f, 0.10f, 0.25f))];

            Step(tracker, policy, T0.AddSeconds(i * 0.5), found);
        }

        ObjectEpisode closed = Assert.Single(policy.Finalise(T0.AddSeconds(6)));

        Assert.Equal(1, policy.Opened);
        Assert.Equal(T0, closed.StartedAt);

        // Eleven: twelve frames, one of which the detector missed. The two sightings that earned
        // confirmation count too — they are frames this object was really detected on, and a
        // consumer weighing FrameCount against the episode's duration is asking exactly that.
        Assert.Equal(11, closed.FrameCount);
    }
}
