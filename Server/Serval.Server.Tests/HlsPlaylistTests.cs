using Serval.Server.Recordings;

namespace Serval.Server.Tests;

/// <summary>
/// The VOD playlist is the one non-trivial pure function in the playback path. What matters: it
/// lists the range's segments in order under the right init, and marks a discontinuity whenever
/// the init changes (a session restart) — because an fMP4 segment cannot be decoded against the
/// wrong init, and a player not told so will try.
/// </summary>
public class HlsPlaylistTests
{
    private static RecordingSegment Seg(string name, string init, DateTimeOffset start, double dur = 4.0) => new()
    {
        CameraId = "cam1",
        FileName = name,
        InitFileName = init,
        StartedAt = start,
        DurationSeconds = dur,
    };

    private static List<string> Lines(string playlist) =>
        playlist.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static readonly DateTimeOffset T0 = new(2026, 7, 24, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void One_init_run_lists_its_segments_in_order_under_a_single_map()
    {
        var segments = new List<RecordingSegment>
        {
            Seg("seg-s1-00000.m4s", "init-s1.mp4", T0),
            Seg("seg-s1-00001.m4s", "init-s1.mp4", T0.AddSeconds(4)),
        };

        List<string> lines = Lines(HlsPlaylist.BuildVod(segments));

        Assert.Equal("#EXTM3U", lines[0]);
        Assert.Single(lines, l => l == "#EXT-X-MAP:URI=\"init-s1.mp4\"");
        Assert.DoesNotContain("#EXT-X-DISCONTINUITY", lines);

        List<string> files = lines.Where(l => l.EndsWith(".m4s", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { "seg-s1-00000.m4s", "seg-s1-00001.m4s" }, files);
    }

    [Fact]
    public void An_init_change_emits_a_discontinuity_and_a_new_map()
    {
        // Two sessions with a restart in the middle. Without the discontinuity a player carries
        // its decoder state across the boundary and produces garbage or stalls.
        var segments = new List<RecordingSegment>
        {
            Seg("seg-s1-00000.m4s", "init-s1.mp4", T0),
            Seg("seg-s2-00000.m4s", "init-s2.mp4", T0.AddSeconds(4)),
        };

        List<string> lines = Lines(HlsPlaylist.BuildVod(segments));

        int discontinuity = lines.IndexOf("#EXT-X-DISCONTINUITY");
        int secondMap = lines.IndexOf("#EXT-X-MAP:URI=\"init-s2.mp4\"");

        Assert.True(discontinuity > 0, "expected a discontinuity at the session boundary.");
        Assert.True(secondMap == discontinuity + 1, "the new map must follow the discontinuity.");
    }

    [Fact]
    public void The_first_run_does_not_get_a_leading_discontinuity()
    {
        // A discontinuity before any media is meaningless, and some players reject it.
        var segments = new List<RecordingSegment> { Seg("seg-s1-00000.m4s", "init-s1.mp4", T0) };

        List<string> lines = Lines(HlsPlaylist.BuildVod(segments));

        Assert.True(
            lines.IndexOf("#EXT-X-MAP:URI=\"init-s1.mp4\"") < lines.Count,
            "expected a map for the first run.");
        Assert.DoesNotContain("#EXT-X-DISCONTINUITY", lines);
    }

    [Fact]
    public void Target_duration_is_at_least_the_longest_segment()
    {
        // A player rejects the whole playlist when an EXTINF exceeds EXT-X-TARGETDURATION, so
        // rounding this down would break playback outright rather than degrade it.
        var segments = new List<RecordingSegment>
        {
            Seg("seg-s1-00000.m4s", "init-s1.mp4", T0, dur: 4.0),
            Seg("seg-s1-00001.m4s", "init-s1.mp4", T0.AddSeconds(4), dur: 6.5),
        };

        List<string> lines = Lines(HlsPlaylist.BuildVod(segments));
        string target = lines.Single(l => l.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal));

        Assert.Equal("#EXT-X-TARGETDURATION:7", target);
    }

    [Fact]
    public void A_vod_playlist_is_closed_and_typed_as_vod()
    {
        // Without ENDLIST a player treats the playlist as live and keeps polling for more.
        var segments = new List<RecordingSegment> { Seg("seg-s1-00000.m4s", "init-s1.mp4", T0) };

        List<string> lines = Lines(HlsPlaylist.BuildVod(segments));

        Assert.Contains("#EXT-X-PLAYLIST-TYPE:VOD", lines);
        Assert.Equal("#EXT-X-ENDLIST", lines[^1]);
    }

    [Fact]
    public void Fmp4_segments_declare_a_version_that_supports_them()
    {
        // EXT-X-MAP requires version 6+, and version 7 is the floor for fMP4 in practice. An
        // older declaration makes conforming players refuse the playlist.
        var segments = new List<RecordingSegment> { Seg("seg-s1-00000.m4s", "init-s1.mp4", T0) };

        List<string> lines = Lines(HlsPlaylist.BuildVod(segments));
        string version = lines.Single(l => l.StartsWith("#EXT-X-VERSION:", StringComparison.Ordinal));

        Assert.True(int.Parse(version["#EXT-X-VERSION:".Length..]) >= 7);
    }

    [Fact]
    public void Every_segment_is_preceded_by_its_duration()
    {
        var segments = new List<RecordingSegment>
        {
            Seg("seg-s1-00000.m4s", "init-s1.mp4", T0, dur: 4.0),
            Seg("seg-s2-00000.m4s", "init-s2.mp4", T0.AddSeconds(4), dur: 2.5),
        };

        List<string> lines = Lines(HlsPlaylist.BuildVod(segments));

        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].EndsWith(".m4s", StringComparison.Ordinal))
            {
                Assert.StartsWith("#EXTINF:", lines[i - 1]);
            }
        }

        Assert.Contains("#EXTINF:4,", lines);
        Assert.Contains("#EXTINF:2.5,", lines);
    }

    [Fact]
    public void A_window_starting_mid_segment_carries_a_start_offset()
    {
        // Segments only cut on keyframes, so a window asked for at 18:00:02.5 begins inside the
        // 18:00:00 segment. Without the tag a player starts 2.5 s before the instant the user
        // clicked, every time.
        var segments = new List<RecordingSegment> { Seg("seg-s1-00000.m4s", "init-s1.mp4", T0) };

        List<string> lines = Lines(HlsPlaylist.BuildVod(segments, T0.AddMilliseconds(2500)));

        Assert.Contains("#EXT-X-START:TIME-OFFSET=2.5,PRECISE=YES", lines);
    }

    [Fact]
    public void A_window_aligned_to_a_segment_emits_no_start_tag()
    {
        var segments = new List<RecordingSegment> { Seg("seg-s1-00000.m4s", "init-s1.mp4", T0) };

        List<string> lines = Lines(HlsPlaylist.BuildVod(segments, T0));

        Assert.DoesNotContain(lines, l => l.StartsWith("#EXT-X-START:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_start_offset_is_never_negative()
    {
        // A `from` earlier than any segment is what an empty stretch at the head of the window
        // looks like. Seeking backwards from zero is not a thing.
        var segments = new List<RecordingSegment> { Seg("seg-s1-00000.m4s", "init-s1.mp4", T0) };

        List<string> lines = Lines(HlsPlaylist.BuildVod(segments, T0.AddSeconds(-30)));

        Assert.DoesNotContain(lines, l => l.StartsWith("#EXT-X-START:", StringComparison.Ordinal));
    }

    [Fact]
    public void The_start_tag_precedes_the_first_map()
    {
        // EXT-X-START is playlist-level. Emitted after a media tag it is out of place, and
        // conforming players are within their rights to ignore or reject it.
        var segments = new List<RecordingSegment> { Seg("seg-s1-00000.m4s", "init-s1.mp4", T0) };

        List<string> lines = Lines(HlsPlaylist.BuildVod(segments, T0.AddSeconds(2)));

        int start = lines.FindIndex(l => l.StartsWith("#EXT-X-START:", StringComparison.Ordinal));
        int map = lines.FindIndex(l => l.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal));

        Assert.True(start >= 0 && map > start, "the start tag must come before the first map.");
    }

    [Fact]
    public void A_playlist_built_without_a_from_is_unchanged()
    {
        // /clip.mp4 and every existing caller pass no `from`, and must keep getting exactly what
        // they got before.
        var segments = new List<RecordingSegment> { Seg("seg-s1-00000.m4s", "init-s1.mp4", T0) };

        Assert.Equal(HlsPlaylist.BuildVod(segments), HlsPlaylist.BuildVod(segments, null));
    }

    /// <summary>
    /// A live playlist as ffmpeg writes it, with segments whose real durations are nothing like the
    /// 4-second target — which is what <c>-c:v copy</c> against a long-GOP camera produces.
    /// </summary>
    private const string LivePlaylist = """
        #EXTM3U
        #EXT-X-VERSION:7
        #EXT-X-TARGETDURATION:11
        #EXT-X-MEDIA-SEQUENCE:0
        #EXT-X-INDEPENDENT-SEGMENTS
        #EXT-X-MAP:URI="init-20260731-120000.mp4"
        #EXTINF:10.000000,
        seg-20260731-120000-00000.m4s
        #EXTINF:10.000000,
        seg-20260731-120000-00001.m4s
        #EXTINF:6.500000,
        seg-20260731-120000-00002.m4s

        """;

    [Fact]
    public void Live_playlist_segments_are_read_with_their_real_durations()
    {
        // The whole point: computing these from SegmentSeconds would say 4, 4, 4 — and every
        // recording would be indexed at a time it was not recorded.
        IReadOnlyList<(string FileName, double DurationSeconds)> segments =
            HlsPlaylist.ParseSegments(LivePlaylist);

        Assert.Equal(3, segments.Count);
        Assert.Equal("seg-20260731-120000-00000.m4s", segments[0].FileName);
        Assert.Equal(10.0, segments[0].DurationSeconds);
        Assert.Equal(6.5, segments[2].DurationSeconds);
    }

    [Fact]
    public void The_init_map_is_not_mistaken_for_a_segment()
    {
        Assert.DoesNotContain(
            HlsPlaylist.ParseSegments(LivePlaylist),
            s => s.FileName.Contains("init-", StringComparison.Ordinal));
    }

    [Fact]
    public void A_playlist_torn_mid_write_stops_rather_than_skipping()
    {
        // ffmpeg rewrites this file in place, so a read can catch it partway. Skipping the torn
        // entry would shift every later segment's start time by its duration — silently, and
        // permanently, since the index is only written once per segment.
        const string torn = """
            #EXTM3U
            #EXT-X-MAP:URI="init-20260731-120000.mp4"
            #EXTINF:10.000000,
            seg-20260731-120000-00000.m4s
            #EXTINF:10.00
            """;

        IReadOnlyList<(string FileName, double DurationSeconds)> segments =
            HlsPlaylist.ParseSegments(torn);

        Assert.Single(segments);
        Assert.Equal("seg-20260731-120000-00000.m4s", segments[0].FileName);
    }

    [Fact]
    public void An_empty_or_headers_only_playlist_yields_nothing()
    {
        Assert.Empty(HlsPlaylist.ParseSegments(""));
        Assert.Empty(HlsPlaylist.ParseSegments("#EXTM3U\n#EXT-X-VERSION:7\n"));
    }

    [Fact]
    public void Round_trips_through_the_vod_builder()
    {
        // The two halves have to agree: BuildVod writes what ParseSegments reads.
        var segments = new List<RecordingSegment>
        {
            Seg("seg-s1-00000.m4s", "init-s1.mp4", T0, dur: 10.0),
            Seg("seg-s1-00001.m4s", "init-s1.mp4", T0.AddSeconds(10), dur: 6.5),
        };

        IReadOnlyList<(string FileName, double DurationSeconds)> parsed =
            HlsPlaylist.ParseSegments(HlsPlaylist.BuildVod(segments));

        Assert.Equal(["seg-s1-00000.m4s", "seg-s1-00001.m4s"], parsed.Select(s => s.FileName));
        Assert.Equal([10.0, 6.5], parsed.Select(s => s.DurationSeconds));
    }

    [Fact]
    public void A_stream_token_reaches_every_segment_and_init_uri()
    {
        // A player resolves these relative names against the playlist's own URL, and RFC 3986
        // drops that URL's query when it does. So a token carried only on the playlist request
        // buys exactly one authorised request: the playlist. Every segment then 401s, which the
        // web player reports as a fragLoadError over an otherwise healthy-looking playlist.
        var segments = new List<RecordingSegment>
        {
            Seg("seg-s1-00000.m4s", "init-s1.mp4", T0),
            Seg("seg-s2-00000.m4s", "init-s2.mp4", T0.AddSeconds(4)),
        };

        List<string> lines = Lines(HlsPlaylist.BuildVod(segments, from: null, streamToken: "tok.en-123"));

        Assert.Equal(
            ["seg-s1-00000.m4s?stream_token=tok.en-123", "seg-s2-00000.m4s?stream_token=tok.en-123"],
            lines.Where(l => l.Contains(".m4s", StringComparison.Ordinal)));
        Assert.Contains("#EXT-X-MAP:URI=\"init-s1.mp4?stream_token=tok.en-123\"", lines);
        Assert.Contains("#EXT-X-MAP:URI=\"init-s2.mp4?stream_token=tok.en-123\"", lines);
    }

    [Fact]
    public void A_token_with_url_unsafe_characters_is_escaped()
    {
        var segments = new List<RecordingSegment> { Seg("seg-s1-00000.m4s", "init-s1.mp4", T0) };

        List<string> lines = Lines(HlsPlaylist.BuildVod(segments, from: null, streamToken: "a+b/c=d&e"));

        Assert.Contains("seg-s1-00000.m4s?stream_token=a%2Bb%2Fc%3Dd%26e", lines);
    }

    [Fact]
    public void No_token_leaves_the_uris_bare()
    {
        // The header path (curl, desktop debugging) can authorise the segment requests itself, and
        // a query parameter there would only put a credential in logs for nothing.
        var segments = new List<RecordingSegment> { Seg("seg-s1-00000.m4s", "init-s1.mp4", T0) };

        foreach (string? token in new[] { null, "" })
        {
            List<string> lines = Lines(HlsPlaylist.BuildVod(segments, from: null, streamToken: token));

            Assert.Contains("seg-s1-00000.m4s", lines);
            Assert.Contains("#EXT-X-MAP:URI=\"init-s1.mp4\"", lines);
            Assert.DoesNotContain(lines, l => l.Contains("stream_token", StringComparison.Ordinal));
        }
    }
}
