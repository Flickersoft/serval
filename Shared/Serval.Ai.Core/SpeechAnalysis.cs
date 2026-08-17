namespace Serval.Ai;

/// <summary>Result of analysing one utterance. Null fields mean "not determined" — never a guess.</summary>
public sealed record SpeechAnalysis(
    string Text,
    string? Language,
    string? Emotion,
    string? AudioEvent,
    double DurationSeconds,
    double RealTimeFactor);

/// <summary>One detected utterance, ready for analysis.</summary>
public sealed record CapturedSpeech(float[] Samples, int SampleRate, DateTimeOffset CapturedAt);
