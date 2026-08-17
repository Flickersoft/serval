using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Serval.Server.Auth;

/// <summary>
/// A refresh token, stored only as its hash — like <see cref="User.PasswordHash"/>, the raw value
/// is a credential and never touches the database. <see cref="FamilyId"/> links every token born
/// from one login's rotation chain: presenting a token that has already been rotated past (or
/// revoked) is the signal that the chain was stolen, so the whole family is revoked in response,
/// not just the one token. See <c>AuthEndpoints.RefreshAsync</c> for where that happens.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class RefreshToken
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.GenerateNewId();

    public required string UserId { get; set; }

    public required string TokenHash { get; set; }

    public required Guid FamilyId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Indexed with a Mongo TTL index (see <c>MongoContext.InitializeAsync</c>) so expired
    /// and long-revoked rows self-clean without a sweep worker.</summary>
    public required DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>The token this one was rotated into, for audit trails. Null until rotated.</summary>
    public ObjectId? ReplacedByTokenId { get; set; }
}
