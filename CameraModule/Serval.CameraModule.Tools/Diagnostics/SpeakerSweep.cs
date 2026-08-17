using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SherpaOnnx;

using Serval.Ai;

namespace Serval.CameraModule;

/// <summary>
/// Measures speaker labelling against a file, at a range of thresholds:
///   dotnet run -- --speakers models/speaker/fixtures/1-two-speakers-en.wav [expected]
///
/// The threshold is the one number that decides whether speaker labels are useful, and it
/// cannot be reasoned about — sherpa-onnx's own diarization example sidesteps it by
/// hardcoding a known speaker count, which we never have. So measure it: run the known-count
/// fixtures and see which threshold recovers the right answer.
///
/// It also compares the two halves directly. If the offline pass is no better than the live
/// labels on a file where speakers overlap, the offline half is not earning its keep.
/// </summary>
public static class SpeakerSweep
{
    /// <summary>
    /// Reaches past 1.0 on purpose. <c>Clustering.Threshold</c> is a cosine <em>distance</em>,
    /// running 0-2, not a similarity capped at 1 — and a real room needs the upper half: a
    /// four-person meeting lands near 1.0 and reports ten speakers anywhere below it. A sweep that
    /// cannot reach the value it is looking for reports a confident table and a wrong conclusion.
    /// </summary>
    private static readonly float[] SweepPoints =
        [0.4f, 0.5f, 0.6f, 0.675f, 0.75f, 0.8f, 0.9f, 1.0f, 1.1f, 1.2f, 1.4f];

    public static int Run(CameraModuleOptions options, ILoggerFactory loggerFactory, string? wavPath, int? expected)
    {
        wavPath ??= "models/speaker/fixtures/1-two-speakers-en.wav";

        if (!File.Exists(wavPath))
        {
            Console.Error.WriteLine($"FAIL: no such WAV: {wavPath}");
            return 1;
        }

        var speaker = options.Speaker;
        foreach (string path in (string[])[speaker.EmbeddingModelPath, speaker.SegmentationModelPath])
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"FAIL: model not found: {path}\nRun ./scripts/fetch-models.sh");
                return 1;
            }
        }

        (float[] samples, int sampleRate) = WavFile.Read(wavPath);
        Console.WriteLine($"Input    : {wavPath}");
        Console.WriteLine($"Audio    : {samples.Length / (double)sampleRate:F1}s @ {sampleRate} Hz");
        if (expected is { } n)
        {
            Console.WriteLine($"Expected : {n} speaker(s)");
        }

        Console.WriteLine();

        // Cut the file into utterances exactly as the live pipeline would, so the live numbers
        // reflect the real VAD behaviour rather than an idealised segmentation.
        List<float[]> utterances = SplitIntoUtterances(options, loggerFactory, samples);
        Console.WriteLine($"VAD found {utterances.Count} utterance(s): "
            + string.Join(", ", utterances.Select(u => $"{u.Length / (double)sampleRate:F1}s")));
        Console.WriteLine();

        // Always include the configured thresholds, or the assertions below would silently
        // report zero whenever a tuned value is not one of the round sweep points.
        float[] thresholds = SweepPoints
            .Concat([speaker.LiveThreshold, speaker.ClusterThreshold])
            .Distinct()
            .OrderBy(t => t)
            .ToArray();

        Console.WriteLine("threshold |  live speakers | offline speakers");
        Console.WriteLine("----------|----------------|-----------------");

        int liveAtDefault = 0;
        int offlineAtDefault = 0;

        foreach (float threshold in thresholds)
        {
            int live = CountLive(speaker, utterances, sampleRate, threshold);
            int offline = CountOffline(speaker, samples, threshold);

            bool isLiveDefault = Math.Abs(threshold - speaker.LiveThreshold) < 0.0001f;
            bool isClusterDefault = Math.Abs(threshold - speaker.ClusterThreshold) < 0.0001f;

            string mark = expected is { } e && (live == e || offline == e) ? " <-" : "   ";
            string cfg = (isLiveDefault, isClusterDefault) switch
            {
                (true, true) => " (configured)",
                (true, false) => " (live default)",
                (false, true) => " (offline default)",
                _ => string.Empty,
            };

            Console.WriteLine($"  {threshold:F3}   |       {live,2}       |        {offline,2}      {mark}{cfg}");

            if (isLiveDefault)
            {
                liveAtDefault = live;
            }

            if (isClusterDefault)
            {
                offlineAtDefault = offline;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Configured: live={speaker.LiveThreshold:F2} -> {liveAtDefault} speaker(s), "
            + $"offline={speaker.ClusterThreshold:F2} -> {offlineAtDefault} speaker(s)");

        if (expected is null)
        {
            Console.WriteLine("\n(no expected count given; nothing asserted)");
            return 0;
        }

        int failures = 0;

        if (offlineAtDefault != expected)
        {
            Console.Error.WriteLine(
                $"FAIL: offline pass found {offlineAtDefault} speaker(s) at the configured "
                + $"threshold {speaker.ClusterThreshold:F2}, expected {expected}. "
                + "Pick a threshold from the table above.");
            failures++;
        }
        else
        {
            Console.WriteLine($"PASS: offline pass found exactly {expected} speaker(s).");
        }

        // The live half is expected to be worse — it labels whole VAD utterances, and the VAD
        // splits on silence, not on speaker change. Report it, don't fail on it.
        Console.WriteLine(liveAtDefault == expected
            ? $"PASS: live labels also found exactly {expected} speaker(s)."
            : $"NOTE: live labels found {liveAtDefault} (expected {expected}). Expected when a VAD "
              + "utterance holds more than one voice — this is what the offline pass exists to fix.");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "SPEAKER SWEEP PASSED" : $"SPEAKER SWEEP FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Splits the fixture the same way the live path does.
    ///
    /// It matters that this goes through the shared <see cref="SileroSpeechDetector"/> rather than
    /// configuring a VAD of its own: this sweep exists to measure the behaviour of the running
    /// system, and a second copy of the configuration could drift from the first without anything
    /// failing — leaving a diagnostic that confidently measures something nobody runs.
    /// </summary>
    private static List<float[]> SplitIntoUtterances(
        CameraModuleOptions options, ILoggerFactory loggerFactory, float[] samples)
    {
        using var detector = new SileroSpeechDetector(
            options.Vad, loggerFactory.CreateLogger("SpeakerSweep"));

        var result = new List<float[]>();
        int window = detector.WindowSize;

        for (int offset = 0; offset + window <= samples.Length; offset += window)
        {
            detector.Accept(samples.AsSpan(offset, window));
            while (detector.TryDequeue(out float[] utterance))
            {
                result.Add(utterance);
            }
        }

        return result;
    }

    private static int CountLive(
        SpeakerOptions options, List<float[]> utterances, int sampleRate, float threshold)
    {
        var config = new SpeakerEmbeddingExtractorConfig { Model = options.EmbeddingModelPath, Debug = 0 };
        using var extractor = new SpeakerEmbeddingExtractor(config);
        using var manager = new SpeakerEmbeddingManager(extractor.Dim);

        int next = 0;
        foreach (float[] utterance in utterances)
        {
            using var stream = extractor.CreateStream();
            stream.AcceptWaveform(sampleRate, utterance);
            stream.InputFinished();
            float[] embedding = extractor.Compute(stream);

            if (!string.IsNullOrEmpty(manager.Search(embedding, threshold)))
            {
                continue;
            }

            // Mirror the live rule: too short to trust, so it may match but never register.
            if (utterance.Length / (double)sampleRate < options.MinSecondsToRegister)
            {
                continue;
            }

            manager.Add($"speaker_{next}", embedding);
            next++;
        }

        return next;
    }

    private static int CountOffline(SpeakerOptions options, float[] samples, float threshold)
    {
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = options.SegmentationModelPath;
        config.Embedding.Model = options.EmbeddingModelPath;
        config.Clustering.NumClusters = -1;
        config.Clustering.Threshold = threshold;

        using var diarizer = new OfflineSpeakerDiarization(config);
        OfflineSpeakerDiarizationSegment[] segments = diarizer.Process(samples);

        return segments.Length == 0 ? 0 : segments.Select(s => s.Speaker).Distinct().Count();
    }
}
