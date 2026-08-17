using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serval.Ai;
using Serval.Contracts;

namespace Serval.CameraModule;

/// <summary>
/// Hosts the offline conversation pass: waits for conversations to finish, hands them to the
/// shared <see cref="ConversationReprocessor"/>, and queues both records it produces.
///
/// All the judgement lives in the reprocessor, which the Server runs too. What is left here is the
/// edge-specific part: which utterances belong to the conversation (read back out of the local
/// outbox), and recovering audio that survived a crash.
///
/// One conversation at a time, and the CPU burst lands during silence by construction — but it
/// still shares the board with the VAD and ASR, which are on a deadline. So the burst itself runs
/// on a dedicated below-normal-priority thread (see <see cref="RunBelowNormalAsync"/>) while the
/// surrounding loop stays on the thread pool. Watch AudioRingBuffer.DroppedSamples.
/// </summary>
public sealed class DiarizationWorker : BackgroundService
{
    private readonly ConversationTracker _tracker;
    private readonly TelemetryRepository _repository;
    private readonly SenseVoiceAnalyzer _analyzer;
    private readonly SpeakerOptions _options;
    private readonly VadOptions _vad;
    private readonly ILogger<DiarizationWorker> _logger;

    public DiarizationWorker(
        ConversationTracker tracker,
        TelemetryRepository repository,
        SenseVoiceAnalyzer analyzer,
        IOptions<CameraModuleOptions> options,
        ILogger<DiarizationWorker> logger)
    {
        _tracker = tracker;
        _repository = repository;
        _analyzer = analyzer;
        _options = options.Value.Speaker;
        _vad = options.Value.Vad;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield so host startup is not held up by the model load below.
        await Task.Yield();

        using var reprocessor = new ConversationReprocessor(_options, _vad, _analyzer, _logger);

        await RecoverOrphansAsync(reprocessor, stoppingToken);

        await foreach (FinishedConversation conversation in _tracker.Finished.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(reprocessor, conversation, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad conversation must not stop the rest, and must not kill audio.
                _logger.LogError(ex, "Reprocessing failed for conversation {Id}.", conversation.ConversationId);
            }
        }
    }

    private async Task ProcessAsync(
        ConversationReprocessor reprocessor,
        FinishedConversation conversation,
        CancellationToken cancellationToken)
    {
        string id = conversation.ConversationId.ToString();

        IReadOnlyList<LiveUtterance> utterances =
            await _repository.GetConversationUtterancesAsync(id, cancellationToken);

        ReprocessedConversation? result = await RunBelowNormalAsync(
            () => reprocessor.Process(conversation, utterances, TelemetrySource.Module));

        if (result is null)
        {
            TryDelete(conversation.AudioPath);
            return;
        }

        // Keyed by conversation id so reprocessing a recovered conversation replaces its
        // records rather than queueing a second, contradictory pair.
        await _repository.SaveRecordAsync($"diarization-{id}", result.Diarization, cancellationToken);
        await _repository.SaveRecordAsync($"transcript-{id}", result.Transcript, cancellationToken);

        _logger.LogInformation(
            "Conversation {Id}: {Turns} attributed turn(s) from {Utterances} utterance(s); "
            + "{Retranscribed} needed re-transcribing.",
            id, result.Transcript.Turns.Count, utterances.Count, result.Transcript.RetranscribedTurns);

        // The audio has served its purpose; keeping it would fill the Pi's storage.
        TryDelete(conversation.AudioPath);
    }

    /// <summary>
    /// Runs one synchronous CPU burst on a dedicated below-normal-priority thread.
    ///
    /// A real thread, not a pool thread. Lowering a pool thread's priority would not survive the
    /// next <c>await</c> — the continuation lands on some other, normal-priority thread — and the
    /// demoted one carries the change back into the pool for unrelated work to inherit. Diarization
    /// is the only thing here that must yield: nothing waits on it, where the VAD and ASR sharing
    /// this board are on a deadline.
    ///
    /// One thread per finished conversation, which is free at the rate conversations end.
    /// Best-effort — a platform that refuses the priority change is no reason not to run.
    /// </summary>
    private Task<T> RunBelowNormalAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not lower the diarization thread priority."); }

            try
            {
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "serval-diarization",
        };

        thread.Start();
        return completion.Task;
    }

    /// <summary>
    /// Reprocesses conversations whose audio survived a crash. The WAV is on disk, so a process
    /// killed mid-conversation loses only the unwritten tail — without this those
    /// conversations would silently never get a diarization record.
    /// </summary>
    private async Task RecoverOrphansAsync(
        ConversationReprocessor reprocessor, CancellationToken cancellationToken)
    {
        string directory = _tracker.Scope is null
            ? _options.ConversationAudioDirectory
            : Path.Combine(_options.ConversationAudioDirectory, _tracker.Scope);

        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string path in Directory.GetFiles(directory, "conversation-*.wav"))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            string stem = Path.GetFileNameWithoutExtension(path)["conversation-".Length..];
            if (!Guid.TryParseExact(stem, "N", out Guid id))
            {
                continue;
            }

            var info = new FileInfo(path);
            if (info.Length <= 44)
            {
                TryDelete(path);
                continue;
            }

            _logger.LogInformation("Recovering orphaned conversation {Id} from a previous run.", id);

            try
            {
                // A crash leaves the RIFF/data sizes unwritten (they are only patched on a
                // clean close), so repair them from the file length before reading.
                double seconds = ConversationAudioBuffer.RepairHeader(path, reprocessor.SampleRate);
                if (seconds < 1.0)
                {
                    TryDelete(path);
                    continue;
                }

                await ProcessAsync(
                    reprocessor,
                    new FinishedConversation(id, path, info.CreationTimeUtc, seconds),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not recover conversation {Id}; deleting its audio.", id);
                TryDelete(path);
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete {Path}.", path);
        }
    }
}
