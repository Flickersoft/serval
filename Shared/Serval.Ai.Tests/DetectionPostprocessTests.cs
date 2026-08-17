using Serval.Ai;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace Serval.Ai.Tests;

/// <summary>
/// The half of inference that does not need a model on disk, and where the errors actually live:
/// mapping a box back onto the original frame, and reading the end-to-end head's rows correctly.
///
/// The coordinate round-trip especially. A letterbox that is wrong by a few percent fails silently
/// forever — nothing throws, no test goes red, the rectangles are simply drawn slightly off, in a
/// direction that depends on the source aspect ratio. Pinning it against known pixel coordinates
/// is the only thing that catches it.
/// </summary>
public class DetectionPostprocessTests
{
    private static byte[] Jpeg(int width, int height)
    {
        using var image = new Image<Rgb24>(width, height, Color.FromRgb(10, 120, 200));
        using var buffer = new MemoryStream();
        image.SaveAsJpeg(buffer);
        return buffer.ToArray();
    }

    [Fact]
    public void A_landscape_frame_is_padded_top_and_bottom_only()
    {
        // 640x360 into a 640 square: no horizontal scaling at all, 140 rows of padding split
        // evenly. Scale 1.0 is the case the snapshot pipeline actually produces.
        using Letterboxed frame = Letterboxed.Fit(Jpeg(640, 360), 640, Color.FromRgb(127, 127, 127));

        Assert.Equal(1.0f, frame.Scale, 3);
        Assert.Equal(0, frame.PadX);
        Assert.Equal(140, frame.PadY);
        Assert.Equal(640, frame.Canvas.Width);
        Assert.Equal(640, frame.Canvas.Height);
    }

    [Fact]
    public void A_box_round_trips_back_to_the_pixels_it_came_from()
    {
        // The load-bearing assertion. A person at (100,50) 80x200 in the source must come back as
        // exactly that after being letterboxed and un-letterboxed.
        using Letterboxed frame = Letterboxed.Fit(Jpeg(640, 360), 640, Color.FromRgb(127, 127, 127));

        // Same rectangle expressed in model-input pixels: x unchanged (scale 1, no pad), y shifted
        // down by the top padding.
        BoundingBox box = frame.ToSource(100, 50 + frame.PadY, 80, 200);

        Assert.Equal(100f / 640f, box.X, 4);
        Assert.Equal(50f / 360f, box.Y, 4);
        Assert.Equal(80f / 640f, box.Width, 4);
        Assert.Equal(200f / 360f, box.Height, 4);
    }

    [Fact]
    public void A_downscaled_frame_round_trips_too()
    {
        // 2560x1440 is a real camera resolution and scales to 0.25. Getting the divide and the pad
        // in the wrong order is invisible at scale 1.0 and obvious here.
        using Letterboxed frame = Letterboxed.Fit(Jpeg(2560, 1440), 640, Color.FromRgb(127, 127, 127));

        Assert.Equal(0.25f, frame.Scale, 3);
        Assert.Equal(140, frame.PadY);

        BoundingBox box = frame.ToSource(160, 40 + frame.PadY, 40, 100);

        Assert.Equal(640f / 2560f, box.X, 3);
        Assert.Equal(160f / 1440f, box.Y, 3);
        Assert.Equal(160f / 2560f, box.Width, 3);
        Assert.Equal(400f / 1440f, box.Height, 3);
    }

    [Fact]
    public void A_portrait_frame_pads_left_and_right()
    {
        using Letterboxed frame = Letterboxed.Fit(Jpeg(360, 640), 640, Color.FromRgb(127, 127, 127));

        Assert.Equal(140, frame.PadX);
        Assert.Equal(0, frame.PadY);

        BoundingBox box = frame.ToSource(frame.PadX + 36, 64, 72, 128);

        Assert.Equal(36f / 360f, box.X, 3);
        Assert.Equal(64f / 640f, box.Y, 3);
    }

    [Fact]
    public void A_box_running_off_the_edge_is_clamped_into_the_frame()
    {
        // Objects at the edge routinely predict past it, and a consumer drawing that literally
        // would draw outside the picture.
        using Letterboxed frame = Letterboxed.Fit(Jpeg(640, 360), 640, Color.FromRgb(127, 127, 127));

        BoundingBox box = frame.ToSource(-50, frame.PadY - 30, 200, 100);

        Assert.Equal(0f, box.X);
        Assert.Equal(0f, box.Y);
        Assert.True(box.X + box.Width <= 1f);
        Assert.True(box.Y + box.Height <= 1f);
    }

    private static readonly string[] Labels = ["person", "car"];

    /// <summary>Decodes through <see cref="YoloEndToEndPostprocessor"/> with the boilerplate of
    /// wrapping one tensor folded away, since every test here has exactly one.</summary>
    private static IReadOnlyList<DetectedObject> Decode(
        float[] output, int detections, PreparedFrame frame, float scoreThreshold, out int unknownClassRows)
    {
        IReadOnlyList<DetectedObject> found = new YoloEndToEndPostprocessor(Labels).Decode(
            [new DetectorOutput("output0", output, [1, detections, YoloEndToEndPostprocessor.Stride])],
            new DetectorInput(640, 640, DetectorLayout.FloatNchw, 1f / 255f),
            frame,
            scoreThreshold,
            out DecodeDiagnostics diagnostics);

        unknownClassRows = diagnostics.UnknownClassRows;
        return found;
    }

    /// <summary>
    /// Builds a <c>[1, rows, 6]</c> tensor in the row-major layout the end-to-end head emits,
    /// filling the given detections from row 0 and leaving the rest zeroed — which is what the
    /// head's unused slots look like, since the row count is a maximum rather than a count.
    /// </summary>
    private static float[] Tensor(
        int rows,
        params (float X1, float Y1, float X2, float Y2, float Score, int Class)[] entries)
    {
        var output = new float[rows * YoloEndToEndPostprocessor.Stride];

        for (int i = 0; i < entries.Length; i++)
        {
            (float x1, float y1, float x2, float y2, float score, int cls) = entries[i];
            int row = i * YoloEndToEndPostprocessor.Stride;

            output[row] = x1;
            output[row + 1] = y1;
            output[row + 2] = x2;
            output[row + 3] = y2;
            output[row + 4] = score;
            output[row + 5] = cls;
        }

        return output;
    }

    [Fact]
    public void A_corner_box_is_taken_as_corners_and_not_as_a_centre()
    {
        // The head emits x1,y1,x2,y2, and the four floats are indistinguishable from a cx,cy,w,h
        // quadruple by inspection. Reading them as a centre and a size produces boxes plausible
        // enough to ship — roughly the right place, badly the wrong size — so only pinned
        // coordinates catch it.
        using Letterboxed frame = Letterboxed.Fit(Jpeg(640, 640), 640, Color.FromRgb(127, 127, 127));
        float[] output = Tensor(8, (X1: 270, Y1: 220, X2: 370, Y2: 420, Score: 0.9f, Class: 0));

        DetectedObject person = Assert.Single(Decode(
            output, detections: 8, frame.Geometry, 0.25f, out int unknown));

        Assert.Equal("person", person.Label);
        Assert.Equal(270f / 640f, person.Box.X, 3);
        Assert.Equal(220f / 640f, person.Box.Y, 3);
        Assert.Equal(100f / 640f, person.Box.Width, 3);
        Assert.Equal(200f / 640f, person.Box.Height, 3);
        Assert.Equal(0, unknown);
    }

    [Fact]
    public void Unused_rows_at_the_end_are_not_detections()
    {
        // The head always returns its full row count, so the zeroed tail is the normal case rather
        // than a malformed one. Decoding it literally would report a pile of zero-size "person"
        // boxes in the top-left corner of every single frame.
        using Letterboxed frame = Letterboxed.Fit(Jpeg(640, 640), 640, Color.FromRgb(127, 127, 127));
        float[] output = Tensor(300, (100, 100, 200, 300, 0.8f, 0));

        Assert.Single(Decode(output, detections: 300, frame.Geometry, 0.25f, out _));
    }

    [Fact]
    public void Anything_under_the_threshold_is_dropped()
    {
        using Letterboxed frame = Letterboxed.Fit(Jpeg(640, 640), 640, Color.FromRgb(127, 127, 127));
        float[] output = Tensor(8, (270, 220, 370, 420, 0.2f, 0));

        Assert.Empty(Decode(output, detections: 8, frame.Geometry, 0.25f, out _));
    }

    [Fact]
    public void A_box_with_no_area_or_crossed_corners_is_dropped()
    {
        using Letterboxed frame = Letterboxed.Fit(Jpeg(640, 640), 640, Color.FromRgb(127, 127, 127));
        float[] output = Tensor(
            8,
            (300, 300, 300, 400, 0.9f, 0),   // zero width
            (300, 300, 400, 300, 0.9f, 0),   // zero height
            (400, 400, 300, 300, 0.9f, 0));  // corners the wrong way round

        Assert.Empty(Decode(output, detections: 8, frame.Geometry, 0.25f, out _));
    }

    [Fact]
    public void A_class_index_past_the_labels_file_is_counted_and_skipped()
    {
        // This count is the only signal that a labels file disagrees with the weights, since the
        // head declares no class count to check the file's length against. One bad row must not
        // cost the good ones in the same frame.
        using Letterboxed frame = Letterboxed.Fit(Jpeg(640, 640), 640, Color.FromRgb(127, 127, 127));
        float[] output = Tensor(
            8,
            (100, 100, 200, 300, 0.9f, 7),
            (300, 100, 400, 300, 0.8f, 1));

        DetectedObject car = Assert.Single(Decode(
            output, detections: 8, frame.Geometry, 0.25f, out int unknown));

        Assert.Equal("car", car.Label);
        Assert.Equal(1, unknown);
    }

    [Fact]
    public void A_class_index_arrives_as_a_float_and_is_rounded_not_truncated()
    {
        // Class 1 in a float tensor can come back as 0.9999999. Truncating that reports "person".
        using Letterboxed frame = Letterboxed.Fit(Jpeg(640, 640), 640, Color.FromRgb(127, 127, 127));
        float[] output = Tensor(8, (100, 100, 200, 300, 0.9f, 0));
        output[5] = 0.9999999f;

        Assert.Equal("car", Assert.Single(Decode(
            output, detections: 8, frame.Geometry, 0.25f, out _)).Label);
    }

    [Fact]
    public void Detections_come_back_sorted_by_score()
    {
        // The head makes no promise about ordering, so the sort is what makes the result a function
        // of the tensor rather than of the graph's internals.
        using Letterboxed frame = Letterboxed.Fit(Jpeg(640, 640), 640, Color.FromRgb(127, 127, 127));
        float[] output = Tensor(
            8,
            (10, 10, 60, 110, 0.4f, 0),
            (100, 10, 150, 110, 0.9f, 0),
            (200, 10, 250, 110, 0.6f, 0));

        IReadOnlyList<DetectedObject> found = Decode(
            output, detections: 8, frame.Geometry, 0.25f, out _);

        Assert.Equal(new[] { 0.9f, 0.6f, 0.4f }, found.Select(static d => d.Score).ToArray());
    }

    [Fact]
    public void Overlapping_boxes_of_one_class_both_survive()
    {
        // The head deduplicates inside the graph, so two overlapping boxes of one class are its
        // considered answer rather than the redundant firing a suppression pass exists to collapse.
        // Adding an overlap filter here would silently delete one of a genuine pair — the second of
        // two people standing together is exactly this shape.
        using Letterboxed frame = Letterboxed.Fit(Jpeg(640, 640), 640, Color.FromRgb(127, 127, 127));
        float[] output = Tensor(
            8,
            (270, 220, 370, 420, 0.9f, 0),
            (272, 218, 374, 416, 0.7f, 0));

        Assert.Equal(2, Decode(output, detections: 8, frame.Geometry, 0.25f, out _).Count);
    }

    [Fact]
    public void Decoded_boxes_come_back_in_the_source_frames_coordinates_not_the_models()
    {
        // The join between the two halves, and the one a unit test of either alone would miss.
        using Letterboxed frame = Letterboxed.Fit(Jpeg(1280, 720), 640, Color.FromRgb(127, 127, 127));
        float[] output = Tensor(8, (288, 275, 352, 365, 0.9f, 0));

        DetectedObject person = Assert.Single(Decode(
            output, detections: 8, frame.Geometry, 0.25f, out _));

        // Centre of the padded square is the centre of the source frame, whatever the scaling.
        Assert.Equal(0.5f, person.Box.X + (person.Box.Width / 2), 3);
        Assert.Equal(0.5f, person.Box.Y + (person.Box.Height / 2), 3);
    }

    [Fact]
    public void The_end_to_end_row_count_comes_from_the_declared_shape()
    {
        // A buffer can be longer than the rows the model claims. Reading the buffer instead of the
        // shape would decode trailing slack as detections whenever a backend over-allocates.
        using Letterboxed frame = Letterboxed.Fit(Jpeg(640, 640), 640, Color.FromRgb(127, 127, 127));

        // Two real rows, then slack that would clear the threshold if it were read.
        float[] output = Tensor(8, (270, 220, 370, 420, 0.9f, 0), (10, 10, 60, 60, 0.8f, 1));
        output[2 * YoloEndToEndPostprocessor.Stride] = 5;
        output[(2 * YoloEndToEndPostprocessor.Stride) + 1] = 5;
        output[(2 * YoloEndToEndPostprocessor.Stride) + 2] = 50;
        output[(2 * YoloEndToEndPostprocessor.Stride) + 3] = 50;
        output[(2 * YoloEndToEndPostprocessor.Stride) + 4] = 0.95f;

        DetectorOutput[] tensors =
            [new DetectorOutput("output0", output, [1, 2, YoloEndToEndPostprocessor.Stride])];

        IReadOnlyList<DetectedObject> found = new YoloEndToEndPostprocessor(Labels).Decode(
            tensors,
            new DetectorInput(640, 640, DetectorLayout.FloatNchw, 1f / 255f),
            frame.Geometry,
            0.25f,
            out DecodeDiagnostics _);

        Assert.Equal(2, found.Count);
    }
}
