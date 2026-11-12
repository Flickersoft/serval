using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Serval.Server.Mcp;

/// <summary>
/// A client that registered itself through <c>POST /api/mcp/oauth/register</c> (RFC 7591 dynamic
/// client registration). Clients that identify themselves with a metadata-document URL instead
/// (CIMD) have no row here — their document is fetched, not stored; see
/// <see cref="McpClientResolver"/>.
///
/// <para><b>Public clients only.</b> There is no secret: the registration endpoint is anonymous by
/// protocol design, so a secret it handed out would prove only that somebody once called it. PKCE
/// is what binds a code to the client that asked for it.</para>
///
/// <para><b>Expires unless used.</b> Some clients register afresh on every connection attempt, and
/// the endpoint is open, so an unused registration lapses after a day through the TTL index. The
/// first successful token exchange clears <see cref="ExpiresAt"/>, which takes the row off the
/// index for good — Mongo skips a document whose indexed field is not a date.</para>
/// </summary>
[BsonIgnoreExtraElements]
public sealed class McpRegisteredClient
{
    [BsonId]
    public required string ClientId { get; set; }

    /// <summary>What the client calls itself. Self-asserted, so the consent screen never shows it alone.</summary>
    public string? ClientName { get; set; }

    public required List<string> RedirectUris { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>
/// An authorization request waiting for somebody to approve or deny it in the App.
///
/// <para>The authorize endpoint validates everything it can and parks the result here, so the App
/// carries only this row's id through the browser. Its id is not a credential — approving needs a
/// signed-in session — so it is stored as-is rather than hashed.</para>
/// </summary>
[BsonIgnoreExtraElements]
public sealed class McpAuthorizationRequest
{
    [BsonId]
    public required string Id { get; set; }

    public required string ClientId { get; set; }

    public string? ClientName { get; set; }

    /// <summary>True when the client identified itself by a metadata document on its own domain.</summary>
    public required bool Verified { get; set; }

    public required string RedirectUri { get; set; }

    public string? State { get; set; }

    public required string CodeChallenge { get; set; }

    public required string Resource { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>TTL-indexed, and also checked on every read since the TTL monitor runs on its own schedule.</summary>
    public required DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set by an atomic update when the request is approved or denied, so it can be answered once.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// An authorization code, stored only as its hash, bound to everything the token endpoint has to
/// check: who approved it, which client for, where it may be redeemed, the PKCE challenge, and the
/// resource the resulting token will be good for.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class McpAuthorizationCode
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public required string CodeHash { get; set; }

    public required string UserId { get; set; }

    public required string ClientId { get; set; }

    public string? ClientName { get; set; }

    public required bool Verified { get; set; }

    public required string RedirectUri { get; set; }

    public required string CodeChallenge { get; set; }

    public required string Resource { get; set; }

    public required DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }
}

/// <summary>
/// One person's approval of one client: what the Account screen lists and what revoking deletes.
///
/// <para><b>One per user and client, not one per approval.</b> Approving a client again — a second
/// device running the same assistant, or a reconnect — adds tokens to the existing grant rather
/// than replacing it, so linking a laptop does not quietly disconnect a phone. Revoking the grant
/// removes every token under it at once.</para>
/// </summary>
[BsonIgnoreExtraElements]
public sealed class McpGrant
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public required string UserId { get; set; }

    public required string ClientId { get; set; }

    public string? ClientName { get; set; }

    public required bool Verified { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Stamped at most every few minutes by the bearer handler — see <see cref="McpOAuthStore.TouchGrantAsync"/>.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>
    /// The <c>/mcp</c> URL its tokens are bound to. A grant issued under a previous public address
    /// can no longer be used, so it is no longer listed.
    /// </summary>
    public string? Resource { get; set; }

    /// <summary>
    /// When the newest refresh token under this grant expires — moved forward on every issue, and
    /// TTL-indexed, so a link nobody uses for <c>RefreshTokenDays</c> disappears from the list at
    /// the same moment it stops working.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}

/// <summary>Which half of a token pair a stored token is. Persisted by name.</summary>
public enum McpTokenKind
{
    Access,
    Refresh,
}

/// <summary>
/// A bearer token issued to an assistant, stored only as its hash.
///
/// <para><b>Refresh tokens rotate, and a reused one revokes the grant.</b> OAuth 2.1 requires
/// rotation for public clients, which is every client here. A spent refresh token is kept, marked
/// with <see cref="UsedAt"/>, until it would have expired anyway; presenting it again means two
/// parties hold the chain, and since there is no telling which is genuine, the whole grant goes.
/// The App's own refresh tokens follow the same rule — see <c>Auth/RefreshTokenRepository</c>.</para>
/// </summary>
[BsonIgnoreExtraElements]
public sealed class McpToken
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public required string TokenHash { get; set; }

    [BsonRepresentation(BsonType.String)]
    public required McpTokenKind Kind { get; set; }

    public required ObjectId GrantId { get; set; }

    public required string UserId { get; set; }

    public required string ClientId { get; set; }

    /// <summary>The <c>/mcp</c> URL this token was issued for. A token is refused anywhere else.</summary>
    public required string Resource { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public required DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When a refresh token was spent. Null on access tokens and on live refresh tokens.</summary>
    public DateTimeOffset? UsedAt { get; set; }
}
