using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serval.Ai;

namespace Serval.CameraModule;

/// <summary>
/// Consumes detected utterances, analyses them, and records the result.
///
/// A single consumer by design: SenseVoice is already multi-threaded internally, and
/// running utterances concurrently would oversubscribe the Pi's cores for no gain.
/// </summary>
public sealed class InferenceOrchestrator : BackgroundService
{
    private readonly ChannelReader<CapturedSpeech> _input;
    private readonly SenseVoiceAnalyzer _analyzer;
    private readonly TelemetryRepository _repository;
    private readonly SceneDescriptionService? _scenes;
    private readonly FrameRing? _frames;
    private readonly SpeakerLabeller? _speakers;
    private readonly ConversationTracker? _conversations;
    private readonly int _maxFrames;
    private readonly ILogger<InferenceOrchestrator> _logger;

    public InferenceOrchestrator(
        Channel<CapturedSpeech> channel,
        SenseVoiceAnalyzer analyzer,
        TelemetryRepository repository,
        IOptions<CameraModuleOptions> options,
        ILogger<InferenceOrchestrator> logger,
        SceneDescriptionService? scenes = null,
        FrameRing? frames = null,
        SpeakerLabeller? speakers = null,
        ConversationTracker? conversations = null)
    {
        _input = channel.Reader;
        _analyzer = analyzer;
        _repository = repository;
        _maxFrames = Math.Max(1, options.Value.Vision.MaxFrames);
        _logger = logger;
        _scenes = scenes;
        _frames = frames;
        _speakers = speakers;
        _conversations = conversations;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (CapturedSpeech speech in _input.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(speech, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad utterance must not kill the pipeline. The previous async-void
                // event handler swallowed these entirely.
                _logger.LogError(ex, "Failed to process an utterance; dropping it and continuing.");
            }
        }
    }

    private async Task ProcessAsync(CapturedSpeech speech, CancellationToken cancellationToken)
    {
        SpeechAnalysis analysis = _analyzer.Analyze(speech.Samples, speech.SampleRate);

        if (string.IsNullOrWhiteSpace(analysis.Text))
        {
            _logger.LogDebug("Utterance produced no transcript; discarding.");
            return;
        }

        // Assign the utterance to a conversation before labelling: the conversation scopes
        // speaker identity, and its id is the only join between this record and the
        // after-the-fact diarization published separately for the same conversation.
        Guid? conversationId = _conversations?.NoteUtterance(speech.CapturedAt, speech.Samples);

        string? speaker = conversationId is { } id
            ? _speakers?.Identify(id, speech.Samples, speech.SampleRate)
            : null;

        _logger.LogInformation(
            "Transcript: \"{Text}\" (speaker={Speaker}, emotion={Emotion}, lang={Language}, rtf={Rtf:F2})",
            analysis.Text, speaker ?? "?", analysis.Emotion ?? "?", analysis.Language ?? "?",
            analysis.RealTimeFactor);

        // Speech asks for a description but never waits for one, and does not carry the result. A
        // description takes seconds (tens of seconds on the Pi) against ~0.1s for transcription, so
        // awaiting it here would stall the pipeline and back up the queue.
        //
        // The utterance does not need to carry it: every completed description is published as its
        // own scene record whatever triggered it — speech included, which is what
        // SceneTrigger.Speech marks — so a consumer wanting scene context for this utterance
        // correlates the two on timestamp, and picks its own idea of "nearby" rather than
        // inheriting one frozen here at write time.
        //
        // Speech is one of two things that can ask; the motion gate asks independently.
        if (_scenes is not null && _frames is not null)
        {
            _scenes.RequestDescription(_frames.Recent(_maxFrames));
        }

        var record = new TelemetryRecord
        {
            Id = Guid.NewGuid().ToString(),
            Timestamp = speech.CapturedAt,
            Transcript = analysis.Text,
            Language = analysis.Language,
            Emotion = analysis.Emotion,
            AudioEvent = analysis.AudioEvent,
            DurationSeconds = analysis.DurationSeconds,
            ConversationId = conversationId?.ToString(),
            Speaker = speaker,
        };

        await _repository.SaveAsync(record, cancellationToken);
    }
}
