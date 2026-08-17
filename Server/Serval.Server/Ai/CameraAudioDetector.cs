using System.Threading.Channels;
using Serval.Ai;
using Serval.Contracts;
using Serval.Server.Alerts;
using Serval.Server.Cameras;
using Serval.Server.Configuration;
using Serval.Server.Events;
using Serval.Server.Telemetry;

namespace Serval.Server.Ai;

/// <summary>
/// One camera's server-side audio pipeline, from PCM to stored telemetry:
///
/// <code>
/// ffmpeg → AudioRingBuffer →┬→ AudioLevelGate → SileroSpeechDetector
///                           │  → SenseVoice + SpeakerLabeller + ConversationTracker → utterance
///                           │  → (conversation ends) → ConversationReprocessor → diarization + transcript
///                           │
///                           └→ SoundEventSegmenter → SoundEventTagger → SoundEventPolicy → sound
/// </code>
///
/// The two branches are independent. Sound tagging is not behind the VAD — Silero rejects
/// everything that is not speech, so a car horn could never reach that branch — and has its own
/// level gate and segmentation. They share one loop because <see cref="AudioRingBuffer"/> is
/// single-consumer, which constrains who may read the ring rather than coupling the detectors.
/// Overlap is expected: speech with a dog barking over it produces an utterance and a sound.
///
/// Every stage between the ring buffer and the records is the shared library, identical to what runs
/// on the edge. Only the ends differ — where the audio came from, and that records are written
/// straight to the repository rather than POSTed over HTTP.
///
/// The heavy per-camera state is the speaker embedding extractor and the diarizer, both per-camera
/// by necessity: speaker identity is scoped to a conversation, and conversations to a camera.
/// </summary>
public sealed class CameraAudioDetector : IDisposable
{
    private readonly Camera _camera;
    private readonly SenseVoiceAnalyzer _analyzer;
    private readonly TelemetryRepository _repository;
    private readonly EventBroadcaster _events;

    /// <summary>
    /// This camera's AI settings, already resolved against its per-camera overrides by
    /// <see cref="CameraAiOptions.For"/>. Deliberately the only options this class holds — with
    /// per-camera thresholds, any global left in reach is a way for the offline reprocessing pass
    /// to silently run a differently-tuned VAD than the live one.
    /// </summary>
    private readonly AiOptions _ai;
    private readonly IngestOptions _ingest;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    private readonly AudioRingBuffer _ringBuffer;
    private readonly ConversationTracker? _conversations;
    private readonly SpeakerLabeller? _speakers;

    private readonly SoundEventTagger? _tagger;
    private readonly SoundEventSegmenter? _segmenter;
    private readonly SoundEventPolicy? _policy;
    private readonly Channel<SoundSegment>? _sounds;

    private readonly AudioLevelBroadcaster? _levels;

    /// <summary>
    /// Where an alerting sound becomes a row in the queue. Optional so this class stays
    /// constructible in tests and on any path that has no alert plumbing — a sound that raises no
    /// alert is still stored and still published.
    /// </summary>
    private readonly AlertService? _alerts;

    /// <summary>
    /// Windows accumulated per published level. Derived from the sample rate rather than read off
    /// a clock so the cadence is deterministic and therefore testable — at 16 kHz and 512-sample
    /// windows this is 3, or a little over ten readings a second.
    /// </summary>
    private readonly int _meterWindowsPerPublish;

    private int _meterWindows;
    private double _meterSumOfSquares;
    private float _meterPeak;

    public CameraAudioDetector(
        Camera camera,
        SenseVoiceAnalyzer analyzer,
        TelemetryRepository repository,
        EventBroadcaster events,
        AiOptions ai,
        IngestOptions ingest,
        ILoggerFactory loggerFactory,
        SoundEventTagger? tagger = null,
        AudioLevelBroadcaster? levels = null,
        AlertService? alerts = null)
    {
        _camera = camera;
        _analyzer = analyzer;
        _repository = repository;
        _events = events;
        _ai = ai;
        _ingest = ingest;
        _loggerFactory = loggerFactory;
        _alerts = alerts;

        // One fixed category for every camera; the id rides along as a scope opened in RunAsync.
        // A category per camera can't be targeted by a Logging:LogLevel rule and turns into
        // unbounded label churn in a log aggregator.
        _logger = loggerFactory.CreateLogger<CameraAudioDetector>();

        _levels = levels;
        _meterWindowsPerPublish =
            Math.Max(1, (int)Math.Round(0.1 * ai.Vad.SampleRate / ai.Vad.WindowSize));

        _ringBuffer = new AudioRingBuffer(ai.Vad.SampleRate * 4);

        if (ai.Speaker.Enabled)
        {
            // Scoped by camera id so two cameras' conversation audio cannot collide on disk.
            _conversations = new ConversationTracker(
                ai.Speaker,
                ai.Vad,
                loggerFactory.CreateLogger<ConversationTracker>(),
                loggerFactory,
                scope: camera.Id);

            _speakers = new SpeakerLabeller(ai.Speaker, loggerFactory.CreateLogger<SpeakerLabeller>());
        }

        // The tagger is shared across every camera — it is stateless per stream — but the segmenter
        // and the policy are not: the level gate tracks this camera's room, and the cooldown is
        // per-camera by definition.
        if (tagger is not null && ai.Sound.Enabled)
        {
            _tagger = tagger;
            _segmenter = new SoundEventSegmenter(ai.Sound, ai.Vad.WindowSize, ai.Vad.SampleRate);
            _policy = new SoundEventPolicy(ai.Sound);

            // DropOldest for the same reason the vision request channel uses it: a queued segment
            // describes a sound that has already finished, so under load the newest is the one
            // worth keeping.
            _sounds = Channel.CreateBounded<SoundSegment>(
                new BoundedChannelOptions(4)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = true,
                });
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Every job below is started from inside this scope, and scope state flows with the async
        // context, so one scope here tags the whole pipeline's logging with the camera. A message
        // template rather than a dictionary: it renders as "CameraId:front-door" for a human
        // reading the console, and still yields a named CameraId field under the JSON formatter.
        using IDisposable? scope = _logger.BeginScope("CameraId:{CameraId}", _camera.Id);

        // Audio comes off the detect stream, the same source the snapshots do — normally the sub
        // stream, which carries the same sound the main one does.
        var session = new FfmpegAudioSession(
            _camera, _camera.DetectStream!, _ringBuffer, _ai.Vad.SampleRate, _ingest, _logger);

        // Concurrent jobs: pull the audio, detect speech in it, and — when speaker tracking is on
        // — notice when a conversation has gone quiet and reprocess it once it has. The quiet
        // watcher needs its own timer because the defining event of a conversation ending is the
        // *absence* of an utterance, which no utterance can announce.
        //
        // The jobs stand or fall together, hence the task group rather than Task.WhenAll: ffmpeg
        // exiting is a routine event (a camera reboot, a network blip), and every other job here
        // runs until cancelled. Under WhenAll that failure would never be observed and this
        // detector would sit forever, apparently healthy, with no audio flowing through it.
        var jobs = new List<Func<CancellationToken, Task>>
        {
            session.RunAsync,
            token => Task.Run(() => RunDetectionLoop(token), token),
        };

        if (_conversations is not null)
        {
            jobs.Add(WatchConversationBoundariesAsync);
            jobs.Add(ReprocessFinishedConversationsAsync);
        }

        // Tagging runs off the detection loop rather than on it. The segmenter fires more often
        // than the VAD does, and this thread is the only consumer of a four-second ring buffer —
        // anything it waits for turns into dropped samples.
        if (_segmenter is not null)
        {
            jobs.Add(TagSoundSegmentsAsync);
        }

        try
        {
            await TaskGroup.RunUntilFirstCompletesAsync(jobs, cancellationToken);
        }
        finally
        {
            _conversations?.FlushOnShutdown();
        }
    }

    private async Task RunDetectionLoop(CancellationToken cancellationToken)
    {
        AiOptions ai = _ai;

        using var detector = new SileroSpeechDetector(ai.Vad, _logger);
        var gate = new AudioLevelGate(ai.AudioGate, detector.WindowSize, ai.Vad.SampleRate);

        // Cached so the hot path allocates no delegate: these run ~31 times a second per camera.
        WindowSink sink = detector.Accept;
        SegmentSink segmentSink = EmitSegment;

        var window = new float[detector.WindowSize];
        int filled = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            int read = _ringBuffer.Read(window.AsSpan(filled));
            if (read == 0)
            {
                await Task.Delay(16, cancellationToken);
                continue;
            }

            filled += read;
            if (filled < window.Length)
            {
                continue;
            }

            filled = 0;

            // Tee every window, including the ones the gate will drop. The conversation audio has
            // to be continuous for diarization offsets to mean anything; a hole here would shift
            // every later timestamp.
            _conversations?.AppendAudio(window);

            // Parallel to the VAD below, not behind it. Its own gate, its own segmentation;
            // neither branch can gate or starve the other.
            _segmenter?.Accept(window, segmentSink);

            gate.Accept(window, sink);

            // After both gates, so the open/closed flags describe the window just fed.
            MeterWindow(window, gate);

            while (detector.TryDequeue(out float[] utterance))
            {
                await ProcessUtteranceAsync(utterance, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Accumulates the level a settings screen draws, and publishes it about ten times a second.
    ///
    /// Costs nothing when nobody is watching, which is the whole design: a camera nobody is tuning
    /// must not pay an RMS pass, an allocation and a timestamp thirty-one times a second for a
    /// number that would be dropped on the floor.
    ///
    /// The RMS is measured here rather than read from <see cref="AudioLevelGate.LastRms"/> because
    /// that property is only assigned on the enabled path — a disabled gate forwards everything
    /// and never measures, so the meter would read a flat zero for exactly the person who turned
    /// the gate off to find out what the room sounds like.
    /// </summary>
    private void MeterWindow(ReadOnlySpan<float> window, AudioLevelGate gate)
    {
        if (_levels is null || !_levels.HasSubscribers(_camera.Id))
        {
            // Reset, so the first reading after someone subscribes is not a peak from minutes ago.
            _meterWindows = 0;
            _meterSumOfSquares = 0;
            _meterPeak = 0;
            return;
        }

        float rms = AudioLevelGate.Rms(window);
        _meterSumOfSquares += (double)rms * rms;
        _meterPeak = Math.Max(_meterPeak, rms);

        if (++_meterWindows < _meterWindowsPerPublish)
        {
            return;
        }

        _levels.Publish(new AudioLevel(
            CameraId: _camera.Id,
            At: DateTimeOffset.UtcNow,
            Rms: (float)Math.Sqrt(_meterSumOfSquares / _meterWindows),
            Peak: _meterPeak,
            SpeechThreshold: _ai.AudioGate.RmsThreshold,
            SoundThreshold: _ai.Sound.Gate.RmsThreshold,
            SpeechGateOpen: gate.IsOpen,
            SoundGateOpen: _segmenter?.IsOpen ?? false));

        _meterWindows = 0;
        _meterSumOfSquares = 0;
        _meterPeak = 0;
    }

    private async Task ProcessUtteranceAsync(float[] samples, CancellationToken cancellationToken)
    {
        int sampleRate = _ai.Vad.SampleRate;
        var capturedAt = DateTimeOffset.UtcNow;

        SpeechAnalysis analysis;
        try
        {
            analysis = _analyzer.Analyze(samples, sampleRate);
        }
        catch (Exception ex)
        {
            // One bad utterance must not stop the camera's audio pipeline.
            _logger.LogError(ex, "Analysis failed for an utterance; dropping it and continuing.");
            return;
        }

        if (string.IsNullOrWhiteSpace(analysis.Text))
        {
            return;
        }

        Guid? conversationId = _conversations?.NoteUtterance(capturedAt, samples);
        string? speaker = conversationId is { } id
            ? _speakers?.Identify(id, samples, sampleRate)
            : null;

        _logger.LogInformation(
            "Transcript: \"{Text}\" (speaker={Speaker}, emotion={Emotion}, lang={Language}, rtf={Rtf:F2})",
            analysis.Text, speaker ?? "?", analysis.Emotion ?? "?", analysis.Language ?? "?",
            analysis.RealTimeFactor);

        var document = new UtteranceDocument
        {
            Id = Guid.NewGuid().ToString(),
            CameraId = _camera.Id,
            ReceivedAt = DateTimeOffset.UtcNow,
            Timestamp = capturedAt,
            Transcript = analysis.Text,
            Language = analysis.Language,
            Emotion = analysis.Emotion,
            AudioEvent = analysis.AudioEvent,
            DurationSeconds = Math.Round(analysis.DurationSeconds, 3),
            ConversationId = conversationId?.ToString(),
            Speaker = speaker,
            SpeakerSource = speaker is null ? null : "live",
            Source = TelemetrySource.Server,
        };

        await _repository.UpsertUtteranceAsync(document, cancellationToken);
        _events.Publish(new LiveEvent(_camera.Id, document.Type, document));
    }

    private void EmitSegment(SoundSegment segment)
    {
        // Bounded with DropOldest, so a failed write means the writer is completed (shutdown)
        // rather than the channel being full.
        if (_sounds?.Writer.TryWrite(segment) == false)
        {
            _logger.LogDebug(
                "Dropped a {Seconds:F2}s sound segment: shutting down.", segment.DurationSeconds);
        }
    }

    /// <summary>Only started when sound tagging is on; <c>_segmenter</c> and friends are non-null.</summary>
    private async Task TagSoundSegmentsAsync(CancellationToken cancellationToken)
    {
        await foreach (SoundSegment segment in _sounds!.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await PublishSoundAsync(segment, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad segment must not stop the camera's audio pipeline.
                _logger.LogError(ex, "Failed to process a sound segment; dropping it and continuing.");
            }
        }
    }

    private async Task PublishSoundAsync(SoundSegment segment, CancellationToken cancellationToken)
    {
        IReadOnlyList<ScoredSound> shortlist = _tagger!.Tag(segment.Samples, segment.SampleRate);

        SoundVerdict? verdict = _policy!.Decide(shortlist, DateTimeOffset.UtcNow);
        if (verdict is null)
        {
            return;
        }

        // No scene description is attached. Every completed description is already published as its
        // own scene record, so a consumer correlates the two on timestamp and picks its own window.
        var document = new SoundDocument
        {
            Id = Guid.NewGuid().ToString(),
            CameraId = _camera.Id,
            ReceivedAt = DateTimeOffset.UtcNow,
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
            Source = TelemetrySource.Server,
        };

        // An alert is the one thing here someone may be woken by, so it outranks the information
        // level a routine dog gets.
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

        await _repository.UpsertSoundAsync(document, cancellationToken);
        _events.Publish(new LiveEvent(_camera.Id, document.Type, document));

        // After the store, so an alert in the queue always has the record it came from behind it.
        // A sound is a single moment rather than an episode, so there is no open-and-close here and
        // nothing to deduplicate — this fires once per sound.
        if (document.IsAlert && _alerts is not null)
        {
            await _alerts.RaiseSoundAsync(_camera, document, cancellationToken);
        }
    }

    /// <summary>Only started when speaker tracking is on; <c>_conversations</c> is non-null.</summary>
    private async Task WatchConversationBoundariesAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                _conversations!.CheckForEnd(DateTimeOffset.UtcNow);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Only started when speaker tracking is on; <c>_conversations</c> is non-null.</summary>
    private async Task ReprocessFinishedConversationsAsync(CancellationToken cancellationToken)
    {
        // Constructed lazily on the first finished conversation: it loads the pyannote model, and
        // a camera that never hears anybody talk should not pay for it.
        ConversationReprocessor? reprocessor = null;

        try
        {
            await foreach (FinishedConversation conversation in
                _conversations!.Finished.ReadAllAsync(cancellationToken))
            {
                try
                {
                    // This camera's VAD settings, not the server's. Reprocessing re-segments the
                    // conversation audio, so a threshold differing from the live pass would split
                    // it into turns that do not line up with the utterances already stored.
                    reprocessor ??= new ConversationReprocessor(
                        _ai.Speaker, _ai.Vad, _analyzer, _logger);

                    await ReprocessAsync(reprocessor, conversation, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex, "Reprocessing failed for conversation {Id}.", conversation.ConversationId);
                }
            }
        }
        finally
        {
            reprocessor?.Dispose();
        }
    }

    private async Task ReprocessAsync(
        ConversationReprocessor reprocessor,
        FinishedConversation conversation,
        CancellationToken cancellationToken)
    {
        string id = conversation.ConversationId.ToString();

        IReadOnlyList<UtteranceDocument> stored =
            await _repository.GetConversationUtterancesAsync(_camera.Id, id, cancellationToken);

        var utterances = stored
            .Select(u => new LiveUtterance(u.Timestamp, u.DurationSeconds, u.Transcript, u.Emotion))
            .ToList();

        ReprocessedConversation? result =
            reprocessor.Process(conversation, utterances, TelemetrySource.Server);

        TryDelete(conversation.AudioPath);

        if (result is null)
        {
            return;
        }

        var receivedAt = DateTimeOffset.UtcNow;

        result.Diarization.CameraId = _camera.Id;
        result.Diarization.ReceivedAt = receivedAt;
        await _repository.UpsertDiarizationAsync(result.Diarization, cancellationToken);
        _events.Publish(new LiveEvent(_camera.Id, result.Diarization.Type, result.Diarization));

        result.Transcript.CameraId = _camera.Id;
        result.Transcript.ReceivedAt = receivedAt;
        await _repository.UpsertConversationTranscriptAsync(result.Transcript, cancellationToken);
        _events.Publish(new LiveEvent(_camera.Id, result.Transcript.Type, result.Transcript));

        _logger.LogInformation(
            "Conversation {Id}: {Turns} attributed turn(s) from {Utterances} utterance(s); "
            + "{Retranscribed} needed re-transcribing.",
            id, result.Transcript.Turns.Count, utterances.Count, result.Transcript.RetranscribedTurns);
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

    public void Dispose()
    {
        _conversations?.Dispose();
        _speakers?.Dispose();
    }
}
