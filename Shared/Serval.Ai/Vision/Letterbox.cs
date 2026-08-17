using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

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
/// The padding is computed here rather than delegated to <see cref="ResizeMode.Pad"/> for the same
/// reason: an odd difference between source and target splits unevenly, and inferring which side
/// got the extra pixel makes <see cref="ToSource"/> wrong by one and impossible to notice.
/// </summary>
public sealed class Letterboxed : IDisposable
{
    private Letterboxed(
        Image<Rgb24> image,
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

    /// <summary>The padded result, at the model's input size.</summary>
    public Image<Rgb24> Canvas { get; }

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
    public static Letterboxed Fit(ReadOnlySpan<byte> jpeg, int width, int height, Color pad)
    {
        using Image<Rgb24> source = Image.Load<Rgb24>(jpeg);

        float scale = Math.Min((float)width / source.Width, (float)height / source.Height);
        int scaledWidth = Math.Max(1, (int)MathF.Round(source.Width * scale));
        int scaledHeight = Math.Max(1, (int)MathF.Round(source.Height * scale));
        int padX = (width - scaledWidth) / 2;
        int padY = (height - scaledHeight) / 2;

        using Image<Rgb24> scaled = source.Clone(x => x.Resize(scaledWidth, scaledHeight));

        var canvas = new Image<Rgb24>(width, height, pad);
        canvas.Mutate(x => x.DrawImage(scaled, new Point(padX, padY), 1f));

        return new Letterboxed(canvas, scale, padX, padY, source.Width, source.Height);
    }

    /// <summary>Fits to a square.</summary>
    public static Letterboxed Fit(ReadOnlySpan<byte> jpeg, int size, Color pad) =>
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
        var buffer = new byte[Canvas.Width * Canvas.Height * 3];
        int i = 0;

        Canvas.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                foreach (Rgb24 px in accessor.GetRowSpan(y))
                {
                    buffer[i++] = px.R;
                    buffer[i++] = px.G;
                    buffer[i++] = px.B;
                }
            }
        });

        return buffer;
    }

    /// <summary>
    /// Planar float RGB in NCHW order, scaled by <paramref name="scale"/> — what an ONNX detector
    /// exported from the usual toolchains takes, with the default 1/255 putting it in 0..1.
    /// </summary>
    public float[] ToNchwFloats(float scale = 1f / 255f)
    {
        int width = Canvas.Width;
        int height = Canvas.Height;
        int plane = width * height;
        var buffer = new float[plane * 3];

        Canvas.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                Span<Rgb24> row = accessor.GetRowSpan(y);
                int offset = y * width;

                for (int x = 0; x < width; x++)
                {
                    Rgb24 px = row[x];
                    buffer[offset + x] = px.R * scale;
                    buffer[plane + offset + x] = px.G * scale;
                    buffer[(2 * plane) + offset + x] = px.B * scale;
                }
            }
        });

        return buffer;
    }

    public void Dispose() => Canvas.Dispose();
}
