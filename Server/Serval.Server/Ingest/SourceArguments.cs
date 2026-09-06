using System.Globalization;

namespace Serval.Server.Ingest;

/// <summary>
/// The ffmpeg input arguments for a source URL, chosen by its scheme.
///
/// This exists because several of ffmpeg's input options are demuxer-private:
/// <c>-rtsp_transport</c> and <c>-allowed_media_types</c> belong to the RTSP demuxer, and passing
/// either to an HTTP-FLV, RTMP or SRT input makes ffmpeg exit immediately with
/// <c>Option rtsp_transport not found</c>. Deciding per scheme, in one place, is what keeps that
/// from being rediscovered at each call site. Branching on anything coarser — "is this a file?" —
/// breaks every non-RTSP camera, retrying forever with the failure visible only in the logs.
///
/// Pure and process-free, so the argument construction is unit-testable.
/// </summary>
internal static class SourceArguments
{
    /// <summary>
    /// Socket I/O deadline for a live session's input. A camera that stops answering without
    /// closing its connection leaves ffmpeg in <c>poll()</c> forever — the peer vanished without a
    /// FIN and an RTSP session carries no keepalive to discover that — so without a deadline the
    /// process outlives every attempt to replace it. Sits below
    /// <see cref="IngestOptions.StallTimeoutSeconds"/> so ffmpeg reports the failure itself rather
    /// than the watchdog taking the blame for it, and above the longest normal gap between packets.
    /// </summary>
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The same deadline for a probe, which is shorter because it has no stall watchdog behind it:
    /// a probe runs before there is a stream to watch, so this is the only bound on it.
    /// </summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Schemes the server knows how to pull. Anything else is rejected at validation.</summary>
    private static readonly IReadOnlySet<string> SupportedSchemes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "rtsp", "rtsps", "http", "https", "rtmp", "rtmps", "srt",
        };

    /// <summary>
    /// Input arguments up to and including <c>-i &lt;url&gt;</c>.
    /// </summary>
    /// <param name="url">The stream's source URL, or a local path for a test file camera.</param>
    /// <param name="audioOnly">
    /// Set by the AI audio tap, which wants sound and nothing else. Over RTSP this is free — the
    /// video stream is never set up, so the camera does not packetize or send it. No other
    /// protocol offers that, so a non-RTSP source still transports video for the caller to
    /// discard with <c>-map 0:a?</c>: correct, but it costs the bandwidth an RTSP source wouldn't.
    /// </param>
    public static IReadOnlyList<string> InputArgs(string url, bool audioOnly = false)
    {
        if (IsFile(url))
        {
            // Loop the file forever, paced to realtime so it behaves like a live camera. A local
            // read cannot half-open, so there is no timeout worth setting on one.
            return ["-stream_loop", "-1", "-re", "-i", url];
        }

        if (IsRtsp(url))
        {
            // TCP transport is far more reliable than UDP over anything but a pristine LAN.
            List<string> args = ["-rtsp_transport", "tcp"];
            if (audioOnly)
            {
                args.AddRange(["-allowed_media_types", "audio"]);
            }

            args.AddRange(TimeoutArgs(url, SourceTimeout));
            args.AddRange(["-i", url]);
            return args;
        }

        // http(s) / rtmp(s) / srt: the demuxer's defaults are what we want, and none of the RTSP
        // options apply.
        return [.. TimeoutArgs(url, SourceTimeout), "-i", url];
    }

    /// <summary>
    /// The same input options for ffprobe, which shares ffmpeg's demuxer options but not its
    /// output-pacing ones: <c>-stream_loop</c> and <c>-re</c> are ffmpeg-only and make ffprobe
    /// exit with an unrecognised-option error, so a file source gets nothing but its path.
    /// </summary>
    public static IReadOnlyList<string> ProbeArgs(string url) =>
        IsRtsp(url)
            ? ["-rtsp_transport", "tcp", .. TimeoutArgs(url, ProbeTimeout), "-i", url]
            : [.. TimeoutArgs(url, ProbeTimeout), "-i", url];

    /// <summary>
    /// The demuxer's socket I/O timeout, in microseconds, or nothing when the caller passed zero or
    /// the source is a local file.
    ///
    /// Which option carries it is scheme-private, the same trap the rest of this class exists for:
    /// the RTSP demuxer declares its own <c>-timeout</c>, while every other network scheme takes
    /// the generic AVIO <c>-rw_timeout</c>. Both must precede <c>-i</c> to bind to the input.
    ///
    /// An either/or rather than a pair, in both directions. <c>-rw_timeout</c> is silently ignored
    /// by the RTSP demuxer, so it would buy nothing there. <c>-timeout</c> on RTMP is a different
    /// option that happens to share the name — listen-seconds, implying <c>-rtmp_listen 1</c> —
    /// so sending it there would turn a camera pull into a server waiting to be connected to.
    /// </summary>
    private static IReadOnlyList<string> TimeoutArgs(string url, TimeSpan readTimeout)
    {
        if (readTimeout <= TimeSpan.Zero || IsFile(url))
        {
            return [];
        }

        string microseconds = ((long)readTimeout.TotalMicroseconds)
            .ToString(CultureInfo.InvariantCulture);

        return IsRtsp(url) ? ["-timeout", microseconds] : ["-rw_timeout", microseconds];
    }

    /// <summary>
    /// True for <c>rtsp://</c> and <c>rtsps://</c>. Callers use this for the handful of behaviours
    /// that are genuinely RTSP-only — go2rtc's <c>#backchannel</c> option, for instance.
    /// </summary>
    public static bool IsRtsp(string url) =>
        Scheme(url) is { } scheme && scheme is "rtsp" or "rtsps";

    /// <summary>
    /// True when the URL is a local path rather than a network source — a test file camera. A
    /// bare path has no scheme; <c>file://</c> is accepted for the same thing written out.
    /// </summary>
    public static bool IsFile(string url) => Scheme(url) is null or "file";

    /// <summary>Whether the server can pull this URL at all. Drives validation, not arguments.</summary>
    public static bool IsSupported(string url) =>
        Scheme(url) is not { } scheme || scheme == "file" || SupportedSchemes.Contains(scheme);

    /// <summary>
    /// The lowercased URL scheme, or null when there isn't one. Deliberately not
    /// <see cref="Uri.TryCreate(string, UriKind, out Uri)"/>: a Windows-style path parses as the
    /// <c>c</c> scheme, and camera URLs routinely carry characters <see cref="Uri"/> rejects.
    /// </summary>
    private static string? Scheme(string url)
    {
        int colon = url.IndexOf("://", StringComparison.Ordinal);
        if (colon <= 0)
        {
            return null;
        }

        string scheme = url[..colon];
        return scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.')
            ? scheme.ToLowerInvariant()
            : null;
    }
}
