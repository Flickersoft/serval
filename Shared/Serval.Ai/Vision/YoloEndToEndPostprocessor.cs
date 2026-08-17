namespace Serval.Ai;

/// <summary>
/// Turns a YOLO end-to-end detection head's output tensor into boxes on the original frame.
///
/// <para>Pure, and free of any runtime type on purpose: this is where the errors live — a
/// transposed index, an off-by-one in the class offset, a box that never got un-letterboxed — and
/// none of them need a model on disk to reproduce.</para>
///
/// <para>The expected layout is the end-to-end head's <c>[1, detections, 6]</c>, row-major: one row
/// per detection carrying <c>x1, y1, x2, y2, score, classId</c>, the box already corner-based and
/// in model-input pixels. That head deduplicates inside the graph and picks the class itself, so
/// there is no suppression pass here and no score-versus-class arithmetic — only reading rows and
/// mapping geometry. Backends validate the shape at load, so a model that disagrees fails loudly
/// rather than decoding into noise.</para>
///
/// <para>Boxes are already in model-input pixels, so <see cref="DetectorInput"/> is unused here —
/// unlike <see cref="SsdPostprocessor"/>, whose coordinates are normalised and need it.</para>
/// </summary>
/// <param name="labels">Class names, indexed as the model orders them.</param>
public sealed class YoloEndToEndPostprocessor(IReadOnlyList<string> labels) : IDetectionPostprocessor
{
    /// <summary>Values per detection row: the box, its score, its class.</summary>
    public const int Stride = 6;

    public string Description => "yolo/end-to-end";

    /// <summary>One tensor, <c>[1, detections, 6]</c>.</summary>
    public int ExpectedOutputs => 1;

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
                $"This head has one output; {outputs.Length} were supplied.", nameof(outputs));
        }

        DetectorOutput output = outputs[0];

        // Rows from the declared shape when it has one, so a model that reports fewer rows than the
        // buffer holds is read as it describes itself. Falling back to the buffer length keeps a
        // backend that cannot state a shape working rather than returning nothing.
        int rows = output.Shape is { Length: 3 } shape && shape[1] > 0
            ? shape[1]
            : output.Values.Length / Stride;

        ReadOnlySpan<float> values = output.Values.Span;
        int unknownClassRows = 0;
        List<DetectedObject> kept = [];

        for (int i = 0; i < rows; i++)
        {
            int row = i * Stride;

            // The row count is a fixed maximum rather than a count of what was found, so every row
            // past the last real detection is filler. This threshold is the only thing separating
            // the two, which is why it is applied before anything else here.
            float score = values[row + 4];
            if (score <= scoreThreshold)
            {
                continue;
            }

            float x1 = values[row];
            float y1 = values[row + 1];
            float x2 = values[row + 2];
            float y2 = values[row + 3];

            // Filler that happens to carry a score would still decode to nothing, and a real box
            // never has its corners crossed.
            if (x2 <= x1 || y2 <= y1)
            {
                continue;
            }

            // The class arrives as a float in a float tensor, so it needs rounding rather than
            // truncation: 2.9999998 is class 3.
            int classIndex = (int)MathF.Round(values[row + 5]);

            if (classIndex < 0 || classIndex >= labels.Count)
            {
                // Counted rather than thrown: one malformed row should not lose the whole frame,
                // and the count is what lets the caller say the labels file looks wrong.
                unknownClassRows++;
                continue;
            }

            kept.Add(new DetectedObject(
                labels[classIndex],
                score,
                frame.ToFrame(x1, y1, x2 - x1, y2 - y1)));
        }

        // The head makes no promise about ordering, so sorting is what makes the result a function
        // of the tensor rather than of the graph's internals: most confident first, every time.
        kept.Sort(static (a, b) => b.Score.CompareTo(a.Score));

        diagnostics = new DecodeDiagnostics(unknownClassRows);
        return kept;
    }
}
