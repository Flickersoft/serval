using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Serval.Server.Auth;
using Serval.Server.Configuration;

namespace Serval.Server.Tests;

/// <summary>
/// Pins the claim shape <see cref="TokenService"/> issues. Program.cs's two JWT bearer schemes
/// and <c>AuthEndpoints.WsTicketAsync</c>/<c>StreamTokenAsync</c> all depend on reading these back
/// exactly as written — see <see cref="TokenService.GetUserId"/> for why that's not automatic.
/// </summary>
public class TokenServiceTests
{
    private static TokenService CreateService(int accessTokenMinutes = 10) =>
        new(new TestOptionsMonitor<ServerOptions>(new ServerOptions
        {
            Auth = new AuthOptions
            {
                SigningKey = "unit-test-signing-key-at-least-32-characters",
                AccessTokenMinutes = accessTokenMinutes,
            },
        }));

    [Fact]
    public void Access_token_carries_the_user_id_and_role_and_no_scope()
    {
        TokenService tokens = CreateService();
        (string token, DateTimeOffset expiresAt) = tokens.CreateAccessToken("alice", Role.Admin);

        JwtSecurityToken parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Equal("alice", parsed.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal(nameof(Role.Admin), parsed.Claims.Single(c => c.Type == ClaimTypes.Role).Value);
        Assert.DoesNotContain(parsed.Claims, c => c.Type == TokenService.ScopeClaimType);
        Assert.True(expiresAt > DateTimeOffset.UtcNow.AddMinutes(9));
    }

    [Fact]
    public void Stream_token_carries_the_stream_scope_claim()
    {
        TokenService tokens = CreateService();
        (string token, DateTimeOffset _) = tokens.CreateStreamToken("bob", Role.Viewer);

        JwtSecurityToken parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Equal(
            TokenService.StreamScope,
            parsed.Claims.Single(c => c.Type == TokenService.ScopeClaimType).Value);
    }

    [Fact]
    public void GetUserId_and_GetRole_read_back_what_the_bearer_middleware_would_hand_them()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, "carol"),
            new Claim(ClaimTypes.Role, nameof(Role.Viewer)),
        ]));

        Assert.Equal("carol", TokenService.GetUserId(principal));
        Assert.Equal(Role.Viewer, TokenService.GetRole(principal));
    }

    [Fact]
    public void GetRole_defaults_to_Viewer_when_the_claim_is_missing()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.Equal(Role.Viewer, TokenService.GetRole(principal));
    }

    [Fact]
    public void Refresh_token_values_are_unique_and_url_safe()
    {
        string a = TokenService.CreateRefreshTokenValue();
        string b = TokenService.CreateRefreshTokenValue();

        Assert.NotEqual(a, b);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain('=', a);
    }
}
