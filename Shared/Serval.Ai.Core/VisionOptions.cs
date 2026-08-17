namespace Serval.Ai;

/// <summary>
/// The vision model and how it is prompted. Frame *capture* is not here: the edge module grabs
/// frames over V4L2 and the Server taps its snapshot fan-out, so acquisition is each host's own
/// business and only the inference settings are shared.
/// </summary>
public sealed class VisionOptions
{
    /// <summary>
    /// Off by default: the model is a 2.3 GB download and vision is optional. Audio is
    /// entirely unaffected either way.
    /// </summary>
    public bool Enabled { get; set; }

    public string ModelPath { get; set; } =
        "models/Qwen3-VL-2B-Instruct-GGUF/Qwen3-VL-2B-Instruct-Q8_0.gguf";

    /// <summary>The vision projector. Without it the model is text-only and cannot see.</summary>
    public string MmprojPath { get; set; } =
        "models/Qwen3-VL-2B-Instruct-GGUF/mmproj-Qwen3-VL-2B-Instruct-Q8_0.gguf";

    /// <summary>
    /// Use the RK3588 NPU (RKLLM/RKNN) instead of the CPU LLamaSharp path. On the Pi this takes
    /// a description from ~48s (CPU) to a few seconds. Requires the vendor rknpu driver, the
    /// RKLLM runtime libs, and the pre-converted models below — so it is off by default and the
    /// desktop keeps using the GGUF/LLamaSharp path.
    /// </summary>
    public bool UseNpu { get; set; }

    /// <summary>Pre-converted Qwen3-VL language model (w8a8) for the NPU. Used only when UseNpu.</summary>
    public string NpuLlmModelPath { get; set; } =
        "models/qwen3-vl-npu/qwen3-vl-2b-instruct_w8a8_rk3588.rkllm";

    /// <summary>Pre-converted Qwen3-VL vision encoder (fp16) for the NPU. Used only when UseNpu.</summary>
    public string NpuVisionModelPath { get; set; } =
        "models/qwen3-vl-npu/qwen3-vl-2b-vision_rk3588.rknn";

    /// <summary>Used when a single frame is described — no movement can be inferred from one image.</summary>
    public string Prompt { get; set; } = "Describe what you see in one sentence.";

    /// <summary>
    /// Used when two or more frames are described together. <c>{count}</c> and <c>{seconds}</c> are
    /// substituted with the number of frames and the span they cover, because "what changed" is
    /// only meaningful if the model knows how far apart the frames are.
    /// </summary>
    public string MotionPrompt { get; set; } =
        "These are {count} consecutive frames from one security camera, {seconds} seconds apart. " +
        "Describe the scene and what is happening or changing between them, in one or two sentences.";

    /// <summary>
    /// Used to describe a whole saved clip from frames sampled along it. <c>{count}</c> and
    /// <c>{seconds}</c> are substituted with the number of frames and the clip's length.
    ///
    /// Separate from <see cref="MotionPrompt"/> because the question is a different one. That
    /// prompt asks what changed between frames taken seconds apart; this asks for the arc of
    /// something minutes long, sampled sparsely — so it has to invite a summary rather than a
    /// comparison, and it must not encourage the model to narrate the gaps it cannot see.
    /// </summary>
    public string ClipPrompt { get; set; } =
        "These are {count} frames sampled evenly across a {seconds}-second security camera clip, " +
        "in order. Summarise what happens over the clip in one sentence. Describe only what is " +
        "visible in the frames.";

    /// <summary>
    /// How many recent frames to send in one description. 1 restores single-image behaviour.
    /// Every extra frame costs roughly another frame's worth of image tokens, so this trades
    /// latency for the ability to describe movement rather than a still.
    /// </summary>
    public int MaxFrames { get; set; } = 2;

    /// <summary>
    /// How many frames to sample across a saved clip for its summary. Higher than
    /// <see cref="MaxFrames"/> because this runs once per clip rather than continuously, so it is
    /// not competing with live description for the CPU — but still clamped by what the backend can
    /// accept, which on the RK3588 NPU path is exactly one.
    /// </summary>
    public int ClipFrames { get; set; } = 4;

    /// <summary>
    /// Kept at 4 to leave cores for the audio path. llama.cpp will saturate whatever it is
    /// given, and starving the VAD thread shows up as AudioRingBuffer dropped samples.
    /// </summary>
    public int NumThreads { get; set; } = 4;

    /// <summary>
    /// How many transformer layers to offload to the GPU. 0 (the default) keeps everything on the
    /// CPU, which is the only thing that works unless the build used a GPU llama.cpp backend —
    /// <c>LLamaSharp.Backend.Cpu</c> ignores this. Any value at or above the model's layer count
    /// offloads the whole thing; llama.cpp clamps.
    ///
    /// The projector moves separately — see <see cref="ProjectorOnGpu"/>, which you almost always
    /// want set alongside this one.
    ///
    /// Whether this is faster is a property of the hardware, not of the model. On a discrete card
    /// with its own VRAM it is a large win. On an APU, the iGPU shares the CPU's memory bus, and
    /// token generation is bandwidth-bound — measure before assuming.
    /// </summary>
    public int GpuLayers { get; set; }

    /// <summary>
    /// Whether the mmproj projector runs on the GPU, independently of <see cref="GpuLayers"/>.
    ///
    /// Offloading the layers while leaving the projector on the CPU is the worst of both — the
    /// GPU's memory gets spent and the slow part still runs on the CPU — so if you set
    /// <see cref="GpuLayers"/>, set this too.
    ///
    /// The useful asymmetry is the other direction. The projector is a fraction of the language
    /// model's size, so on a UMA APU it fits a VRAM carveout the model itself would overflow; and
    /// image encoding is compute-bound, which is the half an iGPU actually wins, while token
    /// generation is bandwidth-bound, which is the half it does not. Projector on the GPU with the
    /// layers left on the CPU is therefore a real configuration, not a mistake.
    /// </summary>
    public bool ProjectorOnGpu { get; set; }

    public int MaxTokens { get; set; } = 80;

    public uint ContextSize { get; set; } = 4096;

    /// <summary>
    /// Floor on the gap between descriptions. Without it, a continuously busy scene means
    /// continuous VLM inference, which would peg the CPU indefinitely. This is the cooldown the
    /// motion gate relies on — the gate says *whether* the scene changed, this says *how often*
    /// we are willing to pay to look.
    /// </summary>
    public double MinSecondsBetweenDescriptions { get; set; } = 5.0;
}
