using Serval.Server.Auth;

namespace Serval.Server.Tests;

/// <summary>
/// The raw refresh token must never be recoverable from what gets stored — the same "never store
/// the credential" rule <see cref="UserRepository"/> follows for passwords.
/// </summary>
public class RefreshTokenHashTests
{
    [Fact]
    public void The_same_token_hashes_the_same_way()
    {
        string token = TokenService.CreateRefreshTokenValue();
        Assert.Equal(RefreshTokenRepository.Hash(token), RefreshTokenRepository.Hash(token));
    }

    [Fact]
    public void Different_tokens_hash_differently()
    {
        string a = TokenService.CreateRefreshTokenValue();
        string b = TokenService.CreateRefreshTokenValue();
        Assert.NotEqual(RefreshTokenRepository.Hash(a), RefreshTokenRepository.Hash(b));
    }

    [Fact]
    public void The_hash_does_not_contain_the_raw_token()
    {
        string token = TokenService.CreateRefreshTokenValue();
        Assert.DoesNotContain(token, RefreshTokenRepository.Hash(token), StringComparison.Ordinal);
    }
}
