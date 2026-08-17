using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Serval.Ai;

/// <summary>
/// Assigns a best-effort speaker label to each utterance as it arrives.
///
/// This is the *live* half of speaker labelling and is deliberately provisional: it labels
/// what the VAD hands it, and the VAD splits on silence rather than on speaker change, so an
/// utterance holding two voices gets one label. The after-the-fact
/// <see cref="ConversationReprocessor"/> re-segments the whole conversation properly; the two
/// outputs are published separately and never reconciled. Consumers are told which is which via
/// `speaker_source`.
///
/// Identity holds *within* a conversation only. The registry is rebuilt per conversation,
/// which is the "reset after silence" behaviour and is what stops it accumulating dozens of
/// near-duplicate speakers over a multi-week run.
/// </summary>
public sealed class SpeakerLabeller : IDisposable
{
    private readonly SpeakerEmbeddingExtractor _extractor;
    private readonly SpeakerOptions _options;
    private readonly ILogger<SpeakerLabeller> _logger;
    private readonly object _gate = new();

    private SpeakerEmbeddingManager? _manager;
    private Guid _conversationId = Guid.Empty;
    private int _nextSpeaker;

    public SpeakerLabeller(SpeakerOptions options, ILogger<SpeakerLabeller> logger)
    {
        _options = options;
        _logger = logger;

        if (!File.Exists(_options.EmbeddingModelPath))
        {
            throw new FileNotFoundException(
                $"Speaker embedding model not found at '{_options.EmbeddingModelPath}'. "
                + "Run ./scripts/fetch-models.sh", _options.EmbeddingModelPath);
        }

        var config = new SpeakerEmbeddingExtractorConfig
        {
            Model = _options.EmbeddingModelPath,
            Debug = 0,
        };

        _extractor = new SpeakerEmbeddingExtractor(config);
        _logger.LogInformation(
            "Speaker labelling ready (embedding dim={Dim}, live threshold={Threshold})",
            _extractor.Dim, _options.LiveThreshold);
    }

    /// <summary>
    /// Labels an utterance. Returns null when no label can be justified — a short clip that
    /// matches nothing is left unlabelled rather than given a speaker we do not believe.
    /// </summary>
    public string? Identify(Guid conversationId, float[] samples, int sampleRate)
    {
        double seconds = (double)samples.Length / sampleRate;

        lock (_gate)
        {
            ResetIfNewConversation(conversationId);

            float[] embedding;
            try
            {
                using var stream = _extractor.CreateStream();
                stream.AcceptWaveform(sampleRate, samples);
                stream.InputFinished();
                embedding = _extractor.Compute(stream);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Speaker embedding failed; leaving this utterance unlabelled.");
                return null;
            }

            string match = _manager!.Search(embedding, _options.LiveThreshold);
            if (!string.IsNullOrEmpty(match))
            {
                return match;
            }

            // No match. Only utterances long enough for a trustworthy embedding may define a
            // new speaker; otherwise a 0.3s "yeah" mints a speaker every time it is said.
            if (seconds < _options.MinSecondsToRegister)
            {
                _logger.LogDebug(
                    "Utterance is {Seconds:F2}s (< {Min}s) and matched nobody; leaving unlabelled.",
                    seconds, _options.MinSecondsToRegister);
                return null;
            }

            string name = $"speaker_{_nextSpeaker}";
            if (!_manager.Add(name, embedding))
            {
                _logger.LogWarning("Could not register {Name}; leaving this utterance unlabelled.", name);
                return null;
            }

            _nextSpeaker++;
            _logger.LogInformation("New speaker in this conversation: {Name} ({Seconds:F1}s)", name, seconds);
            return name;
        }
    }

    private void ResetIfNewConversation(Guid conversationId)
    {
        if (_manager is not null && conversationId == _conversationId)
        {
            return;
        }

        _manager?.Dispose();
        _manager = new SpeakerEmbeddingManager(_extractor.Dim);
        _conversationId = conversationId;
        _nextSpeaker = 0;

        _logger.LogDebug("Speaker history reset for conversation {ConversationId}.", conversationId);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _manager?.Dispose();
            _extractor.Dispose();
        }
    }
}
