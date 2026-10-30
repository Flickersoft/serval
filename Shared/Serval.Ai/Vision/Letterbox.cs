using SkiaSharp;

namespace Serval.Ai;

/// <summary>
/// A frame resized to a model's square input with its aspect ratio preserved and the remainder
/// padded — and, inseparably, the arithmetic to map a box found in that square back onto the
/// original frame.
///
/// The un-mapping is why this is a type rather than a function. Scale and padding are needed again
/// at the far end of inference, and kept as loose locals one eventually goes missing — boxes come
/// back a few percent off in a direction that depends on the source aspect ratio, with nothing
/// failing.
///
/// The padding is computed here rather than delegated to a fit-and-centre helper for the same
/// reason: an odd difference between source and target splits unevenly, and inferring which side
/// got the extra pixel makes <see cref="ToSource"/> wrong by one and impossible to notice.
/// </summary>
public sealed class Letterboxed : IDisposable
{
    /// <summary>
    /// Trilinear: bilinear within the nearest two mip levels. Plain bilinear point-samples a 4x
    /// downscale and aliases fine detail into the model's input; the mip chain averages it first.
    /// </summary>
    private static readonly SKSamplingOptions Downscale = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private Letterboxed(
        SKBitmap image,
        float scale,
        int padX,
        int padY,
        int sourceWidth,
        int sourceHeight)
    {
        Canvas = image;
        Scale = scale;
        PadX = padX;
        PadY = padY;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
    }

    /// <summary>The padded result, at the model's input size, as opaque RGBA.</summary>
    public SKBitmap Canvas { get; }

    /// <summary>Source pixels per model pixel. Never above 1 when the source is larger than the
    /// target, which is the usual case.</summary>
    public float Scale { get; }

    /// <summary>Padding on the left edge, in model pixels. The right edge may have one more.</summary>
    public int PadX { get; }

    /// <summary>Padding on the top edge, in model pixels.</summary>
    public int PadY { get; }

    public int SourceWidth { get; }

    public int SourceHeight { get; }

    /// <summary>
    /// Decodes a JPEG and fits it to <paramref name="width"/> × <paramref name="height"/>.
    /// </summary>
    /// <param name="pad">Fill for the padded strips. Mid-gray by convention: it is the least
    /// suggestive thing to show a model, where black reads as shadow and white as blown sky.</param>
    public static Letterboxed Fit(ReadOnlySpan<byte> jpeg, int width, int height, SKColor pad)
    {
        using SKBitmap source = JpegDecoder.Decode(jpeg);

        float scale = Math.Min((float)width / source.Width, (float)height / source.Height);
        int scaledWidth = Math.Max(1, (int)MathF.Round(source.Width * scale));
        int scaledHeight = Math.Max(1, (int)MathF.Round(source.Height * scale));
        int padX = (width - scaledWidth) / 2;
        int padY = (height - scaledHeight) / 2;

        var canvas = new SKBitmap(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));

        using (var drawing = new SKCanvas(canvas))
        using (SKImage image = SKImage.FromBitmap(source))
        {
            drawing.Clear(pad);
            drawing.DrawImage(
                image, SKRect.Create(padX, padY, scaledWidth, scaledHeight), Downscale);
        }

        return new Letterboxed(canvas, scale, padX, padY, source.Width, source.Height);
    }

    /// <summary>Fits to a square.</summary>
    public static Letterboxed Fit(ReadOnlySpan<byte> jpeg, int size, SKColor pad) =>
        Fit(jpeg, size, size, pad);

    /// <summary>
    /// This transform as the geometry a postprocessor un-maps with — the same value
    /// <see cref="FramePreparer"/> returns for a frame it prepared directly.
    ///
    /// Expressed once, in one type, so the decode path cannot end up with two subtly different
    /// notions of where a box goes. A whole picture rather than a crop, so the crop origin is zero.
    /// </summary>
    public PreparedFrame Geometry =>
        new(Scale, PadX, PadY, 0, 0, SourceWidth, SourceHeight, SourceWidth, SourceHeight);

    /// <summary>
    /// Maps a box in model-input pixels back to a fraction of the original frame.
    ///
    /// Clamped to the frame: an object at the edge routinely produces a box that runs a little off
    /// it, and a consumer drawing that literally would draw outside the picture.
    /// </summary>
    public BoundingBox ToSource(float x, float y, float width, float height) =>
        Geometry.ToFrame(x, y, width, height);

    /// <summary>
    /// Tightly-packed uint8 RGB in NHWC order — what an RKNN encoder takes.
    /// </summary>
    public byte[] ToNhwcBytes()
    {
        ReadOnlySpan<byte> rgba = Canvas.GetPixelSpan();
        var buffer = new byte[Canvas.Width * Canvas.Height * 3];

        for (int i = 0, p = 0; i < buffer.Length; i += 3, p += 4)
        {
            buffer[i] = rgba[p];
            buffer[i + 1] = rgba[p + 1];
            buffer[i + 2] = rgba[p + 2];
        }

        return buffer;
    }

    /// <summary>
    /// Planar float RGB in NCHW order, scaled by <paramref name="scale"/> — what an ONNX detector
    /// exported from the usual toolchains takes, with the default 1/255 putting it in 0..1.
    /// </summary>
    public float[] ToNchwFloats(float scale = 1f / 255f)
    {
        ReadOnlySpan<byte> rgba = Canvas.GetPixelSpan();
        int plane = Canvas.Width * Canvas.Height;
        var buffer = new float[plane * 3];

        for (int i = 0, p = 0; i < plane; i++, p += 4)
        {
            buffer[i] = rgba[p] * scale;
            buffer[plane + i] = rgba[p + 1] * scale;
            buffer[(2 * plane) + i] = rgba[p + 2] * scale;
        }

        return buffer;
    }

    public void Dispose() => Canvas.Dispose();
}
