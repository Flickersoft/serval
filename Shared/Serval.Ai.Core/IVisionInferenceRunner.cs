namespace Serval.Ai;

/// <summary>One captured frame, kept with its capture time so a runner can tell the model how far
/// apart the frames it is looking at were taken.</summary>
public sealed record VisionFrame(byte[] Jpeg, DateTimeOffset CapturedAt);

/// <summary>
/// Runs a vision-language model over one or more frames and returns free text.
///
/// Frames are ordered oldest-first and are consecutive captures from the same camera. Passing more
/// than one is what lets the model describe *movement* rather than a still — but not every backend
/// can accept more than one, so a caller must clamp to <see cref="MaxFrames"/> rather than assume.
/// </summary>
public interface IVisionInferenceRunner : IDisposable
{
    /// <summary>
    /// Most frames this backend can take in a single inference. The CPU (llama.cpp/mtmd) path takes
    /// several; the RK3588 NPU path takes exactly one, because its image-embedding buffer is sized
    /// for a single image.
    /// </summary>
    int MaxFrames { get; }

    /// <param name="frames">Oldest first. Must be non-empty and no longer than <see cref="MaxFrames"/>.</param>
    /// <param name="prompt">The instruction to run against the frames.</param>
    Task<string> RunInferenceAsync(
        IReadOnlyList<VisionFrame> frames,
        string prompt,
        CancellationToken cancellationToken);
}
