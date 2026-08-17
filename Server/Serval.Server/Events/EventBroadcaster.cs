using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Serval.Server.Events;

/// <summary>
/// A live AI event on its way to the App: the same document the ingest endpoint stored,
/// wrapped so the App learns which camera and record type it is without inspecting the payload.
/// </summary>
public sealed record LiveEvent(string CameraId, string Type, object Document);

/// <summary>
/// In-process pub/sub for AI telemetry: the ingest endpoint publishes each freshly-stored
/// record, and every connected App WebSocket receives it. Durable history lives in Mongo; this
/// is only the "as it happens" push.
///
/// <para><b>Two lanes, because two unlike things travel here.</b> Most of what is published is a
/// <i>state transition</i> — an utterance was said, a scene was described, a detection episode
/// closed — sent exactly once, with nothing later that repeats it. Open detection episodes also
/// publish a <i>position heartbeat</i> once per detection frame each, saying where the thing is
/// now; each supersedes the one before it, so losing one costs nothing and the next repairs
/// it.</para>
///
/// <para><b>Why they cannot share a queue.</b> Drop-oldest is the right discipline for
/// superseding samples and the wrong one for state, and mixing them applies the sampling policy
/// to both. Worse, it applies it to the state <i>first</i>: heartbeats are almost all the volume,
/// so they are what fills the queue, but eviction takes from the head — and a close published
/// twenty seconds ago is older than every heartbeat published since. The one message that cannot
/// be lost was preferentially the first one dropped, and a lost close left the App drawing a box
/// over an object that had gone, for the rest of its session, with no reconnect to heal it
/// because the socket had never actually broken.</para>
///
/// <para>So a slow viewer still loses events rather than blocking ingest — it loses positions.
/// Same drop-oldest discipline as the snapshot fan-out, now applied only to the traffic that can
/// afford it.</para>
/// </summary>
public sealed class EventBroadcaster(ILogger<EventBroadcaster>? logger = null)
{
    /// <summary>
    /// Positions in flight per subscriber. A few seconds of them, which is all a position is
    /// worth: anything older is superseded by definition.
    /// </summary>
    private const int DroppableCapacity = 128;

    /// <summary>
    /// State transitions in flight per subscriber. At the handful a minute a house produces this
    /// is hours of backlog, so reaching it means the reader is gone rather than slow — which is
    /// why crossing it is logged rather than absorbed in silence.
    /// </summary>
    private const int DurableCapacity = 1024;

    private readonly ConcurrentDictionary<Guid, Lanes> _subscribers = new();

    /// <summary>
    /// Sends an event to every subscriber.
    ///
    /// <para><paramref name="droppable"/> says this is a position heartbeat: superseded by the
    /// next one, and free to lose. It defaults to false so that a new kind of event is guaranteed
    /// delivery unless someone has thought about it — the failure mode of the wrong default is
    /// silent and long-lived, and the cost of the safe one is a queue slot.</para>
    /// </summary>
    public void Publish(LiveEvent liveEvent, bool droppable = false)
    {
        foreach (Lanes lanes in _subscribers.Values)
        {
            lanes.Write(liveEvent, droppable, logger);
        }
    }

    public Subscription Subscribe()
    {
        var lanes = new Lanes();
        var id = Guid.NewGuid();
        _subscribers[id] = lanes;
        return new Subscription(this, id, lanes.Durable.Reader, lanes.Droppable.Reader);
    }

    private void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out Lanes? lanes))
        {
            lanes.Complete();
        }
    }

    private sealed class Lanes
    {
        public readonly Channel<LiveEvent> Durable = Channel.CreateBounded<LiveEvent>(
            new BoundedChannelOptions(DurableCapacity)
            {
                // Drop-oldest here too, not drop-write: a reader this far behind will be cut loose
                // anyway, and if it does come back the newest state is what it needs. Losing the
                // freshest close to preserve a twenty-minute-old one would be the wrong half.
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        public readonly Channel<LiveEvent> Droppable = Channel.CreateBounded<LiveEvent>(
            new BoundedChannelOptions(DroppableCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        /// <summary>Whether the durable lane is currently full, so saturation is logged once per
        /// episode rather than once per evicted event.</summary>
        private int _saturated;

        public void Write(LiveEvent liveEvent, bool droppable, ILogger? logger)
        {
            if (droppable)
            {
                Droppable.Writer.TryWrite(liveEvent);
                return;
            }

            // TryWrite reports success even when it evicted something to make room, so saturation
            // is read off the depth beforehand rather than from the write's result.
            bool full = Durable.Reader.CanCount && Durable.Reader.Count >= DurableCapacity;

            if (full && Interlocked.Exchange(ref _saturated, 1) == 0)
            {
                logger?.LogWarning(
                    "An App event subscriber is {Depth} events behind; state events are now being "
                    + "dropped for it. Its view of open detections and recent records will be "
                    + "incomplete until it reconnects.",
                    DurableCapacity);
            }
            else if (!full)
            {
                Interlocked.Exchange(ref _saturated, 0);
            }

            Durable.Writer.TryWrite(liveEvent);
        }

        public void Complete()
        {
            Durable.Writer.TryComplete();
            Droppable.Writer.TryComplete();
        }
    }

    public sealed class Subscription : IDisposable
    {
        private readonly EventBroadcaster _owner;
        private readonly Guid _id;

        internal Subscription(
            EventBroadcaster owner,
            Guid id,
            ChannelReader<LiveEvent> durable,
            ChannelReader<LiveEvent> droppable)
        {
            _owner = owner;
            _id = id;
            Durable = durable;
            Droppable = droppable;
        }

        /// <summary>State transitions. Drain this first — that priority is half the point of the
        /// split, since a close queued behind a hundred positions is a close delivered late.</summary>
        public ChannelReader<LiveEvent> Durable { get; }

        /// <summary>Position heartbeats for open detection episodes.</summary>
        public ChannelReader<LiveEvent> Droppable { get; }

        public void Dispose() => _owner.Unsubscribe(_id);
    }
}
