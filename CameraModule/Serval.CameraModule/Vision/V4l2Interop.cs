using System.Runtime.InteropServices;

namespace Serval.CameraModule;

/// <summary>
/// Raw V4L2 bindings: ioctl, mmap, and the structs the capture path needs.
///
/// Why not a library: OpenCvSharp has no linux-arm64 runtime, so the Orange Pi could not
/// capture at all. V4L2 is a stable kernel ABI and its structs use fixed-width types, so
/// the layouts below are identical on x86-64 and aarch64 — the same code works on both.
///
/// Struct layouts and ioctl codes must match linux/videodev2.h exactly. They are verified
/// by --capture-test producing a real JPEG from a real camera.
/// </summary>
internal static partial class V4l2
{
    public const int O_RDWR = 0x0002;
    public const int O_NONBLOCK = 0x0800;

    public const uint V4L2_BUF_TYPE_VIDEO_CAPTURE = 1;
    public const uint V4L2_MEMORY_MMAP = 1;

    public const uint V4L2_CAP_VIDEO_CAPTURE = 0x00000001;
    public const uint V4L2_CAP_STREAMING = 0x04000000;

    /// <summary>'M','J','P','G' as a fourcc — the C930e emits this natively, so frames are already JPEG.</summary>
    public const uint V4L2_PIX_FMT_MJPEG = 'M' | ('J' << 8) | ('P' << 16) | ('G' << 24);

    public const int PROT_READ = 0x1;
    public const int PROT_WRITE = 0x2;
    public const int MAP_SHARED = 0x01;

    // ioctl request codes, taken verbatim from linux/videodev2.h rather than recomputed.
    // The _IOC encoding embeds sizeof(struct), so deriving them from Marshal.SizeOf silently
    // produces a wrong code (and a baffling ENOTTY) whenever a layout is off by a byte.
    // These are identical on x86-64 and aarch64: V4L2 structs use fixed-width types.
    // VerifyLayouts() below asserts our structs match the sizes these codes encode.
    public const uint VIDIOC_QUERYCAP = 0x80685600;
    public const uint VIDIOC_S_FMT = 0xc0d05605;
    public const uint VIDIOC_REQBUFS = 0xc0145608;
    public const uint VIDIOC_QUERYBUF = 0xc0585609;
    public const uint VIDIOC_QBUF = 0xc058560f;
    public const uint VIDIOC_DQBUF = 0xc0585611;
    public const uint VIDIOC_STREAMON = 0x40045612;
    public const uint VIDIOC_STREAMOFF = 0x40045613;

    /// <summary>
    /// Asserts our managed structs match the kernel's, before any ioctl runs. A mismatch here
    /// would otherwise surface as a wrong ioctl code or silently garbled fields.
    /// </summary>
    public static void VerifyLayouts()
    {
        Check<V4l2Capability>(104, "v4l2_capability");
        Check<V4l2Format>(208, "v4l2_format");
        Check<V4l2PixFormat>(48, "v4l2_pix_format");
        Check<V4l2RequestBuffers>(20, "v4l2_requestbuffers");
        Check<V4l2Buffer>(88, "v4l2_buffer");

        static void Check<T>(int expected, string name)
        {
            int actual = Marshal.SizeOf<T>();
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"{typeof(T).Name} is {actual} bytes but the kernel's {name} is {expected}. " +
                    "The struct layout does not match linux/videodev2.h.");
            }
        }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close")]
    public static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    public static partial int Ioctl(int fd, nuint request, IntPtr arg);

    [LibraryImport("libc", EntryPoint = "mmap", SetLastError = true)]
    public static partial IntPtr Mmap(IntPtr addr, nuint length, int prot, int flags, int fd, nint offset);

    [LibraryImport("libc", EntryPoint = "munmap")]
    public static partial int Munmap(IntPtr addr, nuint length);

    /// <summary>Waits for a frame with a timeout, so an unplugged camera cannot block forever.</summary>
    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    public static partial int Poll([In, Out] PollFd[] fds, nuint nfds, int timeoutMs);

    public const short POLLIN = 0x001;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    /// <summary>ioctl is interrupted by signals; V4L2 callers are expected to retry on EINTR.</summary>
    public static int IoctlRetry(int fd, uint request, IntPtr arg)
    {
        int result;
        do
        {
            result = Ioctl(fd, request, arg);
        }
        while (result == -1 && Marshal.GetLastPInvokeError() == 4 /* EINTR */);

        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct V4l2Capability
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Driver;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Card;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] BusInfo;
        public uint Version;
        public uint Capabilities;
        public uint DeviceCaps;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public uint[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct V4l2PixFormat
    {
        public uint Width;
        public uint Height;
        public uint PixelFormat;
        public uint Field;
        public uint BytesPerLine;
        public uint SizeImage;
        public uint Colorspace;
        public uint Priv;
        public uint Flags;
        public uint YcbcrEnc;
        public uint Quantization;
        public uint XferFunc;
    }

    /// <summary>
    /// v4l2_format = { __u32 type; union { ... __u8 raw_data[200]; } fmt; }, total 208 bytes.
    ///
    /// The union is 8-byte aligned (v4l2_window contains pointers), so fmt sits at offset 8,
    /// not 4 — hence the explicit pad. Getting this wrong makes sizeof 204, which yields the
    /// wrong VIDIOC_S_FMT code and an ENOTTY that looks nothing like a layout bug.
    /// We only use the pix member, so the rest of the union is padding.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct V4l2Format
    {
        public uint Type;
        public uint UnionAlignmentPad;
        public V4l2PixFormat Pix;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 200 - 48)] public byte[] Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct V4l2RequestBuffers
    {
        public uint Count;
        public uint Type;
        public uint Memory;
        public uint Capabilities;
        public byte Flags;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public byte[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct V4l2Timeval
    {
        public nint Sec;
        public nint Usec;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct V4l2Timecode
    {
        public uint Type;
        public uint Flags;
        public byte Frames;
        public byte Seconds;
        public byte Minutes;
        public byte Hours;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] Userbits;
    }

    /// <summary>
    /// v4l2_buffer. The m union (offset/userptr/planes/fd) is modelled as a single nuint:
    /// for MMAP we only read m.offset, and the union's largest member is pointer-sized.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct V4l2Buffer
    {
        public uint Index;
        public uint Type;
        public uint BytesUsed;
        public uint Flags;
        public uint Field;
        public V4l2Timeval Timestamp;
        public V4l2Timecode Timecode;
        public uint Sequence;
        public uint Memory;
        public nuint M;
        public uint Length;
        public uint Reserved2;
        public uint Reserved;
    }
}
