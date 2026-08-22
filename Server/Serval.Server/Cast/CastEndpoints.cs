using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Serval.Server.Configuration;
using Serval.Server.Cameras;
using Serval.Server.GoogleHome;
using Serval.Server.Ingest;
using Serval.Server.Media;
using Serval.Server.Recordings;

namespace Serval.Server.Cast;

/// <summary>
/// What the App needs to put a camera on a Chromecast: which receiver application to launch, and a
/// URL that receiver can open.
///
/// <para><b>Why the App casts at all, when Google Home already can.</b> It turned out it cannot,
/// for most televisions. Google routes camera streams to Nest displays and to the Home app, and
/// refuses televisions outright — the fulfillment endpoint is never even called, and the same
/// refusal happens for other vendors' certified integrations, so it is not something a Serval
/// change or a certification could unlock. Casting from the App skips the Assistant entirely and
/// talks to the Cast device directly, which is the only path to a television that exists.</para>
///
/// <para><b>It hands back the same URL shape Google sends.</b> The receiver therefore has one code
/// path and one fallback whichever caller launched it — see <see cref="CameraStreamReceiver"/>.
/// That is worth more than a tidier App-specific payload would be: the Cast path is hard to
/// exercise, and two of them would mean the one nobody tests is the one that breaks.</para>
///
/// <para><b>Authenticated, unlike everything else that touches these tickets.</b> The Google routes
/// are anonymous because Google holds no Serval session; this caller is the App, which does, so
/// there is no reason to widen anything. The ticket it mints is the ordinary camera-scoped playback
/// ticket, with the lifetime and limits <see cref="CameraStreamTicketService"/> already
/// defines.</para>
/// </summary>
public static class CastEndpoints
{
    public static void MapCastEndpoints(this IEndpointRouteBuilder app)
    {
        // Which receiver this deployment casts with, and nothing else.
        //
        // Separate from the POST below because the App needs it *before* anybody presses anything:
        // Google's sender SDK only discovers devices once it has been told which application to
        // look for, so without this there is no discovery, no receiver is ever found, and the
        // button that would have minted a ticket never appears to be pressed. It mints nothing and
        // names no camera, so it is a plain read.
        app.MapGet("/api/cast/receiver", (GoogleHomeGate gate) =>
            !gate.IsEffective || gate.CastReceiverAppId is not string appId
                ? Results.NotFound()
                : Results.Ok(new CastReceiver(appId)))
            .WithSummary("The Cast application this deployment casts with, if any.")
            .WithDescription(
                "404 when no Cast application is registered, which is the default and a working "
                + "deployment — the App simply offers no Cast button. Read by the camera screen on "
                + "open, because device discovery cannot start until the sender SDK knows which "
                + "application to look for.")
            .Produces<CastReceiver>()
            .RequireAuthorization();

        app.MapPost("/api/cameras/{id}/cast", async (
            string id,
            CameraRepository cameras,
            GoogleHomeGate gate,
            CameraStreamTicketService tickets,
            CancellationToken ct) =>
        {
            if (!CameraRepository.IsSafeId(id))
            {
                return Results.NotFound();
            }

            // The same eligibility Go2RtcSyncWorker registers streams by, and asked for the same
            // reason SYNC asks it: a camera go2rtc has no stream for is not a degraded cast, it is
            // a receiver that launches, negotiates nothing and sits on a blank screen. Better to
            // have no button than a button that does that.
            List<Camera> all = await cameras.ListAsync(ct);
            if (!all.Any(c =>
                    string.Equals(c.Id, id, StringComparison.Ordinal)
                    && Go2RtcSyncWorker.IsWebRtcEligible(c)))
            {
                return Results.NotFound();
            }

            // The receiver is served under the Google Home route group and behind its gate, so a
            // deployment that has not configured that has nothing to cast to. 503 rather than 404:
            // the feature exists, this server is not offering it, and that is the same answer
            // WebRtcEndpoints gives when its own switch is off.
            if (!gate.IsEffective || gate.PublicBaseUri is not Uri publicBase)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (gate.CastReceiverAppId is not string appId)
            {
                // No Cast application registered. Distinct from the 503 above because the fix is
                // different and the App says so: this one is "register a receiver", not "configure
                // Google Home".
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }

            string ticket = tickets.MintForPlayback(id);

            return Results.Ok(new CastTarget(
                ReceiverAppId: appId,
                ContentUrl: SmartHomeFulfillment.HlsUrl(publicBase, id, ticket)));
        })
            .WithSummary("Where to cast one camera, and the credential to do it with.")
            .WithDescription(
                "Returns the Cast application to launch and a URL for it to open. The URL is the "
                + "same one the Google Home integration hands a Cast device, so the receiver "
                + "behaves identically whichever launched it: it negotiates WebRTC against this "
                + "server and plays the URL as HLS only if that fails.\n\n"
                + "404 for a camera that is disabled or has no live stream, since go2rtc has no "
                + "stream for it to negotiate against. 503 when the receiver is not being served — "
                + "the Google Home configuration it sits behind is incomplete. 501 when that is "
                + "fine but no Cast application has been registered, which is the default.")
            .Produces<CastTarget>()
            .RequireAuthorization();

        // A recording, as a television can play it.
        //
        // Deliberately beside /vod.m3u8 rather than a flag on it: the App's own player wants the
        // recording untouched, and a Cast device cannot decode it at all. Same window, same index,
        // same durations — the segments differ, and only because they have to.
        app.MapGet("/api/cameras/{id}/cast.m3u8", async (
            string id,
            DateTimeOffset from,
            DateTimeOffset to,
            DateTimeOffset? at,
            int? maxh,
            RecordingIndex recordings,
            IOptions<ServerOptions> options,
            HttpContext context,
            CancellationToken ct) =>
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

            // Two ceilings, and the lower wins. maxh is the receiver's own answer about its screen,
            // put on this URL before CAF fetched it — see the LOAD interceptor in player.html.
            // CastMaxHeight is what this deployment will actually encode and ship in time; a screen
            // that reports 2160 is telling the truth about the panel and nothing about the network.
            int ceiling = options.Value.GoogleHome.CastMaxHeight;
            int? height = maxh is int asked ? Math.Min(asked, ceiling) : ceiling;

            // The window is what the playlist covers; `at` is only where to open it. They are
            // separate because casting the whole visible timeline is what makes scrubbing a seek
            // rather than a reload — the App sends the timeline's own bounds and the playhead
            // inside them, so a click anywhere on the bar is already in the media.
            return Results.Text(
                HlsPlaylist.BuildCastVod(
                    segments, at ?? from, context.Request.Query["stream_token"], height),
                HlsPlaylist.ContentType);
        })
            .WithSummary("A VOD playlist over a past window, for a Cast device.")
            .WithDescription(
                "The same window /vod.m3u8 serves, as MPEG-TS segments re-encoded to 1080p H.264 — "
                + "which is the most a Cast device decodes, and less than any camera here records. "
                + "Nothing is transcoded until a segment is actually asked for.")
            .RequireAuthorization("MediaAccess");

        app.MapGet("/api/cameras/{id}/cast/{file}.ts", async (
            string id,
            string file,
            int? n,
            double? o,
            double? d,
            int? h,

            // Explicit, not inferred. A complex type on a GET that the binder does not recognise as
            // a service is inferred as a *body*, which minimal APIs reject at route-building time —
            // and routes bind at boot, so the failure is a crash loop rather than a compile error.
            // This deployment has been taken down that way once already.
            [FromServices] CastTranscoder transcoder,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!CameraRepository.IsSafeId(id) || !IsSafeSegmentName(file))
            {
                return Results.NotFound();
            }

            // Written directly to the response rather than buffered: a segment is a couple of
            // megabytes and the player wants its first bytes while ffmpeg is still producing the
            // rest. There is no length to declare for the same reason.
            context.Response.ContentType = "video/mp2t";

            try
            {
                await transcoder.WriteSegmentAsync(
                    id, file, n ?? 1, o ?? 0, d, h, context.Response.Body, ct);
            }
            catch (FileNotFoundException)
            {
                // Retention caught up with the window, or the playlist outlived its footage. The
                // response has not been written to yet, so this is still an honest 404.
                return Results.NotFound();
            }

            return Results.Empty;
        })
            .WithSummary("One recorded segment, re-encoded for a Cast device.")
            .WithDescription(
                "Transcoded on request and never stored. `n` is how many recorded segments this one "
                + "covers — fed to a single ffmpeg, which pays the process and GPU setup once for "
                + "the batch. `o` is where the batch sits in its playlist: ffmpeg normalises "
                + "timestamps per run, so without it every batch claims to start at the same "
                + "instant and a seek lands nowhere. `d` is the slot the playlist declared for it, "
                + "which the encode is trimmed to: a batch that runs even a frame long starts the "
                + "next one before it has finished, and a Cast device stops on the backwards "
                + "timestamp that makes.")
            .ExcludeFromDescription()
            .RequireAuthorization("MediaAccess");
    }

    /// <summary>
    /// Segment names as the recorder writes them and nothing else. The path is composed from this,
    /// so nothing that could climb out of the camera's directory may reach it.
    /// </summary>
    private static bool IsSafeSegmentName(string file) =>
        file.Length is > 0 and <= 64
        && file.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <param name="ReceiverAppId">
/// The Cast application to launch, from <c>Serval:GoogleHome:CastReceiverAppId</c>. Registered by
/// the operator against their own server's receiver URL, so it is per-deployment and never shipped.
/// </param>
/// <param name="ContentUrl">
/// What to hand the receiver. Carries the camera in its path and a camera-scoped ticket in its
/// query, and is a real playlist — which is what makes it usable as the receiver's own fallback.
/// </param>
public sealed record CastTarget(string ReceiverAppId, string ContentUrl);

/// <param name="ReceiverAppId">
/// The Cast application to look for and, later, to launch. Registered by the operator against their
/// own server's receiver URL, so it is per-deployment and never shipped.
/// </param>
public sealed record CastReceiver(string ReceiverAppId);
