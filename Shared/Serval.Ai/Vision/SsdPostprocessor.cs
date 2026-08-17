namespace Serval.Ai;

/// <summary>
/// Decodes TFLite's <c>TFLite_Detection_PostProcess</c> head — four tensors of boxes, classes, scores
/// and a count, with the suppression already done inside the graph.
///
/// <para>Pure and free of any runtime's types, for the reason <see cref="YoloEndToEndPostprocessor"/> states:
/// this is where the errors live, and none of them need a model on disk to reproduce.</para>
///
/// <para><b>Three things about this head are counter-intuitive and each was measured rather than
/// assumed</b>, against <c>ssdlite_mobiledet_coco_qat_postprocess_edgetpu.tflite</c>:</para>
///
/// <list type="number">
/// <item><b>Boxes are normalised 0-1, not model pixels</b> — unlike the YOLO end-to-end head. They are
/// scaled by the input shape here before the geometry is undone.</item>
/// <item><b>The component order is <c>ymin, xmin, ymax, xmax</c> — y first.</b> Pinned against
/// pycoral's own decoder on a real frame: reading y-first reproduced its box to within rounding
/// (2.2 px total), reading x-first was out by 548 px. A transposed read does not throw; it produces
/// plausible boxes that are silently wrong forever.</item>
/// <item><b>The count tensor is not a count.</b> It reported 100 — every row — on every frame
/// including a blank one, so it cannot be used to find where the real detections end. The score
/// threshold is the only usable filter, and this model scores spurious rows up to ~0.29 on a blank
/// frame, which is above the 0.25 default. Expect to raise <c>Detection:ScoreThreshold</c>.</item>
/// </list>
///
/// <para>No suppression pass and no score-versus-class arithmetic: the graph did both. What is left is
/// reading rows and mapping geometry.</para>
/// </summary>
/// <param name="labels">Class names, indexed as the model orders them. For a COCO-90 model this is
/// Coral's own 90-entry list, whose indices differ from COCO-80 — <c>cat</c> is 16 rather than 15 —
/// which is exactly why the labels file has to ship beside the weights.</param>
public sealed class SsdPostprocessor(IReadOnlyList<string> labels) : IDetectionPostprocessor
{
    /// <summary>Values per box row: the four normalised edges.</summary>
    public const int BoxStride = 4;

    private const int Boxes = 0;
    private const int Classes = 1;
    private const int Scores = 2;

    public string Description => "ssd/detection-postprocess";

    /// <summary>Boxes, classes, scores, count.</summary>
    public int ExpectedOutputs => 4;

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
                $"This head has {ExpectedOutputs} outputs (boxes, classes, scores, count); "
                + $"{outputs.Length} were supplied.", nameof(outputs));
        }

        ReadOnlySpan<float> boxes = outputs[Boxes].Values.Span;
        ReadOnlySpan<float> classes = outputs[Classes].Values.Span;
        ReadOnlySpan<float> scores = outputs[Scores].Values.Span;

        // The shortest tensor bounds the loop. The count tensor is deliberately not consulted: it
        // reports the row capacity rather than how many rows are real, so trusting it would admit
        // every filler row and leave the score threshold doing the work anyway.
        int rows = Math.Min(scores.Length, Math.Min(classes.Length, boxes.Length / BoxStride));

        int unknownClassRows = 0;
        List<DetectedObject> kept = [];

        for (int i = 0; i < rows; i++)
        {
            float score = scores[i];
            if (score <= scoreThreshold)
            {
                continue;
            }

            int row = i * BoxStride;

            // y first. See the class remark.
            float top = boxes[row] * input.Height;
            float left = boxes[row + 1] * input.Width;
            float bottom = boxes[row + 2] * input.Height;
            float right = boxes[row + 3] * input.Width;

            // Not clamped to the input here on purpose. A box that runs into the letterbox padding is
            // genuinely outside the picture, and clamping in *model* space would move its edge to the
            // padding's boundary rather than the frame's. PreparedFrame.ToFrame clamps in frame space,
            // which is where the answer means something.
            if (right <= left || bottom <= top)
            {
                continue;
            }

            // The class arrives as a float in a float tensor, so it needs rounding rather than
            // truncation: 2.9999998 is class 3.
            int classIndex = (int)MathF.Round(classes[i]);

            if (classIndex < 0 || classIndex >= labels.Count)
            {
                // Counted rather than thrown: one malformed row should not lose the whole frame, and
                // the count is what lets the caller say the labels file looks wrong.
                unknownClassRows++;
                continue;
            }

            kept.Add(new DetectedObject(
                labels[classIndex],
                score,
                frame.ToFrame(left, top, right - left, bottom - top)));
        }

        // This head emits scores descending, but that is a property of the graph rather than a promise
        // of the format, and sorting is what makes the result a function of the tensors.
        kept.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        diagnostics = new DecodeDiagnostics(unknownClassRows);
        return kept;
    }
}
