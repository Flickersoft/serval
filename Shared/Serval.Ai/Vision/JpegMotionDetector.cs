using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Serval.Ai;

/// <summary>
/// The decode half of the motion gate: turns a JPEG into the small grayscale buffer
/// <see cref="MotionScorer"/> compares, and remembers the previous one.
///
/// One instance per camera — it holds that camera's previous frame, so sharing one across cameras
/// would compare each camera against whichever camera happened to publish last, and report
/// constant motion everywhere.
///
/// Not thread-safe. The Server drives one per camera from that camera's own subscription loop.
/// </summary>
public sealed class JpegMotionDetector
{
    private readonly MotionOptions _options;
    private byte[]? _previous;

    public JpegMotionDetector(MotionOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Decodes, downscales and compares against the previous frame, which this then replaces.
    ///
    /// The first frame always reports no motion: with nothing to compare against there is no
    /// evidence of change, and the alternative — treating startup as motion — would mean every
    /// camera fires a description every time the process restarts.
    /// </summary>
    public MotionResult Accept(ReadOnlySpan<byte> jpeg)
    {
        byte[] current = ToGrayscale(jpeg);

        if (_previous is null)
        {
            _previous = current;
            return MotionResult.None;
        }

        MotionResult result = MotionScorer.Compare(_previous, current, _options);

        // Replace unconditionally, including when the comparison was rejected as a lighting flip.
        // Keeping the pre-flip frame as the reference would make every subsequent frame look
        // wildly different and jam the gate shut for as long as the new lighting lasted.
        _previous = current;
        return result;
    }

    private byte[] ToGrayscale(ReadOnlySpan<byte> jpeg)
    {
        // Decode straight to 8-bit luma: motion is a brightness-change question, and carrying
        // three channels through the resize would triple the work for no extra signal.
        using Image<L8> image = Image.Load<L8>(jpeg);

        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(_options.CompareWidth, _options.CompareHeight),

            // Stretch rather than preserve aspect: both frames get the same treatment, so the
            // distortion cancels out in the comparison, and padding would add constant borders
            // that dilute the changed-pixel fraction.
            Mode = ResizeMode.Stretch,

            // Box sampling averages the pixels it discards instead of point-sampling them, which
            // is what makes the downscale suppress sensor noise rather than alias it into
            // false motion.
            Sampler = KnownResamplers.Box,
        }));

        var pixels = new byte[image.Width * image.Height];
        image.CopyPixelDataTo(pixels);
        return pixels;
    }
}
