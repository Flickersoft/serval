using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Serval.Server.GoogleHome;

/// <summary>
/// An authorization code handed to Google, stored only as its hash — the same "never store the
/// credential itself" rule <see cref="Auth.RefreshToken"/> follows, and for the same reason: this
/// collection is one database read away from anything that can reach Mongo.
///
/// <para><b>Single use is enforced by the update, not by a read.</b> <see cref="ConsumedAt"/> is
/// set by a <c>FindOneAndUpdate</c> that matches only rows where it is still null, so two
/// simultaneous exchanges cannot both win. A read-then-write would leave a window in which a
/// replayed code mints a second set of tokens.</para>
/// </summary>
[BsonIgnoreExtraElements]
public sealed class GoogleAuthorizationCode
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public required string CodeHash { get; set; }

    /// <summary>The link this code will grant tokens for. Stable across re-authorization.</summary>
    public required string AgentUserId { get; set; }

    /// <summary>
    /// Bound at issue and compared at exchange. Google sends it both times, and an attacker who
    /// obtained a code cannot redeem it against a different destination.
    /// </summary>
    public required string RedirectUri { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Carries a Mongo TTL index, so a code nobody exchanged disappears without a sweep worker —
    /// the arrangement <c>MongoContext.InitializeAsync</c> already uses for refresh tokens.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }
}

/// <summary>Which half of the pair a stored token is. Persisted by name, so the numbers may move.</summary>
public enum GoogleTokenKind
{
    Access,
    Refresh,
}

/// <summary>
/// A bearer token issued to Google, stored only as its hash.
///
/// <para><b>Refresh tokens never expire here, and that is not an oversight.</b> Google's
/// cloud-to-cloud contract has the <c>refresh_token</c> grant answer <em>without</em> a new refresh
/// token, so the one issued at link time is the only one Google will ever hold. Expiring it — or
/// rotating it the way <c>Auth/RefreshTokenRepository</c> rotates the App's, with family revocation
/// on reuse — would leave Google presenting a dead credential on its next refresh and the cameras
/// silently gone. <see cref="ExpiresAt"/> is therefore null for a refresh token, which is also what
/// keeps the TTL index off it: Mongo skips a document whose indexed field is not a date.</para>
///
/// <para>Revocation is a delete rather than a flag. There is at most one linked account, so there
/// is no audit trail worth keeping and a missing row answers 401 exactly as a revoked one would.</para>
/// </summary>
[BsonIgnoreExtraElements]
public sealed class GoogleToken
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public required string TokenHash { get; set; }

    [BsonRepresentation(BsonType.String)]
    public required GoogleTokenKind Kind { get; set; }

    public required string AgentUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the token stops working. Null for refresh tokens — see the type's remarks.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>
/// The one Google account this deployment is linked to.
///
/// <para><b>The id is a generated GUID, not the Serval username.</b> Google is told this value and
/// sends it back on every request; a username would hand it an account name for no benefit. It is
/// generated once and kept stable across re-authorization, because <c>requestSync</c> addresses it
/// — a new id on every link would leave HomeGraph updating a user that no longer exists. Unlinking
/// deletes the document, so a fresh link does start a fresh id.</para>
///
/// <para>Its own document rather than a field on a token, because it outlives every token: a
/// DISCONNECT that revoked tokens alone would leave nothing to list on the admin card and nothing
/// for <c>requestSync</c> to address.</para>
/// </summary>
[BsonIgnoreExtraElements]
public sealed class GoogleLink
{
    [BsonId]
    public required string AgentUserId { get; set; }

    public DateTimeOffset LinkedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Last time Google called fulfillment. The admin card's "is this alive" signal.</summary>
    public DateTimeOffset? LastFulfillmentAt { get; set; }

    /// <summary>Last successful <c>requestSync</c>. Null when no HomeGraph key is configured.</summary>
    public DateTimeOffset? LastSyncAt { get; set; }
}
