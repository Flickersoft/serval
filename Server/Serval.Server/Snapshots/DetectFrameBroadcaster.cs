using System.Buffers;

namespace Serval.Server.Snapshots;

/// <summary>
/// One camera's raw frame, as planar yuv420p, tagged with when it was captured.
///
/// <para><see cref="Buffer"/> is rented from <see cref="ArrayPool{T}"/> and is therefore usually
/// longer than <see cref="Length"/> — read through <see cref="Luma"/> and <see cref="Chroma"/> rather
/// than the array, and never hold a reference past <see cref="Return"/>. A frame at 1280x720 is
/// 1.38 MB and they arrive several times a second per camera, which is an allocation rate nothing
/// else in the Server approaches; pooling is what keeps that off the gen-2 heap.</para>
///
/// <para>Deliberately not <c>Snapshot</c>. That is an encoded JPEG for the dashboard, the still
/// endpoint and the vision model, published once a second and freely retained by whoever receives it.
/// This is unencoded, short-lived, and exists only for detection.</para>
/// </summary>
public sealed class DetectFrame
{
    /// <summary>
    /// Takes a frame's worth of buffer from the pool, for a caller about to fill it.
    ///
    /// The only way to make one, so that <see cref="Return"/> can never be handed an array the pool
    /// does not own. A constructor taking any array would compile just as happily and fail at
    /// runtime, in the pool, a long way from whoever passed it.
    /// </summary>
    public static DetectFrame Rent(
        string cameraId, int width, int height, DateTimeOffset capturedAt)
    {
        int length = width * height * 3 / 2;

        return new DetectFrame
        {
            CameraId = cameraId,
            Buffer = ArrayPool<byte>.Shared.Rent(length),
            Length = length,
            Width = width,
            Height = height,
            CapturedAt = capturedAt,
        };
    }

    private DetectFrame()
    {
    }

    public string CameraId { get; private init; } = string.Empty;

    /// <summary>
    /// The frame's bytes. Rented, so usually longer than <see cref="Length"/> — read through
    /// <see cref="Luma"/>, <see cref="Chroma"/> or <see cref="Pixels"/> rather than the array.
    /// </summary>
    public byte[] Buffer { get; private init; } = [];

    public int Length { get; private init; }

    public int Width { get; private init; }

    public int Height { get; private init; }

    public DateTimeOffset CapturedAt { get; private init; }

    /// <summary>The frame's bytes, and only the frame's — the writable view a producer fills.</summary>
    public Span<byte> Pixels => Buffer.AsSpan(0, Length);

    /// <summary>
    /// The luma plane: one 8-bit sample per pixel, full resolution.
    ///
    /// This is exactly the grayscale buffer motion detection compares, which is the reason frames are
    /// carried as yuv420p rather than RGB — that half of the pipeline reads it with no conversion at
    /// all, and never touches chroma.
    /// </summary>
    public ReadOnlySpan<byte> Luma => Buffer.AsSpan(0, Width * Height);

    /// <summary>The U and V planes, each at half resolution in both axes, U first.</summary>
    public ReadOnlySpan<byte> Chroma => Buffer.AsSpan(Width * Height, Width * Height / 2);

    /// <summary>Hands the buffer back to the pool. Called by whoever last held the frame.</summary>
    public void Return() => ArrayPool<byte>.Shared.Return(Buffer);
}

/// <summary>
/// Carries raw detect frames from a camera's ingest session to its detection pipeline.
///
/// <para><b>Latest-wins, one slot per camera.</b> A newly published frame displaces whatever was
/// waiting, and the displaced buffer goes straight back to the pool. That is not a compromise: a
/// detector that has fallen behind gains nothing from the frame it missed and everything from the one
/// in front of it, and a queue of stale pictures would only add latency to every subsequent frame.
/// <see cref="Ingest.DetectFrameReader"/> applies the same rule upstream on the files themselves.</para>
///
/// <para><b>One subscriber per camera.</b> Unlike <see cref="SnapshotBroadcaster"/>, which fans one
/// frame out to every connected dashboard, these buffers are pooled and owned — two subscribers would
/// mean two claims on one array and a return while the other was still reading it. Subscribing twice
/// to a camera is a programming error and throws rather than corrupting a frame.</para>
/// </summary>
public sealed class DetectFrameBroadcaster
{
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private long _superseded;

    /// <summary>
    /// Frames that were displaced by a newer one before anything read them.
    ///
    /// Ordinary at a low frame rate and worth counting anyway: this is one of the places accuracy is
    /// quietly traded for keeping up, and a server that is dropping most of its frames here looks
    /// identical to one that is not until somebody can see the number.
    /// </summary>
    public long Superseded => Interlocked.Read(ref _superseded);

    /// <summary>
    /// Frames discarded before they were ever published, because the reader had fallen far enough
    /// behind to be past its backlog.
    ///
    /// Reported here rather than by the reader so that everything counting the same loss — a moment
    /// nothing looked at — accumulates in one place, whichever stage happened to drop it.
    /// </summary>
    public void Dropped(int count) => Interlocked.Add(ref _superseded, count);

    /// <summary>
    /// Whether anything is listening for this camera's frames.
    ///
    /// Asked before a frame is read off disk rather than after: ingest produces these whenever
    /// <c>Ingest:DetectFps</c> is positive, but a host with no detection model, or a camera with AI
    /// switched off, has nothing that wants them. Discarding the file unread is the difference
    /// between that costing a directory sweep and costing a megabyte of copying per frame.
    /// </summary>
    public bool HasSubscriber(string cameraId)
    {
        lock (_gate)
        {
            return _slots.ContainsKey(cameraId);
        }
    }

    /// <summary>
    /// Offers a frame to this camera's subscriber, taking ownership of its buffer.
    ///
    /// With no subscriber the frame is returned to the pool immediately — a camera can be ingesting
    /// while its AI session is stopped or restarting, and those frames must not accumulate.
    /// </summary>
    public void Publish(DetectFrame frame)
    {
        Slot? slot;
        lock (_gate)
        {
            _slots.TryGetValue(frame.CameraId, out slot);
        }

        if (slot is null)
        {
            frame.Return();
            return;
        }

        if (slot.Offer(frame))
        {
            Interlocked.Increment(ref _superseded);
        }
    }

    /// <summary>
    /// Subscribes to one camera's frames. Dispose the handle to unsubscribe; anything still waiting
    /// is returned to the pool at that point.
    /// </summary>
    /// <exception cref="InvalidOperationException">This camera already has a subscriber.</exception>
    public Subscription Subscribe(string cameraId)
    {
        var slot = new Slot();

        lock (_gate)
        {
            if (!_slots.TryAdd(cameraId, slot))
            {
                throw new InvalidOperationException(
                    $"Camera '{cameraId}' already has a detect-frame subscriber. These frames carry "
                    + "pooled buffers with a single owner, so they cannot be fanned out.");
            }
        }

        return new Subscription(this, cameraId, slot);
    }

    private void Unsubscribe(string cameraId, Slot slot)
    {
        lock (_gate)
        {
            if (_slots.TryGetValue(cameraId, out Slot? current) && ReferenceEquals(current, slot))
            {
                _slots.Remove(cameraId);
            }
        }

        slot.Drain();
    }

    /// <summary>
    /// One camera's pending frame, and the signal that one is there.
    ///
    /// A single slot rather than a channel because the buffer has an owner: a bounded channel's
    /// drop-oldest mode discards silently, with no chance to return what it dropped, so every dropped
    /// frame would be a leaked megabyte.
    /// </summary>
    internal sealed class Slot
    {
        private readonly SemaphoreSlim _signal = new(0, 1);
        private readonly Lock _gate = new();
        private DetectFrame? _pending;

        /// <returns>True when this displaced a frame nothing had read yet.</returns>
        public bool Offer(DetectFrame frame)
        {
            DetectFrame? displaced;
            lock (_gate)
            {
                displaced = _pending;
                _pending = frame;
            }

            displaced?.Return();

            // Only ever one outstanding wake-up: the reader takes whatever is in the slot when it
            // gets there, which is by definition the newest frame, so a second signal would only
            // send it back to an empty slot.
            if (_signal.CurrentCount == 0)
            {
                try
                {
                    _signal.Release();
                }
                catch (SemaphoreFullException)
                {
                    // Raced another publish; the reader is already awake.
                }
            }

            return displaced is not null;
        }

        public async Task<DetectFrame> ReadAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                await _signal.WaitAsync(cancellationToken);

                lock (_gate)
                {
                    if (_pending is { } frame)
                    {
                        _pending = null;
                        return frame;
                    }
                }
            }
        }

        public void Drain()
        {
            DetectFrame? left;
            lock (_gate)
            {
                left = _pending;
                _pending = null;
            }

            left?.Return();
        }
    }

    public sealed class Subscription : IDisposable
    {
        private readonly DetectFrameBroadcaster _owner;
        private readonly string _cameraId;
        private readonly Slot _slot;
        private bool _disposed;

        internal Subscription(DetectFrameBroadcaster owner, string cameraId, Slot slot)
        {
            _owner = owner;
            _cameraId = cameraId;
            _slot = slot;
        }

        /// <summary>
        /// Waits for the next frame. The caller owns the returned frame's buffer and must
        /// <see cref="DetectFrame.Return"/> it once done.
        /// </summary>
        public Task<DetectFrame> ReadAsync(CancellationToken cancellationToken) =>
            _slot.ReadAsync(cancellationToken);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.Unsubscribe(_cameraId, _slot);
        }
    }
}
