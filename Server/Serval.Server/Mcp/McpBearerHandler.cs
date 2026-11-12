using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Serval.Server.Auth;

namespace Serval.Server.Mcp;

/// <summary>
/// Authenticates <c>/mcp</c>: an opaque bearer token issued by <see cref="McpOAuthEndpoints"/>,
/// looked up by hash.
///
/// <para><b>Its own scheme, and the only one <c>/mcp</c> accepts.</b> An App session's JWT is
/// refused there — <c>Program.cs</c>'s JWT handler declines to read one on that path — and one of
/// these tokens is refused everywhere else, since it is not a JWT and the default scheme cannot
/// parse it. Each credential works on exactly one surface, so linking an assistant never hands it
/// the App's write routes, and signing in to the App never opens the assistant surface.</para>
///
/// <para><b>The user is re-read on every request.</b> A token outlives nothing about its owner: a
/// deleted account, or one whose role was changed, takes effect on the next call. That costs a
/// lookup per tool call, which at a household's call rate is nothing.</para>
///
/// <para>The challenge is the MCP spec's: a 401 whose <c>WWW-Authenticate</c> names the protected
/// resource metadata (RFC 9728 §5.1). That header is what lets a client that knows only the URL
/// <c>/mcp</c> discover everything else.</para>
/// </summary>
public sealed class McpBearerHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "McpBearer";
    public const string PolicyName = "McpAccess";

    /// <summary>Which client a request came from, for logs and for anything that wants to tell assistants apart.</summary>
    public const string ClientClaimType = "mcp_client";

    private readonly McpGate _gate;
    private readonly McpOAuthStore _store;
    private readonly UserRepository _users;

    public McpBearerHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        McpGate gate,
        McpOAuthStore store,
        UserRepository users)
        : base(options, logger, encoder)
    {
        _gate = gate;
        _store = store;
        _users = users;
    }

    /// <summary>The policy <c>/mcp</c> is mapped with: this scheme, and a signed-in user behind it.</summary>
    public static void ConfigurePolicy(AuthorizationPolicyBuilder policy) =>
        policy.AddAuthenticationSchemes(SchemeName).RequireAuthenticatedUser();

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        if (_gate.Resource is not { } resource)
        {
            return AuthenticateResult.NoResult();
        }

        string raw = header["Bearer ".Length..].Trim();
        McpToken? token = await _store.FindAccessTokenAsync(raw, Context.RequestAborted);

        // The resource check is what stops a token outliving a change of public address: it was
        // issued for one URL and is good only there.
        if (token is null || !string.Equals(token.Resource, resource, StringComparison.Ordinal))
        {
            return AuthenticateResult.Fail("The token is unknown, expired, revoked, or for another address.");
        }

        if (await _users.GetAsync(token.UserId, Context.RequestAborted) is not { } user)
        {
            return AuthenticateResult.Fail("The account that approved this assistant no longer exists.");
        }

        await _store.TouchGrantAsync(token.GrantId, Context.RequestAborted);

        var identity = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
                new Claim(ClientClaimType, token.ClientId),
            ],
            SchemeName);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;

        if (_gate.Issuer is not { } issuer)
        {
            return;
        }

        AuthenticateResult result = await HandleAuthenticateOnceSafeAsync();
        string metadata = issuer + "/.well-known/oauth-protected-resource" + McpGate.McpPath;

        Response.Headers.WWWAuthenticate = result.Failure is null
            ? $"Bearer resource_metadata=\"{metadata}\""
            : $"Bearer resource_metadata=\"{metadata}\", error=\"invalid_token\"";
    }
}

/// <summary>
/// Answers 404 for every MCP route while <see cref="McpGate"/> is closed, before authentication
/// runs — so a switched-off server neither challenges nor advertises.
///
/// <para>Two App routes are exempt, both behind an App session: <c>/api/mcp/status</c>, which is how
/// the App learns the feature is off, and <c>/api/mcp/grants</c>, so somebody can still see and
/// revoke their links while it is off — before switching it back on, say.</para>
/// </summary>
public sealed class McpGateMiddleware(RequestDelegate next, McpGate gate)
{
    public Task InvokeAsync(HttpContext context)
    {
        PathString path = context.Request.Path;

        bool exempt = path.StartsWithSegments("/api/mcp/status") || path.StartsWithSegments("/api/mcp/grants");

        bool mcpRoute = path.StartsWithSegments(McpGate.McpPath)
            || path.StartsWithSegments("/.well-known/oauth-protected-resource")
            || path.StartsWithSegments("/.well-known/oauth-authorization-server")
            || (path.StartsWithSegments("/api/mcp") && !exempt);

        if (mcpRoute && !gate.IsEffective)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        return next(context);
    }
}
