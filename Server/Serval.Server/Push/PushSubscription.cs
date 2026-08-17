using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson.Serialization.Attributes;

namespace Serval.Server.Push;

/// <summary>
/// One browser's standing permission to be told about alerts, and the keys needed to encrypt to it.
///
/// <para><b>A subscription is a device, not a person.</b> Somebody signed in on a phone and a
/// desktop has two of these, and both are notified — which is the behaviour anyone would expect and
/// the reason the per-camera rules live on <see cref="Preferences.UserPreferences"/> instead of
/// here. Filtering belongs to the account; delivery belongs to the browser.</para>
///
/// <para><b>These expire on their own and without warning.</b> A browser reissues an endpoint when
/// it feels like it, and a stale one answers 404 or 410 forever. That is an ordinary outcome rather
/// than an error: <see cref="PushSubscriptionRepository.DeleteAsync"/> is called on those two status
/// codes, and the App re-registers whatever the browser currently holds every time it starts. The
/// pairing of those two behaviours is what keeps the collection honest without any expiry sweep.</para>
/// </summary>
[BsonIgnoreExtraElements]
public sealed class PushSubscription
{
    /// <summary>
    /// A hash of <see cref="Endpoint"/>, which makes re-registering the same browser an upsert
    /// rather than a second row. The endpoint itself would be the natural key and is not usable as
    /// one — they run to hundreds of characters and Mongo's _id index would carry all of it.
    /// </summary>
    [BsonId]
    public required string Id { get; set; }

    /// <summary>The owning account, exactly as <see cref="Auth.User.Id"/> holds it — lowercased.</summary>
    public required string UserId { get; set; }

    /// <summary>Where the push service wants the message POSTed. Origin varies by browser vendor.</summary>
    public required string Endpoint { get; set; }

    /// <summary>The browser's public key, base64url. Half of what the payload is encrypted to.</summary>
    public required string P256dh { get; set; }

    /// <summary>The subscription's auth secret, base64url. The other half.</summary>
    public required string Auth { get; set; }

    /// <summary>
    /// How this subscription is delivered. Only <c>webpush</c> exists today; it is stored anyway
    /// because a native mobile app arrives through FCM or APNs, and those are rows in this same
    /// collection with different credentials rather than a second subscription system. Reading it
    /// before dispatch is what keeps that a addition rather than a migration.
    /// </summary>
    public string Transport { get; set; } = TransportWebPush;

    public const string TransportWebPush = "webpush";

    /// <summary>
    /// Something a person can recognise their own device by in the list — derived from the
    /// user-agent, not trusted for anything. Null when the browser sent nothing useful.
    /// </summary>
    public string? Label { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When this endpoint last accepted a message. Null until the first one lands.</summary>
    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>
    /// Consecutive failures that were not an outright rejection. A push service having a bad
    /// afternoon should not cost somebody their subscription, so this climbs and is reset by the
    /// next success; only <see cref="PushOptions.MaxFailures"/> in a row retires the row.
    /// </summary>
    public int FailureCount { get; set; }

    /// <summary>The id a given endpoint always maps to.</summary>
    public static string IdFor(string endpoint) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)));
}
