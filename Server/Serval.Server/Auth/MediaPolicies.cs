using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace Serval.Server.Auth;

/// <summary>
/// The two authorization policies that accept a <c>?stream_token=</c>, defined here rather than
/// inline in <c>Program.cs</c> so they can be exercised by a test.
///
/// <para>They exist because a <c>&lt;video&gt;</c>, an <c>&lt;img&gt;</c> and a browser fetching a
/// notification's picture cannot set an <c>Authorization</c> header, so the credential has to travel
/// in the URL. Both list the default bearer scheme as well, so an ordinary authenticated call — the
/// App's own, or curl — keeps working on every route either carries.</para>
///
/// <para>The difference between them is one refusal, and it is the whole security boundary around an
/// alert image token. Getting it wrong is silent: the token would simply work everywhere, which is
/// what a longer-lived credential must never do. <c>MediaPolicyTests</c> is what pins it.</para>
/// </summary>
public static class MediaPolicies
{
    /// <summary>The scheme that reads <c>?stream_token=</c>. A constant because it is now named in
    /// two files — here and where <c>Program.cs</c> registers it — and a typo would not fail a
    /// build, only every URL-borne credential at once.</summary>
    public const string StreamTokenScheme = "StreamToken";

    /// <summary>
    /// <c>"MediaAccess"</c>: every media route a player or the App fetches by URL.
    ///
    /// <para>An alert image token satisfies authentication here — it is a stream token with a claim
    /// added — and would otherwise open all of them. The narrowing has to be a refusal on this
    /// policy rather than a permission on the other: a route put on this policy later must not
    /// silently start accepting one.</para>
    /// </summary>
    public static void ConfigureMediaAccess(AuthorizationPolicyBuilder policy) =>
        policy
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, StreamTokenScheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
                !context.User.HasClaim(c => c.Type == TokenService.AlertClaimType));

    /// <summary>
    /// <c>"AlertImageAccess"</c>: <see cref="ConfigureMediaAccess"/> minus that refusal, for the one
    /// route an alert image token is for.
    ///
    /// <para>It admits, and nothing more. <em>Which</em> alert the token names is compared by the
    /// handler, which already binds the id — see <c>AlertEndpoints</c> and
    /// <see cref="TokenService.MayViewAlert"/>. Deliberately not an assertion reading
    /// <c>AuthorizationHandlerContext.Resource</c>: that is the <c>HttpContext</c> only because of a
    /// .NET 9 default which one runtimeconfig switch reverts, and a policy cannot answer 404, which
    /// is the answer this route owes a token asking about somebody else's alert.</para>
    /// </summary>
    public static void ConfigureAlertImageAccess(AuthorizationPolicyBuilder policy) =>
        policy
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, StreamTokenScheme)
            .RequireAuthenticatedUser();
}
