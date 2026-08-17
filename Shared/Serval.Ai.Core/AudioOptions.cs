namespace Serval.Ai;

public sealed record VadOptions
{
    public string ModelPath { get; set; } = "models/silero_vad.onnx";
    public float Threshold { get; set; } = 0.5f;
    public float MinSilenceSeconds { get; set; } = 0.7f;
    public float MinSpeechSeconds { get; set; } = 0.25f;

    /// <summary>Hard ceiling on a single utterance; longer speech is cut and emitted.</summary>
    public float MaxSpeechSeconds { get; set; } = 15.0f;

    /// <summary>Must be 512 for Silero v5 at 16 kHz. The model rejects other sizes.</summary>
    public int WindowSize { get; set; } = 512;

    /// <summary>Sample rate every stage of the audio path runs at. Silero v5 and SenseVoice both want 16 kHz.</summary>
    public int SampleRate { get; set; } = 16000;

}

public sealed class AsrOptions
{
    public string ModelPath { get; set; } =
        "models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/model.int8.onnx";

    public string TokensPath { get; set; } =
        "models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/tokens.txt";

    /// <summary>
    /// One of: auto, zh, en, ja, ko, yue. <b>Pin it.</b>
    ///
    /// <c>auto</c> re-decides per utterance, and an utterance is a few seconds of one voice in a
    /// room — far too little to identify a language from, so it misfires on exactly the short,
    /// quiet, half-heard speech a camera spends its life recording. Measured over a 90-second
    /// English meeting it returned a line of Mandarin, and pinning <c>en</c> improved nearly every
    /// other line as well: "Are you al read" became "Are you all read", "I du't know" became
    /// "I don't know", "that F 11" became "that half 11". The mis-detected line became empty, which
    /// is the right answer — nothing said, rather than something nobody said.
    /// </summary>
    public string Language { get; set; } = "en";

    public bool UseInverseTextNormalization { get; set; } = true;

    /// <summary>4 suits the RK3588's A76 cluster and a desktop alike.</summary>
    public int NumThreads { get; set; } = 4;

    public string Provider { get; set; } = "cpu";
}

/// <summary>
/// Speaker labelling. Produces two independent, never-reconciled outputs joined downstream
/// by conversation id: a best-effort live label on each utterance, and an after-the-fact
/// diarization record covering a whole conversation.
/// </summary>
public sealed class SpeakerOptions
{
    /// <summary>Off by default; adds ~35 MB of models and work per utterance.</summary>
    public bool Enabled { get; set; }

    public string EmbeddingModelPath { get; set; } =
        "models/speaker/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx";

    public string SegmentationModelPath { get; set; } =
        "models/speaker/sherpa-onnx-pyannote-segmentation-3-0/model.onnx";

    /// <summary>
    /// Cosine similarity above which a live utterance is judged the same speaker as one
    /// already seen this conversation. Too low merges everyone into one speaker; too high
    /// splits one person across many — measured, 0.80 turns four speakers into seven.
    ///
    /// 0.6 is sherpa-onnx's speaker-identification default and measures as well as anything
    /// in 0.45-0.65 on the fixtures. Note the live half cannot be fixed by tuning: where a VAD
    /// utterance holds two voices it reports one speaker at *every* threshold. That is what
    /// the offline pass is for.
    /// </summary>
    public float LiveThreshold { get; set; } = 0.6f;

    /// <summary>
    /// Clustering distance threshold for the offline pass. Only needed because the speaker
    /// count is never known ahead of time — sherpa's own example sidesteps this by hardcoding
    /// NumClusters. Smaller means more clusters.
    ///
    /// <para><b>This is a cosine distance, so it runs 0-2, not 0-1.</b> Values above 1 are
    /// meaningful and are where a real room lands.</para>
    ///
    /// <para><b>There is no global value.</b> 0.675 recovers the three studio fixtures, which agree
    /// with each other rather than with anything a camera hears; a real four-person meeting needs
    /// ~1.0, at which point those fixtures collapse to one speaker. The right threshold is a
    /// property of the recording, the same way <see cref="AudioGateOptions.RmsThreshold"/> is a
    /// property of the room. This stays at 0.675 for want of a better single number.
    /// <b>Measure your own rooms with `--speakers`</b>; see Docs/detection.md#tuning-the-models.</para>
    /// </summary>
    public float ClusterThreshold { get; set; } = 0.675f;

    /// <summary>
    /// Utterances shorter than this may match an existing speaker but never register a new
    /// one. Embeddings need ~1.5-3s to be meaningful, while the VAD emits from 0.25s — so
    /// without this every short "yeah" would mint a bogus speaker.
    /// </summary>
    public double MinSecondsToRegister { get; set; } = 1.5;

    /// <summary>Silence after which a conversation is finalised and its speaker history reset.</summary>
    public double SilenceTimeoutMinutes { get; set; } = 3.0;

    /// <summary>Caps a runaway session: finalise early and start a new conversation.</summary>
    public double MaxConversationMinutes { get; set; } = 30.0;

    /// <summary>
    /// Where conversation audio is buffered while a conversation is open. The files are deleted
    /// once the offline pass has consumed them; only crash-orphaned ones survive a restart.
    ///
    /// Pointing this at a tmpfs spares the SD card the churn — see the module's systemd unit —
    /// but gives up crash recovery, since the audio no longer survives a power cut.
    /// </summary>
    public string ConversationAudioDirectory { get; set; } = "data/conversations";

    /// <summary>
    /// Threads the offline pass's segmentation and embedding models may each use.
    ///
    /// Deliberately lower than <see cref="AsrOptions.NumThreads"/>. That work is on the critical
    /// path of a live utterance; this is a background burst over a whole conversation that can
    /// take as long as it likes, and capping it keeps cores free for VAD and ASR. Left at sherpa's
    /// default the burst would size itself to the machine and contend with exactly the real-time
    /// work it must not disturb.
    /// </summary>
    public int NumThreads { get; set; } = 2;

    /// <summary>
    /// How much of a live utterance the turn it best matches must account for before that turn
    /// inherits the utterance's whole transcript. Below this the audio is re-cut per turn and
    /// re-transcribed.
    ///
    /// <para><b>The question is asked about the winner, not the runner-up.</b> Only the winner's
    /// share can answer "is this utterance really one turn's worth of speech". A runner-up test —
    /// split when the second-best turn takes a fifth of the utterance and belongs to someone else —
    /// reads as the same check and is not: a seventeen-second utterance spanning five turns, best
    /// match 29.8% and runner-up 19.3%, passes it by seven tenths of a point and has all sixty of
    /// its words attributed to one 5.04-second turn, with the other four dropped for having no
    /// text.</para>
    ///
    /// <para>0.8 leaves diarization boundary jitter alone — an utterance almost entirely inside one
    /// turn keeps the transcript it already has, for free — while anything genuinely spread across
    /// turns gets cut where the speakers actually change.</para>
    /// </summary>
    public double ContainedOverlapFraction { get; set; } = 0.8;

    /// <summary>
    /// Shortest piece of a split utterance worth re-transcribing. Matches the VAD's own
    /// <c>MinSpeechSeconds</c> floor: below it ASR output is as likely to be noise as words, and a
    /// fabricated fragment attributed to the wrong speaker is worse than an absent one.
    /// </summary>
    public double MinSpeechSecondsForSplit { get; set; } = 0.25;
}
