using Microsoft.AspNetCore.Identity;
using MongoDB.Driver;
using Serval.Server.Storage;

namespace Serval.Server.Auth;

/// <summary>
/// CRUD over accounts, password verification, and the failed-login bookkeeping that drives
/// lockout. Usernames are matched case-insensitively by lowercasing before every lookup, since
/// the id itself is stored lowercased — see <see cref="Normalize"/>.
/// </summary>
public sealed class UserRepository
{
    /// <summary>Consecutive failures before an account locks. Below this, a mistyped password is
    /// just a mistyped password.</summary>
    private const int LockoutThreshold = 5;

    private static readonly TimeSpan LockoutBase = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LockoutMax = TimeSpan.FromMinutes(30);

    private readonly IMongoCollection<User> _users;
    private readonly PasswordHasher<User> _hasher = new();

    public UserRepository(MongoContext context) => _users = context.Users;

    public async Task<User?> GetAsync(string username, CancellationToken cancellationToken = default) =>
        await _users.Find(u => u.Id == Normalize(username)).FirstOrDefaultAsync(cancellationToken);

    public async Task<List<User>> ListAsync(CancellationToken cancellationToken = default) =>
        await _users.Find(FilterDefinition<User>.Empty).ToListAsync(cancellationToken);

    public async Task<long> CountAsync(CancellationToken cancellationToken = default) =>
        await _users.CountDocumentsAsync(FilterDefinition<User>.Empty, cancellationToken: cancellationToken);

    public async Task<long> CountAdminsAsync(CancellationToken cancellationToken = default) =>
        await _users.CountDocumentsAsync(u => u.Role == Role.Admin, cancellationToken: cancellationToken);

    /// <summary>Creates an account. Throws <see cref="AuthValidationException"/> on bad input or a duplicate username.</summary>
    public async Task<User> CreateAsync(
        string username, string displayName, string password, Role role,
        CancellationToken cancellationToken = default)
    {
        Validate(username, password);

        if (await GetAsync(username, cancellationToken) is not null)
        {
            throw new AuthValidationException($"A user '{Normalize(username)}' already exists.");
        }

        var user = new User
        {
            Id = Normalize(username),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName,
            PasswordHash = "",
            Role = role,
        };
        user.PasswordHash = _hasher.HashPassword(user, password);

        await _users.InsertOneAsync(user, cancellationToken: cancellationToken);
        return user;
    }

    /// <summary>
    /// Verifies a password against the stored hash. Returns null for "wrong password" and
    /// "unknown user" alike — deliberately the same outcome for both, so the login endpoint can
    /// return one generic message without a caller here having to remember to collapse them.
    /// </summary>
    public async Task<User?> VerifyPasswordAsync(
        string username, string password, CancellationToken cancellationToken = default)
    {
        User? user = await GetAsync(username, cancellationToken);
        if (user is null)
        {
            return null;
        }

        PasswordVerificationResult result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            return null;
        }

        // The hasher asked to be upgraded (e.g. an iteration-count bump) — do it on this login
        // rather than waiting for a migration, since verifying already proved the password.
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _hasher.HashPassword(user, password);
            await _users.UpdateOneAsync(
                u => u.Id == user.Id,
                Builders<User>.Update.Set(u => u.PasswordHash, user.PasswordHash),
                cancellationToken: cancellationToken);
        }

        return user;
    }

    public static bool IsLocked(User user) =>
        user.LockedUntil is { } until && until > DateTimeOffset.UtcNow;

    /// <summary>
    /// Records a failed attempt and, once <see cref="LockoutThreshold"/> is reached, locks the
    /// account for a backoff that doubles with each further failure while locked (capped at
    /// <see cref="LockoutMax"/>) — a mistyped password recovers in a minute, a sustained attack
    /// keeps paying more. The increment is atomic, so concurrent attempts against the same
    /// account can't undercount each other.
    /// </summary>
    public async Task RecordFailedLoginAsync(string username, CancellationToken cancellationToken = default)
    {
        User? user = await _users.FindOneAndUpdateAsync(
            u => u.Id == Normalize(username),
            Builders<User>.Update.Inc(u => u.FailedLoginAttempts, 1),
            new FindOneAndUpdateOptions<User> { ReturnDocument = ReturnDocument.After },
            cancellationToken);

        if (user is null || user.FailedLoginAttempts < LockoutThreshold)
        {
            return;
        }

        int overBy = user.FailedLoginAttempts - LockoutThreshold;
        TimeSpan duration = TimeSpan.FromMilliseconds(Math.Min(
            LockoutBase.TotalMilliseconds * Math.Pow(2, overBy), LockoutMax.TotalMilliseconds));

        await _users.UpdateOneAsync(
            u => u.Id == user.Id,
            Builders<User>.Update.Set(u => u.LockedUntil, DateTimeOffset.UtcNow + duration),
            cancellationToken: cancellationToken);
    }

    public async Task ClearFailedLoginsAsync(string username, CancellationToken cancellationToken = default) =>
        await _users.UpdateOneAsync(
            u => u.Id == Normalize(username),
            Builders<User>.Update.Set(u => u.FailedLoginAttempts, 0).Set(u => u.LockedUntil, (DateTimeOffset?)null),
            cancellationToken: cancellationToken);

    /// <summary>
    /// Sets a password, returning false if the username is unknown. Does not revoke sessions —
    /// callers decide that, because "an Admin is resetting a compromised account" and "someone is
    /// changing their own password on their own laptop" want opposite answers.
    /// </summary>
    public async Task<bool> SetPasswordAsync(
        string username, string newPassword, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 8)
        {
            throw new AuthValidationException("Password must be at least 8 characters.");
        }

        // Hashed against the real account rather than a stand-in. PasswordHasher<TUser> ignores the
        // user it is handed, so this changes nothing today — but VerifyPasswordAsync passes the
        // real one, and a hasher that ever mixed identity into the salt would make every password
        // written here unverifiable. Matching the two costs a read.
        User? user = await GetAsync(username, cancellationToken);
        if (user is null)
        {
            return false;
        }

        string hash = _hasher.HashPassword(user, newPassword);

        UpdateResult result = await _users.UpdateOneAsync(
            u => u.Id == user.Id,
            Builders<User>.Update.Set(u => u.PasswordHash, hash),
            cancellationToken: cancellationToken);

        return result.MatchedCount > 0;
    }

    /// <summary>
    /// Inserts a fully-formed account, hash and all. The one write path that does not hash a
    /// password, because a restored backup arrives with the hash already made and has no plaintext
    /// to work from.
    ///
    /// <para>The caller is responsible for having checked the hash with
    /// <see cref="IsPlausiblePasswordHash"/> first — see the note there for what an unchecked one
    /// does to the login endpoint.</para>
    /// </summary>
    public async Task InsertRestoredAsync(User user, CancellationToken cancellationToken = default)
    {
        user.Id = Normalize(user.Id);
        await _users.InsertOneAsync(user, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Applies a restored account over one that already exists, setting only what is non-null.
    ///
    /// <para>Null means "leave this alone", which is what lets a restore withhold a single field —
    /// the account performing the restore keeps its own role and password while its display name is
    /// restored like anyone else's.</para>
    ///
    /// <para>A non-null <paramref name="passwordHash"/> also clears the lockout bookkeeping. A
    /// password change is the recovery action for a lockout, so leaving a countdown in place behind
    /// a credential that no longer exists would make the restore look like it had failed.</para>
    /// </summary>
    public async Task UpdateRestoredAsync(
        string username,
        string? displayName,
        string? passwordHash,
        Role? role,
        CancellationToken cancellationToken = default)
    {
        List<UpdateDefinition<User>> sets = [];

        if (displayName is not null)
        {
            sets.Add(Builders<User>.Update.Set(u => u.DisplayName, displayName));
        }

        if (passwordHash is not null)
        {
            sets.Add(Builders<User>.Update.Set(u => u.PasswordHash, passwordHash));
            sets.Add(Builders<User>.Update.Set(u => u.FailedLoginAttempts, 0));
            sets.Add(Builders<User>.Update.Set(u => u.LockedUntil, (DateTimeOffset?)null));
        }

        if (role is { } newRole)
        {
            sets.Add(Builders<User>.Update.Set(u => u.Role, newRole));
        }

        if (sets.Count == 0)
        {
            return;
        }

        await _users.UpdateOneAsync(
            u => u.Id == Normalize(username),
            Builders<User>.Update.Combine(sets),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Whether a string could be <see cref="PasswordHasher{TUser}"/> output.
    ///
    /// <para><b>This is not cosmetic.</b> <c>VerifyHashedPassword</c> calls
    /// <c>Convert.FromBase64String</c> on the stored hash with nothing catching a bad one, so an
    /// account carrying a hash that is not base64 makes every login attempt for that username throw
    /// — a 500, permanently, with no path back through the UI. The only place a hash arrives from
    /// outside this class is a restored backup, and a restore that skips one account is recoverable
    /// where one that bricks an account is not.</para>
    ///
    /// <para>A cheap shape check, not a verification: the first byte is the hasher's format marker
    /// (0 for the v2 format, 1 for v3), and there is no way to tell a well-formed hash of an unknown
    /// password from a well-formed hash of a known one — nor any reason to want to.</para>
    /// </summary>
    public static bool IsPlausiblePasswordHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return false;
        }

        Span<byte> decoded = new byte[hash.Length];
        return Convert.TryFromBase64String(hash, decoded, out int written)
            && written > 1
            && decoded[0] is 0x00 or 0x01;
    }

    /// <summary>Returns false if the id is unknown, or if this would demote the last remaining
    /// Admin — a household system that locks itself out of camera management has no recovery path.</summary>
    public async Task<bool> UpdateRoleAsync(string username, Role role, CancellationToken cancellationToken = default)
    {
        if (role != Role.Admin && await WouldRemoveLastAdminAsync(username, cancellationToken))
        {
            throw new AuthValidationException("Cannot demote the last remaining Admin account.");
        }

        UpdateResult result = await _users.UpdateOneAsync(
            u => u.Id == Normalize(username),
            Builders<User>.Update.Set(u => u.Role, role),
            cancellationToken: cancellationToken);
        return result.MatchedCount > 0;
    }

    public async Task<bool> DeleteAsync(string username, CancellationToken cancellationToken = default)
    {
        if (await WouldRemoveLastAdminAsync(username, cancellationToken))
        {
            throw new AuthValidationException("Cannot delete the last remaining Admin account.");
        }

        DeleteResult result = await _users.DeleteOneAsync(u => u.Id == Normalize(username), cancellationToken);
        return result.DeletedCount > 0;
    }

    private async Task<bool> WouldRemoveLastAdminAsync(string username, CancellationToken cancellationToken)
    {
        User? user = await GetAsync(username, cancellationToken);
        return user is { Role: Role.Admin } && await CountAdminsAsync(cancellationToken) <= 1;
    }

    public static void Validate(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Any(char.IsWhiteSpace))
        {
            throw new AuthValidationException("Username is required and cannot contain whitespace.");
        }

        if (string.IsNullOrEmpty(password) || password.Length < 8)
        {
            throw new AuthValidationException("Password must be at least 8 characters.");
        }
    }

    /// <summary>
    /// A username reduced to the form stored as <see cref="User.Id"/>. Public because that id is
    /// the key other per-account collections are filed under, so anything storing state for a user
    /// has to arrive at the same string this does.
    /// </summary>
    public static string Normalize(string username) => username.Trim().ToLowerInvariant();
}
