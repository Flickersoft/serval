namespace Serval.Ai;

/// <summary>
/// Holds the most recent camera frames as JPEG, for opportunistic use by the vision path.
///
/// A short history rather than a single latest-frame slot, because that is what makes movement
/// describable: one image can only say what is *there*, never what is *happening*.
///
/// Deliberately free of any capture-library dependency, so the frame hand-off compiles on every
/// target. Capture itself is each host's business — the edge module reads MJPEG straight from
/// V4L2 with raw ioctls, the Server taps the snapshot fan-out its ffmpeg sessions already publish.
///
/// One instance per camera. Safe for concurrent readers and a single writer.
/// </summary>
public sealed class FrameRing
{
    private readonly object _gate = new();
    private readonly VisionFrame?[] _frames;
    private int _head;
    private int _count;

    /// <param name="capacity">
    /// How many frames to retain. Sized from <see cref="VisionOptions.MaxFrames"/> — there is no
    /// point holding frames no runner can be shown.
    /// </param>
    public FrameRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _frames = new VisionFrame?[capacity];
    }

    public void Add(VisionFrame frame)
    {
        lock (_gate)
        {
            _frames[_head] = frame;
            _head = (_head + 1) % _frames.Length;
            _count = Math.Min(_count + 1, _frames.Length);
        }
    }

    public void Add(byte[] jpeg) => Add(new VisionFrame(jpeg, DateTimeOffset.UtcNow));

    /// <summary>
    /// Up to <paramref name="wanted"/> of the most recent frames, oldest first — the order the
    /// vision runners expect, so "what changed between them" reads forwards in time.
    ///
    /// Returns fewer than asked for when fewer have been captured, and an empty list when none
    /// have. A caller must handle the short case rather than wait: shortly after startup one frame
    /// is all there is, and describing a still beats describing nothing.
    /// </summary>
    public IReadOnlyList<VisionFrame> Recent(int wanted)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(wanted);

        lock (_gate)
        {
            int take = Math.Min(wanted, _count);
            if (take == 0)
            {
                return [];
            }

            var result = new VisionFrame[take];
            for (int i = 0; i < take; i++)
            {
                // Walk back from the newest, filling forwards so the result comes out oldest-first.
                int index = (_head - take + i + _frames.Length) % _frames.Length;
                result[i] = _frames[index]!;
            }

            return result;
        }
    }
}
