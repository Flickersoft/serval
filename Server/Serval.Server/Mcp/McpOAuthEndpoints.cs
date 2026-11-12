using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using Serval.Server.Auth;
using Serval.Server.GoogleHome;

namespace Serval.Server.Mcp;

/// <summary>
/// Serval as the OAuth 2.1 authorization server for its own MCP endpoint, per the MCP
/// authorization spec: discovery documents, client registration, the authorize and token
/// endpoints, and the two halves only Serval has — the App's consent step and the list of linked
/// assistants.
///
/// <para><b>How a link is made.</b> The client finds this server from the 401 on <c>/mcp</c>,
/// reads the two <c>.well-known</c> documents, identifies itself (a metadata URL, or a
/// registration), and sends the person's browser to <c>/api/mcp/oauth/authorize</c>. That
/// validates everything it can and parks the request, then hands the browser to the App at
/// <c>/connect</c>. The App — where the person is already signed in, or signs in — shows who is
/// asking and calls approve or deny, and the server answers with the URL to send the browser back
/// to. The client then redeems the code here for tokens.</para>
///
/// <para><b>Why consent is in the App, not a page served here.</b> The App already holds the
/// person's session; a page served by this endpoint would need its own sign-in form, which would
/// be a second place passwords are typed and a second thing to rate-limit. Unlike Google Home's
/// linking (see <see cref="GoogleOAuthEndpoints"/>) there must be a consent step at all, because
/// any client can register itself and so the client id gates nothing.</para>
///
/// <para>Every route here 404s while <see cref="McpGate"/> is closed — see <see cref="McpGateMiddleware"/>
/// — except <c>/api/mcp/status</c> and <c>/api/mcp/grants</c>, which the App needs either way.</para>
/// </summary>
public static class McpOAuthEndpoints
{
    /// <summary>How long somebody has to approve a request after the browser reaches the App.</summary>
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Ten minutes, OAuth's figure for a code. It only has to survive the hop from the browser back
    /// to the client and the client's call here.
    /// </summary>
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    private const int MaxRegistrationBytes = 64 * 1024;

    public static void MapMcpOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // RFC 9728. Both spellings: the path-suffixed one is what a client derives from /mcp, and
        // the bare one is where older clients look.
        app.MapGet("/.well-known/oauth-protected-resource", ProtectedResourceMetadata)
            .AllowAnonymous().ExcludeFromDescription().RequireCors(McpRegistration.CorsPolicy);
        app.MapGet("/.well-known/oauth-protected-resource" + McpGate.McpPath, ProtectedResourceMetadata)
            .AllowAnonymous().ExcludeFromDescription().RequireCors(McpRegistration.CorsPolicy);

        // RFC 8414.
        app.MapGet("/.well-known/oauth-authorization-server", AuthorizationServerMetadata)
            .AllowAnonymous().ExcludeFromDescription().RequireCors(McpRegistration.CorsPolicy);

        RouteGroupBuilder group = app.MapGroup("/api/mcp").WithTags("AI assistants");

        group.MapGet("/status", (McpGate gate) => Results.Ok(gate.Status))
            .WithSummary("Whether AI assistants can connect, and the address to give them.")
            .RequireAuthorization();

        group.MapPost("/oauth/register", RegisterAsync)
            .WithSummary("RFC 7591 dynamic client registration, for MCP clients without a metadata URL.")
            .AllowAnonymous()
            .RequireRateLimiting(McpRegistration.RegisterRateLimit)
            .RequireCors(McpRegistration.CorsPolicy);

        group.MapGet("/oauth/authorize", AuthorizeAsync)
            .WithSummary("OAuth authorization endpoint. Validates, then hands the browser to the App to ask.")
            .AllowAnonymous()
            .RequireRateLimiting(McpRegistration.AuthorizeRateLimit);

        group.MapPost("/oauth/token", TokenAsync)
            .WithSummary("OAuth token endpoint: authorization_code (with PKCE) and refresh_token grants.")
            .AllowAnonymous()
            .RequireRateLimiting(McpRegistration.TokenRateLimit)
            .RequireCors(McpRegistration.CorsPolicy);

        group.MapGet("/oauth/requests/{id}", GetRequestAsync)
            .WithSummary("The pending authorization request the App's consent screen is showing.")
            .RequireAuthorization();

        group.MapPost("/oauth/requests/{id}/approve", ApproveAsync)
            .WithSummary("Approves a pending request as the signed-in user; answers the URL to send the browser to.")
            .RequireAuthorization();

        group.MapPost("/oauth/requests/{id}/deny", DenyAsync)
            .WithSummary("Denies a pending request; answers the URL to send the browser to.")
            .RequireAuthorization();

        group.MapGet("/grants", ListGrantsAsync)
            .WithSummary("The assistants the signed-in user has linked.")
            .RequireAuthorization();

        group.MapDelete("/grants/{id}", RevokeGrantAsync)
            .WithSummary("Unlinks an assistant, ending its access immediately. Your own, or anyone's as Admin.")
            .RequireAuthorization();
    }

    // ------------------------------------------------------------------ discovery

    private static IResult ProtectedResourceMetadata(McpGate gate) =>
        gate.Issuer is not { } issuer
            ? Results.NotFound()
            : Results.Ok(new ProtectedResourceDocument(
                Resource: gate.Resource!,
                AuthorizationServers: [issuer],
                BearerMethodsSupported: ["header"],
                ResourceName: "Serval"));

    private static IResult AuthorizationServerMetadata(McpGate gate) =>
        gate.Issuer is not { } issuer
            ? Results.NotFound()
            : Results.Ok(new AuthorizationServerDocument(
                Issuer: issuer,
                AuthorizationEndpoint: issuer + "/api/mcp/oauth/authorize",
                TokenEndpoint: issuer + "/api/mcp/oauth/token",
                RegistrationEndpoint: issuer + "/api/mcp/oauth/register",
                ResponseTypesSupported: ["code"],
                GrantTypesSupported: ["authorization_code", "refresh_token"],
                CodeChallengeMethodsSupported: ["S256"],
                TokenEndpointAuthMethodsSupported: ["none"],
                ClientIdMetadataDocumentSupported: true,
                AuthorizationResponseIssParameterSupported: true));

    // --------------------------------------------------------------- registration

    private static async Task<IResult> RegisterAsync(
        HttpContext http, McpOAuthStore store, ILoggerFactory loggers, CancellationToken ct)
    {
        ILogger logger = loggers.CreateLogger("Serval.Server.Mcp.OAuth");

        // Read by hand rather than bound, so a malformed body gets RFC 7591's error shape rather
        // than the framework's, and so its size is capped before it is parsed — counted as it is
        // read, since a chunked body declares no length to check up front.
        byte[]? body = await McpClientResolver.ReadCappedAsync(http.Request.Body, MaxRegistrationBytes, ct);
        if (body is null)
        {
            return RegistrationError("invalid_client_metadata", "The registration is too large.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return RegistrationError("invalid_client_metadata", "The body is not JSON.");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return RegistrationError("invalid_client_metadata", "The body is not a JSON object.");
            }

            List<string>? redirects = McpClientResolver.ReadRedirectUris(root);
            if (redirects is null)
            {
                return RegistrationError(
                    "invalid_redirect_uri",
                    "redirect_uris must list one to twenty https, loopback http, or private-scheme URIs.");
            }

            string? name = root.TryGetProperty("client_name", out JsonElement n) && n.ValueKind == JsonValueKind.String
                ? McpClientResolver.CleanName(n.GetString())
                : null;

            McpRegisteredClient client = await store.RegisterClientAsync(name, redirects, ct);
            logger.LogInformation("MCP client registered: {Name} ({ClientId}).", name ?? "(unnamed)", client.ClientId);

            // Whatever was asked for, a public client using PKCE is what is granted; RFC 7591 lets
            // the server substitute values and says so by returning them.
            return Results.Json(
                new RegistrationResponse(
                    ClientId: client.ClientId,
                    ClientIdIssuedAt: client.CreatedAt.ToUnixTimeSeconds(),
                    ClientName: name,
                    RedirectUris: redirects,
                    TokenEndpointAuthMethod: "none",
                    GrantTypes: ["authorization_code", "refresh_token"],
                    ResponseTypes: ["code"]),
                statusCode: StatusCodes.Status201Created);
        }
    }

    // ------------------------------------------------------------------ authorize

    private static async Task<IResult> AuthorizeAsync(
        HttpContext http,
        McpGate gate,
        McpOAuthStore store,
        McpClientResolver resolver,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        if (gate.Issuer is not { } issuer || gate.Resource is not { } resource)
        {
            return Results.NotFound();
        }

        ILogger logger = loggers.CreateLogger("Serval.Server.Mcp.OAuth");
        IQueryCollection query = http.Request.Query;
        string clientId = query["client_id"].ToString();

        // Until the client and its redirect are both known good, nothing may redirect: sending a
        // browser — or an error — to an unverified address is the open-redirect bug. These two
        // answer the person in the browser directly instead.
        McpClient? client = await resolver.ResolveAsync(clientId, ct);
        if (client is null)
        {
            logger.LogWarning("MCP authorization refused: client_id {ClientId} is not a usable client.", clientId);
            return BrowserError("The app asking to connect could not be identified. Try connecting again from the app.");
        }

        string? redirectUri = McpOAuthRules.MatchRedirectUri(query["redirect_uri"].ToString(), client.RedirectUris);
        if (redirectUri is null)
        {
            logger.LogWarning(
                "MCP authorization refused: redirect_uri {RedirectUri} is not one {ClientId} registered.",
                query["redirect_uri"].ToString(), clientId);
            return BrowserError("The app asking to connect gave an address to return to that it never registered.");
        }

        string? state = query["state"].ToString() is { Length: > 0 } s ? s : null;

        if (query["response_type"] != "code")
        {
            return Results.Redirect(McpOAuthRules.BuildRedirect(redirectUri, issuer, state, error: "unsupported_response_type"));
        }

        string challenge = query["code_challenge"].ToString();
        if (query["code_challenge_method"] != "S256" || !McpOAuthRules.IsValidChallenge(challenge))
        {
            return Results.Redirect(McpOAuthRules.BuildRedirect(redirectUri, issuer, state, error: "invalid_request"));
        }

        // RFC 8707. Clients are required to send it; one that does not is bound to the only
        // resource there is, and one that names another is refused rather than issued a token
        // somebody else's server would accept.
        string requested = query["resource"].ToString();
        if (requested.Length > 0 && !string.Equals(requested.TrimEnd('/'), resource, StringComparison.Ordinal))
        {
            return Results.Redirect(McpOAuthRules.BuildRedirect(redirectUri, issuer, state, error: "invalid_target"));
        }

        var pending = new McpAuthorizationRequest
        {
            Id = GoogleOAuthStore.NewSecret(),
            ClientId = client.ClientId,
            ClientName = client.Name,
            Verified = client.Verified,
            RedirectUri = redirectUri,
            State = state,
            CodeChallenge = challenge,
            Resource = resource,
            ExpiresAt = DateTimeOffset.UtcNow + RequestLifetime,
        };

        await store.CreateRequestAsync(pending, ct);

        // Relative, so the browser stays on whichever origin it reached this endpoint on — which,
        // being the issuer's authorize URL, is the public one.
        return Results.Redirect($"/connect?request={Uri.EscapeDataString(pending.Id)}");
    }

    // ---------------------------------------------------------------------- token

    private static async Task<IResult> TokenAsync(
        HttpContext http,
        McpGate gate,
        McpOAuthStore store,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        if (gate.Resource is not { } resource)
        {
            return Results.NotFound();
        }

        ILogger logger = loggers.CreateLogger("Serval.Server.Mcp.OAuth");

        if (!http.Request.HasFormContentType)
        {
            return TokenError("invalid_request");
        }

        IFormCollection form = await http.Request.ReadFormAsync(ct);
        string grant = form["grant_type"].ToString();

        IResult result = grant switch
        {
            "authorization_code" => await ExchangeCodeAsync(form, gate, store, resource, logger, ct),
            "refresh_token" => await RefreshAsync(form, gate, store, resource, logger, ct),
            _ => TokenError("unsupported_grant_type"),
        };

        // A token response must never be cached by anything between here and the client.
        http.Response.Headers.CacheControl = "no-store";
        return result;
    }

    private static async Task<IResult> ExchangeCodeAsync(
        IFormCollection form, McpGate gate, McpOAuthStore store, string resource, ILogger logger, CancellationToken ct)
    {
        string raw = form["code"].ToString();
        if (raw.Length == 0)
        {
            return TokenError("invalid_request");
        }

        // Consumed before anything else is checked, so a code that fails PKCE is burnt rather than
        // left for another attempt.
        McpAuthorizationCode? code = await store.ConsumeCodeAsync(raw, ct);
        if (code is null)
        {
            return TokenError("invalid_grant");
        }

        string redirect = form["redirect_uri"].ToString();
        string requested = form["resource"].ToString();

        bool valid = string.Equals(form["client_id"].ToString(), code.ClientId, StringComparison.Ordinal)
            && (redirect.Length == 0 || string.Equals(redirect, code.RedirectUri, StringComparison.Ordinal))
            && (requested.Length == 0 || string.Equals(requested.TrimEnd('/'), code.Resource, StringComparison.Ordinal))
            && string.Equals(code.Resource, resource, StringComparison.Ordinal)
            && McpOAuthRules.VerifyPkce(form["code_verifier"].ToString(), code.CodeChallenge);

        if (!valid)
        {
            logger.LogWarning(
                "MCP code exchange refused for client {ClientId}: client, redirect, resource or PKCE "
                + "verifier did not match what was approved.", code.ClientId);
            return TokenError("invalid_grant");
        }

        McpGrant grant = await store.EnsureGrantAsync(code, ct);
        IResult issued = await IssueAsync(grant, gate, store, resource, ct);

        logger.LogInformation(
            "MCP client {Name} ({ClientId}) linked for user {UserId}.",
            grant.ClientName ?? "(unnamed)", grant.ClientId, grant.UserId);
        return issued;
    }

    private static async Task<IResult> RefreshAsync(
        IFormCollection form, McpGate gate, McpOAuthStore store, string resource, ILogger logger, CancellationToken ct)
    {
        string raw = form["refresh_token"].ToString();
        if (raw.Length == 0)
        {
            return TokenError("invalid_request");
        }

        // RFC 8707 §2.2: a resource named on refresh must be the one the grant is bound to, which
        // is the current /mcp URL (a token for any other is refused below). Checked before the
        // token is spent: a refusal here must not burn it, or the client's next, correct refresh
        // would read as a replay and revoke the whole link.
        string requested = form["resource"].ToString();
        if (requested.Length > 0 && !string.Equals(requested.TrimEnd('/'), resource, StringComparison.Ordinal))
        {
            return TokenError("invalid_target");
        }

        if (await store.SpendRefreshTokenAsync(raw, ct) is not { } spent)
        {
            return TokenError("invalid_grant");
        }

        (McpToken token, bool reused) = spent;
        if (reused)
        {
            logger.LogWarning(
                "MCP refresh token reused for client {ClientId}, user {UserId}: revoking the link, "
                + "since two parties hold it and there is no telling which is genuine.",
                token.ClientId, token.UserId);
            await store.RevokeGrantAsync(token.GrantId, ct);
            return TokenError("invalid_grant");
        }

        string clientId = form["client_id"].ToString();
        if ((clientId.Length > 0 && !string.Equals(clientId, token.ClientId, StringComparison.Ordinal))
            || !string.Equals(token.Resource, resource, StringComparison.Ordinal)
            || await store.GetGrantAsync(token.GrantId, ct) is not { } grant)
        {
            return TokenError("invalid_grant");
        }

        return await IssueAsync(grant, gate, store, resource, ct);
    }

    private static async Task<IResult> IssueAsync(
        McpGrant grant, McpGate gate, McpOAuthStore store, string resource, CancellationToken ct)
    {
        var access = TimeSpan.FromMinutes(Math.Clamp(gate.Options.AccessTokenMinutes, 5, 24 * 60));
        var refresh = TimeSpan.FromDays(Math.Clamp(gate.Options.RefreshTokenDays, 1, 365));

        (string accessToken, string refreshToken) = await store.IssueTokenPairAsync(grant, resource, access, refresh, ct);

        return Results.Ok(new TokenResponse(
            AccessToken: accessToken,
            TokenType: "Bearer",
            ExpiresIn: (int)access.TotalSeconds,
            RefreshToken: refreshToken));
    }

    // ------------------------------------------------------------ App: consent

    private static async Task<IResult> GetRequestAsync(string id, McpOAuthStore store, CancellationToken ct) =>
        await store.GetOpenRequestAsync(id, ct) is { } request
            ? Results.Ok(new PendingRequestView(
                ClientName: request.ClientName,
                VerifiedHost: McpClient.VerifiedHostOf(request.ClientId, request.Verified),
                RedirectTarget: McpOAuthRules.DescribeRedirect(request.RedirectUri),
                Verified: request.Verified,
                ExpiresAt: request.ExpiresAt))
            : Results.NotFound();

    private static async Task<IResult> ApproveAsync(
        string id, HttpContext http, McpGate gate, McpOAuthStore store, ILoggerFactory loggers, CancellationToken ct)
    {
        if (gate.Issuer is not { } issuer)
        {
            return Results.NotFound();
        }

        McpAuthorizationRequest? request = await store.CompleteRequestAsync(id, ct);
        if (request is null)
        {
            return Results.NotFound();
        }

        string userId = TokenService.RequireUserId(http.User);
        string code = await store.IssueCodeAsync(request, userId, CodeLifetime, ct);

        loggers.CreateLogger("Serval.Server.Mcp.OAuth").LogInformation(
            "User {UserId} approved MCP client {Name} ({ClientId}).",
            userId, request.ClientName ?? "(unnamed)", request.ClientId);

        return Results.Ok(new ConsentAnswer(McpOAuthRules.BuildRedirect(request.RedirectUri, issuer, request.State, code: code)));
    }

    private static async Task<IResult> DenyAsync(string id, McpGate gate, McpOAuthStore store, CancellationToken ct)
    {
        if (gate.Issuer is not { } issuer)
        {
            return Results.NotFound();
        }

        McpAuthorizationRequest? request = await store.CompleteRequestAsync(id, ct);
        return request is null
            ? Results.NotFound()
            : Results.Ok(new ConsentAnswer(
                McpOAuthRules.BuildRedirect(request.RedirectUri, issuer, request.State, error: "access_denied")));
    }

    // ------------------------------------------------------------- App: grants

    private static async Task<IResult> ListGrantsAsync(
        HttpContext http, McpGate gate, McpOAuthStore store, CancellationToken ct)
    {
        List<McpGrant> grants = await store.ListGrantsAsync(TokenService.RequireUserId(http.User), gate.Resource, ct);
        return Results.Ok(grants.Select(g => new GrantView(
            Id: g.Id.ToString(),
            ClientName: g.ClientName,
            VerifiedHost: McpClient.VerifiedHostOf(g.ClientId, g.Verified),
            Verified: g.Verified,
            CreatedAt: g.CreatedAt,
            LastUsedAt: g.LastUsedAt)));
    }

    private static async Task<IResult> RevokeGrantAsync(
        string id, HttpContext http, McpOAuthStore store, ILoggerFactory loggers, CancellationToken ct)
    {
        if (!ObjectId.TryParse(id, out ObjectId grantId) || await store.GetGrantAsync(grantId, ct) is not { } grant)
        {
            return Results.NotFound();
        }

        string userId = TokenService.RequireUserId(http.User);

        // Not found rather than forbidden for somebody else's grant, so a Viewer cannot learn which
        // ids exist.
        if (grant.UserId != userId && TokenService.GetRole(http.User) != Role.Admin)
        {
            return Results.NotFound();
        }

        await store.RevokeGrantAsync(grantId, ct);

        loggers.CreateLogger("Serval.Server.Mcp.OAuth").LogInformation(
            "User {UserId} unlinked MCP client {Name} ({ClientId}) belonging to {Owner}.",
            userId, grant.ClientName ?? "(unnamed)", grant.ClientId, grant.UserId);

        return Results.NoContent();
    }

    // -------------------------------------------------------------------- helpers

    private static IResult TokenError(string error, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new { error }, statusCode: status);

    private static IResult RegistrationError(string error, string description) =>
        Results.Json(new RegistrationErrorResponse(error, description), statusCode: StatusCodes.Status400BadRequest);

    /// <summary>A refusal the person in the browser reads, since there is nowhere safe to send them.</summary>
    private static IResult BrowserError(string message) =>
        Results.Text($"Serval could not start connecting this assistant. {message}\n", "text/plain", statusCode: 400);

    // ------------------------------------------------------------------ wire types
    //
    // The OAuth documents name every field explicitly: RFC 6749, 7591, 8414 and 9728 are
    // snake_case, and this server's default serializer is camelCase. The App-facing views below
    // them use the default, like every other App route.

    private sealed record ProtectedResourceDocument(
        [property: JsonPropertyName("resource")] string Resource,
        [property: JsonPropertyName("authorization_servers")] IReadOnlyList<string> AuthorizationServers,
        [property: JsonPropertyName("bearer_methods_supported")] IReadOnlyList<string> BearerMethodsSupported,
        [property: JsonPropertyName("resource_name")] string ResourceName);

    private sealed record AuthorizationServerDocument(
        [property: JsonPropertyName("issuer")] string Issuer,
        [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
        [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
        [property: JsonPropertyName("registration_endpoint")] string RegistrationEndpoint,
        [property: JsonPropertyName("response_types_supported")] IReadOnlyList<string> ResponseTypesSupported,
        [property: JsonPropertyName("grant_types_supported")] IReadOnlyList<string> GrantTypesSupported,
        [property: JsonPropertyName("code_challenge_methods_supported")] IReadOnlyList<string> CodeChallengeMethodsSupported,
        [property: JsonPropertyName("token_endpoint_auth_methods_supported")] IReadOnlyList<string> TokenEndpointAuthMethodsSupported,
        [property: JsonPropertyName("client_id_metadata_document_supported")] bool ClientIdMetadataDocumentSupported,
        [property: JsonPropertyName("authorization_response_iss_parameter_supported")] bool AuthorizationResponseIssParameterSupported);

    private sealed record RegistrationResponse(
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("client_id_issued_at")] long ClientIdIssuedAt,
        [property: JsonPropertyName("client_name")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClientName,
        [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string> RedirectUris,
        [property: JsonPropertyName("token_endpoint_auth_method")] string TokenEndpointAuthMethod,
        [property: JsonPropertyName("grant_types")] IReadOnlyList<string> GrantTypes,
        [property: JsonPropertyName("response_types")] IReadOnlyList<string> ResponseTypes);

    private sealed record RegistrationErrorResponse(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("error_description")] string ErrorDescription);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("refresh_token")] string RefreshToken);

    /// <summary>What the consent screen shows. Never the client id itself, which for a registration is meaningless.</summary>
    public sealed record PendingRequestView(
        string? ClientName, string? VerifiedHost, string RedirectTarget, bool Verified, DateTimeOffset ExpiresAt);

    /// <summary>Where the App should send the browser next.</summary>
    public sealed record ConsentAnswer(string RedirectUrl);

    public sealed record GrantView(
        string Id, string? ClientName, string? VerifiedHost, bool Verified, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);
}
