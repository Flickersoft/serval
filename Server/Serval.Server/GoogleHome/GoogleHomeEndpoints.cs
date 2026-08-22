namespace Serval.Server.GoogleHome;

/// <summary>
/// The Google Home cloud-to-cloud integration's route surface.
///
/// <para><b>Most of this group is anonymous, and every anonymous route authenticates itself.</b>
/// That is the same arrangement telemetry ingest uses — <c>.AllowAnonymous()</c> to the
/// authorization pipeline plus a constant-time check in the handler — and it is necessary here
/// because the callers are Google's servers, which have no Serval session and cannot get one. The
/// application-wide fallback policy still requires a login for anything added without an explicit
/// answer, so each exception below is deliberate rather than inherited.</para>
///
/// <para><b><c>/status</c> is not behind the gate.</b> Every other route 503s when the integration
/// is not effective; this one exists precisely to say why, so gating it would hide the answer at
/// the moment it is wanted.</para>
/// </summary>
public static class GoogleHomeEndpoints
{
    public static void MapGoogleHomeEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/google")
            .WithTags("Google Home");

        group.MapGet("/status", (GoogleHomeGate gate) => Results.Ok(gate.Status))
            .WithSummary("Whether the Google Home integration is live, and what is stopping it.")
            .WithDescription(
                "Admin-only, and readable whether or not the integration is switched on — the "
                + "point of it is to name the one unmet condition, which is the thing an operator "
                + "cannot get from a 503.\n\n"
                + "Configuration is not writable here or anywhere else in the App: every "
                + "Serval:GoogleHome:* key is environment-only. Two are secrets, and two more "
                + "decide where an anonymous endpoint sends credentials. See Docs/google-home.md.")
            .Produces<GoogleHomeStatus>()
            .RequireAuthorization("Admin");

        group.MapGet("/links", async (GoogleOAuthStore store, CancellationToken ct) =>
            Results.Ok(await store.ListLinksAsync(ct)))
            .WithSummary("The Google account linked to this deployment, if any.")
            .WithDescription(
                "At most one. LastFulfillmentAt is the useful field: it is the last time Google "
                + "actually called, which distinguishes a link that works from one that was made "
                + "and then quietly stopped being used.")
            .Produces<IReadOnlyList<GoogleLink>>()
            .RequireAuthorization("Admin");

        group.MapDelete("/links/{agentUserId}", async (
            string agentUserId,
            GoogleOAuthStore store,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            GoogleLink? link = await store.GetLinkAsync(ct);
            if (link is null || link.AgentUserId != agentUserId)
            {
                return Results.NotFound();
            }

            await store.UnlinkAsync(agentUserId, ct);

            // Logged at Information for the same reason the config backup export is: this is a
            // privileged act that leaves no other trace, and "the cameras vanished from Google"
            // should be answerable from the log.
            loggers.CreateLogger("Serval.Server.GoogleHome")
                .LogInformation("Google Home link {AgentUserId} removed.", agentUserId);

            return Results.NoContent();
        })
            .WithSummary("Unlink the Google account, revoking every credential issued to it.")
            .WithDescription(
                "Deletes the link and every token and code under it. Google keeps showing the "
                + "cameras until it next calls and is refused, then drops them. The same thing "
                + "happens from Google's side when the integration is unlinked in the Home app, "
                + "which arrives here as a DISCONNECT intent.")
            .RequireAuthorization("Admin");

        group.MapGoogleOAuthEndpoints();
        group.MapCameraStreamSignaling();
        group.MapCameraStreamPlayback();
        group.MapCameraStreamReceiver();

        group.MapPost("/fulfillment", async (
            SmartHomeRequest request,
            HttpContext http,
            GoogleHomeGate gate,
            GoogleOAuthStore store,
            SmartHomeFulfillment fulfillment,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (!gate.IsEffective)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            ILogger logger = loggers.CreateLogger("Serval.Server.GoogleHome.Fulfillment");

            // The access token this server issued at link time, presented as an ordinary bearer.
            // It is looked up by hash, so a stolen database yields nothing usable.
            string? bearer = CameraStreamSignaling.ReadTicket(http);
            GoogleToken? token = bearer is null
                ? null
                : await store.FindTokenAsync(bearer, GoogleTokenKind.Access, ct);

            if (token is null)
            {
                // Distinguishing these two matters more than it looks: "Google never called" and
                // "Google called and was refused" are the same silence from the outside, and
                // telling them apart otherwise means reading the token collection by hand.
                logger.LogWarning(
                    "Google Home fulfillment refused: {Reason}.",
                    bearer is null
                        ? "no bearer token was presented"
                        : "the access token is unknown, expired, or was revoked");
                return Results.Unauthorized();
            }

            SmartHomeResponse response = await fulfillment.HandleAsync(request, token.AgentUserId, ct);

            // Stamped after the work, so the admin card's "last heard from Google" reflects calls
            // that were actually served. Not awaited for correctness — a failed stamp must not fail
            // a voice command — but awaited for simplicity, since it is one indexed update.
            await store.TouchFulfillmentAsync(token.AgentUserId, ct);

            return Results.Ok(response);
        })
            .WithSummary("Google's fulfillment endpoint: SYNC, QUERY, EXECUTE and DISCONNECT.")
            .WithDescription(
                "Called server-to-server by Google with the access token issued during account "
                + "linking. SYNC lists the cameras go2rtc can actually serve, QUERY reports each "
                + "one's online state from snapshot staleness, EXECUTE hands back a signaling URL "
                + "and a single-camera ticket, and DISCONNECT revokes everything.\n\n"
                + "Anonymous to the authorization pipeline because Google holds no Serval session; "
                + "the bearer token is checked here instead.")
            .Produces<SmartHomeResponse>()
            .AllowAnonymous();
    }
}
