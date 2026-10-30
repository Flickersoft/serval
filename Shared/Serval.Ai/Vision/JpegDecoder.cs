using SkiaSharp;

namespace Serval.Ai;

/// <summary>
/// JPEG to tightly-packed, opaque RGBA — the one place an encoded picture becomes pixels.
///
/// RGBA rather than RGB because Skia has no packed three-byte format; callers that want RGB skip
/// every fourth byte. Decoded with no colour space, so an embedded ICC profile is not applied: the
/// detectors and encoders downstream were trained on camera bytes as they come, not on
/// colour-managed ones.
/// </summary>
public static class JpegDecoder
{
    /// <summary>Decodes at full size.</summary>
    public static SKBitmap Decode(ReadOnlySpan<byte> jpeg) => Decode(jpeg, 0, 0);

    /// <summary>
    /// Decodes at the smallest size the codec reaches by scaling inside the IDCT — 1/8 to 8/8 for a
    /// JPEG — that is still at least <paramref name="minWidth"/> × <paramref name="minHeight"/>.
    ///
    /// That is far cheaper than decoding at full size and resizing after: a 1/8 decode skips most
    /// of the inverse transform rather than computing pixels only to average them away. Falls back
    /// to full size when no smaller scale is large enough.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are not a complete, decodable image.</exception>
    public static SKBitmap Decode(ReadOnlySpan<byte> jpeg, int minWidth, int minHeight)
    {
        using SKData data = SKData.CreateCopy(jpeg);
        using SKCodec codec = SKCodec.Create(data)
            ?? throw new InvalidDataException("Not a decodable image.");

        SKSizeI size = SmallestCovering(codec, minWidth, minHeight);
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var bitmap = new SKBitmap(info);

        // IncompleteInput is refused rather than accepted: Skia fills the missing rows, and a
        // half-gray frame compared against a whole one reads as motion across the bottom of it.
        SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success)
        {
            bitmap.Dispose();
            throw new InvalidDataException($"JPEG decode failed: {result}.");
        }

        return bitmap;
    }

    private static SKSizeI SmallestCovering(SKCodec codec, int minWidth, int minHeight)
    {
        SKSizeI full = codec.Info.Size;
        if (minWidth <= 0 || minHeight <= 0)
        {
            return full;
        }

        // Ascending, so the first that covers is the cheapest. GetScaledDimensions snaps to the
        // nearest scale the codec supports rather than rounding up, which is why each candidate is
        // checked instead of asking once for the exact ratio.
        for (int eighths = 1; eighths < 8; eighths++)
        {
            SKSizeI scaled = codec.GetScaledDimensions(eighths / 8f);
            if (scaled.Width >= minWidth && scaled.Height >= minHeight)
            {
                return scaled;
            }
        }

        return full;
    }
}
