using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Serval.Ai;

/// <summary>
/// AudioSet tagging — 527 non-speech classes — over sherpa-onnx's small zipformer, in-process.
///
/// Deliberately thin: everything that decides what reaches telemetry lives in
/// <see cref="SoundEventPolicy"/>, which needs no model and is unit-tested. What is left here is
/// model construction and one call, so the part that cannot be tested without 26 MB of weights is
/// also the part with nothing in it to get wrong.
///
/// Thread-safe by lock, and shared across every camera like <see cref="SenseVoiceAnalyzer"/>: the
/// model is stateless per stream, so one instance per camera is the same weights loaded N times.
/// Tagging is tens of milliseconds and each camera reaches this at most once per segment, so the
/// lock is not a contention point. If it becomes one, the answer is a small pool rather than
/// per-camera instances.
/// </summary>
public sealed class SoundEventTagger : IDisposable
{
    private readonly AudioTagging _tagging;
    private readonly ILogger _logger;
    private readonly int _topK;
    private readonly Lock _gate = new();

    public SoundEventTagger(SoundOptions options, ILogger logger)
    {
        _logger = logger;
        _topK = options.TopK;

        // Fail at startup rather than on the first sound: a missing model is a deployment error,
        // and the labels file is just as load-bearing as the weights — without it every class comes
        // back unnamed.
        if (!File.Exists(options.ModelPath))
        {
            throw new FileNotFoundException(
                $"Audio tagging model not found at '{options.ModelPath}'. Run ./scripts/fetch-models.sh",
                options.ModelPath);
        }

        if (!File.Exists(options.LabelsPath))
        {
            throw new FileNotFoundException(
                $"Audio tagging labels not found at '{options.LabelsPath}'. Run ./scripts/fetch-models.sh",
                options.LabelsPath);
        }

        var config = new AudioTaggingConfig();
        config.Model.Zipformer.Model = options.ModelPath;
        config.Model.NumThreads = options.NumThreads;
        config.Model.Provider = options.Provider;
        config.Model.Debug = 0;
        config.Labels = options.LabelsPath;
        config.TopK = options.TopK;

        _tagging = new AudioTagging(config);

        _logger.LogInformation(
            "Audio tagging ready (top-{TopK}, threads={Threads}, provider={Provider})",
            options.TopK, options.NumThreads, options.Provider);
    }

    /// <summary>
    /// Classifies one segment, best-scoring label first. Returns empty if inference failed —
    /// one bad segment must not take a camera's audio pipeline down with it.
    /// </summary>
    public IReadOnlyList<ScoredSound> Tag(float[] samples, int sampleRate)
    {
        try
        {
            AudioEvent[] events;

            lock (_gate)
            {
                using OfflineStream stream = _tagging.CreateStream();
                stream.AcceptWaveform(sampleRate, samples);
                events = _tagging.Compute(stream, _topK);
            }

            var scored = new ScoredSound[events.Length];
            for (int i = 0; i < events.Length; i++)
            {
                // Name verbatim: these are the model's own AudioSet strings, commas and all, and
                // they are what gets published. Tidying them here would put a translation nobody
                // can see between the model and the record.
                scored[i] = new ScoredSound(events[i].Name, events[i].Prob);
            }

            return scored;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio tagging failed for one segment; dropping it and continuing.");
            return [];
        }
    }

    public void Dispose() => _tagging.Dispose();
}
