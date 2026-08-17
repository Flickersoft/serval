namespace Serval.Ai;

/// <summary>What one raw-frame comparison found: the score, and where the frame changed.</summary>
/// <param name="Result">The score and whether it counts as motion.</param>
/// <param name="ChangedCells">One byte per compare cell, non-zero where the frame changed. Valid
/// until the next <see cref="FrameMotionDetector.Accept"/> on the same instance.</param>
public readonly ref struct FrameMotion(MotionResult result, ReadOnlySpan<byte> changedCells)
{
    public MotionResult Result { get; } = result;

    public ReadOnlySpan<byte> ChangedCells { get; } = changedCells;
}

/// <summary>
/// The motion half of the raw-frame pipeline: downsamples a frame's luma plane to the compare grid,
/// scores it against the previous one, and reports where it changed.
///
/// <para>Free of any image library, and that is not incidental. A <c>yuv420p</c> frame's luma plane
/// is already exactly what this wants — one 8-bit sample per pixel, full resolution — so there is
/// nothing to decode and nothing to convert. The JPEG equivalent had to decode an encoded picture and
/// resize it through ImageSharp for every frame of every camera; this is a box average over a span.
/// It is the reason frames are carried as planar YUV rather than RGB.</para>
///
/// <para>One instance per camera: it holds that camera's previous grid, so sharing one would compare
/// each camera against whichever published last and report motion everywhere. Not thread-safe; the
/// Server drives one per camera from that camera's own loop.</para>
/// </summary>
public sealed class FrameMotionDetector
{
    private readonly MotionOptions _options;
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _current;
    private readonly byte[] _changed;
    private byte[]? _previous;

    public FrameMotionDetector(MotionOptions options)
    {
        _options = options;
        _width = Math.Max(options.CompareWidth, 1);
        _height = Math.Max(options.CompareHeight, 1);
        _current = new byte[_width * _height];
        _changed = new byte[_width * _height];
        GridWidth = _width;
        GridHeight = _height;
    }

    public int GridWidth { get; }

    public int GridHeight { get; }

    /// <summary>
    /// Downsamples, compares against the previous frame, and replaces it.
    ///
    /// The first frame always reports no motion: with nothing to compare against there is no evidence
    /// of change, and treating startup as motion would mean every camera proposes regions over its
    /// whole frame every time the process restarts.
    /// </summary>
    /// <param name="luma">The frame's luma plane, <paramref name="frameWidth"/> ×
    /// <paramref name="frameHeight"/> bytes.</param>
    public FrameMotion Accept(ReadOnlySpan<byte> luma, int frameWidth, int frameHeight)
    {
        if (luma.Length < frameWidth * frameHeight)
        {
            throw new ArgumentException(
                $"Luma plane is {luma.Length} bytes; {frameWidth}x{frameHeight} needs "
                + $"{frameWidth * frameHeight}.",
                nameof(luma));
        }

        Downsample(luma, frameWidth, frameHeight);

        if (_previous is null)
        {
            _previous = [.. _current];
            _changed.AsSpan().Clear();
            return new FrameMotion(MotionResult.None, _changed);
        }

        MotionResult result = MotionScorer.Compare(_previous, _current, _options, _changed);

        // Replaced unconditionally, including when the comparison was rejected as a lighting flip.
        // Keeping the pre-flip frame as the reference would make every subsequent frame look wildly
        // different and jam the comparison for as long as the new lighting lasted.
        _current.CopyTo(_previous.AsSpan());

        return new FrameMotion(result, _changed);
    }

    /// <summary>
    /// Box-averages the luma plane down to the compare grid.
    ///
    /// Averaging rather than sampling for the same reason the frame preparer averages: it is what
    /// makes the downscale suppress sensor noise instead of aliasing it into false motion. Aspect
    /// ratio is deliberately not preserved — both frames get identical treatment so the distortion
    /// cancels in the comparison, and padding would add constant borders that only dilute the
    /// changed fraction.
    /// </summary>
    private void Downsample(ReadOnlySpan<byte> luma, int frameWidth, int frameHeight)
    {
        for (int y = 0; y < _height; y++)
        {
            int top = y * frameHeight / _height;
            int bottom = Math.Max(top + 1, (y + 1) * frameHeight / _height);

            for (int x = 0; x < _width; x++)
            {
                int left = x * frameWidth / _width;
                int right = Math.Max(left + 1, (x + 1) * frameWidth / _width);

                int total = 0;
                int count = 0;

                for (int row = top; row < bottom; row++)
                {
                    int offset = row * frameWidth;
                    for (int column = left; column < right; column++)
                    {
                        total += luma[offset + column];
                        count++;
                    }
                }

                _current[(y * _width) + x] = (byte)(total / Math.Max(count, 1));
            }
        }
    }
}
