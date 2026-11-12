using MongoDB.Bson;
using MongoDB.Driver;
using Serval.Server.GoogleHome;
using Serval.Server.Storage;

namespace Serval.Server.Mcp;

/// <summary>
/// Storage for everything Serval issues to MCP clients: registrations, pending authorization
/// requests, codes, grants and tokens.
///
/// <para>Raw credentials never reach Mongo, only SHA-256 hashes — the rule
/// <see cref="GoogleOAuthStore"/> and <c>Auth/RefreshTokenRepository</c> already follow, using the
/// same helpers. The OAuth decisions live in <see cref="McpOAuthEndpoints"/>; what lives here is
/// whatever has to be a single atomic update to be safe — consuming a code, completing a request,
/// spending a refresh token — because a read followed by a write would let two callers both win.</para>
/// </summary>
public sealed class McpOAuthStore
{
    /// <summary>How long an unused dynamic registration survives. See <see cref="McpRegisteredClient"/>.</summary>
    private static readonly TimeSpan RegistrationLifetime = TimeSpan.FromDays(1);

    /// <summary>
    /// The least time between two <see cref="McpGrant.LastUsedAt"/> writes. The stamp is for a
    /// person reading "last used", for whom five minutes is exact; without the floor every tool
    /// call would be a database write.
    /// </summary>
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    private readonly IMongoCollection<McpRegisteredClient> _clients;
    private readonly IMongoCollection<McpAuthorizationRequest> _requests;
    private readonly IMongoCollection<McpAuthorizationCode> _codes;
    private readonly IMongoCollection<McpGrant> _grants;
    private readonly IMongoCollection<McpToken> _tokens;

    public McpOAuthStore(MongoContext context)
    {
        _clients = context.McpClients;
        _requests = context.McpAuthorizationRequests;
        _codes = context.McpAuthorizationCodes;
        _grants = context.McpGrants;
        _tokens = context.McpTokens;
    }

    // ---------------------------------------------------------------- registrations

    public async Task<McpRegisteredClient> RegisterClientAsync(
        string? clientName, IReadOnlyList<string> redirectUris, CancellationToken ct = default)
    {
        var client = new McpRegisteredClient
        {
            ClientId = GoogleOAuthStore.NewSecret(),
            ClientName = clientName,
            RedirectUris = [.. redirectUris],
            ExpiresAt = DateTimeOffset.UtcNow + RegistrationLifetime,
        };

        await _clients.InsertOneAsync(client, cancellationToken: ct);
        return client;
    }

    public async Task<McpRegisteredClient?> GetRegisteredClientAsync(string clientId, CancellationToken ct = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return await _clients
            .Find(c => c.ClientId == clientId && (c.ExpiresAt == null || c.ExpiresAt > now))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Takes a registration off the expiry index once something has actually been granted to it.</summary>
    private Task KeepRegisteredClientAsync(string clientId, CancellationToken ct) =>
        _clients.UpdateOneAsync(
            c => c.ClientId == clientId,
            Builders<McpRegisteredClient>.Update.Unset(c => c.ExpiresAt),
            cancellationToken: ct);

    // ------------------------------------------------------------------- requests

    public Task CreateRequestAsync(McpAuthorizationRequest request, CancellationToken ct = default) =>
        _requests.InsertOneAsync(request, cancellationToken: ct);

    /// <summary>A request still waiting for an answer, or null if it is unknown, expired or answered.</summary>
    public async Task<McpAuthorizationRequest?> GetOpenRequestAsync(string id, CancellationToken ct = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return await _requests
            .Find(r => r.Id == id && r.CompletedAt == null && r.ExpiresAt > now)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Marks a request answered and returns it, or null if somebody else already answered it. One
    /// update matching <c>CompletedAt == null</c>, so a double-clicked Allow issues one code.
    /// </summary>
    public async Task<McpAuthorizationRequest?> CompleteRequestAsync(string id, CancellationToken ct = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return await _requests.FindOneAndUpdateAsync<McpAuthorizationRequest>(
            r => r.Id == id && r.CompletedAt == null && r.ExpiresAt > now,
            Builders<McpAuthorizationRequest>.Update.Set(r => r.CompletedAt, now),
            new FindOneAndUpdateOptions<McpAuthorizationRequest> { ReturnDocument = ReturnDocument.After },
            ct);
    }

    // ---------------------------------------------------------------------- codes

    /// <summary>Issues a code for an approved request and returns the raw value, the only time it exists in the clear.</summary>
    public async Task<string> IssueCodeAsync(
        McpAuthorizationRequest request, string userId, TimeSpan lifetime, CancellationToken ct = default)
    {
        string raw = GoogleOAuthStore.NewSecret();

        await _codes.InsertOneAsync(
            new McpAuthorizationCode
            {
                CodeHash = GoogleOAuthStore.Hash(raw),
                UserId = userId,
                ClientId = request.ClientId,
                ClientName = request.ClientName,
                Verified = request.Verified,
                RedirectUri = request.RedirectUri,
                CodeChallenge = request.CodeChallenge,
                Resource = request.Resource,
                ExpiresAt = DateTimeOffset.UtcNow + lifetime,
            },
            cancellationToken: ct);

        return raw;
    }

    /// <summary>
    /// Marks a code used and returns it, or null if it is unknown, expired or already redeemed —
    /// one atomic update, so a replay loses the race. Same shape as
    /// <see cref="GoogleOAuthStore.ConsumeCodeAsync"/>.
    /// </summary>
    public async Task<McpAuthorizationCode?> ConsumeCodeAsync(string rawCode, CancellationToken ct = default)
    {
        string hash = GoogleOAuthStore.Hash(rawCode);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        return await _codes.FindOneAndUpdateAsync<McpAuthorizationCode>(
            c => c.CodeHash == hash && c.ConsumedAt == null && c.ExpiresAt > now,
            Builders<McpAuthorizationCode>.Update.Set(c => c.ConsumedAt, now),
            new FindOneAndUpdateOptions<McpAuthorizationCode> { ReturnDocument = ReturnDocument.After },
            ct);
    }

    // --------------------------------------------------------------------- grants

    /// <summary>The user's grant for this client, created on first approval. See <see cref="McpGrant"/>.</summary>
    public async Task<McpGrant> EnsureGrantAsync(McpAuthorizationCode code, CancellationToken ct = default)
    {
        McpGrant grant = await _grants.FindOneAndUpdateAsync<McpGrant>(
            g => g.UserId == code.UserId && g.ClientId == code.ClientId,
            Builders<McpGrant>.Update
                .SetOnInsert(g => g.Id, ObjectId.GenerateNewId())
                .SetOnInsert(g => g.UserId, code.UserId)
                .SetOnInsert(g => g.ClientId, code.ClientId)
                .SetOnInsert(g => g.CreatedAt, DateTimeOffset.UtcNow)
                .Set(g => g.ClientName, code.ClientName)
                .Set(g => g.Verified, code.Verified)
                .Set(g => g.Resource, code.Resource),
            new FindOneAndUpdateOptions<McpGrant> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
            ct);

        if (!code.Verified)
        {
            await KeepRegisteredClientAsync(code.ClientId, ct);
        }

        return grant;
    }

    public async Task<McpGrant?> GetGrantAsync(ObjectId id, CancellationToken ct = default) =>
        await _grants.Find(g => g.Id == id).FirstOrDefaultAsync(ct);

    /// <summary>
    /// A user's live grants: unexpired, and — when <paramref name="resource"/> is known — issued for
    /// the current address. Null while the endpoint is closed, when every unexpired grant is shown
    /// so it can still be revoked before switching back on.
    /// </summary>
    public async Task<List<McpGrant>> ListGrantsAsync(string userId, string? resource, CancellationToken ct = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        FilterDefinitionBuilder<McpGrant> by = Builders<McpGrant>.Filter;
        FilterDefinition<McpGrant> filter = by.Eq(g => g.UserId, userId) & by.Gt(g => g.ExpiresAt, now);

        if (resource is not null)
        {
            filter &= by.Eq(g => g.Resource, resource);
        }

        return await _grants.Find(filter).SortByDescending(g => g.CreatedAt).ToListAsync(ct);
    }

    /// <summary>
    /// Removes a grant and every token under it. Tokens first: a grant deleted while its tokens
    /// survived would be access nothing lists.
    /// </summary>
    public async Task RevokeGrantAsync(ObjectId grantId, CancellationToken ct = default)
    {
        await _tokens.DeleteManyAsync(t => t.GrantId == grantId, ct);
        await _grants.DeleteOneAsync(g => g.Id == grantId, ct);
    }

    /// <summary>Every grant a user holds — what "sign out everywhere" and deleting the account do.</summary>
    public async Task RevokeAllForUserAsync(string userId, CancellationToken ct = default)
    {
        await _tokens.DeleteManyAsync(t => t.UserId == userId, ct);
        await _grants.DeleteManyAsync(g => g.UserId == userId, ct);
    }

    /// <summary>Stamps <see cref="McpGrant.LastUsedAt"/>, at most once per <see cref="TouchInterval"/>.</summary>
    public async Task TouchGrantAsync(ObjectId grantId, CancellationToken ct = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset stale = now - TouchInterval;

        await _grants.UpdateOneAsync(
            g => g.Id == grantId && (g.LastUsedAt == null || g.LastUsedAt < stale),
            Builders<McpGrant>.Update.Set(g => g.LastUsedAt, now),
            cancellationToken: ct);
    }

    // --------------------------------------------------------------------- tokens

    /// <summary>Issues an access and refresh token under a grant and returns the raw values.</summary>
    public async Task<(string Access, string Refresh)> IssueTokenPairAsync(
        McpGrant grant,
        string resource,
        TimeSpan accessLifetime,
        TimeSpan refreshLifetime,
        CancellationToken ct = default)
    {
        string access = GoogleOAuthStore.NewSecret();
        string refresh = GoogleOAuthStore.NewSecret();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await _tokens.InsertManyAsync(
            [
                NewToken(McpTokenKind.Access, access, grant, resource, now + accessLifetime),
                NewToken(McpTokenKind.Refresh, refresh, grant, resource, now + refreshLifetime),
            ],
            cancellationToken: ct);

        // The grant lives exactly as long as its newest refresh token can.
        await _grants.UpdateOneAsync(
            g => g.Id == grant.Id,
            Builders<McpGrant>.Update.Set(g => g.ExpiresAt, now + refreshLifetime).Set(g => g.Resource, resource),
            cancellationToken: ct);

        return (access, refresh);
    }

    private static McpToken NewToken(
        McpTokenKind kind, string raw, McpGrant grant, string resource, DateTimeOffset expiresAt) =>
        new()
        {
            TokenHash = GoogleOAuthStore.Hash(raw),
            Kind = kind,
            GrantId = grant.Id,
            UserId = grant.UserId,
            ClientId = grant.ClientId,
            Resource = resource,
            ExpiresAt = expiresAt,
        };

    /// <summary>A live access token, or null.</summary>
    public async Task<McpToken?> FindAccessTokenAsync(string rawToken, CancellationToken ct = default)
    {
        string hash = GoogleOAuthStore.Hash(rawToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        return await _tokens
            .Find(t => t.TokenHash == hash && t.Kind == McpTokenKind.Access && t.ExpiresAt > now)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Spends a refresh token. Returns the token and <c>Reused: false</c> when this call spent it;
    /// the token and <c>Reused: true</c> when it had already been spent, which the caller answers by
    /// revoking the grant; and null when it is unknown or expired.
    /// </summary>
    public async Task<(McpToken Token, bool Reused)?> SpendRefreshTokenAsync(
        string rawToken, CancellationToken ct = default)
    {
        string hash = GoogleOAuthStore.Hash(rawToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        McpToken? spent = await _tokens.FindOneAndUpdateAsync<McpToken>(
            t => t.TokenHash == hash && t.Kind == McpTokenKind.Refresh && t.UsedAt == null && t.ExpiresAt > now,
            Builders<McpToken>.Update.Set(t => t.UsedAt, now),
            new FindOneAndUpdateOptions<McpToken> { ReturnDocument = ReturnDocument.After },
            ct);

        if (spent is not null)
        {
            return (spent, false);
        }

        McpToken? reused = await _tokens
            .Find(t => t.TokenHash == hash && t.Kind == McpTokenKind.Refresh && t.UsedAt != null)
            .FirstOrDefaultAsync(ct);

        return reused is null ? null : (reused, true);
    }
}
