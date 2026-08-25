using System.Collections.Concurrent;

namespace Serval.Server.Ingest;

/// <summary>
/// Lets the AI half ask for a camera's detect session to be started again.
///
/// The raw frames object detection runs on are written by an ingest session and read by
/// <see cref="Ai.CameraAiCoordinator"/>, which supervises sessions of its own and restarts them when
/// a reader falls silent. That is the right response to a consumer that has wedged and no response
/// at all to a producer that has stopped: re-subscribing cannot conjure frames nobody is writing.
/// This is how the consumer reaches the one thing that can put them back.
///
/// <para>A signal rather than an injected <see cref="StreamIngestManager"/>, because both sides are
/// hosted services — resolving one from the other would build a second copy of it, supervising a
/// second set of ffmpegs. A singleton both of them take instead keeps each with one owner.</para>
///
/// <para><b>Only the detect session.</b> A camera's recording is supervised separately so that a sub
/// stream's trouble costs snapshots and AI and never footage, and recovering a detector by dropping
/// the recording alongside it would be the wrong trade every time — the more so because the recording
/// is usually a different stream, off a different connection, and perfectly healthy.</para>
/// </summary>
public sealed class DetectSessionRestarts
{
    private readonly ConcurrentDictionary<string, Action> _attempts = new(StringComparer.Ordinal);

    /// <summary>
    /// Ends <paramref name="cameraId"/>'s running detect session so its supervisor starts a fresh
    /// one, which re-probes the source and rebuilds the outputs from what it answers.
    ///
    /// Does nothing for a camera with no detect session in flight — one between attempts, or one
    /// whose frames come from the recording session instead. Both are ordinary states, and a
    /// request that lands in either is satisfied by the attempt that follows it.
    /// </summary>
    public void Request(string cameraId)
    {
        if (_attempts.TryGetValue(cameraId, out Action? restart))
        {
            restart();
        }
    }

    /// <summary>Publishes the running attempt's canceller, for as long as that attempt lasts.</summary>
    internal IDisposable Register(string cameraId, Action restart)
    {
        _attempts[cameraId] = restart;
        return new Registration(this, cameraId, restart);
    }

    private sealed class Registration(DetectSessionRestarts owner, string cameraId, Action restart)
        : IDisposable
    {
        /// <summary>
        /// Removes this attempt's canceller and only this one.
        ///
        /// By key and value together because attempts overlap for a moment at the handover — the
        /// next one registers before the one it replaces has finished unwinding — and a plain
        /// remove-by-key from the outgoing attempt would take the incoming attempt's canceller with
        /// it, leaving a session nothing could ask to restart.
        /// </summary>
        public void Dispose() =>
            ((ICollection<KeyValuePair<string, Action>>)owner._attempts)
                .Remove(new KeyValuePair<string, Action>(cameraId, restart));
    }
}
