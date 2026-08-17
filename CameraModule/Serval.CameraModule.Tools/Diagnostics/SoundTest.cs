using Microsoft.Extensions.Logging;
using Serval.Ai;

namespace Serval.CameraModule;

/// <summary>
/// Classifies WAV files through the real tagger and prints the full shortlist. Run with:
///   dotnet run -- --tag-sounds horn.wav dog.wav door.wav
///
/// This is the tuning instrument, not a pass/fail check, and it is the step to run before wiring
/// sound detection into anything. Every threshold in <see cref="SoundOptions"/> and every entry in
/// its alert list is a guess until it has been measured against recordings from the site it will
/// run at — a driveway, a hallway and a garden disagree about what 0.4 confidence means.
///
/// So it prints scores rather than judging them: the shortlist with what the policy would decide
/// beside it, so a threshold can be read off directly.
/// </summary>
public static class SoundTest
{
    public static int Run(
        CameraModuleOptions options, ILoggerFactory loggerFactory, IReadOnlyList<string> wavPaths)
    {
        if (wavPaths.Count == 0)
        {
            Console.Error.WriteLine("Usage: --tag-sounds <wav> [wav...]");
            return 1;
        }

        SoundOptions sound = options.Sound;

        Console.WriteLine($"Model      : {sound.ModelPath}");
        Console.WriteLine($"Labels     : {sound.LabelsPath}");
        Console.WriteLine(
            $"Thresholds : {sound.MinConfidence:F2} ordinary, {sound.AlertMinConfidence:F2} alert");
        Console.WriteLine();

        using var tagger = new SoundEventTagger(sound, loggerFactory.CreateLogger<SoundEventTagger>());

        int failures = 0;

        foreach (string path in wavPaths)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"FAIL: no such WAV file: {path}");
                failures++;
                continue;
            }

            (float[] samples, int sampleRate) = WavFile.Read(path);
            double seconds = samples.Length / (double)sampleRate;

            long startedAt = Environment.TickCount64;
            IReadOnlyList<ScoredSound> shortlist = tagger.Tag(samples, sampleRate);
            double elapsed = (Environment.TickCount64 - startedAt) / 1000.0;

            Console.WriteLine($"{path}  ({seconds:F2}s @ {sampleRate} Hz, tagged in {elapsed:F3}s)");

            if (shortlist.Count == 0)
            {
                Console.Error.WriteLine("  FAIL: the tagger returned nothing.");
                failures++;
                Console.WriteLine();
                continue;
            }

            // A fresh policy per file: the cooldown is stateful, and two clips of the same sound
            // are exactly what someone tuning this will pass in one go. Sharing it would silently
            // suppress the second and read as a detection failure.
            var policy = new SoundEventPolicy(sound);
            SoundVerdict? verdict = policy.Decide(shortlist, DateTimeOffset.UtcNow);

            foreach (ScoredSound scored in shortlist)
            {
                bool wins = verdict is not null && scored.Label == verdict.Label;
                Console.WriteLine($"  {(wins ? "->" : "  ")} {scored.Confidence:F3}  {scored.Label}");
            }

            if (verdict is null)
            {
                // Not a failure. Most audio should produce nothing — that is the thresholds
                // working — but which of the three reasons applied is the useful part.
                string why = policy.SuppressedByLabel > 0 && policy.BelowThreshold == 0
                    ? "every candidate was in IgnoredLabels"
                    : "below the confidence floor";

                Console.WriteLine($"     (no record: {why})");
            }
            else if (verdict.IsAlert)
            {
                Console.WriteLine($"     ALERT: {verdict.Label}");
            }

            Console.WriteLine();
        }

        if (failures > 0)
        {
            Console.Error.WriteLine($"{failures} file(s) failed to classify.");
        }

        return failures == 0 ? 0 : 1;
    }
}
