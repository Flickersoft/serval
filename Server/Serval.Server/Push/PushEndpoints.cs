using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Serval.Server.Auth;
using Serval.Server.Configuration;

namespace Serval.Server.Push;

/// <summary>
/// Where a browser registers itself for notifications, and where a person manages the devices they
/// have registered.
///
/// <para><b>No account in any route.</b> It comes from the bearer token and is part of every
/// filter, exactly as <c>PreferencesEndpoints</c> does it and for the same reason. A subscription
/// may be named by id, but naming one is never enough on its own: an id is handed out with every
/// device list, so a delete matching on it alone would let any signed-in account retire somebody
/// else's device by replaying one it had seen. There is no request shape here that reaches another
/// account's devices.</para>
///
/// <para><b>Which cameras notify is not here.</b> That is per-account rather than per-device and
/// lives on <c>/api/preferences</c>; this route is delivery only. Somebody with a phone and a
/// laptop has two subscriptions and one set of rules, which is the behaviour anyone would expect.</para>
/// </summary>
public static class PushEndpoints
{
    public static void MapPushEndpoints(this IEndpointRouteBuilder app)
    {
        // Any signed-in account. Subscribing to alerts about your own house is not an
        // administrative act, and a Viewer who cannot be notified is a Viewer who has to keep the
        // page open to be told anything.
        RouteGroupBuilder group = app.MapGroup("/api/push")
            .WithTags("Notifications")
            .RequireAuthorization();

        group.MapGet("/config", async (
            VapidKeyStore keys,
            IOptionsMonitor<ServerOptions> options,
            CancellationToken ct) =>
        {
            VapidKeyPair pair = await keys.GetAsync(ct);
            return Results.Ok(new PushConfigResponse(pair.PublicKey, options.CurrentValue.Push.Enabled));
        })
            .WithSummary("What the browser needs before it can subscribe.")
            .WithDescription(
                "The deployment's VAPID public key, which the browser passes as "
                + "applicationServerKey when it subscribes, and whether notifications are switched "
                + "on at all. The key is generated on first use and then never changes: it is the "
                + "identity every existing subscription is bound to, so a new one would silently "
                + "unsubscribe every device in the house.\n\n"
                + "Public by design — it is the half of the key pair that is meant to be handed "
                + "out. The private half never leaves the server.")
            .Produces<PushConfigResponse>();

        group.MapGet("/subscriptions", async (
            HttpContext http,
            PushSubscriptionRepository subscriptions,
            CancellationToken ct) =>
        {
            string userId = TokenService.RequireUserId(http.User);

            List<PushSubscription> devices = await subscriptions.ListForUserAsync(userId, ct);
            return Results.Ok(devices.Select(ToResponse).ToList());
        })
            .WithSummary("The devices this account has registered.")
            .WithDescription(
                "One row per browser, not per session. The endpoint itself is never returned — it "
                + "is a credential of sorts, and nothing on the screen needs it.")
            .Produces<List<PushDeviceResponse>>();

        group.MapPost("/subscriptions", async (
            PushSubscriptionRequest body,
            HttpContext http,
            PushSubscriptionRepository subscriptions,
            CancellationToken ct) =>
        {
            string userId = TokenService.RequireUserId(http.User);

            if (Validate(body) is { } problem)
            {
                return Results.BadRequest(new { error = problem });
            }

            var saved = await subscriptions.SaveAsync(
                new PushSubscription
                {
                    Id = PushSubscription.IdFor(body.Endpoint),
                    UserId = userId,
                    Endpoint = body.Endpoint,
                    P256dh = body.Keys.P256dh,
                    Auth = body.Keys.Auth,
                    Label = Describe(body.Label ?? http.Request.Headers.UserAgent.ToString()),
                },
                ct);

            return Results.Ok(ToResponse(saved));
        })
            .WithSummary("Register this browser for notifications.")
            .WithDescription(
                "Send what the browser's PushSubscription.toJSON() produced.\n\n"
                + "  { \"endpoint\": \"https://fcm.googleapis.com/fcm/send/...\", "
                + "\"keys\": { \"p256dh\": \"...\", \"auth\": \"...\" } }\n\n"
                + "Safe to call on every launch, and the App does: the row is keyed by a hash of "
                + "the endpoint, so re-registering the same browser overwrites its own row rather "
                + "than accumulating one per visit. That is also how a browser quietly reissuing "
                + "its endpoint gets noticed, since the old one simply stops being written to and "
                + "is dropped the first time it is rejected.")
            .Produces<PushDeviceResponse>()
            .Produces(StatusCodes.Status400BadRequest);

        // The id is in the route rather than in a body, and that is not a style choice: minimal
        // APIs refuse to *infer* a body parameter on DELETE, and the refusal is thrown when the
        // route table is first materialised — which is at startup, after every health check has
        // passed and every test has gone green. `[FromBody]` would silence it, and would then
        // depend on every proxy between the App and here forwarding a DELETE body, which not all
        // of them do. A path segment has neither problem.
        group.MapDelete("/subscriptions/{id}", async (
            string id,
            HttpContext http,
            PushSubscriptionRepository subscriptions,
            CancellationToken ct) =>
        {
            // Scoped to the caller: an id is not a secret — it is handed out with every device
            // list — and this is what stops one account retiring another's device by quoting one
            // back.
            await subscriptions.DeleteAsync(id, TokenService.RequireUserId(http.User), ct);
            return Results.NoContent();
        })
            .WithSummary("Stop notifying one browser.")
            .WithDescription(
                "The id comes from the device list, or from the response to registering — which is "
                + "how a browser learns its own. It is a hash of the push endpoint, so it names a "
                + "device without the endpoint itself ever being given out.\n\n"
                + "Only ever within the calling account: an id belonging to somebody else matches "
                + "nothing.\n\n"
                + "Removing one that is not there is not an error — turning notifications off "
                + "should succeed whether or not the server still had a record of them being on.")
            .Produces(StatusCodes.Status204NoContent);

        group.MapPost("/test", async (
            HttpContext http,
            PushSubscriptionRepository subscriptions,
            WebPushClient client,
            CancellationToken ct) =>
        {
            string userId = TokenService.RequireUserId(http.User);

            List<PushSubscription> devices = await subscriptions.ListForUserAsync(userId, ct);
            if (devices.Count == 0)
            {
                return Results.BadRequest(new { error = "This account has no devices registered." });
            }

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new NotificationPayload(
                Id: "test",
                CameraId: "",
                Title: "Serval notifications are working",
                Body: "This is a test. A real alert names the camera and what it saw.",
                Image: "",
                Url: "/alerts",
                At: DateTimeOffset.UtcNow));

            var delivered = 0;
            foreach (PushSubscription device in devices)
            {
                PushOutcome outcome = await client.SendAsync(device, payload, topic: null, ct);

                if (outcome == PushOutcome.Delivered)
                {
                    delivered++;
                }
                else if (outcome == PushOutcome.Expired)
                {
                    await subscriptions.DeleteAsync(device.Id, userId, ct);
                }
            }

            return Results.Ok(new PushTestResponse(devices.Count, delivered));
        })
            .WithSummary("Send a test notification to this account's devices.")
            .WithDescription(
                "The way to tell a working setup from a silent one, which is otherwise very hard: "
                + "every failure mode of push — no TLS, a stale subscription, a permission the "
                + "browser revoked, a key that does not match — looks exactly like nothing "
                + "happening.\n\n"
                + "The count returned is how many push services accepted the message, which is not "
                + "quite the same as how many devices showed it: a phone that is switched off "
                + "counts as accepted, and the message waits for it.")
            .Produces<PushTestResponse>()
            .Produces(StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// What must be true of a subscription for it to be usable later. Checked here rather than at
    /// send time so a browser sending nonsense is told immediately, instead of it becoming a row
    /// that fails silently every alert forever.
    /// </summary>
    private static string? Validate(PushSubscriptionRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Endpoint)
            || !Uri.TryCreate(body.Endpoint, UriKind.Absolute, out Uri? endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            return "A subscription endpoint must be an absolute https URL.";
        }

        if (string.IsNullOrWhiteSpace(body.Keys.P256dh) || string.IsNullOrWhiteSpace(body.Keys.Auth))
        {
            return "A subscription must carry both keys.";
        }

        try
        {
            if (System.Buffers.Text.Base64Url.DecodeFromChars(body.Keys.P256dh) is not { Length: 65 })
            {
                return "The p256dh key must be a 65-byte uncompressed P-256 point.";
            }

            if (System.Buffers.Text.Base64Url.DecodeFromChars(body.Keys.Auth) is not { Length: 16 })
            {
                return "The auth secret must be 16 bytes.";
            }
        }
        catch (FormatException)
        {
            return "Both keys must be base64url.";
        }

        return null;
    }

    /// <summary>
    /// Something a person can pick their own phone out of a list by. A user-agent string in full is
    /// unreadable and this is only ever a label, so it keeps the browser and the platform and
    /// discards the version soup.
    /// </summary>
    private static string? Describe(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }

        string browser = userAgent switch
        {
            var a when a.Contains("Firefox/", StringComparison.Ordinal) => "Firefox",
            var a when a.Contains("Edg/", StringComparison.Ordinal) => "Edge",
            var a when a.Contains("OPR/", StringComparison.Ordinal) => "Opera",
            var a when a.Contains("Chrome/", StringComparison.Ordinal) => "Chrome",
            var a when a.Contains("Safari/", StringComparison.Ordinal) => "Safari",
            _ => "Browser",
        };

        string platform = userAgent switch
        {
            var a when a.Contains("Android", StringComparison.Ordinal) => "Android",
            var a when a.Contains("iPhone", StringComparison.Ordinal) => "iPhone",
            var a when a.Contains("iPad", StringComparison.Ordinal) => "iPad",
            var a when a.Contains("Mac OS X", StringComparison.Ordinal) => "Mac",
            var a when a.Contains("Windows", StringComparison.Ordinal) => "Windows",
            var a when a.Contains("Linux", StringComparison.Ordinal) => "Linux",
            _ => null!,
        };

        return platform is null ? browser : $"{browser} on {platform}";
    }

    private static PushDeviceResponse ToResponse(PushSubscription subscription) =>
        new(
            subscription.Id,
            subscription.Label,
            subscription.CreatedAt,
            subscription.LastSuccessAt);
}

/// <summary>What a browser needs before it can call <c>PushManager.subscribe</c>.</summary>
public sealed record PushConfigResponse(
    [property: JsonPropertyName("vapid_public_key")] string VapidPublicKey,
    [property: JsonPropertyName("enabled")] bool Enabled);

/// <summary>A browser's subscription, in the shape <c>PushSubscription.toJSON()</c> produces.</summary>
public sealed record PushSubscriptionRequest(
    string Endpoint,
    PushSubscriptionKeys Keys,
    string? Label);

public sealed record PushSubscriptionKeys(string P256dh, string Auth);

/// <summary>
/// One registered device as the App lists it. The endpoint is deliberately absent — the id is
/// derived from it and is enough to talk about a row, and nothing on screen needs the URL itself.
/// </summary>
public sealed record PushDeviceResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("last_success_at")] DateTimeOffset? LastSuccessAt);

/// <summary>How a test send went.</summary>
public sealed record PushTestResponse(
    [property: JsonPropertyName("devices")] int Devices,
    [property: JsonPropertyName("accepted")] int Accepted);
