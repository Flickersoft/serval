using System.Runtime.InteropServices;

namespace Serval.Ai;

/// <summary>
/// P/Invoke over the TFLite C API and libedgetpu, the same shape as <see cref="Rknn"/>.
///
/// <para><b>Two libraries and not one, which is the thing to understand before reading further.</b>
/// libedgetpu is only a TFLite <em>delegate</em>: it contributes the Edge TPU kernels and nothing else.
/// Loading a model, allocating tensors, reading shapes and calling Invoke are all the TFLite C API, so
/// an EdgeTPU backend needs <c>libtensorflowlite_c.so</c> as well — and nobody publishes that, so it is
/// built from TensorFlow source (see the Dockerfile's edgetpu stage for the pinned tag and hash).</para>
///
/// <para>The declarations mirror <c>edgetpu_c.h</c> and <c>tensorflow/lite/c/c_api.h</c> from the
/// pinned runtime — libedgetpu 16.x and TensorFlow v2.16.2 — and were transcribed against those
/// headers rather than from memory. <b>Re-check them against the headers if either is bumped</b>: the
/// struct layouts and enum values below are load-bearing, and a changed one is a silent marshalling
/// fault rather than a link error.</para>
///
/// <para>No error checking and no disposal here, following the same split as
/// <see cref="Rknn"/>: every call returns its status and the consumer decides what a failure means.
/// x86-64 only in practice — libedgetpu ships no build this repo would use elsewhere.</para>
/// </summary>
internal static unsafe class EdgeTpu
{
    private const string TfLite = "tensorflowlite_c";
    private const string LibEdgeTpu = "edgetpu";

    // TfLiteStatus, verbatim from c_api_types.h.
    public const int TfLiteOk = 0;
    public const int TfLiteError = 1;
    public const int TfLiteDelegateError = 2;

    // TfLiteType, the two that matter for a quantised detector. The full enum is longer; a model of
    // any other input type is refused at load rather than silently reinterpreted.
    public const int TfLiteUInt8 = 3;
    public const int TfLiteInt8 = 9;

    // edgetpu_device_type, verbatim from edgetpu_c.h.
    public const int ApexPci = 0;
    public const int ApexUsb = 1;

    /// <summary>Mirrors <c>TfLiteQuantizationParams</c>: <c>float scale; int32_t zero_point;</c>.
    ///
    /// <para>Returned by value from <see cref="TfLiteTensorQuantizationParams"/>. Informational for
    /// this backend rather than a gate — both conventions in circulation (scale 1/255 zero 0, and scale
    /// 1/128 zero 128) are satisfied by writing the raw pixel byte, which is what
    /// <see cref="FramePreparer"/> already does for <see cref="DetectorLayout.Uint8Nhwc"/>.</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TfLiteQuantizationParams
    {
        public float Scale;
        public int ZeroPoint;
    }

    /// <summary>Mirrors <c>struct edgetpu_device</c>: an <c>enum</c> (int) and a <c>const char*</c>.
    ///
    /// <para><see cref="Path"/> is the device's sysfs path and is the only stable way to name a device:
    /// <c>edgetpu_list_devices</c> does not guarantee an order, and it was observed returning two USB
    /// devices in one order and then the other between runs. A lane must bind to the path, never to an
    /// index.</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EdgeTpuDevice
    {
        public int Type;
        public IntPtr Path;
    }

    /// <summary>Mirrors <c>struct edgetpu_option</c>, a name/value pair of C strings.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EdgeTpuOption
    {
        public IntPtr Name;
        public IntPtr Value;
    }

    // ---- libedgetpu -----------------------------------------------------------------------------

    [DllImport(LibEdgeTpu, CallingConvention = CallingConvention.Cdecl)]
    public static extern EdgeTpuDevice* edgetpu_list_devices(out nuint num_devices);

    [DllImport(LibEdgeTpu, CallingConvention = CallingConvention.Cdecl)]
    public static extern void edgetpu_free_devices(EdgeTpuDevice* devices);

    [DllImport(LibEdgeTpu, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr edgetpu_create_delegate(
        int type,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? name,
        EdgeTpuOption* options,
        nuint num_options);

    [DllImport(LibEdgeTpu, CallingConvention = CallingConvention.Cdecl)]
    public static extern void edgetpu_free_delegate(IntPtr delegateHandle);

    [DllImport(LibEdgeTpu, CallingConvention = CallingConvention.Cdecl)]
    public static extern void edgetpu_verbosity(int verbosity);

    [DllImport(LibEdgeTpu, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr edgetpu_version();

    // ---- TFLite C API ---------------------------------------------------------------------------

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr TfLiteModelCreateFromFile(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string model_path);

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TfLiteModelDelete(IntPtr model);

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr TfLiteInterpreterOptionsCreate();

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TfLiteInterpreterOptionsDelete(IntPtr options);

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TfLiteInterpreterOptionsSetNumThreads(IntPtr options, int num_threads);

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TfLiteInterpreterOptionsAddDelegate(IntPtr options, IntPtr delegateHandle);

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr TfLiteInterpreterCreate(IntPtr model, IntPtr optional_options);

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern void TfLiteInterpreterDelete(IntPtr interpreter);

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TfLiteInterpreterAllocateTensors(IntPtr interpreter);

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TfLiteInterpreterInvoke(IntPtr interpreter);

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TfLiteInterpreterGetInputTensorCount(IntPtr interpreter);

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr TfLiteInterpreterGetInputTensor(IntPtr interpreter, int input_index);

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TfLiteInterpreterGetOutputTensorCount(IntPtr interpreter);

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr TfLiteInterpreterGetOutputTensor(IntPtr interpreter, int output_index);

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TfLiteTensorType(IntPtr tensor);

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TfLiteTensorNumDims(IntPtr tensor);

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TfLiteTensorDim(IntPtr tensor, int dim_index);

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern nuint TfLiteTensorByteSize(IntPtr tensor);

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr TfLiteTensorData(IntPtr tensor);

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr TfLiteTensorName(IntPtr tensor);

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern TfLiteQuantizationParams TfLiteTensorQuantizationParams(IntPtr tensor);

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TfLiteTensorCopyFromBuffer(
        IntPtr tensor, void* input_data, nuint input_data_size);

    [DllImport(TfLite, CallingConvention = CallingConvention.Cdecl)]
    public static extern int TfLiteTensorCopyToBuffer(
        IntPtr tensor, void* output_data, nuint output_data_size);
}
