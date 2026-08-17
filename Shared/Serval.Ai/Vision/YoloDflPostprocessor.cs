namespace Serval.Ai;

/// <summary>
/// The raw YOLO detect head — anchor points, distances encoded as distribution-focal-loss bins, and
/// no suppression — as compiled for the Edge TPU.
///
/// <para><b>The first head in this repo that arrives undecoded.</b> The SSD detection-postprocess head
/// and the YOLO end-to-end head both finish inside the graph and hand back boxes; this one hands back
/// the layer before that, because the operations that would finish it are ones an Edge TPU either
/// cannot run or runs badly. A sigmoid, a softmax over 16 bins and an overlap sort are trivial on a
/// CPU and hostile to a fixed-function accelerator, so the compiler leaves them out and the host pays
/// about two milliseconds for them. That is the trade the whole file exists to make.</para>
///
/// <para>Three tensors, for one 320x320 model:</para>
///
/// <list type="bullet">
/// <item><c>[1, 2100, 64]</c> — four distances per anchor, each as 16 DFL bins.</item>
/// <item><c>[1, 2100, 17]</c> — class logits, <em>not</em> probabilities.</item>
/// <item><c>[1, 2100, 1]</c> — the per-anchor maximum of those logits, which this ignores. See
/// <see cref="Decode"/>.</item>
/// </list>
///
/// <para>2100 is 40² + 20² + 10², the three stride grids over a 320-pixel input. The 512 model is the
/// same head at 5376 = 64² + 32² + 16², so the anchor geometry is derived from the input size rather
/// than configured, and one decoder serves both.</para>
///
/// <para>Pure float in and boxes out, holding no mutable state, like every other head here — which is
/// what lets the arithmetic below be pinned against hand-computed tensors with no model on disk and no
/// accelerator attached. That matters more for this head than for the others, because it is the only
/// one where a mistake produces a plausible box in the wrong place rather than an obvious failure.</para>
/// </summary>
/// <param name="labels">Class names, indexed as the model orders them. Unlike the other heads, this one
/// declares how many classes it has, so a labels file that disagrees is caught when the model loads
/// rather than mislabelling everything forever.</param>
public sealed class YoloDflPostprocessor(IReadOnlyList<string> labels) : IDetectionPostprocessor
{
    /// <summary>DFL bins per distance. Four distances of 16 bins is the 64 the box tensor carries.</summary>
    public const int RegMax = 16;

    /// <summary>Values per anchor in the box tensor.</summary>
    public const int BoxStride = 4 * RegMax;

    /// <summary>
    /// Feature strides, largest grid first — the order the head concatenates its levels in.
    ///
    /// Fixed rather than inferred: three levels at 8, 16 and 32 is the YOLO detect head's own shape,
    /// and a model with a different pyramid would have a different anchor count, which
    /// <see cref="Anchors"/> refuses rather than silently mis-decodes.
    /// </summary>
    private static readonly int[] Strides = [8, 16, 32];

    /// <summary>
    /// How much two boxes may overlap before the weaker is dropped.
    ///
    /// The reference implementation's value. Suppression is the one part of this head with no single
    /// right answer — it trades a doubled object against a lost one — so it matches the decoder these
    /// weights were published against rather than inventing a number.
    /// </summary>
    private const float OverlapLimit = 0.4f;

    public string Description => "yolo/dfl";

    /// <summary>Boxes, class logits, and a maximum this does not read.</summary>
    public int ExpectedOutputs => 3;

    /// <summary>Classes this head was compiled for, or zero before a model has been seen.</summary>
    public int ClassCount { get; private set; }

    /// <summary>
    /// Decodes one frame's outputs.
    ///
    /// <para><b>The third tensor is deliberately unread.</b> It is the per-anchor maximum of the class
    /// logits, and it exists so a decoder can find the few interesting anchors before dequantising
    /// anything — worth a lot when the decoder owns dequantisation. Serval's does not: the backend
    /// dequantises every output before this is called, by a design decision that keeps postprocessors
    /// pure and testable. So the tensor buys nothing here, and reading it would mean trusting an
    /// assumption about its meaning that the graph gives no way to check. The maximum is taken from the
    /// class row instead, which cannot be wrong.</para>
    /// </summary>
    public IReadOnlyList<DetectedObject> Decode(
        ReadOnlySpan<DetectorOutput> outputs,
        DetectorInput input,
        PreparedFrame frame,
        float scoreThreshold,
        out DecodeDiagnostics diagnostics)
    {
        if (outputs.Length < ExpectedOutputs)
        {
            throw new ArgumentException(
                $"This head has {ExpectedOutputs} outputs (boxes, class logits, and a per-anchor "
                + $"maximum); {outputs.Length} were supplied.", nameof(outputs));
        }

        (DetectorOutput boxes, DetectorOutput classes) = Identify(outputs);

        int anchorCount = AnchorCount(boxes);
        int classCount = LastDimension(classes);
        ClassCount = classCount;

        ReadOnlySpan<float> boxValues = boxes.Values.Span;
        ReadOnlySpan<float> classValues = classes.Values.Span;

        // Compared in the logit domain, so the exponential runs on the handful of anchors that survive
        // rather than on every one of them. The threshold converts once per call; the alternative
        // converts 2100 times to answer the same question.
        float logitThreshold = Logit(scoreThreshold);

        (float X, float Y, int Stride)[] anchors = Anchors(input, anchorCount);

        int unknownClassRows = 0;
        List<Candidate> candidates = [];

        for (int anchor = 0; anchor < anchorCount; anchor++)
        {
            int classRow = anchor * classCount;
            if (classRow + classCount > classValues.Length)
            {
                break;
            }

            float best = float.NegativeInfinity;
            int bestClass = -1;

            for (int c = 0; c < classCount; c++)
            {
                float logit = classValues[classRow + c];
                if (logit > best)
                {
                    best = logit;
                    bestClass = c;
                }
            }

            if (bestClass < 0 || best <= logitThreshold)
            {
                continue;
            }

            if (bestClass >= labels.Count)
            {
                // Counted rather than thrown, as every other head does: one malformed row should not
                // lose the whole frame. A model whose class count was checked at load cannot get here.
                unknownClassRows++;
                continue;
            }

            int boxRow = anchor * BoxStride;
            if (boxRow + BoxStride > boxValues.Length)
            {
                break;
            }

            (float left, float top, float right, float bottom) =
                Distances(boxValues.Slice(boxRow, BoxStride));

            (float anchorX, float anchorY, int stride) = anchors[anchor];

            candidates.Add(new Candidate(
                (anchorX - left) * stride,
                (anchorY - top) * stride,
                (anchorX + right) * stride,
                (anchorY + bottom) * stride,
                Sigmoid(best),
                bestClass));
        }

        diagnostics = new DecodeDiagnostics(unknownClassRows);

        return Suppress(candidates, frame);
    }

    /// <summary>
    /// Sorts the three tensors out by shape rather than by position.
    ///
    /// <para>Index order is a property of one exporter's graph, not of the head: the published weights
    /// changed theirs between releases, described only as an ordering "better suited to the EdgeTPU
    /// limits". The last dimension is unambiguous — 64 is the DFL box tensor, 1 is the per-anchor
    /// maximum, and what remains is the classes — so this reads what it was given instead of trusting
    /// a convention that has already moved once.</para>
    /// </summary>
    private static (DetectorOutput Boxes, DetectorOutput Classes) Identify(
        ReadOnlySpan<DetectorOutput> outputs)
    {
        DetectorOutput? boxes = null;
        DetectorOutput? classes = null;

        foreach (DetectorOutput output in outputs)
        {
            int last = LastDimension(output);

            if (last == BoxStride)
            {
                boxes ??= output;
            }
            else if (last > 1)
            {
                classes ??= output;
            }
        }

        if (boxes is null || classes is null)
        {
            throw new InvalidOperationException(
                "This head needs one output whose last dimension is "
                + $"{BoxStride} (four distances of {RegMax} DFL bins) and one whose last dimension is "
                + "the class count. Shapes supplied: "
                + string.Join(", ", Describe(outputs)) + ".");
        }

        return (boxes.Value, classes.Value);
    }

    private static IEnumerable<string> Describe(ReadOnlySpan<DetectorOutput> outputs)
    {
        var described = new List<string>(outputs.Length);

        foreach (DetectorOutput output in outputs)
        {
            described.Add($"[{string.Join(", ", output.Shape)}]");
        }

        return described;
    }

    private static int LastDimension(DetectorOutput output) =>
        output.Shape is { Length: > 0 } shape ? shape[^1] : 0;

    private static int AnchorCount(DetectorOutput boxes)
    {
        // From the declared shape when there is one, so a buffer larger than the tensor is read as the
        // model describes itself rather than as far as the array happens to run.
        if (boxes.Shape is { Length: 3 } shape && shape[1] > 0)
        {
            return shape[1];
        }

        return boxes.Values.Length / BoxStride;
    }

    /// <summary>
    /// Anchor centres for every level, largest grid first.
    ///
    /// <para>Derived from the input shape rather than configured, and then checked: a 320-pixel input
    /// gives 40² + 20² + 10² = 2100 anchors and a 512-pixel one gives 5376. If the tensor disagrees the
    /// grids are not the ones this head was compiled with, and every box would land somewhere plausible
    /// and wrong — so it throws instead.</para>
    /// </summary>
    private static (float X, float Y, int Stride)[] Anchors(DetectorInput input, int anchorCount)
    {
        var anchors = new (float, float, int)[anchorCount];
        int next = 0;

        foreach (int stride in Strides)
        {
            int columns = input.Width / stride;
            int rows = input.Height / stride;

            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < columns; x++)
                {
                    if (next >= anchorCount)
                    {
                        throw new InvalidOperationException(
                            $"A {input.Width}x{input.Height} input makes more anchors than the "
                            + $"{anchorCount} the box tensor carries, over strides "
                            + $"{string.Join(", ", Strides)}.");
                    }

                    // The cell's centre, which is what the distances below are measured from.
                    anchors[next++] = (x + 0.5f, y + 0.5f, stride);
                }
            }
        }

        if (next != anchorCount)
        {
            throw new InvalidOperationException(
                $"A {input.Width}x{input.Height} input over strides {string.Join(", ", Strides)} makes "
                + $"{next} anchors, but the box tensor carries {anchorCount}. The model was compiled "
                + "for a different input size or a different feature pyramid than this decode assumes.");
        }

        return anchors;
    }

    /// <summary>
    /// Turns one anchor's 64 values into four distances, in stride units.
    ///
    /// <para>Each distance is a distribution over <see cref="RegMax"/> bins rather than a number: the
    /// network predicts how likely each integer offset is, and the distance is their mean. Softmax
    /// first, because what the tensor carries are logits, then the expectation against 0..15.</para>
    ///
    /// <para>The four groups are read consecutively — bins 0-15 are the left distance, 16-31 the top,
    /// and so on. A model that interleaved them instead would decode into boxes that look reasonable
    /// and sit in the wrong place, which is exactly what the cat fixture is for.</para>
    /// </summary>
    private static (float Left, float Top, float Right, float Bottom) Distances(
        ReadOnlySpan<float> row)
    {
        Span<float> distances = stackalloc float[4];

        for (int side = 0; side < 4; side++)
        {
            ReadOnlySpan<float> bins = row.Slice(side * RegMax, RegMax);

            // Shifted by the maximum before exponentiating. These are int8 logits scaled by about 0.08,
            // so they stay small and this is not strictly needed — but a softmax written without it is
            // a trap for the next model whose range is wider.
            float max = float.NegativeInfinity;
            for (int bin = 0; bin < RegMax; bin++)
            {
                max = Math.Max(max, bins[bin]);
            }

            float total = 0f;
            float weighted = 0f;

            for (int bin = 0; bin < RegMax; bin++)
            {
                float value = MathF.Exp(bins[bin] - max);
                total += value;
                weighted += value * bin;
            }

            distances[side] = total > 0f ? weighted / total : 0f;
        }

        return (distances[0], distances[1], distances[2], distances[3]);
    }

    /// <summary>
    /// Drops boxes that are another box's worse duplicate.
    ///
    /// <para><b>Per class, where the reference implementation suppresses across all of them.</b> A
    /// deliberate difference: this head's seventeen classes are people, vehicles and animals, and a dog
    /// standing against its owner overlaps them heavily. Suppressing across classes turns that into one
    /// detection, and which one survives is decided by whichever logit happened to be larger — the
    /// wrong mechanism to be deciding whether a person was there. Costs a little more work and returns
    /// a superset.</para>
    /// </summary>
    private IReadOnlyList<DetectedObject> Suppress(List<Candidate> candidates, PreparedFrame frame)
    {
        candidates.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        List<DetectedObject> kept = [];
        var suppressed = new bool[candidates.Count];

        for (int i = 0; i < candidates.Count; i++)
        {
            if (suppressed[i])
            {
                continue;
            }

            Candidate best = candidates[i];

            kept.Add(new DetectedObject(
                labels[best.ClassIndex],
                best.Score,
                frame.ToFrame(
                    best.Left, best.Top, best.Right - best.Left, best.Bottom - best.Top)));

            for (int j = i + 1; j < candidates.Count; j++)
            {
                if (suppressed[j] || candidates[j].ClassIndex != best.ClassIndex)
                {
                    continue;
                }

                if (Overlap(best, candidates[j]) > OverlapLimit)
                {
                    suppressed[j] = true;
                }
            }
        }

        return kept;
    }

    /// <summary>Intersection over union, in model pixels.</summary>
    private static float Overlap(Candidate a, Candidate b)
    {
        float left = Math.Max(a.Left, b.Left);
        float top = Math.Max(a.Top, b.Top);
        float right = Math.Min(a.Right, b.Right);
        float bottom = Math.Min(a.Bottom, b.Bottom);

        float width = right - left;
        float height = bottom - top;

        if (width <= 0f || height <= 0f)
        {
            return 0f;
        }

        float intersection = width * height;
        float union =
            ((a.Right - a.Left) * (a.Bottom - a.Top))
            + ((b.Right - b.Left) * (b.Bottom - b.Top))
            - intersection;

        return union > 0f ? intersection / union : 0f;
    }

    private static float Sigmoid(float logit) => 1f / (1f + MathF.Exp(-logit));

    /// <summary>
    /// The logit a probability corresponds to, for comparing a threshold against raw scores.
    ///
    /// Saturated rather than infinite at the ends: a threshold of zero admits everything and one admits
    /// nothing, and an infinity here would turn into a NaN comparison instead.
    /// </summary>
    private static float Logit(float probability) => probability switch
    {
        <= 0f => float.NegativeInfinity,
        >= 1f => float.PositiveInfinity,
        _ => MathF.Log(probability / (1f - probability)),
    };

    /// <summary>One surviving anchor, in model pixels, before suppression.</summary>
    private readonly record struct Candidate(
        float Left, float Top, float Right, float Bottom, float Score, int ClassIndex);
}
