using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Serval.Server.Snapshots;

/// <summary>One camera's latest still, as JPEG bytes, tagged with when it was taken.</summary>
public sealed record Snapshot(string CameraId, byte[] Jpeg, DateTimeOffset CapturedAt);

/// <summary>
/// The dashboard's fan-out point. Ingest sessions publish ~1 fps snapshots here; each
/// connected dashboard WebSocket subscribes and receives every camera's frames over one
/// socket. It also keeps the newest frame per camera so a freshly-connected client and the
/// single-still endpoint don't have to wait up to a second for the next push.
///
/// Subscribers get a bounded, drop-oldest channel: a slow client falls behind on frames
/// rather than applying backpressure to ingest — a lagging viewer must never stall capture.
///
/// A subscriber can also ask for a single camera. That is not a convenience: the bound is 32
/// frames, so an every-camera subscriber interested in one of them burns its buffer on the other
/// N-1 and has only 32/N seconds of slack before it starts dropping the frames it actually wants.
/// Server-side vision subscribes per camera for exactly that reason.
/// </summary>
public sealed class SnapshotBroadcaster
{
    private readonly ConcurrentDictionary<string, Snapshot> _latest = new();
    private readonly ConcurrentDictionary<Guid, Channel<Snapshot>> _subscribers = new();

    /// <summary>
    /// Per-camera subscribers, so a publish touches only the channels that want that camera
    /// rather than every channel. Emptied buckets are left in place: removing one races with a
    /// concurrent subscribe to the same camera, and the set is bounded by the camera registry.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<Snapshot>>>
        _keyed = new(StringComparer.Ordinal);

    /// <summary>Newest frame for a camera, or null if none has arrived yet.</summary>
    public Snapshot? Latest(string cameraId) =>
        _latest.TryGetValue(cameraId, out Snapshot? snapshot) ? snapshot : null;

    /// <summary>Newest frame for every camera, to paint a dashboard the instant it connects.</summary>
    public IReadOnlyCollection<Snapshot> AllLatest() => _latest.Values.ToArray();

    public void Publish(Snapshot snapshot)
    {
        _latest[snapshot.CameraId] = snapshot;

        foreach (Channel<Snapshot> channel in _subscribers.Values)
        {
            // DropOldest bounds memory; TryWrite never blocks the ingest thread.
            channel.Writer.TryWrite(snapshot);
        }

        if (_keyed.TryGetValue(snapshot.CameraId, out ConcurrentDictionary<Guid, Channel<Snapshot>>? forCamera))
        {
            foreach (Channel<Snapshot> channel in forCamera.Values)
            {
                channel.Writer.TryWrite(snapshot);
            }
        }
    }

    /// <summary>Drops a camera's cached frame when it stops being ingested.</summary>
    public void Forget(string cameraId) => _latest.TryRemove(cameraId, out _);

    /// <summary>
    /// Subscribe to every camera's frames. Dispose the returned handle to unsubscribe; leaking
    /// one would grow the fan-out set with a channel nobody drains.
    /// </summary>
    public Subscription Subscribe()
    {
        Channel<Snapshot> channel = CreateChannel();
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return new Subscription(this, id, cameraId: null, channel.Reader);
    }

    /// <summary>
    /// Subscribe to one camera's frames. Prefer this whenever only one camera is of interest —
    /// the buffer then holds 32 of that camera's frames rather than 32 of everyone's.
    /// </summary>
    public Subscription Subscribe(string cameraId)
    {
        Channel<Snapshot> channel = CreateChannel();
        var id = Guid.NewGuid();
        _keyed.GetOrAdd(cameraId, _ => new ConcurrentDictionary<Guid, Channel<Snapshot>>())[id] = channel;
        return new Subscription(this, id, cameraId, channel.Reader);
    }

    private static Channel<Snapshot> CreateChannel() =>
        Channel.CreateBounded<Snapshot>(new BoundedChannelOptions(capacity: 32)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private void Unsubscribe(Guid id, string? cameraId)
    {
        Channel<Snapshot>? channel;

        if (cameraId is null)
        {
            _subscribers.TryRemove(id, out channel);
        }
        else
        {
            channel = null;
            if (_keyed.TryGetValue(cameraId, out ConcurrentDictionary<Guid, Channel<Snapshot>>? forCamera))
            {
                forCamera.TryRemove(id, out channel);
            }
        }

        channel?.Writer.TryComplete();
    }

    public sealed class Subscription : IDisposable
    {
        private readonly SnapshotBroadcaster _owner;
        private readonly Guid _id;
        private readonly string? _cameraId;

        internal Subscription(
            SnapshotBroadcaster owner, Guid id, string? cameraId, ChannelReader<Snapshot> reader)
        {
            _owner = owner;
            _id = id;
            _cameraId = cameraId;
            Reader = reader;
        }

        public ChannelReader<Snapshot> Reader { get; }

        public void Dispose() => _owner.Unsubscribe(_id, _cameraId);
    }
}
