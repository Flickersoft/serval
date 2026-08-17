using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Serval.Server.Ai;

/// <summary>
/// One camera's measured input level, with the thresholds it is being compared against.
///
/// The thresholds ride along with the reading rather than being fetched separately, and that is
/// the point of the type. A client drawing "is this camera's speech loud enough to be heard"
/// needs the line as well as the bar, and it cannot derive the line: a camera that overrides
/// nothing inherits a server default the App has never seen, and no endpoint exposes the
/// <c>Serval:Ai</c> section. Sending them makes the feed self-describing and keeps the meter
/// honest for an untuned camera.
/// </summary>
/// <param name="Rms">Mean level over the publish interval — the body of the bar.</param>
/// <param name="Peak">
/// Loudest single window since the last publish. Held rather than averaged because a mean over
/// 100 ms hides the transient that actually opens the gate, and "does my speech cross the line"
/// is the only question this feed exists to answer.
/// </param>
public sealed record AudioLevel(
    string CameraId,
    DateTimeOffset At,
    float Rms,
    float Peak,
    float SpeechThreshold,
    float SoundThreshold,
    bool SpeechGateOpen,
    bool SoundGateOpen);

/// <summary>
/// Fan-out for the camera settings screen's level meter.
///
/// Keyed only, unlike <see cref="Snapshots.SnapshotBroadcaster"/>: there is no every-camera
/// subscription because nothing wants one, and no cached latest reading because a level from a
/// second ago is a lie rather than a stale-but-useful frame.
///
/// <para><see cref="HasSubscribers"/> is what makes the feature free when nobody is looking. The
/// detector checks it before measuring, so a camera nobody is tuning pays nothing — no RMS pass,
/// no allocation, no timestamp, thirty-one times a second. That also makes a leaked subscription a
/// correctness problem and not merely waste, which is why <see cref="Subscription"/> is disposable
/// and the endpoint is built to guarantee it runs.</para>
/// </summary>
public sealed class AudioLevelBroadcaster
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<AudioLevel>>>
        _keyed = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether anyone is watching this camera's level.
    ///
    /// Tests <see cref="ConcurrentDictionary{TKey,TValue}.IsEmpty"/> and not merely presence,
    /// because <see cref="Unsubscribe"/> leaves emptied buckets in place — removing one races with
    /// a concurrent subscribe to the same camera. Without the emptiness check the first viewer a
    /// camera ever had would leave it measured for the lifetime of the process.
    /// </summary>
    public bool HasSubscribers(string cameraId) =>
        _keyed.TryGetValue(cameraId, out ConcurrentDictionary<Guid, Channel<AudioLevel>>? forCamera)
        && !forCamera.IsEmpty;

    public void Publish(AudioLevel level)
    {
        if (_keyed.TryGetValue(level.CameraId, out ConcurrentDictionary<Guid, Channel<AudioLevel>>? forCamera))
        {
            foreach (Channel<AudioLevel> channel in forCamera.Values)
            {
                // TryWrite never blocks the detection thread — the one thread draining a
                // four-second ring buffer is the last place to apply backpressure from a viewer.
                channel.Writer.TryWrite(level);
            }
        }
    }

    /// <summary>
    /// Watch one camera's level. Dispose the handle to stop — leaking one keeps the camera being
    /// measured for a viewer that is no longer there.
    /// </summary>
    public Subscription Subscribe(string cameraId)
    {
        // Capacity 4 rather than the snapshot broadcaster's 32: a meter has no history worth
        // buffering, and a reader far enough behind to fill even this wants the newest reading
        // rather than a queue of old ones.
        var channel = Channel.CreateBounded<AudioLevel>(new BoundedChannelOptions(capacity: 4)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        var id = Guid.NewGuid();
        _keyed.GetOrAdd(cameraId, _ => new ConcurrentDictionary<Guid, Channel<AudioLevel>>())[id] = channel;
        return new Subscription(this, id, cameraId, channel.Reader);
    }

    private void Unsubscribe(Guid id, string cameraId)
    {
        Channel<AudioLevel>? channel = null;

        if (_keyed.TryGetValue(cameraId, out ConcurrentDictionary<Guid, Channel<AudioLevel>>? forCamera))
        {
            forCamera.TryRemove(id, out channel);
        }

        // Completing the writer is what ends a reader still sitting in ReadAllAsync, so a session
        // torn down from this side does not need the cancellation token to have fired.
        channel?.Writer.TryComplete();
    }

    public sealed class Subscription : IDisposable
    {
        private readonly AudioLevelBroadcaster _owner;
        private readonly Guid _id;
        private readonly string _cameraId;

        internal Subscription(
            AudioLevelBroadcaster owner, Guid id, string cameraId, ChannelReader<AudioLevel> reader)
        {
            _owner = owner;
            _id = id;
            _cameraId = cameraId;
            Reader = reader;
        }

        public ChannelReader<AudioLevel> Reader { get; }

        public void Dispose() => _owner.Unsubscribe(_id, _cameraId);
    }
}
