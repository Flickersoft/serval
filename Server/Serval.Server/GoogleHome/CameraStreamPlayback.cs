using Microsoft.Extensions.Options;
using Serval.Server.Cameras;
using Serval.Server.Configuration;
using Serval.Server.Recordings;

namespace Serval.Server.GoogleHome;

/// <summary>
/// The HLS half of the camera stream: a live playlist and the segments behind it, fetched by a
/// Cast Web Receiver on a Google TV.
///
/// <para><b>Why this exists when WebRTC already works.</b> Casting does not use WebRTC. Google
/// launches a Cast Web Receiver on the device, and that receiver plays <c>hls</c>, <c>dash</c>,
/// <c>smooth_stream</c> or <c>progressive_mp4</c> and nothing else — so a camera offering WebRTC
/// alone is never even asked, and the option to show it on a TV simply does not appear. WebRTC
/// remains the preferred protocol and is what every other surface gets; this is the fallback for
/// the one that cannot take it.</para>
///
/// <para><b>Nothing new is recorded or transcoded.</b> These are the fMP4 segments the recorder is
/// already writing — H.264 and AAC, which is exactly what a Cast receiver wants — served from disk.
/// The only thing generated per request is the playlist text. A TV watching therefore costs a file
/// read, and the camera nothing at all.</para>
///
/// <para><b>The credential is in the URL, and has to be.</b> The generic Cast receiver cannot set
/// an <c>Authorization</c> header — Google's own note is that auth tokens work only with a custom
/// receiver — which is why <c>cameraStreamNeedAuthToken</c> is false and the ticket rides as
/// <c>?t=</c>. WebRTC is unaffected: its signaling URL already carried the ticket too, and its
/// handler accepts either form.</para>
///
/// <para><b>This serves the live edge and only the live edge.</b> The playlist is built from the
/// last few seconds of the recording index, so the ticket cannot be turned into a window onto the
/// archive — a segment older than the window is simply not named, and a name that is not in the
/// index is not served.</para>
/// </summary>
public static class CameraStreamPlayback
{
    public static void MapCameraStreamPlayback(this RouteGroupBuilder group)
    {
        group.MapGet("/camerastream/hls/{cameraId}/index.m3u8", async (
            string cameraId,
            string? t,
            GoogleHomeGate gate,
            CameraStreamTicketService tickets,
            RecordingIndex recordings,
            IOptions<ServerOptions> options,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (!gate.IsEffective)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            ILogger logger = loggers.CreateLogger("Serval.Server.GoogleHome.Playback");

            if (Ticketed(tickets, t, cameraId) is not string id)
            {
                logger.LogWarning(
                    "Google Home HLS playlist refused for {CameraId}: bad or expired ticket.",
                    cameraId);
                return Results.Unauthorized();
            }

            // The preview ring first, for the reason MediaEndpoints.PreviewPlaylist gives: a Cast
            // receiver decodes H.264 to Level 4.2, and every recorded stream here is well past it.
            if (Media.MediaEndpoints.PreviewPlaylist(options.Value.Media.Root, id, t)
                is string preview)
            {
                return Results.Text(preview, HlsPlaylist.ContentType);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;

            // `to` is pushed past now because a segment's StartedAt is the instant it began, and
            // the newest one always began before this request.
            List<RecordingSegment> segments =
                await recordings.InRangeAsync(id, now - LiveWindow.Span, now.AddMinutes(1), ct);

            List<RecordingSegment> window = LiveWindow.NewestSession(segments);
            if (window.Count == 0)
            {
                // The camera records but nothing has landed yet — ffmpeg has not closed a segment,
                // or recording is switched off. 404 rather than an empty playlist, which a player
                // would treat as a stream that has ended.
                logger.LogInformation(
                    "Google Home HLS playlist for {CameraId}: no recent segments.", id);
                return Results.NotFound();
            }

            return Results.Text(
                HlsPlaylist.BuildLive(window, HlsPlaylist.SequenceOf(window[0].FileName), t),
                HlsPlaylist.ContentType);
        })
            .WithSummary("A live HLS playlist for one camera, fetched by a Cast receiver.")
            .WithDescription(
                "Built from the last few seconds of the recording index over the segments the "
                + "recorder is already writing — nothing is transcoded and nothing extra is "
                + "stored. Authenticated by the camera-scoped ticket in ?t=, because the generic "
                + "Cast receiver cannot send an Authorization header.")
            .AllowAnonymous()

            // Chromecast is strict about CORS on adaptive streaming, and the receiver fetching
            // these is served from Google's origin, so every request here is cross-origin. Same
            // policy as signaling, which also carries the App's own origins.
            .RequireCors("google-signaling");

        // Media and initialisation segments. Two routes rather than one because the HLS muxer
        // writes .m4s media and a .mp4 initialisation segment, and the fixed extensions are also
        // half of what stops these routes serving arbitrary files.
        group.MapGet("/camerastream/hls/{cameraId}/{file}.m4s", (
            string cameraId, string file, string? t, GoogleHomeGate gate,
            CameraStreamTicketService tickets, IOptions<ServerOptions> options) =>
            Segment(cameraId, file, "m4s", t, gate, tickets, options))
            .AllowAnonymous()
            .RequireCors("google-signaling")
            .ExcludeFromDescription();

        group.MapGet("/camerastream/hls/{cameraId}/{file}.mp4", (
            string cameraId, string file, string? t, GoogleHomeGate gate,
            CameraStreamTicketService tickets, IOptions<ServerOptions> options) =>
            Segment(cameraId, file, "mp4", t, gate, tickets, options))
            .AllowAnonymous()
            .RequireCors("google-signaling")
            .ExcludeFromDescription();
    }

    private static IResult Segment(
        string cameraId,
        string file,
        string extension,
        string? ticket,
        GoogleHomeGate gate,
        CameraStreamTicketService tickets,
        IOptions<ServerOptions> options)
    {
        if (!gate.IsEffective)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (Ticketed(tickets, ticket, cameraId) is not string id || !IsSafeSegmentName(file))
        {
            return Results.NotFound();
        }

        string path = Path.Combine(options.Value.Media.Root, id, $"{file}.{extension}");
        return File.Exists(path)
            ? Results.File(path, "video/mp4")
            : Results.NotFound();
    }

    /// <summary>
    /// The camera a ticket is good for, but only if that is the camera being asked for.
    ///
    /// <para>The ticket alone names the camera, so the id in the path is not what grants anything —
    /// it is checked so that a ticket for one camera cannot be pointed at another's directory, the
    /// same cross-check the signaling handler makes against <c>deviceId</c>.</para>
    /// </summary>
    private static string? Ticketed(CameraStreamTicketService tickets, string? ticket, string cameraId)
    {
        if (!CameraRepository.IsSafeId(cameraId))
        {
            return null;
        }

        string? ticketed = tickets.TrySpend(ticket);
        return string.Equals(ticketed, cameraId, StringComparison.Ordinal) ? ticketed : null;
    }

    /// <summary>
    /// Segment names as ffmpeg writes them and no others. Belt and braces beside the extension in
    /// the route: the path is composed from this, so nothing that could climb out of the camera's
    /// directory may reach it.
    /// </summary>
    private static bool IsSafeSegmentName(string file) =>
        file.Length is > 0 and <= 64
        && file.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
