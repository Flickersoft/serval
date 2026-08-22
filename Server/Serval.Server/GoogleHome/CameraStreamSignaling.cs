using Microsoft.Extensions.Primitives;
using Serval.Server.Ingest;

namespace Serval.Server.GoogleHome;

/// <summary>
/// Where Google's cloud exchanges SDP for one camera, and the last place this server is involved
/// in a Google Home stream.
///
/// <para><b>The handler is thin because <c>Go2RtcClient.ExchangeSdpAsync</c> already is the whole
/// job.</b> It takes raw SDP, returns raw SDP, and identifies the stream by name — which is the
/// camera id, because that is what <c>Go2RtcSyncWorker</c> registers. So this unwraps Google's
/// JSON, calls it, and wraps the answer, against the very same stream the App signals against.
/// The Opus audio track Google's player waits on is a source on that stream rather than a stream
/// of its own (<c>Go2RtcSyncWorker.SourcesFor</c>), so there is no Google-shaped stream to resolve
/// and nothing in <c>Ingest/</c> that knows this integration exists.</para>
///
/// <para><b>Media does not pass through here.</b> Only the offer and the answer do. Once they have
/// been exchanged the Nest Hub and go2rtc carry SRTP directly over the LAN — the same split
/// <c>WebRtcEndpoints</c> describes for the browser, and the reason WebRTC was chosen over HLS,
/// where the display would instead fetch every segment through the operator's public origin.</para>
///
/// <para><b>Which is also the constraint that breaks it.</b> go2rtc advertises the addresses in
/// <c>SERVAL_WEBRTC_CANDIDATES</c> and no TURN server is configured, so the display has to be able
/// to reach the Docker host directly. A Hub on a guest VLAN, or one at somebody else's house, gets
/// a successful voice command and then a spinner — there is no error to report, because as far as
/// this endpoint is concerned the exchange succeeded.</para>
/// </summary>
public static class CameraStreamSignaling
{
    public static void MapCameraStreamSignaling(this RouteGroupBuilder group)
    {
        group.MapPost("/camerastream/signal", async (
            HttpContext http,
            SignalingRequest request,
            GoogleHomeGate gate,
            CameraStreamTicketService tickets,
            IGo2RtcClient go2rtc,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (!gate.IsEffective)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            ILogger logger = loggers.CreateLogger("Serval.Server.GoogleHome.Signaling");
            string? ticket = ReadTicket(http);

            // "end" closes the session. There is nothing to tell go2rtc — its signaling is one
            // request and one response, with nowhere to renegotiate to — so the only real work is
            // dropping the ticket so the credential does not outlive the session it was for.
            if (string.Equals(request.Action, "end", StringComparison.Ordinal))
            {
                // Logged because an "end" arriving seconds after an "offer" is a display giving up,
                // which is the difference between a stream that never started and one that did and
                // then dropped.
                logger.LogInformation(
                    "Google Home signaling ended for device {DeviceId}.", request.DeviceId);
                tickets.Revoke(ticket);
                return Results.Ok(new SignalingResponse("answer", string.Empty));
            }

            if (!string.Equals(request.Action, "offer", StringComparison.Ordinal))
            {
                // "answer" is a direction this server never speaks in: go2rtc answers offers, it
                // does not make them, which is why cameraStreamOffer is left out of EXECUTE.
                return Results.BadRequest(new { error = "unsupported_action" });
            }

            string? cameraId = tickets.TrySpend(ticket);
            if (cameraId is null)
            {
                logger.LogWarning(
                    "Google Home signaling refused: the ticket is unknown, expired, or spent.");
                return Results.Unauthorized();
            }

            // The ticket names the camera; the body also claims one. They must agree.
            //
            // The ticket alone would be enough — the URL carries no camera id, so there is nothing
            // for a caller to tamper with. This is here for the other direction: a bug on *our*
            // side that minted a ticket for the wrong camera would otherwise stream a neighbouring
            // room to a display, silently and plausibly.
            if (!string.IsNullOrEmpty(request.DeviceId)
                && !string.Equals(request.DeviceId, cameraId, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Google Home signaling refused: ticket is for {Ticketed} but the request names "
                    + "{Requested}.", cameraId, request.DeviceId);
                return Results.BadRequest(new { error = "device_mismatch" });
            }

            if (string.IsNullOrWhiteSpace(request.Sdp))
            {
                return Results.BadRequest(new { error = "missing_sdp" });
            }

            // The offer, before anything is done with it. The summary is what answers "will media
            // actually flow" — see SdpSummary — and the full text is at Debug for when it does not.
            logger.LogInformation(
                "Google Home signaling offer for camera {CameraId}: {Summary}",
                cameraId, SdpSummary.Describe(request.Sdp));
            logger.LogDebug("Google Home offer SDP for {CameraId}:\n{Sdp}", cameraId, request.Sdp);

            try
            {
                // The camera id is the go2rtc stream name — the same one the App signals against.
                // Google needs an Opus audio track the camera itself cannot supply, but that is a
                // source on the camera's stream rather than a stream of its own, so there is
                // nothing here to resolve. See Go2RtcSyncWorker.SourcesFor.
                string answer = await go2rtc.ExchangeSdpAsync(cameraId, request.Sdp, ct);

                // Both halves are logged because a mismatch between them is the failure that is
                // otherwise invisible: go2rtc answers 200 whether or not the negotiation produced
                // anything usable, so "the exchange succeeded" says nothing about whether a codec
                // survived it or the two ends have a route to each other.
                logger.LogInformation(
                    "Google Home signaling answer for camera {CameraId}: {Summary}",
                    cameraId, SdpSummary.Describe(answer));
                logger.LogDebug("Google Home answer SDP for {CameraId}:\n{Sdp}", cameraId, answer);

                return Results.Ok(new SignalingResponse("answer", answer));
            }
            catch (HttpRequestException ex)
            {
                // go2rtc unreachable, or the camera failed to start. 502 rather than 500: the
                // failure is upstream of us, and saying so is what makes it diagnosable from the
                // Google console's logs rather than only from this server's.
                logger.LogWarning(
                    ex, "Google Home signaling failed for camera {CameraId}.", cameraId);
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
        })
            .WithSummary("WebRTC signaling for one camera, called by Google's cloud.")
            .WithDescription(
                "Takes {action, deviceId, sdp} and answers {action:\"answer\", sdp}. The offer is "
                + "forwarded to the go2rtc sidecar and its answer returned unchanged.\n\n"
                + "Authenticated by the single-camera ticket minted in the EXECUTE response, "
                + "accepted either as a bearer token or as ?t= on the URL — Google's trait and "
                + "signaling references disagree about which reaches every receiver. Media never "
                + "passes through this server: after the exchange the display and go2rtc talk "
                + "directly over the LAN.")
            .Produces<SignalingResponse>()
            // Named explicitly rather than inheriting the app policy: the caller is a page served
            // by Google, not the Serval App, and a credentialed fetch will not accept the wildcard
            // origin the app policy emits. See Program.cs for the whole of why.
            .RequireCors("google-signaling")
            .AllowAnonymous();
    }

    /// <summary>
    /// The ticket, from the <c>Authorization: Bearer</c> header Google's signaling reference
    /// specifies or from the <c>?t=</c> the URL carries.
    ///
    /// <para>Both are accepted because the two Google documents disagree: the CameraStream trait
    /// reference notes that auth tokens are not supported by every receiver, while the signaling
    /// protocol reference shows a bearer header. Accepting either costs one method and survives
    /// whichever turns out to be true on the operator's actual hardware.</para>
    /// </summary>
    internal static string? ReadTicket(HttpContext http)
    {
        if (http.Request.Headers.TryGetValue("Authorization", out StringValues authorization)
            && authorization.Count == 1
            && authorization[0] is { } header
            && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            string bearer = header["Bearer ".Length..].Trim();
            if (bearer.Length > 0)
            {
                return bearer;
            }
        }

        string query = http.Request.Query["t"].ToString();
        return string.IsNullOrEmpty(query) ? null : query;
    }
}
