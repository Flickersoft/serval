namespace Serval.Server.GoogleHome;

/// <summary>
/// Serves Serval's own Cast Web Receiver — the page a Cast device runs when
/// <c>Serval:GoogleHome:CastReceiverAppId</c> names it.
///
/// <para><b>Why a receiver of our own is the only way WebRTC reaches most televisions.</b> Google
/// plays WebRTC itself on a Nest display and a Chromecast with Google TV, and nowhere else. Every
/// other Cast device — an NVIDIA Shield, an Android TV box, a Chromecast-enabled television — is
/// offered <c>hls</c> and, in Google's words, "a Cast Web Receiver is launched" to play it. That
/// receiver is Google's by default and does nothing but play the URL. Named as the application to
/// launch, it is this page instead, which opens a peer connection first and plays the URL only if
/// that fails.</para>
///
/// <para><b>Launched by the App, not by the Assistant.</b> Google refuses to send a camera to a
/// television by voice at all — for Serval and for other vendors' certified integrations alike, and
/// before this server is ever called. The EXECUTE response still names this receiver, because
/// Google ignores the field where it does not apply and one source of truth is worth more than a
/// branch nobody exercises; but what actually launches it is <see cref="Cast.CastEndpoints"/> and
/// the Cast button on the camera screen.</para>
///
/// <para><b>Serving it here rather than hosting it is what makes it simple.</b> The receiver is
/// then same-origin with the API it signals against: no CORS policy to get right, no mixed-content
/// rule to fall foul of, and the ticket already in the access URL is a credential the page may
/// reuse as-is. It also means no receiver is hosted by the project and no application id is shipped
/// — an operator registers their own Cast application against their own address, so the page and
/// the server it talks to are always the same deployment.</para>
///
/// <para><b>Anonymous, and it has to be.</b> A Cast device fetches this before any load request
/// exists, so there is no ticket to present yet. What it gets is a static page naming no camera and
/// carrying no credential; everything specific arrives afterwards, in the Cast load request, and is
/// checked by the signaling and playback routes that already exist.</para>
///
/// <para><b>Served whether or not a receiver app id is configured.</b> Registering a Cast
/// application means giving the console a URL that already answers, so gating this route on the
/// setting would make the setting impossible to obtain. It is gated on
/// <see cref="GoogleHomeGate"/> like the rest of the group and nothing more.</para>
/// </summary>
public static class CameraStreamReceiver
{
    /// <summary>
    /// The page, read once from disk. It is a compile-time asset copied beside the binary rather
    /// than an embedded resource, so an operator debugging a receiver on a television can read what
    /// their server is actually serving.
    /// </summary>
    private static readonly Lazy<string> Template = new(() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "GoogleHome", "Receiver", "player.html")));

    public static void MapCameraStreamReceiver(this RouteGroupBuilder group)
    {
        group.MapGet("/camerastream/receiver", (GoogleHomeGate gate) =>
        {
            if (!gate.IsEffective)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Text(Render(gate.IceServersJson), "text/html");
        })
            .WithSummary("Serval's Cast Web Receiver, run on the Cast device itself.")
            .WithDescription(
                "The page a Cast device runs when Serval:GoogleHome:CastReceiverAppId names it as "
                + "the application to launch — in practice from the App's Cast button, since "
                + "Google will not send a camera to a television by voice. Handed a live camera it "
                + "negotiates WebRTC against /camerastream/signal with the ticket in the access "
                + "URL, and plays that URL as HLS if the peer connection produces no picture; "
                + "handed a recording it plays it as media.\n\n"
                + "Anonymous because a Cast device loads it before any ticket exists. It names no "
                + "camera and carries no credential.")
            .AllowAnonymous();
    }

    /// <summary>
    /// Substitutes the one per-deployment value the page needs: the ICE configuration, exactly as
    /// <see cref="GoogleHomeGate.IceServersJson"/> already renders it for Google.
    ///
    /// <para>It is inlined here rather than sent in the stream response because
    /// <c>cameraStreamIceServers</c> is read by Google's player, which is not the one running when
    /// this page is. Null — no TURN, the normal case for a Cast device and go2rtc on one LAN —
    /// becomes <c>[]</c>, because this lands in a JavaScript expression where nothing at all is a
    /// syntax error rather than an empty list.</para>
    /// </summary>
    internal static string Render(string? iceServersJson) =>
        Template.Value.Replace("$ICE_SERVERS$", iceServersJson ?? "[]", StringComparison.Ordinal);
}
