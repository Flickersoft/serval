using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Driver;
using Serval.Server.Storage;

namespace Serval.Server.GoogleHome;

/// <summary>
/// Storage for the credentials this server issues to Google: authorization codes, access and
/// refresh tokens, and the single link they all belong to.
///
/// <para>Raw values never reach Mongo — only their SHA-256 hashes, the rule
/// <c>Auth/RefreshTokenRepository</c> and <c>Auth/UserRepository</c> already follow. A lookup
/// therefore hashes what it was given and matches on that; there is no way back from a stored row
/// to a working credential.</para>
///
/// <para>Deliberately a plain store with the OAuth decisions left to
/// <see cref="GoogleOAuthEndpoints"/> — with one exception. Consuming a code is
/// <em>here</em>, because single-use is a storage guarantee: it has to be one atomic update, and a
/// caller that could read a code and then mark it used would have a window in which two exchanges
/// both succeed.</para>
/// </summary>
public sealed class GoogleOAuthStore
{
    private readonly IMongoCollection<GoogleAuthorizationCode> _codes;
    private readonly IMongoCollection<GoogleToken> _tokens;
    private readonly IMongoCollection<GoogleLink> _links;

    public GoogleOAuthStore(MongoContext context)
    {
        _codes = context.GoogleAuthorizationCodes;
        _tokens = context.GoogleTokens;
        _links = context.GoogleLinks;
    }

    public static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    /// <summary>
    /// A 256-bit URL-safe secret. Base64url rather than base64 because every one of these ends up
    /// in a query string or a form body at some point, and '+' and '/' do not survive that
    /// unescaped.
    /// </summary>
    public static string NewSecret() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    // ------------------------------------------------------------------ links

    /// <summary>
    /// The linked account, or null when nobody has linked. At most one exists — see
    /// <see cref="GoogleLink"/>.
    /// </summary>
    public async Task<GoogleLink?> GetLinkAsync(CancellationToken ct = default) =>
        await _links.Find(FilterDefinition<GoogleLink>.Empty).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<GoogleLink>> ListLinksAsync(CancellationToken ct = default) =>
        await _links.Find(FilterDefinition<GoogleLink>.Empty).ToListAsync(ct);

    /// <summary>
    /// The agent user id to issue against, creating the link on first use. Stable afterwards, so
    /// re-authorizing an existing link does not orphan HomeGraph's view of it.
    /// </summary>
    public async Task<string> EnsureLinkAsync(CancellationToken ct = default)
    {
        GoogleLink? existing = await GetLinkAsync(ct);
        if (existing is not null)
        {
            return existing.AgentUserId;
        }

        var link = new GoogleLink { AgentUserId = Guid.NewGuid().ToString("N") };
        await _links.InsertOneAsync(link, cancellationToken: ct);
        return link.AgentUserId;
    }

    /// <summary>Stamps the last time Google actually called. Fire-and-forget from fulfillment.</summary>
    public async Task TouchFulfillmentAsync(string agentUserId, CancellationToken ct = default) =>
        await _links.UpdateOneAsync(
            l => l.AgentUserId == agentUserId,
            Builders<GoogleLink>.Update.Set(l => l.LastFulfillmentAt, DateTimeOffset.UtcNow),
            cancellationToken: ct);

    public async Task TouchSyncAsync(string agentUserId, CancellationToken ct = default) =>
        await _links.UpdateOneAsync(
            l => l.AgentUserId == agentUserId,
            Builders<GoogleLink>.Update.Set(l => l.LastSyncAt, DateTimeOffset.UtcNow),
            cancellationToken: ct);

    /// <summary>
    /// Removes the link and every credential issued under it — what DISCONNECT and the admin
    /// card's Unlink both do. Tokens go first: a link deleted while its tokens survived would be
    /// an account that still answers fulfillment and that nothing lists.
    /// </summary>
    public async Task UnlinkAsync(string agentUserId, CancellationToken ct = default)
    {
        await _tokens.DeleteManyAsync(t => t.AgentUserId == agentUserId, ct);
        await _codes.DeleteManyAsync(c => c.AgentUserId == agentUserId, ct);
        await _links.DeleteOneAsync(l => l.AgentUserId == agentUserId, ct);
    }

    // ------------------------------------------------------------------ codes

    /// <summary>Issues a code and returns the raw value — the only moment it exists in the clear.</summary>
    public async Task<string> IssueCodeAsync(
        string agentUserId, string redirectUri, TimeSpan lifetime, CancellationToken ct = default)
    {
        string raw = NewSecret();

        await _codes.InsertOneAsync(
            new GoogleAuthorizationCode
            {
                CodeHash = Hash(raw),
                AgentUserId = agentUserId,
                RedirectUri = redirectUri,
                ExpiresAt = DateTimeOffset.UtcNow + lifetime,
            },
            cancellationToken: ct);

        return raw;
    }

    /// <summary>
    /// Marks a code used and returns it, or null if it does not exist, has expired, or has already
    /// been redeemed.
    ///
    /// <para>One atomic <c>FindOneAndUpdate</c> matching on <c>ConsumedAt == null</c>: the filter
    /// is what makes it single-use, so a replay loses the race rather than being caught by a check.
    /// Expiry is filtered here too rather than left to the TTL index, which Mongo runs on its own
    /// schedule and may not have swept yet.</para>
    /// </summary>
    public async Task<GoogleAuthorizationCode?> ConsumeCodeAsync(
        string rawCode, CancellationToken ct = default)
    {
        string hash = Hash(rawCode);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        return await _codes.FindOneAndUpdateAsync<GoogleAuthorizationCode>(
            c => c.CodeHash == hash && c.ConsumedAt == null && c.ExpiresAt > now,
            Builders<GoogleAuthorizationCode>.Update.Set(c => c.ConsumedAt, now),
            new FindOneAndUpdateOptions<GoogleAuthorizationCode>
            {
                ReturnDocument = ReturnDocument.After,
            },
            ct);
    }

    // ----------------------------------------------------------------- tokens

    /// <summary>
    /// Issues a token and returns the raw value. <paramref name="expiresAt"/> is null for a
    /// refresh token, which never expires — see <see cref="GoogleToken"/>.
    /// </summary>
    public async Task<string> IssueTokenAsync(
        GoogleTokenKind kind,
        string agentUserId,
        DateTimeOffset? expiresAt,
        CancellationToken ct = default)
    {
        string raw = NewSecret();

        await _tokens.InsertOneAsync(
            new GoogleToken
            {
                TokenHash = Hash(raw),
                Kind = kind,
                AgentUserId = agentUserId,
                ExpiresAt = expiresAt,
            },
            cancellationToken: ct);

        return raw;
    }

    /// <summary>
    /// Looks up a live token of the expected kind. The kind is part of the match so an access
    /// token cannot be presented at the refresh grant or the reverse — the same cross-rejection
    /// <c>Program.cs</c> does between the App's two JWT schemes, where a signature alone likewise
    /// says nothing about which of the two a credential was meant to be.
    /// </summary>
    public async Task<GoogleToken?> FindTokenAsync(
        string rawToken, GoogleTokenKind kind, CancellationToken ct = default)
    {
        string hash = Hash(rawToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        return await _tokens
            .Find(t => t.TokenHash == hash
                && t.Kind == kind
                && (t.ExpiresAt == null || t.ExpiresAt > now))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Drops every token of one kind for a link. Used when a fresh authorization code is redeemed:
    /// the new grant supersedes the old one, and leaving the previous access tokens live would mean
    /// a re-link never actually took anything away.
    /// </summary>
    public async Task RevokeTokensAsync(
        string agentUserId, CancellationToken ct = default) =>
        await _tokens.DeleteManyAsync(t => t.AgentUserId == agentUserId, ct);
}
