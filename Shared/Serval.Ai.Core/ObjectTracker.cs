namespace Serval.Ai;

/// <summary>What a track is currently claiming about itself.</summary>
public enum TrackState
{
    /// <summary>Seen, but not for long enough to be believed. Not reported to anything downstream:
    /// a confident one-frame ghost looks exactly like this on its first frame.</summary>
    Tentative,

    /// <summary>Seen for long enough, and seen this frame.</summary>
    Confirmed,

    /// <summary>Confirmed, but not matched by the most recent frame — occluded, missed, or gone.
    /// Still reported, because a subject behind a parked car has not left.</summary>
    Coasting,
}

/// <summary>
/// One object followed across frames.
///
/// <paramref name="Id"/> is unique for the life of the tracker and never reused, so a consumer can
/// treat it as identity without checking anything else.
///
/// <paramref name="Box"/> is the filter's estimate rather than the last measurement — where the
/// subject is predicted to be, not where it was last seen. While coasting the estimate is all there
/// is.
///
/// <paramref name="Score"/> is the last measured confidence and is deliberately not decayed while
/// coasting. A decayed score would read as "the detector is becoming unsure", which it is not; it
/// was not asked.
/// </summary>
public sealed record TrackedObject(
    int Id,
    string Label,
    BoundingBox Box,
    float Score,
    TrackState State,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    int Hits);

/// <summary>
/// Turns per-frame detections into objects with identity, so that "a person" becomes "this person,
/// since 14:02:11".
///
/// <para><b>Association depends on the detect rate</b>, because it asks whether a box this frame
/// overlaps one from the last. A walking subject covers about 1.4 m between frames at 1 fps and
/// consecutive boxes frequently do not overlap at all, so the question has no answer; at 2 fps the
/// gap is ~70 cm and it works, at 5 fps ~28 cm and it works comfortably. This is most of what
/// <c>Detection:MaxFps</c> is for.</para>
///
/// <para><b>Everything here is in seconds, never in frames.</b> Tuned in frame counts, a tracker is
/// retuned silently by any change to the detect rate — confirmation taking two seconds at 1 fps
/// takes 0.4 s at 5 fps, and a coast window that carried a subject through a two-second occlusion
/// stops doing so. In seconds, raising the rate raises the evidence behind a decision and leaves
/// its timing alone.</para>
///
/// <para>The cost of identity is two mistakes class-presence cannot make: two subjects crossing can
/// swap ids, and a subject occluded past the coast window becomes two tracks.</para>
///
/// Pure and allocation-light, in <c>Abstractions</c> for the same reason the gates are: what an
/// episode *is* should be testable on a fresh clone against synthetic trajectories, with no model
/// on disk and no native dependency.
/// </summary>
public sealed class ObjectTracker
{
    private readonly TrackingOptions _options;
    private readonly List<Track> _tracks = [];
    private int _nextId;

    public ObjectTracker(TrackingOptions options) => _options = options;

    /// <summary>
    /// Tracks that were seen once and never again — the confident one-frame ghost, an IR-lit bush
    /// read as a person.
    ///
    /// The number to watch when tuning <see cref="TrackingOptions.ConfirmSeconds"/> on a camera that
    /// ghosts: it is how much noise the confirmation requirement is absorbing, which a count of the
    /// episodes that survived it cannot show.
    /// </summary>
    public long Ghosts { get; private set; }

    /// <summary>Tracks that have earned the right to be believed, including coasting ones.</summary>
    public IEnumerable<TrackedObject> Confirmed =>
        _tracks.Where(static t => t.State != TrackState.Tentative).Select(static t => t.Snapshot());

    /// <summary>
    /// Every track, tentative ones included — what <see cref="RegionPlanner"/> decides where to look
    /// from.
    ///
    /// <para>Deliberately not <see cref="Confirmed"/>, which answers "what should be believed" — the
    /// right question for a record and the wrong one for a crop. A tentative track is an open
    /// question and another look settles it. Planning from confirmed tracks alone leaves a new
    /// object with no crop until something else finds it, so it can only be confirmed on a floor
    /// pass: measured on five real cameras, a parked truck detected ten times in twenty-nine seconds
    /// instead of on every frame.</para>
    /// </summary>
    public IEnumerable<TrackedObject> Live => _tracks.Select(static t => t.Snapshot());

    /// <summary>
    /// Advances every track to <paramref name="at"/>, matches this frame's detections against them,
    /// and returns what is believed to be present.
    ///
    /// <para><paramref name="detections"/> may be empty, and an empty frame is not the same as no
    /// frame: it says the detector looked and found nothing, which is what lets a coasting track
    /// eventually die. A frame that was never examined must not be passed here at all — see
    /// <see cref="ObjectEpisode"/> for why "nothing is there" and "nothing was looked at" have to
    /// stay distinguishable.</para>
    /// </summary>
    public IReadOnlyList<TrackedObject> Update(
        IReadOnlyList<DetectedObject> detections,
        DateTimeOffset at)
    {
        foreach (Track track in _tracks)
        {
            track.Predict(at, _options);
        }

        Associate(detections, at);
        Retire(at);

        return [.. _tracks.Where(static t => t.State != TrackState.Tentative).Select(static t => t.Snapshot())];
    }

    /// <summary>
    /// Greedy matching, best overlap first.
    ///
    /// <para>Greedy rather than Hungarian: the assignment it gets wrong is two tracks preferring the
    /// same detection, which is the crossing-subjects case, where geometry alone is already
    /// ambiguous. An optimal solver would swap identities with more confidence rather than less.
    /// Appearance would settle it; a cost matrix will not.</para>
    ///
    /// <para>Matching is confined to one label, so a car cannot become a person. The price is that a
    /// detector flickering between two plausible labels — car and truck is the common pair —
    /// fragments one object into two tracks, which is the visible failure rather than the invisible
    /// one.</para>
    /// </summary>
    private void Associate(IReadOnlyList<DetectedObject> detections, DateTimeOffset at)
    {
        var taken = new bool[detections.Count];
        var matched = new HashSet<Track>();

        // Every candidate pair above the overlap floor, strongest first. Small enough to sort
        // outright: tracks and detections are both bounded by what a frame can hold.
        List<(float Iou, Track Track, int Detection)> pairs = [];

        foreach (Track track in _tracks)
        {
            for (int i = 0; i < detections.Count; i++)
            {
                if (!string.Equals(track.Label, detections[i].Label, StringComparison.Ordinal))
                {
                    continue;
                }

                float iou = Overlap(track.Box, detections[i].Box);

                if (iou >= _options.MinIou)
                {
                    pairs.Add((iou, track, i));
                }
            }
        }

        foreach ((float _, Track track, int detection) in pairs.OrderByDescending(static p => p.Iou))
        {
            if (taken[detection] || matched.Contains(track))
            {
                continue;
            }

            taken[detection] = true;
            matched.Add(track);
            track.Observe(detections[detection], at, _options);
        }

        // Before the new tracks are added, so a track created by this frame is not immediately told
        // it was missed by it.
        foreach (Track track in _tracks)
        {
            if (!matched.Contains(track))
            {
                track.Miss();
            }
        }

        for (int i = 0; i < detections.Count; i++)
        {
            if (!taken[i] && _tracks.Count < _options.MaxTracks)
            {
                _tracks.Add(new Track(_nextId++, detections[i], at));
            }
        }
    }

    /// <summary>
    /// Drops what is no longer believable.
    ///
    /// A tentative track dies the moment it is missed: it exists precisely because one sighting is
    /// not evidence, and a second miss would keep a ghost alive for the whole coast window. A
    /// confirmed one coasts for <see cref="TrackingOptions.CoastSeconds"/> first, which is what
    /// carries a subject behind a parked car or through a frame the detector simply missed.
    /// </summary>
    private void Retire(DateTimeOffset at)
    {
        _tracks.RemoveAll(t =>
        {
            bool dead = t.State switch
            {
                TrackState.Tentative => t.LastSeen < at,
                _ => (at - t.LastSeen).TotalSeconds > _options.CoastSeconds,
            };

            if (dead && t.State == TrackState.Tentative)
            {
                Ghosts++;
            }

            return dead;
        });
    }

    /// <summary>Intersection over union. Zero for boxes that do not touch.</summary>
    internal static float Overlap(BoundingBox a, BoundingBox b)
    {
        float x = MathF.Max(0, MathF.Min(a.X + a.Width, b.X + b.Width) - MathF.Max(a.X, b.X));
        float y = MathF.Max(0, MathF.Min(a.Y + a.Height, b.Y + b.Height) - MathF.Max(a.Y, b.Y));
        float intersection = x * y;
        float union = (a.Width * a.Height) + (b.Width * b.Height) - intersection;

        return union > 0 ? intersection / union : 0;
    }

    /// <summary>
    /// A track's mutable half. Separate from <see cref="TrackedObject"/> so what is handed out is an
    /// immutable snapshot — a consumer holding a track across frames would otherwise watch it change
    /// underneath.
    /// </summary>
    private sealed class Track
    {
        private Axis _x;
        private Axis _y;
        private Axis _width;
        private Axis _height;
        private DateTimeOffset _lastStep;

        public Track(int id, DetectedObject detection, DateTimeOffset at)
        {
            Id = id;
            Label = detection.Label;
            Score = detection.Score;
            FirstSeen = at;
            LastSeen = at;
            _lastStep = at;
            Hits = 1;
            State = TrackState.Tentative;

            BoundingBox box = detection.Box;
            _x = Axis.Start(box.X + (box.Width / 2));
            _y = Axis.Start(box.Y + (box.Height / 2));
            _width = Axis.Start(box.Width);
            _height = Axis.Start(box.Height);
        }

        public int Id { get; }

        public string Label { get; }

        public float Score { get; private set; }

        public TrackState State { get; private set; }

        public DateTimeOffset FirstSeen { get; }

        public DateTimeOffset LastSeen { get; private set; }

        public int Hits { get; private set; }

        public BoundingBox Box => new(
            _x.Position - (_width.Position / 2),
            _y.Position - (_height.Position / 2),
            _width.Position,
            _height.Position);

        /// <summary>Advances the estimate to <paramref name="at"/> without a measurement.</summary>
        public void Predict(DateTimeOffset at, TrackingOptions options)
        {
            double dt = (at - _lastStep).TotalSeconds;

            if (dt <= 0)
            {
                return;
            }

            _lastStep = at;
            _x.Predict(dt, options.ProcessNoise);
            _y.Predict(dt, options.ProcessNoise);
            _width.Predict(dt, options.ProcessNoise);
            _height.Predict(dt, options.ProcessNoise);
        }

        /// <summary>Folds a measurement in and re-decides whether this track is believable.</summary>
        public void Observe(DetectedObject detection, DateTimeOffset at, TrackingOptions options)
        {
            BoundingBox box = detection.Box;
            _x.Update(box.X + (box.Width / 2), options.MeasurementNoise);
            _y.Update(box.Y + (box.Height / 2), options.MeasurementNoise);
            _width.Update(box.Width, options.MeasurementNoise);
            _height.Update(box.Height, options.MeasurementNoise);

            Score = detection.Score;
            LastSeen = at;
            Hits++;

            // Both halves, because either alone admits a ghost. Elapsed time alone confirms a single
            // sighting seen again a second later with nothing in between; hit count alone confirms
            // in 0.4 s at 5 fps what takes 2 s at 1 fps.
            if (State == TrackState.Tentative
                && Hits >= 2
                && (at - FirstSeen).TotalSeconds >= options.ConfirmSeconds)
            {
                State = TrackState.Confirmed;
            }
            else if (State == TrackState.Coasting)
            {
                State = TrackState.Confirmed;
            }
        }

        public TrackedObject Snapshot() =>
            new(Id, Label, Box, Score, State, FirstSeen, LastSeen, Hits);

        /// <summary>Records that this frame did not match. A confirmed track starts coasting; a
        /// tentative one is left alone for <see cref="Retire"/> to drop.</summary>
        public void Miss()
        {
            if (State == TrackState.Confirmed)
            {
                State = TrackState.Coasting;
            }
        }
    }

    /// <summary>
    /// One axis of a constant-velocity Kalman filter: a position, a rate, and the 2×2 covariance
    /// between them.
    ///
    /// <para><b>Four independent axes rather than one coupled filter.</b> A single state over
    /// centre, size and their rates needs general matrix inversion, and that would put a
    /// linear-algebra dependency into a project whose point is that the gate logic has none.
    /// Decoupling costs the cross terms — the filter cannot learn that a subject walking towards the
    /// camera grows as it descends — and at these frame rates those are worth less than the
    /// dependency.</para>
    ///
    /// <para>Sized in frame fractions, like every box here, so the noise constants mean the same
    /// thing on every camera regardless of resolution.</para>
    /// </summary>
    private struct Axis
    {
        private float _p00;
        private float _p01;
        private float _p10;
        private float _p11;

        public float Position { get; private set; }

        public float Velocity { get; private set; }

        /// <summary>
        /// A first sighting: position known as well as the detector knows it, rate unknown. The
        /// large initial rate variance lets the second measurement set the velocity almost outright
        /// rather than dragging it up over several frames, which is the difference between a track
        /// that follows a subject and one that trails behind it.
        /// </summary>
        public static Axis Start(float position) => new()
        {
            Position = position,
            Velocity = 0,
            _p00 = 0.01f,
            _p11 = 1f,
        };

        public void Predict(double dt, float processNoise)
        {
            float step = (float)dt;

            Position += Velocity * step;

            // P = F·P·Fᵀ + Q, written out for F = [[1, dt], [0, 1]].
            float p00 = _p00 + (step * (_p10 + _p01)) + (step * step * _p11);
            float p01 = _p01 + (step * _p11);
            float p10 = _p10 + (step * _p11);
            float p11 = _p11;

            float q = processNoise * step;
            _p00 = p00 + q;
            _p01 = p01;
            _p10 = p10;
            _p11 = p11 + q;
        }

        public void Update(float measurement, float measurementNoise)
        {
            // H = [1, 0], so the innovation covariance is just P₀₀ plus the measurement noise and
            // the gain falls out without an inversion.
            float s = _p00 + measurementNoise;

            if (s <= 0)
            {
                Position = measurement;
                return;
            }

            float k0 = _p00 / s;
            float k1 = _p10 / s;
            float residual = measurement - Position;

            Position += k0 * residual;
            Velocity += k1 * residual;

            float p00 = _p00;
            float p01 = _p01;
            _p00 -= k0 * p00;
            _p01 -= k0 * p01;
            _p10 -= k1 * p00;
            _p11 -= k1 * p01;
        }
    }
}
