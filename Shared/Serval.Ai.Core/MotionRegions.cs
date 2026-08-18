namespace Serval.Ai;

/// <summary>
/// Turns a changed-cell mask into a short list of rectangles in the frame worth looking at.
///
/// <para>This is the whole of motion's job once a detector exists. It no longer decides
/// <em>whether</em> to look — that would make "nothing is there" and "nothing was looked at" the same
/// observation, which is exactly what an episode's duration cannot survive. It decides
/// <em>where</em>, and the payoff is resolution: a crop taken around a changed cluster reaches the
/// model at the frame's own pixels, where a whole frame would have been shrunk until a distant person
/// was too few pixels to find.</para>
///
/// <para>Connected components on the compare grid rather than contours on the full frame. The grid is
/// a few thousand cells, so this costs microseconds and needs no image library — which is what keeps
/// it here in <c>Abstractions</c>, testable on a fresh clone, beside the rest of the code that
/// decides what the expensive models are asked to do.</para>
/// </summary>
internal static class MotionRegions
{
    /// <summary>
    /// Clusters the changed cells and returns their bounding boxes in frame pixels, largest first.
    /// </summary>
    /// <param name="changedCells">One byte per compare cell, non-zero where the frame changed.</param>
    /// <param name="gridWidth">Compare grid width, matching <see cref="MotionOptions.CompareWidth"/>.</param>
    /// <param name="gridHeight">Compare grid height.</param>
    /// <param name="frameWidth">Frame width the regions are expressed in.</param>
    /// <param name="frameHeight">Frame height.</param>
    /// <param name="options">Cluster size floor, padding and cap.</param>
    /// <param name="max">The largest a region may be, or null for no bound. Clusters are merged within
    /// it and anything still over it is cut, so no rectangle returned here is ever larger.</param>
    /// <param name="min">The smallest a region may be, or null for no bound — the size below which
    /// reaching the detector's input would mean enlarging it. Applied by <see cref="Fit"/>, so a motion
    /// crop and a track crop of the same subject are bounded identically.</param>
    public static IReadOnlyList<FrameRegion> Cluster(
        ReadOnlySpan<byte> changedCells,
        int gridWidth,
        int gridHeight,
        int frameWidth,
        int frameHeight,
        RegionOptions options,
        (int Width, int Height)? max = null,
        (int Width, int Height)? min = null)
    {
        if (gridWidth <= 0 || gridHeight <= 0
            || changedCells.Length < gridWidth * gridHeight)
        {
            return [];
        }

        Span<bool> visited = gridWidth * gridHeight <= 8192
            ? stackalloc bool[gridWidth * gridHeight]
            : new bool[gridWidth * gridHeight];

        List<Blob> clusters = [];
        Span<int> queue = new int[gridWidth * gridHeight];

        for (int start = 0; start < gridWidth * gridHeight; start++)
        {
            if (visited[start] || changedCells[start] == 0)
            {
                continue;
            }

            // Iterative rather than recursive: a frame that changes everywhere — the lighting flip
            // this is not supposed to see, arriving anyway because a caller passed the mask through
            // — would otherwise be a stack overflow rather than a wasted pass.
            int head = 0;
            int tail = 0;
            queue[tail++] = start;
            visited[start] = true;

            int minX = gridWidth, minY = gridHeight, maxX = -1, maxY = -1, cells = 0;

            while (head < tail)
            {
                int index = queue[head++];
                int x = index % gridWidth;
                int y = index / gridWidth;

                cells++;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);

                // Eight-connected, so a diagonal streak of changed cells — which is what a person
                // walking across a coarse grid actually produces — stays one cluster instead of
                // fragmenting into a region per cell.
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        int ny = y + dy;

                        if (nx < 0 || ny < 0 || nx >= gridWidth || ny >= gridHeight)
                        {
                            continue;
                        }

                        int neighbour = (ny * gridWidth) + nx;
                        if (!visited[neighbour] && changedCells[neighbour] != 0)
                        {
                            visited[neighbour] = true;
                            queue[tail++] = neighbour;
                        }
                    }
                }
            }

            if (cells >= Math.Max(options.MinCells, 1))
            {
                clusters.Add(new Blob(minX, minY, maxX, maxY, cells));
            }
        }

        if (clusters.Count == 0)
        {
            return [];
        }

        // Biggest first, so the cap below keeps the clusters most likely to be something and drops
        // the ones most likely to be a branch.
        clusters.Sort(static (a, b) => b.Cells.CompareTo(a.Cells));

        List<FrameRegion> regions = [];
        double cellWidth = (double)frameWidth / gridWidth;
        double cellHeight = (double)frameHeight / gridHeight;

        foreach (Blob cluster in clusters)
        {
            if (regions.Count >= Math.Max(options.MaxPerFrame, 1))
            {
                break;
            }

            // The mask is coarse, and an object's edges routinely fall outside the cells that
            // changed — a person's legs move while their torso does not. Padding puts the whole of
            // them back inside the crop rather than handing the model a waist.
            double padX = frameWidth * options.PaddingFraction;
            double padY = frameHeight * options.PaddingFraction;

            double left = (cluster.MinX * cellWidth) - padX;
            double top = (cluster.MinY * cellHeight) - padY;
            double right = ((cluster.MaxX + 1) * cellWidth) + padX;
            double bottom = ((cluster.MaxY + 1) * cellHeight) + padY;

            AddMerged(
                regions,
                Fit(left, top, right, bottom, frameWidth, frameHeight, options.MinSizeFraction, min),
                max);
        }

        // Cutting after every merge has settled, rather than as each cluster arrives. A piece put back
        // into the list would be a candidate for the next cluster's merge, and the bound would hold
        // only by luck of the ordering; splitting last means nothing can grow after being cut.
        if (regions.TrueForAll(region => Fits(region, max)))
        {
            return regions;
        }

        List<FrameRegion> split = [];

        foreach (FrameRegion region in regions)
        {
            split.AddRange(Split(region, max, options.TileOverlapFraction));
        }

        return split;
    }

    /// <summary>
    /// A crop from raw edges: clamped inside the frame, then widened if it is too tight to
    /// recognise anything in.
    ///
    /// Shared with <see cref="RegionPlanner"/>'s tracked-object crops so that a region around a
    /// subject the tracker is following has exactly the same geometry as one around the motion that
    /// first found them — otherwise a subject would be handed to the detector differently depending
    /// on which proposer happened to name it, and would score differently for no reason in the scene.
    /// </summary>
    internal static FrameRegion Fit(
        double left,
        double top,
        double right,
        double bottom,
        int frameWidth,
        int frameHeight,
        double minSizeFraction,
        (int Width, int Height)? min = null) =>
        Grow(
            Clamp(left, top, right, bottom, frameWidth, frameHeight),
            frameWidth,
            frameHeight,
            minSizeFraction,
            min);

    /// <summary>
    /// Adds a crop, folding it into an existing one it overlaps as long as the result stays inside
    /// <paramref name="max"/>.
    ///
    /// <para>Overlapping crops cost an inference each and hand the tracker two boxes for one object.
    /// Merging is cheaper than deduplicating the results afterwards, and it is what stops a group of
    /// people standing together from spending the whole per-frame budget on near-identical views of
    /// the same patch of ground.</para>
    ///
    /// <para><b>But a union is itself a region, so unions chain</b>, and left unbounded they run away:
    /// measured over four minutes on a 1536x432 panoramic, every one of 479 retention crops that found
    /// something was between 1244 and 1536 pixels wide — a quarter-scale look at nearly the whole
    /// frame, where the crop for one tracked object should have been 384. Three subjects spread across
    /// a wide frame are enough, because the first union reaches far enough to overlap the third crop.
    /// At that scale a small detector invents objects (see
    /// <see cref="RegionOptions.MinRegionScale"/>), which start tracks, which propose more crops that
    /// merge — so the runaway is self-sustaining rather than a transient.</para>
    ///
    /// <para>Refusing the union rather than trimming it afterwards is what makes the bound hold: two
    /// crops that would exceed it stay two crops, each still a close look at its own subject, which is
    /// what a retention crop was for. Crops that overlap and fit merge exactly as they always did.</para>
    /// </summary>
    /// <param name="max">The largest a region may be, or null for no bound.</param>
    internal static void AddMerged(
        List<FrameRegion> regions, FrameRegion region, (int Width, int Height)? max = null)
    {
        for (int i = 0; i < regions.Count; i++)
        {
            if (!Overlaps(regions[i], region))
            {
                continue;
            }

            FrameRegion union = Union(regions[i], region);

            if (Fits(union, max))
            {
                regions[i] = union;
                return;
            }

            // Keep looking. A later region may overlap this one too and be close enough to absorb it,
            // and stopping at the first refusal would add a duplicate crop while a legal merge existed.
        }

        regions.Add(region);
    }

    /// <summary>
    /// Cuts a region that is too large on its own into pieces that fit, or returns it unchanged.
    ///
    /// <para>For what a bounded merge cannot help with: a single cluster of changed cells covering half
    /// the scene, or one tracked object whose padded box is already oversized. There is nothing to
    /// un-merge there, so the choice is to look at it badly or to look at it in pieces.</para>
    ///
    /// <para>Pieces overlap for the reason a floor sweep's tiles do — an object on a seam must be whole
    /// in at least one of them — and the covering maths is <see cref="DetectorShapes.Tiles"/> rather
    /// than a second implementation of it, offset from the region's own origin.</para>
    /// </summary>
    /// <param name="region">The region to cut.</param>
    /// <param name="max">The largest a region may be, or null for no bound.</param>
    /// <param name="overlapFraction">Fraction of a piece that neighbouring pieces share.</param>
    internal static IReadOnlyList<FrameRegion> Split(
        FrameRegion region, (int Width, int Height)? max, double overlapFraction)
    {
        if (Fits(region, max))
        {
            return [region];
        }

        IReadOnlyList<FrameRegion> pieces = DetectorShapes.Tiles(
            region.Width, region.Height, max!.Value.Width, max.Value.Height, overlapFraction);

        var placed = new FrameRegion[pieces.Count];

        for (int i = 0; i < pieces.Count; i++)
        {
            placed[i] = new FrameRegion(
                region.X + pieces[i].X, region.Y + pieces[i].Y, pieces[i].Width, pieces[i].Height);
        }

        return placed;
    }

    /// <summary>
    /// Grows a crop toward the detector's own aspect, so that what reaches the model is picture rather
    /// than padding.
    ///
    /// <para>A crop is sized from the frame and then fitted into an input of some other shape, with the
    /// remainder filled with flat grey. A 384x108 crop from a 1536x432 panoramic reaches a 320² input as
    /// a 320x90 band with 72 % of the model's field spent on grey — while a tile of the floor sweep,
    /// built at the detector's own shape, arrives at 100 %.</para>
    ///
    /// <para><b>It is free, which is the whole reason to do it.</b> The scale is set by whichever axis is
    /// squeezed hardest — 0.83x across against 2.96x down, for that crop — so growing the slack axis
    /// costs no resolution at all, and no extra inference. 384x108 becomes 384x384 at the identical
    /// 0.83x, with three and a half times more real picture in it. It also answers the tight-crop
    /// failure <see cref="Grow"/> records: 108 pixels of height around a 71-pixel subject is barely any
    /// context, and this is where the context comes from.</para>
    ///
    /// <para>Only ever grows. Shrinking an axis would cut away the subject the crop was taken for.
    /// A crop already at the detector's aspect is returned untouched, which is what makes this a no-op
    /// on a camera whose frames and input already agree.</para>
    ///
    /// <para>Cannot breach a bound of the same aspect: growing the slack axis to the target ratio takes
    /// it to at most that bound's own value on that axis.</para>
    /// </summary>
    /// <param name="aspect">The detector input's width over its height, or zero to leave the crop
    /// alone.</param>
    internal static FrameRegion ShapeToAspect(
        FrameRegion region, int frameWidth, int frameHeight, double aspect)
    {
        if (aspect <= 0 || region.Width <= 0 || region.Height <= 0)
        {
            return region;
        }

        int width = region.Width;
        int height = region.Height;

        if ((double)width / height > aspect)
        {
            height = (int)Math.Round(width / aspect);
        }
        else
        {
            width = (int)Math.Round(height * aspect);
        }

        width = Math.Min(width, frameWidth);
        height = Math.Min(height, frameHeight);

        if (width <= region.Width && height <= region.Height)
        {
            return region;
        }

        // Centred on what the crop was already about, then slid back inside the frame rather than
        // clipped — the same treatment Grow gives a widened crop, and for the same reason: a subject at
        // the edge deserves the context one in the middle gets.
        return new FrameRegion(
            Math.Clamp(region.X + (region.Width / 2) - (width / 2), 0, frameWidth - width),
            Math.Clamp(region.Y + (region.Height / 2) - (height / 2), 0, frameHeight - height),
            width,
            height);
    }

    /// <summary>Whether a region is within the bound. No bound admits everything.</summary>
    private static bool Fits(FrameRegion region, (int Width, int Height)? max) =>
        max is null || (region.Width <= max.Value.Width && region.Height <= max.Value.Height);

    /// <summary>
    /// Widens a crop that is too tight to recognise anything in, around its own centre.
    ///
    /// A detector needs the whole of an object and some of its surroundings. Measured on real
    /// footage, a crop tight enough to cut a truck in half returned nothing at all — not the person
    /// it was centred on, and not the truck either — while a larger crop of the same scene found the
    /// truck at 0.84. Magnification past that point costs more evidence than it buys.
    ///
    /// <para>Two floors, and the larger wins. <paramref name="minSizeFraction"/> is a share of the
    /// frame and bounds how much context comes with the subject; <paramref name="min"/> comes from the
    /// detector's input and bounds magnification, being the size below which reaching that input would
    /// mean enlarging the crop. They answer different questions and neither implies the other.</para>
    ///
    /// <para>Both sit inside the clamp to the frame, which is why a frame shorter than
    /// <paramref name="min"/> still yields a region inside it: a region grown past the frame would be
    /// trimmed by <see cref="FramePreparer"/> afterwards, and the prepared geometry would then describe
    /// a rectangle nobody asked for. Clamping the slack axis usually costs nothing, because a region's
    /// scale is the *minimum* over its two axes — a 512x512 floor on a 360-tall frame gives 512x360,
    /// which still reaches a 512x512 input at 1.00x.</para>
    /// </summary>
    private static FrameRegion Grow(
        FrameRegion region,
        int frameWidth,
        int frameHeight,
        double minSizeFraction,
        (int Width, int Height)? min = null)
    {
        int floorWidth = Math.Max((int)Math.Round(frameWidth * minSizeFraction), min?.Width ?? 0);
        int floorHeight = Math.Max((int)Math.Round(frameHeight * minSizeFraction), min?.Height ?? 0);

        int minWidth = Math.Min(frameWidth, floorWidth);
        int minHeight = Math.Min(frameHeight, floorHeight);

        if (region.Width >= minWidth && region.Height >= minHeight)
        {
            return region;
        }

        int width = Math.Max(region.Width, minWidth);
        int height = Math.Max(region.Height, minHeight);

        // Centred on what actually moved, then slid back inside the frame rather than clipped — a
        // subject at the edge deserves the same amount of context as one in the middle.
        int x = Math.Clamp(region.X + (region.Width / 2) - (width / 2), 0, frameWidth - width);
        int y = Math.Clamp(region.Y + (region.Height / 2) - (height / 2), 0, frameHeight - height);

        return new FrameRegion(x, y, width, height);
    }

    private static FrameRegion Clamp(
        double left, double top, double right, double bottom, int frameWidth, int frameHeight)
    {
        int x = (int)Math.Clamp(Math.Floor(left), 0, frameWidth - 1);
        int y = (int)Math.Clamp(Math.Floor(top), 0, frameHeight - 1);
        int x2 = (int)Math.Clamp(Math.Ceiling(right), x + 1, frameWidth);
        int y2 = (int)Math.Clamp(Math.Ceiling(bottom), y + 1, frameHeight);

        return new FrameRegion(x, y, x2 - x, y2 - y);
    }

    private static bool Overlaps(FrameRegion a, FrameRegion b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width
        && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    private static FrameRegion Union(FrameRegion a, FrameRegion b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);

        return new FrameRegion(
            x,
            y,
            Math.Max(a.X + a.Width, b.X + b.Width) - x,
            Math.Max(a.Y + a.Height, b.Y + b.Height) - y);
    }

    private readonly record struct Blob(int MinX, int MinY, int MaxX, int MaxY, int Cells);
}
