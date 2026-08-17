using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serval.Contracts;

namespace Serval.Ai.Tests;

/// <summary>What one fixture produced, live half and offline half together.</summary>
/// <param name="LiveSpeakers">
/// Distinct labels <see cref="SpeakerLabeller"/> minted. Reported, never asserted — see
/// <see cref="ConversationOverFixtureTests"/> for why the live half cannot be held to a count.
/// </param>
/// <param name="MeanRms">
/// Loudness as <see cref="AudioLevelGate"/> itself measures it, so it can be read directly against
/// <see cref="AudioGateOptions.RmsThreshold"/>. The single most useful number when a room produces
/// no transcripts at all.
/// </param>
internal sealed record FixtureRun(
    IReadOnlyList<UtteranceDocument> Utterances,
    DiarizationDocument? Diarization,
    ConversationTranscriptDocument? Transcript,
    int LiveSpeakers,
    float MeanRms,
    float PeakRms,
    long WindowsAdmitted,
    long WindowsSkipped,
    double AudioSeconds,
    double ElapsedSeconds)
{
    public int SpeakerCount => Transcript?.SpeakerCount ?? 0;

    public IReadOnlyList<TranscriptTurn> Turns => Transcript?.Turns ?? [];
}

/// <summary>
/// Runs a WAV through the real audio pipeline and returns the records a host would have published.
///
/// <para>This mirrors <c>CameraAudioDetector.RunDetectionLoop</c> and its
/// <c>ProcessUtteranceAsync</c>, in that order, holding real instances of every stage. The one
/// thing it substitutes is the clock; everything else — the gate, Silero, SenseVoice, the labeller,
/// the tracker, the reprocessor — is the code that runs on a camera.</para>
///
/// <para><b>Why the clock is simulated rather than real.</b> A host stamps an utterance with
/// <c>UtcNow</c> at the moment the VAD hands it over, and because the host runs in realtime that
/// timestamp lands a predictable distance into the audio.
/// <c>ConversationReprocessor.SpanOf</c> relies on exactly that: it recovers an utterance's span by
/// subtracting <see cref="VadOptions.MinSilenceSeconds"/> and the utterance's own duration from its
/// timestamp. Push ninety seconds of audio through in four and stamp <c>UtcNow</c>, and every
/// utterance claims the same instant — attribution then fails for a reason that has nothing to do
/// with the models, which is the one failure this class exists to rule out. So utterances are
/// stamped at their true position in the stream: <c>origin + samplesFed / sampleRate</c>. That is
/// what a realtime host would have written, computed rather than waited for.</para>
///
/// <para>The realtime case is not left unchecked — the module's <c>--replay</c> paces a WAV at
/// realtime through the same stages, and the Server drives the genuine loop off a file-source
/// camera. This is the fast half of that pair, not a replacement for it.</para>
/// </summary>
internal static class FixtureConversationPipeline
{
    /// <summary>
    /// Silence appended after the file so the VAD's hangover releases the final utterance.
    ///
    /// A file simply stops; a microphone does not. Silero only emits a segment once it has seen
    /// <see cref="VadOptions.MinSilenceSeconds"/> of trailing silence, so without this the last
    /// thing anybody said is still inside the detector when the loop ends. <c>ReplayAudioWorker</c>
    /// appends the same tail for the same reason.
    /// </summary>
    private const double TrailingSilenceSeconds = 2.0;

    /// <summary>A fixed origin, so a failure reproduces with the same timestamps it first had.</summary>
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <param name="ai">
    /// Fully resolved, including <see cref="SpeakerOptions.ConversationAudioDirectory"/> — each run
    /// wants its own scratch directory, and the caller is the one that knows where.
    /// </param>
    public static FixtureRun Run(string wavPath, AiOptions ai)
    {
        (float[] samples, int sampleRate) = WavFile.Read(wavPath);

        if (sampleRate != ai.Vad.SampleRate)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(wavPath)} is {sampleRate} Hz; the pipeline runs at "
                + $"{ai.Vad.SampleRate} Hz. Resample it: sox in.wav -r 16000 -c 1 -b 16 out.wav");
        }

        SpeakerOptions speaker = ai.Speaker;

        ILoggerFactory loggerFactory = NullLoggerFactory.Instance;
        ILogger logger = NullLogger.Instance;

        using var analyzer = new SenseVoiceAnalyzer(
            ai.Asr, loggerFactory.CreateLogger<SenseVoiceAnalyzer>());
        using var detector = new SileroSpeechDetector(ai.Vad, logger);
        using var conversations = new ConversationTracker(
            speaker, ai.Vad, loggerFactory.CreateLogger<ConversationTracker>(), loggerFactory);
        using var labeller = new SpeakerLabeller(
            speaker, loggerFactory.CreateLogger<SpeakerLabeller>());

        var gate = new AudioLevelGate(ai.AudioGate, detector.WindowSize, ai.Vad.SampleRate);
        WindowSink sink = detector.Accept;

        var utterances = new List<UtteranceDocument>();
        var live = new List<LiveUtterance>();
        var speakers = new HashSet<string>();

        double sumOfSquares = 0;
        float peak = 0;
        int meteredWindows = 0;

        var stopwatch = Stopwatch.StartNew();

        // The file, then the tail of silence. Both go through the loop identically — the tracker
        // tees every window including silence, and a hole here would shift every later timestamp.
        int silenceWindows = (int)Math.Ceiling(TrailingSilenceSeconds * sampleRate / detector.WindowSize);
        var silence = new float[detector.WindowSize];
        int fed = 0;

        for (int offset = 0; offset + detector.WindowSize <= samples.Length; offset += detector.WindowSize)
        {
            ReadOnlySpan<float> window = samples.AsSpan(offset, detector.WindowSize);
            fed += detector.WindowSize;

            Pump(window);
        }

        for (int i = 0; i < silenceWindows; i++)
        {
            fed += detector.WindowSize;
            Pump(silence);
        }

        stopwatch.Stop();

        // Finalise without waiting out the silence timeout. The boundary is a wall-clock judgement
        // the tracker makes against the last utterance it saw, so it can simply be told that enough
        // time has passed.
        DateTimeOffset wellPastTheEnd = Origin
            + TimeSpan.FromSeconds((double)fed / sampleRate)
            + TimeSpan.FromMinutes(speaker.SilenceTimeoutMinutes)
            + TimeSpan.FromSeconds(1);

        conversations.CheckForEnd(wellPastTheEnd);

        DiarizationDocument? diarization = null;
        ConversationTranscriptDocument? transcript = null;

        if (conversations.Finished.TryRead(out FinishedConversation? finished))
        {
            using var reprocessor = new ConversationReprocessor(
                speaker, ai.Vad, analyzer, loggerFactory.CreateLogger<ConversationReprocessor>());

            ReprocessedConversation? reprocessed = reprocessor.Process(finished, live, TelemetrySource.Server);
            diarization = reprocessed?.Diarization;
            transcript = reprocessed?.Transcript;
        }

        return new FixtureRun(
            Utterances: utterances,
            Diarization: diarization,
            Transcript: transcript,
            LiveSpeakers: speakers.Count,
            MeanRms: meteredWindows == 0 ? 0f : (float)Math.Sqrt(sumOfSquares / meteredWindows),
            PeakRms: peak,
            WindowsAdmitted: gate.WindowsAdmitted,
            WindowsSkipped: gate.WindowsSkipped,
            AudioSeconds: (double)samples.Length / sampleRate,
            ElapsedSeconds: stopwatch.Elapsed.TotalSeconds);

        // One window, in the order CameraAudioDetector.RunDetectionLoop uses it: tee first, then
        // gate, then drain. Sound tagging is the one branch left out — it is parallel to the VAD
        // and answers a different question.
        void Pump(ReadOnlySpan<float> window)
        {
            conversations.AppendAudio(window);

            float rms = AudioLevelGate.Rms(window);
            sumOfSquares += (double)rms * rms;
            peak = Math.Max(peak, rms);
            meteredWindows++;

            gate.Accept(window, sink);

            while (detector.TryDequeue(out float[] utterance))
            {
                Process(utterance);
            }
        }

        // ProcessUtteranceAsync, minus the repository and the broadcaster.
        void Process(float[] utterance)
        {
            DateTimeOffset capturedAt = Origin + TimeSpan.FromSeconds((double)fed / sampleRate);

            SpeechAnalysis analysis = analyzer.Analyze(utterance, sampleRate);
            if (string.IsNullOrWhiteSpace(analysis.Text))
            {
                return;
            }

            Guid conversationId = conversations.NoteUtterance(capturedAt, utterance);
            string? who = labeller.Identify(conversationId, utterance, sampleRate);

            if (who is not null)
            {
                speakers.Add(who);
            }

            utterances.Add(new UtteranceDocument
            {
                Id = Guid.NewGuid().ToString(),
                CameraId = "fixture",
                ReceivedAt = capturedAt,
                Timestamp = capturedAt,
                Transcript = analysis.Text,
                Language = analysis.Language,
                Emotion = analysis.Emotion,
                AudioEvent = analysis.AudioEvent,
                DurationSeconds = Math.Round(analysis.DurationSeconds, 3),
                ConversationId = conversationId.ToString(),
                Speaker = who,
                SpeakerSource = who is null ? null : "live",
                Source = TelemetrySource.Server,
            });

            live.Add(new LiveUtterance(
                capturedAt, analysis.DurationSeconds, analysis.Text, analysis.Emotion));
        }
    }
}
