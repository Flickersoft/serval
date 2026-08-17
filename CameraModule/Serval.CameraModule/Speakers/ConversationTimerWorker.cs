using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Serval.Ai;

namespace Serval.CameraModule;

/// <summary>
/// Polls for the end of a conversation.
///
/// A conversation ends on the *absence* of utterances, which no utterance can announce — so
/// something has to watch the clock. This runs on its own thread rather than inside
/// SpeechDetectionWorker so that finalising (flushing and rewriting the WAV header) never
/// happens on the VAD thread.
/// </summary>
public sealed class ConversationTimerWorker : BackgroundService
{
    /// <summary>Poll rate. Costs a lock and a comparison, so it can be brisk; it bounds how
    /// late a conversation's end is noticed, on top of the silence timeout itself.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly ConversationTracker _tracker;
    private readonly ILogger<ConversationTimerWorker> _logger;

    public ConversationTimerWorker(ConversationTracker tracker, ILogger<ConversationTimerWorker> logger)
    {
        _tracker = tracker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);
                _tracker.CheckForEnd(DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed while checking for the end of a conversation.");
            }
        }

        // Finalise any open conversation so a clean shutdown still produces a diarization
        // record rather than stranding the audio for startup recovery.
        _tracker.FlushOnShutdown();
    }
}
