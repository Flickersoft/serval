namespace Serval.Ai;

/// <summary>
/// Chooses the input shape a camera's frames are fitted into: the stride-32 rectangle whose aspect
/// is nearest the camera's, at a fixed pixel budget.
///
/// <para><b>The budget is held constant and the aspect is what varies.</b> Inference cost tracks the
/// input's pixel count and a detection backbone spends the same on padding as on picture, so a
/// square-ish input handed a 32:9 panoramic buys nothing with more than half of what it computes. A
/// shape at the camera's own aspect carries the same picture at a higher scale for the same cost —
/// a 1536x432 frame reaches 94.8% picture at 960x256 where 640x384 leaves it at 46.9%, and the
/// subject arrives 42% larger.</para>
///
/// <para>Holding the budget constant rather than the long edge is what keeps one measured
/// <see cref="InferenceBudget"/> valid for every camera: across aspects from 3.75 to 0.27 the cost
/// per megapixel is flat, so the scheduler needs no per-shape model. Holding the *long edge*
/// constant instead would starve a panoramic — 640 across at 32:9 is 640x192, a third of the
/// pixels and back to a poor scale.</para>
///
/// <para>A camera resolves its shape once, when its frame size is first known, and keeps it. Region
/// crops use their camera's shape too rather than resolving their own: a crop exists to magnify part
/// of a frame, not to match an aspect, and letting every motion cluster mint a shape would put an
/// unbounded number of arenas behind one session for no gain. The number of distinct shapes a host
/// ever sees is therefore its camera count.</para>
/// </summary>
public static class DetectorShapes
{
    /// <summary>
    /// How far a detection backbone downsamples, and so what both axes must divide by. A shape that
    /// breaks it either fails to build the head's grid or silently loses the remainder.
    /// </summary>
    public const int Stride = 32;

    /// <summary>
    /// How far a candidate's area may sit from the budget and still be considered.
    ///
    /// Stride quantisation means an exact hit is usually unavailable — at 245,760 the shapes near
    /// 4:3 land on 239,616 and 243,712 — so demanding the budget exactly would leave whole stretches
    /// of the aspect range with no candidate at all. A tenth is loose enough to always offer one and
    /// tight enough that cost stays flat.
    /// </summary>
    private const double AreaTolerance = 0.10;

    /// <summary>
    /// The widest and tallest aspect that gets its own shape, past which frames are letterboxed into
    /// the most extreme shape available.
    ///
    /// Generous on purpose — 32:9 stitched panoramics are real hardware, and this is 16:1 — but not
    /// unbounded, because the ladder is enumerated and an unbounded range at a small budget produces
    /// shapes one stride tall that no backbone can use.
    /// </summary>
    private const double ExtremeAspect = 16.0;

    /// <summary>
    /// The shape a frame of this size should be fitted into.
    ///
    /// <para>Nearest by the *ratio* of aspects rather than their difference, because aspect is
    /// multiplicative: 4.0 against 3.0 and 1.33 against 1.0 are the same mismatch and cost the same
    /// fraction of the input in padding, while their arithmetic differences are 1.0 and 0.33. Judged
    /// by difference, every wide camera would be handed the widest shape on the ladder.</para>
    /// </summary>
    /// <param name="frameWidth">Frame width in pixels. A non-positive value resolves the budget's
    /// squarest shape, which is what a caller with nothing better to say should get.</param>
    /// <param name="frameHeight">Frame height in pixels.</param>
    /// <param name="pixelBudget">Roughly how many pixels every inference may cost.</param>
    public static (int Width, int Height) Fit(int frameWidth, int frameHeight, int pixelBudget)
    {
        double aspect = frameWidth > 0 && frameHeight > 0
            ? (double)frameWidth / frameHeight
            : 1.0;

        bool landscape = aspect >= 1.0;

        (int Width, int Height) best = default;
        double bestDistance = double.MaxValue;

        foreach ((int width, int height) in Ladder(pixelBudget))
        {
            double distance = Math.Abs(Math.Log((double)width / height / aspect));

            // Ties are real rather than hypothetical: a square frame sits exactly as far from
            // 480x512 as from 512x480, and the stride grid is symmetric enough that near-ties recur
            // at every aspect. Broken toward the frame's own orientation, so a picture never gets
            // turned on its side by a rounding difference in the last bit of a logarithm.
            bool better = distance < bestDistance - Tie
                || (distance < bestDistance + Tie && landscape == (width >= height)
                    && landscape != (best.Width >= best.Height));

            if (better || best == default)
            {
                bestDistance = Math.Min(distance, bestDistance);
                best = (width, height);
            }
        }

        return best;
    }

    /// <summary>
    /// How close two log-aspect distances have to be to count as the same. Loose enough to catch a
    /// tie that floating point split by one bit, far tighter than the gap between adjacent rungs.
    /// </summary>
    private const double Tie = 1e-9;

    /// <summary>
    /// Every shape available at a budget, widest first.
    ///
    /// <para>Enumerated from the stride grid rather than written down as a table: the grid is already
    /// the set of legal shapes, and it is naturally fine near square and coarse at the extremes,
    /// which is the right density — a tenth of an aspect matters far more at 1:1 than at 16:1. A
    /// hand-written ladder would also have to be rewritten for every budget an operator picks.</para>
    ///
    /// <para>Public because it is the honest way to see what a budget offers, and because the tests
    /// check the whole set rather than a few spot cases.</para>
    /// </summary>
    public static IReadOnlyList<(int Width, int Height)> Ladder(int pixelBudget)
    {
        int budget = Math.Max(pixelBudget, Stride * Stride);
        var shapes = new List<(int Width, int Height)>();

        int tallest = Snap(Math.Sqrt(budget * ExtremeAspect));

        for (int height = tallest; height >= Stride; height -= Stride)
        {
            int width = Snap((double)budget / height);

            if (width < Stride)
            {
                continue;
            }

            double area = (double)width * height;

            if (Math.Abs(area - budget) / budget > AreaTolerance)
            {
                continue;
            }

            double aspect = (double)width / height;

            if (aspect > ExtremeAspect || aspect < 1.0 / ExtremeAspect)
            {
                continue;
            }

            shapes.Add((width, height));
        }

        return shapes;
    }

    /// <summary>Nearest multiple of the stride, never below one.</summary>
    private static int Snap(double value) =>
        Math.Max(Stride, (int)Math.Round(value / Stride) * Stride);

    /// <summary>
    /// Covers a frame with overlapping tiles the size of the detector's input, so a whole-frame pass can
    /// be made at native scale instead of by shrinking everything into one inference.
    ///
    /// <para><b>What this is for.</b> A fixed-shape backend gives every camera the same input, and a
    /// panoramic squeezed into it arrives at a fraction of its scale — the frame is fully *covered* but
    /// its far field has already been thrown away, so covering is not the same as examining. Tiles
    /// examine the same area at the scale the picture actually has.</para>
    ///
    /// <para><b>Overlap is not optional.</b> An object straddling a boundary is cut in two and detected
    /// as neither half, so tiles are spaced to overlap by roughly
    /// <paramref name="overlapFraction"/> of a tile — which should exceed the largest object worth
    /// finding. This is the one genuinely subtle part of tiling.</para>
    ///
    /// <para>Tiles are distributed evenly and the last one ends exactly at the frame's edge, so coverage
    /// is total by construction and the real overlap is at least the requested one. A tile larger than
    /// the frame on an axis collapses to a single tile on that axis rather than hanging off the end.
    /// </para>
    /// </summary>
    /// <param name="frameWidth">The frame's width in pixels.</param>
    /// <param name="frameHeight">The frame's height in pixels.</param>
    /// <param name="tileWidth">Tile width — the detector's own input width.</param>
    /// <param name="tileHeight">Tile height — the detector's own input height.</param>
    /// <param name="overlapFraction">Fraction of a tile that neighbouring tiles share, 0 to 0.9.</param>
    /// <returns>Tiles in reading order, together covering the whole frame.</returns>
    public static IReadOnlyList<FrameRegion> Tiles(
        int frameWidth,
        int frameHeight,
        int tileWidth,
        int tileHeight,
        double overlapFraction)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || tileWidth <= 0 || tileHeight <= 0)
        {
            return [];
        }

        double overlap = Math.Clamp(overlapFraction, 0.0, 0.9);

        int width = Math.Min(tileWidth, frameWidth);
        int height = Math.Min(tileHeight, frameHeight);
        int columns = Count(frameWidth, width, overlap);
        int rows = Count(frameHeight, height, overlap);

        var tiles = new List<FrameRegion>(columns * rows);

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                tiles.Add(new FrameRegion(
                    Origin(column, columns, frameWidth, width),
                    Origin(row, rows, frameHeight, height),
                    width,
                    height));
            }
        }

        return tiles;

        // How many tiles of this size it takes to span the extent with at least the asked-for overlap.
        static int Count(int extent, int tile, double overlap)
        {
            if (tile >= extent)
            {
                return 1;
            }

            double step = tile * (1.0 - overlap);

            // A 0.9 overlap on a small tile can round the step to nothing, which would loop forever.
            return step < 1.0
                ? extent
                : Math.Max(1, (int)Math.Ceiling((extent - tile) / step) + 1);
        }

        // Evenly spread, first tile flush to the origin and last flush to the far edge — which is what
        // makes coverage total without a special case for the remainder.
        static int Origin(int index, int count, int extent, int tile) =>
            count <= 1 ? 0 : (int)Math.Round((double)index * (extent - tile) / (count - 1));
    }
}
