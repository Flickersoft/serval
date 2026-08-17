using System.Runtime.InteropServices;

namespace Serval.Ai;

/// <summary>
/// P/Invoke bindings for the RKNN runtime (<c>librknnrt.so</c>), used to run the Qwen3-VL
/// vision encoder on the RK3588 NPU. Only the handful of calls the two-stage vision path needs
/// are bound; the structs mirror <c>rknn_api.h</c> from the pinned runtime (2.3.x) exactly, so
/// the layout must be re-checked against that header if the runtime is bumped.
///
/// arm64/Pi only: there is no x86 build of this library. The desktop keeps using the LLamaSharp
/// vision path (<see cref="Qwen3VisionRunner"/>); this is only ever loaded on the Pi.
/// </summary>
internal static unsafe class Rknn
{
    private const string Lib = "rknnrt";

    // rknn_context is uint64_t on 64-bit platforms (rknn_api.h).
    // Query commands, tensor types, formats, and core masks — verbatim from rknn_api.h.
    public const int RKNN_SUCC = 0;
    public const int RKNN_MAX_DIMS = 16;
    public const int RKNN_MAX_NAME_LEN = 256;

    public const uint RKNN_QUERY_IN_OUT_NUM = 0;
    public const uint RKNN_QUERY_INPUT_ATTR = 1;
    public const uint RKNN_QUERY_OUTPUT_ATTR = 2;

    public const int RKNN_TENSOR_UINT8 = 3;   // FLOAT32=0, FLOAT16=1, INT8=2, UINT8=3
    public const int RKNN_TENSOR_NCHW = 0;
    public const int RKNN_TENSOR_NHWC = 1;

    public const uint RKNN_NPU_CORE_AUTO = 0;
    public const uint RKNN_NPU_CORE_0_1_2 = 1 | 2 | 4; // all three RK3588 cores

    [StructLayout(LayoutKind.Sequential)]
    public struct rknn_input_output_num
    {
        public uint n_input;
        public uint n_output;
    }

    /// <summary>Mirrors <c>rknn_tensor_attr</c> (376 bytes). Filled by rknn_query.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct rknn_tensor_attr
    {
        public uint index;
        public uint n_dims;
        public fixed uint dims[RKNN_MAX_DIMS];   // 16
        public fixed byte name[RKNN_MAX_NAME_LEN]; // 256
        public uint n_elems;
        public uint size;
        public int fmt;        // rknn_tensor_format
        public int type;       // rknn_tensor_type
        public int qnt_type;   // rknn_tensor_qnt_type
        public sbyte fl;
        public int zp;
        public float scale;
        public uint w_stride;
        public uint size_with_stride;
        public byte pass_through;
        public uint h_stride;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct rknn_input
    {
        public uint index;
        public void* buf;
        public uint size;
        public byte pass_through;
        public int type;   // rknn_tensor_type
        public int fmt;    // rknn_tensor_format
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct rknn_output
    {
        public byte want_float;
        public byte is_prealloc;
        public uint index;
        public void* buf;
        public uint size;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rknn_init(out ulong context, void* model, uint size, uint flag, void* extend);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rknn_set_core_mask(ulong context, uint core_mask);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rknn_query(ulong context, uint cmd, void* info, uint size);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rknn_inputs_set(ulong context, uint n_inputs, rknn_input* inputs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rknn_run(ulong context, void* extend);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rknn_outputs_get(ulong context, uint n_outputs, rknn_output* outputs, void* extend);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rknn_outputs_release(ulong context, uint n_outputs, rknn_output* outputs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rknn_destroy(ulong context);
}
