using System.Collections.Concurrent;

namespace Serval.Server.Recordings;

/// <summary>
/// Where each camera's rolling preview segments are and when each one starts — the ring's answer to
/// <see cref="RecordingIndex"/>.
///
/// In memory rather than in Mongo, and that is the whole design. A ring segment lives for
/// <c>PreviewBufferSeconds</c> and is then deleted by ffmpeg, so indexing it would mean an insert
/// and a delete every few seconds per camera, forever, to describe files that are already gone by
/// the time anybody could query them a second way. Ten cameras would write more index churn for
/// footage nobody keeps than for the recordings they do.
///
/// <para>The cost of that choice is that a restart loses the index — but it loses the ring with it,
/// because <see cref="Ingest.PreviewRing.Reset"/> clears the previous session's segments at start.
/// There is nothing a persisted index could have pointed at.</para>
///
/// <para>Registered as a singleton and written by one watcher task per camera, read by the alert
/// clip worker on another thread; hence the lock around each camera's list.</para>
/// </summary>
public sealed class PreviewRingIndex
{
    private readonly ConcurrentDictionary<string, CameraRing> _rings = new(StringComparer.Ordinal);

    /// <summary>
    /// Starts a camera's ring over, discarding whatever the previous session left. Called when a
    /// session starts, which is the only moment the init segment changes: unlike the recording
    /// index, a ring never spans two sessions, because the older half of it has been deleted.
    /// </summary>
    public void Begin(string cameraId, string initFileName) =>
        _rings[cameraId] = new CameraRing(initFileName);

    /// <summary>Records a segment the watcher has just seen for the first time.</summary>
    public void Add(string cameraId, RecordingSegment segment)
    {
        if (!_rings.TryGetValue(cameraId, out CameraRing? ring))
        {
            return;
        }

        lock (ring.Gate)
        {
            ring.Segments.Add(segment);
        }
    }

    /// <summary>
    /// Drops every segment ffmpeg has pruned, given the names still in the playlist.
    ///
    /// Called on the same pass that adds new ones, so the index tracks the files rather than
    /// outliving them. Without it the worker would hand <see cref="Media.ClipExporter"/> the names
    /// of segments that no longer exist — which it tolerates by skipping them, producing a clip
    /// quietly missing its first seconds instead of one that is honestly too short.
    /// </summary>
    public void Retain(string cameraId, IReadOnlySet<string> living)
    {
        if (!_rings.TryGetValue(cameraId, out CameraRing? ring))
        {
            return;
        }

        lock (ring.Gate)
        {
            ring.Segments.RemoveAll(s => !living.Contains(s.FileName));
        }
    }

    /// <summary>Forgets a camera entirely — its session has stopped, so its ring is being deleted.</summary>
    public void Forget(string cameraId) => _rings.TryRemove(cameraId, out _);

    /// <summary>Whether this camera has a live ring at all, which is what decides where a preview is cut from.</summary>
    public bool HasRing(string cameraId) => _rings.ContainsKey(cameraId);

    /// <summary>
    /// The ring segments overlapping [from, to), in order — the same overlap rule
    /// <see cref="RecordingIndex.InRangeAsync"/> applies, so a window landing mid-segment still
    /// includes that segment and one merely touching an end does not.
    ///
    /// Empty when the range has rolled out of the ring, which is the answer the caller needs: an
    /// alert whose footage is gone gets no preview rather than a truncated one.
    /// </summary>
    public IReadOnlyList<RecordingSegment> InRange(string cameraId, DateTimeOffset from, DateTimeOffset to)
    {
        if (!_rings.TryGetValue(cameraId, out CameraRing? ring))
        {
            return [];
        }

        lock (ring.Gate)
        {
            return
            [
                .. ring.Segments
                    .Where(s => s.StartedAt < to && s.StartedAt.AddSeconds(s.DurationSeconds) > from)
                    .OrderBy(s => s.StartedAt),
            ];
        }
    }

    private sealed class CameraRing(string initFileName)
    {
        public string InitFileName { get; } = initFileName;

        public object Gate { get; } = new();

        public List<RecordingSegment> Segments { get; } = [];
    }
}
