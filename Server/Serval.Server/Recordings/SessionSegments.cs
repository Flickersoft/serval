using System.Globalization;

namespace Serval.Server.Recordings;

/// <summary>
/// Turns one ffmpeg session's live playlist into the segments that belong to *that* session, with
/// the wall-clock start of each.
///
/// Split out from the indexing loop because the arithmetic is where the errors live and the loop is
/// only I/O. The session that owns it needs a Mongo collection, a snapshot broadcaster and a
/// probed ffmpeg to exist at all, so nothing about the timing could be tested while it lived
/// inside — see <see cref="HlsPlaylist"/>, which is split out for the same reason.
///
/// <para><b>Why it carries state.</b> A segment's start is the sum of every duration before it in
/// the session, but the recording playlist lists only the newest
/// <see cref="Ingest.RecordArguments.PlaylistWindowSeconds"/> of them. The running total lives here
/// and each pass advances it, because a playlist whose first entry is hours into the session no
/// longer holds what that total was summed from.</para>
/// </summary>
public sealed class SessionSegments
{
    private const string Extension = ".m4s";

    private readonly string _prefix;
    private readonly double _nominalSeconds;

    /// <summary>ffmpeg's counter value for the next segment no pass has returned yet.</summary>
    private int _next;

    private DateTimeOffset _nextStartsAt;

    /// <param name="sessionStamp">This session's <c>yyyyMMdd-HHmmss</c> stamp.</param>
    /// <param name="sessionStart">The instant this session's media offset zero is taken to be.
    /// Whatever error this carries is shared with every other consumer of the same anchor, which is
    /// what lets a detection and a frame of footage agree about when they happened.</param>
    /// <param name="nominalSeconds">The segment length ffmpeg was asked for. Used only for a segment
    /// the window dropped before any pass saw it — see <see cref="Advance"/>.</param>
    public SessionSegments(string sessionStamp, DateTimeOffset sessionStart, double nominalSeconds)
    {
        _prefix = $"seg-{sessionStamp}-";
        _nextStartsAt = sessionStart;
        _nominalSeconds = nominalSeconds;
    }

    /// <summary>
    /// The segments of this session that <paramref name="playlist"/> lists and no earlier pass
    /// returned, in playlist order, each with the instant it starts.
    ///
    /// <para><b>Only this session's segments.</b> ffmpeg writes <c>seg-{stamp}-%05d.m4s</c> and
    /// <c>init-{stamp}.mp4</c> from one stamp, so the filename says which session produced it, and
    /// anything else in the playlist was left behind by a previous run. A restarted session can
    /// read that before ffmpeg overwrites it, since under <c>-c:v copy</c> the first segment cannot
    /// close until the camera sends a keyframe, and counting those strangers would push this
    /// session's own segments an entire previous recording into the future.</para>
    ///
    /// <para>A skipped segment contributes no duration either: the offset is the media this session
    /// has produced, so letting a stranger advance it misplaces everything after it.</para>
    ///
    /// <para><b>A segment the window dropped unseen</b> is still returned, because retention
    /// deletes only what the index names and an unindexed file would stay on disk for good. Its
    /// name follows from ffmpeg's counter, but its duration left the playlist with it, so it is
    /// given the nominal length and counted in <paramref name="estimated"/> — and every later start
    /// in the session inherits that guess. Getting here takes an indexer that made no pass for the
    /// whole window, not merely a slow one.</para>
    /// </summary>
    /// <param name="playlist">The contents of <c>live.m3u8</c>.</param>
    /// <param name="estimated">How many of the returned segments carry the nominal duration rather
    /// than their own.</param>
    public IReadOnlyList<(string FileName, DateTimeOffset StartsAt, double DurationSeconds)> Advance(
        string playlist, out int estimated)
    {
        List<(string, DateTimeOffset, double)> segments = [];
        estimated = 0;

        foreach ((string fileName, double duration) in HlsPlaylist.ParseSegments(playlist))
        {
            if (!TryNumber(fileName, out int number) || number < _next)
            {
                continue;
            }

            for (; _next < number; _next++, estimated++)
            {
                segments.Add((FileName(_next), _nextStartsAt, _nominalSeconds));
                _nextStartsAt = _nextStartsAt.AddSeconds(_nominalSeconds);
            }

            segments.Add((fileName, _nextStartsAt, duration));
            _nextStartsAt = _nextStartsAt.AddSeconds(duration);
            _next++;
        }

        return segments;
    }

    /// <summary>
    /// ffmpeg's counter from one of this session's segment names. The prefix ends in its separator,
    /// so a stamp that merely starts the same way is not mistaken for this one.
    /// </summary>
    private bool TryNumber(string fileName, out int number)
    {
        number = 0;
        return fileName.StartsWith(_prefix, StringComparison.Ordinal)
            && fileName.EndsWith(Extension, StringComparison.Ordinal)
            && int.TryParse(
                fileName.AsSpan(_prefix.Length, fileName.Length - _prefix.Length - Extension.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out number);
    }

    /// <summary>The name ffmpeg's <c>%05d</c> gives segment <paramref name="number"/>.</summary>
    private string FileName(int number) =>
        $"{_prefix}{number.ToString("D5", CultureInfo.InvariantCulture)}{Extension}";
}
