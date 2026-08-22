using Microsoft.Extensions.Options;
using Serval.Server.Cameras;
using Serval.Server.Configuration;
using Serval.Server.Ingest;
using Serval.Server.Recordings;
using Serval.Server.Snapshots;

namespace Serval.Server.Media;

/// <summary>
/// Everything the App fetches to see video: the latest still, the live HLS playlist and its fMP4
/// segments, a VOD playlist synthesised for any past time range, and a standalone MP4 export.
/// Segment files are shared between live and VOD — a VOD playlist just references the same
/// <c>.m4s</c> files still on disk. Everything is one codec in one container, so the frontend has
/// a single decode path, and each segment carries video and audio together.
/// </summary>
public static class MediaEndpoints
{
    private const string SegmentContentType = "video/mp4";

    public static void MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/cameras/{id}").WithTags("Media");

        // Latest still for the dashboard tile / initial paint, from memory.
        //
        // No on-disk fallback: snapshots are read and deleted as they are written, so there is no
        // longer a lasting file to serve — see SnapshotWatcher, which numbers them by frame so a
        // detection can be dated from the picture rather than from when the Server got to it. The
        // cost is a 404 between a Server restart and the first frame of the session, which at
        // Ingest:SnapshotFps is about a second.
        //
        // MediaAccess, like the routes below and for the same reason: this is a push notification's
        // picture, and the browser fetches that itself with no Authorization header available to
        // it. AlertNotifier.Compose puts a ?stream_token= in the URL for exactly that, and only the
        // "StreamToken" scheme — which only this policy lists — reads one.
        group.MapGet("/snapshot.jpg", (string id, SnapshotBroadcaster snapshots) =>
        {
            if (!CameraRepository.IsSafeId(id))
            {
                return Results.NotFound();
            }

            return snapshots.Latest(id) is { } snapshot
                ? Results.Bytes(snapshot.Jpeg, "image/jpeg")
                : Results.NotFound();
        })
            .RequireAuthorization("MediaAccess");

        // Media segments, requested by a player resolving a playlist entry — .m4s for the media
        // segments and .mp4 for the EXT-X-MAP initialisation segment, which the HLS muxer writes
        // with that extension. One handler: the fixed name patterns are also what keep these
        // routes from serving arbitrary files.
        IResult Segment(string id, string file, string extension, IOptions<ServerOptions> options)
        {
            if (!CameraRepository.IsSafeId(id) || !IsSafeSegmentName(file))
            {
                return Results.NotFound();
            }

            string path = Path.Combine(options.Value.Media.Root, id, $"{file}.{extension}");
            return File.Exists(path)
                ? Results.File(path, SegmentContentType)
                : Results.NotFound();
        }

        group.MapGet("/{file}.m4s", (string id, string file, IOptions<ServerOptions> options) =>
            Segment(id, file, "m4s", options))
            .RequireAuthorization("MediaAccess");

        group.MapGet("/{file}.mp4", (string id, string file, IOptions<ServerOptions> options) =>
            Segment(id, file, "mp4", options))
            .RequireAuthorization("MediaAccess");

        // VOD playlist for a past window, built from the recording index over the same segments
        // the live view uses.
        group.MapGet("/vod.m3u8",
            async (string id, DateTimeOffset from, DateTimeOffset to, RecordingIndex recordings,
                   HttpContext context, CancellationToken ct) =>
        {
            if (!CameraRepository.IsSafeId(id))
            {
                return Results.NotFound();
            }

            List<RecordingSegment> segments = await recordings.InRangeAsync(id, from, to, ct);
            if (segments.Count == 0)
            {
                return Results.NotFound();
            }

            // `from` is passed so the playlist carries an EXT-X-START for a window that begins
            // mid-segment — the App asks for an instant, not a segment boundary.
            //
            // The stream token is passed straight back through into every segment URI: a player
            // resolves those relative names against this playlist's URL, and that drops the query
            // string, so without this the playlist loads and then every segment 401s. Null when
            // the caller used an Authorization header instead, which it can set on segments too.
            return Results.Text(
                HlsPlaylist.BuildVod(segments, from, context.Request.Query["stream_token"]),
                HlsPlaylist.ContentType);
        })
            .WithSummary("A VOD playlist over a past window.")
            .WithDescription(
                "Synthesised from the recording index over the same segment files the live view "
                + "uses. Carries EXT-X-START when the window begins mid-segment.")
            .RequireAuthorization("MediaAccess");

        // A standalone, downloadable MP4 of a time range, video and audio in one file that plays
        // anywhere with no init segment and no playlist.
        //
        // This is the only place a remux happens. Storing standalone files instead would mean
        // remuxing on every playback request, for every viewer — whereas exporting a clip is
        // something a person does occasionally, so the cost belongs here.
        group.MapGet("/clip.mp4",
            async (string id, DateTimeOffset from, DateTimeOffset to,
                   RecordingIndex recordings, ClipExporter exporter,
                   HttpContext context, CancellationToken ct) =>
        {
            if (!CameraRepository.IsSafeId(id))
            {
                return Results.NotFound();
            }

            List<RecordingSegment> segments = await recordings.InRangeAsync(id, from, to, ct);
            if (segments.Count == 0)
            {
                return Results.NotFound();
            }

            // What the file will actually contain, worked out before a byte is written — the
            // export stops at a session boundary, and headers cannot be set once the body is
            // streaming. Without this the client is handed a 200 and a clip quietly shorter than
            // the window it asked for, which looks like missing footage rather than a restart.
            IReadOnlyList<RecordingSegment> run = ClipExporter.LeadingRun(segments);
            RecordingSegment last = run[^1];

            context.Response.Headers.ContentDisposition =
                $"attachment; filename=\"{id}-{from:yyyyMMdd-HHmmss}.mp4\"";
            context.Response.Headers["X-Serval-Clip-From"] = run[0].StartedAt.ToString("O");
            context.Response.Headers["X-Serval-Clip-To"] =
                last.StartedAt.AddSeconds(last.DurationSeconds).ToString("O");
            context.Response.Headers["X-Serval-Clip-Truncated"] =
                run.Count < segments.Count ? "true" : "false";

            await exporter.WriteAsync(id, segments, context.Response.Body, ct);
            return Results.Empty;
        })
            .WithSummary("A standalone MP4 of a time range.")
            .WithDescription(
                "Carries X-Serval-Clip-From / -To (what the file actually covers) and "
                + "X-Serval-Clip-Truncated, set when the range crossed an ffmpeg session restart "
                + "and the export stopped at that boundary. Browsers can only read those through "
                + "the CORS policy's exposed headers.")
            .RequireAuthorization();

        // The raw index of recorded segments in a window — for a timeline/scrubber UI.
        group.MapGet("/recordings",
            async (string id, DateTimeOffset from, DateTimeOffset to, RecordingIndex recordings, CancellationToken ct) =>
        {
            if (!CameraRepository.IsSafeId(id))
            {
                return Results.NotFound();
            }

            List<RecordingSegment> segments = await recordings.InRangeAsync(id, from, to, ct);
            return Results.Ok(segments.Select(s => new
            {
                s.FileName,
                startedAt = s.StartedAt,
                s.DurationSeconds,
                s.InitFileName,
            }));
        })
            .WithSummary("The raw segment index for a window.")
            .WithDescription(
                "One row per segment. Use /coverage instead for a scrubber: a day of a "
                + "4-second-segment camera is ~21,600 rows here.\n\n"
                + "This is the route a clip trimmer wants, though, because a segment is the smallest "
                + "thing that can be copied without re-encoding — so these boundaries are the "
                + "positions a trim handle can actually land on. `initFileName` identifies the "
                + "recording session: a selection whose segments do not all share one cannot be "
                + "saved as a single clip.")
            .RequireAuthorization();

        // The same information merged — where footage exists, which is what a scrubber draws.
        group.MapGet("/coverage",
            async (string id, DateTimeOffset from, DateTimeOffset to, RecordingIndex recordings, CancellationToken ct) =>
        {
            if (!CameraRepository.IsSafeId(id))
            {
                return Results.NotFound();
            }

            // An empty array, not a 404. "This camera recorded nothing in this window" is a valid
            // answer a timeline can draw, and the App must not have to tell it apart from a
            // failure. /vod.m3u8 404s because there is genuinely nothing to play; this differs.
            return Results.Ok(await recordings.CoverageAsync(id, from, to, ct));
        })
            .WithSummary("Contiguous runs of recorded footage in a window.")
            .WithDescription(
                "What /recordings says, collapsed. That route returns one row per segment — "
                + "measured against a live camera, two hours is 1798 rows and 208 KB — which is an "
                + "expensive way to learn that the camera recorded all day. This returns one span "
                + "per ffmpeg session, typically one to three for a day.")
            .RequireAuthorization();
    }

    /// <summary>
    /// Only the shapes ffmpeg writes — <c>init-&lt;stamp&gt;</c>, <c>seg-&lt;stamp&gt;-&lt;n&gt;</c>,
    /// and the preview ring's <c>preview-init-&lt;stamp&gt;</c> and
    /// <c>preview-&lt;stamp&gt;-&lt;n&gt;</c> — and no path separators to escape the directory.
    ///
    /// <para>The ring's prefix belongs here because the Google Home playback route serves that
    /// playlist: without it the playlist is fetched happily and every segment in it 404s, which
    /// reads on a television as a stream that connected and then showed nothing.</para>
    /// </summary>
    internal static bool IsSafeSegmentName(string file) =>
        (file.StartsWith("init-", StringComparison.Ordinal)
            || file.StartsWith("seg-", StringComparison.Ordinal)
            || file.StartsWith(PreviewRing.FilePrefix, StringComparison.Ordinal))
        && file.All(c => char.IsAsciiLetterOrDigit(c) || c is '-');

    /// <summary>
    /// The live HLS a Cast device is given: the preview ring, not the recording.
    ///
    /// <para><b>Why not the recording.</b> The recording is the camera's main stream copied
    /// untouched — 4K HEVC on one of these cameras, 7680x2160 on another, 1920x2560 on the
    /// doorbell. A Cast receiver decodes H.264 to Level 4.2, which is 1080p; every one of those is
    /// beyond it. The receiver fetches the playlist, fetches the segments, decodes nothing, and
    /// sits on the title card with no error to report — which is exactly what it did.</para>
    ///
    /// <para>The preview ring is the <em>detect</em> stream copied instead: 640x360 H.264 with AAC,
    /// already written, already a rolling window with its own init, and already sized for exactly
    /// this. It costs nothing extra because it is being written whether anybody casts or not — see
    /// <see cref="PreviewRing"/> for why it exists.</para>
    ///
    /// <para>Null when there is no ring. A camera whose detect stream <em>is</em> its recorded
    /// stream writes none, because the recording already is those bytes — the caller falls back to
    /// the recording index there, and on such a camera the recorded stream is the small one
    /// anyway.</para>
    /// </summary>
    internal static string? PreviewPlaylist(string mediaRoot, string cameraId, string? streamToken)
    {
        string path = Path.Combine(mediaRoot, cameraId, PreviewRing.PlaylistName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            // ffmpeg rewrites this file in place, so a read can catch it mid-write. A torn playlist
            // is not worth serving; the player asks again within a segment's time either way.
            string playlist = File.ReadAllText(path);
            return playlist.Contains("#EXTM3U", StringComparison.Ordinal)
                ? HlsPlaylist.WithStreamToken(playlist, streamToken)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
