using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Serval.Server.Mcp;

/// <summary>
/// The protocol rules of Serval's MCP authorization server that need no storage, kept apart from
/// the endpoints so each can be tested on its own.
/// </summary>
public static class McpOAuthRules
{
    /// <summary>
    /// PKCE S256 (RFC 7636 §4.6): the challenge sent at authorize must be the base64url SHA-256 of
    /// the verifier presented at the token endpoint. The verifier's shape is checked too — 43 to
    /// 128 unreserved characters — since a short one is guessable and the spec forbids it.
    /// Compared in fixed time.
    /// </summary>
    public static bool VerifyPkce(string? verifier, string challenge)
    {
        if (verifier is null || verifier.Length is < 43 or > 128 || !verifier.All(IsUnreserved))
        {
            return false;
        }

        string computed = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        byte[] a = Encoding.ASCII.GetBytes(computed);
        byte[] b = Encoding.ASCII.GetBytes(challenge);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// Whether an S256 challenge is well formed: exactly the 43 base64url characters a SHA-256
    /// digest encodes to. Checked at authorize, so a malformed one fails there rather than turning
    /// into an unredeemable code.
    /// </summary>
    public static bool IsValidChallenge(string? challenge) =>
        challenge is { Length: 43 } && challenge.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool IsUnreserved(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~';

    /// <summary>
    /// The redirect that ends an authorization request: back to the client, carrying either a code
    /// or an error, its own <c>state</c> unchanged, and <c>iss</c> (RFC 9207) so the client can
    /// tell which authorization server answered — the defence against a mix-up attack between two
    /// servers it uses, which the MCP spec asks every server to support.
    /// </summary>
    public static string BuildRedirect(
        string redirectUri, string issuer, string? state, string? code = null, string? error = null)
    {
        var url = new StringBuilder(redirectUri);
        url.Append(redirectUri.Contains('?', StringComparison.Ordinal) ? '&' : '?');

        url.Append(code is not null ? "code=" : "error=");
        url.Append(Uri.EscapeDataString(code ?? error!));

        if (!string.IsNullOrEmpty(state))
        {
            url.Append("&state=").Append(Uri.EscapeDataString(state));
        }

        url.Append("&iss=").Append(Uri.EscapeDataString(issuer));
        return url.ToString();
    }

    /// <summary>
    /// The redirect URI a request will use, or null when it names none the client registered.
    ///
    /// <para>Exact string comparison, as OAuth 2.1 requires — no prefix, no normalisation. The
    /// one allowance is RFC 8252 §7.3: a loopback redirect may differ in port alone, because a
    /// desktop client picks a free port each time it listens. When the request names no redirect
    /// at all, a client with exactly one registered gets that one.</para>
    /// </summary>
    public static string? MatchRedirectUri(string? requested, IReadOnlyList<string> registered)
    {
        if (string.IsNullOrEmpty(requested))
        {
            return registered.Count == 1 ? registered[0] : null;
        }

        foreach (string candidate in registered)
        {
            if (string.Equals(candidate, requested, StringComparison.Ordinal) || LoopbackPortOnlyDiffers(candidate, requested))
            {
                return requested;
            }
        }

        return null;
    }

    private static bool LoopbackPortOnlyDiffers(string registered, string requested) =>
        Uri.TryCreate(registered, UriKind.Absolute, out Uri? a)
        && Uri.TryCreate(requested, UriKind.Absolute, out Uri? b)
        && a.Scheme == Uri.UriSchemeHttp && b.Scheme == Uri.UriSchemeHttp
        && a.IsLoopback && b.IsLoopback
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.PathAndQuery, b.PathAndQuery, StringComparison.Ordinal);

    /// <summary>
    /// Where a redirect will send the browser, put the way a person can judge it: the host and
    /// port for a web address; for an app's own scheme, the scheme and its authority, which names
    /// the extension receiving the code (<c>vscode://publisher.extension</c>), or the scheme alone
    /// when there is none.
    /// </summary>
    public static string DescribeRedirect(string redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out Uri? uri))
        {
            return redirectUri;
        }

        if (uri.Scheme is "http" or "https")
        {
            return uri.Authority;
        }

        return uri.Authority.Length > 0 ? $"{uri.Scheme}://{uri.Authority}" : $"{uri.Scheme}:";
    }
}
