using Microsoft.Extensions.Logging;
using SherpaOnnx;

namespace Serval.Ai;

/// <summary>
/// SenseVoice: transcription, speech emotion, and audio-event detection in a single
/// forward pass, in-process over sherpa-onnx's C API. Runs identically on linux-x64 and
/// linux-arm64 (RK3588), so there is no per-architecture branch here.
/// </summary>
public sealed class SenseVoiceAnalyzer : IDisposable
{
    // Taken from the model's own tokens.txt rather than documentation, so they cannot
    // drift from the weights we actually ship.
    internal static readonly HashSet<string> KnownEmotions =
    [
        "HAPPY", "SAD", "ANGRY", "NEUTRAL", "FEARFUL",
        "DISGUSTED", "SURPRISED", "EMO_UNKNOWN", "OTHER",
    ];

    internal static readonly HashSet<string> KnownEvents =
    [
        "Speech", "Speech_Noise", "BGM", "Applause", "Laughter",
        "Cry", "Sneeze", "Breath", "Cough", "Sing", "Event_UNK",
    ];

    private readonly OfflineRecognizer _recognizer;
    private readonly ILogger<SenseVoiceAnalyzer> _logger;

    public SenseVoiceAnalyzer(AsrOptions asr, ILogger<SenseVoiceAnalyzer> logger)
    {
        _logger = logger;

        // Fail loudly at startup rather than on the first utterance: a missing model is a
        // deployment error, and a capability either runs a real model or reports nothing.
        if (!File.Exists(asr.ModelPath))
        {
            throw new FileNotFoundException(
                $"SenseVoice model not found at '{asr.ModelPath}'. Run ./scripts/fetch-models.sh", asr.ModelPath);
        }

        if (!File.Exists(asr.TokensPath))
        {
            throw new FileNotFoundException(
                $"SenseVoice tokens not found at '{asr.TokensPath}'. Run ./scripts/fetch-models.sh", asr.TokensPath);
        }

        var config = new OfflineRecognizerConfig();
        config.ModelConfig.SenseVoice.Model = asr.ModelPath;
        config.ModelConfig.SenseVoice.Language = asr.Language;
        config.ModelConfig.SenseVoice.UseInverseTextNormalization = asr.UseInverseTextNormalization ? 1 : 0;
        config.ModelConfig.Tokens = asr.TokensPath;
        config.ModelConfig.NumThreads = asr.NumThreads;
        config.ModelConfig.Provider = asr.Provider;
        config.ModelConfig.Debug = 0;

        _recognizer = new OfflineRecognizer(config);

        _logger.LogInformation(
            "SenseVoice ready (language={Language}, threads={Threads}, provider={Provider})",
            asr.Language, asr.NumThreads, asr.Provider);
    }

    public SpeechAnalysis Analyze(float[] samples, int sampleRate)
    {
        double durationSeconds = (double)samples.Length / sampleRate;
        long startedAt = Environment.TickCount64;

        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(sampleRate, samples);
        _recognizer.Decode(stream);

        // Read via the shim rather than stream.Result: the managed wrapper drops the
        // emotion/language/event fields. See SenseVoiceInterop for why.
        SenseVoiceRaw raw = SenseVoiceInterop.Read(stream.Handle);

        double elapsedSeconds = (Environment.TickCount64 - startedAt) / 1000.0;
        double realTimeFactor = durationSeconds > 0 ? elapsedSeconds / durationSeconds : 0;

        return new SpeechAnalysis(
            Text: raw.Text.Trim(),
            Language: NormalizeTag(raw.Language, known: null, kind: "language", _logger),
            Emotion: NormalizeTag(raw.Emotion, KnownEmotions, "emotion", _logger),
            AudioEvent: NormalizeTag(raw.Event, KnownEvents, "event", _logger),
            DurationSeconds: durationSeconds,
            RealTimeFactor: realTimeFactor);
    }

    /// <summary>
    /// Turns "&lt;|HAPPY|&gt;" into "happy", and validates it against the labels the model
    /// can actually emit. A value outside that set means the native struct layout has
    /// shifted under us (see SenseVoiceInterop), so we surface null rather than write a
    /// plausible-looking wrong answer into telemetry.
    ///
    /// Static and internal so the no-fabricated-data guard can be unit-tested without loading
    /// the 1.1 GB model — see Serval.CameraModule.Tests.SenseVoiceLabelTests.
    /// </summary>
    internal static string? NormalizeTag(string? tag, HashSet<string>? known, string kind, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        string inner = tag.Trim();
        if (inner.StartsWith("<|", StringComparison.Ordinal) && inner.EndsWith("|>", StringComparison.Ordinal))
        {
            inner = inner[2..^2];
        }
        else
        {
            logger.LogWarning(
                "SenseVoice {Kind} label '{Tag}' is not in <|TAG|> form. The native result struct may have " +
                "changed; verify SenseVoiceInterop against c-api.h for the pinned sherpa-onnx version.",
                kind, tag);
            return null;
        }

        if (known is not null && !known.Contains(inner))
        {
            logger.LogWarning(
                "SenseVoice returned unknown {Kind} '{Tag}'. Treating as undetermined. This usually means the " +
                "pinned sherpa-onnx version changed its result struct layout.",
                kind, inner);
            return null;
        }

        return inner.ToLowerInvariant();
    }

    public void Dispose() => _recognizer.Dispose();
}
