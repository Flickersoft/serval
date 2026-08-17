using Microsoft.Extensions.Logging.Abstractions;

namespace Serval.Ai.Tests;

/// <summary>
/// Our transcription against the words SenseVoice is <em>published</em> to produce.
///
/// <para><b>Why this is worth a test of its own.</b> Every other check here asks whether the
/// pipeline produced something well-formed. None of them asks whether it produced the right words,
/// so all of them pass on a model that has been swapped, half-downloaded, or paired with the wrong
/// <c>tokens.txt</c> — the last of which yields fluent, confident, entirely wrong text. The model
/// ships its own test clip and sherpa-onnx documents exactly what that clip should decode to, which
/// makes this the one place a transcript can be checked against an outside authority rather than
/// against our own output from last week.</para>
///
/// <para><b>Inverse text normalization is not free.</b> The two strings below differ by more than
/// formatting: with ITN on, the int8 model renders "gold" as <c>code</c>. Both are upstream's own
/// documented output, so this is not our defect and not something to fix here — but it is the
/// clearest available evidence that ITN can change a word rather than merely its spelling. It buys
/// "2026" for "twenty twenty six" and occasionally costs a noun. Left on, because dates and times
/// are most of what a camera overhears worth reading back; recorded here so the trade is a decision
/// rather than a surprise.</para>
///
/// <para>Skipped unless <c>SERVAL_MODELS</c> points at the weights, like the rest of the
/// model-dependent suite.</para>
/// </summary>
public class SenseVoiceReferenceTests
{
    /// <summary>
    /// sherpa-onnx's documented decode of <c>test_wavs/en.wav</c> for <c>model.int8.onnx</c>, with
    /// and without <c>--sense-voice-use-itn</c>.
    /// https://k2-fsa.github.io/sherpa/onnx/sense-voice/pretrained.html
    /// </summary>
    private const string WithItn =
        "The tribal chieftain called for the boy and presented him with 50 pieces of code.";

    private const string WithoutItn =
        "the tribal chieftain called for the boy and presented him with fifty pieces of gold";

    [Theory]
    [InlineData(true, WithItn)]
    [InlineData(false, WithoutItn)]
    public void The_reference_clip_decodes_to_the_published_text(bool itn, string expected)
    {
        string models = ModelRoot();
        string directory = Path.Combine(
            models, "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17");

        var asr = new AsrOptions
        {
            ModelPath = Path.Combine(directory, "model.int8.onnx"),
            TokensPath = Path.Combine(directory, "tokens.txt"),

            // Pinned rather than inherited from the shipped default: this asserts a published
            // string for one model in one language, and would start failing for an unrelated
            // reason the day either default moves.
            Language = "en",
            UseInverseTextNormalization = itn,
        };

        string clip = Path.Combine(directory, "test_wavs", "en.wav");

        foreach (string path in (string[])[asr.ModelPath, asr.TokensPath, clip])
        {
            if (!File.Exists(path))
            {
                Assert.Skip($"Not found: '{path}'. Run CameraModule/Serval.CameraModule/scripts/fetch-models.sh");
            }
        }

        (float[] samples, int sampleRate) = WavFile.Read(clip);

        using var analyzer = new SenseVoiceAnalyzer(asr, NullLogger<SenseVoiceAnalyzer>.Instance);
        SpeechAnalysis analysis = analyzer.Analyze(samples, sampleRate);

        Assert.Equal(expected, analysis.Text);
        Assert.Equal("en", analysis.Language);
    }

    private static string ModelRoot()
    {
        string? models = Environment.GetEnvironmentVariable("SERVAL_MODELS");

        if (string.IsNullOrWhiteSpace(models) || !Directory.Exists(models))
        {
            Assert.Skip("Set SERVAL_MODELS to a directory holding the ASR weights to run this.");
        }

        return models!;
    }
}
