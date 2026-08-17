using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Serval.CameraModule;

/// <summary>
/// Captures MJPEG frames from a V4L2 device using the mmap streaming path.
///
/// Frames come off the camera already JPEG-encoded, which is exactly what the vision model
/// wants — no decode, no re-encode, no image library. This replaces OpenCvSharp, which has
/// no linux-arm64 runtime and so could not run on the Orange Pi at all.
///
/// The device must support MJPEG. Cameras that only emit YUYV are rejected loudly rather
/// than silently producing something the model cannot read.
/// </summary>
public sealed class V4l2MjpegCamera : IDisposable
{
    private const int BufferCount = 4;

    private readonly ILogger _logger;
    private readonly int _fd;
    private readonly IntPtr[] _buffers = new IntPtr[BufferCount];
    private readonly nuint[] _bufferLengths = new nuint[BufferCount];
    private bool _streaming;
    private bool _disposed;

    public uint Width { get; }

    public uint Height { get; }

    public V4l2MjpegCamera(string devicePath, uint width, uint height, ILogger logger)
    {
        _logger = logger;

        // Fail on a layout mismatch before issuing any ioctl, where the error is legible.
        V4l2.VerifyLayouts();

        _fd = V4l2.Open(devicePath, V4l2.O_RDWR);
        if (_fd < 0)
        {
            throw new IOException(
                $"Cannot open {devicePath} (errno {Marshal.GetLastPInvokeError()}). " +
                "Check the device exists and the user is in the 'video' group.");
        }

        try
        {
            VerifyCapabilities(devicePath);
            (Width, Height) = SetFormat(devicePath, width, height);
            AllocateBuffers();
            StartStreaming();
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    private void VerifyCapabilities(string devicePath)
    {
        var caps = new V4l2.V4l2Capability
        {
            Driver = new byte[16],
            Card = new byte[32],
            BusInfo = new byte[32],
            Reserved = new uint[3],
        };

        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<V4l2.V4l2Capability>());
        try
        {
            Marshal.StructureToPtr(caps, ptr, false);
            if (V4l2.IoctlRetry(_fd, V4l2.VIDIOC_QUERYCAP, ptr) < 0)
            {
                throw new IOException($"VIDIOC_QUERYCAP failed on {devicePath}: not a V4L2 device?");
            }

            caps = Marshal.PtrToStructure<V4l2.V4l2Capability>(ptr);

            // DeviceCaps describes this specific node; Capabilities describes the whole device.
            uint effective = caps.DeviceCaps != 0 ? caps.DeviceCaps : caps.Capabilities;

            if ((effective & V4l2.V4L2_CAP_VIDEO_CAPTURE) == 0)
            {
                throw new IOException($"{devicePath} does not support video capture.");
            }

            if ((effective & V4l2.V4L2_CAP_STREAMING) == 0)
            {
                throw new IOException($"{devicePath} does not support streaming I/O (mmap).");
            }

            string card = Encoding(caps.Card);
            _logger.LogInformation("Camera: {Card} on {Device}", card, devicePath);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private (uint Width, uint Height) SetFormat(string devicePath, uint width, uint height)
    {
        var format = new V4l2.V4l2Format
        {
            Type = V4l2.V4L2_BUF_TYPE_VIDEO_CAPTURE,
            Pix = new V4l2.V4l2PixFormat
            {
                Width = width,
                Height = height,
                PixelFormat = V4l2.V4L2_PIX_FMT_MJPEG,
                Field = 1, // V4L2_FIELD_NONE
            },
            Padding = new byte[200 - 48],
        };

        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<V4l2.V4l2Format>());
        try
        {
            Marshal.StructureToPtr(format, ptr, false);
            if (V4l2.IoctlRetry(_fd, V4l2.VIDIOC_S_FMT, ptr) < 0)
            {
                throw new IOException(
                    $"VIDIOC_S_FMT failed on {devicePath} (errno {Marshal.GetLastPInvokeError()}).");
            }

            format = Marshal.PtrToStructure<V4l2.V4l2Format>(ptr);

            // The driver rewrites the struct with what it actually accepted. If it fell back
            // to another format, the "JPEG" bytes would be raw pixels and the model would
            // silently receive garbage — so fail instead.
            if (format.Pix.PixelFormat != V4l2.V4L2_PIX_FMT_MJPEG)
            {
                throw new IOException(
                    $"{devicePath} rejected MJPEG and chose fourcc 0x{format.Pix.PixelFormat:X8} instead. " +
                    "This camera cannot deliver JPEG frames directly.");
            }

            if (format.Pix.Width != width || format.Pix.Height != height)
            {
                _logger.LogInformation(
                    "Camera adjusted resolution to {Width}x{Height} (requested {ReqW}x{ReqH}).",
                    format.Pix.Width, format.Pix.Height, width, height);
            }

            return (format.Pix.Width, format.Pix.Height);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private void AllocateBuffers()
    {
        var request = new V4l2.V4l2RequestBuffers
        {
            Count = BufferCount,
            Type = V4l2.V4L2_BUF_TYPE_VIDEO_CAPTURE,
            Memory = V4l2.V4L2_MEMORY_MMAP,
            Reserved = new byte[3],
        };

        IntPtr reqPtr = Marshal.AllocHGlobal(Marshal.SizeOf<V4l2.V4l2RequestBuffers>());
        try
        {
            Marshal.StructureToPtr(request, reqPtr, false);
            if (V4l2.IoctlRetry(_fd, V4l2.VIDIOC_REQBUFS, reqPtr) < 0)
            {
                throw new IOException($"VIDIOC_REQBUFS failed (errno {Marshal.GetLastPInvokeError()}).");
            }

            request = Marshal.PtrToStructure<V4l2.V4l2RequestBuffers>(reqPtr);
            if (request.Count < 2)
            {
                throw new IOException($"Camera granted only {request.Count} buffers; need at least 2.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(reqPtr);
        }

        for (uint i = 0; i < BufferCount; i++)
        {
            var buffer = NewBuffer(i);
            IntPtr bufPtr = Marshal.AllocHGlobal(Marshal.SizeOf<V4l2.V4l2Buffer>());
            try
            {
                Marshal.StructureToPtr(buffer, bufPtr, false);
                if (V4l2.IoctlRetry(_fd, V4l2.VIDIOC_QUERYBUF, bufPtr) < 0)
                {
                    throw new IOException($"VIDIOC_QUERYBUF failed for buffer {i}.");
                }

                buffer = Marshal.PtrToStructure<V4l2.V4l2Buffer>(bufPtr);

                IntPtr mapped = V4l2.Mmap(
                    IntPtr.Zero, buffer.Length, V4l2.PROT_READ | V4l2.PROT_WRITE,
                    V4l2.MAP_SHARED, _fd, (nint)buffer.M);

                if (mapped == new IntPtr(-1))
                {
                    throw new IOException($"mmap failed for buffer {i} (errno {Marshal.GetLastPInvokeError()}).");
                }

                _buffers[i] = mapped;
                _bufferLengths[i] = buffer.Length;
            }
            finally
            {
                Marshal.FreeHGlobal(bufPtr);
            }
        }

        for (uint i = 0; i < BufferCount; i++)
        {
            EnqueueBuffer(i);
        }
    }

    private void StartStreaming()
    {
        IntPtr typePtr = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(typePtr, (int)V4l2.V4L2_BUF_TYPE_VIDEO_CAPTURE);
            if (V4l2.IoctlRetry(_fd, V4l2.VIDIOC_STREAMON, typePtr) < 0)
            {
                throw new IOException($"VIDIOC_STREAMON failed (errno {Marshal.GetLastPInvokeError()}).");
            }

            _streaming = true;
        }
        finally
        {
            Marshal.FreeHGlobal(typePtr);
        }
    }

    /// <summary>
    /// Returns one JPEG frame, or null if none arrived within the timeout. The returned array
    /// is a copy: the underlying mmap buffer is handed straight back to the driver.
    /// </summary>
    public byte[]? ReadFrame(int timeoutMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var fds = new[] { new V4l2.PollFd { Fd = _fd, Events = V4l2.POLLIN } };
        int ready = V4l2.Poll(fds, 1, timeoutMs);

        if (ready < 0)
        {
            throw new IOException($"poll failed (errno {Marshal.GetLastPInvokeError()}).");
        }

        if (ready == 0)
        {
            return null;
        }

        var buffer = NewBuffer(0);
        IntPtr bufPtr = Marshal.AllocHGlobal(Marshal.SizeOf<V4l2.V4l2Buffer>());
        try
        {
            Marshal.StructureToPtr(buffer, bufPtr, false);
            if (V4l2.IoctlRetry(_fd, V4l2.VIDIOC_DQBUF, bufPtr) < 0)
            {
                throw new IOException($"VIDIOC_DQBUF failed (errno {Marshal.GetLastPInvokeError()}).");
            }

            buffer = Marshal.PtrToStructure<V4l2.V4l2Buffer>(bufPtr);

            var jpeg = new byte[buffer.BytesUsed];
            Marshal.Copy(_buffers[buffer.Index], jpeg, 0, (int)buffer.BytesUsed);

            // Hand the buffer back immediately; the driver needs it to keep streaming.
            EnqueueBuffer(buffer.Index);

            return jpeg;
        }
        finally
        {
            Marshal.FreeHGlobal(bufPtr);
        }
    }

    private void EnqueueBuffer(uint index)
    {
        var buffer = NewBuffer(index);
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<V4l2.V4l2Buffer>());
        try
        {
            Marshal.StructureToPtr(buffer, ptr, false);
            if (V4l2.IoctlRetry(_fd, V4l2.VIDIOC_QBUF, ptr) < 0)
            {
                throw new IOException($"VIDIOC_QBUF failed for buffer {index}.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// True if the buffer is a complete JPEG: starts with SOI and ends with EOI.
    ///
    /// UVC cameras genuinely emit truncated frames, especially the first few after
    /// STREAMON while exposure settles. A partial frame may still decode to a smeared or
    /// half-grey image, so it must be discarded rather than handed to the model — a wasted
    /// VLM inference costs 10-30s on the Pi, and a smeared frame yields a confident wrong
    /// description.
    /// </summary>
    public static bool IsCompleteJpeg(ReadOnlySpan<byte> data) =>
        data.Length > 4
        && data[0] == 0xFF && data[1] == 0xD8
        && data[^2] == 0xFF && data[^1] == 0xD9;

    private static V4l2.V4l2Buffer NewBuffer(uint index) => new()
    {
        Index = index,
        Type = V4l2.V4L2_BUF_TYPE_VIDEO_CAPTURE,
        Memory = V4l2.V4L2_MEMORY_MMAP,
        Timecode = new V4l2.V4l2Timecode { Userbits = new byte[4] },
    };

    private static string Encoding(byte[] raw)
    {
        int end = Array.IndexOf(raw, (byte)0);
        return System.Text.Encoding.ASCII.GetString(raw, 0, end < 0 ? raw.Length : end);
    }

    private void Cleanup()
    {
        if (_streaming)
        {
            IntPtr typePtr = Marshal.AllocHGlobal(sizeof(uint));
            try
            {
                Marshal.WriteInt32(typePtr, (int)V4l2.V4L2_BUF_TYPE_VIDEO_CAPTURE);
                V4l2.IoctlRetry(_fd, V4l2.VIDIOC_STREAMOFF, typePtr);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "VIDIOC_STREAMOFF failed during cleanup.");
            }
            finally
            {
                Marshal.FreeHGlobal(typePtr);
            }

            _streaming = false;
        }

        for (int i = 0; i < BufferCount; i++)
        {
            if (_buffers[i] != IntPtr.Zero && _buffers[i] != new IntPtr(-1))
            {
                V4l2.Munmap(_buffers[i], _bufferLengths[i]);
                _buffers[i] = IntPtr.Zero;
            }
        }

        if (_fd >= 0)
        {
            V4l2.Close(_fd);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Cleanup();
    }
}
