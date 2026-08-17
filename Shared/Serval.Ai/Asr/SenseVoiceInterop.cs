using System.Runtime.InteropServices;

namespace Serval.Ai;

/// <summary>Raw labels as SenseVoice emits them, e.g. "&lt;|HAPPY|&gt;". Empty when absent.</summary>
public readonly record struct SenseVoiceRaw(string Text, string? Language, string? Emotion, string? Event);

/// <summary>
/// Reads SenseVoice's language/emotion/event labels from the sherpa-onnx C API.
///
/// Why this exists: the C++ core parses these into structured fields, and the C struct
/// SherpaOnnxOfflineRecognizerResult exposes them, but the official managed wrapper
/// (OfflineRecognizerResult) only maps Text/Tokens/Timestamps/Durations. Emotion is
/// therefore unreachable through the public C# API. OfflineStream.Handle is public, so
/// we call the same C entrypoint ourselves with a fuller struct definition.
///
/// This binds us to the native struct layout, which is why the sherpa-onnx package
/// version is pinned exactly in Serval.CameraModule.csproj. Verified against c-api.h at tag v1.13.4.
/// If you bump that version, re-check the struct below against the header, and trust the
/// label validation in SenseVoiceAnalyzer to catch drift at runtime.
/// </summary>
internal static class SenseVoiceInterop
{
    private const string Lib = "sherpa-onnx-c-api";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SherpaOnnxGetOfflineStreamResult(IntPtr stream);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void SherpaOnnxDestroyOfflineRecognizerResult(IntPtr result);

    /// <summary>
    /// Prefix of SherpaOnnxOfflineRecognizerResult, truncated after the fields we read.
    /// Reading a prefix is safe: Marshal only touches the declared bytes. Field order and
    /// types must match the header exactly — `count` sits at offset 16 and the following
    /// pointer is padded to 24 on both x86-64 and aarch64, which sequential layout
    /// reproduces.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeResult
    {
        public IntPtr Text;
        public IntPtr Timestamps;
        public int Count;
        public IntPtr Tokens;
        public IntPtr TokensArr;
        public IntPtr Json;
        public IntPtr Lang;
        public IntPtr Emotion;
        public IntPtr Event;
    }

    /// <summary>Reads the result for an already-decoded stream. Safe to call alongside OfflineStream.Result.</summary>
    public static SenseVoiceRaw Read(IntPtr streamHandle)
    {
        IntPtr result = SherpaOnnxGetOfflineStreamResult(streamHandle);
        if (result == IntPtr.Zero)
        {
            return new SenseVoiceRaw(string.Empty, null, null, null);
        }

        try
        {
            var native = Marshal.PtrToStructure<NativeResult>(result);
            return new SenseVoiceRaw(
                Marshal.PtrToStringUTF8(native.Text) ?? string.Empty,
                Marshal.PtrToStringUTF8(native.Lang),
                Marshal.PtrToStringUTF8(native.Emotion),
                Marshal.PtrToStringUTF8(native.Event));
        }
        finally
        {
            SherpaOnnxDestroyOfflineRecognizerResult(result);
        }
    }
}
