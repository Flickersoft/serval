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

        // A plain stream token growing an alert claim would refuse every media route at once, since
        // MediaAccess rejects the claim outright rather than inspecting it.
        Assert.DoesNotContain(parsed.Claims, c => c.Type == TokenService.AlertClaimType);
    }

    /// <summary>
    /// The stream scope is the load-bearing half: it is what keeps an alert image token on the
    /// "StreamToken" scheme rather than being refused there and accepted as a full session by the
    /// default one. See the note on <see cref="TokenService.CreateAlertImageToken"/>.
    /// </summary>
    [Fact]
    public void Alert_image_token_carries_the_stream_scope_and_the_alert_it_is_for()
    {
        TokenService tokens = CreateService();
        (string token, DateTimeOffset expiresAt) =
            tokens.CreateAlertImageToken("dave", "alert-1", TimeSpan.FromHours(24));

        JwtSecurityToken parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Equal("dave", parsed.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Sub).Value);
        Assert.Equal(nameof(Role.Viewer), parsed.Claims.Single(c => c.Type == ClaimTypes.Role).Value);
        Assert.Equal(
            TokenService.StreamScope,
            parsed.Claims.Single(c => c.Type == TokenService.ScopeClaimType).Value);
        Assert.Equal(
            "alert-1", parsed.Claims.Single(c => c.Type == TokenService.AlertClaimType).Value);

        // Its own lifetime, not the account's ten minutes — the whole point of the thing.
        Assert.True(expiresAt > DateTimeOffset.UtcNow.AddHours(23));
    }

    [Fact]
    public void MayViewAlert_admits_a_credential_that_names_no_alert()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(TokenService.ScopeClaimType, TokenService.StreamScope)]));

        Assert.True(TokenService.MayViewAlert(principal, "alert-1"));
    }

    [Fact]
    public void MayViewAlert_admits_the_alert_the_token_was_minted_for_and_no_other()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(TokenService.AlertClaimType, "alert-1")]));

        Assert.True(TokenService.MayViewAlert(principal, "alert-1"));
        Assert.False(TokenService.MayViewAlert(principal, "alert-2"));
    }

    /// <summary>
    /// A policy listing two schemes authenticates both and merges the identities, so one request can
    /// hold an unrestricted credential and an alert token at once. Every claim has to agree, or the
    /// restriction is one extra header away from meaning nothing.
    /// </summary>
    [Fact]
    public void MayViewAlert_refuses_when_any_identity_names_a_different_alert()
    {
        var principal = new ClaimsPrincipal(
        [
            new ClaimsIdentity([new Claim(TokenService.ScopeClaimType, TokenService.StreamScope)]),
            new ClaimsIdentity([new Claim(TokenService.AlertClaimType, "alert-2")]),
        ]);

        Assert.False(TokenService.MayViewAlert(principal, "alert-1"));
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
