using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Serval.Server.Auth;

namespace Serval.Server.Tests;

/// <summary>
/// Pins the one refusal that bounds an alert image token.
///
/// <para>That token lives for hours where every other URL-borne credential lives for ten minutes,
/// and the only thing making that affordable is that it opens a single alert's picture. If
/// <c>MediaAccess</c> ever stopped refusing it, nothing would break, no test would fail and no log
/// line would appear — every media route on the deployment would simply start accepting a day-long
/// credential. That is exactly the kind of rule worth a test.</para>
///
/// <para>These evaluate the real policy bodies from <see cref="MediaPolicies"/>, which is why those
/// live in a class rather than inline in <c>Program.cs</c>: a rule stated in a top-level statement
/// is a rule nothing here can reach.</para>
/// </summary>
public class MediaPolicyTests
{
    private const string MediaAccess = "MediaAccess";
    private const string AlertImageAccess = "AlertImageAccess";

    private static IAuthorizationService Authorization()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationBuilder()
            .AddPolicy(MediaAccess, MediaPolicies.ConfigureMediaAccess)
            .AddPolicy(AlertImageAccess, MediaPolicies.ConfigureAlertImageAccess);

        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    /// <summary>What the bearer middleware hands a policy for a token this server minted.</summary>
    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Bearer"));

    private static Claim StreamScope() =>
        new(TokenService.ScopeClaimType, TokenService.StreamScope);

    private static Claim Alert(string id) => new(TokenService.AlertClaimType, id);

    [Fact]
    public async Task An_ordinary_stream_token_opens_both_policies()
    {
        IAuthorizationService authorization = Authorization();
        ClaimsPrincipal user = Principal(StreamScope());

        Assert.True((await authorization.AuthorizeAsync(user, null, MediaAccess)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(user, null, AlertImageAccess)).Succeeded);
    }

    /// <summary>
    /// The refusal itself. An alert image token is a stream token with a claim added, so it passes
    /// authentication and everything else on the policy — this assertion is the only thing standing
    /// between it and every playlist, segment, snapshot and clip on the deployment.
    /// </summary>
    [Fact]
    public async Task An_alert_image_token_is_refused_by_every_ordinary_media_route()
    {
        IAuthorizationService authorization = Authorization();
        ClaimsPrincipal user = Principal(StreamScope(), Alert("alert-1"));

        Assert.False((await authorization.AuthorizeAsync(user, null, MediaAccess)).Succeeded);
    }

    /// <summary>
    /// And it is admitted by the policy that exists for it. Which alert it names is not this
    /// policy's question — the handler compares that, because it can answer 404 and a policy
    /// cannot. See <see cref="TokenService.MayViewAlert"/>.
    /// </summary>
    [Fact]
    public async Task An_alert_image_token_is_admitted_by_the_policy_that_exists_for_it()
    {
        IAuthorizationService authorization = Authorization();
        ClaimsPrincipal user = Principal(StreamScope(), Alert("alert-1"));

        Assert.True((await authorization.AuthorizeAsync(user, null, AlertImageAccess)).Succeeded);
    }

    [Fact]
    public async Task An_unauthenticated_caller_opens_neither()
    {
        IAuthorizationService authorization = Authorization();
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.False((await authorization.AuthorizeAsync(anonymous, null, MediaAccess)).Succeeded);
        Assert.False(
            (await authorization.AuthorizeAsync(anonymous, null, AlertImageAccess)).Succeeded);
    }

    /// <summary>
    /// End to end from the mint: whatever <see cref="TokenService.CreateAlertImageToken"/> puts in a
    /// token is what the policy sees. A test that hand-builds the claims cannot catch the two being
    /// renamed apart.
    /// </summary>
    [Fact]
    public async Task A_freshly_minted_alert_image_token_is_refused_by_MediaAccess()
    {
        TokenService tokens = new(new TestOptionsMonitor<Configuration.ServerOptions>(
            new Configuration.ServerOptions
            {
                Auth = new Configuration.AuthOptions
                {
                    SigningKey = "unit-test-signing-key-at-least-32-characters",
                },
            }));

        (string token, DateTimeOffset _) =
            tokens.CreateAlertImageToken("erin", "alert-1", TimeSpan.FromHours(24));

        JwtSecurityToken parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);
        ClaimsPrincipal user = Principal([.. parsed.Claims]);

        IAuthorizationService authorization = Authorization();

        Assert.False((await authorization.AuthorizeAsync(user, null, MediaAccess)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(user, null, AlertImageAccess)).Succeeded);
        Assert.True(TokenService.MayViewAlert(user, "alert-1"));
        Assert.False(TokenService.MayViewAlert(user, "alert-2"));
    }
}
