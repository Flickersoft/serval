using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Serval.Ai;

/// <summary>
/// Silero v5 VAD via sherpa-onnx, wrapped so both hosts — and the <c>--speakers</c> diagnostic —
/// split audio into utterances identically.
///
/// The wrapper is the point: configuring this inline in the live worker and again in the threshold
/// sweep would have the sweep measuring a VAD that could quietly drift from the one actually
/// running. sherpa's <c>VoiceActivityDetector</c> already provides the pre-roll, the silence
/// hangover and the maximum-utterance cap, so there is nothing here but construction and a drain
/// loop.
///
/// Not thread-safe: feed one instance from one thread.
/// </summary>
public sealed class SileroSpeechDetector : IDisposable
{
    private readonly VoiceActivityDetector _detector;
    private readonly ILogger _logger;

    public SileroSpeechDetector(VadOptions options, ILogger logger)
    {
        _logger = logger;

        if (!File.Exists(options.ModelPath))
        {
            throw new FileNotFoundException(
                $"Silero VAD model not found at '{options.ModelPath}'. Run ./scripts/fetch-models.sh",
                options.ModelPath);
        }

        var config = new VadModelConfig();
        config.SileroVad.Model = options.ModelPath;
        config.SileroVad.Threshold = options.Threshold;
        config.SileroVad.MinSilenceDuration = options.MinSilenceSeconds;
        config.SileroVad.MinSpeechDuration = options.MinSpeechSeconds;
        config.SileroVad.MaxSpeechDuration = options.MaxSpeechSeconds;
        config.SileroVad.WindowSize = options.WindowSize;
        config.SampleRate = options.SampleRate;
        config.Debug = 0;

        WindowSize = options.WindowSize;
        _detector = new VoiceActivityDetector(config, bufferSizeInSeconds: 30f);

        _logger.LogInformation(
            "Speech detection ready (threshold={Threshold}, min silence={Silence}s, max utterance={Max}s)",
            options.Threshold, options.MinSilenceSeconds, options.MaxSpeechSeconds);
    }

    /// <summary>Window size required per <see cref="Accept"/> call. Silero v5 demands exactly 512.</summary>
    public int WindowSize { get; }

    /// <summary>Feeds exactly <see cref="WindowSize"/> samples.</summary>
    public void Accept(ReadOnlySpan<float> window)
    {
        if (window.Length != WindowSize)
        {
            throw new ArgumentException(
                $"Silero requires exactly {WindowSize} samples per call, got {window.Length}.", nameof(window));
        }

        try
        {
            _detector.AcceptWaveform(window.ToArray());
        }
        catch (Exception ex)
        {
            // One bad window must not stop detection: the next one may be fine, and throwing here
            // would take down the thread that owns the whole audio path.
            _logger.LogError(ex, "VAD inference failed for one window; continuing.");
        }
    }

    /// <summary>Takes the next completed utterance, if one is ready. Call until it returns false.</summary>
    public bool TryDequeue(out float[] utterance)
    {
        if (_detector.IsEmpty())
        {
            utterance = [];
            return false;
        }

        SpeechSegment segment = _detector.Front();
        _detector.Pop();
        utterance = segment.Samples;
        return true;
    }

    public void Dispose() => _detector.Dispose();
}
