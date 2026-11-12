using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Serval.Server.Configuration;

namespace Serval.Server.Mcp;

/// <summary>Which condition is keeping the MCP endpoint closed. Written to the wire by name.</summary>
[JsonConverter(typeof(McpBlockerConverter))]
public enum McpBlocker
{
    /// <summary>Nothing — the endpoint is open.</summary>
    None,

    /// <summary><c>Serval:Mcp:Enabled</c> is false. The ordinary state.</summary>
    Disabled,

    /// <summary><c>Serval:Mcp:PublicBaseUrl</c> is missing, not https, or carries a path.</summary>
    PublicBaseUrlInvalid,
}

public sealed class McpBlockerConverter() : JsonStringEnumConverter<McpBlocker>(JsonNamingPolicy.CamelCase);

/// <summary>What <c>GET /api/mcp/status</c> answers: whether assistants can connect, and where to point them.</summary>
/// <param name="ServerUrl">The address to give an assistant — <c>{PublicBaseUrl}/mcp</c> — or null while closed.</param>
public sealed record McpStatus(bool Effective, McpBlocker Blocker, string? Reason, string? ServerUrl);

/// <summary>
/// The single "is MCP on" predicate, and the one place the issuer and resource URLs are derived.
///
/// <para><b>404 when closed, not 503 like Google Home.</b> Google's console tests a configured
/// endpoint and an operator reads its answer, so naming the outage helps. An MCP client probing a
/// server is a stranger as often as not, and a switched-off feature should look like one this
/// server does not have.</para>
///
/// <para>Read through <see cref="IOptionsMonitor{TOptions}"/> on every call, so the settings page
/// can switch it without a restart — the same arrangement as <c>GoogleHomeGate</c>.</para>
/// </summary>
public sealed class McpGate
{
    public const string McpPath = "/mcp";

    private readonly IOptionsMonitor<ServerOptions> _options;

    public McpGate(IOptionsMonitor<ServerOptions> options) => _options = options;

    public McpStatus Status => Evaluate(_options.CurrentValue.Mcp);

    public bool IsEffective => Status.Effective;

    /// <summary>The OAuth issuer: the configured origin with no trailing slash. Null while closed.</summary>
    public string? Issuer => IsEffective ? ParsePublicBaseUrl(_options.CurrentValue.Mcp.PublicBaseUrl) : null;

    /// <summary>The protected resource every token is bound to, <c>{Issuer}/mcp</c>. Null while closed.</summary>
    public string? Resource => Issuer is { } issuer ? issuer + McpPath : null;

    public McpOptions Options => _options.CurrentValue.Mcp;

    public static McpStatus Evaluate(McpOptions options)
    {
        McpBlocker blocker = !options.Enabled ? McpBlocker.Disabled
            : ParsePublicBaseUrl(options.PublicBaseUrl) is null ? McpBlocker.PublicBaseUrlInvalid
            : McpBlocker.None;

        return new McpStatus(
            Effective: blocker == McpBlocker.None,
            Blocker: blocker,
            Reason: Describe(blocker),
            ServerUrl: blocker == McpBlocker.None ? ParsePublicBaseUrl(options.PublicBaseUrl) + McpPath : null);
    }

    /// <summary>
    /// The configured origin, normalised to <c>scheme://host[:port]</c>, or null when unusable.
    ///
    /// <para>https, or http on a loopback host — the one case where plain HTTP is still a real
    /// deployment, namely a client and server on the same machine. A path is refused because the
    /// discovery documents live at fixed paths under the origin, and a server behind a path prefix
    /// would publish an issuer that does not match where its metadata is found.</para>
    /// </summary>
    public static string? ParsePublicBaseUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        bool secure = uri.Scheme == Uri.UriSchemeHttps
            || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

        bool bareOrigin = uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && string.IsNullOrEmpty(uri.UserInfo);

        return secure && bareOrigin ? uri.GetLeftPart(UriPartial.Authority) : null;
    }

    public static string? Describe(McpBlocker blocker) => blocker switch
    {
        McpBlocker.None => null,
        McpBlocker.Disabled =>
            "AI assistants are switched off (Serval:Mcp:Enabled). Turn them on under Settings → AI assistants.",
        McpBlocker.PublicBaseUrlInvalid =>
            "Serval:Mcp:PublicBaseUrl must be this server's public https address with no path, "
            + "e.g. https://serval.example.com. Assistants connect from the internet, and the "
            + "address is part of every link, so it cannot be guessed from the request.",
        _ => null,
    };
}
