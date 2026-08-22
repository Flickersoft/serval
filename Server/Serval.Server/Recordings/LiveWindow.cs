namespace Serval.Server.Recordings;

/// <summary>
/// Which recorded segments make up "now" for a live HLS playlist.
///
/// <para>Used by the Google Home playback route, which is what a Cast receiver fetches when the
/// destination cannot do WebRTC. Separate from the recording index's own idea of a window because
/// this one answers "what can a player start from right now", not "what was recorded" — and a
/// player handed segments it has no init for shows nothing at all.</para>
/// </summary>
public static class LiveWindow
{
    /// <summary>
    /// How far back to look. Long enough to hold the three or four segments a player wants before
    /// it starts, short enough that "live" means it: at the default four-second segment length this
    /// is the most recent half-minute.
    /// </summary>
    public static readonly TimeSpan Span = TimeSpan.FromSeconds(32);

    /// <summary>
    /// The most segments a playlist will name. A player buffers a few and then follows; naming more
    /// only invites it to start further behind.
    /// </summary>
    public const int MaxSegments = 6;

    /// <summary>
    /// The tail of <paramref name="segments"/> that shares the newest initialisation segment, at
    /// most <see cref="MaxSegments"/> of them.
    ///
    /// <para>An fMP4 segment cannot be decoded without the init it was written with, and each
    /// ffmpeg session writes a fresh one. A VOD playlist spans that boundary with a discontinuity;
    /// a live one must not, because the player would have to reset its decoder at the live edge and
    /// because the sequence numbering starts again on the far side of it.</para>
    /// </summary>
    public static List<RecordingSegment> NewestSession(List<RecordingSegment> segments)
    {
        if (segments.Count == 0)
        {
            return segments;
        }

        string init = segments[^1].InitFileName;

        return
        [
            .. segments
                .Where(s => string.Equals(s.InitFileName, init, StringComparison.Ordinal))
                .TakeLast(MaxSegments),
        ];
    }
}
