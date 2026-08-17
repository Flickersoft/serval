using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.Logging;

namespace Serval.Ai;

/// <summary>
/// Describes one or more JPEG frames using Qwen3-VL through llama.cpp's mtmd, in-process.
///
/// Runs on linux-x64 and linux-arm64 alike: LLamaSharp.Backend.Cpu ships libllama/libmtmd
/// for both. Qwen3-VL was chosen over Gemma 4 because RKLLM can accelerate it on the RK3588
/// NPU later, whereas Gemma 4's vision path cannot run on the NPU at all.
///
/// Several frames can be shown at once — mtmd substitutes one image per media marker — which lets
/// the model say what is *happening* rather than only what is there. They must be consecutive
/// captures from one camera, and the prompt tells the model how far apart they were taken, since
/// "what changed" means nothing without that.
///
/// Roughly a thousand times slower than the audio pipeline (SenseVoice runs at rtf 0.02; a
/// description takes seconds here and 10-30s on the Pi), and each extra frame adds another frame's
/// worth of image tokens. **Never call this from the audio path.**
/// </summary>
public sealed class Qwen3VisionRunner : IVisionInferenceRunner
{
    private readonly VisionOptions _options;
    private readonly ILogger<Qwen3VisionRunner> _logger;

    private readonly LLamaWeights _weights;
    private readonly MtmdWeights _mtmd;
    private readonly ModelParams _modelParams;

    /// <summary>Where the image is spliced into the prompt. Comes from mtmd's native defaults.</summary>
    private readonly string _mediaMarker;

    // llama.cpp contexts are not thread-safe, and inference is single-consumer by design.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Qwen3VisionRunner(
        VisionOptions options,
        ILogger<Qwen3VisionRunner> logger,
        LLamaWeights weights,
        MtmdWeights mtmd,
        ModelParams modelParams,
        string mediaMarker)
    {
        _options = options;
        _logger = logger;
        _weights = weights;
        _mtmd = mtmd;
        _modelParams = modelParams;
        _mediaMarker = mediaMarker;
    }

    /// <summary>
    /// Loads the model and projector. Async because MtmdWeights.LoadFromFileAsync is; the
    /// caller should await this at startup so a missing model fails immediately rather than
    /// on the first frame.
    /// </summary>
    public static async Task<Qwen3VisionRunner> CreateAsync(
        VisionOptions vision,
        ILogger<Qwen3VisionRunner> logger,
        CancellationToken cancellationToken = default)
    {
        // llama.cpp logs every tensor at load. Route it through ILogger at Trace so it is
        // available when debugging but does not bury the application's own output.
        NativeLogConfig.llama_log_set((level, message) =>
        {
            LogLevel mapped = level switch
            {
                LLamaLogLevel.Error => LogLevel.Error,
                LLamaLogLevel.Warning => LogLevel.Warning,
                _ => LogLevel.Trace,
            };
            logger.Log(mapped, "llama.cpp: {Message}", message.TrimEnd());
        });

        if (!File.Exists(vision.ModelPath))
        {
            throw new FileNotFoundException(
                $"Vision model not found at '{vision.ModelPath}'. Run ./scripts/fetch-models.sh", vision.ModelPath);
        }

        // Without the projector the model loads fine but is blind, and would describe nothing
        // while looking like it worked. Refuse instead.
        if (!File.Exists(vision.MmprojPath))
        {
            throw new FileNotFoundException(
                $"Vision projector (mmproj) not found at '{vision.MmprojPath}'. The model cannot "
                + "see images without it. Run ./scripts/fetch-models.sh", vision.MmprojPath);
        }

        var modelParams = new ModelParams(vision.ModelPath)
        {
            ContextSize = vision.ContextSize,
            GpuLayerCount = vision.GpuLayers,

            // Set explicitly: llama.cpp otherwise grabs every core, which on the Pi would
            // starve the VAD thread and drop audio. Vision must never cost us transcripts.
            Threads = vision.NumThreads,
            BatchThreads = vision.NumThreads,
        };

        var stopwatch = Stopwatch.StartNew();
        LLamaWeights weights = await LLamaWeights.LoadFromFileAsync(modelParams, cancellationToken);

        var mtmdParams = MtmdContextParams.Default();

        // Set independently of GpuLayers, because the projector and the layers do not want the
        // same hardware on the same machine — see VisionOptions.ProjectorOnGpu.
        mtmdParams.UseGpu = vision.ProjectorOnGpu;
        mtmdParams.NThreads = vision.NumThreads;

        MtmdWeights mtmd = await MtmdWeights.LoadFromFileAsync(vision.MmprojPath, weights, mtmdParams);

        string marker = mtmdParams.MediaMarker
            ?? throw new InvalidOperationException("mtmd did not report a media marker.");

        logger.LogInformation(
            "Qwen3-VL ready in {Elapsed:F1}s (threads={Threads}, context={Context}, "
            + "gpuLayers={GpuLayers}, projectorOnGpu={ProjectorOnGpu})",
            stopwatch.Elapsed.TotalSeconds, vision.NumThreads, vision.ContextSize, vision.GpuLayers,
            vision.ProjectorOnGpu);

        return new Qwen3VisionRunner(vision, logger, weights, mtmd, modelParams, marker);
    }

    /// <summary>mtmd splices one image per media marker, so the CPU path is limited only by context size.</summary>
    public int MaxFrames => Math.Max(1, _options.MaxFrames);

    public async Task<string> RunInferenceAsync(
        IReadOnlyList<VisionFrame> frames,
        string prompt,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(frames.Count);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await DescribeAsync(frames, prompt, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> DescribeAsync(
        IReadOnlyList<VisionFrame> frames,
        string instruction,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // A fresh context per description: it keeps each one independent and stops KV cache
        // from growing across frames. Context creation is cheap next to the inference itself.
        using LLamaContext context = _weights.CreateContext(_modelParams);
        var executor = new InteractiveExecutor(context, _mtmd);

        // LoadMedia takes the JPEG bytes directly — no temp file, and the camera already
        // produced JPEG, so there is no encode step anywhere in this path. Order matters: mtmd
        // consumes the embeds in the order the markers appear, so oldest frame first.
        foreach (VisionFrame frame in frames)
        {
            executor.Embeds.Add(_mtmd.LoadMedia(frame.Jpeg.AsSpan()));
        }

        // Context creation and embed loading both land here — everything paid before a single
        // token is asked for.
        TimeSpan setup = stopwatch.Elapsed;

        string prompt = BuildPrompt(instruction, frames.Count);

        var inferenceParams = new InferenceParams
        {
            MaxTokens = _options.MaxTokens,
            AntiPrompts = ["<|im_end|>", "<|endoftext|>"],
        };

        // The two halves of a description respond to hardware very differently: everything up to
        // the first token is image encode plus prefill, which is compute-bound, while generation
        // after it is memory-bandwidth-bound. A single total hides which one a config change moved,
        // which is exactly the question when deciding what to offload to a GPU.
        TimeSpan toFirstToken = TimeSpan.Zero;
        TimeSpan inferenceDone = TimeSpan.Zero;
        int tokens = 0;

        var text = new StringBuilder();
        try
        {
            await foreach (string token in executor.InferAsync(prompt, inferenceParams, cancellationToken))
            {
                if (tokens++ == 0)
                {
                    toFirstToken = stopwatch.Elapsed - setup;
                }

                text.Append(token);
            }

            // Read before the finally disposes the embeds: teardown is not generation, and
            // folding it into the token rate would make the figure answer a different question.
            inferenceDone = stopwatch.Elapsed;
        }
        finally
        {
            foreach (var embed in executor.Embeds)
            {
                embed.Dispose();
            }

            executor.Embeds.Clear();
        }

        // InferAsync ends the enumeration quietly when cancelled rather than throwing, which
        // leaves a half-generated fragment looking exactly like a finished description — a
        // shutdown mid-inference once yielded the single word "A". Turn that into the
        // exception it should have been so the caller discards it instead of publishing it.
        cancellationToken.ThrowIfCancellationRequested();

        string description = Clean(text.ToString());

        TimeSpan generating = inferenceDone - setup - toFirstToken;
        double tokensPerSecond = generating > TimeSpan.Zero ? tokens / generating.TotalSeconds : 0;

        _logger.LogInformation(
            "Described {Frames} frame(s) in {Elapsed:F1}s "
            + "(setup {Setup:F1}s, first token {FirstToken:F1}s, {Tokens} tok in {Generating:F1}s "
            + "= {TokensPerSecond:F1} tok/s): \"{Description}\"",
            frames.Count, stopwatch.Elapsed.TotalSeconds,
            setup.TotalSeconds, toFirstToken.TotalSeconds, tokens, generating.TotalSeconds,
            tokensPerSecond, description);

        return description;
    }

    /// <summary>
    /// Qwen3-VL expects ChatML with a media marker where each image belongs. mtmd splits the
    /// prompt on those markers and substitutes the image embeddings in order, so the marker count
    /// must equal the number of loaded frames.
    /// </summary>
    private string BuildPrompt(string instruction, int frameCount) =>
        "<|im_start|>system\nYou are a helpful assistant.<|im_end|>\n"
        + $"<|im_start|>user\n{string.Concat(Enumerable.Repeat(_mediaMarker, frameCount))}{instruction}<|im_end|>\n"
        + "<|im_start|>assistant\n";

    private static string Clean(string raw)
    {
        string text = raw.Trim();

        foreach (string marker in (string[])["<|im_end|>", "<|endoftext|>"])
        {
            int index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
            {
                text = text[..index];
            }
        }

        return text.Trim();
    }

    public void Dispose()
    {
        _mtmd.Dispose();
        _weights.Dispose();
        _gate.Dispose();
    }
}
