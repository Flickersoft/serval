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
