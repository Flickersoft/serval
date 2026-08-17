using Serval.Server.Auth;

namespace Serval.Server.Preferences;

/// <summary>
/// One account's own preferences — read and written by whoever is signed in, for themselves only.
///
/// <para><b>There is no id in the route.</b> The account is taken from the bearer token, so there
/// is no shape of request that reaches somebody else's wall. That is deliberate and is the whole
/// access-control story here: not a check inside the handler that a future route could forget, but
/// an address that cannot express the question.</para>
///
/// <para><b>The write merges; it does not replace.</b> A property the request leaves out is left
/// as it was. This is the opposite of the camera registry's PUT, and for a reason that only
/// applies here: preferences are meant to grow, and the App is served by the Server but can be a
/// stale cached build. A client that predates a preference would, under replace semantics, silently
/// erase it every time somebody dragged a tile. Sending an <em>empty</em> layout still clears it —
/// absent and empty are different, which is what makes "reset my wall" expressible.</para>
/// </summary>
public static class PreferencesEndpoints
{
    public static void MapPreferencesEndpoints(this IEndpointRouteBuilder app)
    {
        // Any signed-in account, not Admin: arranging your own wall is not an administrative act,
        // and a Viewer who cannot rearrange the screen they spend all day looking at is the case
        // this feature exists for.
        RouteGroupBuilder group = app.MapGroup("/api/preferences")
            .WithTags("Preferences")
            .RequireAuthorization();

        group.MapGet("", async (
            HttpContext http,
            UserPreferencesRepository preferences,
            CancellationToken ct) =>
        {
            return Results.Ok(ToResponse(
                await preferences.GetAsync(TokenService.RequireUserId(http.User), ct)));
        })
            .WithSummary("This account's own preferences.")
            .WithDescription(
                "Always 200 for a signed-in caller: an account that has never saved anything reads "
                + "as empty rather than 404, because 'I have not arranged my wall' and 'my wall is "
                + "empty' mean the same thing to the client. An empty wallLayout means the App "
                + "packs its default arrangement.")
            .Produces<PreferencesResponse>();

        group.MapPut("", async (
            PreferencesRequest body,
            HttpContext http,
            UserPreferencesRepository preferences,
            CancellationToken ct) =>
        {
            string userId = TokenService.RequireUserId(http.User);

            // Nothing to change is not an error — it is what an older client sends, and the point
            // of merge semantics is that it costs that client nothing.
            if (body.WallLayout is null
                && body.NotificationsEnabled is null
                && body.Notifications is null)
            {
                return Results.Ok(ToResponse(await preferences.GetAsync(userId, ct)));
            }

            // Each present property is saved by its own call, so an omitted one is never
            // written — not even back as the value it already had. Reading the document and
            // writing it whole would reintroduce exactly the lost update the merge exists to
            // prevent, between two tabs changing different preferences.
            UserPreferences saved = await preferences.GetAsync(userId, ct);

            if (body.WallLayout is { } layout)
            {
                saved = await preferences.SaveWallLayoutAsync(
                    userId,
                    [.. layout.Select(t => new WallTile
                    {
                        CameraId = t.CameraId,
                        Column = t.Column,
                        Row = t.Row,
                        ColumnSpan = t.ColumnSpan,
                        RowSpan = t.RowSpan,
                    })],
                    ct);
            }

            if (body.NotificationsEnabled is { } notificationsEnabled)
            {
                saved = await preferences.SaveNotificationsEnabledAsync(
                    userId, notificationsEnabled, ct);
            }

            if (body.Notifications is { } rules)
            {
                saved = await preferences.SaveNotificationRulesAsync(
                    userId,
                    [.. rules.Select(r => new CameraNotificationRule
                    {
                        CameraId = r.CameraId,
                        Enabled = r.Enabled,
                        ObjectClasses = r.ObjectClasses,
                        SoundLabels = r.SoundLabels,
                        CooldownSeconds = r.CooldownSeconds,
                    })],
                    ct);
            }

            return Results.Ok(ToResponse(saved));
        })
            .WithSummary("Change this account's preferences, leaving what you omit alone.")
            .WithDescription(
                "Send only what you are changing. A property left out — or sent as null — is kept "
                + "as it was, so a client that predates a preference cannot erase it.\n\n"
                + "  { \"wallLayout\": [ { \"cameraId\": \"front-door\", \"column\": 0, \"row\": 0, "
                + "\"columnSpan\": 12, \"rowSpan\": 4 } ] }\n\n"
                + "An empty wallLayout array is not the same as omitting it: the array clears the "
                + "saved arrangement, so the wall falls back to the default packing. Tiles are "
                + "checked for being on the 24-column grid, spanning at least one cell, and naming "
                + "each camera once; overlap and packing are the client's rules and are not "
                + "re-checked here.\n\n"
                + "notifications replaces the whole set of per-camera rules. A camera with no rule "
                + "notifies about everything it raises an alert for, and within a rule a null "
                + "objectClasses or soundLabels means the same — null inherits, an empty array "
                + "means nothing. A rule can only narrow what a camera already alerts on: naming a "
                + "class the camera does not alert on is stored and simply matches nothing until "
                + "an admin adds it in the camera's settings.\n\n"
                + "cooldownSeconds is how long this camera is left alone after interrupting you "
                + "about one thing, before it may interrupt you about that same thing again. Null "
                + "inherits Serval:Push:CooldownSeconds and zero sends every alert — the two are "
                + "different answers, and only a stored zero survives an admin changing the "
                + "deployment's default. It holds back the notification and nothing else: the "
                + "alert is still raised, still gets its clip, and still appears on /api/alerts.\n\n"
                + "The response is the same shape GET returns.")
            .Produces<PreferencesResponse>()
            .Produces(StatusCodes.Status400BadRequest);
    }

    private static PreferencesResponse ToResponse(UserPreferences preferences) =>
        new(
            [.. preferences.WallLayout.Select(t =>
                new WallTilePayload(t.CameraId, t.Column, t.Row, t.ColumnSpan, t.RowSpan))],
            preferences.NotificationsEnabled,
            [.. preferences.Notifications.Select(r =>
                new CameraNotificationRulePayload(
                    r.CameraId, r.Enabled, r.ObjectClasses, r.SoundLabels, r.CooldownSeconds))],
            preferences.UpdatedAt);
}

/// <summary>
/// A preferences change. Every property is nullable and null means "leave this alone" — see the
/// merge note on <see cref="PreferencesEndpoints"/>. New preferences are added here as further
/// nullable properties, which is what keeps an older client harmless.
/// </summary>
public sealed record PreferencesRequest(
    IReadOnlyList<WallTilePayload>? WallLayout,
    bool? NotificationsEnabled,
    IReadOnlyList<CameraNotificationRulePayload>? Notifications);

/// <summary>This account's preferences as the App reads them.</summary>
public sealed record PreferencesResponse(
    IReadOnlyList<WallTilePayload> WallLayout,
    bool NotificationsEnabled,
    IReadOnlyList<CameraNotificationRulePayload> Notifications,
    DateTimeOffset UpdatedAt);

/// <summary>
/// One camera's notification rule on the wire. <c>ObjectClasses</c>, <c>SoundLabels</c> and
/// <c>CooldownSeconds</c> all keep their null-means-inherit meaning across the API — see
/// <see cref="CameraNotificationRule"/>.
///
/// <para>Every parameter is required rather than defaulted, deliberately. The backup writer and the
/// restore reader both build this positionally, and a defaulted parameter would let a new field
/// compile at both of them while quietly falling out of every backup ever taken.</para>
/// </summary>
public sealed record CameraNotificationRulePayload(
    string CameraId,
    bool Enabled,
    string[]? ObjectClasses,
    string[]? SoundLabels,
    int? CooldownSeconds);

/// <summary>One tile's place, in the App's own 24-column grid terms.</summary>
public sealed record WallTilePayload(
    string CameraId,
    int Column,
    int Row,
    int ColumnSpan,
    int RowSpan);
