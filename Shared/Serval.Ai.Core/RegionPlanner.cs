namespace Serval.Ai;

/// <summary>Why a region is being examined. Carried so the reason a detection was found can be
/// logged and, later, so a scheduler can drop the cheapest proposals first.</summary>
public enum RegionReason
{
    /// <summary>The whole frame, on the interval that runs regardless of anything else.</summary>
    Floor,

    /// <summary>Around where a track is predicted to be, whether or not anything moved there.</summary>
    Track,

    /// <summary>A cluster of changed cells.</summary>
    Motion,

    /// <summary>
    /// One tile of a rolling whole-frame sweep, at the detector's native scale.
    ///
    /// <para>Replaces <see cref="Floor"/> on a camera whose frame would otherwise be shrunk so far into
    /// a fixed-shape input that the far field is gone before the model sees it. A tile is a coverage
    /// guarantee like the floor, not an opportunistic look like <see cref="Motion"/>, so the scheduler
    /// reserves it rather than paying for it out of the discretionary budget.</para>
    /// </summary>
    Tile,
}

/// <summary>One place to point the detector this frame.</summary>
public readonly record struct PlannedRegion(FrameRegion Region, RegionReason Reason);

/// <summary>
/// Decides where the detector looks on a given frame.
///
/// <para>Three sources — the floor, live tracks, and motion clusters. What each is for, and why
/// retention and acquisition both have to be represented, is in <c>Docs/detection.md</c> under
/// *Where the detector looks*.</para>
///
/// <para><b>Masks are consulted here, not only after inference</b>, because a track inside one buys
/// a retention crop on every frame for as long as it lives. Only masks applying to every class are
/// usable — see <see cref="DetectionMask.AppliesToEverything"/> — since a class-scoped one needs a
/// label that does not exist until the model has run.</para>
///
/// <para>Pure and stateless apart from when the floor last ran and the masks it was built with, so
/// what the detector is asked to do can be pinned in tests without a model, a camera or a frame.</para>
/// </summary>
/// <param name="masks">This camera's masks, or null for none. The class-scoped and the unusable are
/// discarded on construction; the rest are a session constant, which is safe because
/// <c>AiSessionSignature</c> hashes them and an edit restarts the camera.</param>
public sealed class RegionPlanner(RegionOptions options, IReadOnlyList<DetectionMask>? masks = null)
{
    private readonly DetectionMask[] _masks =
        masks is null
            ? []
            : [.. masks.Where(static m => m.IsUsable && m.AppliesToEverything)];

    private DateTimeOffset _lastFloorAt = DateTimeOffset.MinValue;

    /// <summary>How far through a tile sweep this camera is. At or past the tile count means the sweep is
    /// finished and the next one waits for <see cref="RegionOptions.FloorSeconds"/>.</summary>
    private int _sweepIndex = int.MaxValue;

    /// <summary>Retention crops withheld because the track stood inside a mask.</summary>
    public long MaskedTracks { get; private set; }

    /// <summary>Motion crops withheld because the cluster lay wholly inside a mask.</summary>
    public long MaskedRegions { get; private set; }

    /// <summary>
    /// Plans one frame.
    ///
    /// <paramref name="cropping"/> is the resolved answer from
    /// <see cref="RegionOptions.ShouldCrop"/> rather than something worked out here, because it
    /// depends on the detector's input size and this type has no business knowing about detectors.
    /// When it is false the result is always the whole frame, on every frame — the behaviour of a
    /// detector with no region support at all.
    /// </summary>
    /// <param name="tracks">What the tracker believed was present as of the previous frame, used to
    /// decide where to look on this one. One frame stale by construction — the tracker cannot be
    /// updated until the detector has run, which is what this call is planning — and
    /// <see cref="RegionOptions.PaddingFraction"/> is what absorbs the gap.</param>
    /// <param name="floorTiles">Tiles covering the whole frame at the detector's native scale, or null
    /// to make the whole-frame pass as a single shrunken look. Resolved by the caller from
    /// <see cref="RegionOptions.ShouldTileFloor"/> and <see cref="DetectorShapes.Tiles"/>, for the same
    /// reason <paramref name="cropping"/> is: it depends on the detector's input and this type has no
    /// business knowing about detectors.</param>
    /// <param name="maxRegion">The largest crop that can still reach the detector at
    /// <see cref="RegionOptions.MinRegionScale"/>, or null for no bound. Resolved by the caller from
    /// <see cref="RegionOptions.MaxRegion"/>, arriving already in frame pixels for the same reason the
    /// two above do.
    ///
    /// <para>It bounds the opportunistic half only. The floor is the whole frame by definition and a
    /// sweep tile is the detector's own shape already, so neither has anything to bound.</para></param>
    /// <param name="inputAspect">The detector input's width over its height, or zero to leave crop shapes
    /// alone. A bare ratio rather than the input itself, so this stays a number the planner can apply
    /// without knowing what produced it — the same boundary the three arguments above keep.
    ///
    /// <para>Crops are grown toward it, which costs nothing: the scale is set by whichever axis is
    /// squeezed hardest, so the slack one is free to fill. It affects the opportunistic half only, for
    /// the reason <paramref name="maxRegion"/> does.</para></param>
    /// <returns>Regions to examine, or empty when this frame needs no inference at all. None is ever
    /// larger than <paramref name="maxRegion"/>.</returns>
    public IReadOnlyList<PlannedRegion> Plan(
        DateTimeOffset now,
        int frameWidth,
        int frameHeight,
        bool cropping,
        ReadOnlySpan<byte> changedCells,
        int gridWidth,
        int gridHeight,
        IReadOnlyList<TrackedObject> tracks,
        IReadOnlyList<FrameRegion>? floorTiles = null,
        (int Width, int Height)? maxRegion = null,
        double inputAspect = 0)
    {
        var whole = new FrameRegion(0, 0, frameWidth, frameHeight);
        bool floorDue = now - _lastFloorAt >= TimeSpan.FromSeconds(Math.Max(options.FloorSeconds, 0));
        bool tiling = cropping && floorTiles is { Count: > 1 };

        if (!cropping)
        {
            // Every frame, not every FloorSeconds. Without crops a motion cluster could only propose
            // the whole frame, which is an argument for ignoring motion here, not for looking less
            // often. FloorSeconds is a minimum guarantee for a scheme that would otherwise look only
            // where something moved; reading it as the whole schedule caps detection at 0.2 fps on
            // the default, below the rate at which boxes in consecutive frames still overlap, so
            // ObjectTracker associates nothing and every object becomes a one-frame episode.
            //
            // How often this runs is bounded elsewhere: Detection:MaxFps decides whether a frame is
            // considered, and InferenceScheduler what the host can afford across every camera.
            _lastFloorAt = now;
            return [new PlannedRegion(whole, RegionReason.Floor)];
        }

        // A tile sweep replaces the whole-frame pass rather than accompanying it, so the "crops would
        // re-examine what the floor already covers" argument below still holds: no area is looked at
        // twice. What changes is that covering the frame now takes several frames instead of one.
        //
        // One tile per frame, and only one. That keeps the guaranteed part of a frame's cost at exactly
        // one inference — the flat, predictable figure InferenceScheduler budgets against — where firing
        // the whole sweep at once would spike it by the tile count. A five-tile sweep at 2 fps completes
        // in 2.5s, comfortably inside the default 5s interval, so coverage is not made less frequent.
        //
        // Motion and track crops still run alongside, unlike on a whole-frame floor: a tile covers a
        // fraction of the picture, so a cluster somewhere else is genuinely new information rather than a
        // re-examination.
        if (tiling)
        {
            if (_sweepIndex >= floorTiles!.Count)
            {
                if (!floorDue)
                {
                    // Sweep finished and the next one is not due. Fall through to motion and tracks.
                    return PlanCrops(now, frameWidth, frameHeight, changedCells, gridWidth, gridHeight, tracks, inputAspect, maxRegion);
                }

                _sweepIndex = 0;
                _lastFloorAt = now;
            }

            FrameRegion tile = floorTiles[_sweepIndex++];

            List<PlannedRegion> withTile = [new PlannedRegion(tile, RegionReason.Tile)];
            withTile.AddRange(
                PlanCrops(now, frameWidth, frameHeight, changedCells, gridWidth, gridHeight, tracks, inputAspect, maxRegion));

            return withTile;
        }

        // A floor frame examines everything, so crops would re-examine what it already covers.
        // Running it alone keeps the expensive frame flat and predictable, which is what lets
        // InferenceScheduler budget for it exactly.
        if (floorDue)
        {
            _lastFloorAt = now;
            return [new PlannedRegion(whole, RegionReason.Floor)];
        }

        return PlanCrops(now, frameWidth, frameHeight, changedCells, gridWidth, gridHeight, tracks, inputAspect, maxRegion);
    }

    /// <summary>
    /// The opportunistic half: retention crops around live tracks, then motion clusters.
    ///
    /// Split out so a tile sweep can run alongside it without duplicating any of it.
    /// </summary>
    private IReadOnlyList<PlannedRegion> PlanCrops(
        DateTimeOffset now,
        int frameWidth,
        int frameHeight,
        ReadOnlySpan<byte> changedCells,
        int gridWidth,
        int gridHeight,
        IReadOnlyList<TrackedObject> tracks,
        double inputAspect,
        (int Width, int Height)? maxRegion)
    {
        int cap = Math.Max(options.MaxPerFrame, 1);
        List<PlannedRegion> planned = [];
        List<FrameRegion> taken = [];

        // Tracks before motion: losing a track fragments an episode that already exists, where
        // missing a motion cluster costs at worst a later acquisition the floor still guarantees.
        // Merging means a group standing together is one crop rather than one each.
        foreach (FrameRegion region in Tracked(tracks, frameWidth, frameHeight, maxRegion))
        {
            if (planned.Count >= cap)
            {
                break;
            }

            int before = taken.Count;
            MotionRegions.AddMerged(taken, region, maxRegion);

            if (taken.Count > before)
            {
                Emit(planned, region, RegionReason.Track, frameWidth, frameHeight, inputAspect, maxRegion, cap);
            }
        }

        IReadOnlyList<FrameRegion> motion = MotionRegions.Cluster(
            changedCells, gridWidth, gridHeight, frameWidth, frameHeight, options, maxRegion);

        foreach (FrameRegion region in motion)
        {
            if (planned.Count >= cap)
            {
                break;
            }

            if (IsMasked(region, frameWidth, frameHeight))
            {
                MaskedRegions++;
                continue;
            }

            int before = taken.Count;
            MotionRegions.AddMerged(taken, region, maxRegion);

            if (taken.Count > before)
            {
                Emit(planned, region, RegionReason.Motion, frameWidth, frameHeight, inputAspect, maxRegion, cap);
            }
        }

        return planned;
    }

    /// <summary>
    /// Adds one accepted crop to the plan, in as many pieces as the bound needs.
    ///
    /// <para><b>The cap counts pieces rather than crops</b>, because it exists to make a frame's cost
    /// predictable and a piece costs an inference exactly as a whole crop does. Counting the crop
    /// instead would let one oversized cluster spend several times the budget the operator set.</para>
    ///
    /// <para><b>Pieces keep the reason they were cut from</b>, and that is not cosmetic:
    /// <see cref="InferenceScheduler"/> reserves <see cref="RegionReason.Floor"/> and
    /// <see cref="RegionReason.Tile"/> outside the budget and never sheds them, on the strength of each
    /// being one inference per camera per frame. Calling these tiles because they are tile-shaped would
    /// let a single camera emit several unsheddable inferences in one frame and break that guarantee.
    /// An opportunistic look must stay sheddable however it is shaped.</para>
    /// </summary>
    private void Emit(
        List<PlannedRegion> planned,
        FrameRegion region,
        RegionReason reason,
        int frameWidth,
        int frameHeight,
        double inputAspect,
        (int Width, int Height)? maxRegion,
        int cap)
    {
        // Shaped before cutting rather than after. A piece is already the bound's shape, so shaping it
        // afterwards would be a no-op on the pieces while leaving the region they came from unshaped —
        // and it is the region that decides how much of the frame gets covered.
        FrameRegion shaped =
            MotionRegions.ShapeToAspect(region, frameWidth, frameHeight, inputAspect);

        foreach (FrameRegion piece in
            MotionRegions.Split(shaped, maxRegion, options.TileOverlapFraction))
        {
            if (planned.Count >= cap)
            {
                return;
            }

            planned.Add(new PlannedRegion(piece, reason));
        }
    }

    /// <summary>
    /// A crop around each track, padded and merged. <see cref="TrackedObject.Box"/> is the filter's
    /// own estimate rather than the last measurement, so a coasting subject is looked for where it
    /// is going rather than where it was last seen.
    /// </summary>
    private IReadOnlyList<FrameRegion> Tracked(
        IReadOnlyList<TrackedObject> tracks,
        int frameWidth,
        int frameHeight,
        (int Width, int Height)? maxRegion)
    {
        if (tracks.Count == 0)
        {
            return [];
        }

        double padX = frameWidth * options.PaddingFraction;
        double padY = frameHeight * options.PaddingFraction;
        List<FrameRegion> regions = [];

        foreach (TrackedObject track in tracks)
        {
            BoundingBox box = track.Box;

            // The same ground point ObjectEventPolicy drops a track for standing on, so a track the
            // policy would silence is never spent a crop on.
            //
            // A track whose predicted box drifts into a mask stops being retained and coasts. It is
            // reacquired by unmasked motion when it leaves, or by the floor pass if it was never
            // inside. Nothing being reported is lost — the policy was suppressing it throughout.
            if (IsMasked(box))
            {
                MaskedTracks++;
                continue;
            }

            // Bounded here as well as where these are added to the frame's plan, because this is a
            // merge pass in its own right: two subjects standing together fold into one crop before
            // the caller ever sees them, and an unbounded fold here would hand on a region already too
            // large to be worth looking at.
            MotionRegions.AddMerged(
                regions,
                MotionRegions.Fit(
                    (box.X * frameWidth) - padX,
                    (box.Y * frameHeight) - padY,
                    ((box.X + box.Width) * frameWidth) + padX,
                    ((box.Y + box.Height) * frameHeight) + padY,
                    frameWidth,
                    frameHeight,
                    options.MinSizeFraction),
                maxRegion);
        }

        return regions;
    }

    /// <summary>
    /// Whether a track stands inside a mask, at the same bottom-centre point
    /// <see cref="ObjectEventPolicy"/> tests.
    /// </summary>
    private bool IsMasked(BoundingBox box)
    {
        foreach (DetectionMask mask in _masks)
        {
            if (mask.Contains(box))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a proposed crop lies wholly inside a single mask.
    ///
    /// <para><b>All four corners, and one mask rather than the union.</b> A motion cluster has no
    /// ground point to test, so the question is whether anything worth finding could be in it, and
    /// only a rectangle with no unmasked pixel certainly cannot. Someone stepping off the road onto
    /// the drive straddles the edge, keeps its crop, and lets the policy decide.</para>
    ///
    /// <para>One mask rather than the union because two shapes can cover a rectangle between them
    /// while leaving a gap along their shared edge that a corner test cannot see. Erring towards
    /// examining is the only safe direction: a crop wrongly withheld is a subject silently never
    /// detected, where one wrongly cut costs a single inference.</para>
    /// </summary>
    private bool IsMasked(FrameRegion region, int frameWidth, int frameHeight)
    {
        if (_masks.Length == 0 || frameWidth <= 0 || frameHeight <= 0)
        {
            return false;
        }

        double left = (double)region.X / frameWidth;
        double top = (double)region.Y / frameHeight;
        double right = (double)(region.X + region.Width) / frameWidth;
        double bottom = (double)(region.Y + region.Height) / frameHeight;

        foreach (DetectionMask mask in _masks)
        {
            if (mask.Contains(left, top)
                && mask.Contains(right, top)
                && mask.Contains(right, bottom)
                && mask.Contains(left, bottom))
            {
                return true;
            }
        }

        return false;
    }
}
