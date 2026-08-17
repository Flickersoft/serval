using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Serval.Contracts;
using SherpaOnnx;

namespace Serval.Ai;

/// <summary>
/// A live utterance as the reprocessor needs to see it: when the VAD emitted it, how long its
/// audio was, what was said, and how it sounded. The host supplies these from whatever it already
/// published.
/// </summary>
/// <param name="Emotion">
/// Carried through so a turn can keep the reading its own audio already produced. Null where the
/// analyzer could not say, which is the common case and stays absent rather than becoming neutral.
/// </param>
public sealed record LiveUtterance(
    DateTimeOffset EmittedAt,
    double DurationSeconds,
    string Text,
    string? Emotion = null);

/// <summary>Both records the offline pass produces for one conversation.</summary>
public sealed record ReprocessedConversation(
    DiarizationDocument Diarization,
    ConversationTranscriptDocument Transcript);

/// <summary>
/// The offline pass over a finished conversation. Runs once, when a conversation ends, over the
/// continuous WAV that <see cref="ConversationAudioBuffer"/> wrote.
///
/// It does two things, and deliberately not a third:
///
/// <b>Re-diarizes.</b> The live labeller judges each utterance against the handful it has already
/// seen. Pyannote here sees the whole exchange at once and clusters globally, so a turn that was
/// unlabelable in the moment gets resolved by comparison against every other turn. Context is a
/// real gain for this half.
///
/// <b>Re-attributes.</b> The transcripts are then mapped onto the corrected turns. This is what
/// the live stream structurally cannot produce: the VAD splits on silence, not on speaker change,
/// so an utterance holding two voices gets one speaker label at *every* threshold — the words are
/// right and the attribution is wrong.
///
/// <b>It does not blanket re-transcribe.</b> SenseVoice is a non-autoregressive encoder with no
/// decoder conditioning on previously emitted text, so a longer window gives it no extra linguistic
/// context and it returns the same words for the same audio. ASR is re-run in exactly one case — an
/// utterance whose best-matching turn cannot account for it — where the audio has to be cut in a
/// place the live pass never cut it. Swapping the analyzer for an autoregressive,
/// context-conditioned model changes that calculus, and this is the place to revisit.
/// </summary>
public sealed class ConversationReprocessor : IDisposable
{
    private readonly SpeakerOptions _options;
    private readonly double _vadMinSilenceSeconds;
    private readonly SenseVoiceAnalyzer _analyzer;
    private readonly ILogger _logger;
    private readonly OfflineSpeakerDiarization _diarizer;

    public ConversationReprocessor(
        SpeakerOptions options,
        VadOptions vad,
        SenseVoiceAnalyzer analyzer,
        ILogger logger)
    {
        _options = options;
        _vadMinSilenceSeconds = vad.MinSilenceSeconds;
        _analyzer = analyzer;
        _logger = logger;

        if (!File.Exists(options.SegmentationModelPath))
        {
            throw new FileNotFoundException(
                $"Segmentation model not found at '{options.SegmentationModelPath}'. "
                + "Run ./scripts/fetch-models.sh", options.SegmentationModelPath);
        }

        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = options.SegmentationModelPath;
        config.Embedding.Model = options.EmbeddingModelPath;

        // Cap both models' threads. This pass is a background burst on a box whose real job is
        // real-time audio; unbounded it would size itself to the machine and take cores from the
        // VAD and ASR it runs alongside. Slower here is the right trade — nothing waits on it.
        config.Segmentation.NumThreads = options.NumThreads;
        config.Embedding.NumThreads = options.NumThreads;

        // -1 means "infer the count from the threshold". We never know how many people are in
        // the room, so the count cannot be fixed the way sherpa's own example does.
        config.Clustering.NumClusters = -1;
        config.Clustering.Threshold = options.ClusterThreshold;

        _diarizer = new OfflineSpeakerDiarization(config);

        _logger.LogInformation(
            "Conversation reprocessing ready (cluster threshold={Threshold}, sample rate={Rate}, "
            + "threads={Threads})",
            options.ClusterThreshold, _diarizer.SampleRate, options.NumThreads);
    }

    public int SampleRate => _diarizer.SampleRate;

    /// <summary>
    /// Diarizes and re-attributes one conversation. Returns null when the audio is missing or
    /// unusable — a conversation we cannot process yields no record rather than a hollow one.
    /// </summary>
    /// <param name="utterances">
    /// The live utterances already published for this conversation, in any order. An empty list is
    /// valid and yields a diarization record with an empty transcript: we know who spoke and when,
    /// and claiming words nobody transcribed would be fabrication.
    /// </param>
    public ReprocessedConversation? Process(
        FinishedConversation conversation,
        IReadOnlyList<LiveUtterance> utterances,
        string source)
    {
        if (!File.Exists(conversation.AudioPath))
        {
            _logger.LogWarning("Audio for conversation {Id} is missing.", conversation.ConversationId);
            return null;
        }

        (float[] samples, int sampleRate) = WavFile.Read(conversation.AudioPath);

        if (sampleRate != _diarizer.SampleRate)
        {
            _logger.LogError(
                "Conversation {Id} is {Actual} Hz but the diarizer expects {Expected} Hz.",
                conversation.ConversationId, sampleRate, _diarizer.SampleRate);
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        OfflineSpeakerDiarizationSegment[] segments = _diarizer.Process(samples);
        stopwatch.Stop();

        int speakerCount = segments.Length == 0 ? 0 : segments.Select(s => s.Speaker).Distinct().Count();

        _logger.LogInformation(
            "Diarized conversation {Id}: {Speakers} speaker(s), {Segments} segment(s) over {Audio:F1}s "
            + "(took {Elapsed:F1}s)",
            conversation.ConversationId, speakerCount, segments.Length,
            conversation.AudioSeconds, stopwatch.Elapsed.TotalSeconds);

        var diarization = new DiarizationDocument
        {
            ConversationId = conversation.ConversationId.ToString(),
            StartedAt = conversation.StartedAt,
            AudioSeconds = Math.Round(conversation.AudioSeconds, 2),
            SpeakerCount = speakerCount,
            Source = source,
            Segments = segments
                .Select(s => new DiarizationSegment
                {
                    Start = Math.Round(s.Start, 3),
                    End = Math.Round(s.End, 3),
                    Speaker = s.Speaker,
                })
                .ToList(),
        };

        (List<TranscriptTurn> turns, int retranscribed) =
            Attribute(segments, utterances, conversation.StartedAt, samples, sampleRate);

        var transcript = new ConversationTranscriptDocument
        {
            ConversationId = conversation.ConversationId.ToString(),
            StartedAt = conversation.StartedAt,
            AudioSeconds = Math.Round(conversation.AudioSeconds, 2),
            SpeakerCount = speakerCount,
            Turns = turns,
            Text = string.Join(" ", turns.Select(t => t.Text)),
            RetranscribedTurns = retranscribed,
            Source = source,
        };

        return new ReprocessedConversation(diarization, transcript);
    }

    /// <summary>
    /// Maps live transcripts onto diarized turns, re-transcribing only where an utterance spans a
    /// speaker change.
    /// </summary>
    private (List<TranscriptTurn> Turns, int Retranscribed) Attribute(
        OfflineSpeakerDiarizationSegment[] segments,
        IReadOnlyList<LiveUtterance> utterances,
        DateTimeOffset startedAt,
        float[] samples,
        int sampleRate)
    {
        // Text assigned to each diarized segment, by segment index.
        var texts = new List<string>[segments.Length];

        // Emotion readings offered to each segment, with how much audio each one speaks for. A
        // turn can gather several — one per utterance that overlapped it — and Winner picks
        // between them at the end rather than the last writer winning.
        var feelings = new List<EmotionClaim>[segments.Length];
        int retranscribed = 0;

        foreach (LiveUtterance utterance in utterances)
        {
            if (string.IsNullOrWhiteSpace(utterance.Text))
            {
                continue;
            }

            (double start, double end) = SpanOf(utterance, startedAt);
            if (end <= start)
            {
                continue;
            }

            // Which diarized turns this utterance's audio actually covers, and by how much.
            var overlaps = new List<(int Index, double Seconds)>();
            for (int i = 0; i < segments.Length; i++)
            {
                double seconds = Overlap(start, end, segments[i].Start, segments[i].End);
                if (seconds > 0)
                {
                    overlaps.Add((i, seconds));
                }
            }

            if (overlaps.Count == 0)
            {
                // Diarization found no speech where the VAD did. Nothing to attribute it to, and
                // inventing a turn for it would contradict the record we just published.
                _logger.LogDebug(
                    "Utterance at {Start:F2}s-{End:F2}s overlaps no diarized turn; leaving it unattributed.",
                    start, end);
                continue;
            }

            overlaps.Sort((a, b) => b.Seconds.CompareTo(a.Seconds));

            double duration = end - start;

            // Does the best-matching turn actually account for this utterance? Only then can it
            // inherit all of its words. Asking instead whether the *runner-up* was large enough
            // lets an utterance spread thinly over five turns land whole on the largest of them,
            // however small a share that is — see ContainedOverlapFraction.
            bool contained = overlaps[0].Seconds / duration >= _options.ContainedOverlapFraction;

            if (contained)
            {
                // The common case: the utterance sits inside one speaker's turn, so its existing
                // transcript is already correct and costs nothing to reuse. Its emotion comes with
                // it — one voice in the audio means the reading is about that speaker.
                Append(texts, overlaps[0].Index, utterance.Text);
                Claim(feelings, overlaps[0].Index, utterance.Emotion, overlaps[0].Seconds);
                continue;
            }

            // The utterance covers ground the winner cannot speak for. This is the case the live
            // pass gets wrong by construction — the VAD splits on silence, not on speaker change —
            // and the only one where cutting the audio differently yields new information, so it is
            // the only one we pay ASR for.
            //
            // The utterance's own emotion is deliberately *not* claimed here: it was measured over
            // audio holding more than one turn, so lending it to either is a reading of the other
            // one too. SplitAndTranscribe claims each piece's own instead, which is the whole reason
            // this belongs on this side of the wire.
            retranscribed += SplitAndTranscribe(
                texts, feelings, segments, overlaps, start, end, samples, sampleRate);
        }

        var turns = new List<TranscriptTurn>();
        for (int i = 0; i < segments.Length; i++)
        {
            if (texts[i] is not { Count: > 0 } parts)
            {
                continue;
            }

            turns.Add(new TranscriptTurn
            {
                Start = Math.Round(segments[i].Start, 3),
                End = Math.Round(segments[i].End, 3),
                Speaker = segments[i].Speaker,
                Text = string.Join(" ", parts),
                Emotion = WinningEmotion(feelings[i]),
            });
        }

        turns.Sort((a, b) => a.Start.CompareTo(b.Start));
        return (turns, retranscribed);
    }

    /// <summary>
    /// Re-runs ASR over each turn's share of a straddling utterance. Returns how many pieces were
    /// actually transcribed.
    ///
    /// The analyzer returns text and emotion from one forward pass, so cutting the audio per
    /// speaker buys a per-speaker reading of both at no extra cost — the piece each turn claims
    /// really is only that speaker.
    /// </summary>
    private int SplitAndTranscribe(
        List<string>[] texts,
        List<EmotionClaim>[] feelings,
        OfflineSpeakerDiarizationSegment[] segments,
        List<(int Index, double Seconds)> overlaps,
        double start,
        double end,
        float[] samples,
        int sampleRate)
    {
        int transcribed = 0;

        foreach ((int index, double seconds) in overlaps)
        {
            // Too short to transcribe reliably — below the VAD's own minimum speech duration, ASR
            // output is as likely to be noise as words.
            if (seconds < _options.MinSpeechSecondsForSplit)
            {
                continue;
            }

            double pieceStart = Math.Max(start, segments[index].Start);
            double pieceEnd = Math.Min(end, segments[index].End);

            int from = (int)Math.Round(pieceStart * sampleRate);
            int to = (int)Math.Round(pieceEnd * sampleRate);
            from = Math.Clamp(from, 0, samples.Length);
            to = Math.Clamp(to, from, samples.Length);

            if (to - from < sampleRate * _options.MinSpeechSecondsForSplit)
            {
                continue;
            }

            SpeechAnalysis analysis = _analyzer.Analyze(samples[from..to], sampleRate);
            transcribed++;

            if (!string.IsNullOrWhiteSpace(analysis.Text))
            {
                Append(texts, index, analysis.Text);
            }

            // Claimed on the piece's own length rather than on `seconds`: this reading covers the
            // audio actually handed to the analyzer, which is where the two differ if the piece was
            // clamped to the sample buffer.
            Claim(feelings, index, analysis.Emotion, (double)(to - from) / sampleRate);
        }

        return transcribed;
    }

    /// <summary>
    /// Where an utterance's audio sits within the conversation.
    ///
    /// The timestamp is when the VAD *emitted* the segment, which is after the speech itself plus
    /// the trailing silence the VAD waited through — the same lead
    /// <see cref="ConversationTracker.NoteUtterance"/> backdates by so that buffer position 0 lines
    /// up with the conversation start. Subtracting both is what makes the two streams align by
    /// arithmetic instead of by an offset table.
    /// </summary>
    private (double Start, double End) SpanOf(LiveUtterance utterance, DateTimeOffset startedAt)
    {
        double emitted = (utterance.EmittedAt - startedAt).TotalSeconds;
        double end = emitted - _vadMinSilenceSeconds;
        return (end - utterance.DurationSeconds, end);
    }

    private static double Overlap(double aStart, double aEnd, double bStart, double bEnd) =>
        Math.Max(0, Math.Min(aEnd, bEnd) - Math.Max(aStart, bStart));

    private static void Append(List<string>[] texts, int index, string text) =>
        (texts[index] ??= []).Add(text.Trim());

    /// <summary>
    /// Offers one emotion reading to a turn, with how much audio it speaks for. A reading of null
    /// is dropped rather than recorded: "the analyzer could not say" is not a candidate that could
    /// win, and letting it compete would let a long silence-ish stretch outvote a short clear one.
    /// </summary>
    private static void Claim(
        List<EmotionClaim>[] feelings, int index, string? emotion, double seconds)
    {
        if (string.IsNullOrWhiteSpace(emotion) || seconds <= 0)
        {
            return;
        }

        (feelings[index] ??= []).Add(new EmotionClaim(emotion, seconds));
    }

    /// <summary>
    /// Which of a turn's readings to keep: the one covering the most of its audio.
    ///
    /// A turn can gather several — the VAD cuts on silence and a turn often spans more than one
    /// utterance — and they can disagree. Longest wins as the reading with the most evidence behind
    /// it; a half-second interjection should not relabel a turn somebody spent ten seconds on. Ties
    /// go to the earlier claim, so the answer does not depend on iteration order.
    ///
    /// **Deliberately not a vote.** Three short claims of one feeling outnumbering one long claim of
    /// another would let the VAD's cutting decide the answer, and where the audio was cut is not
    /// evidence about how it sounded.
    ///
    /// Internal and pure, so it can be tested without a diarizer or an ASR model behind it.
    /// </summary>
    internal static string? WinningEmotion(IReadOnlyList<EmotionClaim>? claims)
    {
        if (claims is not { Count: > 0 })
        {
            return null;
        }

        EmotionClaim best = claims[0];
        foreach (EmotionClaim claim in claims)
        {
            if (claim.Seconds > best.Seconds)
            {
                best = claim;
            }
        }

        return best.Emotion;
    }

    public void Dispose() => _diarizer.Dispose();
}

/// <summary>
/// One emotion reading offered to a turn, and how many seconds of that turn's audio it was measured
/// over. <see cref="ConversationReprocessor.WinningEmotion"/> picks between them.
/// </summary>
public readonly record struct EmotionClaim(string Emotion, double Seconds);
