using Microsoft.Extensions.Options;
using Serval.Server.Cameras;
using Serval.Server.Configuration;
using Serval.Server.Ingest;

namespace Serval.Server.WebRtc;

/// <summary>
/// The one endpoint the App uses to open the low-latency WebRTC live view: a signaling proxy.
/// The browser sends its SDP offer here, the server forwards it to the go2rtc sidecar for that
/// camera's stream, and hands back go2rtc's SDP answer. That's the whole of the server's role —
/// the actual media (SRTP) flows directly browser ↔ go2rtc and never transits the server.
///
/// Signaling is proxied (rather than pointing the App straight at go2rtc) so there's a single
/// origin and a single place to add auth later; go2rtc itself is never exposed to clients.
///
/// This same endpoint carries <b>two-way audio (talk-back)</b>: for a camera with
/// <see cref="Camera.TwoWayAudio"/> enabled, the browser simply adds a microphone track to its SDP
/// offer, and go2rtc routes that audio to the camera's backchannel over the very same WebRTC
/// session. No extra endpoint is needed — the offer we forward is already bidirectional. (Browsers
/// only grant microphone access over HTTPS.)
/// </summary>
public static class WebRtcEndpoints
{
    private const string SdpContentType = "application/sdp";

    public static void MapWebRtcEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/cameras/{id}/webrtc", async (
            string id,
            HttpRequest request,
            CameraRepository cameras,
            IGo2RtcClient go2rtc,
            IOptionsMonitor<ServerOptions> options,
            CancellationToken ct) =>
        {
            if (!options.CurrentValue.WebRtc.Enabled)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (!CameraRepository.IsSafeId(id))
            {
                return Results.NotFound();
            }

            // WebRTC live is only offered for cameras with a real network source — the same set
            // the sync worker registers with go2rtc. A file (test) camera has no stream to answer.
            Camera? camera = await cameras.GetAsync(id, ct);
            if (camera is null
                || !camera.Enabled
                || camera.LiveStream is not { Url: var liveUrl }
                || string.IsNullOrWhiteSpace(liveUrl)
                || Ingest.SourceArguments.IsFile(liveUrl))
            {
                return Results.NotFound();
            }

            using var reader = new StreamReader(request.Body);
            string offer = await reader.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(offer))
            {
                return Results.BadRequest(new { error = "An SDP offer body is required." });
            }

            try
            {
                string answer = await go2rtc.ExchangeSdpAsync(id, offer, ct);
                return Results.Text(answer, SdpContentType);
            }
            catch (HttpRequestException)
            {
                // go2rtc unreachable or the stream failed to start (e.g. camera offline).
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }
        })
            .WithTags("WebRTC")
            .WithSummary("Exchange an SDP offer for an answer, proxied to go2rtc.")
            .Accepts<string>(SdpContentType)
            .Produces<string>(StatusCodes.Status200OK, SdpContentType)
            .RequireAuthorization();
    }
}
