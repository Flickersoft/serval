using SkiaSharp;

namespace Serval.Ai.Tests;

/// <summary>Encodes synthetic JPEGs for the tests that exercise the decode paths.</summary>
internal static class TestJpeg
{
    public static byte[] Solid(int width, int height, SKColor color)
    {
        using SKBitmap bitmap = Blank(width, height);
        bitmap.Erase(color);
        return ToJpeg(bitmap);
    }

    /// <summary>Gray levels per pixel, row-major.</summary>
    public static byte[] Gray(int width, int height, Func<int, int, byte> level)
    {
        using SKBitmap bitmap = Blank(width, height);
        Span<byte> rgba = bitmap.GetPixelSpan();

        for (int y = 0, p = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++, p += 4)
            {
                byte v = level(x, y);
                rgba[p] = v;
                rgba[p + 1] = v;
                rgba[p + 2] = v;
                rgba[p + 3] = 255;
            }
        }

        return ToJpeg(bitmap);
    }

    private static SKBitmap Blank(int width, int height) =>
        new(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));

    private static byte[] ToJpeg(SKBitmap bitmap)
    {
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }
}
