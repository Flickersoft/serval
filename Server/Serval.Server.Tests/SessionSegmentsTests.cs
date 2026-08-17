using Serval.Server.Recordings;

namespace Serval.Server.Tests;

/// <summary>
/// Which segments in the live playlist belong to this session, and when each of them starts.
///
/// This had no coverage at all, and the cost of that was measurable: on a real deployment a
/// restarted session adopted the previous run's entire playlist, stamped it with its own start, and
/// pushed the recording index hours into the future — 1025 seconds of media labelled across 6281.
/// Nothing corrects that afterwards, because a segment's start is written once and never revised.
/// </summary>
public class SessionSegmentsTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 4, 16, 13, 37, TimeSpan.Zero);
    private const string Stamp = "20260804-161337";

    private static string Playlist(params (string File, double Duration)[] segments)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:5\n");
        sb.Append("#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-INDEPENDENT-SEGMENTS\n");
        sb.Append($"#EXT-X-MAP:URI=\"init-{Stamp}.mp4\"\n");

        foreach ((string file, double duration) in segments)
        {
            sb.Append($"#EXTINF:{duration.ToString(System.Globalization.CultureInfo.InvariantCulture)},\n{file}\n");
        }

        return sb.ToString();
    }

    private static string Ours(int index) => $"seg-{Stamp}-{index:D5}.m4s";
    private static string Theirs(int index) => $"seg-20260804-142753-{index:D5}.m4s";

    [Fact]
    public void The_first_segment_of_a_session_starts_at_the_session_start()
    {
        var resolved = SessionSegments.Resolve(
            Playlist((Ours(0), 4.0), (Ours(1), 4.0)), Stamp, Start);

        Assert.Equal(Start, resolved[0].StartsAt);
        Assert.Equal(Start.AddSeconds(4), resolved[1].StartsAt);
    }

    [Fact]
    public void Starts_accumulate_the_real_durations_not_the_nominal_one()
    {
        // Under `-c:v copy` ffmpeg cuts only at a keyframe the camera already sent, so a segment
        // is as long as the GOP made it. Assuming the target would drift every seek.
        var resolved = SessionSegments.Resolve(
            Playlist((Ours(0), 3.02), (Ours(1), 4.99), (Ours(2), 4.01)), Stamp, Start);

        // To the millisecond: summing doubles one at a time lands a tick off an all-at-once sum,
        // and a tick is not what this is about.
        Assert.Equal(3.02, (resolved[1].StartsAt - Start).TotalSeconds, 3);
        Assert.Equal(8.01, (resolved[2].StartsAt - Start).TotalSeconds, 3);
    }

    [Fact]
    public void A_previous_sessions_playlist_yields_nothing()
    {
        // What is on disk when a session starts. `live.m3u8` is never deleted and `hls_list_size 0`
        // keeps every segment the last run wrote, and ffmpeg does not overwrite it until its first
        // segment closes — so this is the *normal* first read after a restart, not an edge case.
        string stale = Playlist(
            (Theirs(1568), 4.0), (Theirs(1569), 4.0), (Theirs(1570), 4.0));

        Assert.Empty(SessionSegments.Resolve(stale, Stamp, Start));
    }

    [Fact]
    public void A_stranger_shifts_nothing_that_follows_it()
    {
        // The offset counts the media *this* session has produced. Letting a foreign segment
        // advance it would misplace ours by exactly as much as indexing it would.
        var resolved = SessionSegments.Resolve(
            Playlist((Theirs(1570), 6285.0), (Ours(0), 4.0), (Ours(1), 4.0)), Stamp, Start);

        Assert.Equal(2, resolved.Count);
        Assert.Equal(Ours(0), resolved[0].FileName);
        Assert.Equal(Start, resolved[0].StartsAt);
        Assert.Equal(Start.AddSeconds(4), resolved[1].StartsAt);
    }

    [Fact]
    public void A_session_whose_stamp_merely_shares_a_prefix_is_still_a_stranger()
    {
        // The separator matters: "seg-20260804-1613" must not swallow "seg-20260804-161337".
        var resolved = SessionSegments.Resolve(
            Playlist(($"seg-{Stamp}7-00000.m4s", 4.0), (Ours(0), 4.0)), Stamp, Start);

        Assert.Equal(Ours(0), Assert.Single(resolved).FileName);
    }

    [Fact]
    public void An_empty_or_headers_only_playlist_is_not_an_error()
    {
        Assert.Empty(SessionSegments.Resolve(Playlist(), Stamp, Start));
        Assert.Empty(SessionSegments.Resolve(string.Empty, Stamp, Start));
    }
}
