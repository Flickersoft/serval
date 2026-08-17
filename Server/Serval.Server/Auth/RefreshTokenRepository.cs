using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using Serval.Server.Storage;

namespace Serval.Server.Auth;

/// <summary>
/// Storage for refresh tokens. The raw token never reaches Mongo — only its SHA-256 hash, the
/// same "never store the credential itself" rule <see cref="UserRepository"/> follows for
/// passwords. Rotation and reuse detection happen in <c>AuthEndpoints</c>, built on the
/// family/revoked bookkeeping here; this stays a plain store.
/// </summary>
public sealed class RefreshTokenRepository
{
    private readonly IMongoCollection<RefreshToken> _tokens;

    public RefreshTokenRepository(MongoContext context) => _tokens = context.RefreshTokens;

    public static string Hash(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    public async Task InsertAsync(RefreshToken token, CancellationToken cancellationToken = default) =>
        await _tokens.InsertOneAsync(token, cancellationToken: cancellationToken);

    public async Task<RefreshToken?> GetByRawTokenAsync(
        string rawToken, CancellationToken cancellationToken = default) =>
        await _tokens.Find(t => t.TokenHash == Hash(rawToken)).FirstOrDefaultAsync(cancellationToken);

    public async Task RevokeAsync(
        ObjectId id, ObjectId? replacedByTokenId, CancellationToken cancellationToken = default) =>
        await _tokens.UpdateOneAsync(
            t => t.Id == id,
            Builders<RefreshToken>.Update
                .Set(t => t.RevokedAt, DateTimeOffset.UtcNow)
                .Set(t => t.ReplacedByTokenId, replacedByTokenId),
            cancellationToken: cancellationToken);

    /// <summary>
    /// Revokes every still-live token in a rotation family — the response to reuse detection,
    /// since a dead token being presented again means the chain was likely stolen and every
    /// descendant of it needs to stop working, not just the one that was replayed.
    /// </summary>
    public async Task RevokeFamilyAsync(Guid familyId, CancellationToken cancellationToken = default) =>
        await _tokens.UpdateManyAsync(
            t => t.FamilyId == familyId && t.RevokedAt == null,
            Builders<RefreshToken>.Update.Set(t => t.RevokedAt, DateTimeOffset.UtcNow),
            cancellationToken: cancellationToken);

    public async Task RevokeAllForUserAsync(string userId, CancellationToken cancellationToken = default) =>
        await _tokens.UpdateManyAsync(
            t => t.UserId == userId && t.RevokedAt == null,
            Builders<RefreshToken>.Update.Set(t => t.RevokedAt, DateTimeOffset.UtcNow),
            cancellationToken: cancellationToken);
}
