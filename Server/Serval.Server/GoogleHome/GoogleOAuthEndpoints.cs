using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Serval.Server.Configuration;

namespace Serval.Server.GoogleHome;

/// <summary>
/// Serval as an OAuth2 <b>provider</b>, with Google as the client. Two routes, both public,
/// because Google's servers are the callers and have no Serval session to present.
///
/// <para><b>Why there is no consent screen.</b> The obvious shape — bounce into the App so a
/// signed-in user approves the link — was considered and rejected, on three grounds. The link
/// identifies nothing: Serval has no per-user camera scoping, so every signed-in account already
/// sees every camera and "which user linked" carries no information. The security it appears to add
/// already exists elsewhere: an authorization code is worthless without the client secret, which
/// Google presents in a form body at the token endpoint and which never appears in a URL. And the
/// authorize request arrives as a browser navigation from Google, while a Serval session is a JWT
/// in localStorage rather than a cookie — so honouring one would mean building an SPA round trip to
/// answer a question with no answer worth having.
///
/// <para><b>Revisit all of that if Serval ever gains per-user camera scoping.</b> The reasoning
/// rests entirely on every account seeing every camera.</para></para>
///
/// <para><b>What stands in the consent screen's place is a high-entropy client id.</b> The
/// authorization endpoint is public by protocol design and carries no other credential, so a second
/// factor has to live somewhere in the URL or not exist at all. Folding it into
/// <c>client_id</c> — generated, not assigned — buys the same protection as a separate link token
/// in the path while keeping a secret out of the one URL a human copies into Google's console. That
/// is the leak path that matters for an open-source project: people paste their configuration into
/// issues and screenshots. What remains exposed is the query string in a reverse-proxy log and in
/// the Home app's own webview history, which is the tradeoff <c>stream_token</c> already makes and
/// is taken here once per link rather than dozens of times per playback.</para>
/// </summary>
public static class GoogleOAuthEndpoints
{
    /// <summary>
    /// Ten minutes, the figure OAuth2 recommends for an authorization code. It exists only to
    /// survive the round trip from Google's redirect back to Google's token call.
    /// </summary>
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    public static void MapGoogleOAuthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/oauth/authorize", async (
            HttpContext http,
            GoogleHomeGate gate,
            GoogleOAuthStore store,
            IOptionsMonitor<ServerOptions> options,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (!gate.IsEffective)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            ILogger logger = loggers.CreateLogger("Serval.Server.GoogleHome.OAuth");
            GoogleHomeOptions google = options.CurrentValue.GoogleHome;
            IQueryCollection query = http.Request.Query;

            string clientId = query["client_id"].ToString();
            string redirectUri = query["redirect_uri"].ToString();
            string state = query["state"].ToString();
            string responseType = query["response_type"].ToString();

            // Both of these are refused *without* redirecting, and that is the whole point of
            // doing them first. Redirecting to a URI we have not verified is the classic open
            // redirect, and echoing an error to an unverified destination is the same bug wearing
            // a different hat. Everything after this block may redirect, because by then the
            // destination is known to be Google's.
            if (!IsAllowedRedirect(redirectUri, google.ProjectId))
            {
                logger.LogWarning(
                    "Google Home account linking refused: redirect_uri is not one of this "
                    + "project's. Check Serval:GoogleHome:ProjectId matches the Google Home "
                    + "Developer Console project.");
                return Results.BadRequest(new { error = "invalid_request" });
            }

            if (!FixedTimeEquals(clientId, google.ClientId))
            {
                // Deliberately terse to the caller and specific to the log. The operator debugging
                // their own setup reads the log; a stranger probing the endpoint learns only that
                // it refused.
                logger.LogWarning(
                    "Google Home account linking refused: client_id does not match "
                    + "Serval:GoogleHome:ClientId. Paste the same value into the Google console.");
                return Results.BadRequest(new { error = "invalid_client" });
            }

            if (!string.Equals(responseType, "code", StringComparison.Ordinal))
            {
                return Redirect(redirectUri, state, error: "unsupported_response_type");
            }

            if (string.IsNullOrEmpty(state))
            {
                return Redirect(redirectUri, state, error: "invalid_request");
            }

            // No screen, no login: the client id above was the gate. The link is created on first
            // use and its agent user id then stays put, so re-authorizing does not orphan
            // HomeGraph's view of it.
            string agentUserId = await store.EnsureLinkAsync(ct);
            string code = await store.IssueCodeAsync(agentUserId, redirectUri, CodeLifetime, ct);

            logger.LogInformation("Google Home account linking authorized for agent {AgentUserId}.", agentUserId);
            return Redirect(redirectUri, state, code: code);
        })
            .WithSummary("OAuth2 authorization endpoint — where Google starts account linking.")
            .WithDescription(
                "Called by the Google Home app as a browser navigation. Validates the redirect "
                + "URI against Serval:GoogleHome:ProjectId and the client id against "
                + "Serval:GoogleHome:ClientId, then redirects straight back to Google with an "
                + "authorization code.\n\n"
                + "There is no consent screen, and no Serval sign-in: one deployment links to one "
                + "Google account, and every Serval account already sees every camera, so there is "
                + "nothing for a consent step to decide. The client id is the gate.")
            .AllowAnonymous();

        group.MapPost("/oauth/token", async (
            HttpContext http,
            GoogleHomeGate gate,
            GoogleOAuthStore store,
            IOptionsMonitor<ServerOptions> options,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (!gate.IsEffective)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            ILogger logger = loggers.CreateLogger("Serval.Server.GoogleHome.OAuth");

            if (!http.Request.HasFormContentType)
            {
                logger.LogWarning(
                    "Google Home token request refused: the body was not form-encoded.");
                return TokenError("invalid_request");
            }

            GoogleHomeOptions google = options.CurrentValue.GoogleHome;
            IFormCollection form = await http.Request.ReadFormAsync(ct);

            // Credentials arrive one of two ways, and which one is a checkbox in Google's console
            // ("transmit Client ID and secret via HTTP basic auth header"). Reading only the form
            // body — the unchecked default — meant ticking that box silently turned a working
            // integration into a 401 with nothing to explain it. Both are accepted so the switch
            // cannot break anything.
            (string clientId, string clientSecret) = ReadClientCredentials(http, form);

            // Fixed-time, both halves, before anything is looked up. The secret is the credential
            // that actually protects this endpoint — it is the one value here that never travels
            // in a URL — so a wrong one must not be distinguishable from a wrong grant.
            if (!FixedTimeEquals(clientId, google.ClientId)
                || !FixedTimeEquals(clientSecret, google.ClientSecret))
            {
                logger.LogWarning(
                    "Google Home token request refused: client credentials did not match "
                    + "(presented via {Source}). Check the client id and secret in the console's "
                    + "Account linking section.",
                    http.Request.Headers.ContainsKey("Authorization") ? "basic auth" : "the form body");
                return TokenError("invalid_client", StatusCodes.Status401Unauthorized);
            }

            var lifetime = TimeSpan.FromMinutes(Math.Clamp(google.AccessTokenMinutes, 1, 24 * 60));
            string grant = form["grant_type"].ToString();

            IResult result = grant switch
            {
                "authorization_code" => await ExchangeCodeAsync(form, store, lifetime, ct),
                "refresh_token" => await RefreshAsync(form, store, lifetime, ct),
                _ => TokenError("unsupported_grant_type"),
            };

            // The outcome, never the credential. This is the line whose absence meant the only way
            // to tell whether Google had exchanged a code was to read the token collection.
            logger.LogInformation(
                "Google Home token request: grant={Grant} outcome={Outcome}",
                string.IsNullOrEmpty(grant) ? "(none)" : grant,
                result is IStatusCodeHttpResult { StatusCode: >= 400 } ? "refused" : "issued");

            return result;
        })
            .WithSummary("OAuth2 token endpoint — where Google exchanges a code, and refreshes.")
            .WithDescription(
                "Called server-to-server by Google with the client id and secret in the form body. "
                + "Supports the authorization_code and refresh_token grants.\n\n"
                + "The refresh grant deliberately answers without a new refresh_token: Google's "
                + "contract keeps the original valid forever, so rotating it would leave Google "
                + "holding a dead credential and the cameras silently gone.")
            .AllowAnonymous();
    }

    private static async Task<IResult> ExchangeCodeAsync(
        IFormCollection form, GoogleOAuthStore store, TimeSpan lifetime, CancellationToken ct)
    {
        string code = form["code"].ToString();
        if (string.IsNullOrEmpty(code))
        {
            return TokenError("invalid_request");
        }

        // Single-use is the store's atomic update, not a check here — a replay loses the race.
        GoogleAuthorizationCode? consumed = await store.ConsumeCodeAsync(code, ct);
        if (consumed is null)
        {
            return TokenError("invalid_grant");
        }

        // Bound at issue, compared here. A code lifted from a log cannot be redeemed against a
        // destination other than the one it was minted for.
        string redirectUri = form["redirect_uri"].ToString();
        if (!string.IsNullOrEmpty(redirectUri)
            && !string.Equals(redirectUri, consumed.RedirectUri, StringComparison.Ordinal))
        {
            return TokenError("invalid_grant");
        }

        // A fresh grant supersedes the previous one. Without this a re-link would leave the old
        // access and refresh tokens live, so re-linking would never actually take anything away.
        await store.RevokeTokensAsync(consumed.AgentUserId, ct);

        string access = await store.IssueTokenAsync(
            GoogleTokenKind.Access, consumed.AgentUserId, DateTimeOffset.UtcNow + lifetime, ct);
        string refresh = await store.IssueTokenAsync(
            GoogleTokenKind.Refresh, consumed.AgentUserId, expiresAt: null, ct);

        return Results.Ok(new GoogleTokenResponse(
            TokenType: "Bearer",
            AccessToken: access,
            RefreshToken: refresh,
            ExpiresIn: (int)lifetime.TotalSeconds));
    }

    private static async Task<IResult> RefreshAsync(
        IFormCollection form, GoogleOAuthStore store, TimeSpan lifetime, CancellationToken ct)
    {
        string refresh = form["refresh_token"].ToString();
        if (string.IsNullOrEmpty(refresh))
        {
            return TokenError("invalid_request");
        }

        GoogleToken? token = await store.FindTokenAsync(refresh, GoogleTokenKind.Refresh, ct);
        if (token is null)
        {
            return TokenError("invalid_grant");
        }

        string access = await store.IssueTokenAsync(
            GoogleTokenKind.Access, token.AgentUserId, DateTimeOffset.UtcNow + lifetime, ct);

        // No RefreshToken in the response, and the stored one is left alone. See the class remarks.
        return Results.Ok(new GoogleTokenResponse(
            TokenType: "Bearer",
            AccessToken: access,
            RefreshToken: null,
            ExpiresIn: (int)lifetime.TotalSeconds));
    }

    /// <summary>
    /// The client id and secret, from wherever Google put them.
    ///
    /// <para>RFC 6749 allows both, and Google's console has a checkbox choosing between them. The
    /// header form is <c>Authorization: Basic base64(client_id:client_secret)</c>, with both halves
    /// form-urlencoded before being joined — a detail worth honouring, since a secret containing
    /// <c>+</c> or <c>/</c> is exactly what <c>openssl rand -base64</c> produces.</para>
    /// </summary>
    internal static (string ClientId, string ClientSecret) ReadClientCredentials(
        HttpContext http, IFormCollection form)
    {
        string? header = http.Request.Headers.Authorization.ToString();

        if (!string.IsNullOrEmpty(header)
            && header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                string decoded = Encoding.UTF8.GetString(
                    Convert.FromBase64String(header["Basic ".Length..].Trim()));

                int split = decoded.IndexOf(':', StringComparison.Ordinal);
                if (split > 0)
                {
                    return (
                        Uri.UnescapeDataString(decoded[..split]),
                        Uri.UnescapeDataString(decoded[(split + 1)..]));
                }
            }
            catch (FormatException)
            {
                // Not valid base64. Fall through to the form body rather than throwing: the caller
                // is refused either way, and a 500 here would be reported to Google as an outage.
            }
        }

        return (form["client_id"].ToString(), form["client_secret"].ToString());
    }

    /// <summary>
    /// The only two destinations account linking will send a code to, both derived from the
    /// project id. Google publishes exactly these; anything else is either a misconfiguration or
    /// somebody trying to have this server hand them a credential.
    /// </summary>
    internal static bool IsAllowedRedirect(string redirectUri, string projectId)
    {
        if (string.IsNullOrWhiteSpace(redirectUri) || string.IsNullOrWhiteSpace(projectId))
        {
            return false;
        }

        return redirectUri == $"https://oauth-redirect.googleusercontent.com/r/{projectId}"
            || redirectUri == $"https://oauth-redirect-sandbox.googleusercontent.com/r/{projectId}";
    }

    /// <summary>
    /// Fixed-time compare, so a wrong value cannot be narrowed a byte at a time. Lengths are
    /// checked first because they are not secret and <c>FixedTimeEquals</c> needs equal spans —
    /// the same shape <c>TelemetryEndpoints.IsAuthorized</c> uses for the API key. An empty
    /// expected value never matches, which is what makes an unset secret close the route rather
    /// than open it.
    /// </summary>
    internal static bool FixedTimeEquals(string provided, string expected)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided))
        {
            return false;
        }

        byte[] a = Encoding.UTF8.GetBytes(provided);
        byte[] b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// Back to Google, carrying either the code or an error, and always the state it sent — which
    /// is its CSRF bookkeeping and is meaningless to us beyond being returned unchanged.
    /// </summary>
    private static IResult Redirect(
        string redirectUri, string state, string? code = null, string? error = null)
    {
        var url = new StringBuilder(redirectUri);
        url.Append(redirectUri.Contains('?', StringComparison.Ordinal) ? '&' : '?');

        url.Append(code is not null ? "code=" : "error=");
        url.Append(Uri.EscapeDataString(code ?? error!));

        if (!string.IsNullOrEmpty(state))
        {
            url.Append("&state=").Append(Uri.EscapeDataString(state));
        }

        return Results.Redirect(url.ToString());
    }

    /// <summary>
    /// RFC 6749's error shape. Google reads the <c>error</c> field and reports it in the console's
    /// account-linking test, which is where an operator will actually see it.
    /// </summary>
    private static IResult TokenError(string error, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new { error }, statusCode: status);
}

/// <summary>
/// The token endpoint's success shape.
///
/// <para><b>Every field is named explicitly, and that is load-bearing.</b> RFC 6749 specifies
/// <c>snake_case</c> and Google parses by those exact names, while this server configures no
/// serializer options and therefore defaults to <c>camelCase</c>. Without these attributes the
/// response would be well-formed JSON that Google cannot read, and the symptom is an account-linking
/// failure with nothing wrong in any log.</para>
///
/// <para><see cref="RefreshToken"/> is null on the refresh grant and omitted rather than sent as
/// null — see <see cref="GoogleToken"/> for why there is no new one to send.</para>
/// </summary>
public sealed record GoogleTokenResponse(
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);
