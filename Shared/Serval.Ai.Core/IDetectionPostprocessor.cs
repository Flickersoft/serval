namespace Serval.Ai;

/// <summary>
/// One output tensor, dequantised to float by the backend that read it.
///
/// <para><b>Dequantisation belongs to the backend, not here.</b> Only the backend knows its own
/// quantisation parameters, and keeping that arithmetic on the native side of the seam is what lets
/// every postprocessor stay pure float and testable without a model on disk — the property
/// <see cref="YoloEndToEndPostprocessor"/> already earns and the reason its errors are reproducible.</para>
///
/// <para><paramref name="Shape"/> is the tensor's dimensions as the model declares them. A backend
/// should build these once at load and reuse them: shapes are fixed for the life of a session, and
/// allocating an array per output per frame is pure garbage on a path that runs tens of times a
/// second.</para>
/// </summary>
public readonly record struct DetectorOutput(string Name, ReadOnlyMemory<float> Values, int[] Shape);

/// <summary>
/// What a decode noticed but did not treat as fatal.
///
/// A record struct rather than an <c>out int</c> so a head that has more to report can say so without
/// changing every implementation's signature.
/// </summary>
/// <param name="UnknownClassRows">Rows dropped because their class index fell outside the label list.
/// Non-zero means the labels file disagrees with the weights — which no head this repo reads gives any
/// way to catch at load, so it is a runtime signal or nothing.</param>
public readonly record struct DecodeDiagnostics(int UnknownClassRows);

/// <summary>
/// Turns a detector's output tensors into boxes on the original frame.
///
/// <para><b>The seam that makes a fourth backend cheap rather than another special case.</b> A head
/// format is a property of a model file, not of the runtime that executes it: an end-to-end YOLO head
/// emits one <c>[1, N, 6]</c> tensor that already carries its own class and score, while
/// SSD's <c>TFLite_Detection_PostProcess</c> emits four tensors of boxes, classes, scores and a count
/// with no decode left to do. Both arrive through the same backend, so the decode cannot live in
/// it.</para>
///
/// <para>Implementations hold their own label list, because labels are fixed for the life of a model
/// and threading them through every frame buys nothing.</para>
///
/// <para>Implementations must be safe to call from several camera loops at once — one detector serves
/// every camera — which in practice means holding no mutable state.</para>
/// </summary>
public interface IDetectionPostprocessor
{
    /// <summary>Short name of the head this decodes, for the detector's startup line. Never
    /// parsed.</summary>
    string Description { get; }

    /// <summary>
    /// How many output tensors this head has.
    ///
    /// Declared so the backend can reject a mismatched model <em>at load</em> rather than on the first
    /// frame. That matters more than it looks: a four-tensor head read as a one-tensor head does not
    /// throw, it decodes plausible nonsense.
    /// </summary>
    int ExpectedOutputs { get; }

    /// <summary>
    /// Decodes one frame's outputs.
    /// </summary>
    /// <param name="outputs">The model's outputs, in the order the model declares them.</param>
    /// <param name="input">The shape the pixels were prepared at. Needed because some heads emit
    /// coordinates normalised to 0-1 rather than in model pixels, and the difference is invisible in
    /// the numbers themselves.</param>
    /// <param name="frame">The geometry that produced the input, for mapping boxes back to the frame.
    /// From <see cref="FramePreparer"/> when pixels arrived ready, or
    /// <see cref="Letterboxed.Geometry"/> when a JPEG was decoded.</param>
    /// <param name="scoreThreshold">Floor below which a row is not worth returning.</param>
    /// <param name="diagnostics">What was noticed and tolerated.</param>
    /// <returns>Everything found, most confident first.</returns>
    IReadOnlyList<DetectedObject> Decode(
        ReadOnlySpan<DetectorOutput> outputs,
        DetectorInput input,
        PreparedFrame frame,
        float scoreThreshold,
        out DecodeDiagnostics diagnostics);
}
