using Serval.Ai;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Serval.CameraModule;

/// <summary>
/// Decodes a JPEG into the yuv420p layout ffmpeg hands the Server, so a diagnostic detects through
/// <see cref="FramePreparer"/> — the production path — rather than through a parallel decode whose
/// scores would drift from what a deployment sees.
/// </summary>
internal static class JpegFrames
{
    public static async Task<IReadOnlyList<DetectedObject>> DetectAsync(
        IObjectDetector detector, byte[] jpeg, CancellationToken cancellationToken)
    {
        using Image<Rgb24> image = Image.Load<Rgb24>(jpeg);

        // yuv420p has half-resolution chroma, so dimensions must be even; drop a stray edge
        // row/column rather than refuse the file.
        int width = image.Width & ~1;
        int height = image.Height & ~1;

        byte[] frame = ToYuv420p(image, width, height);
        DetectorInput input = detector.InputFor(width, height);
        byte[] prepared = new byte[input.ByteLength];
        PreparedFrame geometry = FramePreparer.Prepare(
            frame, width, height, FrameRegion.Whole(width, height), input, prepared);

        return await detector
            .DetectPreparedAsync(prepared, input, geometry, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// BT.601 limited range — the inverse of <see cref="FramePreparer"/>'s read side, so the round
    /// trip through this file matches what a camera's decoded stream produces.
    /// </summary>
    private static byte[] ToYuv420p(Image<Rgb24> image, int width, int height)
    {
        byte[] frame = new byte[width * height * 3 / 2];
        int lumaSize = width * height;
        int chromaOffsetV = lumaSize + (lumaSize / 4);

        image.ProcessPixelRows(access =>
        {
            for (int y = 0; y < height; y++)
            {
                Span<Rgb24> row = access.GetRowSpan(y);
                for (int x = 0; x < width; x++)
                {
                    Rgb24 pixel = row[x];
                    frame[(y * width) + x] = (byte)Math.Clamp(
                        (((66 * pixel.R) + (129 * pixel.G) + (25 * pixel.B) + 128) >> 8) + 16, 0, 255);
                }
            }

            // Chroma from the top-left pixel of each 2x2 block; averaging buys nothing a detector
            // can measure, and this keeps the conversion exact to invert.
            for (int y = 0; y < height; y += 2)
            {
                Span<Rgb24> row = access.GetRowSpan(y);
                for (int x = 0; x < width; x += 2)
                {
                    Rgb24 pixel = row[x];
                    int index = ((y / 2) * (width / 2)) + (x / 2);
                    frame[lumaSize + index] = (byte)Math.Clamp(
                        (((-38 * pixel.R) - (74 * pixel.G) + (112 * pixel.B) + 128) >> 8) + 128, 0, 255);
                    frame[chromaOffsetV + index] = (byte)Math.Clamp(
                        (((112 * pixel.R) - (94 * pixel.G) - (18 * pixel.B) + 128) >> 8) + 128, 0, 255);
                }
            }
        });

        return frame;
    }
}
