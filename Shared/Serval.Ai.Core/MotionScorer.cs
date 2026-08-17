namespace Serval.Ai;

/// <summary>
/// What one frame comparison concluded. <see cref="ChangedFraction"/> is reported even when
/// <see cref="Moved"/> is false, because a gate that only ever says no is impossible to tune —
/// the number is what tells you whether a threshold is nearly right or wildly off.
/// </summary>
/// <param name="Moved">Whether this comparison should trigger the expensive vision model.</param>
/// <param name="ChangedFraction">Fraction of compared pixels that differed by more than the delta.</param>
/// <param name="Rejected">
/// True when pixels changed *too* widely to be motion — a lighting or IR-cut flip. Distinguished
/// from a quiet scene so the two are not silently conflated in the logs.
/// </param>
public readonly record struct MotionResult(bool Moved, double ChangedFraction, bool Rejected)
{
    public static readonly MotionResult None = new(false, 0, false);
}

/// <summary>
/// The cheap half of the vision pipeline: decides whether a scene changed enough to be worth a
/// description. Pure and allocation-free — it runs on every frame from every camera, where the model
/// behind it runs seconds at a time.
///
/// Works on already-downscaled grayscale buffers and does no decoding, which keeps it
/// dependency-free and testable without an image library. See <c>JpegMotionDetector</c> in
/// Serval.Ai for the decode-and-downscale half.
///
/// Plain frame differencing rather than background subtraction, deliberately: a background model
/// needs to learn, drifts when the camera is bumped, and has to be told what "background" means
/// after a scene legitimately changes. Frame differencing answers only "is this frame different
/// from the last one", which is what a gate asks.
/// </summary>
public static class MotionScorer
{
    /// <summary>
    /// Compares two equally-sized 8-bit grayscale buffers.
    ///
    /// The caller owns "is this the first frame" — with nothing to compare against, there is no
    /// evidence of motion, and reporting motion on the first frame of every camera would mean a
    /// description every time the process restarts.
    /// </summary>
    /// <exception cref="ArgumentException">The two buffers differ in length, or are empty.</exception>
    public static MotionResult Compare(
        ReadOnlySpan<byte> previous,
        ReadOnlySpan<byte> current,
        MotionOptions options) =>
        Compare(previous, current, options, changedCells: default);

    /// <summary>
    /// Compares two frames and also records <em>where</em> they differed.
    ///
    /// <paramref name="changedCells"/> receives 1 for each compared pixel that changed and 0 for the
    /// rest, in input order. That turns motion from a yes/no gate into a proposal of places worth
    /// looking — the only use for it that survives having a detector.
    ///
    /// Filled even when the comparison is rejected as a lighting flip: a caller debugging the mask
    /// wants the rejected frame most of all.
    /// </summary>
    public static MotionResult Compare(
        ReadOnlySpan<byte> previous,
        ReadOnlySpan<byte> current,
        MotionOptions options,
        Span<byte> changedCells)
    {
        if (previous.Length != current.Length)
        {
            throw new ArgumentException(
                $"Frames must be the same size to compare ({previous.Length} vs {current.Length}).",
                nameof(current));
        }

        if (previous.Length == 0)
        {
            throw new ArgumentException("Cannot compare empty frames.", nameof(current));
        }

        if (changedCells.Length != 0 && changedCells.Length < previous.Length)
        {
            throw new ArgumentException(
                $"Changed-cell mask holds {changedCells.Length} of the {previous.Length} pixels "
                + "being compared.",
                nameof(changedCells));
        }

        int delta = options.PixelDelta;
        int changed = 0;

        for (int i = 0; i < previous.Length; i++)
        {
            // Widen before subtracting: byte arithmetic would wrap and turn a large negative
            // difference into a small positive one, which is a change that reads as stillness.
            int difference = previous[i] - current[i];
            if (difference < 0)
            {
                difference = -difference;
            }

            bool moved = difference > delta;
            if (moved)
            {
                changed++;
            }

            if (!changedCells.IsEmpty)
            {
                changedCells[i] = moved ? (byte)1 : (byte)0;
            }
        }

        double fraction = (double)changed / previous.Length;

        // Ordered so a whole-frame change is reported as rejected rather than as motion: night
        // mode switching on changes every pixel, and describing that as movement is worse than
        // saying nothing, because it happens at dusk every single day.
        if (fraction > options.MaxChangedFraction)
        {
            return new MotionResult(Moved: false, fraction, Rejected: true);
        }

        return new MotionResult(fraction >= options.MinChangedFraction, fraction, Rejected: false);
    }
}
