using Serval.Ai;

namespace Serval.Ai.Tests;

/// <summary>
/// Reading the raw YOLO detect head — the only one here that arrives undecoded, and so the only one
/// where a mistake produces a box that is plausible and in the wrong place.
///
/// <para>Nothing about this decode fails loudly. Read the DFL bins in the wrong grouping and boxes come
/// back slightly the wrong size; walk the anchor grids in the wrong order and every box lands somewhere
/// else in the frame; forget that the scores are logits and everything is confident. So the arithmetic
/// is pinned here against tensors built by hand, where the right answer is known exactly rather than
/// eyeballed — the same reason <see cref="SsdPostprocessTests"/> exists.</para>
///
/// <para>Shapes match the published EdgeTPU weights: a 320-pixel input over strides 8, 16 and 32 gives
/// 40² + 20² + 10² = 2100 anchors.</para>
/// </summary>
public class YoloDflPostprocessTests
{
    private const int Size = 320;
    private const int Anchors = (40 * 40) + (20 * 20) + (10 * 10);

    private static readonly string[] Labels = ["person", "bicycle", "car"];

    private static DetectorInput Input => new(Size, Size, DetectorLayout.Uint8Nhwc, 1f / 255f);

    /// <summary>The whole of a 320x320 frame, so model pixels and frame fractions differ only by scale.</summary>
    private static PreparedFrame Whole => new(1f, 0, 0, 0, 0, Size, Size, Size, Size);

    /// <summary>
    /// Builds the three tensors the head emits, with every anchor dead and the named ones set.
    ///
    /// <paramref name="distances"/> are in stride units and are turned into DFL bins by
    /// <see cref="Bins"/>, so a test states the distance it means rather than a distribution.
    /// </summary>
    private static DetectorOutput[] Head(
        params (int Anchor, int Class, float Logit, float Left, float Top, float Right, float Bottom)[]
            entries)
    {
        var boxes = new float[Anchors * YoloDflPostprocessor.BoxStride];
        var classes = new float[Anchors * Labels.Length];
        var maxima = new float[Anchors];

        // A large negative logit is a dead class, not a zero: zero is a coin flip once it has been
        // through a sigmoid, and a tensor of zeroes would put every anchor over a 0.4 threshold.
        Array.Fill(classes, -20f);
        Array.Fill(maxima, -20f);

        foreach ((int anchor, int cls, float logit, float l, float t, float r, float b) in entries)
        {
            classes[(anchor * Labels.Length) + cls] = logit;
            maxima[anchor] = logit;

            int row = anchor * YoloDflPostprocessor.BoxStride;
            Bins(l).CopyTo(boxes.AsSpan(row));
            Bins(t).CopyTo(boxes.AsSpan(row + YoloDflPostprocessor.RegMax));
            Bins(r).CopyTo(boxes.AsSpan(row + (2 * YoloDflPostprocessor.RegMax)));
            Bins(b).CopyTo(boxes.AsSpan(row + (3 * YoloDflPostprocessor.RegMax)));
        }

        return
        [
            new DetectorOutput("boxes", boxes, [1, Anchors, YoloDflPostprocessor.BoxStride]),
            new DetectorOutput("classes", classes, [1, Anchors, Labels.Length]),
            new DetectorOutput("max", maxima, [1, Anchors, 1]),
        ];
    }

    /// <summary>
    /// Logits whose softmax has exactly <paramref name="distance"/> as its mean.
    ///
    /// A whole number puts all the weight on one bin; a fraction splits it between the two either side,
    /// which is what the network actually produces and what makes the expectation meaningful. Built by
    /// taking the log of the weights, since the decode exponentiates them again.
    /// </summary>
    private static float[] Bins(float distance)
    {
        var bins = new float[YoloDflPostprocessor.RegMax];
        Array.Fill(bins, -30f);

        int low = (int)MathF.Floor(distance);
        float high = distance - low;

        if (high <= 0f)
        {
            bins[low] = 0f;
            return bins;
        }

        bins[low] = MathF.Log(1f - high);
        bins[low + 1] = MathF.Log(high);

        return bins;
    }

    /// <summary>
    /// Cell (20, 20) of the 40x40 stride-8 grid, centred at 164 pixels.
    ///
    /// Away from the edges on purpose: <see cref="PreparedFrame.ToFrame"/> clamps a box to the frame,
    /// which is right — a box running off the picture should not be drawn outside it — but it means a
    /// fixture near an edge measures the clamp rather than the arithmetic under test.
    /// </summary>
    private const int Middle = (20 * 40) + 20;

    [Fact]
    public void An_anchor_decodes_to_the_box_its_distances_describe()
    {
        // Two cells either side of a centre at 164 pixels is 16 pixels either side of it.
        var head = new YoloDflPostprocessor(Labels);

        IReadOnlyList<DetectedObject> found = head.Decode(
            Head((Anchor: Middle, Class: 0, Logit: 2f, Left: 2f, Top: 2f, Right: 2f, Bottom: 2f)),
            Input,
            Whole,
            0.4f,
            out _);

        DetectedObject only = Assert.Single(found);

        Assert.Equal("person", only.Label);
        Assert.Equal((20.5f - 2f) * 8 / Size, only.Box.X, 4);
        Assert.Equal((20.5f - 2f) * 8 / Size, only.Box.Y, 4);
        Assert.Equal(4f * 8 / Size, only.Box.Width, 4);
        Assert.Equal(4f * 8 / Size, only.Box.Height, 4);
    }

    [Fact]
    public void A_fractional_distance_is_the_mean_of_its_bins()
    {
        // What DFL buys over a plain regression, and the part that is silently wrong if the softmax is
        // skipped: the distance is an expectation over the bins, not the largest of them.
        var head = new YoloDflPostprocessor(Labels);

        IReadOnlyList<DetectedObject> found = head.Decode(
            Head((Middle, 0, 2f, 1.25f, 1.25f, 1.25f, 1.25f)),
            Input,
            Whole,
            0.4f,
            out _);

        DetectedObject only = Assert.Single(found);

        Assert.Equal(2.5f * 8 / Size, only.Box.Width, 3);
    }

    [Theory]
    // First anchor of each level: stride 8 at index 0, stride 16 after 1600, stride 32 after 2000.
    [InlineData(0, 8)]
    [InlineData(40 * 40, 16)]
    [InlineData((40 * 40) + (20 * 20), 32)]
    public void Each_level_is_read_at_its_own_stride(int anchor, int stride)
    {
        // The levels are concatenated largest grid first, and reading them in the other order puts every
        // box on the wrong part of the picture while still looking like a box.
        var head = new YoloDflPostprocessor(Labels);

        IReadOnlyList<DetectedObject> found = head.Decode(
            Head((anchor, 0, 2f, 0f, 0f, 1f, 1f)),
            Input,
            Whole,
            0.4f,
            out _);

        DetectedObject only = Assert.Single(found);

        Assert.Equal(0.5f * stride / Size, only.Box.X, 4);
        Assert.Equal(1f * stride / Size, only.Box.Width, 4);
    }

    [Fact]
    public void The_score_is_the_logits_sigmoid()
    {
        // The head emits logits, and a decode that returned them raw would report 2.0 as a confidence
        // and put everything above every threshold.
        var head = new YoloDflPostprocessor(Labels);

        IReadOnlyList<DetectedObject> found = head.Decode(
            Head((0, 1, 2f, 1f, 1f, 1f, 1f)), Input, Whole, 0.4f, out _);

        DetectedObject only = Assert.Single(found);

        Assert.Equal("bicycle", only.Label);
        Assert.Equal(1f / (1f + MathF.Exp(-2f)), only.Score, 4);
    }

    [Fact]
    public void An_anchor_below_the_threshold_is_dropped()
    {
        // The threshold is a probability and the scores are logits, so this is only right if the
        // comparison converts one to the other. A logit of 0 is a probability of 0.5.
        var head = new YoloDflPostprocessor(Labels);

        Assert.Empty(head.Decode(
            Head((0, 0, -1f, 1f, 1f, 1f, 1f)), Input, Whole, 0.4f, out _));

        Assert.Single(head.Decode(
            Head((0, 0, 0f, 1f, 1f, 1f, 1f)), Input, Whole, 0.4f, out _));
    }

    [Fact]
    public void Overlapping_boxes_of_one_class_collapse_to_the_best()
    {
        // What the graph does for every other head here and has to be done on the host for this one:
        // neighbouring anchors all fire on the same object.
        var head = new YoloDflPostprocessor(Labels);

        IReadOnlyList<DetectedObject> found = head.Decode(
            Head(
                (0, 0, 2f, 2f, 2f, 2f, 2f),
                (1, 0, 3f, 3f, 2f, 1f, 2f)),
            Input,
            Whole,
            0.4f,
            out _);

        DetectedObject only = Assert.Single(found);

        Assert.Equal(1f / (1f + MathF.Exp(-3f)), only.Score, 4);
    }

    [Fact]
    public void Overlapping_boxes_of_different_classes_both_survive()
    {
        // The deliberate difference from the reference decode, which suppresses across classes. A dog
        // against its owner overlaps heavily, and letting the larger logit decide which of them existed
        // is the wrong mechanism for that question.
        var head = new YoloDflPostprocessor(Labels);

        IReadOnlyList<DetectedObject> found = head.Decode(
            Head(
                (0, 0, 2f, 2f, 2f, 2f, 2f),
                (1, 2, 3f, 3f, 2f, 1f, 2f)),
            Input,
            Whole,
            0.4f,
            out _);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, d => d.Label == "person");
        Assert.Contains(found, d => d.Label == "car");
    }

    [Fact]
    public void Results_are_most_confident_first()
    {
        var head = new YoloDflPostprocessor(Labels);

        IReadOnlyList<DetectedObject> found = head.Decode(
            Head(
                (0, 0, 1f, 1f, 1f, 1f, 1f),
                ((40 * 40) + (20 * 20), 1, 3f, 1f, 1f, 1f, 1f)),
            Input,
            Whole,
            0.4f,
            out _);

        Assert.Equal(2, found.Count);
        Assert.True(found[0].Score > found[1].Score);
    }

    [Fact]
    public void Tensors_are_found_by_shape_rather_than_by_position()
    {
        // The published weights already changed their output ordering once between releases, described
        // only as being better suited to the accelerator. Position is not a contract; the 64-wide box
        // tensor is.
        var head = new YoloDflPostprocessor(Labels);
        DetectorOutput[] normal = Head((0, 0, 2f, 2f, 2f, 2f, 2f));
        DetectorOutput[] shuffled = [normal[2], normal[1], normal[0]];

        IReadOnlyList<DetectedObject> first = head.Decode(normal, Input, Whole, 0.4f, out _);
        IReadOnlyList<DetectedObject> second = head.Decode(shuffled, Input, Whole, 0.4f, out _);

        Assert.Equal(first, second);
    }

    [Fact]
    public void An_input_whose_grids_do_not_match_the_anchor_count_throws()
    {
        // Rather than decoding every box to a plausible wrong place. A 416-pixel input over the same
        // strides makes 3549 anchors, so tensors built for 320 cannot belong to it.
        var head = new YoloDflPostprocessor(Labels);

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => head.Decode(
                Head((0, 0, 2f, 1f, 1f, 1f, 1f)),
                new DetectorInput(416, 416, DetectorLayout.Uint8Nhwc, 1f / 255f),
                Whole,
                0.4f,
                out _));

        Assert.Contains("anchors", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_head_with_no_box_tensor_says_what_it_was_given()
    {
        var head = new YoloDflPostprocessor(Labels);

        DetectorOutput[] wrong =
        [
            new DetectorOutput("a", new float[Anchors * 4], [1, Anchors, 4]),
            new DetectorOutput("b", new float[Anchors * 3], [1, Anchors, 3]),
            new DetectorOutput("c", new float[Anchors], [1, Anchors, 1]),
        ];

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
            () => head.Decode(wrong, Input, Whole, 0.4f, out _));

        Assert.Contains("64", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_class_beyond_the_label_list_is_counted_rather_than_thrown()
    {
        // One malformed row must not lose the whole frame. A model whose class count was checked when it
        // loaded cannot reach this, which is what makes it a backstop rather than the defence.
        var head = new YoloDflPostprocessor(["person"]);

        IReadOnlyList<DetectedObject> found = head.Decode(
            Head((0, 2, 2f, 1f, 1f, 1f, 1f)),
            Input,
            Whole,
            0.4f,
            out DecodeDiagnostics diagnostics);

        Assert.Empty(found);
        Assert.Equal(1, diagnostics.UnknownClassRows);
    }

    [Fact]
    public void Boxes_are_mapped_back_through_the_crop_they_came_from()
    {
        // The whole point of PreparedFrame: this crop was the right half of a 640x320 frame, so a box in
        // the middle of the model input is in the middle of that half, not of the picture.
        var head = new YoloDflPostprocessor(Labels);
        var crop = new PreparedFrame(1f, 0, 0, 320, 0, Size, Size, 640, Size);

        IReadOnlyList<DetectedObject> found = head.Decode(
            Head((0, 0, 2f, 0f, 0f, 1f, 1f)), Input, crop, 0.4f, out _);

        DetectedObject only = Assert.Single(found);

        Assert.Equal(0.5f + (0.5f * 8 / 640f), only.Box.X, 4);
    }
}
