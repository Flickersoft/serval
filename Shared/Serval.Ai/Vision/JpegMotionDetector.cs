using SkiaSharp;

namespace Serval.Ai;

/// <summary>
/// The JPEG front end of the motion gate: turns each snapshot into a luma plane and hands it to a
/// <see cref="FrameMotionDetector"/>, which downscales, compares and remembers the previous one.
///
/// One instance per camera — it holds that camera's previous frame, so sharing one across cameras
/// would compare each camera against whichever camera happened to publish last, and report
/// constant motion everywhere.
///
/// Not thread-safe. The Server drives one per camera from that camera's own subscription loop.
/// </summary>
public sealed class JpegMotionDetector
{
    private readonly FrameMotionDetector _frames;
    private byte[] _luma = [];

    public JpegMotionDetector(MotionOptions options)
    {
        _frames = new FrameMotionDetector(options);
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
        // Decoded only as large as the compare grid needs: the box average that follows throws
        // the rest of the resolution away, and the codec can skip computing it in the first place.
        using SKBitmap image = JpegDecoder.Decode(jpeg, _frames.GridWidth, _frames.GridHeight);

        int width = image.Width;
        int height = image.Height;
        if (_luma.Length != width * height)
        {
            _luma = new byte[width * height];
        }

        ToLuma(image.GetPixelSpan(), _luma);
        return _frames.Accept(_luma, width, height).Result;
    }

    /// <summary>
    /// Full-range BT.601 luma, the Y a JPEG stores. Recomputed from RGBA because Skia only decodes
    /// to gray from a gray source; motion is a brightness question, so the colour is discarded here
    /// rather than carried through the downscale.
    /// </summary>
    private static void ToLuma(ReadOnlySpan<byte> rgba, Span<byte> luma)
    {
        for (int i = 0, p = 0; i < luma.Length; i++, p += 4)
        {
            luma[i] = (byte)(((77 * rgba[p]) + (150 * rgba[p + 1]) + (29 * rgba[p + 2]) + 128) >> 8);
        }
    }
}
