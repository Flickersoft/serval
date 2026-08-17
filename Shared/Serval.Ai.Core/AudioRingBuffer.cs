using System.Threading;

namespace Serval.Ai;

/// <summary>
/// Single-producer/single-consumer float ring buffer connecting an audio source to the VAD thread.
/// On the edge that source is the PortAudio realtime callback; on the Server it is the stdout of
/// an ffmpeg process decoding a camera's RTSP audio track.
///
/// The producer may be a realtime audio callback, so <see cref="Write"/> must not allocate,
/// lock, or block — anything that stalls it causes dropped audio. Capacity is rounded to
/// a power of two so indexing is a mask rather than a division.
///
/// On overrun we drop the incoming samples and count them, rather than overwriting the
/// oldest. Advancing the read cursor from the producer would race with the consumer;
/// dropping the newest is the only lock-free option, and a sustained nonzero
/// <see cref="DroppedSamples"/> means the VAD thread cannot keep up.
/// </summary>
public sealed class AudioRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _mask;

    // Monotonic cursors. Only the producer advances _writePos, only the consumer _readPos.
    // At 16 kHz a long takes ~18 million years to overflow.
    private long _writePos;
    private long _readPos;
    private long _droppedSamples;

    public AudioRingBuffer(int minimumCapacity)
    {
        int capacity = 1;
        while (capacity < minimumCapacity)
        {
            capacity <<= 1;
        }

        _buffer = new float[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _buffer.Length;

    public long DroppedSamples => Interlocked.Read(ref _droppedSamples);

    /// <summary>Producer side. Realtime-safe: no allocation, no locks. Returns false if samples were dropped.</summary>
    public bool Write(ReadOnlySpan<float> source)
    {
        long write = _writePos;
        long read = Volatile.Read(ref _readPos);
        int free = _buffer.Length - (int)(write - read);

        if (source.Length > free)
        {
            Interlocked.Add(ref _droppedSamples, source.Length);
            return false;
        }

        int index = (int)(write & _mask);
        int firstChunk = Math.Min(source.Length, _buffer.Length - index);
        source[..firstChunk].CopyTo(_buffer.AsSpan(index));
        if (firstChunk < source.Length)
        {
            source[firstChunk..].CopyTo(_buffer.AsSpan(0));
        }

        // Publish only after the data is in place, so the consumer never reads a torn window.
        Volatile.Write(ref _writePos, write + source.Length);
        return true;
    }

    /// <summary>Consumer side. Returns the number of samples copied, which may be fewer than requested.</summary>
    public int Read(Span<float> destination)
    {
        long read = _readPos;
        long write = Volatile.Read(ref _writePos);
        int count = Math.Min((int)(write - read), destination.Length);

        if (count <= 0)
        {
            return 0;
        }

        int index = (int)(read & _mask);
        int firstChunk = Math.Min(count, _buffer.Length - index);
        _buffer.AsSpan(index, firstChunk).CopyTo(destination);
        if (firstChunk < count)
        {
            _buffer.AsSpan(0, count - firstChunk).CopyTo(destination[firstChunk..]);
        }

        Volatile.Write(ref _readPos, read + count);
        return count;
    }
}
