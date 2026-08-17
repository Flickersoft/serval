using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace Serval.Server.Auth;

/// <summary>
/// A person who can log in. The id is the username, lowercased at creation, so lookup is a
/// direct key match and two accounts can never differ only by case.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class User
{
    [BsonId]
    public required string Id { get; set; }

    public required string DisplayName { get; set; }

    /// <summary><see cref="Microsoft.AspNetCore.Identity.PasswordHasher{TUser}"/> output — versioned
    /// PBKDF2. The plaintext password is never stored, logged, or held longer than verification needs.</summary>
    public required string PasswordHash { get; set; }

    public Role Role { get; set; } = Role.Viewer;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Consecutive failed logins since the last success. Drives the lockout backoff in
    /// <see cref="UserRepository.RecordFailedLoginAsync"/>.</summary>
    public int FailedLoginAttempts { get; set; }

    /// <summary>Set while locked out; cleared on the next successful login.</summary>
    public DateTimeOffset? LockedUntil { get; set; }
}

/// <summary>
/// The split is configuration against operation. Admin can register/edit/delete cameras, change
/// settings, and manage other accounts. Viewer can watch live and recorded video, read telemetry,
/// and work the live controls — pan/tilt/zoom and talk-back — but cannot change how a camera is
/// set up.
/// </summary>
[JsonConverter(typeof(RoleConverter))]
public enum Role
{
    Viewer,
    Admin,
}

/// <summary>
/// Writes roles as <c>"viewer"</c> / <c>"admin"</c> rather than as numbers — the same reasoning
/// (and the same shape) as <see cref="Cameras.StreamRoleConverter"/>: a client reading a raw 0/1
/// would have the enum's declaration order silently baked into its parsing.
/// </summary>
public sealed class RoleConverter() : JsonStringEnumConverter<Role>(JsonNamingPolicy.CamelCase);
