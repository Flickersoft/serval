using SkiaSharp;

namespace Serval.Ai.Tests;

/// <summary>
/// The JPEG decode paths: the codec's scaled decode, the letterbox built on it, and the motion
/// gate's JPEG front end.
/// </summary>
public class JpegDecoderTests
{
    [Fact]
    public void A_full_decode_keeps_the_source_size()
    {
        using SKBitmap image = JpegDecoder.Decode(TestJpeg.Solid(640, 360, SKColors.Gray));

        Assert.Equal(640, image.Width);
        Assert.Equal(360, image.Height);
        Assert.Equal(SKColorType.Rgba8888, image.ColorType);
    }

    [Fact]
    public void A_scaled_decode_picks_the_smallest_scale_that_still_covers()
    {
        // 2560x1440 at 1/8 is 320x180, which covers a 64x36 grid; nothing smaller is offered.
        using SKBitmap image = JpegDecoder.Decode(
            TestJpeg.Solid(2560, 1440, SKColors.Gray), 64, 36);

        Assert.Equal(320, image.Width);
        Assert.Equal(180, image.Height);
    }

    [Fact]
    public void A_scaled_decode_never_comes_back_smaller_than_asked()
    {
        // 3/8 of 1280 is 480, the first eighth that clears 400; a nearest-scale request would have
        // snapped to 3/8 or 2/8 depending on rounding, and 2/8 is too small.
        using SKBitmap image = JpegDecoder.Decode(
            TestJpeg.Solid(1280, 720, SKColors.Gray), 400, 225);

        Assert.True(image.Width >= 400, $"width {image.Width}");
        Assert.True(image.Height >= 225, $"height {image.Height}");
        Assert.True(image.Width < 1280, "should not have fallen back to full size");
    }

    [Fact]
    public void Asking_for_more_than_the_source_decodes_at_full_size()
    {
        using SKBitmap image = JpegDecoder.Decode(
            TestJpeg.Solid(320, 180, SKColors.Gray), 640, 360);

        Assert.Equal(320, image.Width);
        Assert.Equal(180, image.Height);
    }

    [Fact]
    public void Bytes_that_are_not_an_image_are_refused()
    {
        Assert.Throws<InvalidDataException>(() => JpegDecoder.Decode("not a jpeg"u8));
    }

    [Fact]
    public void A_truncated_jpeg_is_refused_rather_than_filled()
    {
        // A half-gray frame compared against a whole one would read as motion across the bottom of
        // it, so the missing rows are an error, not something to paper over.
        byte[] whole = TestJpeg.Gray(640, 360, (x, y) => (byte)((x ^ y) & 0xFF));

        Assert.Throws<InvalidDataException>(() => JpegDecoder.Decode(whole.AsSpan(0, whole.Length / 2)));
    }

    [Fact]
    public void The_letterbox_pads_with_the_given_colour_and_keeps_the_picture()
    {
        using Letterboxed frame = Letterboxed.Fit(
            TestJpeg.Solid(640, 360, new SKColor(10, 120, 200)), 640, new SKColor(127, 127, 127));

        byte[] rgb = frame.ToNhwcBytes();

        Assert.Equal(640 * 640 * 3, rgb.Length);
        AssertNear(Pixel(rgb, 640, 320, 10), 127, 127, 127);
        AssertNear(Pixel(rgb, 640, 320, 320), 10, 120, 200);
    }

    [Fact]
    public void A_non_integer_downscale_averages_fine_detail_instead_of_aliasing_it()
    {
        // A one-pixel checkerboard brought down 3.125x. Averaged, every output pixel is mid-gray;
        // point-sampled, the phase drifts across the row and the output strobes between black and
        // white. That strobing is what a detector would see as texture that is not there.
        byte[] jpeg = TestJpeg.Gray(2000, 1125, (x, y) => ((x + y) & 1) == 0 ? (byte)0 : (byte)255);

        using Letterboxed frame = Letterboxed.Fit(jpeg, 640, new SKColor(127, 127, 127));
        byte[] rgb = frame.ToNhwcBytes();

        int top = frame.PadY + 4;
        int bottom = frame.PadY + 360 - 4;
        for (int y = top; y < bottom; y += 7)
        {
            for (int x = 4; x < 636; x += 7)
            {
                byte r = rgb[((y * 640) + x) * 3];
                Assert.InRange(r, 127 - 40, 127 + 40);
            }
        }
    }

    [Fact]
    public void The_motion_gate_sees_a_change_in_a_jpeg()
    {
        var options = new MotionOptions
        {
            CompareWidth = 32,
            CompareHeight = 24,
            PixelDelta = 25,
            MinChangedFraction = 0.02,
            MaxChangedFraction = 0.5,
        };
        var detector = new JpegMotionDetector(options);

        byte[] still = TestJpeg.Gray(1280, 720, (_, _) => 80);
        byte[] moved = TestJpeg.Gray(
            1280, 720, (x, y) => x is >= 200 and < 520 && y is >= 200 and < 440 ? (byte)220 : (byte)80);

        Assert.False(detector.Accept(still).Moved);
        Assert.False(detector.Accept(still).Moved);
        Assert.True(detector.Accept(moved).Moved);
    }

    private static (byte R, byte G, byte B) Pixel(byte[] rgb, int width, int x, int y)
    {
        int p = ((y * width) + x) * 3;
        return (rgb[p], rgb[p + 1], rgb[p + 2]);
    }

    private static void AssertNear((byte R, byte G, byte B) actual, int r, int g, int b)
    {
        // JPEG is lossy; a flat field comes back within a few levels of what was encoded.
        Assert.InRange(actual.R, r - 6, r + 6);
        Assert.InRange(actual.G, g - 6, g + 6);
        Assert.InRange(actual.B, b - 6, b + 6);
    }
}
