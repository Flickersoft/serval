using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Serval.Ai;

namespace Serval.CameraModule;

/// <summary>
/// Replays a directory of consecutive frames through <em>both</em> gates and reports what each one
/// would have cost. Run with:
///   dotnet run -- --replay-gates /path/to/frames [more/frames ...] [--fps 2]
///
/// The measurement the object-detection gate rests on. Frames must be named so lexical order is
/// capture order — what <c>ffmpeg -vf fps=2 out-%05d.jpg</c> produces.
///
/// <para><b><c>--fps</c> must match the rate the frames were extracted at</b>, and defaults to the
/// server's <c>Ingest:DetectFps</c> rather than one a second. Every tracking decision is made from
/// the gap between frames, so telling the replay that 2 fps frames are a second apart makes a
/// walking subject appear to jump further than its own width: nothing associates, and the report
/// shows fragmentation the running system does not have.</para>
///
/// <para><b>What this does not cover.</b> Frames go to the detector whole, so region crops are not
/// exercised — the recall they buy on small distant subjects has to be measured against the server's
/// own path. What this measures is the policy: episode volume, description volume, and what the two
/// gates cost against each other.</para>
///
/// <para>Detections are cached beside the frame directory on the first run and reused afterwards,
/// which turns "what does <c>AbsenceSeconds</c> do to episode volume" from a ninety-second question
/// into an instant one. Delete the <c>detections-*.tsv</c> to force a fresh pass.</para>
///
/// <para>Frames where the motion gate fired and the object gate stayed silent are copied out to a
/// <c>misses-*</c> directory. Those are what the object gate gives up, and a reduction ratio cannot
/// speak to them — a gate that describes nothing scores perfectly and is worthless. There is no
/// ground truth here, so they have to be looked at.</para>
/// </summary>
public static class GateReplayTest
{
    private sealed record Frame(string Path, IReadOnlyList<DetectedObject> Detections);

    public static int Run(
        CameraModuleOptions options,
        IReadOnlyList<string> directories,
        double fps,
        ILoggerFactory loggerFactory)
    {
        if (directories.Count == 0)
        {
            Console.Error.WriteLine(
                "Usage: --replay-gates <frame-directory> [more-directories ...] [--fps 2]");
            return 1;
        }

        foreach (string directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                Console.Error.WriteLine($"FAIL: no such directory: {directory}");
                return 1;
            }

            if (!ReplayOne(options, directory, fps, loggerFactory))
            {
                return 1;
            }
        }

        return 0;
    }

    private static bool ReplayOne(
        CameraModuleOptions options,
        string directory,
        double fps,
        ILoggerFactory loggerFactory)
    {
        string trimmed = directory.TrimEnd(Path.DirectorySeparatorChar);
        string[] paths = [.. Directory.EnumerateFiles(trimmed, "*.jpg").Order(StringComparer.Ordinal)];

        if (paths.Length == 0)
        {
            Console.Error.WriteLine($"FAIL: no .jpg frames in {directory}");
            return false;
        }

        string name = Path.GetFileName(trimmed);
        string parent = Path.GetDirectoryName(trimmed) ?? ".";
        string cachePath = Path.Combine(parent, $"detections-{name}.tsv");

        Console.WriteLine($"=== {trimmed}  ({paths.Length} frames, assumed 1 fps) ===");

        List<Frame>? frames = LoadCache(cachePath, paths);

        if (frames is null)
        {
            Console.WriteLine("  (detecting — no usable cache)");
            frames = Detect(options, paths, loggerFactory);
            if (frames is null)
            {
                return false;
            }

            SaveCache(cachePath, frames);
        }
        else
        {
            Console.WriteLine($"  (detections from {Path.GetFileName(cachePath)})");
        }

        return Score(options, name, parent, frames, fps);
    }

    private static List<Frame>? Detect(
        CameraModuleOptions options,
        string[] paths,
        ILoggerFactory loggerFactory)
    {
        // Through the factory, because comparing gate decisions across backends over the same recorded
        // footage is how a shape or vocabulary trade gets measured rather than argued about.
        IObjectDetector detector;
        try
        {
            detector = ObjectDetectorFactory.Create(options.Detection, loggerFactory);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: could not load the detector: {ex.Message}");
            return null;
        }

        using (detector)
        {
            List<Frame> frames = [];
            List<double> elapsed = [];

            foreach (string path in paths)
            {
                long started = Stopwatch.GetTimestamp();
                IReadOnlyList<DetectedObject> found = JpegFrames
                    .DetectAsync(detector, File.ReadAllBytes(path), CancellationToken.None)
                    .GetAwaiter().GetResult();
                elapsed.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);

                frames.Add(new Frame(path, found));
            }

            List<double> sorted = [.. elapsed.Order()];
            Console.WriteLine(
                $"  Inference : {sorted[sorted.Count / 2]:0}ms median, {sorted[^1]:0}ms worst");

            return frames;
        }
    }

    /// <summary>
    /// Only used when the cache names exactly as many frames as are on disk. Anything else — a
    /// re-extraction, a different window — and it is ignored rather than scoring one run's
    /// detections against another run's pictures.
    /// </summary>
    private static List<Frame>? LoadCache(string cachePath, string[] paths)
    {
        if (!File.Exists(cachePath))
        {
            return null;
        }

        var byFrame = new Dictionary<int, List<DetectedObject>>();
        int declared = -1;

        foreach (string line in File.ReadLines(cachePath))
        {
            string[] parts = line.Split('\t');

            if (parts[0] == "#frames")
            {
                declared = int.Parse(parts[1], CultureInfo.InvariantCulture);
                continue;
            }

            if (parts.Length != 7)
            {
                continue;
            }

            int index = int.Parse(parts[0], CultureInfo.InvariantCulture);

            if (!byFrame.TryGetValue(index, out List<DetectedObject>? list))
            {
                list = [];
                byFrame[index] = list;
            }

            list.Add(new DetectedObject(
                parts[1],
                float.Parse(parts[2], CultureInfo.InvariantCulture),
                new BoundingBox(
                    float.Parse(parts[3], CultureInfo.InvariantCulture),
                    float.Parse(parts[4], CultureInfo.InvariantCulture),
                    float.Parse(parts[5], CultureInfo.InvariantCulture),
                    float.Parse(parts[6], CultureInfo.InvariantCulture))));
        }

        if (declared != paths.Length)
        {
            return null;
        }

        return [.. paths.Select((p, i) =>
            new Frame(p, byFrame.TryGetValue(i, out List<DetectedObject>? d) ? d : []))];
    }

    private static void SaveCache(string cachePath, List<Frame> frames)
    {
        using var writer = new StreamWriter(cachePath);
        writer.WriteLine($"#frames\t{frames.Count}");

        for (int i = 0; i < frames.Count; i++)
        {
            foreach (DetectedObject d in frames[i].Detections)
            {
                writer.WriteLine(string.Join('\t',
                    i.ToString(CultureInfo.InvariantCulture),
                    d.Label,
                    d.Score.ToString("R", CultureInfo.InvariantCulture),
                    d.Box.X.ToString("R", CultureInfo.InvariantCulture),
                    d.Box.Y.ToString("R", CultureInfo.InvariantCulture),
                    d.Box.Width.ToString("R", CultureInfo.InvariantCulture),
                    d.Box.Height.ToString("R", CultureInfo.InvariantCulture)));
            }
        }
    }

    private static bool Score(
        CameraModuleOptions options,
        string name,
        string parent,
        List<Frame> frames,
        double fps)
    {
        double step = 1.0 / Math.Max(fps, 0.01);

        var motion = new JpegMotionDetector(options.Motion);
        var tracker = new ObjectTracker(options.Detection.Tracking);
        var policy = new ObjectEventPolicy(options.Detection);
        var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        int motionFired = 0;
        int motionRejected = 0;
        var triggerReasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var triggerLabels = new Dictionary<string, int>(StringComparer.Ordinal);
        List<int> motionFrames = [];
        List<int> describedFrames = [];

        for (int i = 0; i < frames.Count; i++)
        {
            // The motion gate exactly as the live path runs it: the first frame only establishes
            // the reference, and every later frame is compared against the one before.
            MotionResult result = motion.Accept(File.ReadAllBytes(frames[i].Path));
            bool motionSaysDescribe = false;

            if (i > 0)
            {
                if (result.Rejected)
                {
                    motionRejected++;
                }
                else if (result.Moved)
                {
                    motionFired++;
                    motionSaysDescribe = true;
                    motionFrames.Add(i);
                }
            }

            DateTimeOffset now = clock.AddSeconds(i * step);
            ObjectObservation observed =
                policy.Observe(tracker.Update(frames[i].Detections, now), now);

            foreach (DescriptionTrigger trigger in observed.Triggers)
            {
                triggerReasons[trigger.Reason] = triggerReasons.GetValueOrDefault(trigger.Reason) + 1;
                triggerLabels[trigger.Label] = triggerLabels.GetValueOrDefault(trigger.Label) + 1;
            }

            if (observed.Triggers.Count > 0)
            {
                describedFrames.Add(i);
            }

            _ = motionSaysDescribe;
        }

        // A motion fire only counts as given up if nothing was described *near* it. Scoring this
        // frame-by-frame is the obvious thing and it is wrong: one person walking across the view
        // trips motion on twenty consecutive frames, and the object gate is supposed to describe
        // that once and stay quiet — counting the other nineteen as misses would condemn the gate
        // for behaving exactly as designed. The window is the describe cooldown, since that is the
        // span over which one event is deliberately reported once.
        // In frames, because it is compared against frame indices — so it has to be derived from the
        // rate rather than read straight off a setting in seconds.
        int window = (int)Math.Ceiling(options.Detection.DescribeCooldownSeconds * fps);
        List<string> missed = [.. motionFrames
            .Where(m => !describedFrames.Any(d => Math.Abs(d - m) <= window))
            .Select(m => frames[m].Path)];

        policy.Finalise(clock.AddSeconds(frames.Count * step));

        Console.WriteLine();
        Console.WriteLine($"  Motion gate    : {motionFired} description(s) requested");
        Console.WriteLine(
            $"                   {motionRejected} frame(s) rejected as whole-frame lighting change");
        Console.WriteLine($"  Object gate    : {policy.Described} description(s) requested");

        foreach ((string reason, int count) in triggerReasons.OrderByDescending(p => p.Value))
        {
            Console.WriteLine($"                     {count,4} on {reason}");
        }

        foreach ((string label, int count) in triggerLabels.OrderByDescending(p => p.Value))
        {
            Console.WriteLine($"                     {count,4} for {label}");
        }

        Console.WriteLine(
            $"  Recorded       : {policy.Opened} episode(s) opened ({policy.Arrivals} arrival(s)), "
            + $"{policy.Closed} closed, {policy.Rejoined} rejoined");

        if (policy.Described > 0)
        {
            Console.WriteLine(
                $"  Reduction      : {(double)motionFired / policy.Described:0.0}x fewer descriptions");
        }

        Console.WriteLine(
            $"  Suppressed     : {policy.SuppressedByMask} masked, "
            + $"{policy.SuppressedByDescribeClass} not worth describing, "
            + $"{policy.SuppressedByCooldown} on cooldown, "
            + $"{tracker.Ghosts} unconfirmed track(s)");

        // What this change gives up. The reduction ratio says nothing about these, so they get
        // written out to be looked at rather than summarised into a number.
        if (missed.Count > 0)
        {
            string missDir = Path.Combine(parent, $"misses-{name}");

            if (Directory.Exists(missDir))
            {
                Directory.Delete(missDir, recursive: true);
            }

            Directory.CreateDirectory(missDir);

            foreach (string path in missed)
            {
                File.Copy(path, Path.Combine(missDir, Path.GetFileName(path)), overwrite: true);
            }

            Console.WriteLine(
                $"  Given up       : {missed.Count} of {motionFired} motion fire(s) had no "
                + $"description within {window}s -> {missDir}");
        }

        Console.WriteLine();
        return true;
    }
}
