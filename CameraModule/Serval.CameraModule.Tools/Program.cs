using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serval.CameraModule;

// The calibration and bring-up diagnostics, as one binary beside the worker. Every verb reads the
// same CameraModule configuration section the service binds — run these from the deployment
// directory and they measure exactly what the service would do with the same files.
var builder = Host.CreateApplicationBuilder(args);

var options = builder.Configuration
    .GetSection(CameraModuleOptions.SectionName)
    .Get<CameraModuleOptions>() ?? new CameraModuleOptions();

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole());

// Value following a flag, or null when the flag is absent or followed by another flag.
static string? ArgValue(string[] argv, string flag)
{
    string? next = argv.SkipWhile(a => a != flag).Skip(1).FirstOrDefault();
    return next is null || next.StartsWith("--", StringComparison.Ordinal) ? null : next;
}

// Every value following a flag, up to the next flag.
static string[] ArgValues(string[] argv, string flag) =>
    argv.SkipWhile(a => a != flag)
        .Skip(1)
        .TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal))
        .ToArray();

// Compare two images through the real motion gate and exit. No model is loaded, so this answers
// "would this scene have woken the VLM?" in milliseconds — the fastest way to calibrate a camera.
if (args.Contains("--motion"))
{
    return MotionTest.Run(options, ArgValues(args, "--motion"));
}

// Run the object detector over images and print what it found, plus which episodes it would have
// opened. The counterpart to --motion, and where the detection thresholds get set: run it against
// frames from the site before enabling anything.
if (args.Contains("--detect"))
{
    return DetectTest.Run(options.Detection, ArgValues(args, "--detect"), loggerFactory);
}

// Replay a directory of consecutive frames through both gates and report what each would have
// cost. This is how the object gate earns its place: the motion gate's traffic is mostly weather
// and light, and how much of it is real is a property of the site, not of any argument about it.
if (args.Contains("--replay-gates"))
{
    // How far apart the frames are, which decides everything the tracker does. Defaults to the
    // server's own detect rate rather than to one a second: at 1 fps a walking subject moves further
    // than its own width between frames, nothing associates, and the replay reports fragmentation
    // that the running system does not have.
    string[] fpsArg = ArgValues(args, "--fps");
    double replayFps = fpsArg.Length > 0
        && double.TryParse(fpsArg[0], CultureInfo.InvariantCulture, out double parsed)
        && parsed > 0
            ? parsed
            : 2.0;

    return GateReplayTest.Run(options, ArgValues(args, "--replay-gates"), replayFps, loggerFactory);
}

// Run the real models against files and exit. Verifies a deployment with no mic or camera:
//   --selftest [wav]                    audio only
//   --describe <jpeg> [jpeg...]         vision only; two or more exercises movement inference
//   --selftest --describe <jpeg>        both
if (args.Contains("--selftest") || args.Contains("--describe"))
{
    return await SelfTest.RunAsync(
        options,
        loggerFactory,
        runAudio: args.Contains("--selftest"),
        wavPath: ArgValue(args, "--selftest"),
        imagePaths: ArgValues(args, "--describe"));
}

// Classify WAV files through the real tagger and print the scored shortlist. This is where the
// sound thresholds get set: run it against recordings from the site before enabling anything.
if (args.Contains("--tag-sounds"))
{
    return SoundTest.Run(options, loggerFactory, ArgValues(args, "--tag-sounds"));
}

// Measure speaker labelling against a file at a range of thresholds.
if (args.Contains("--speakers"))
{
    string? expectedArg = ArgValue(args, "--expect");
    return SpeakerSweep.Run(
        options,
        loggerFactory,
        ArgValue(args, "--speakers"),
        expectedArg is null ? null : int.Parse(expectedArg));
}

// Grab one frame and write it. Verifies the V4L2 interop with no model involved.
if (args.Contains("--capture-test"))
{
    string outPath = args.SkipWhile(a => a != "--capture-test").Skip(1).FirstOrDefault() ?? "frame.jpg";
    return CaptureTest.Run(options, loggerFactory, outPath);
}

Console.Error.WriteLine(
    "camera-module-tools — Serval CameraModule diagnostics.\n\n"
    + "  --motion <before.jpg> <after.jpg>      compare two frames through the motion gate\n"
    + "  --detect <frame.jpg> [...]             run the object detector over frames\n"
    + "  --replay-gates <dir> [--fps N]         replay consecutive frames through both gates\n"
    + "  --selftest [clip.wav]                  run the audio models against a file\n"
    + "  --describe <frame.jpg> [...]           run the vision model against frames\n"
    + "  --tag-sounds <clip.wav> [...]          classify sounds and print the shortlist\n"
    + "  --speakers <clip.wav> [--expect N]     sweep the speaker-labelling thresholds\n"
    + "  --capture-test [out.jpg]               grab one V4L2 frame and write it\n\n"
    + "Each verb reads the CameraModule configuration section from appsettings.json and the\n"
    + "environment, exactly as the service does.");
return 2;
