using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Serval.Ai;

namespace Serval.CameraModule;

/// <summary>
/// Runs the object detector over a sequence of images and prints what it found. Run with:
///   dotnet run -- --detect a.jpg [b.jpg ...]
///
/// The counterpart to <see cref="MotionTest"/>, for the same reason: the thresholds that decide
/// whether the expensive model runs have to be chosen against real frames from the real camera,
/// and the alternative is guessing at why a camera reports everything or nothing.
///
/// Frames are treated as consecutive at one a second, so the episode column shows what the live
/// path would have done with them — which is the number that actually matters. A detection that
/// never survives <see cref="TrackingOptions.ConfirmSeconds"/> costs nothing downstream, and a run
/// of them is a description.
/// </summary>
public static class DetectTest
{
    public static int Run(
        DetectionOptions options,
        IReadOnlyList<string> imagePaths,
        ILoggerFactory loggerFactory)
    {
        if (imagePaths.Count == 0)
        {
            Console.Error.WriteLine("Usage: --detect <first.jpg> [more.jpg ...]");
            return 1;
        }

        Console.WriteLine($"Model      : {options.ModelPath}");
        Console.WriteLine($"Labels     : {options.LabelsPath}");
        Console.WriteLine($"Score      : >= {options.ScoreThreshold:0.00}");
        // Effective* rather than the raw arrays: those are empty when unset, so printing them
        // would show a blank list on a default configuration and read as "nothing is allowed".
        Console.WriteLine($"Classes    : {string.Join(", ", options.EffectiveClasses)}");
        Console.WriteLine($"Describe   : {string.Join(", ", options.EffectiveDescribeClasses)}");
        Console.WriteLine(
            $"Episode    : opens after {options.Tracking.ConfirmSeconds:0.#}s confirmed, "
            + $"coasts {options.Tracking.CoastSeconds:0.#}s, "
            + $"closes after {options.AbsenceSeconds:0.#}s absent");
        Console.WriteLine();

        // Through the factory so this diagnostic exercises whichever backend the host is configured
        // for. Comparing the same fixture image across backends is the check that catches a
        // transposed head, and it only works if --detect can reach both.
        IObjectDetector detector;
        try
        {
            detector = ObjectDetectorFactory.Create(options, loggerFactory);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: could not load the detector: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"Detector   : {detector.Description}");

        // Printed from the loaded model rather than from the setting, because the setting is only a
        // budget: a fixed-shape export ignores it entirely, and a dynamic one spends it differently
        // for every aspect it is shown. The shape here is the one a 16:9 picture resolves to, and
        // each image below reports its own.
        DetectorInput nominal = detector.InputFor(1920, 1080);
        Console.WriteLine($"Input      : {nominal.Width}x{nominal.Height} for 16:9");
        Console.WriteLine();

        using (detector)
        {
            // A synthetic one-second cadence. The files carry no timing of their own, and the
            // episode rules are all in seconds.
            //
            // One second is the hardest rate for the tracker: a walking subject covers about 1.4 m
            // between frames and consecutive boxes often do not overlap at all, so a folder of 1 fps
            // snapshots will fragment tracks in a way the server's detect stream does not. Read the
            // per-frame detections here, and take the episode counts as a floor.
            var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var tracker = new ObjectTracker(options.Tracking);
            var policy = new ObjectEventPolicy(options);
            List<double> elapsed = [];
            int framesWithObjects = 0;

            // Open episodes are reported every frame, since they are meant for drawing rather than
            // for reading. What this diagnostic is about is the moment one opens, so it announces
            // an id the first time it sees it and stays quiet about it afterwards.
            HashSet<string> announced = [];

            for (int i = 0; i < imagePaths.Count; i++)
            {
                string path = imagePaths[i];
                if (!File.Exists(path))
                {
                    Console.Error.WriteLine($"FAIL: no such image: {path}");
                    return 1;
                }

                DateTimeOffset now = clock.AddSeconds(i);
                IReadOnlyList<DetectedObject> found;
                long started = Stopwatch.GetTimestamp();
                try
                {
                    found = JpegFrames
                        .DetectAsync(detector, File.ReadAllBytes(path), CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"FAIL: could not detect on {path}: {ex.Message}");
                    return 1;
                }

                double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                elapsed.Add(elapsedMs);

                ObjectObservation observed = policy.Observe(tracker.Update(found, now), now);

                Console.WriteLine($"{Path.GetFileName(path)}  ({elapsedMs:0}ms)");

                if (found.Count == 0)
                {
                    Console.WriteLine("    (nothing above the score threshold)");
                }
                else
                {
                    framesWithObjects++;
                    foreach (DetectedObject d in found.OrderByDescending(d => d.Score))
                    {
                        Console.WriteLine(
                            $"    {d.Label,-14} {d.Score,5:0.00}   "
                            + $"x{d.Box.X,6:0.000} y{d.Box.Y,6:0.000} "
                            + $"w{d.Box.Width,6:0.000} h{d.Box.Height,6:0.000}");
                    }
                }

                foreach (ObjectEpisode episode in observed.Live)
                {
                    if (announced.Add(episode.Id))
                    {
                        Console.WriteLine(
                            $"    -> OPEN  {episode.Label} (peak {episode.PeakConfidence:0.00}"
                            + $"{(episode.IsArrival ? ", arrival" : ", already present")}"
                            + $"{(episode.IsAlert ? ", ALERT" : "")})");
                    }
                }

                foreach (ObjectEpisode episode in observed.Published)
                {
                    Console.WriteLine(
                        $"    -> CLOSE {episode.Label} after "
                        + $"{(episode.EndedAt!.Value - episode.StartedAt).TotalSeconds:0.#}s, "
                        + $"{episode.FrameCount} frame(s)");
                }

                foreach (DescriptionTrigger trigger in observed.Triggers)
                {
                    Console.WriteLine(
                        $"    -> DESCRIBE {trigger.Label} on {trigger.Reason} "
                        + $"({trigger.Confidence:0.00})");
                }
            }

            foreach (ObjectEpisode episode in policy.Finalise(clock.AddSeconds(imagePaths.Count)))
            {
                Console.WriteLine(
                    $"    -> CLOSE {episode.Label} at end of input, {episode.FrameCount} frame(s)");
            }

            Console.WriteLine();

            // The first inference includes warm-up, so the median is the number worth planning
            // capacity against. Cameras x MaxFps x this is the whole steady-state detection cost.
            if (elapsed.Count > 0)
            {
                List<double> sorted = [.. elapsed.Order()];
                Console.WriteLine(
                    $"Inference : {sorted[sorted.Count / 2]:0}ms median, {sorted[0]:0}ms best, "
                    + $"{sorted[^1]:0}ms worst over {sorted.Count} frame(s).");
            }

            Console.WriteLine(
                $"{framesWithObjects} of {imagePaths.Count} frame(s) had a detection; "
                + $"{policy.Opened} episode(s) opened, {policy.Closed} closed, "
                + $"{policy.Rejoined} rejoined.");
            Console.WriteLine(
                $"Suppressed: {tracker.Ghosts} unconfirmed track(s), "
                + $"{policy.SuppressedByClass} out-of-allowlist, {policy.BelowThreshold} below "
                + $"threshold, {policy.TooSmall} too small.");
        }

        // Always a success exit: this reports a measurement, it does not assert one. What the
        // numbers should be depends on the camera.
        return 0;
    }
}
