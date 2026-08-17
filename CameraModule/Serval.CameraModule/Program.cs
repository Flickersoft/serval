using System.Threading.Channels;
using Serval.Ai;
using Serval.CameraModule;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// The calibration and bring-up diagnostics live in Serval.CameraModule.Tools — one binary beside
// this one, reading the same configuration section, so the deployed service carries none of them.
var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddOptions<CameraModuleOptions>()
    .Bind(builder.Configuration.GetSection(CameraModuleOptions.SectionName))
    // Camera capture settings have always lived under CameraModule:Vision alongside the model
    // settings. Vision moved into the shared library and capture did not, so the two are separate
    // types now — but they stay bound to the one section, because splitting it would have broken
    // every deployed config to no benefit.
    // One sample rate for the whole audio path. The mic setting is the authority here; the shared
    // VAD options carry their own copy because the Server's audio does not come from a microphone.
    .PostConfigure(o => o.Vad.SampleRate = o.Audio.SampleRate);

var options = builder.Configuration
    .GetSection(CameraModuleOptions.SectionName)
    .Get<CameraModuleOptions>() ?? new CameraModuleOptions();

// The shared detection components take their own slice of configuration rather than the whole
// options object, so each is registered against the section it actually reads.
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<CameraModuleOptions>>().Value.Vad);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<CameraModuleOptions>>().Value.Asr);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<CameraModuleOptions>>().Value.Vision);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<CameraModuleOptions>>().Value.Speaker);

// Ring buffer: realtime capture callback -> speech detection thread.
builder.Services.AddSingleton(new AudioRingBuffer(
    options.Audio.SampleRate * options.Audio.RingBufferSeconds));

// Bounded queue: speech detection -> inference. DropOldest keeps memory flat on an 8 GB
// board if inference ever falls behind; an unbounded queue would grow until it OOMs.
builder.Services.AddSingleton(Channel.CreateBounded<CapturedSpeech>(
    new BoundedChannelOptions(options.Output.QueueCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
    }));

builder.Services.AddSingleton<TelemetryRepository>();

// Deliver to the Serval server when one is configured, else keep writing JSONL locally. Either
// way the durable outbox sits in front, so switching sink never risks a record.
if (!string.IsNullOrWhiteSpace(options.Output.ServerUrl))
{
    builder.Services.AddHttpClient();
    builder.Services.AddSingleton<ITelemetrySink, HttpTelemetrySink>();
}
else
{
    builder.Services.AddSingleton<ITelemetrySink, FileTelemetrySink>();
}

// SenseVoice runs identically on linux-x64 and linux-arm64 (RK3588), so audio needs no
// per-architecture branch. Only vision will, once it exists.
builder.Services.AddSingleton<SenseVoiceAnalyzer>();

// --replay <wav> substitutes a file for the microphone. Everything downstream — ring
// buffer, VAD, inference, outbox — is the real path.
string? replayPath = args.SkipWhile(a => a != "--replay").Skip(1).FirstOrDefault();
if (replayPath is not null)
{
    builder.Services.AddSingleton(new ReplaySource(replayPath));
    builder.Services.AddHostedService<ReplayAudioWorker>();
}
else
{
    builder.Services.AddHostedService<AudioCaptureWorker>();
}

builder.Services.AddHostedService<SpeechDetectionWorker>();
builder.Services.AddHostedService<InferenceOrchestrator>();
builder.Services.AddHostedService<TelemetrySyncWorker>();

// Non-speech sound detection. Opt-in: a 26 MB model and a second onnxruntime session, which a
// deployment that only wants transcription should not pay for. It runs beside the VAD rather than
// behind it — Silero rejects everything that is not speech, so nothing here could reach that path.
if (options.Sound.Enabled)
{
    builder.Services.AddSingleton(sp =>
        sp.GetRequiredService<IOptions<CameraModuleOptions>>().Value.Sound);

    // Bounded with DropOldest for the same reason the utterance queue is: a segment describes a
    // sound that has already finished, so under load the newest is the one worth keeping.
    builder.Services.AddSingleton(Channel.CreateBounded<SoundSegment>(
        new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        }));

    builder.Services.AddSingleton(sp => new SoundEventSegmenter(
        sp.GetRequiredService<SoundOptions>(),
        options.Vad.WindowSize,
        options.Audio.SampleRate));

    // Loaded eagerly so a missing model or labels file fails at startup rather than silently
    // detecting nothing later, matching how the vision runner is treated below.
    builder.Services.AddSingleton(sp => new SoundEventTagger(
        sp.GetRequiredService<SoundOptions>(),
        sp.GetRequiredService<ILogger<SoundEventTagger>>()));

    builder.Services.AddHostedService<SoundTaggingWorker>();
}

// Speaker labelling produces two independent streams joined by conversation_id: a live
// best-effort label on each utterance, and an after-the-fact diarization record per
// conversation. They are never reconciled — downstream decides which to trust.
if (options.Speaker.Enabled)
{
    builder.Services.AddSingleton<SpeakerLabeller>();
    builder.Services.AddSingleton(sp => new ConversationTracker(
        sp.GetRequiredService<SpeakerOptions>(),
        sp.GetRequiredService<VadOptions>(),
        sp.GetRequiredService<ILogger<ConversationTracker>>(),
        sp.GetRequiredService<ILoggerFactory>()));
    builder.Services.AddHostedService<ConversationTimerWorker>();
    builder.Services.AddHostedService<DiarizationWorker>();
}

// Vision is opt-in: it costs a 2.3GB model and seconds of CPU per description. Audio runs
// identically whether or not this is enabled.
if (options.Vision.Enabled)
{
    // Sized to what a description can actually use — holding frames no runner will be shown
    // would just be memory.
    builder.Services.AddSingleton(new FrameRing(Math.Max(1, options.Vision.MaxFrames)));
    builder.Services.AddSingleton(_ => new SceneDescriptionService());

    // Loaded eagerly so a missing model or projector fails at startup rather than silently
    // producing no descriptions later. The NPU path (RKLLM/RKNN on the RK3588) is opt-in and
    // replaces the CPU LLamaSharp path where it is enabled — ~48s/image becomes a few seconds.
    // Note it describes a single frame; multi-frame movement inference is CPU-path only.
    if (options.Vision.UseNpu)
    {
        builder.Services.AddSingleton<IVisionInferenceRunner>(sp =>
            RkllmVisionRunner.Create(
                sp.GetRequiredService<VisionOptions>(),
                sp.GetRequiredService<ILogger<RkllmVisionRunner>>()));
    }
    else
    {
        builder.Services.AddSingleton<IVisionInferenceRunner>(sp =>
            Qwen3VisionRunner.CreateAsync(
                sp.GetRequiredService<VisionOptions>(),
                sp.GetRequiredService<ILogger<Qwen3VisionRunner>>()).GetAwaiter().GetResult());
    }

    builder.Services.AddHostedService<VisionCaptureWorker>();
    builder.Services.AddHostedService<VisionDescriptionWorker>();
}

var host = builder.Build();

host.Services.GetRequiredService<TelemetryRepository>().InitializeDatabase();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var resolved = host.Services.GetRequiredService<IOptions<CameraModuleOptions>>().Value;
logger.LogInformation(
    "camera-module starting on {Arch}. Vision {VisionState}.",
    System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture,
    resolved.Vision.Enabled ? "enabled" : "disabled");

host.Run();
return 0;
