using System.Runtime.InteropServices;

namespace Serval.Ai;

/// <summary>
/// P/Invoke bindings for the RKLLM runtime (<c>librkllmrt.so</c>, 1.2.3), which runs the
/// Qwen3-VL language model on the RK3588 NPU and consumes the image embeddings produced by the
/// RKNN vision encoder (<see cref="Rknn"/>).
///
/// Structs mirror <c>rkllm.h</c> from the pinned runtime exactly — bools are 1 byte (C++), the
/// input union is modelled as its largest member (multimodal), and the callback is Cdecl. Bump
/// the runtime only after re-checking these against the header, the same discipline as
/// <see cref="SenseVoiceInterop"/>.
///
/// arm64/Pi only.
/// </summary>
internal static class Rkllm
{
    private const string Lib = "rkllmrt";

    public const int RKLLM_INPUT_PROMPT = 0;
    public const int RKLLM_INPUT_MULTIMODAL = 3;

    public const int RKLLM_INFER_GENERATE = 0;

    // LLMCallState
    public const int RKLLM_RUN_NORMAL = 0;
    public const int RKLLM_RUN_WAITING = 1;
    public const int RKLLM_RUN_FINISH = 2;
    public const int RKLLM_RUN_ERROR = 3;

    /// <summary>Mirrors <c>RKLLMExtendParam</c>. reserved[104] fixes the size at 120 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct RKLLMExtendParam
    {
        public int base_domain_id;
        public sbyte embed_flash;
        public sbyte enabled_cpus_num;
        public uint enabled_cpus_mask;
        public byte n_batch;
        public sbyte use_cross_attn;
        public fixed byte reserved[104];
    }

    /// <summary>
    /// Mirrors <c>RKLLMParam</c>. The two bools are <see cref="byte"/> (C++ bool is one byte);
    /// declaring them as managed bool would be 4 bytes and shift every field after them.
    /// Returned by value from <see cref="rkllm_createDefaultParam"/>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RKLLMParam
    {
        public IntPtr model_path;
        public int max_context_len;
        public int max_new_tokens;
        public int top_k;
        public int n_keep;
        public float top_p;
        public float temperature;
        public float repeat_penalty;
        public float frequency_penalty;
        public float presence_penalty;
        public int mirostat;
        public float mirostat_tau;
        public float mirostat_eta;
        public byte skip_special_token;
        public byte is_async;
        public IntPtr img_start;
        public IntPtr img_end;
        public IntPtr img_content;
        public RKLLMExtendParam extend_param;
    }

    /// <summary>
    /// Mirrors <c>RKLLMInput</c> with its input union expanded to the multimodal member (the
    /// largest). For a plain text prompt, <see cref="prompt"/> aliases <c>prompt_input</c> — the
    /// union starts at the same offset — so the text path sets only role, input_type, prompt.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RKLLMInput
    {
        public IntPtr role;
        public byte enable_thinking;
        public int input_type;
        // union { const char* prompt_input; RKLLMMultiModalInput multimodal_input; ... }
        public IntPtr prompt;        // multimodal_input.prompt (and prompt_input)
        public IntPtr image_embed;   // float*
        public nuint n_image_tokens;
        public nuint n_image;
        public nuint image_width;
        public nuint image_height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RKLLMInferParam
    {
        public int mode;                 // RKLLMInferMode
        public IntPtr lora_params;       // RKLLMLoraParam*
        public IntPtr prompt_cache_params; // RKLLMPromptCacheParam*
        public int keep_history;
    }

    // RKLLMResult and its sub-structs are read from the callback's pointer. We only need `text`,
    // which is the first field, so we marshal it directly rather than mirror the whole struct.
    [StructLayout(LayoutKind.Sequential)]
    public struct RKLLMResultHead
    {
        public IntPtr text;      // const char*
        public int token_id;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int LLMResultCallback(IntPtr result, IntPtr userdata, int state);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern RKLLMParam rkllm_createDefaultParam();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rkllm_init(out IntPtr handle, ref RKLLMParam param, LLMResultCallback callback);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int rkllm_set_chat_template(
        IntPtr handle, string system_prompt, string prompt_prefix, string prompt_postfix);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rkllm_run(
        IntPtr handle, ref RKLLMInput input, ref RKLLMInferParam infer_params, IntPtr userdata);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rkllm_clear_kv_cache(IntPtr handle, int keep_system_prompt, IntPtr start_pos, IntPtr end_pos);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rkllm_destroy(IntPtr handle);
}
