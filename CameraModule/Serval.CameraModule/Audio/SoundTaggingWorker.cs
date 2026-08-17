using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serval.Ai;
using Serval.Contracts;

namespace Serval.CameraModule;

/// <summary>
/// Classifies the segments <see cref="SpeechDetectionWorker"/> cut, and records what survives the
/// policy.
///
/// A separate worker rather than work done on the detection thread, for the reason
/// <see cref="InferenceOrchestrator"/> is separate: that thread is the only consumer of a
/// four-second ring buffer, and anything it waits for turns into dropped samples. The segmenter
/// fires more often than the VAD does, so this matters more here than it does for speech.
///
/// Single consumer by design — the tagger serializes internally anyway, and two of these would
/// only contend for the same cores.
/// </summary>
public sealed class SoundTaggingWorker : BackgroundService
{
    private readonly ChannelReader<SoundSegment> _input;
    private readonly SoundEventTagger _tagger;
    private readonly SoundEventPolicy _policy;
    private readonly TelemetryRepository _repository;
    private readonly ILogger<SoundTaggingWorker> _logger;

    public SoundTaggingWorker(
        Channel<SoundSegment> channel,
        SoundEventTagger tagger,
        TelemetryRepository repository,
        IOptions<CameraModuleOptions> options,
        ILogger<SoundTaggingWorker> logger)
    {
        _input = channel.Reader;
        _tagger = tagger;
        _repository = repository;
        _policy = new SoundEventPolicy(options.Value.Sound);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (SoundSegment segment in _input.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(segment, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad segment must not kill the pipeline, exactly as for an utterance.
                _logger.LogError(ex, "Failed to process a sound segment; dropping it and continuing.");
            }
        }
    }

    private async Task ProcessAsync(SoundSegment segment, CancellationToken cancellationToken)
    {
        IReadOnlyList<ScoredSound> shortlist = _tagger.Tag(segment.Samples, segment.SampleRate);

        SoundVerdict? verdict = _policy.Decide(shortlist, DateTimeOffset.UtcNow);
        if (verdict is null)
        {
            return;
        }

        // No scene description is attached. Every completed description is already published as its
        // own scene record, and a consumer correlates the two on timestamp — which lets it choose
        // its own window rather than inherit one frozen here.
        var record = new SoundDocument
        {
            Id = Guid.NewGuid().ToString(),
            Timestamp = segment.CapturedAt,
            Label = verdict.Label,
            Confidence = Math.Round(verdict.Confidence, 3),
            Alternates = [.. verdict.Alternates.Select(a => new SoundAlternate
            {
                Label = a.Label,
                Confidence = Math.Round(a.Confidence, 3),
            })],
            IsAlert = verdict.IsAlert,
            DurationSeconds = Math.Round(segment.DurationSeconds, 2),
        };

        // An alert is the one thing here someone may be woken by, so it is worth more than the
        // information level a routine dog gets.
        if (verdict.IsAlert)
        {
            _logger.LogWarning(
                "Sound ALERT: {Label} ({Confidence:P0}, {Seconds:F1}s)",
                verdict.Label, verdict.Confidence, segment.DurationSeconds);
        }
        else
        {
            _logger.LogInformation(
                "Sound: {Label} ({Confidence:P0}, {Seconds:F1}s)",
                verdict.Label, verdict.Confidence, segment.DurationSeconds);
        }

        await _repository.SaveRecordAsync(record.Id, record, cancellationToken);
    }
}
