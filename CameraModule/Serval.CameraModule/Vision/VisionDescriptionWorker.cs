using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serval.Ai;
using Serval.Contracts;

namespace Serval.CameraModule;

/// <summary>
/// Owns the vision model and runs descriptions off the audio path.
///
/// Single consumer, one description at a time. Because this saturates CPU for seconds at a
/// time, the guard rails matter: a minimum gap between descriptions bounds load in a busy
/// scene, and the request channel drops rather than queues. If this worker ever starves the
/// VAD thread it will show up as AudioRingBuffer dropped samples, which AudioCaptureWorker
/// already logs.
///
/// Every completed description is published twice over: as a standalone <c>scene</c> record, and
/// into the cache the audio path attaches to utterances. The scene record is what makes
/// motion-triggered vision visible at all — a description produced when nobody is speaking has no
/// utterance to ride on.
/// </summary>
public sealed class VisionDescriptionWorker : BackgroundService
{
    private readonly SceneDescriptionService _scenes;
    private readonly IVisionInferenceRunner _runner;
    private readonly TelemetryRepository _repository;
    private readonly VisionOptions _options;
    private readonly ILogger<VisionDescriptionWorker> _logger;

    public VisionDescriptionWorker(
        SceneDescriptionService scenes,
        IVisionInferenceRunner runner,
        TelemetryRepository repository,
        IOptions<CameraModuleOptions> options,
        ILogger<VisionDescriptionWorker> logger)
    {
        _scenes = scenes;
        _runner = runner;
        _repository = repository;
        _options = options.Value.Vision;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minimumGap = TimeSpan.FromSeconds(_options.MinSecondsBetweenDescriptions);
        DateTimeOffset lastRun = DateTimeOffset.MinValue;

        await foreach (SceneRequest request in _scenes.Requests.ReadAllAsync(stoppingToken))
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now - lastRun < minimumGap)
            {
                _logger.LogDebug("Skipping description: last one was under {Gap}s ago.", minimumGap.TotalSeconds);
                continue;
            }

            // Clamp to what this backend can actually take. The NPU path accepts exactly one
            // frame, so asking it for two would be a request it cannot honour.
            IReadOnlyList<VisionFrame> frames = request.Frames.Count > _runner.MaxFrames
                ? request.Frames.TakeLast(_runner.MaxFrames).ToList()
                : request.Frames;

            if (frames.Count == 0)
            {
                continue;
            }

            try
            {
                string text = await _runner.RunInferenceAsync(frames, PromptFor(frames), stoppingToken);
                lastRun = DateTimeOffset.UtcNow;

                if (string.IsNullOrWhiteSpace(text))
                {
                    _logger.LogWarning("Vision model returned an empty description; keeping the previous one.");
                    continue;
                }

                _scenes.Publish(new SceneDescription(text, lastRun));
                await PublishSceneAsync(request, frames, text, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shut down mid-description. Whatever was generated is a fragment, so it is
                // dropped rather than published.
                _logger.LogDebug("Description abandoned: shutting down.");
                break;
            }
            catch (Exception ex)
            {
                // A failed description must not take the process down or stop audio.
                _logger.LogError(ex, "Vision inference failed; keeping the previous description.");
                lastRun = DateTimeOffset.UtcNow;
            }
        }
    }

    /// <summary>
    /// One frame can only say what is there; several can say what is happening. The multi-frame
    /// prompt states the spacing, because "what changed" is meaningless without knowing over what.
    /// </summary>
    private string PromptFor(IReadOnlyList<VisionFrame> frames)
    {
        if (frames.Count < 2)
        {
            return _options.Prompt;
        }

        double seconds = (frames[^1].CapturedAt - frames[0].CapturedAt).TotalSeconds;

        return _options.MotionPrompt
            .Replace("{count}", frames.Count.ToString(), StringComparison.Ordinal)
            .Replace("{seconds}", seconds.ToString("0.#"), StringComparison.Ordinal);
    }

    private async Task PublishSceneAsync(
        SceneRequest request,
        IReadOnlyList<VisionFrame> frames,
        string description,
        CancellationToken cancellationToken)
    {
        var scene = new SceneDocument
        {
            Id = Guid.NewGuid().ToString(),
            Timestamp = frames[^1].CapturedAt,
            Description = description,
            Trigger = request.Trigger,
            MotionScore = request.MotionScore is { } score ? Math.Round(score, 4) : null,
            FrameCount = frames.Count,
            FrameSpanSeconds = Math.Round(
                frames.Count < 2 ? 0 : (frames[^1].CapturedAt - frames[0].CapturedAt).TotalSeconds, 2),
            Source = TelemetrySource.Module,
        };

        await _repository.SaveRecordAsync(scene.Id, scene, cancellationToken);
    }
}
