using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace Serval.Ai.Tests;

/// <summary>
/// Cutting a region out of a raw frame and handing it to a detector in that detector's own form.
///
/// The failures worth guarding here are the quiet ones. A box that comes back a few percent off in
/// a direction that depends on the source aspect ratio fails nothing and is drawn slightly wrong
/// forever; a buffer laid out for the wrong backend decodes into plausible noise. Both are geometry
/// and arithmetic, so both are testable without a model on disk.
/// </summary>
public class FramePreparerTests
{
    /// <summary>
    /// A yuv420p frame filled with one flat colour, which is what makes a conversion check
    /// unambiguous: every sampled pixel must come back the same regardless of how it was scaled.
    /// </summary>
    private static byte[] Flat(int width, int height, byte y, byte u, byte v)
    {
        var frame = new byte[width * height * 3 / 2];
        int luma = width * height;
        int chroma = luma / 4;

        frame.AsSpan(0, luma).Fill(y);
        frame.AsSpan(luma, chroma).Fill(u);
        frame.AsSpan(luma + chroma, chroma).Fill(v);
        return frame;
    }

    /// <summary>A frame whose left half is black and right half is white, in luma only.</summary>
    private static byte[] SplitVertically(int width, int height)
    {
        byte[] frame = Flat(width, height, 0, 128, 128);

        for (int row = 0; row < height; row++)
        {
            frame.AsSpan((row * width) + (width / 2), width / 2).Fill(255);
        }

        return frame;
    }

    [Fact]
    public void A_square_source_fills_a_square_input_with_no_padding()
    {
        var input = new DetectorInput(64, 64, DetectorLayout.Uint8Nhwc);
        var destination = new byte[input.ByteLength];

        PreparedFrame frame = FramePreparer.Prepare(
            Flat(128, 128, 128, 128, 128), 128, 128, FrameRegion.Whole(128, 128), input, destination);

        Assert.Equal(0, frame.PadX);
        Assert.Equal(0, frame.PadY);
        Assert.Equal(0.5f, frame.Scale, 3);
    }

    [Fact]
    public void A_wide_source_is_padded_top_and_bottom_rather_than_stretched()
    {
        // Stretching would be cheaper and is what a naive resize does. It also distorts every box a
        // detector returns, by an amount that depends on the camera's aspect ratio.
        var input = new DetectorInput(64, 64, DetectorLayout.Uint8Nhwc);
        var destination = new byte[input.ByteLength];

        PreparedFrame frame = FramePreparer.Prepare(
            Flat(128, 72, 128, 128, 128), 128, 72, FrameRegion.Whole(128, 72), input, destination);

        Assert.Equal(0, frame.PadX);
        Assert.Equal(0.5f, frame.Scale, 3);
        Assert.True(frame.PadY > 0, "a 16:9 source in a square input must be letterboxed");
    }

    [Fact]
    public void A_wide_source_in_a_matching_input_is_carried_at_full_density()
    {
        // Why a detection model's input need not be square, and on a 16:9 stream should not be. A
        // 640x360 frame in a 640x640 input is 56% picture and 44% mid-grey, and the convolutions cost
        // the same over both; in a 640x384 input the same pixels arrive at the same scale of 1.0 with
        // 24 rows of padding instead of 280. Measured at two threads, that is 68% of the time for the
        // same detections.
        var input = new DetectorInput(640, 384, DetectorLayout.Uint8Nhwc);
        var destination = new byte[input.ByteLength];

        PreparedFrame frame = FramePreparer.Prepare(
            Flat(640, 360, 128, 128, 128), 640, 360, FrameRegion.Whole(640, 360), input, destination);

        Assert.Equal(1f, frame.Scale, 3);
        Assert.Equal(0, frame.PadX);
        Assert.Equal(12, frame.PadY);
    }

    [Fact]
    public void A_box_in_a_rectangular_input_maps_back_the_same_as_a_square_one()
    {
        // The un-mapping is per-axis arithmetic that never knew the input was square, and this is
        // what pins that: the same subject, at the same place in the same frame, through two input
        // shapes has to come back to the same place.
        var square = Prepared(new DetectorInput(640, 640, DetectorLayout.Uint8Nhwc));
        var wide = Prepared(new DetectorInput(640, 384, DetectorLayout.Uint8Nhwc));

        // The middle of the picture in each buffer, which is the middle of the frame in both.
        BoundingBox fromSquare = square.ToFrame(320, square.PadY + 90, 64, 36);
        BoundingBox fromWide = wide.ToFrame(320, wide.PadY + 90, 64, 36);

        Assert.Equal(fromSquare.X, fromWide.X, 3);
        Assert.Equal(fromSquare.Y, fromWide.Y, 3);
        Assert.Equal(fromSquare.Width, fromWide.Width, 3);
        Assert.Equal(fromSquare.Height, fromWide.Height, 3);

        static PreparedFrame Prepared(DetectorInput input) => FramePreparer.Prepare(
            Flat(640, 360, 128, 128, 128), 640, 360, FrameRegion.Whole(640, 360),
            input, new byte[input.ByteLength]);
    }

    [Fact]
    public void A_box_filling_the_prepared_buffer_maps_back_to_the_whole_frame()
    {
        var input = new DetectorInput(64, 64, DetectorLayout.Uint8Nhwc);
        var destination = new byte[input.ByteLength];

        PreparedFrame frame = FramePreparer.Prepare(
            Flat(128, 128, 128, 128, 128), 128, 128, FrameRegion.Whole(128, 128), input, destination);

        BoundingBox box = frame.ToFrame(0, 0, 64, 64);

        Assert.Equal(0f, box.X, 3);
        Assert.Equal(0f, box.Y, 3);
        Assert.Equal(1f, box.Width, 3);
        Assert.Equal(1f, box.Height, 3);
    }

    [Fact]
    public void A_box_found_in_a_crop_maps_back_to_where_the_crop_was()
    {
        // The whole point of regions: a detector looking at a corner of the frame reports boxes in
        // that corner's coordinates, and they have to land back in the frame the operator sees. An
        // implementation that forgets the crop origin puts every distant detection in the top-left.
        var input = new DetectorInput(64, 64, DetectorLayout.Uint8Nhwc);
        var destination = new byte[input.ByteLength];

        PreparedFrame frame = FramePreparer.Prepare(
            Flat(256, 256, 128, 128, 128),
            256,
            256,
            new FrameRegion(128, 128, 64, 64),
            input,
            destination);

        // The whole crop, which sits in the bottom-right quadrant a quarter of the frame across.
        BoundingBox box = frame.ToFrame(0, 0, 64, 64);

        Assert.Equal(128f / 256f, box.X, 3);
        Assert.Equal(128f / 256f, box.Y, 3);
        Assert.Equal(64f / 256f, box.Width, 3);
        Assert.Equal(64f / 256f, box.Height, 3);
    }

    [Fact]
    public void A_box_running_off_the_edge_is_clamped_to_the_frame()
    {
        var input = new DetectorInput(64, 64, DetectorLayout.Uint8Nhwc);
        var destination = new byte[input.ByteLength];

        PreparedFrame frame = FramePreparer.Prepare(
            Flat(128, 128, 128, 128, 128), 128, 128, FrameRegion.Whole(128, 128), input, destination);

        BoundingBox box = frame.ToFrame(-20, -20, 200, 200);

        Assert.Equal(0f, box.X, 3);
        Assert.Equal(0f, box.Y, 3);
        Assert.Equal(1f, box.Width, 3);
        Assert.Equal(1f, box.Height, 3);
    }

    [Fact]
    public void Interleaved_output_carries_one_byte_per_channel_per_pixel()
    {
        // Mid grey in limited-range BT.601 is y=126,u=v=128, which must not come back tinted: a
        // channel order or matrix mistake shows up here before it shows up as a confidence shift.
        var input = new DetectorInput(8, 8, DetectorLayout.Uint8Nhwc);
        var destination = new byte[input.ByteLength];

        FramePreparer.Prepare(
            Flat(16, 16, 126, 128, 128), 16, 16, FrameRegion.Whole(16, 16), input, destination);

        for (int i = 0; i < input.ByteLength; i += 3)
        {
            Assert.InRange(destination[i], 120, 136);
            Assert.Equal(destination[i], destination[i + 1]);
            Assert.Equal(destination[i], destination[i + 2]);
        }
    }

    [Fact]
    public void Planar_float_output_is_one_channel_plane_after_another_and_scaled()
    {
        var input = new DetectorInput(8, 8, DetectorLayout.FloatNchw);
        var destination = new byte[input.ByteLength];

        // Pure red in limited-range BT.601.
        FramePreparer.Prepare(
            Flat(16, 16, 82, 90, 240), 16, 16, FrameRegion.Whole(16, 16), input, destination);

        float[] floats = System.Runtime.InteropServices.MemoryMarshal
            .Cast<byte, float>(destination).ToArray();
        int plane = input.Width * input.Height;

        Assert.All(floats[..plane], red => Assert.True(red > 0.8f, $"red plane was {red}"));
        Assert.All(floats[plane..(2 * plane)], green => Assert.True(green < 0.2f, $"green was {green}"));
        Assert.All(floats[(2 * plane)..], blue => Assert.True(blue < 0.2f, $"blue was {blue}"));
    }

    [Fact]
    public void Downscaling_averages_the_pixels_it_discards()
    {
        // Point-sampling a half-and-half frame down to one pixel per side would return pure black or
        // pure white depending on where the sample landed. Averaging is what keeps a small distant
        // object present in the buffer at all rather than aliasing it away.
        var input = new DetectorInput(2, 2, DetectorLayout.Uint8Nhwc);
        var destination = new byte[input.ByteLength];

        FramePreparer.Prepare(
            SplitVertically(64, 64), 64, 64, FrameRegion.Whole(64, 64), input, destination);

        // Left column dark, right column light, and neither saturated to the other's value.
        Assert.InRange(destination[0], 0, 40);
        Assert.InRange(destination[3], 215, 255);
    }

    [Fact]
    public void Padding_is_mid_grey_rather_than_black()
    {
        // Black reads as shadow to a model and white as blown sky; grey is the least suggestive
        // thing to show it. A letterboxed portrait source is the case that puts real area into it.
        var input = new DetectorInput(64, 64, DetectorLayout.Uint8Nhwc);
        var destination = new byte[input.ByteLength];

        PreparedFrame frame = FramePreparer.Prepare(
            Flat(32, 64, 235, 128, 128), 32, 64, FrameRegion.Whole(32, 64), input, destination);

        Assert.True(frame.PadX > 0, "a portrait source in a square input must be pillarboxed");
        Assert.Equal(127, destination[0]);
    }

    [Fact]
    public void A_region_outside_the_frame_is_clamped_rather_than_read_out_of_bounds()
    {
        var input = new DetectorInput(16, 16, DetectorLayout.Uint8Nhwc);
        var destination = new byte[input.ByteLength];

        PreparedFrame frame = FramePreparer.Prepare(
            Flat(64, 64, 128, 128, 128),
            64,
            64,
            new FrameRegion(48, 48, 999, 999),
            input,
            destination);

        Assert.Equal(48, frame.CropX);
        Assert.Equal(16, frame.CropWidth);
        Assert.Equal(16, frame.CropHeight);
    }

    [Fact]
    public void An_odd_frame_size_is_refused()
    {
        // yuv420p halves chroma in both axes, so an odd dimension has no representation. Accepting
        // one would read the chroma planes at the wrong stride and tint the whole frame.
        var input = new DetectorInput(16, 16, DetectorLayout.Uint8Nhwc);
        var destination = new byte[input.ByteLength];

        Assert.Throws<ArgumentException>(() => FramePreparer.Prepare(
            new byte[64 * 65 * 3 / 2], 64, 65, FrameRegion.Whole(64, 65), input, destination));
    }

    [Fact]
    public void A_short_frame_is_refused_rather_than_read_past_its_end()
    {
        var input = new DetectorInput(16, 16, DetectorLayout.Uint8Nhwc);
        var destination = new byte[input.ByteLength];

        Assert.Throws<ArgumentException>(() => FramePreparer.Prepare(
            new byte[100], 64, 64, FrameRegion.Whole(64, 64), input, destination));
    }

    [Theory]
    [InlineData(320, 320, DetectorLayout.Uint8Nhwc, 320 * 320 * 3)]
    [InlineData(640, 640, DetectorLayout.FloatNchw, 640 * 640 * 3 * 4)]
    public void An_input_reports_the_buffer_size_it_needs(
        int width, int height, DetectorLayout layout, int expected) =>
        Assert.Equal(expected, new DetectorInput(width, height, layout).ByteLength);

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(640, 480)]
    [InlineData(720, 1280)]
    [InlineData(1000, 562)]
    public void The_raw_path_and_the_jpeg_path_agree_on_where_a_box_goes(
        int sourceWidth, int sourceHeight)
    {
        // The two paths reach a detector by completely different routes — one decodes and resizes
        // through ImageSharp, the other scales in ffmpeg and crops here — and they must still put a
        // box in the same place. A disagreement would show as detections drifting the day a camera
        // moved onto the raw path, in a direction that depends on its aspect ratio, with nothing
        // failing.
        var input = new DetectorInput(640, 640, DetectorLayout.FloatNchw);
        var destination = new byte[input.ByteLength];

        PreparedFrame raw = FramePreparer.Prepare(
            Flat(sourceWidth, sourceHeight, 128, 128, 128),
            sourceWidth,
            sourceHeight,
            FrameRegion.Whole(sourceWidth, sourceHeight),
            input,
            destination);

        using Letterboxed jpeg = Letterboxed.Fit(
            JpegOf(sourceWidth, sourceHeight), 640, Color.FromRgb(127, 127, 127));

        Assert.Equal(jpeg.Geometry.Scale, raw.Scale, 4);
        Assert.Equal(jpeg.Geometry.PadX, raw.PadX);
        Assert.Equal(jpeg.Geometry.PadY, raw.PadY);

        // And the same for a box, which is the thing that actually reaches a consumer.
        BoundingBox fromRaw = raw.ToFrame(100, 120, 80, 90);
        BoundingBox fromJpeg = jpeg.ToSource(100, 120, 80, 90);

        Assert.Equal(fromJpeg.X, fromRaw.X, 4);
        Assert.Equal(fromJpeg.Y, fromRaw.Y, 4);
        Assert.Equal(fromJpeg.Width, fromRaw.Width, 4);
        Assert.Equal(fromJpeg.Height, fromRaw.Height, 4);
    }

    private static byte[] JpegOf(int width, int height)
    {
        using var image = new Image<Rgb24>(width, height);
        using var stream = new MemoryStream();
        image.Save(stream, new JpegEncoder());
        return stream.ToArray();
    }
}
