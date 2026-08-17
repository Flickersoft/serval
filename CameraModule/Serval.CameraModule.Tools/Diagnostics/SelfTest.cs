using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SherpaOnnx;

using Serval.Ai;

namespace Serval.CameraModule;

/// <summary>
/// Decodes a WAV file through the real pipeline and prints the result. Run with:
///   dotnet run -- --selftest [path-to.wav]
///
/// Two jobs. First, it proves the models load and transcribe without needing a
/// microphone, which is the fastest way to validate an Orange Pi deployment. Second, it
/// checks SenseVoiceInterop against the official managed wrapper: if our struct mirror
/// has drifted from the native layout, the two texts diverge and this fails loudly.
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(
        CameraModuleOptions options,
        ILoggerFactory loggerFactory,
        bool runAudio,
        string? wavPath,
        IReadOnlyList<string> imagePaths)
    {
        int failures = 0;

        if (runAudio)
        {
            failures += RunAudio(options, loggerFactory, wavPath);
        }

        // Vision is opt-in and costs a 2.3GB model load, so only run it when asked.
        if (imagePaths.Count > 0)
        {
            if (runAudio)
            {
                Console.WriteLine();
            }

            failures += await RunVisionAsync(options, loggerFactory, imagePaths);
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "SELF-TEST PASSED" : $"SELF-TEST FAILED ({failures} check(s))");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Describes one or more JPEGs through the real model. Verifies a vision deployment with no
    /// camera. Pass two or more images to exercise the multi-frame path — the one that says what
    /// is <em>happening</em> rather than only what is there.
    /// </summary>
    private static async Task<int> RunVisionAsync(
        CameraModuleOptions options, ILoggerFactory loggerFactory, IReadOnlyList<string> imagePaths)
    {
        var frames = new List<VisionFrame>();
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;

        for (int i = 0; i < imagePaths.Count; i++)
        {
            string path = imagePaths[i];
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"FAIL: no such image: {path}");
                return 1;
            }

            byte[] jpeg = File.ReadAllBytes(path);
            Console.WriteLine($"Image      : {path} ({jpeg.Length / 1024:N0} KB)");

            if (!V4l2MjpegCamera.IsCompleteJpeg(jpeg))
            {
                Console.Error.WriteLine($"FAIL: {path} is not a complete JPEG (missing SOI/EOI).");
                return 1;
            }

            // Space the timestamps as the camera would, so the multi-frame prompt states a
            // realistic interval rather than claiming the frames were simultaneous.
            frames.Add(new VisionFrame(
                jpeg,
                capturedAt.AddSeconds((i - (imagePaths.Count - 1)) * options.Capture.CaptureIntervalSeconds)));
        }

        // Same backend selection as Program.cs: NPU (RKLLM/RKNN) when enabled, else CPU LLamaSharp.
        using IVisionInferenceRunner runner = options.Vision.UseNpu
            ? RkllmVisionRunner.Create(options.Vision, loggerFactory.CreateLogger<RkllmVisionRunner>())
            : await Qwen3VisionRunner.CreateAsync(
                options.Vision, loggerFactory.CreateLogger<Qwen3VisionRunner>());

        if (frames.Count > runner.MaxFrames)
        {
            Console.WriteLine(
                $"Note       : this backend takes {runner.MaxFrames} frame(s); using the most recent.");
            frames = frames.TakeLast(runner.MaxFrames).ToList();
        }

        string prompt = frames.Count < 2
            ? options.Vision.Prompt
            : options.Vision.MotionPrompt
                .Replace("{count}", frames.Count.ToString(), StringComparison.Ordinal)
                .Replace(
                    "{seconds}",
                    (frames[^1].CapturedAt - frames[0].CapturedAt).TotalSeconds.ToString("0.#"),
                    StringComparison.Ordinal);

        Console.WriteLine($"Prompt     : {prompt}");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        string description = await runner.RunInferenceAsync(frames, prompt, CancellationToken.None);
        stopwatch.Stop();

        Console.WriteLine($"Description: {description}");
        Console.WriteLine($"Elapsed    : {stopwatch.Elapsed.TotalSeconds:F1}s");

        int failures = 0;

        if (string.IsNullOrWhiteSpace(description))
        {
            Console.Error.WriteLine("FAIL: empty description.");
            failures++;
        }
        else if (description.Length < 10)
        {
            Console.Error.WriteLine($"FAIL: description is implausibly short: '{description}'");
            failures++;
        }
        else
        {
            Console.WriteLine("PASS: model produced a description.");
        }

        return failures;
    }

    private static int RunAudio(CameraModuleOptions options, ILoggerFactory loggerFactory, string? wavPath)
    {
        wavPath ??= Path.Combine(
            Path.GetDirectoryName(options.Asr.ModelPath) ?? ".", "test_wavs", "en.wav");

        if (!File.Exists(wavPath))
        {
            Console.Error.WriteLine($"FAIL: no such WAV file: {wavPath}");
            return 1;
        }

        (float[] samples, int sampleRate) = WavFile.Read(wavPath);
        Console.WriteLine($"Input : {wavPath}");
        Console.WriteLine($"Audio : {samples.Length / (double)sampleRate:F2}s @ {sampleRate} Hz");
        Console.WriteLine();

        var analyzer = new SenseVoiceAnalyzer(
            options.Asr, loggerFactory.CreateLogger<SenseVoiceAnalyzer>());

        SpeechAnalysis analysis = analyzer.Analyze(samples, sampleRate);

        Console.WriteLine($"Transcript : {analysis.Text}");
        Console.WriteLine($"Language   : {analysis.Language ?? "(undetermined)"}");
        Console.WriteLine($"Emotion    : {analysis.Emotion ?? "(undetermined)"}");
        Console.WriteLine($"Event      : {analysis.AudioEvent ?? "(undetermined)"}");
        Console.WriteLine($"RTF        : {analysis.RealTimeFactor:F3}");
        Console.WriteLine();

        int failures = 0;

        if (string.IsNullOrWhiteSpace(analysis.Text))
        {
            Console.Error.WriteLine("FAIL: transcript is empty.");
            failures++;
        }

        // The layout guard. Decode the same audio through the official wrapper and compare
        // the text our shim read from the same native struct.
        string officialText = DecodeWithOfficialWrapper(options, samples, sampleRate);
        if (!string.Equals(officialText.Trim(), analysis.Text, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"FAIL: interop text mismatch.\n  shim     : '{analysis.Text}'\n  official : '{officialText.Trim()}'\n" +
                "  SenseVoiceInterop's struct no longer matches the native layout for this " +
                "sherpa-onnx version. Re-check it against c-api.h.");
            failures++;
        }
        else
        {
            Console.WriteLine("PASS: interop text matches the official wrapper (struct layout is correct).");
        }

        if (analysis.Emotion is null)
        {
            Console.Error.WriteLine(
                "FAIL: no emotion label. Either the struct layout drifted or the label is unrecognised.");
            failures++;
        }
        else
        {
            Console.WriteLine($"PASS: emotion label '{analysis.Emotion}' is valid.");
        }

        analyzer.Dispose();
        return failures;
    }

    private static string DecodeWithOfficialWrapper(CameraModuleOptions options, float[] samples, int sampleRate)
    {
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.SenseVoice.Model = options.Asr.ModelPath;
        config.ModelConfig.SenseVoice.Language = options.Asr.Language;
        config.ModelConfig.SenseVoice.UseInverseTextNormalization = options.Asr.UseInverseTextNormalization ? 1 : 0;
        config.ModelConfig.Tokens = options.Asr.TokensPath;
        config.ModelConfig.NumThreads = options.Asr.NumThreads;
        config.ModelConfig.Provider = options.Asr.Provider;
        config.ModelConfig.Debug = 0;

        using var recognizer = new OfflineRecognizer(config);
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(sampleRate, samples);
        recognizer.Decode(stream);
        return stream.Result.Text;
    }
}
