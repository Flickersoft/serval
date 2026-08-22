using System.Globalization;
using System.Text;

namespace Serval.Server.Recordings;

/// <summary>
/// Builds a static (VOD) HLS playlist for a range of recorded segments — pure, no I/O, so it's
/// trivially unit-testable and shared only by playback. The live playlist is written by ffmpeg
/// directly; this synthesises playback of the archive from the recording index.
///
/// fMP4 segments depend on the init segment they were written with, and each ffmpeg session
/// writes a fresh one. So a change of init emits <c>EXT-X-DISCONTINUITY</c> followed by a new
/// <c>EXT-X-MAP</c> — HLS's way of saying "the decoder must be reset here", and the direct
/// equivalent of the Period boundary the DASH manifest used before recordings carried audio.
/// </summary>
public static class HlsPlaylist
{
    public const string ContentType = "application/vnd.apple.mpegurl";

    /// <summary>
    /// The playlist for <paramref name="segments"/>, in order.
    ///
    /// <paramref name="from"/> is the instant the window was actually asked for. Segments only cut
    /// on keyframes, so the first one usually starts before it; given <paramref name="from"/> this
    /// emits <c>EXT-X-START</c> so a player opens at the requested instant rather than at the
    /// segment boundary. hls.js honours that tag (with <c>startPosition: -1</c>); ffmpeg's HLS
    /// demuxer, which is what libmpv uses, does not — so the desktop player seeks by the same
    /// offset itself, and the tag is what saves the web player from a visible flash of earlier
    /// footage before its seek lands. Omit <paramref name="from"/> and no tag is written.
    ///
    /// <paramref name="streamToken"/> is appended to every segment and init URI. Required whenever
    /// the playlist itself was fetched with a <c>?stream_token=</c>, because a player resolves
    /// these relative URIs against the playlist's URL and RFC 3986 drops the query when it does —
    /// so segments would arrive with no credential and 401, playing the playlist and nothing else.
    /// Null when the caller authenticated with a header (curl, desktop debugging), which it can
    /// equally set on the segment requests.
    /// </summary>
    /// <summary>
    /// The same window, as MPEG-TS segments a Cast device can actually decode.
    ///
    /// <para><b>Why a second builder rather than a parameter.</b> Three things differ and each one
    /// is load-bearing. There is no <c>EXT-X-MAP</c>, because a TS segment carries its own
    /// parameter sets — which is the whole reason for TS here: every segment is transcoded by a
    /// separate ffmpeg, and independent runs cannot be relied on to emit byte-identical
    /// initialisation. The version drops to 3, since <c>EXT-X-MAP</c> is what required 7. And each
    /// URI carries the segment's offset into the window.</para>
    ///
    /// <para><b>Every batch is pinned to where the playlist says it is</b>, by the <c>o=</c> it
    /// carries and the <c>-output_ts_offset</c> the transcoder passes on. So playlist time and media
    /// time are the same thing, which is what makes a seek mean anything: a player told to go to
    /// twelve minutes lands twelve minutes in.</para>
    ///
    /// <para><b>Keeping the recording's own timestamps instead does not work here</b>, and it is
    /// worth saying why since it looks tidier. The recorder restarts every few minutes — seven
    /// sessions in an hour on this deployment — and each restart begins its timestamps afresh. A
    /// window of any length therefore spans several, and its media timeline jumps backwards partway
    /// through: seeks land nowhere, and playback ends early. Normalising removes that, and a
    /// discontinuity tag becomes unnecessary along with it, because every batch is re-encoded to
    /// identical parameters and joins the previous one seamlessly.</para>
    ///
    /// <para>Segment names are relative — <c>cast/&lt;name&gt;.ts</c> — so they resolve against this
    /// playlist's own URL, and the token rides on each because RFC 3986 drops the playlist's query
    /// when it does.</para>
    /// </summary>
    public static string BuildCastVod(
        IReadOnlyList<RecordingSegment> segments,
        DateTimeOffset? from = null,
        string? streamToken = null,
        int? maxHeight = null)
    {
        var sb = new StringBuilder();

        string token = string.IsNullOrEmpty(streamToken)
            ? string.Empty
            : $"&stream_token={Uri.EscapeDataString(streamToken)}";

        // Carried per segment rather than held server-side, because it belongs to the screen and
        // not to the window: the receiver asks its own device what it will decode and puts the
        // answer on the playlist URL, so the same recording cast to a 4K television and to a 1080p
        // one is encoded differently, with nothing to remember between them.
        string height = maxHeight is int h ? $"&h={h.ToString(CultureInfo.InvariantCulture)}" : string.Empty;

        int target = segments.Count == 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(segments.Max(s => s.DurationSeconds)));

        sb.Append("#EXTM3U\n");
        sb.Append("#EXT-X-VERSION:3\n");
        sb.Append(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{target}\n");
        sb.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");
        sb.Append("#EXT-X-MEDIA-SEQUENCE:0\n");

        if (from is { } start && segments.Count > 0)
        {
            double offset = (start - segments[0].StartedAt).TotalSeconds;
            if (offset > 0.05)
            {
                sb.Append(CultureInfo.InvariantCulture,
                    $"#EXT-X-START:TIME-OFFSET={offset.ToString("0.###", CultureInfo.InvariantCulture)},PRECISE=YES\n");
            }
        }

        List<IReadOnlyList<RecordingSegment>> batches = [.. Batches(segments)];
        double elapsed = 0;

        for (int i = 0; i < batches.Count; i++)
        {
            IReadOnlyList<RecordingSegment> batch = batches[i];

            // How long this batch occupies the timeline: the distance to where the next one starts,
            // which is what the recorder's own clock says. Its segments' declared durations run
            // fractionally short of that, and using them accumulated a drift of seconds over a long
            // window. The last batch has no successor to measure against and falls back to them.
            //
            // Sent to the transcoder as well as declared here, because the two have to agree
            // exactly. A batch encoded to its own natural length overruns this slot by a frame or
            // two, which puts the next batch's first packet *before* the last packet of this one —
            // a timestamp running backwards mid-stream, which a Cast device reports as a decode
            // failure and stops on. Measured at every join: 60 ms of video and 120 ms of audio.
            double duration = i + 1 < batches.Count
                ? (batches[i + 1][0].StartedAt - batch[0].StartedAt).TotalSeconds
                : batch.Sum(s => s.DurationSeconds);

            sb.Append(CultureInfo.InvariantCulture,
                $"#EXTINF:{duration.ToString("0.######", CultureInfo.InvariantCulture)},\n");
            sb.Append(CultureInfo.InvariantCulture,
                $"cast/{CastSegmentName(batch[0].FileName)}.ts"
                + $"?n={batch.Count}&o={elapsed.ToString("0.###", CultureInfo.InvariantCulture)}"
                + $"&d={duration.ToString("0.###", CultureInfo.InvariantCulture)}{height}{token}\n");

            elapsed += duration;
        }

        sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }

    /// <summary>
    /// How many recorded segments one transcoded segment covers.
    ///
    /// <para>Each is a separate ffmpeg run, and a run costs a process launch and a VAAPI
    /// initialisation whatever it then does — most of the time a four-second segment took. Batching
    /// pays that once per batch instead of once per segment, and everything inside a batch is one
    /// continuous encode rather than several independent ones, which is fewer joins for a decoder
    /// to object to.</para>
    ///
    /// <para>Four, not more: a batch is also the seek granularity and the smallest thing that can
    /// be transcoded ahead, so a large one makes scrubbing coarse and wastes work whenever somebody
    /// moves.</para>
    /// </summary>
    public const int CastBatchSegments = 4;

    /// <summary>
    /// Consecutive runs of at most <see cref="CastBatchSegments"/> segments sharing one init.
    ///
    /// <para>A batch never spans a session restart: the segments in one are concatenated and fed to
    /// a single decoder, and across a restart they are not decodable together at all.</para>
    /// </summary>
    internal static IEnumerable<IReadOnlyList<RecordingSegment>> Batches(
        IReadOnlyList<RecordingSegment> segments)
    {
        var batch = new List<RecordingSegment>();

        foreach (RecordingSegment segment in segments)
        {
            bool sameRun = batch.Count > 0
                && string.Equals(batch[0].InitFileName, segment.InitFileName, StringComparison.Ordinal);

            if (batch.Count > 0 && (!sameRun || batch.Count == CastBatchSegments))
            {
                yield return batch;
                batch = [];
            }

            batch.Add(segment);
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    /// <summary>
    /// A recorded segment's name without its extension, which is what the transcoding route takes.
    /// The extension changes — <c>.m4s</c> in, <c>.ts</c> out — so it cannot be part of the name.
    /// </summary>
    public static string CastSegmentName(string fileName) =>
        fileName.EndsWith(".m4s", StringComparison.Ordinal)
            ? fileName[..^4]
            : fileName;

    public static string BuildVod(
        IReadOnlyList<RecordingSegment> segments,
        DateTimeOffset? from = null,
        string? streamToken = null)
    {
        var sb = new StringBuilder();

        string suffix = string.IsNullOrEmpty(streamToken)
            ? string.Empty
            : $"?stream_token={Uri.EscapeDataString(streamToken)}";

        // EXT-X-TARGETDURATION must be >= every EXTINF, rounded up, or players reject the
        // playlist outright rather than tolerating the overrun.
        int target = segments.Count == 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(segments.Max(s => s.DurationSeconds)));

        sb.Append("#EXTM3U\n");
        sb.Append("#EXT-X-VERSION:7\n"); // 7 is the floor for fMP4 (EXT-X-MAP) segments
        sb.Append(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{target}\n");
        sb.Append("#EXT-X-PLAYLIST-TYPE:VOD\n");
        sb.Append("#EXT-X-MEDIA-SEQUENCE:0\n");

        // Playlist-level, so it must precede the first EXT-X-MAP. PRECISE=YES is the point: without
        // it a player is free to round back to the segment boundary, which is what we are avoiding.
        if (from is { } start && segments.Count > 0)
        {
            double offset = (start - segments[0].StartedAt).TotalSeconds;
            if (offset > 0.05)
            {
                sb.Append(CultureInfo.InvariantCulture,
                    $"#EXT-X-START:TIME-OFFSET={offset.ToString("0.###", CultureInfo.InvariantCulture)},PRECISE=YES\n");
            }
        }

        string? currentInit = null;

        foreach (RecordingSegment segment in segments)
        {
            if (segment.InitFileName != currentInit)
            {
                if (currentInit is not null)
                {
                    // Not the first run: the stream genuinely breaks here, and a player told
                    // otherwise would try to decode across two unrelated initialisations.
                    sb.Append("#EXT-X-DISCONTINUITY\n");
                }

                sb.Append(CultureInfo.InvariantCulture, $"#EXT-X-MAP:URI=\"{segment.InitFileName}{suffix}\"\n");
                currentInit = segment.InitFileName;
            }

            sb.Append(CultureInfo.InvariantCulture,
                $"#EXTINF:{segment.DurationSeconds.ToString("0.######", CultureInfo.InvariantCulture)},\n");
            sb.Append(segment.FileName).Append(suffix).Append('\n');
        }

        sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }

    /// <summary>
    /// A live playlist over the newest few segments: the same files <see cref="BuildVod"/> serves,
    /// presented as a stream that has not finished.
    ///
    /// <para><b>Why ffmpeg's own <c>live.m3u8</c> is not simply served instead.</b> Two reasons,
    /// both deliberate elsewhere. It is written with <c>hls_list_size 0</c> so that nothing is ever
    /// deleted from it — the retention worker prunes by age instead — which means it names every
    /// segment the session ever wrote, and a player handed it would open hours behind. And its
    /// segment names carry no credential, so on any authenticated route the playlist would load and
    /// every segment would then 401. See <see cref="BuildVod"/>'s note on relative resolution.</para>
    ///
    /// <para><b>All segments must share one initialisation.</b> The caller passes segments from a
    /// single ffmpeg session, because a discontinuity at the live edge is exactly where players
    /// give up rather than recover, and because <c>EXT-X-MEDIA-SEQUENCE</c> must never go backwards
    /// across a refresh — which a session restart, whose numbering starts again at zero, would
    /// otherwise make it do.</para>
    /// </summary>
    /// <param name="segments">Consecutive segments of one session, oldest first.</param>
    /// <param name="mediaSequence">The sequence number of <paramref name="segments"/>[0]. Taken
    /// from ffmpeg's own filename counter, so it advances by one per segment and survives the
    /// window sliding forward.</param>
    /// <param name="streamToken">Appended to every segment and init URI, for the reason
    /// <see cref="BuildVod"/> gives.</param>
    public static string BuildLive(
        IReadOnlyList<RecordingSegment> segments,
        int mediaSequence,
        string? streamToken = null)
    {
        var sb = new StringBuilder();

        string suffix = string.IsNullOrEmpty(streamToken)
            ? string.Empty
            : $"?stream_token={Uri.EscapeDataString(streamToken)}";

        int target = segments.Count == 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(segments.Max(s => s.DurationSeconds)));

        sb.Append("#EXTM3U\n");
        sb.Append("#EXT-X-VERSION:7\n");
        sb.Append(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{target}\n");

        // No EXT-X-PLAYLIST-TYPE. VOD would promise the list never changes and EVENT would promise
        // it only ever grows; a sliding window is neither, and a player told either one stops
        // refreshing.
        sb.Append(CultureInfo.InvariantCulture, $"#EXT-X-MEDIA-SEQUENCE:{mediaSequence}\n");

        if (segments.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"#EXT-X-MAP:URI=\"{segments[0].InitFileName}{suffix}\"\n");
        }

        foreach (RecordingSegment segment in segments)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"#EXTINF:{segment.DurationSeconds.ToString("0.######", CultureInfo.InvariantCulture)},\n");
            sb.Append(segment.FileName).Append(suffix).Append('\n');
        }

        // No EXT-X-ENDLIST: its absence is the whole difference. It is what tells the player to
        // come back for this playlist again rather than stopping at the last segment in it.
        return sb.ToString();
    }

    /// <summary>
    /// The sequence number ffmpeg gave a segment, read out of its filename
    /// (<c>seg-{stamp}-{NNNNN}.m4s</c>, written by <c>-hls_segment_filename</c>).
    ///
    /// <para>It is taken from the name rather than counted here because it has to mean the same
    /// thing on the next request, when the window has moved on and this playlist starts at a
    /// different segment. ffmpeg's counter is the only numbering that both sides already agree on.
    /// Returns 0 for anything that does not parse, which costs a player one reload rather than a
    /// failure.</para>
    /// </summary>
    public static int SequenceOf(string fileName)
    {
        int lastDash = fileName.LastIndexOf('-');
        int dot = fileName.LastIndexOf('.');

        return lastDash >= 0 && dot > lastDash
            && int.TryParse(
                fileName.AsSpan(lastDash + 1, dot - lastDash - 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int sequence)
            ? sequence
            : 0;
    }

    /// <summary>
    /// The same playlist with <paramref name="streamToken"/> appended to every URI in it.
    ///
    /// <para>For serving a playlist ffmpeg wrote — the preview ring's — rather than one built here.
    /// A player resolves the relative names in it against the playlist's own URL, and RFC 3986
    /// drops the query when it does, so without this the playlist loads and every segment is then
    /// refused. Exactly the trap <see cref="BuildVod"/> describes; this is it applied to text
    /// somebody else produced.</para>
    ///
    /// <para>A URI is any line that is not blank and does not begin with <c>#</c>, plus the one
    /// inside <c>EXT-X-MAP</c>. Nothing else in a playlist names a file.</para>
    /// </summary>
    public static string WithStreamToken(string playlist, string? streamToken)
    {
        if (string.IsNullOrEmpty(streamToken))
        {
            return playlist;
        }

        string suffix = $"?stream_token={Uri.EscapeDataString(streamToken)}";
        var sb = new StringBuilder();

        foreach (string line in playlist.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');

            if (trimmed.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                // URI="init-….mp4" — the only tag that names a file.
                int open = trimmed.IndexOf('"');
                int close = open < 0 ? -1 : trimmed.IndexOf('"', open + 1);

                sb.Append(close < 0
                    ? trimmed
                    : string.Concat(
                        trimmed.AsSpan(0, close), suffix, trimmed.AsSpan(close)));
            }
            else if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
            {
                sb.Append(trimmed).Append(suffix);
            }
            else
            {
                sb.Append(trimmed);
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads the segments and their true durations out of a live playlist ffmpeg wrote, in order.
    ///
    /// This is how a recording is indexed, and it has to come from the playlist rather than be
    /// computed from <c>SegmentSeconds</c>. Under <c>-c:v copy</c> — now the default — ffmpeg
    /// cannot honour <c>hls_time</c> exactly, because it can only cut at a keyframe the source
    /// already contains. A camera with a 10-second GOP and a 4-second segment target produces
    /// 10-second segments, and an index that assumed 4 would drift by six seconds per segment:
    /// within an hour, seeks land half an hour from where they were asked to, and the retention
    /// sweep prunes by a timestamp that is not the segment's.
    ///
    /// Parsing stops at the first entry that does not parse rather than skipping it. ffmpeg
    /// rewrites this file in place, so a read can catch it mid-write; skipping a torn entry would
    /// silently shift every later segment's start time, whereas stopping just defers them to the
    /// next pass.
    /// </summary>
    public static IReadOnlyList<(string FileName, double DurationSeconds)> ParseSegments(string playlist)
    {
        var segments = new List<(string, double)>();
        string[] lines = playlist.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (!line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                continue;
            }

            // "#EXTINF:4.000000," — the trailing comma separates an optional title.
            string value = line["#EXTINF:".Length..].Split(',')[0];
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double duration))
            {
                break;
            }

            // The URI is the next line that is neither blank nor a tag.
            string? uri = null;
            for (int j = i + 1; j < lines.Length; j++)
            {
                string candidate = lines[j].Trim();
                if (candidate.Length == 0)
                {
                    continue;
                }

                uri = candidate.StartsWith('#') ? null : candidate;
                i = j;
                break;
            }

            if (uri is null)
            {
                break;
            }

            segments.Add((uri, duration));
        }

        return segments;
    }
}
