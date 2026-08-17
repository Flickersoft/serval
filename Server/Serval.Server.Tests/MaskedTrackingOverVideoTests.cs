using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Serval.Ai;

namespace Serval.Server.Tests;

/// <summary>
/// A mask decides on where an object <em>stands</em>, and this is the only place that claim is
/// checked against a real model over real frames.
///
/// <para><b>Why video and not boxes.</b> The unit tests hand <see cref="RegionPlanner"/> a track and
/// assert what it plans. That pins the geometry and nothing else: it cannot catch the planner
/// withholding the crop that would have produced the detection in the first place, because there is
/// no detector in the loop to withhold it from. The failure this guards is a subject that stops
/// being reported at all — invisible to every test that supplies its own detections.</para>
///
/// <para><b>The case that matters</b> is <c>walk-straddle</c>: a person whose box is four fifths
/// inside the mask and whose feet are below it. They must be tracked exactly as if the mask were not
/// there. Someone reading "ignore this area" reasonably expects overlap to be enough, and a change
/// that quietly made it enough would remove people from the record while looking like a tuning
/// improvement.</para>
///
/// <para>Runs the Server's own path — <see cref="RegionPlanner"/>, <see cref="FramePreparer"/>, the
/// ONNX detector, <see cref="ObjectTracker"/>, <see cref="ObjectEventPolicy"/>, in that order. That
/// ordering is the point, and it is the one thing <c>--replay-gates</c> cannot reproduce: it feeds
/// frames whole and never cuts a crop, so it exercises no planner at all.</para>
///
/// <para><b>Slow, and skipped unless the host can run it.</b> It needs ffmpeg, and it needs weights
/// that are not in the repository — point <c>SERVAL_DETECT_MODEL</c> at a model and
/// <c>SERVAL_DETECT_LABELS</c> at its label file. Frames are 1280x720 so region crops resolve
/// <em>on</em>; at the crops-off sizes the planner returns the whole frame every time and there is no
/// masking decision left to make.</para>
/// </summary>
public class MaskedTrackingOverVideoTests
{
    private const int FrameWidth = 1280;
    private const int FrameHeight = 720;
    private const double Fps = 2.0;

    /// <summary>Sprite height in frame pixels. The person is 64x240, so feet are the bottom edge.</summary>
    private const int SpriteHeight = 240;

    private static readonly DateTimeOffset Start =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The lower third of the view — a road past a drive.</summary>
    private static DetectionMask BottomBand() =>
        new() { Name = "road", Points = [0.0, 0.60, 1.0, 0.60, 1.0, 1.0, 0.0, 1.0] };

    /// <summary>A band across the middle of the view — a hedge, or a neighbour's frontage.</summary>
    private static DetectionMask UpperBand() =>
        new() { Name = "hedge", Points = [0.0, 0.20, 1.0, 0.20, 1.0, 0.72, 0.0, 0.72] };

    [Fact]
    public async Task A_person_whose_feet_are_below_the_mask_is_tracked_though_their_body_is_inside()
    {
        // Box spans y 0.447..0.78 against a mask covering 0.20..0.72: four fifths of the person is
        // inside it, including the centre of their box, and their feet are not. The whole rule.
        Outcome masked = await RunAsync(feetAt: 0.78, [UpperBand()]);
        Outcome plain = await RunAsync(feetAt: 0.78, []);

        Assert.Equal(plain.Episodes, masked.Episodes);
        Assert.True(masked.Episodes > 0, "the person was never detected at all, masked or not");
        Assert.Equal(0, masked.MaskedTracks);
        Assert.Equal(0, masked.SuppressedByMask);
    }

    [Fact]
    public async Task A_person_whose_feet_are_inside_the_mask_opens_no_episode()
    {
        Outcome masked = await RunAsync(feetAt: 0.90, [BottomBand()]);
        Outcome plain = await RunAsync(feetAt: 0.90, []);

        Assert.True(plain.Episodes > 0, "the person was never detected at all, masked or not");
        Assert.Equal(0, masked.Episodes);
        Assert.True(masked.MaskedTracks > 0, "the planner kept spending crops on a masked track");
    }

    [Fact]
    public async Task A_person_who_stops_inside_the_mask_stops_costing_inferences()
    {
        // The parked car on the road, which is what the retention crops cost. Once it stops moving,
        // motion proposes nothing and the track is the only thing still asking for a crop — so a
        // masked one should leave the floor pass and nothing else.
        Outcome masked = await RunAsync(feetAt: 0.90, [BottomBand()], stopsAfterSeconds: 4);
        Outcome plain = await RunAsync(feetAt: 0.90, [], stopsAfterSeconds: 4);

        Assert.True(
            masked.Inferences < plain.Inferences / 2,
            $"masked {masked.Inferences} inference(s) against {plain.Inferences} unmasked; the "
            + "retention crops on a masked track are not being withheld");

        Assert.Equal(0, masked.Episodes);
    }

    private sealed record Outcome(
        int Episodes, int Inferences, long MaskedTracks, long SuppressedByMask);

    /// <summary>
    /// Renders a clip of one person crossing the view at a fixed height, then runs it through the
    /// detect path and reports what came out.
    /// </summary>
    /// <param name="feetAt">Where the bottom of the person sits, as a fraction of frame height.</param>
    /// <param name="stopsAfterSeconds">When the person stops walking, or null to keep going.</param>
    private static async Task<Outcome> RunAsync(
        double feetAt, DetectionMask[] masks, double? stopsAfterSeconds = null)
    {
        if (!Ffmpeg.IsAvailable)
        {
            Assert.Skip("ffmpeg is not on PATH.");
        }

        string? modelPath = Environment.GetEnvironmentVariable("SERVAL_DETECT_MODEL");
        string? labelsPath = Environment.GetEnvironmentVariable("SERVAL_DETECT_LABELS");

        if (!File.Exists(modelPath) || !File.Exists(labelsPath))
        {
            Assert.Skip(
                "Set SERVAL_DETECT_MODEL and SERVAL_DETECT_LABELS to a detection model to run this.");
        }

        // SERVAL_DETECT_DEVICE so the identical end-to-end test can be pointed at an accelerator: on a
        // host with Corals, `tflite-edgetpu` plus a compiled model in SERVAL_DETECT_MODEL runs this whole
        // pipeline — planner, preparer, detector, tracker, policy — against the real device.
        string device = Environment.GetEnvironmentVariable("SERVAL_DETECT_DEVICE") ?? "onnx-cpu";

        var options = new DetectionOptions
        {
            Enabled = true,
            Device = device,
            ModelPath = modelPath!,
            LabelsPath = labelsPath!,
            Masks = masks,
        };

        using var scratch = new Scratch();
        IReadOnlyList<byte[]> frames = await RenderAsync(scratch.Path, feetAt, stopsAfterSeconds);

        using IObjectDetector detector = ObjectDetectorFactory.Create(
            options, NullLoggerFactory.Instance);

        DetectorInput input = detector.InputFor(FrameWidth, FrameHeight);
        bool cropping = options.Regions.ShouldCrop(FrameWidth, FrameHeight, input);
        Assert.True(
            cropping,
            $"a {FrameWidth}x{FrameHeight} frame into a {input.Width}x{input.Height} input resolved "
            + "crops off, so there is no planning decision left for a mask to affect");

        byte[] prepared = new byte[input.ByteLength];
        var motion = new FrameMotionDetector(new MotionOptions());
        var planner = new RegionPlanner(options.Regions, options.Masks);
        var tracker = new ObjectTracker(options.Tracking);
        var policy = new ObjectEventPolicy(options);

        var episodes = new HashSet<string>();
        int inferences = 0;

        for (int i = 0; i < frames.Count; i++)
        {
            byte[] frame = frames[i];
            DateTimeOffset now = Start.AddSeconds(i / Fps);

            FrameMotion scored = motion.Accept(
                frame.AsSpan(0, FrameWidth * FrameHeight), FrameWidth, FrameHeight);

            IReadOnlyList<PlannedRegion> planned = planner.Plan(
                now,
                FrameWidth,
                FrameHeight,
                cropping,
                scored.Result.Rejected ? default : scored.ChangedCells,
                motion.GridWidth,
                motion.GridHeight,
                [.. tracker.Live]);

            List<DetectedObject> found = [];

            foreach (PlannedRegion region in planned)
            {
                inferences++;

                PreparedFrame geometry = FramePreparer.Prepare(
                    frame, FrameWidth, FrameHeight, region.Region, input, prepared);

                found.AddRange(await detector.DetectPreparedAsync(
                    prepared, input, geometry, TestContext.Current.CancellationToken));
            }

            if (planned.Count == 0)
            {
                // Not "looked and saw nothing" — the policy must not be told, or an episode closes
                // on an observation nobody made.
                continue;
            }

            ObjectObservation observed = policy.Observe(tracker.Update(found, now), now);

            foreach (ObjectEpisode episode in observed.Live.Concat(observed.Published))
            {
                episodes.Add(episode.Id);
            }
        }

        return new Outcome(
            episodes.Count, inferences, planner.MaskedTracks, policy.SuppressedByMask);
    }

    /// <summary>
    /// Renders the clip and decodes it back with the <see cref="Ingest.DetectFrameReader"/> recipe,
    /// so the bytes reaching the detector are the ones the Server's ffmpeg side-output produces.
    /// </summary>
    private static async Task<IReadOnlyList<byte[]>> RenderAsync(
        string scratch, double feetAt, double? stopsAfterSeconds)
    {
        string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        string clip = Path.Combine(scratch, "clip.mp4");

        int top = (int)Math.Round((feetAt * FrameHeight) - SpriteHeight);

        // 50 px/s. Fast enough to be motion on the compare grid, slow enough that consecutive boxes
        // at 2 fps still overlap by more than TrackingOptions.MinIou — otherwise nothing associates
        // and the clip measures the tracker's frame rate rather than the mask.
        string x = stopsAfterSeconds is { } stop
            ? $"min(300+50*t,{(300 + (50 * stop)).ToString(CultureInfo.InvariantCulture)})"
            : "300+50*t";

        await RunFfmpegAsync(
            "-y", "-v", "error",
            "-loop", "1", "-i", Path.Combine(assets, "scene.png"),
            "-loop", "1", "-i", Path.Combine(assets, "person.png"),
            "-filter_complex",
            $"[1:v]scale=-1:{SpriteHeight}[p];[0:v][p]overlay=x='{x}':y={top}",
            "-t", "12", "-r", "15", "-c:v", "libx264", "-preset", "veryfast",
            "-pix_fmt", "yuv420p", "-g", "15", clip);

        string frameDir = Path.Combine(scratch, "frames");
        Directory.CreateDirectory(frameDir);

        await RunFfmpegAsync(
            "-y", "-v", "error", "-i", clip,
            "-vf",
            $"fps={Fps.ToString(CultureInfo.InvariantCulture)},"
            + $"scale={FrameWidth}:{FrameHeight}:flags=area",
            "-pix_fmt", "yuv420p", "-c:v", "rawvideo", "-f", "image2",
            Path.Combine(frameDir, "f-%05d.yuv"));

        int expected = FrameWidth * FrameHeight * 3 / 2;

        var frames = new List<byte[]>();

        foreach (string path in Directory.GetFiles(frameDir, "*.yuv")
            .OrderBy(static p => p, StringComparer.Ordinal))
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

            // A short read is a frame ffmpeg was still writing, which is the same thing
            // DetectFrameReader guards against by length.
            if (bytes.Length == expected)
            {
                frames.Add(bytes);
            }
        }

        Assert.NotEmpty(frames);
        return frames;
    }

    private static async Task RunFfmpegAsync(params string[] arguments)
    {
        var info = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info)!;
        string errors = await process.StandardError.ReadToEndAsync(
            TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(process.ExitCode == 0, $"ffmpeg failed: {errors}");
    }

    private sealed class Scratch : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"serval-mask-{Guid.NewGuid():N}");

        public Scratch() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a passing test over.
            }
        }
    }

    private static class Ffmpeg
    {
        public static bool IsAvailable { get; } = Probe();

        private static bool Probe()
        {
            try
            {
                using Process? process = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });

                if (process is null)
                {
                    return false;
                }

                process.WaitForExit(5000);
                return process.HasExited && process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
