using System.Text;
using Serval.Contracts;

namespace Serval.Ai.Tests;

/// <summary>
/// The one place a transcript and its speaker attribution are checked together, over audio whose
/// speaker count is known independently of anything this repository computes.
///
/// <para><b>Why it exists.</b> Every other audio test either supplies its own utterances or asserts
/// on synthetic samples, so all of them pass on a pipeline that transcribes nothing.
/// <c>--speakers</c> counts voices but never reads words; <see cref="SenseVoiceReferenceTests"/>
/// reads words from a single speaker. Between them sits the failure that actually gets reported:
/// the right words against the wrong person.</para>
///
/// <para><b>The reference recordings, and only those.</b> sherpa-onnx's own two-speaker clips,
/// published alongside the segmentation model and known to be within what these weights handle —
/// that is the entire selection criterion. Audio the models are not known to cope with produces
/// failures indistinguishable from ours, and a suite that cannot tell "we broke it" from "nothing
/// could do this" reports noise. Harder material belongs in a measurement.</para>
///
/// <para><b>Skipped unless the host can run it.</b> Point <c>SERVAL_MODELS</c> at a directory
/// holding the ASR, VAD and speaker weights — the layout <c>fetch-models.sh</c> produces — and the
/// fixtures beside them. With nothing set this suite still runs on a fresh clone, which is the rule
/// every other suite here keeps.</para>
/// </summary>
public class ConversationOverFixtureTests : IDisposable
{
    /// <summary>
    /// Fraction of turn audio one speaker may hold before the attribution is called degenerate.
    ///
    /// A correct <c>speaker_count</c> is not enough on its own: a diarizer that finds the right
    /// number of clusters and then attributes nearly all the words to one of them has produced a
    /// record that is wrong in the way people actually notice, while satisfying every count-based
    /// check.
    /// </summary>
    private const double DegenerateShare = 0.85;

    /// <summary>
    /// Generous ceiling on speaking rate. Conversational English runs about 2-3 words a second and
    /// fast speech about 4; this sits well above either, so tripping it means a turn has been given
    /// words from audio it does not cover rather than that someone talked quickly.
    /// </summary>
    private const double MaxWordsPerSecond = 6.0;

    /// <summary>
    /// The richer of the two reference clips — 34 seconds against 16, and more turns to render.
    /// Used wherever one recording has to stand for the rest.
    /// </summary>
    private const string Reference = "2-two-speakers-en.wav";

    private readonly string _scratch = Directory.CreateTempSubdirectory("serval-fixture-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
            // A leftover scratch directory is not worth failing a passing test over.
        }
    }

    [Theory]
    [InlineData("1-two-speakers-en.wav", 2)]
    [InlineData("2-two-speakers-en.wav", 2)]
    public void A_two_speaker_recording_is_transcribed_and_told_apart(string fixture, int expected)
    {
        FixtureRun run = Run(fixture);

        Report(fixture, run);

        Assert.NotNull(run.Transcript);
        Assert.Equal(expected, run.SpeakerCount);
        AssertTurnsAreWellFormed(run);
    }

    /// <summary>
    /// Both voices reach the record with words against them.
    ///
    /// Separate from the count above because they fail differently and mean different things: a
    /// wrong count is a clustering problem, whereas a speaker who is counted and then never quoted
    /// is a record that names two people and lets one of them say nothing. The feed draws exactly
    /// what is here, so a silent speaker is a silent bubble on screen.
    /// </summary>
    [Fact]
    public void Every_speaker_that_is_counted_also_says_something()
    {
        FixtureRun run = Run(Reference);
        Assert.NotNull(run.Transcript);

        var spoken = run.Turns
            .Where(t => !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => t.Speaker)
            .Distinct()
            .ToList();

        Assert.Equal(run.SpeakerCount, spoken.Count);
    }

    /// <summary>
    /// Writes the records this pipeline produced where the app's test suite can read them.
    ///
    /// The Flutter feed test needs documents a real pipeline emitted rather than ones hand-built to
    /// match what it expects — hand-built fixtures agree with the parser by construction, and agree
    /// with the pipeline only by luck. Opt-in, because it writes outside the scratch directory:
    /// <c>SERVAL_TRANSCRIPT_GOLDEN_OUT=/path/to/fixture.json</c>.
    /// </summary>
    [Fact]
    public void Capturing_the_golden_documents_for_the_app_suite()
    {
        string? destination = Environment.GetEnvironmentVariable("SERVAL_TRANSCRIPT_GOLDEN_OUT");
        if (string.IsNullOrWhiteSpace(destination))
        {
            Assert.Skip("Set SERVAL_TRANSCRIPT_GOLDEN_OUT to regenerate the app's feed fixture.");
        }

        FixtureRun run = Run(Reference);
        Assert.NotNull(run.Transcript);

        // The camera id and the conversation id are what join these records in the feed, so they
        // travel together exactly as the wire carries them.
        var documents = new List<IOutputRecord>();
        documents.AddRange(run.Utterances);
        documents.Add(run.Diarization!);
        documents.Add(run.Transcript!);

        // Element by element through TelemetryJson, not one Serialize of the list: handing the
        // generic overload a collection of the interface emits only the interface's members, and
        // every record collapses to its `type`. TelemetryJson.Serialize is the documented way past
        // that, and it takes one record at a time.
        string json = "[" + string.Join(",", documents.Select(TelemetryJson.Serialize)) + "]";

        // No BOM. Dart's jsonDecode treats a leading U+FEFF as content and fails on it, so the
        // default UTF8Encoding here — which writes one — would produce a fixture only .NET can read.
        File.WriteAllText(destination!, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Wrote {documents.Count} document(s) to {destination}");
    }

    /// <summary>
    /// Structural claims that hold whatever the models say. Anything phrased in terms of specific
    /// words would have to be rewritten the first time a model is swapped, and would be rewritten
    /// to match whatever the new one produced — which is not a test. The words themselves are
    /// pinned against an outside authority in <see cref="SenseVoiceReferenceTests"/>.
    /// </summary>
    private static void AssertTurnsAreWellFormed(FixtureRun run)
    {
        IReadOnlyList<TranscriptTurn> turns = run.Turns;

        Assert.NotEmpty(turns);
        Assert.All(turns, turn => Assert.False(
            string.IsNullOrWhiteSpace(turn.Text),
            "a turn was published with no words in it"));

        for (int i = 1; i < turns.Count; i++)
        {
            Assert.True(
                turns[i].Start >= turns[i - 1].Start,
                $"turn {i} starts at {turns[i].Start:F2}s, behind turn {i - 1} at "
                + $"{turns[i - 1].Start:F2}s; the feed renders these in order");
        }

        Assert.True(
            turns.Select(t => t.Speaker).Distinct().Count() > 1,
            "every turn was attributed to one speaker, so nothing was actually told apart");

        // Nobody says nine words a second. A turn carrying far more speech than its own duration
        // could hold has been given someone else's words — which is exactly how the attribution bug
        // in ContainedOverlapFraction presented: sixty words, spanning both speakers, stamped onto
        // one five-second turn while the four turns they belonged to were dropped for being empty.
        // The count is what makes that visible; the text alone looks like a long sentence.
        foreach (TranscriptTurn turn in turns)
        {
            double seconds = turn.End - turn.Start;
            int words = turn.Text.Split(
                (char[])[' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;

            Assert.True(
                words <= Math.Max(4, seconds * MaxWordsPerSecond),
                $"the turn at {turn.Start:F2}s is {seconds:F2}s long and carries {words} words "
                + $"({words / Math.Max(seconds, 0.01):F1}/s); it is holding speech from turns it "
                + "does not cover");
        }

        double total = turns.Sum(t => t.End - t.Start);
        (int speaker, double held) = turns
            .GroupBy(t => t.Speaker)
            .Select(g => (Speaker: g.Key, Held: g.Sum(t => t.End - t.Start)))
            .MaxBy(x => x.Held);

        Assert.True(
            held / total <= DegenerateShare,
            $"speaker {speaker} holds {held / total:P0} of the turn audio; the attribution has "
            + "collapsed even though the speaker count looks right");
    }

    private FixtureRun Run(string fixture) =>
        FixtureConversationPipeline.Run(FixturePath(fixture), Options());

    /// <summary>
    /// The shipped defaults, re-rooted at <c>SERVAL_MODELS</c>. Deliberately not a bespoke
    /// configuration: this measures what a deployment runs, so every threshold it depends on has to
    /// be the one <c>AudioOptions.cs</c> ships.
    /// </summary>
    private AiOptions Options()
    {
        string models = ModelRoot();

        var ai = new AiOptions();
        ai.Asr.ModelPath = Path.Combine(
            models, "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17", "model.int8.onnx");
        ai.Asr.TokensPath = Path.Combine(
            models, "sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17", "tokens.txt");
        ai.Vad.ModelPath = Path.Combine(models, "silero_vad.onnx");

        ai.Speaker.Enabled = true;
        ai.Speaker.EmbeddingModelPath = Path.Combine(
            models, "speaker", "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx");
        ai.Speaker.SegmentationModelPath = Path.Combine(
            models, "speaker", "sherpa-onnx-pyannote-segmentation-3-0", "model.onnx");
        ai.Speaker.ConversationAudioDirectory =
            Path.Combine(_scratch, Guid.NewGuid().ToString("N"));

        foreach (string path in (string[])
            [ai.Asr.ModelPath, ai.Asr.TokensPath, ai.Vad.ModelPath,
             ai.Speaker.EmbeddingModelPath, ai.Speaker.SegmentationModelPath])
        {
            if (!File.Exists(path))
            {
                Assert.Skip($"Model not found at '{path}'. Run scripts/fetch-models.sh");
            }
        }

        return ai;
    }

    private static string ModelRoot()
    {
        string? models = Environment.GetEnvironmentVariable("SERVAL_MODELS");

        if (string.IsNullOrWhiteSpace(models) || !Directory.Exists(models))
        {
            Assert.Skip(
                "Set SERVAL_MODELS to a directory holding the ASR, VAD and speaker weights "
                + "(the layout scripts/fetch-models.sh produces) to run these.");
        }

        return models!;
    }

    private static string FixturePath(string fixture)
    {
        string fixtures = Environment.GetEnvironmentVariable("SERVAL_SPEAKER_FIXTURES")
            is { Length: > 0 } explicitly
            ? explicitly
            : Path.Combine(ModelRoot(), "speaker", "fixtures");

        string path = Path.Combine(fixtures, fixture);

        if (!File.Exists(path))
        {
            Assert.Skip($"Fixture not found at '{path}'. Run CameraModule/Serval.CameraModule/scripts/fetch-models.sh");
        }

        return path;
    }

    private static void Report(string fixture, FixtureRun run)
    {
        var report = new StringBuilder();

        report.AppendLine($"=== {fixture}");
        report.AppendLine(
            $"  audio {run.AudioSeconds:F1}s in {run.ElapsedSeconds:F1}s "
            + $"(RTF {run.ElapsedSeconds / Math.Max(run.AudioSeconds, 0.001):F2})");
        report.AppendLine($"  RMS mean {run.MeanRms:F4}  peak {run.PeakRms:F4}");
        report.AppendLine(
            $"  live: {run.Utterances.Count} utterance(s), {run.LiveSpeakers} speaker label(s)");
        report.AppendLine(
            $"  offline: {run.SpeakerCount} speaker(s) over {run.Turns.Count} turn(s)");

        foreach (TranscriptTurn turn in run.Turns)
        {
            report.AppendLine(
                $"    [{turn.Start,6:F2}-{turn.End,6:F2}] speaker {turn.Speaker}: {turn.Text}");
        }

        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
    }
}
