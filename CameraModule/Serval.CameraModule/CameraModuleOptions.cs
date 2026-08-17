using Serval.Ai;

namespace Serval.CameraModule;

/// <summary>
/// This worker's configuration.
///
/// Most of it is the shared detection library's own option types, bound at the same config paths
/// they always used — <c>CameraModule:Vad</c>, <c>CameraModule:Asr</c> and so on — so moving the
/// detection code into a library the Server also uses changed no deployed setting. What remains
/// here is what only an edge module has: a microphone, a V4L2 camera, and a durable outbox.
/// </summary>
public sealed class CameraModuleOptions
{
    public const string SectionName = "CameraModule";

    /// <summary>Microphone capture. Module-only: the Server gets audio from ffmpeg, not PortAudio.</summary>
    public AudioOptions Audio { get; set; } = new();

    /// <summary>V4L2 camera capture. Separate from <see cref="Vision"/>, which is model settings only.</summary>
    public CaptureOptions Capture { get; set; } = new();

    public VadOptions Vad { get; set; } = new();
    public AudioGateOptions AudioGate { get; set; } = new();
    public AsrOptions Asr { get; set; } = new();
    public VisionOptions Vision { get; set; } = new();
    public MotionOptions Motion { get; set; } = new();
    public SpeakerOptions Speaker { get; set; } = new();
    public SoundOptions Sound { get; set; } = new();
    public OutputOptions Output { get; set; } = new();

    /// <summary>
    /// Settings for the <c>--detect</c> diagnostic only. The module itself does not run object
    /// detection — it gates vision on <see cref="Motion"/> — and this is here because calibrating
    /// a camera happens on the camera, next to <c>--motion</c>, whichever host will eventually do
    /// the detecting.
    /// </summary>
    public DetectionOptions Detection { get; set; } = new();
}

/// <summary>Microphone capture, up to the ring buffer the shared VAD path reads from.</summary>
public sealed class AudioOptions
{
    public int SampleRate { get; set; } = 16000;

    /// <summary>Frames per PortAudio callback. Kept equal to <see cref="VadOptions.WindowSize"/>.</summary>
    public int FramesPerBuffer { get; set; } = 512;

    /// <summary>
    /// Case-insensitive substring of the input device name. Null selects PortAudio's
    /// default input. Prefer setting this explicitly: device order is not stable across
    /// reboots, and picking the wrong device is indistinguishable from a dead mic.
    /// </summary>
    public string? DeviceName { get; set; }

    /// <summary>
    /// Linear gain applied to captured audio. Leave at 1.0 unless the startup RMS probe
    /// shows the input is genuinely quiet, then calibrate. This replaces a hardcoded 50x
    /// multiplier that hard-clipped the signal.
    ///
    /// Note the RMS gate threshold is measured after this, so changing one means re-checking
    /// the other.
    /// </summary>
    public float InputGain { get; set; } = 1.0f;

    /// <summary>Ring buffer depth between the realtime callback and the VAD thread.</summary>
    public int RingBufferSeconds { get; set; } = 4;
}

/// <summary>Where frames come from on the edge. The Server has its own answer and ignores this.</summary>
public sealed class CaptureOptions
{
    public string DevicePath { get; set; } = "/dev/video0";

    /// <summary>
    /// The camera must support MJPEG at this size; it is rejected loudly if not.
    ///
    /// 640x480 is deliberate: image resolution dominates inference cost. Measured on a
    /// 7950X3D, 1280x720 took 15.5s versus 5.0s at 640x480 — 3x for modestly more detail
    /// ("a drink and a can on a table beside him" vs just the subject). On the Pi that
    /// difference is the gap between usable and not.
    /// </summary>
    public uint Width { get; set; } = 640;

    public uint Height { get; set; } = 480;

    /// <summary>
    /// How often to grab a frame. This is also the spacing between the frames a multi-frame
    /// description compares, so it sets what counts as "movement" — too fast and nothing has
    /// changed between them, too slow and the model sees two unrelated scenes.
    /// </summary>
    public double CaptureIntervalSeconds { get; set; } = 2.0;
}

public sealed class OutputOptions
{
    public string DatabasePath { get; set; } = "data/telemetry.db";
    public string JsonlPath { get; set; } = "data/telemetry.jsonl";
    public double SyncIntervalSeconds { get; set; } = 10.0;

    /// <summary>
    /// Base URL of the Serval server. When set, telemetry is delivered over HTTP to
    /// <c>{ServerUrl}/api/cameras/{CameraId}/telemetry</c> instead of the local JSONL file, and
    /// <see cref="CameraId"/> becomes required — the server keys everything by camera and the
    /// module has no identity of its own. Null keeps the file sink (the default, offline mode).
    /// </summary>
    public string? ServerUrl { get; set; }

    /// <summary>
    /// This module's camera id on the server. Must match a camera registered there. Required
    /// whenever <see cref="ServerUrl"/> is set; ignored otherwise.
    /// </summary>
    public string? CameraId { get; set; }

    /// <summary>Shared secret sent as X-Api-Key when delivering over HTTP. Match the server's ApiKey.</summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Bounded queue between speech detection and inference. On overflow the oldest
    /// pending utterance is dropped and counted, so an 8 GB Pi degrades predictably
    /// instead of growing an unbounded backlog.
    /// </summary>
    public int QueueCapacity { get; set; } = 32;

    /// <summary>Delete rows once delivered. Enable on the Pi so storage cannot fill.</summary>
    public bool DeleteAfterSync { get; set; }
}
