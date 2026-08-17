namespace Serval.Server.Recordings;

/// <summary>
/// Turns one ffmpeg session's live playlist into the segments that belong to *that* session, with
/// the wall-clock start of each.
///
/// Split out from the indexing loop because the arithmetic is where the errors live and the loop is
/// only I/O. The session that owns it needs a Mongo collection, a snapshot broadcaster and a
/// probed ffmpeg to exist at all, so nothing about the timing could be tested while it lived
/// inside — see <see cref="HlsPlaylist"/>, which is split out for the same reason.
/// </summary>
public static class SessionSegments
{
    /// <summary>
    /// The segments of <paramref name="sessionStamp"/>'s own session, in playlist order, each with
    /// the instant it starts.
    ///
    /// <para><b>Only this session's segments.</b> ffmpeg writes <c>seg-{stamp}-%05d.m4s</c> and
    /// <c>init-{stamp}.mp4</c> from one stamp, so the filename says which session produced it, and
    /// anything else in the playlist was left behind by a previous run — <c>live.m3u8</c> is never
    /// deleted and <c>hls_list_size 0</c> keeps every segment that run ever wrote. A restarted
    /// session reads that before ffmpeg overwrites it, since under <c>-c:v copy</c> the first
    /// segment cannot close until the camera sends a keyframe, and counting those strangers would
    /// push this session's own segments an entire previous recording into the future.</para>
    ///
    /// <para>A skipped segment contributes no duration either: the offset is the media this session
    /// has produced, so letting a stranger advance it misplaces everything after it.</para>
    /// </summary>
    /// <param name="playlist">The contents of <c>live.m3u8</c>.</param>
    /// <param name="sessionStamp">This session's <c>yyyyMMdd-HHmmss</c> stamp.</param>
    /// <param name="sessionStart">The instant this session's media offset zero is taken to be.
    /// Whatever error this carries is shared with every other consumer of the same anchor, which is
    /// what lets a detection and a frame of footage agree about when they happened.</param>
    public static IReadOnlyList<(string FileName, DateTimeOffset StartsAt, double DurationSeconds)>
        Resolve(string playlist, string sessionStamp, DateTimeOffset sessionStart)
    {
        string prefix = $"seg-{sessionStamp}-";
        List<(string, DateTimeOffset, double)> segments = [];
        DateTimeOffset startsAt = sessionStart;

        foreach ((string fileName, double duration) in HlsPlaylist.ParseSegments(playlist))
        {
            if (!fileName.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            segments.Add((fileName, startsAt, duration));
            startsAt = startsAt.AddSeconds(duration);
        }

        return segments;
    }
}
