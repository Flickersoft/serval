using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Serval.Server.Configuration;
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

    // ------------------------------------------------------ the live playlist

    /// <summary>
    /// <b>The whole difference between live and VOD is the tag that is missing.</b> EXT-X-ENDLIST
    /// tells a player the stream is complete; without it, the player comes back for the playlist
    /// again. A live playlist that carried it would play the window once and stop.
    /// </summary>
    [Fact]
    public void A_live_playlist_does_not_claim_to_have_ended()
    {
        List<string> lines = Lines(HlsPlaylist.BuildLive(
            [Seg("seg-s1-00007.m4s", "init-s1.mp4", T0)], mediaSequence: 7));

        Assert.DoesNotContain("#EXT-X-ENDLIST", lines);

        // Nor a type: VOD promises the list never changes and EVENT promises it only grows. A
        // sliding window is neither, and a player told either one stops refreshing.
        Assert.DoesNotContain(lines, l => l.StartsWith("#EXT-X-PLAYLIST-TYPE", StringComparison.Ordinal));
    }

    /// <summary>
    /// The sequence number is what lets a player line up the segments it already has against a
    /// refreshed playlist that starts further on. It comes from ffmpeg's own filename counter, so
    /// it keeps meaning the same thing as the window slides.
    /// </summary>
    [Fact]
    public void A_live_playlist_states_where_its_window_starts()
    {
        List<string> lines = Lines(HlsPlaylist.BuildLive(
            [
                Seg("seg-s1-00042.m4s", "init-s1.mp4", T0),
                Seg("seg-s1-00043.m4s", "init-s1.mp4", T0.AddSeconds(4)),
            ],
            HlsPlaylist.SequenceOf("seg-s1-00042.m4s")));

        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:42", lines);
    }

    /// <summary>
    /// Every segment and the init carry the ticket. A player resolves these relative names against
    /// the playlist's own URL, and RFC 3986 drops the query when it does — so without this the
    /// playlist loads and then every segment is refused, which looks like a camera that produces
    /// no picture rather than one that is not authorised.
    /// </summary>
    [Fact]
    public void A_live_playlist_carries_the_ticket_on_every_url()
    {
        List<string> lines = Lines(HlsPlaylist.BuildLive(
            [Seg("seg-s1-00007.m4s", "init-s1.mp4", T0)], 7, "tick et"));

        Assert.Contains("#EXT-X-MAP:URI=\"init-s1.mp4?stream_token=tick%20et\"", lines);
        Assert.Contains("seg-s1-00007.m4s?stream_token=tick%20et", lines);
    }

    [Theory]
    [InlineData("seg-20260820-120000-00042.m4s", 42)]
    [InlineData("seg-s1-00000.m4s", 0)]
    [InlineData("init-s1.mp4", 0)]
    [InlineData("nonsense", 0)]
    public void A_segments_sequence_comes_from_its_filename(string name, int expected) =>
        Assert.Equal(expected, HlsPlaylist.SequenceOf(name));

    /// <summary>
    /// A playlist ffmpeg wrote — the preview ring's — served with the ticket appended to every URI
    /// in it. Same trap as <c>BuildVod</c>: a player resolves these relative names against the
    /// playlist's own URL, and RFC 3986 drops the query, so without this the playlist loads and
    /// every segment is refused.
    /// </summary>
    [Fact]
    public void An_ffmpeg_playlist_is_served_with_the_token_on_every_uri()
    {
        const string written = """
            #EXTM3U
            #EXT-X-VERSION:7
            #EXT-X-TARGETDURATION:4
            #EXT-X-MEDIA-SEQUENCE:45
            #EXT-X-INDEPENDENT-SEGMENTS
            #EXT-X-MAP:URI="preview-init-1.mp4"
            #EXTINF:4.008691,
            preview-1-00045.m4s
            """;

        List<string> lines = Lines(HlsPlaylist.WithStreamToken(written, "tick et"));

        Assert.Contains("#EXT-X-MAP:URI=\"preview-init-1.mp4?stream_token=tick%20et\"", lines);
        Assert.Contains("preview-1-00045.m4s?stream_token=tick%20et", lines);

        // Tags that name no file are untouched — appending to one would corrupt the playlist.
        Assert.Contains("#EXT-X-TARGETDURATION:4", lines);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:45", lines);
    }

    /// <summary>No token — a header-authenticated caller — leaves the playlist exactly as written.</summary>
    [Fact]
    public void An_ffmpeg_playlist_is_untouched_without_a_token()
    {
        const string written = "#EXTM3U\n#EXT-X-MAP:URI=\"i.mp4\"\nseg.m4s\n";

        Assert.Equal(written, HlsPlaylist.WithStreamToken(written, null));
    }

    /// <summary>
    /// <b>Which filenames the segment routes will serve.</b> Two jobs at once: keeping a request
    /// from climbing out of the camera's directory, and admitting every shape ffmpeg actually
    /// writes. Getting the second half wrong is quiet — the playlist is served, every segment in it
    /// 404s, and a television shows a connected stream with no picture, which is what the preview
    /// ring's prefix missing from here did.
    /// </summary>
    [Theory]
    [InlineData("seg-20260820-232113-00047", true)]
    [InlineData("init-20260820-232113", true)]
    [InlineData("preview-20260820-233021-00075", true)]
    [InlineData("preview-init-20260820-233021", true)]
    [InlineData("../../etc/passwd", false)]
    [InlineData("seg-../../escape", false)]
    [InlineData("live", false)]
    [InlineData("anything-else", false)]
    public void The_segment_routes_serve_only_what_ffmpeg_writes(string file, bool served) =>
        Assert.Equal(served, Media.MediaEndpoints.IsSafeSegmentName(file));

    // ------------------------------------------------- the cast playlist

    /// <summary>
    /// The cast playlist names transcoded MPEG-TS segments and carries no <c>EXT-X-MAP</c>.
    ///
    /// <para>Both follow from the same fact: each segment is transcoded by its own ffmpeg, and
    /// independent runs cannot be relied on to emit a byte-identical initialisation. A TS segment
    /// carries its own, so there is nothing to share and nothing to get wrong.</para>
    /// </summary>
    [Fact]
    public void The_cast_playlist_lists_transcoded_segments_and_no_init()
    {
        var segments = new List<RecordingSegment>
        {
            Seg("seg-s1-00000.m4s", "init-s1.mp4", T0),
            Seg("seg-s1-00001.m4s", "init-s1.mp4", T0.AddSeconds(4)),
        };

        List<string> lines = Lines(HlsPlaylist.BuildCastVod(segments));

        Assert.DoesNotContain(lines, l => l.StartsWith("#EXT-X-MAP", StringComparison.Ordinal));
        Assert.Contains("#EXT-X-VERSION:3", lines);
        Assert.Contains("cast/seg-s1-00000.ts?n=2&o=0&d=8", lines);
        Assert.Contains("#EXT-X-ENDLIST", lines);
    }

    /// <summary>
    /// Recorded segments are batched, and the entry declares the batch's whole duration.
    ///
    /// <para>Launching ffmpeg and initialising the GPU costs more than transcoding four seconds of
    /// video, so a batch pays it once rather than once per segment — and everything inside one is a
    /// single continuous encode instead of several independent ones.</para>
    /// </summary>
    [Fact]
    public void Segments_are_batched_and_the_entry_declares_the_whole_batch()
    {
        var segments = new List<RecordingSegment>
        {
            Seg("seg-s1-00000.m4s", "init-s1.mp4", T0, dur: 4),
            Seg("seg-s1-00001.m4s", "init-s1.mp4", T0.AddSeconds(4), dur: 3.5),
            Seg("seg-s1-00002.m4s", "init-s1.mp4", T0.AddSeconds(7.5), dur: 4),
        };

        List<string> lines = Lines(HlsPlaylist.BuildCastVod(segments));

        // One entry for all three, and its EXTINF is their total rather than any one of them. The
        // same total reaches the transcoder as `d`, which is what it trims the encode to.
        Assert.Contains("cast/seg-s1-00000.ts?n=3&o=0&d=11.5", lines);
        Assert.Contains("#EXTINF:11.5,", lines);
        Assert.DoesNotContain(lines, l => l.Contains("seg-s1-00001.ts", StringComparison.Ordinal));
    }

    /// <summary>
    /// A batch never spans a session restart. Its segments are concatenated and fed to one decoder,
    /// and across a restart they are not decodable together at all.
    /// </summary>
    [Fact]
    public void A_batch_stops_at_a_session_boundary()
    {
        var segments = new List<RecordingSegment>
        {
            Seg("seg-s1-00000.m4s", "init-s1.mp4", T0),
            Seg("seg-s2-00000.m4s", "init-s2.mp4", T0.AddSeconds(4)),
            Seg("seg-s2-00001.m4s", "init-s2.mp4", T0.AddSeconds(8)),
        };

        List<IReadOnlyList<RecordingSegment>> batches = [.. HlsPlaylist.Batches(segments)];

        Assert.Equal(2, batches.Count);
        Assert.Single(batches[0]);
        Assert.Equal(2, batches[1].Count);
    }

    /// <summary>A batch is bounded, so a long recording does not become one enormous encode.</summary>
    [Fact]
    public void A_batch_is_bounded()
    {
        List<RecordingSegment> segments =
        [
            .. Enumerable.Range(0, HlsPlaylist.CastBatchSegments * 2 + 1)
                .Select(i => Seg($"seg-s1-{i:00000}.m4s", "init-s1.mp4", T0.AddSeconds(4 * i))),
        ];

        List<IReadOnlyList<RecordingSegment>> batches = [.. HlsPlaylist.Batches(segments)];

        Assert.All(batches, b => Assert.True(b.Count <= HlsPlaylist.CastBatchSegments));
        Assert.Equal(segments.Count, batches.Sum(b => b.Count));
    }

    /// <summary>
    /// A batch names its first segment and how many follow, and the transcoder resolves the rest by
    /// counting — the recorder numbers them consecutively within a session.
    /// </summary>
    [Fact]
    public void A_batch_resolves_to_consecutive_files()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;

        try
        {
            foreach (int i in new[] { 7, 8, 9 })
            {
                File.WriteAllText(Path.Combine(dir, $"seg-s1-{i:00000}.m4s"), "x");
            }

            IReadOnlyList<string> paths = Media.CastTranscoder.BatchPaths(dir, "seg-s1-00007", 4);

            // Three, not four: the fourth is not on disk. Retention runs while somebody is
            // watching, and a batch whose tail has gone is still playable up to the gap.
            Assert.Equal(3, paths.Count);
            Assert.EndsWith("seg-s1-00007.m4s", paths[0], StringComparison.Ordinal);
            Assert.EndsWith("seg-s1-00009.m4s", paths[2], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The token rides on every segment, because a player resolves these names against the
    /// playlist's URL and RFC 3986 drops its query when it does — so a playlist that loaded fine
    /// would be followed by segments that all 401. The same trap <see cref="BuildVod"/> documents,
    /// and it is worth a test on each because the query already has a parameter here.
    /// </summary>
    [Fact]
    public void The_cast_segments_keep_the_token_alongside_their_offset()
    {
        var segments = new List<RecordingSegment> { Seg("seg-s1-00000.m4s", "init-s1.mp4", T0) };

        List<string> lines = Lines(HlsPlaylist.BuildCastVod(segments, streamToken: "tok en"));

        Assert.Contains("cast/seg-s1-00000.ts?n=1&o=0&d=4&stream_token=tok%20en", lines);
    }

    /// <summary>
    /// A session restart is <em>not</em> a discontinuity here, and that is the point.
    ///
    /// <para>The recorder restarts every few minutes and each restart begins its timestamps afresh.
    /// Carrying those through would make the media timeline jump backwards partway along a window —
    /// seeks land nowhere and playback ends early, which is what it did. Every batch is instead
    /// pinned to its place in the playlist, so the timeline is continuous across a restart and
    /// there is nothing to declare. It is safe because every batch is re-encoded to identical
    /// parameters: the join is seamless in the media as well as in the timeline.</para>
    /// </summary>
    [Fact]
    public void A_session_restart_is_not_a_discontinuity_when_cast()
    {
        var segments = new List<RecordingSegment>
        {
            Seg("seg-s1-00000.m4s", "init-s1.mp4", T0),
            Seg("seg-s2-00000.m4s", "init-s2.mp4", T0.AddSeconds(4)),
        };

        List<string> lines = Lines(HlsPlaylist.BuildCastVod(segments));

        Assert.DoesNotContain("#EXT-X-DISCONTINUITY", lines);

        // Still two batches, because they cannot share one decoder — but consecutive on the
        // timeline, the second starting exactly where the first ends.
        Assert.Contains("cast/seg-s1-00000.ts?n=1&o=0&d=4", lines);
        Assert.Contains("cast/seg-s2-00000.ts?n=1&o=4&d=4", lines);
    }

    /// <summary>
    /// Offsets accumulate from the recorder's own clock, not from declared durations.
    ///
    /// <para>A segment's declared duration runs fractionally short of the distance to the next one,
    /// and summing those drifts by seconds over a long window — so a seek near the end of a
    /// recording lands somewhere else entirely. The spacing between starts is what the timeline
    /// actually is.</para>
    /// </summary>
    [Fact]
    public void Offsets_follow_the_spacing_of_the_recording()
    {
        // Declared durations are short of the real 4s spacing, exactly as the recorder writes them.
        List<RecordingSegment> segments =
        [
            .. Enumerable.Range(0, HlsPlaylist.CastBatchSegments * 2)
                .Select(i => Seg($"seg-s1-{i:00000}.m4s", "init-s1.mp4", T0.AddSeconds(4 * i), dur: 3.9)),
        ];

        List<string> lines = Lines(HlsPlaylist.BuildCastVod(segments));

        // The second batch starts one batch of real time in, not one batch of declared durations.
        Assert.Contains($"cast/seg-s1-{HlsPlaylist.CastBatchSegments:00000}.ts"
            + $"?n={HlsPlaylist.CastBatchSegments}&o={4 * HlsPlaylist.CastBatchSegments}"
            + $"&d={(3.9 * HlsPlaylist.CastBatchSegments).ToString("0.###", CultureInfo.InvariantCulture)}",
            lines);
    }


    /// <summary>
    /// The receiver's answer about its own screen reaches every batch.
    ///
    /// <para>It is the one thing only the television knows — the floor every Cast device decodes is
    /// 1080p, but a 4K set will take more, and transcoding down to the floor for a screen that
    /// would have shown the detail throws the picture away for nothing.</para>
    /// </summary>
    [Fact]
    public void The_screens_own_ceiling_reaches_every_cast_segment()
    {
        var segments = new List<RecordingSegment> { Seg("seg-s1-00000.m4s", "init-s1.mp4", T0) };

        List<string> withHeight = Lines(HlsPlaylist.BuildCastVod(segments, maxHeight: 2160));
        List<string> without = Lines(HlsPlaylist.BuildCastVod(segments));

        Assert.Contains("cast/seg-s1-00000.ts?n=1&o=0&d=4&h=2160", withHeight);

        // Absent rather than guessed at: the receiver could not say, and the transcoder's own
        // default is the thing that decides — in one place rather than two.
        Assert.Contains("cast/seg-s1-00000.ts?n=1&o=0&d=4", without);
        Assert.DoesNotContain(without, l => l.Contains("&h=", StringComparison.Ordinal));
    }

    /// <summary>
    /// What a receiver asks for is honoured inside reason, and its absence is the safe floor. A
    /// device claiming something absurd would otherwise have the server encoding at that size,
    /// which is expensive and pointless together.
    /// </summary>
    [Theory]
    [InlineData(null, 1080)]
    [InlineData(2160, 2160)]
    [InlineData(720, 720)]
    [InlineData(4320, 2160)]
    [InlineData(1, 360)]
    [InlineData(-100, 360)]
    public void A_receivers_claim_is_honoured_within_reason(int? asked, int used) =>
        Assert.Equal(used, Media.CastTranscoder.Clamp(asked));

    /// <summary>
    /// Bitrate follows the height. Fixed, it would be generous at 1080p and visibly poor at 2160p —
    /// which would waste most of what asking the device for its ceiling bought.
    /// </summary>
    [Fact]
    public void Bitrate_rises_with_the_picture()
    {
        Assert.Equal("16M", Media.CastTranscoder.Bitrate(2160));
        Assert.Equal("5M", Media.CastTranscoder.Bitrate(1080));
        Assert.Equal("3M", Media.CastTranscoder.Bitrate(720));
    }

    /// <summary>
    /// Deliverability is a second ceiling, and the lower of the two wins.
    ///
    /// <para>A television reporting 2160 is telling the truth about its panel and nothing about the
    /// network. Measured: a 2160p segment is 9.4 MB and 1.25s to encode, leaving 2.75s of a
    /// four-second segment to carry it — 27 Mbit/s sustained, through the operator's public address.
    /// It buffers ten seconds, plays them, and stops. The same recording at 1080p needs about
    /// 6 Mbit/s and plays.</para>
    /// </summary>
    [Theory]
    [InlineData(2160, 1080, 1080)]
    [InlineData(720, 1080, 720)]
    [InlineData(2160, 2160, 2160)]
    [InlineData(null, 1080, 1080)]
    public void The_lower_of_the_two_ceilings_decides(int? asked, int configured, int used)
    {
        int? effective = asked is int a ? Math.Min(a, configured) : configured;

        var segments = new List<RecordingSegment> { Seg("seg-s1-00000.m4s", "init-s1.mp4", T0) };
        List<string> lines = Lines(HlsPlaylist.BuildCastVod(segments, maxHeight: effective));

        Assert.Contains($"cast/seg-s1-00000.ts?n=1&o=0&d=4&h={used}", lines);
    }

    /// <summary>
    /// A batch is trimmed to the slot the playlist declared for it, and starts exactly there.
    ///
    /// <para><b>The bug this exists for.</b> Every batch is an independent encode positioned
    /// absolutely by <c>-output_ts_offset</c>, and left to its own length it runs a frame or two
    /// past the wall-clock spacing the playlist measured between recorded segments. The next batch
    /// then begins <em>inside</em> the tail of this one, so its first packet carries a timestamp
    /// earlier than this one's last — a stream whose timestamps run backwards at every join.
    /// Measured on real recordings: 60 ms of video and 120 ms of audio each time, which a Cast
    /// device reports as <c>MEDIA_DECODE</c> and stops on about thirty seconds in.</para>
    ///
    /// <para>The mux delay goes for the same reason. Left at its default the muxer holds the stream
    /// back 1.4 seconds from the offset it was given, which is 1.4 seconds of this batch not being
    /// where the playlist says it is.</para>
    /// </summary>
    [Fact]
    public void A_cast_batch_is_trimmed_to_the_slot_it_was_given()
    {
        var transcoder = new Media.CastTranscoder(
            Options.Create(new ServerOptions()),
            NullLogger<Media.CastTranscoder>.Instance);

        List<string> args = [.. transcoder.Arguments(offsetSeconds: 16.007, durationSeconds: 16.065, height: 1080)];

        Assert.Equal("16.065", args[args.IndexOf("-t") + 1]);
        Assert.Equal("16.007", args[args.IndexOf("-output_ts_offset") + 1]);
        Assert.Equal("0", args[args.IndexOf("-muxdelay") + 1]);
        Assert.Equal("0", args[args.IndexOf("-muxpreload") + 1]);

        // Output options, so they have to precede the output itself — after it they would be read
        // as belonging to a second output that does not exist.
        Assert.True(args.IndexOf("-t") < args.IndexOf("-f"));
    }

    /// <summary>
    /// No duration, no trim. A caller that did not say how long the slot is has not asked for one,
    /// and cutting the encode to a guessed length would lose footage rather than protect a join.
    /// </summary>
    [Fact]
    public void A_batch_with_no_declared_slot_is_not_trimmed()
    {
        var transcoder = new Media.CastTranscoder(
            Options.Create(new ServerOptions()),
            NullLogger<Media.CastTranscoder>.Instance);

        Assert.DoesNotContain(
            "-t", transcoder.Arguments(offsetSeconds: 0, durationSeconds: null, height: 1080));
        Assert.DoesNotContain(
            "-t", transcoder.Arguments(offsetSeconds: 0, durationSeconds: 0, height: 1080));
    }
}
