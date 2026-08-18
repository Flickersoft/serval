namespace Serval.Ai;

/// <summary>
/// One object's continuous presence in front of one camera — "this person, from 14:02:11 until
/// 14:02:53" — rather than one frame it was seen in.
///
/// Why episodes rather than frames, and what that summarises away, is in <c>Docs/detection.md</c>
/// under *Episodes, not frames*.
///
/// <paramref name="EndedAt"/> is null while the thing is still there. An episode is therefore
/// published twice under the same <paramref name="Id"/>: once when it opens and once when it
/// closes, the second replacing the first.
///
/// <paramref name="IsArrival"/> is the one a consumer should act on. Presence is not news — a
/// driveway camera sees a parked car in every frame for hours — so only an episode beginning with
/// something turning up is worth spending an expensive model on.
///
/// <paramref name="Track"/> is where it was as it went, run-length encoded — see
/// <see cref="TrackSample"/>. <paramref name="BestBox"/> is the clearest look at it and is what a
/// still wants; the track is what footage wants, because a box drawn from the peak frame is in the
/// wrong place for every other second of a walk across the view.
/// </summary>
public sealed record ObjectEpisode(
    string Id,
    string Label,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    float PeakConfidence,
    DateTimeOffset PeakFrameAt,
    int FrameCount,
    ScoredBox? BestBox,
    IReadOnlyList<TrackSample> Track,
    bool IsAlert,
    bool IsArrival);

/// <summary>Why a description is worth spending on this moment.</summary>
public static class DescribeReason
{
    /// <summary>Something turned up that had been observably absent.</summary>
    public const string Arrival = "arrival";

    /// <summary>Something already in shot moved. Catches what arrival cannot — a car parked since
    /// before the camera started watching, driving away.</summary>
    public const string Movement = "movement";
}

/// <summary>One class, this frame, worth waking the vision model for.</summary>
public sealed record DescriptionTrigger(string Label, string Reason, float Confidence);

/// <summary>
/// What one frame changed: records to store, where things are right now, and whether any of it is
/// worth describing.
///
/// <paramref name="Published"/> and <paramref name="Triggers"/> are deliberately separate. Nearly
/// every frame that changes state is worth *recording* — a car arriving, a car leaving — while only
/// a fraction is worth spending seconds of vision inference on.
///
/// <paramref name="Live"/> is not a record of anything: it is where every open episode is *this
/// frame*, produced on every frame rather than only on a state change, so a consumer can draw a box
/// that follows what it is drawn around. **Broadcast it, never persist it** — storing it would be
/// one write a second per camera to say what the episode's track already says once at the end. Its
/// snapshots carry only the current sample; see <see cref="ObjectEventPolicy.Observe"/>.
/// </summary>
public sealed record ObjectObservation(
    IReadOnlyList<ObjectEpisode> Published,
    IReadOnlyList<ObjectEpisode> Live,
    IReadOnlyList<DescriptionTrigger> Triggers)
{
    public static readonly ObjectObservation Empty = new([], [], []);
}

/// <summary>
/// Everything between "the tracker listed what it is following" and "open, close, or say nothing".
///
/// Pure, and split from the detector for the reason <see cref="SoundEventPolicy"/> is split from
/// the tagger: the detector is model configuration that cannot be exercised without weights on
/// disk, while every decision that shapes what lands in telemetry lives here and is testable with
/// a list of labels and a fake clock.
///
/// One instance per camera. The state is per-camera by definition, and so is the configuration —
/// this is where a camera's own class allowlist and confidence floor are applied, because the
/// detector itself is shared by every camera and cannot hold them.
///
/// <para><b>It reads tracks, not detections.</b> Identity turns "a person is present" into "this
/// person, since 14:02:11", and it is what keeps the gate simple: matching a box to the nearest
/// previous one, or guessing whether two boxes are the same thing, are approximations of a question
/// <see cref="ObjectTracker"/> answers directly.</para>
/// </summary>
public sealed class ObjectEventPolicy
{
    /// <summary>
    /// One object being followed, and the episode being written about it.
    ///
    /// <para><see cref="TrackId"/> is the track this episode most recently followed, and is not
    /// cleared when that track dies. A tracker never reuses an id, so a stale one can only ever fail
    /// to match — which is what lets an object that drops below the confidence floor for a frame and
    /// comes back keep its episode instead of fragmenting into two.</para>
    /// </summary>
    private sealed class ObjectState
    {
        public required string Label;
        public required Open Episode;
        public int TrackId;

        // Where this object was the last time it was reported, kept for two jobs: measuring
        // movement against the previous frame, and recognising a new track as this object coming
        // back. Survives the track's death, because that is exactly when the second job matters.
        public ScoredBox? LastBox;
        public DateTimeOffset LastFrameAt;
        public long LastPresentFrame = -1;

        public bool MovedThisFrame;
        public long OpenedOnFrame = -1;
    }

    /// <summary>
    /// Where an episode ended, kept for <see cref="DetectionOptions.NoveltySeconds"/> after it
    /// closed.
    ///
    /// <para>What stops something the camera was already watching from announcing itself as new when
    /// it comes back. <see cref="Rejoin"/> covers the case where the episode is still open, which is
    /// most of them; this covers the rest — a distant object that drops out for longer than
    /// <see cref="DetectionOptions.AbsenceSeconds"/> and is re-acquired in the same place gets a
    /// second episode, correctly, and that episode is not an arrival.</para>
    ///
    /// <para>Scoped to a place rather than to a class, which is the whole point: a second car
    /// pulling onto a drive where one is already parked is standing somewhere nothing just left
    /// from, and is news.</para>
    /// </summary>
    private readonly record struct Departure(string Label, BoundingBox Box, DateTimeOffset At);

    private sealed class Open
    {
        public required string Id;
        public required DateTimeOffset StartedAt;
        public DateTimeOffset LastSeenAt;
        public float PeakConfidence;
        public DateTimeOffset PeakFrameAt;
        public ScoredBox? BestBox;
        public int FrameCount;
        public List<TrackSample> Track = [];
        public bool IsAlert;

        /// <summary>Whether this episode is the kind that may raise an alert, leaving only the
        /// question of whether it is ever seen clearly enough. Settled when the episode opens.</summary>
        public bool IsAlertEligible;

        public bool IsArrival;
    }

    private readonly DetectionOptions _options;
    private readonly Func<string> _newId;
    private readonly HashSet<string> _allowed;
    private readonly HashSet<string> _alerts;
    private readonly HashSet<string> _describable;
    private readonly DetectionMask[] _masks;
    private readonly List<ObjectState> _objects = [];
    private readonly List<Departure> _departed = [];

    /// <summary>
    /// When an episode ended, indexed by the track that was carrying it.
    ///
    /// <para>Separate from <see cref="Departure"/> because it answers a different question — not
    /// "did something leave this spot" but "is the track opening this episode one that already had
    /// one" — and because that question has a different natural horizon. A departure stops mattering
    /// once <see cref="DetectionOptions.NoveltySeconds"/> has passed, which is the definition of
    /// having been gone long enough to be news again; this stops mattering only when the track
    /// itself dies, which is not observable from here.</para>
    ///
    /// <para>Kept for <see cref="DetectionOptions.MaxEpisodeSeconds"/>, the longest any single
    /// episode is allowed to run, so it cannot grow without bound on a camera watched for days. A
    /// tracker never reuses an id, so a stale entry can only ever fail to match.</para>
    /// </summary>
    private readonly Dictionary<int, DateTimeOffset> _continued = [];

    // Bounds how often a class can wake the vision model, whatever keeps triggering it. Per class
    // rather than per object, because a description describes a scene: three people walking in one
    // behind another are one thing to be told about, not three.
    private readonly Dictionary<string, DateTimeOffset> _lastDescribed =
        new(StringComparer.Ordinal);

    private DateTimeOffset? _watchingSince;
    private long _frameNumber = -1;

    /// <param name="newId">Injected so a test can assert on ids without matching a GUID. Defaults
    /// to one per episode, which is what makes the open and close writes replace rather than
    /// accumulate.</param>
    public ObjectEventPolicy(DetectionOptions options, Func<string>? newId = null)
    {
        _options = options;
        _newId = newId ?? (static () => Guid.NewGuid().ToString());

        // Ordinal: these are matched against the model's own class strings, which are fixed ASCII
        // from its label file. Culture-sensitive comparison here would be a way to be surprised.
        // Effective* rather than the raw arrays: an unset list means the built-in default, and
        // the arrays are empty by default so configuration binding cannot append to them.
        _allowed = new HashSet<string>(options.EffectiveClasses, StringComparer.Ordinal);
        _alerts = new HashSet<string>(options.EffectiveAlertClasses, StringComparer.Ordinal);
        _describable = new HashSet<string>(options.EffectiveDescribeClasses, StringComparer.Ordinal);

        _masks = [.. options.Masks.Where(static m => m.IsUsable)];
    }

    /// <summary>
    /// Tracks dropped because they stood inside a masked region. <b>Not the whole of what a mask
    /// removed, on a live session</b> — <see cref="RegionPlanner"/> declines to point the detector
    /// at an unconditional mask at all, so most of it never reaches a detection. <c>--replay-gates</c>
    /// reports the complete figure; see <c>Docs/detection.md</c> under *Masks*.
    /// </summary>
    public long SuppressedByMask { get; private set; }

    /// <summary>Tracks dropped because their class is not in this camera's allowlist.</summary>
    public long SuppressedByClass { get; private set; }

    /// <summary>Moments that would have been described but for the class not being one worth
    /// describing.</summary>
    public long SuppressedByDescribeClass { get; private set; }

    /// <summary>Moments dropped because the same class was described too recently.</summary>
    public long SuppressedByCooldown { get; private set; }

    /// <summary>Descriptions actually requested. The number that decides whether any of this is
    /// cheaper than the motion gate it replaces.</summary>
    public long Described { get; private set; }

    /// <summary>Tracks whose confidence was under this camera's floor.</summary>
    public long BelowThreshold { get; private set; }

    /// <summary>
    /// Tracks dropped for being smaller than <see cref="DetectionOptions.MinObjectFraction"/>.
    ///
    /// The number to watch when setting it: a camera whose distant subjects genuinely matter should
    /// see this stay near zero, and one climbing steadily is a floor set above something the site
    /// cares about.
    /// </summary>
    public long TooSmall { get; private set; }

    /// <summary>
    /// New tracks matched back onto an episode whose own track had died, rather than opening a
    /// second episode for the same object.
    ///
    /// The number to watch when tuning <see cref="TrackingOptions.CoastSeconds"/>: a large one says
    /// the tracker is giving up on objects that are still there, and the episode hysteresis is
    /// quietly holding the record together.
    /// </summary>
    public long Rejoined { get; private set; }

    /// <summary>
    /// Episodes opened on a track that had already outlived an earlier episode of its own, and so
    /// were dated from that episode's close rather than from the track's birth.
    ///
    /// The number to watch when setting <see cref="DetectionOptions.ScoreThreshold"/>: it counts
    /// objects sitting exactly on the floor, seen by the tracker and discarded by the policy, and a
    /// large one says the floor is set into the middle of something real.
    /// </summary>
    public long Continued { get; private set; }

    public long Opened { get; private set; }

    /// <summary>Episodes that began with something actually turning up, as opposed to the scenery
    /// a camera opened on or a hard cut continuing. The only ones worth an expensive model, and
    /// the number this whole design should be judged on.</summary>
    public long Arrivals { get; private set; }

    public long Closed { get; private set; }

    /// <summary>Whether anything is currently present, i.e. any episode is open.</summary>
    public bool HasOpenEpisodes => _objects.Count > 0;

    /// <summary>
    /// Folds one frame's tracks into the running episodes.
    /// </summary>
    /// <param name="tracked">Everything <see cref="ObjectTracker"/> believes is present, including
    /// coasting tracks, and unfiltered — this is where a camera's masks, allowlist and confidence
    /// floor are applied.</param>
    /// <param name="now">Injected so every duration here is testable without sleeping.</param>
    /// <returns>Episodes to publish — newly opened ones and newly closed ones. Usually empty: a
    /// frame in the middle of a person walking past changes state without producing a record.</returns>
    public ObjectObservation Observe(
        IReadOnlyList<TrackedObject> tracked,
        DateTimeOffset now)
    {
        List<ObjectEpisode> published = [];
        _frameNumber++;

        // The moment this camera started being watched. Everything already in shot at that point is
        // inventory rather than arrival, because no absence was ever observed for it — but something
        // first seen well *after* has been observed absent for that whole time, and is news.
        _watchingSince ??= now;

        // Anything that left longer ago than NoveltySeconds no longer withholds an arrival: that
        // duration is the definition of having been gone long enough for a return to mean something.
        _departed.RemoveAll(d => now - d.At >= TimeSpan.FromSeconds(_options.NoveltySeconds));

        // Held far longer than a departure — see _continued for why the two horizons differ — so
        // this is swept rather than rebuilt, and only when there is something old enough to drop.
        if (_continued.Count > 0)
        {
            var stale = TimeSpan.FromSeconds(_options.MaxEpisodeSeconds);
            List<int>? drop = null;

            foreach ((int id, DateTimeOffset closedAt) in _continued)
            {
                if (now - closedAt >= stale)
                {
                    (drop ??= []).Add(id);
                }
            }

            foreach (int id in drop ?? [])
            {
                _continued.Remove(id);
            }
        }

        List<TrackedObject> present = [];

        foreach (TrackedObject track in tracked)
        {
            if (IsMasked(track.Box, track.Label))
            {
                SuppressedByMask++;
                continue;
            }

            if (!_allowed.Contains(track.Label))
            {
                SuppressedByClass++;
                continue;
            }

            // Before the confidence floor, because it is the case confidence gets wrong: a region
            // crop magnifies a few-pixel artefact until the model is sure of it, so the score rises
            // with the mistake rather than falling. See DetectionOptions.MinObjectFraction.
            if (_options.MinObjectFraction > 0
                && (double)track.Box.Width * track.Box.Height < _options.MinObjectFraction)
            {
                TooSmall++;
                continue;
            }

            // A coasting track keeps the confidence it was last measured at rather than a decayed
            // one, so this gate does not quietly end an episode for a subject that is merely behind
            // something. See TrackedObject.Score.
            if (track.Score < _options.ScoreThreshold)
            {
                BelowThreshold++;
                continue;
            }

            present.Add(track);
        }

        // Two passes, so that what happens to a track cannot depend on where the tracker listed it.
        // The first claims every episode whose own track is still alive; only then can the second
        // safely offer the unclaimed ones to tracks it has never seen before.
        var seen = new HashSet<ObjectState>();
        List<(TrackedObject Track, ObjectState State)> matched = [];
        List<TrackedObject> fresh = [];

        foreach (TrackedObject track in present)
        {
            ObjectState? state = null;

            foreach (ObjectState candidate in _objects)
            {
                if (candidate.TrackId == track.Id)
                {
                    state = candidate;
                    break;
                }
            }

            if (state is null)
            {
                fresh.Add(track);
                continue;
            }

            seen.Add(state);
            matched.Add((track, state));
        }

        foreach (TrackedObject track in fresh)
        {
            ObjectState state = Rejoin(track, seen) ?? OpenEpisode(track, now);
            seen.Add(state);
            matched.Add((track, state));
        }

        foreach ((TrackedObject track, ObjectState state) in matched)
        {
            Open open = state.Episode;
            var box = new ScoredBox(track.Box, track.Score);

            // Only against the immediately preceding frame. Comparing a box to where the object was
            // ten minutes ago would report "movement" for something that simply came back to a
            // different spot, which is an arrival at most and usually nothing.
            state.MovedThisFrame = state.LastPresentFrame == _frameNumber - 1
                && state.LastBox is { } was
                && HasMoved(was.Box, track.Box, now - state.LastFrameAt);

            // A coasting track is where the filter predicts, not where anything was seen. It keeps
            // the episode open and gets drawn, but it is not evidence: counting it would inflate
            // FrameCount, and letting it set the peak would date the clearest look at an object to a
            // frame nobody looked at it in.
            bool measured = track.State != TrackState.Coasting;

            if (measured)
            {
                open.LastSeenAt = now;
                open.FrameCount++;

                if (track.Score > open.PeakConfidence)
                {
                    open.PeakConfidence = track.Score;
                    open.PeakFrameAt = now;
                    open.BestBox = box;
                }

                // The confidence half of the alert decision, re-asked until it passes and then never
                // again. An alert says a thing of this class turned up and was at some point seen
                // this clearly; which frame carried that certainty is a property of the detection
                // schedule rather than of what happened.
                //
                // Judging only the opening frame would penalise examining a subject early: the first
                // look at an arrival is the one where they are furthest away and smallest, so a
                // visitor walking up a path is least convincing in the moment they appear.
                //
                // Inside `measured` for the reason the peak update is: a coasting track is where the
                // filter predicts, and an alert must not fire on a frame nobody looked at.
                if (!open.IsAlert
                    && open.IsAlertEligible
                    && track.Score >= _options.AlertMinConfidence)
                {
                    open.IsAlert = true;
                }
            }

            AppendTrack(open.Track, now, box);

            state.LastBox = box;
            state.LastFrameAt = now;
            state.LastPresentFrame = _frameNumber;

            // Hard cut, then immediately reopen: something that has been there an hour is still
            // there, and the record must say so rather than the stream going quiet. The
            // continuation is explicitly not an arrival — nothing turned up, the clock merely
            // ran out — so it is bookkeeping and never asks for a description.
            //
            // Only on a measured frame, because the cut asserts the object is still there and a
            // prediction is not that assertion. Waiting for one costs at most CoastSeconds of
            // overrun, against a continuation opening with a peak from a frame nobody looked at.
            if (measured && now - open.StartedAt >= TimeSpan.FromSeconds(_options.MaxEpisodeSeconds))
            {
                published.Add(Close(state, now));

                state.Episode = new Open
                {
                    Id = _newId(),
                    StartedAt = now,
                    LastSeenAt = now,
                    PeakConfidence = track.Score,
                    PeakFrameAt = now,
                    BestBox = box,
                    FrameCount = 1,
                    IsAlert = open.IsAlert,

                    // Carried across, unlike IsArrival: eligibility is a fact about the object that
                    // turned up and the clock running out does not unmake it. A presence not yet seen
                    // clearly enough when it was cut must still be able to earn its alert afterwards,
                    // which is also why this is stored rather than derived from IsArrival.
                    IsAlertEligible = open.IsAlertEligible,
                    IsArrival = false,
                };

                AppendTrack(state.Episode.Track, now, box);
                Opened++;
            }
        }

        // Not reported this frame: the episode is orphaned rather than closed. CoastSeconds is how
        // long the tracker keeps predicting a position — a claim about geometry, which degrades fast
        // — where AbsenceSeconds is how long the record waits before saying the object left. A
        // subject occluded for ten seconds outlives the first and not the second, and Rejoin puts
        // the two back together when the detector picks them up again.
        for (int i = _objects.Count - 1; i >= 0; i--)
        {
            ObjectState state = _objects[i];

            if (seen.Contains(state))
            {
                continue;
            }

            state.MovedThisFrame = false;

            // The absence is part of the record even though it does not end the episode. Under the
            // run-length rule an unmarked gap reads as "still where it was", which would hold a box
            // over empty footage for the whole of the absence window.
            AppendTrack(state.Episode.Track, now, box: null);

            if (now - state.Episode.LastSeenAt >= TimeSpan.FromSeconds(_options.AbsenceSeconds))
            {
                published.Add(Close(state, now));
                _objects.RemoveAt(i);
            }
        }

        List<DescriptionTrigger> triggers = Describe(seen, now);

        // Where everything still open is, as of this frame. Built last so an episode closed above is
        // already gone from it, and one opened above is already in it.
        //
        // An orphaned episode contributes its last known position and a null track sample: "here is
        // where this was, and it was not seen this frame". Both claims are owed to a consumer that
        // can draw them separately — a dotted outline against a solid one — because between
        // CoastSeconds and AbsenceSeconds the episode has no other evidence and the record still
        // says the subject has not left.
        List<ObjectEpisode> live = [];

        foreach (ObjectState state in _objects)
        {
            live.Add(LiveSnapshot(
                state.Episode,
                state.Label,
                now,
                state.LastBox,
                measured: seen.Contains(state)));
        }

        return new ObjectObservation(published, live, triggers);
    }

    /// <summary>
    /// Closes everything still open, for when the camera's session is stopping.
    ///
    /// Without this every restart would strand its open episodes as records that claim, forever,
    /// that someone is still standing there.
    /// </summary>
    public IReadOnlyList<ObjectEpisode> Finalise(DateTimeOffset now)
    {
        List<ObjectEpisode> published = [];

        foreach (ObjectState state in _objects)
        {
            published.Add(Close(state, now));
        }

        _objects.Clear();
        return published;
    }

    /// <summary>
    /// Decides what this frame is worth waking the vision model for.
    ///
    /// A separate pass, after every episode's state is settled, so the answer cannot depend on the
    /// order the tracker listed things in. Grouped by class rather than by object, because a
    /// description describes a scene: three people arriving together is one thing to be told about.
    /// </summary>
    private List<DescriptionTrigger> Describe(HashSet<ObjectState> seen, DateTimeOffset now)
    {
        Dictionary<string, (string Reason, float Score)> wanted = new(StringComparer.Ordinal);

        foreach (ObjectState state in _objects)
        {
            if (!seen.Contains(state))
            {
                continue;
            }

            string? reason = state.OpenedOnFrame == _frameNumber && state.Episode.IsArrival
                ? DescribeReason.Arrival
                : state.MovedThisFrame ? DescribeReason.Movement : null;

            if (reason is null)
            {
                continue;
            }

            float score = state.LastBox?.Score ?? 0f;

            // The most confident of them, so which one is quoted depends on the frame rather than
            // on the order the tracker listed things in.
            //
            // No tie-break between the two reasons is needed, because they cannot both appear for
            // one class on one frame: a movement means something of that class was here last frame,
            // and an arrival means nothing of it has been here for NoveltySeconds. If novelty ever
            // stops being class-scoped, that stops being true and this needs to say which wins.
            if (!wanted.TryGetValue(state.Label, out (string Reason, float Score) best)
                || score > best.Score)
            {
                wanted[state.Label] = (reason, score);
            }
        }

        List<DescriptionTrigger> triggers = [];

        foreach (string label in wanted.Keys.OrderBy(static k => k, StringComparer.Ordinal))
        {
            (string reason, float score) = wanted[label];

            if (!_describable.Contains(label))
            {
                SuppressedByDescribeClass++;
                continue;
            }

            if (_lastDescribed.TryGetValue(label, out DateTimeOffset last)
                && now - last < TimeSpan.FromSeconds(_options.DescribeCooldownSeconds))
            {
                SuppressedByCooldown++;
                continue;
            }

            _lastDescribed[label] = now;
            Described++;
            triggers.Add(new DescriptionTrigger(label, reason, score));
        }

        return triggers;
    }

    /// <summary>
    /// Whether a track stands inside any masked region that applies to its class, tested at the
    /// bottom centre of its box — where the object meets the ground. See
    /// <see cref="DetectionMask.Contains(BoundingBox)"/>.
    ///
    /// The class is checked before the geometry because it is a name comparison against a short
    /// array and the other is a ray cast over every vertex.
    /// </summary>
    private bool IsMasked(BoundingBox box, string label)
    {
        if (_masks.Length == 0)
        {
            return false;
        }

        foreach (DetectionMask mask in _masks)
        {
            if (mask.Applies(label) && mask.Contains(box))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether this object shifted between two consecutive frames.
    ///
    /// <para>One box against one box, because identity is known. Guessing which of the previous
    /// frame's boxes belonged to which of this frame's reports a large movement every time two
    /// people in shot swap which of them scores highest.</para>
    ///
    /// <para><b>The threshold is scaled by how long the two frames are apart</b>, because this is
    /// the one comparison here made against the previous <em>frame</em> rather than the last thing
    /// recorded. The setting is a fraction of the frame per second, so unscaled it would mean a
    /// different speed at every detect rate — and raising the rate would quietly stop movement being
    /// reported at all.</para>
    ///
    /// <para><see cref="HasShifted"/> deliberately does <em>not</em> scale: it compares against the
    /// last sample written rather than the last frame, so its distance accumulates across however
    /// many frames it took to cover and is already rate-independent.</para>
    /// </summary>
    private bool HasMoved(BoundingBox was, BoundingBox current, TimeSpan elapsed)
    {
        double threshold = _options.MinMovementFraction * Math.Max(elapsed.TotalSeconds, 0);
        double dx = (current.X + (current.Width / 2)) - (was.X + (was.Width / 2));
        double dy = (current.Y + (current.Height / 2)) - (was.Y + (was.Height / 2));

        return Math.Sqrt((dx * dx) + (dy * dy)) > threshold;
    }

    /// <summary>
    /// Matches a track the policy has never seen before onto an episode whose own track has died,
    /// if it is standing where that object was.
    ///
    /// <para>Without this, every flicker is a new episode: a re-acquisition gets a new track id, and
    /// a distant static object sitting on its confidence threshold drops out for several frames at a
    /// time all day. It is what makes <see cref="DetectionOptions.AbsenceSeconds"/> mean what it was
    /// measured to mean — five seconds produced 263 episodes over 5.5 hours where thirty produced
    /// 36, for identical descriptions.</para>
    ///
    /// <para>Recognition is <see cref="TrackingOptions.MinIou"/> against where the object was last
    /// seen — the overlap test association uses within a frame, applied across the gap. Near 1 for
    /// the flickering static object; 0 for a person who walked out of shot and came back, who
    /// correctly gets a second episode. It gets one car leaving and another parking in the same spot
    /// inside the absence window wrong.</para>
    /// </summary>
    private ObjectState? Rejoin(TrackedObject track, HashSet<ObjectState> seen)
    {
        ObjectState? best = null;
        float bestOverlap = _options.Tracking.MinIou;

        foreach (ObjectState state in _objects)
        {
            if (seen.Contains(state)
                || !string.Equals(state.Label, track.Label, StringComparison.Ordinal)
                || state.LastBox is not { } was)
            {
                continue;
            }

            float overlap = ObjectTracker.Overlap(was.Box, track.Box);

            if (overlap >= bestOverlap)
            {
                bestOverlap = overlap;
                best = state;
            }
        }

        if (best is null)
        {
            return null;
        }

        best.TrackId = track.Id;
        Rejoined++;
        return best;
    }

    /// <summary>
    /// Whether this object turning up is news, as opposed to the scenery a camera opened on or
    /// something it was already watching coming back.
    ///
    /// <para>The setting that decides whether object detection is worth anything at all. A driveway
    /// detects a parked car in every frame for hours; describing each of those asked for 47
    /// descriptions over 88 minutes where the motion gate asked for 7.</para>
    ///
    /// <para>Two conditions ruling out different things. An arrival is a transition from
    /// <em>observed</em> absence, so it cannot be claimed for longer than this camera has been
    /// watched — which makes everything in shot at startup inventory. And nothing of this class may
    /// recently have left from where this object stands, which stops a re-acquisition announcing
    /// itself.</para>
    ///
    /// <para>Both are about this object rather than its class: a second car pulling onto a drive
    /// where one has been parked all day is news, and "has any car been absent for two minutes"
    /// would answer no and lose it.</para>
    ///
    /// <para>Dated from <see cref="TrackedObject.FirstSeen"/> rather than from now, so raising the
    /// confirmation requirement cannot turn an arrival into inventory.</para>
    /// </summary>
    private bool IsArrival(TrackedObject track)
    {
        if (track.FirstSeen - (_watchingSince ?? DateTimeOffset.MinValue)
            < TimeSpan.FromSeconds(_options.NoveltySeconds))
        {
            return false;
        }

        foreach (Departure departure in _departed)
        {
            if (string.Equals(departure.Label, track.Label, StringComparison.Ordinal)
                && ObjectTracker.Overlap(departure.Box, track.Box) >= _options.Tracking.MinIou)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Opens an episode for an object the tracker has confirmed.
    ///
    /// <para>Dated from <see cref="TrackedObject.FirstSeen"/> rather than from now: confirmation is
    /// how certainty is earned, not a claim about when the thing turned up. Dating from confirmation
    /// would start every episode late by <see cref="TrackingOptions.ConfirmSeconds"/>.</para>
    ///
    /// <para>The peak starts here rather than at the track's first frame, so the unconfirmed
    /// sightings cannot supply the clearest look — they are also the frames where an object has just
    /// entered and is usually half out of shot.</para>
    ///
    /// <para><b>Both read the track rather than the episode, and a track can outlive an episode of
    /// its own.</b> An object sitting on the camera's floor is followed by the tracker on sub-floor
    /// detections the policy discards, so its episode closes after
    /// <see cref="DetectionOptions.AbsenceSeconds"/> while the track lives on. Dating the next
    /// episode from <see cref="TrackedObject.FirstSeen"/> would backdate it to the track's birth and
    /// seed it with every sighting the track ever had, so one object reads as a pile of overlapping
    /// records claiming the same start. <see cref="Departure"/> remembers where the previous one
    /// ended.</para>
    /// </summary>
    private ObjectState OpenEpisode(TrackedObject track, DateTimeOffset now)
    {
        bool isArrival = IsArrival(track);

        // Whether this episode may raise an alert at all — the half of the decision settled here,
        // because neither part can become more true later. Whether it was ever seen clearly enough is
        // asked on every measured frame until it passes.
        //
        // Only an arrival qualifies. Presence is not news — the scenery a camera opened on and a
        // thing that came back to where it just left are both already known about — and an alert
        // that fires on them is the whole feed. IsArrival is the question this file exists to
        // answer, and the alert flag is the one consumer that most needs the answer.
        bool isEligible = isArrival && _alerts.Contains(track.Label);

        // Where the previous episode of this same track ended, if it had one.
        DateTimeOffset? previous =
            _continued.TryGetValue(track.Id, out DateTimeOffset closed) ? closed : null;

        if (previous is not null)
        {
            Continued++;
        }

        var state = new ObjectState
        {
            Label = track.Label,
            TrackId = track.Id,
            OpenedOnFrame = _frameNumber,
            Episode = new Open
            {
                Id = _newId(),
                StartedAt = previous ?? track.FirstSeen,
                LastSeenAt = now,
                PeakConfidence = track.Score,
                PeakFrameAt = now,
                BestBox = new ScoredBox(track.Box, track.Score),

                // Seeded from the sightings the tracker already has, less the one this frame is
                // about to count. FrameCount is what a consumer weighs against the episode's
                // duration to tell a solid sighting from an intermittent one, so counting only from
                // confirmation onward would report 1 for an object genuinely detected twice — an
                // understatement of the evidence, on exactly the records where it matters most.
                //
                // A continuation seeds nothing. The credit above is for the sightings that
                // earned confirmation, and a track opening its second episode was confirmed long
                // ago — carrying its lifetime total across would restate every frame the first
                // episode already reported.
                FrameCount = previous is null ? Math.Max(track.Hits - 1, 0) : 0,

                // Never set here, even well above the threshold: the frame that opens an episode is
                // processed by the promoting pass immediately after this returns, so a confident
                // arrival still alerts on its opening frame and the test lives in one place.
                IsAlert = false,
                IsAlertEligible = isEligible,
                IsArrival = isArrival,
            },
        };

        _objects.Add(state);
        Opened++;

        if (isArrival)
        {
            Arrivals++;
        }

        return state;
    }

    private ObjectEpisode Close(ObjectState state, DateTimeOffset now)
    {
        Closed++;

        // Remembered from where it was last reported rather than from the episode's best frame: the
        // question a returning track asks is "did something just leave from here", and here is where
        // it was last, not where it looked clearest.
        if (state.LastBox is { } last)
        {
            _departed.Add(new Departure(state.Label, last.Box, now));
        }

        // Recorded whether or not there was a box, because this is about the track rather than
        // about a place: a track that closed without ever being placed can still come back. Dated
        // from the last sighting for the same reason the episode is.
        _continued[state.TrackId] = state.Episode.LastSeenAt;

        // Ends when it was last *seen*, not when the absence window expired. The window is how
        // long we waited to be sure; reporting it as part of the presence would add AbsenceSeconds
        // to the duration of every episode.
        return Snapshot(state.Episode, state.Label, state.Episode.LastSeenAt);
    }

    /// <summary>
    /// Where an open episode is right now, for a consumer to draw rather than to store.
    ///
    /// Deliberately not <see cref="Snapshot"/>, which copies the whole accumulated track — right
    /// once per episode, wrong at a frame a second, where it would re-send up to
    /// <see cref="DetectionOptions.TrackMaxSamples"/> samples to say where something is now.
    /// <paramref name="position"/> rather than the best box for the same reason: the peak frame is
    /// where the object looked *clearest*, not where it is.
    ///
    /// <para><b>The box and the sample carry different things on purpose.</b> The box is the last
    /// position known and survives a frame the object was not reported on; the sample is null on
    /// exactly those frames, because the run-length track records measurements and a gap is the
    /// honest way to say nothing was seen.</para>
    /// </summary>
    private static ObjectEpisode LiveSnapshot(
        Open open,
        string label,
        DateTimeOffset now,
        ScoredBox? position,
        bool measured) => new(
        open.Id,
        label,
        open.StartedAt,
        EndedAt: null,
        open.PeakConfidence,
        open.PeakFrameAt,
        open.FrameCount,
        position,
        [new TrackSample(now, measured ? position : null)],
        open.IsAlert,
        open.IsArrival);

    // The track is copied rather than handed over. A published record that kept growing afterwards
    // would make its contents depend on when its reader got round to it.
    private static ObjectEpisode Snapshot(Open open, string label, DateTimeOffset? endedAt) => new(
        open.Id,
        label,
        open.StartedAt,
        endedAt,
        open.PeakConfidence,
        open.PeakFrameAt,
        open.FrameCount,
        open.BestBox,
        [.. open.Track],
        open.IsAlert,
        open.IsArrival);

    /// <summary>
    /// Records where the object is, if that is not already what the track says.
    ///
    /// A null <paramref name="box"/> marks an absence. One marks the whole gap however many
    /// frames it lasts, and a track never opens with one — a gap before the first sighting
    /// describes time the episode does not cover.
    /// </summary>
    private void AppendTrack(List<TrackSample> track, DateTimeOffset now, ScoredBox? box)
    {
        if (track.Count > 0)
        {
            ScoredBox? previous = track[^1].Box;

            if (previous is null && box is null)
            {
                return;
            }

            if (previous is { } was && box is { } current && !HasShifted(was.Box, current.Box))
            {
                return;
            }
        }
        else if (box is null)
        {
            return;
        }

        track.Add(new TrackSample(now, box));

        if (track.Count > _options.TrackMaxSamples)
        {
            Thin(track);
        }
    }

    /// <summary>
    /// Whether any edge has moved far enough to be worth another sample. See
    /// <see cref="DetectionOptions.TrackMinMovementFraction"/> for why this tests edges where
    /// <see cref="HasMoved"/> tests centres.
    /// </summary>
    private bool HasShifted(BoundingBox was, BoundingBox current)
    {
        double threshold = _options.TrackMinMovementFraction;

        return Math.Abs(current.X - was.X) > threshold
            || Math.Abs(current.Y - was.Y) > threshold
            || Math.Abs((current.X + current.Width) - (was.X + was.Width)) > threshold
            || Math.Abs((current.Y + current.Height) - (was.Y + was.Height)) > threshold;
    }

    /// <summary>
    /// Halves a track by dropping alternate samples, keeping the first and the newest.
    ///
    /// Thinning rather than truncating, so an episode long enough to hit the cap keeps a box over
    /// the whole of itself instead of losing one partway through. Samples keep arriving at full
    /// rate afterwards and each pass halves only what is already there, so the oldest stretches end
    /// up coarsest and the last minutes stay detailed — which is the right way round for footage
    /// somebody is scrubbing.
    /// </summary>
    private static void Thin(List<TrackSample> track)
    {
        if (track.Count < 3)
        {
            return;
        }

        int write = 1;

        for (int read = 1; read < track.Count - 1; read += 2)
        {
            track[write++] = track[read];
        }

        track[write++] = track[^1];
        track.RemoveRange(write, track.Count - write);
    }
}
