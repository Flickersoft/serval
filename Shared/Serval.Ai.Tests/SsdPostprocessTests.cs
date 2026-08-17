using Serval.Ai;

namespace Serval.Ai.Tests;

/// <summary>
/// Reading TFLite's <c>TFLite_Detection_PostProcess</c> head, which differs from the YOLO end-to-end
/// head in three ways that all fail silently.
///
/// Its boxes are normalised rather than in model pixels, its components are ordered y before x, and its
/// "count" tensor reports capacity rather than how many rows are real. Each of those, read wrongly,
/// produces boxes that are plausible on inspection and consistently in the wrong place — nothing
/// throws and no rectangle looks obviously broken. The numbers here were taken from
/// <c>ssdlite_mobiledet_coco_qat_postprocess_edgetpu.tflite</c> on real hardware.
/// </summary>
public class SsdPostprocessTests
{
    private static readonly string[] Labels = ["person", "bicycle", "car"];

    /// <summary>
    /// Builds the four tensors the head emits. Boxes are given in the head's own order —
    /// <c>ymin, xmin, ymax, xmax</c>, normalised 0-1 — so a test reads the way the model writes.
    ///
    /// <paramref name="rows"/> is the row capacity, and the count tensor is deliberately filled with it
    /// rather than with the number of real entries, because that is what the real head does.
    /// </summary>
    private static DetectorOutput[] Head(
        int rows,
        params (float Ymin, float Xmin, float Ymax, float Xmax, float Score, float Class)[] entries)
    {
        var boxes = new float[rows * SsdPostprocessor.BoxStride];
        var classes = new float[rows];
        var scores = new float[rows];

        for (int i = 0; i < entries.Length; i++)
        {
            // The class index is a float here because it is a float in the tensor — the head shares
            // one float buffer for indices, and that is exactly why the decode has to round.
            (float ymin, float xmin, float ymax, float xmax, float score, float cls) = entries[i];
            int row = i * SsdPostprocessor.BoxStride;

            boxes[row] = ymin;
            boxes[row + 1] = xmin;
            boxes[row + 2] = ymax;
            boxes[row + 3] = xmax;
            classes[i] = cls;
            scores[i] = score;
        }

        return
        [
            new DetectorOutput("boxes", boxes, [1, rows, SsdPostprocessor.BoxStride]),
            new DetectorOutput("classes", classes, [1, rows]),
            new DetectorOutput("scores", scores, [1, rows]),
            new DetectorOutput("count", new[] { (float)rows }, [1]),
        ];
    }

    /// <summary>A 320x320 input over a 320x320 frame: no letterboxing, so model pixels and frame
    /// pixels coincide and a coordinate error has nowhere to hide.</summary>
    private static readonly DetectorInput Input = new(320, 320, DetectorLayout.Uint8Nhwc, 1f);

    private static readonly PreparedFrame Whole = PreparedFrame.Whole(320, 320, 1f);

    [Fact]
    public void An_ssd_box_is_read_y_first_not_x_first()
    {
        // The trap, and the one measured against pycoral's own decoder: on a real frame, reading
        // y-first reproduced its box to within rounding while x-first was out by 548 pixels. Both
        // readings produce a valid-looking rectangle, so only an asymmetric box pins it.
        //
        // ymin=0.25 xmin=0.50 ymax=0.75 xmax=0.60 over 320x320 is a tall narrow box on the right:
        // x from 160 to 192, y from 80 to 240. Transposed it would be short and wide on the left.
        var head = Head(10, (Ymin: 0.25f, Xmin: 0.50f, Ymax: 0.75f, Xmax: 0.60f, Score: 0.9f, Class: 0));

        DetectedObject found = Assert.Single(new SsdPostprocessor(Labels)
            .Decode(head, Input, Whole, 0.25f, out DecodeDiagnostics _));

        Assert.Equal(160f / 320f, found.Box.X, 4);
        Assert.Equal(80f / 320f, found.Box.Y, 4);
        Assert.Equal(32f / 320f, found.Box.Width, 4);
        Assert.Equal(160f / 320f, found.Box.Height, 4);
    }

    [Fact]
    public void Normalised_coordinates_are_scaled_by_the_input_shape_not_taken_as_pixels()
    {
        // Unlike the YOLO end-to-end head, whose boxes arrive in model pixels. Taken as pixels these
        // fractions would all collapse into the top-left pixel and every detection would be a dot.
        var head = Head(10, (Ymin: 0.0f, Xmin: 0.0f, Ymax: 1.0f, Xmax: 1.0f, Score: 0.9f, Class: 0));

        DetectedObject found = Assert.Single(new SsdPostprocessor(Labels)
            .Decode(head, Input, Whole, 0.25f, out DecodeDiagnostics _));

        Assert.Equal(0f, found.Box.X, 4);
        Assert.Equal(0f, found.Box.Y, 4);
        Assert.Equal(1f, found.Box.Width, 4);
        Assert.Equal(1f, found.Box.Height, 4);
    }

    [Fact]
    public void A_non_square_input_scales_each_axis_by_its_own_extent()
    {
        // The axes are scaled independently, so a single shared scale would be right on a square input
        // and wrong on every other one — which is the shape most cameras actually resolve to.
        var input = new DetectorInput(640, 384, DetectorLayout.Uint8Nhwc, 1f);
        PreparedFrame frame = PreparedFrame.Whole(640, 384, 1f);
        var head = Head(10, (Ymin: 0.5f, Xmin: 0.25f, Ymax: 1.0f, Xmax: 0.75f, Score: 0.9f, Class: 0));

        DetectedObject found = Assert.Single(new SsdPostprocessor(Labels)
            .Decode(head, input, frame, 0.25f, out DecodeDiagnostics _));

        // x: 0.25..0.75 of 640 = 160..480. y: 0.5..1.0 of 384 = 192..384.
        Assert.Equal(160f / 640f, found.Box.X, 4);
        Assert.Equal(192f / 384f, found.Box.Y, 4);
        Assert.Equal(320f / 640f, found.Box.Width, 4);
        Assert.Equal(192f / 384f, found.Box.Height, 4);
    }

    [Fact]
    public void The_count_tensor_is_not_trusted_to_say_where_the_real_rows_end()
    {
        // Measured: this head reported 100 — the full capacity — on every frame including a blank one.
        // A decoder that believed it would return a hundred detections a frame, most of them filler
        // whose scores happen to clear the floor. The score threshold is the only usable filter.
        var head = Head(
            100,
            (Ymin: 0.1f, Xmin: 0.1f, Ymax: 0.2f, Xmax: 0.2f, Score: 0.90f, Class: 0),
            (Ymin: 0.3f, Xmin: 0.3f, Ymax: 0.4f, Xmax: 0.4f, Score: 0.80f, Class: 2));

        // count says 100; only two rows carry a score above the floor.
        Assert.Equal(100f, head[3].Values.Span[0]);

        IReadOnlyList<DetectedObject> found = new SsdPostprocessor(Labels)
            .Decode(head, Input, Whole, 0.25f, out DecodeDiagnostics _);

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void A_row_at_or_below_the_threshold_is_not_a_detection()
    {
        // Measured: a blank frame scored spurious rows up to ~0.29, above the 0.25 default. The floor
        // is what separates a detection from filler, so the boundary has to be exact.
        var head = Head(
            10,
            (Ymin: 0.1f, Xmin: 0.1f, Ymax: 0.2f, Xmax: 0.2f, Score: 0.30f, Class: 0),
            (Ymin: 0.3f, Xmin: 0.3f, Ymax: 0.4f, Xmax: 0.4f, Score: 0.25f, Class: 0));

        DetectedObject found = Assert.Single(new SsdPostprocessor(Labels)
            .Decode(head, Input, Whole, 0.25f, out DecodeDiagnostics _));

        Assert.Equal(0.30f, found.Score, 4);
    }

    [Fact]
    public void A_class_index_arrives_as_a_float_and_is_rounded_not_truncated()
    {
        // Same hazard the YOLO head has: the index shares a float tensor with the scores, so 1.9999998
        // is class 2 and truncation would name it "bicycle" forever.
        var head = Head(
            10, (Ymin: 0.1f, Xmin: 0.1f, Ymax: 0.2f, Xmax: 0.2f, Score: 0.9f, Class: 1.9999998f));

        DetectedObject found = Assert.Single(new SsdPostprocessor(Labels)
            .Decode(head, Input, Whole, 0.25f, out DecodeDiagnostics _));

        Assert.Equal("car", found.Label);
    }

    [Fact]
    public void A_class_index_past_the_labels_is_counted_rather_than_thrown()
    {
        // One malformed row should not lose the whole frame, and the count is what lets the caller say
        // the labels file disagrees with the weights — the only signal this head offers, since it
        // declares no class count to check at load.
        var head = Head(
            10,
            (Ymin: 0.1f, Xmin: 0.1f, Ymax: 0.2f, Xmax: 0.2f, Score: 0.9f, Class: 0),
            (Ymin: 0.3f, Xmin: 0.3f, Ymax: 0.4f, Xmax: 0.4f, Score: 0.8f, Class: 77));

        DetectedObject found = Assert.Single(new SsdPostprocessor(Labels)
            .Decode(head, Input, Whole, 0.25f, out DecodeDiagnostics diagnostics));

        Assert.Equal("person", found.Label);
        Assert.Equal(1, diagnostics.UnknownClassRows);
    }

    [Fact]
    public void A_crossed_box_is_refused_rather_than_decoded_inside_out()
    {
        var head = Head(10, (Ymin: 0.6f, Xmin: 0.6f, Ymax: 0.2f, Xmax: 0.2f, Score: 0.9f, Class: 0));

        Assert.Empty(new SsdPostprocessor(Labels)
            .Decode(head, Input, Whole, 0.25f, out DecodeDiagnostics _));
    }

    [Fact]
    public void Detections_come_back_most_confident_first()
    {
        // The head happens to emit scores descending, but that is a property of the graph rather than
        // of the format, and sorting is what makes the result a function of the tensors.
        var head = Head(
            10,
            (Ymin: 0.1f, Xmin: 0.1f, Ymax: 0.2f, Xmax: 0.2f, Score: 0.40f, Class: 0),
            (Ymin: 0.3f, Xmin: 0.3f, Ymax: 0.4f, Xmax: 0.4f, Score: 0.90f, Class: 2));

        IReadOnlyList<DetectedObject> found = new SsdPostprocessor(Labels)
            .Decode(head, Input, Whole, 0.25f, out DecodeDiagnostics _);

        Assert.Equal(["car", "person"], found.Select(static f => f.Label));
    }

    [Fact]
    public void A_head_with_too_few_outputs_is_refused_by_name()
    {
        // Rejected loudly, because four tensors read as one decode into plausible nonsense.
        var truncated = Head(10, (Ymin: 0.1f, Xmin: 0.1f, Ymax: 0.2f, Xmax: 0.2f, Score: 0.9f, Class: 0))
            .Take(2)
            .ToArray();

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            new SsdPostprocessor(Labels).Decode(truncated, Input, Whole, 0.25f, out DecodeDiagnostics _));

        Assert.Contains("boxes, classes, scores, count", error.Message);
    }

    [Fact]
    public void The_head_declares_how_many_outputs_it_needs()
    {
        // So a backend can reject a mismatched model at load rather than on the first frame.
        Assert.Equal(4, new SsdPostprocessor(Labels).ExpectedOutputs);
    }
}
