using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Serval.Ai;

/// <summary>
/// Runs Qwen3-VL on the RK3588 NPU as the arm64 vision path, replacing the CPU LLamaSharp
/// runner (<see cref="Qwen3VisionRunner"/>) which takes ~48s/image on the Pi. Two stages:
///
///   JPEG → (decode, letterbox, resize) → RKNN vision encoder → float image embeddings
///        → RKLLM language model (multimodal) → description text
///
/// See <see cref="Rknn"/>/<see cref="Rkllm"/> for the native contracts, and the Qengineering
/// RK35llm reference this follows. Single-context: one inference at a time (the vision worker
/// is already single-consumer). NPU-only — never constructed on x64.
/// </summary>
public sealed class RkllmVisionRunner : IVisionInferenceRunner
{
    private readonly ILogger<RkllmVisionRunner> _logger;
    private readonly object _gate = new();

    private IntPtr _llm;
    private ulong _rknn;
    private int _modelWidth, _modelHeight, _modelChannel;
    private int _imageTokens, _embedSize, _outputCount;
    private float[] _imgVec = [];

    // The callback is held in a field so the GC cannot collect it while the native runtime
    // holds a pointer to it (same reason AudioCaptureWorker pins its PortAudio callback).
    private readonly Rkllm.LLMResultCallback _callback;
    private readonly StringBuilder _response = new();

    // Signalled by the callback on FINISH/ERROR. rkllm_run is documented synchronous, but the
    // reference still waits on completion after it returns, so we do too rather than assume the
    // last tokens have landed by the time the call unwinds.
    private readonly ManualResetEventSlim _done = new(initialState: false);

    private RkllmVisionRunner(ILogger<RkllmVisionRunner> logger)
    {
        _logger = logger;
        _callback = OnToken;
    }

    /// <summary>Loads both models onto the NPU. Fails loudly at startup if either is missing.</summary>
    public static RkllmVisionRunner Create(VisionOptions vision, ILogger<RkllmVisionRunner> logger)
    {
        var runner = new RkllmVisionRunner(logger);
        runner.LoadModels(vision);
        return runner;
    }

    private void LoadModels(VisionOptions vision)
    {
        foreach (string path in new[] { vision.NpuLlmModelPath, vision.NpuVisionModelPath })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"NPU vision model not found at '{path}'. Download the pre-converted Qwen3-VL-2B "
                    + "files and set Vision.NpuLlmModelPath / Vision.NpuVisionModelPath.", path);
            }
        }

        InitLlm(vision);
        InitVisionEncoder(vision.NpuVisionModelPath);

        _logger.LogInformation(
            "RKLLM vision ready on NPU (encoder {W}x{H}x{C}, {Tokens} image tokens x {Embed} embed)",
            _modelWidth, _modelHeight, _modelChannel, _imageTokens, _embedSize);
    }

    private void InitLlm(VisionOptions vision)
    {
        Rkllm.RKLLMParam param = Rkllm.rkllm_createDefaultParam();
        param.max_new_tokens = vision.MaxTokens;
        param.max_context_len = (int)vision.ContextSize;
        param.top_k = 1;
        param.skip_special_token = 1;
        param.extend_param.base_domain_id = 1;

        // Qwen3-VL image markers (from the reference).
        IntPtr modelPath = Marshal.StringToHGlobalAnsi(vision.NpuLlmModelPath);
        IntPtr imgStart = Marshal.StringToHGlobalAnsi("<|vision_start|>");
        IntPtr imgEnd = Marshal.StringToHGlobalAnsi("<|vision_end|>");
        IntPtr imgContent = Marshal.StringToHGlobalAnsi("<|image_pad|>");
        try
        {
            param.model_path = modelPath;
            param.img_start = imgStart;
            param.img_end = imgEnd;
            param.img_content = imgContent;

            int ret = Rkllm.rkllm_init(out _llm, ref param, _callback);
            if (ret != 0)
            {
                throw new InvalidOperationException($"rkllm_init failed (ret={ret}).");
            }

            Rkllm.rkllm_set_chat_template(
                _llm,
                "<|im_start|>system\nYou are a helpful assistant.<|im_end|>\n",
                "<|im_start|>user\n",
                "<|im_end|>\n<|im_start|>assistant\n");
        }
        finally
        {
            Marshal.FreeHGlobal(modelPath);
            Marshal.FreeHGlobal(imgStart);
            Marshal.FreeHGlobal(imgEnd);
            Marshal.FreeHGlobal(imgContent);
        }
    }

    private unsafe void InitVisionEncoder(string modelPath)
    {
        byte[] pathBytes = Encoding.ASCII.GetBytes(modelPath + '\0');
        int ret;
        fixed (byte* p = pathBytes)
        {
            // size=0 tells rknn_init to treat `model` as a file path rather than a buffer.
            ret = Rknn.rknn_init(out _rknn, p, 0, 0, null);
        }
        if (ret < 0)
        {
            throw new InvalidOperationException($"rknn_init failed (ret={ret}).");
        }

        if (Rknn.rknn_set_core_mask(_rknn, Rknn.RKNN_NPU_CORE_0_1_2) < 0)
        {
            throw new InvalidOperationException("rknn_set_core_mask failed.");
        }

        var ioNum = default(Rknn.rknn_input_output_num);
        if (Rknn.rknn_query(_rknn, Rknn.RKNN_QUERY_IN_OUT_NUM, &ioNum, (uint)sizeof(Rknn.rknn_input_output_num)) != Rknn.RKNN_SUCC)
        {
            throw new InvalidOperationException("rknn_query(IN_OUT_NUM) failed.");
        }
        _outputCount = (int)ioNum.n_output;

        var inputAttr = default(Rknn.rknn_tensor_attr);
        inputAttr.index = 0;
        if (Rknn.rknn_query(_rknn, Rknn.RKNN_QUERY_INPUT_ATTR, &inputAttr, (uint)sizeof(Rknn.rknn_tensor_attr)) != Rknn.RKNN_SUCC)
        {
            throw new InvalidOperationException("rknn_query(INPUT_ATTR) failed.");
        }

        if (inputAttr.fmt == Rknn.RKNN_TENSOR_NCHW)
        {
            _modelChannel = (int)inputAttr.dims[1];
            _modelHeight = (int)inputAttr.dims[2];
            _modelWidth = (int)inputAttr.dims[3];
        }
        else
        {
            _modelHeight = (int)inputAttr.dims[1];
            _modelWidth = (int)inputAttr.dims[2];
            _modelChannel = (int)inputAttr.dims[3];
        }

        // Preprocess packs three bytes per pixel, and EncodeImage hands the native side a length
        // derived from this. They agree only for an RGB encoder, so a model wanting anything else
        // is rejected here rather than having the runtime read past the end of the buffer.
        if (_modelChannel != 3)
        {
            throw new InvalidOperationException(
                $"Vision encoder expects {_modelChannel} input channels; only 3-channel RGB is supported.");
        }

        var outputAttr = default(Rknn.rknn_tensor_attr);
        outputAttr.index = 0;
        if (Rknn.rknn_query(_rknn, Rknn.RKNN_QUERY_OUTPUT_ATTR, &outputAttr, (uint)sizeof(Rknn.rknn_tensor_attr)) != Rknn.RKNN_SUCC)
        {
            throw new InvalidOperationException("rknn_query(OUTPUT_ATTR) failed.");
        }

        // First >1 output dim is the image-token count, the next is the embedding size.
        for (int i = 0; i < 4; i++)
        {
            if (outputAttr.dims[i] > 1)
            {
                _imageTokens = (int)outputAttr.dims[i];
                _embedSize = (int)outputAttr.dims[i + 1];
                break;
            }
        }

        _imgVec = new float[_imageTokens * _embedSize * _outputCount];
    }

    /// <summary>
    /// Exactly one. <c>_imgVec</c> is allocated for a single image's <c>_imageTokens × _embedSize</c>
    /// and the RKLLM input carries one <c>image_embed</c> pointer, so there is nowhere to put a
    /// second frame. Callers hand this runner the newest frame and it describes a still; inferring
    /// movement is a CPU-path capability only.
    /// </summary>
    public int MaxFrames => 1;

    public Task<string> RunInferenceAsync(
        IReadOnlyList<VisionFrame> frames,
        string prompt,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(frames.Count);

        // Newest frame: if a caller hands us more than we can take, the most recent one is the
        // one worth describing.
        byte[] jpeg = frames[^1].Jpeg;
        return Task.Run(() => Describe(jpeg, prompt, cancellationToken), cancellationToken);
    }

    private unsafe string Describe(byte[] imageJpeg, string prompt, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] pixels = Preprocess(imageJpeg);
            EncodeImage(pixels);

            cancellationToken.ThrowIfCancellationRequested();

            _response.Clear();
            _done.Reset();

            // The RKLLM multimodal flow inserts the image embeddings at the prompt's image
            // marker; "<image>" is the placeholder the reference uses.
            IntPtr promptPtr = Marshal.StringToHGlobalAnsi("<image>" + prompt);
            IntPtr rolePtr = Marshal.StringToHGlobalAnsi("user");
            GCHandle embedHandle = GCHandle.Alloc(_imgVec, GCHandleType.Pinned);
            try
            {
                var input = new Rkllm.RKLLMInput
                {
                    role = rolePtr,
                    input_type = Rkllm.RKLLM_INPUT_MULTIMODAL,
                    prompt = promptPtr,
                    image_embed = embedHandle.AddrOfPinnedObject(),
                    n_image_tokens = (nuint)_imageTokens,
                    n_image = 1,
                    image_width = (nuint)_modelWidth,
                    image_height = (nuint)_modelHeight,
                };

                var infer = new Rkllm.RKLLMInferParam
                {
                    mode = Rkllm.RKLLM_INFER_GENERATE,
                    keep_history = 0,
                };

                int ret = Rkllm.rkllm_run(_llm, ref input, ref infer, IntPtr.Zero);
                if (ret != 0)
                {
                    throw new InvalidOperationException($"rkllm_run failed (ret={ret}).");
                }

                // Wait for the callback's FINISH/ERROR before reading the buffer.
                _done.Wait(cancellationToken);
            }
            finally
            {
                embedHandle.Free();
                Marshal.FreeHGlobal(promptPtr);
                Marshal.FreeHGlobal(rolePtr);
            }

            return _response.ToString().Trim();
        }
    }

    /// <summary>Streams tokens in on the RKLLM inference thread; runs during rkllm_run.</summary>
    private int OnToken(IntPtr result, IntPtr userdata, int state)
    {
        if (state == Rkllm.RKLLM_RUN_NORMAL && result != IntPtr.Zero)
        {
            var head = Marshal.PtrToStructure<Rkllm.RKLLMResultHead>(result);
            if (head.text != IntPtr.Zero)
            {
                _response.Append(Marshal.PtrToStringUTF8(head.text));
            }
        }
        else if (state is Rkllm.RKLLM_RUN_FINISH or Rkllm.RKLLM_RUN_ERROR)
        {
            _done.Set();
        }

        return 0;
    }

    /// <summary>
    /// Decodes the JPEG and letterboxes it to the encoder's input (gray padding), the same
    /// preprocessing the reference does with OpenCV — but pure-managed, so no image-library native
    /// dependency. Returns tightly-packed uint8 RGB in NHWC order.
    ///
    /// The letterboxing is <see cref="Letterboxed"/>, shared with the object detector, which needs
    /// the identical transform. Nothing here maps coordinates back: this path hands the encoder a
    /// whole image and gets an embedding, so there is no box to place.
    /// </summary>
    private byte[] Preprocess(byte[] imageJpeg)
    {
        using Letterboxed frame = Letterboxed.Fit(
            imageJpeg, _modelWidth, _modelHeight, Color.FromRgb(127, 127, 127));

        return frame.ToNhwcBytes();
    }

    private unsafe void EncodeImage(byte[] pixels)
    {
        fixed (byte* pix = pixels)
        {
            var input = new Rknn.rknn_input
            {
                index = 0,
                type = Rknn.RKNN_TENSOR_UINT8,
                fmt = Rknn.RKNN_TENSOR_NHWC,
                size = (uint)(_modelWidth * _modelHeight * _modelChannel),
                buf = pix,
            };

            if (Rknn.rknn_inputs_set(_rknn, 1, &input) < 0)
            {
                throw new InvalidOperationException("rknn_inputs_set failed.");
            }

            if (Rknn.rknn_run(_rknn, null) < 0)
            {
                throw new InvalidOperationException("rknn_run failed.");
            }

            var outputs = new Rknn.rknn_output[_outputCount];
            for (int j = 0; j < _outputCount; j++)
            {
                outputs[j].want_float = 1;
            }

            fixed (Rknn.rknn_output* outPtr = outputs)
            {
                if (Rknn.rknn_outputs_get(_rknn, (uint)_outputCount, outPtr, null) < 0)
                {
                    throw new InvalidOperationException("rknn_outputs_get failed.");
                }

                fixed (float* vec = _imgVec)
                {
                    if (_outputCount == 1)
                    {
                        Buffer.MemoryCopy(outputs[0].buf, vec, _imgVec.Length * sizeof(float), outputs[0].size);
                    }
                    else
                    {
                        // Interleave the "deepstack" outputs per image token, as the reference does.
                        for (int t = 0; t < _imageTokens; t++)
                        {
                            for (int j = 0; j < _outputCount; j++)
                            {
                                float* src = (float*)outputs[j].buf + t * _embedSize;
                                float* dst = vec + t * _outputCount * _embedSize + j * _embedSize;
                                Buffer.MemoryCopy(src, dst, _embedSize * sizeof(float), _embedSize * sizeof(float));
                            }
                        }
                    }
                }

                Rknn.rknn_outputs_release(_rknn, (uint)_outputCount, outPtr);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_llm != IntPtr.Zero)
            {
                Rkllm.rkllm_destroy(_llm);
                _llm = IntPtr.Zero;
            }

            if (_rknn != 0)
            {
                Rknn.rknn_destroy(_rknn);
                _rknn = 0;
            }
        }
    }
}
