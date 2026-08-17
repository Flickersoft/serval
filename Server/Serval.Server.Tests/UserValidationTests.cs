using Microsoft.AspNetCore.Identity;
using Serval.Server.Auth;

namespace Serval.Server.Tests;

/// <summary>
/// The rules that keep bad input from ever reaching Mongo — the same shape as
/// CameraValidationTests for CameraRepository.Validate.
/// </summary>
public class UserValidationTests
{
    [Fact]
    public void A_reasonable_username_and_password_pass()
    {
        UserRepository.Validate("alice", "correct-horse-battery");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    public void A_bad_username_is_rejected(string username)
    {
        Assert.Throws<AuthValidationException>(() => UserRepository.Validate(username, "long-enough"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short1")]
    public void A_short_password_is_rejected(string password)
    {
        Assert.Throws<AuthValidationException>(() => UserRepository.Validate("alice", password));
    }

    [Fact]
    public void A_user_with_no_lock_is_not_locked()
    {
        var user = new User { Id = "alice", DisplayName = "Alice", PasswordHash = "x" };
        Assert.False(UserRepository.IsLocked(user));
    }

    [Fact]
    public void A_user_locked_in_the_future_is_locked()
    {
        var user = new User
        {
            Id = "alice",
            DisplayName = "Alice",
            PasswordHash = "x",
            LockedUntil = DateTimeOffset.UtcNow.AddMinutes(1),
        };
        Assert.True(UserRepository.IsLocked(user));
    }

    [Fact]
    public void A_user_whose_lock_expired_is_not_locked()
    {
        var user = new User
        {
            Id = "alice",
            DisplayName = "Alice",
            PasswordHash = "x",
            LockedUntil = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        Assert.False(UserRepository.IsLocked(user));
    }

    // ---------------------------------------------------------------- restored hashes

    /// <summary>
    /// The one field that arrives from outside this class already hashed, and the one that has to be
    /// checked before it is stored: <c>PasswordHasher</c> base64-decodes the stored hash with
    /// nothing catching a bad one, so an account carrying a hash that is not base64 throws on every
    /// login attempt for that username — a 500, permanently, with no path back through the UI.
    /// </summary>
    [Fact]
    public void A_real_hash_is_one_this_server_can_read()
    {
        var user = new User { Id = "alice", DisplayName = "Alice", PasswordHash = "" };
        string hash = new PasswordHasher<User>().HashPassword(user, "correct-horse-battery");

        Assert.True(UserRepository.IsPlausiblePasswordHash(hash));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64!!")]
    public void A_hash_that_is_not_base64_is_refused(string? hash)
    {
        Assert.False(UserRepository.IsPlausiblePasswordHash(hash));
    }

    /// <summary>
    /// Base64 alone is not enough — the first byte is the hasher's format marker, and something that
    /// merely decodes would still throw further in.
    /// </summary>
    [Fact]
    public void Base64_of_something_that_is_not_a_hash_is_refused()
    {
        Assert.False(UserRepository.IsPlausiblePasswordHash(Convert.ToBase64String([])));
        Assert.False(UserRepository.IsPlausiblePasswordHash(Convert.ToBase64String([0x01])));
        Assert.False(UserRepository.IsPlausiblePasswordHash(
            Convert.ToBase64String([0x02, .. new byte[48]])));
    }
}
