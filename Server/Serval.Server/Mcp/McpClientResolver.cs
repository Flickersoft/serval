using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Serval.Server.Mcp;

/// <summary>An MCP client as the authorization endpoint knows it, however it identified itself.</summary>
/// <param name="ClientId">Exactly as presented.</param>
/// <param name="Name">What the client calls itself, cleaned for display. Self-asserted either way.</param>
/// <param name="RedirectUris">The only destinations a code for this client may be sent to.</param>
/// <param name="Verified">
/// True for a client that identified itself by a metadata document: its <c>client_id</c> is an
/// https URL on its own domain, so the host the consent screen shows is one it proved it controls.
/// False for a dynamic registration, which anybody can create with any name.
/// </param>
public sealed record McpClient(string ClientId, string? Name, IReadOnlyList<string> RedirectUris, bool Verified)
{
    /// <summary>The domain a verified client's metadata lives on — the one name on it that is not self-asserted.</summary>
    public string? VerifiedHost => VerifiedHostOf(ClientId, Verified);

    public static string? VerifiedHostOf(string clientId, bool verified) =>
        verified && Uri.TryCreate(clientId, UriKind.Absolute, out Uri? uri) ? uri.Host : null;
}

/// <summary>
/// Turns a <c>client_id</c> into an <see cref="McpClient"/>, by either of the two routes the MCP
/// authorization spec allows a client without prior arrangement.
///
/// <list type="bullet">
/// <item><b>Client ID Metadata Document (CIMD)</b>, preferred: the <c>client_id</c> is an https
/// URL, and the JSON at that URL describes the client. Nothing is stored; the document is fetched
/// and cached for an hour.</item>
/// <item><b>Dynamic Client Registration (DCR)</b>, the fallback the 2026-07-28 revision deprecates
/// but many clients still use: the client registered earlier and got an opaque id, looked up in
/// <see cref="McpOAuthStore"/>.</item>
/// </list>
///
/// <para><b>Fetching a URL a stranger chose is a server-side request forgery primitive</b>, so the
/// fetch is fenced: https only, no redirects, no proxy, a five-second deadline, a 16 KB ceiling, and
/// a connect callback that resolves the host itself and refuses any address that is not on the
/// public internet. Checking the address at connect time rather than before the request is what
/// defeats DNS rebinding — there is no second lookup for an attacker's resolver to answer
/// differently.</para>
/// </summary>
public sealed class McpClientResolver : IDisposable
{
    internal const int MaxDocumentBytes = 16 * 1024;
    private const int MaxCachedDocuments = 256;
    private const int MaxNameLength = 80;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Schemes no redirect may use. A native app's private-use scheme (<c>cursor://</c>,
    /// <c>vscode://</c>) is legitimate per RFC 8252; these are not, being either script, a local
    /// resource, or plain transport an attacker could intercept.
    /// </summary>
    private static readonly HashSet<string> RefusedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "javascript", "vbscript", "data", "file", "about", "blob", "ftp", "ws", "wss",
    };

    private readonly McpOAuthStore _store;
    private readonly ILogger<McpClientResolver> _logger;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, (McpClient Client, DateTimeOffset Until)> _cache =
        new(StringComparer.Ordinal);

    public McpClientResolver(McpOAuthStore store, ILogger<McpClientResolver> logger)
    {
        _store = store;
        _logger = logger;

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = FetchTimeout,
            ConnectCallback = ConnectPublicOnlyAsync,
        };

        _http = new HttpClient(handler) { Timeout = FetchTimeout };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Serval-MCP-Authorization/1");
    }

    /// <summary>The client this id names, or null when it names nothing usable. Never throws on bad input.</summary>
    public async Task<McpClient?> ResolveAsync(string clientId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        if (IsMetadataDocumentUrl(clientId))
        {
            return await FetchMetadataDocumentAsync(clientId, ct);
        }

        McpRegisteredClient? registered = await _store.GetRegisteredClientAsync(clientId, ct);
        return registered is null
            ? null
            : new McpClient(registered.ClientId, CleanName(registered.ClientName), registered.RedirectUris, Verified: false);
    }

    private async Task<McpClient?> FetchMetadataDocumentAsync(string url, CancellationToken ct)
    {
        if (_cache.TryGetValue(url, out var cached) && cached.Until > DateTimeOffset.UtcNow)
        {
            return cached.Client;
        }

        try
        {
            using HttpResponseMessage response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MCP client metadata at {Url} answered {Status}.", url, (int)response.StatusCode);
                return null;
            }

            byte[]? body = await ReadCappedAsync(response.Content, ct);
            if (body is null)
            {
                _logger.LogWarning("MCP client metadata at {Url} is larger than {Max} bytes.", url, MaxDocumentBytes);
                return null;
            }

            McpClient? client = ParseMetadataDocument(url, body);
            if (client is null)
            {
                _logger.LogWarning(
                    "MCP client metadata at {Url} is not a usable document: it must be JSON naming "
                    + "this exact URL as client_id, with acceptable redirect_uris.", url);
                return null;
            }

            if (_cache.Count >= MaxCachedDocuments)
            {
                // Expired entries first; only a cache full of live ones is dropped wholesale, which
                // costs each real client one refetch.
                DateTimeOffset now = DateTimeOffset.UtcNow;
                foreach ((string key, var entry) in _cache)
                {
                    if (entry.Until <= now)
                    {
                        _cache.TryRemove(key, out _);
                    }
                }

                if (_cache.Count >= MaxCachedDocuments)
                {
                    _cache.Clear();
                }
            }

            _cache[url] = (client, DateTimeOffset.UtcNow + CacheLifetime);
            return client;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Could not fetch MCP client metadata from {Url}: {Error}", url, ex.Message);
            return null;
        }
    }

    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaxDocumentBytes)
        {
            return null;
        }

        await using Stream stream = await content.ReadAsStreamAsync(ct);
        return await ReadCappedAsync(stream, MaxDocumentBytes, ct);
    }

    /// <summary>
    /// The whole stream, or null as soon as it passes <paramref name="max"/> bytes. Counted while
    /// reading rather than trusted from a declared length, which a chunked body does not have.
    /// </summary>
    internal static async Task<byte[]?> ReadCappedAsync(Stream stream, int max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];
        int read;

        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Validates a fetched metadata document. Public and static so the rules are testable without a
    /// network.
    ///
    /// <para>The document must name its own URL as <c>client_id</c>, byte for byte — otherwise one
    /// client's document could be served from another's address. It must list at least one
    /// acceptable redirect URI, and if it states an authentication method, that method must be
    /// <c>none</c>: this server holds no client keys, so a client expecting to authenticate would
    /// fail at the token endpoint anyway.</para>
    /// </summary>
    internal static McpClient? ParseMetadataDocument(string url, ReadOnlySpan<byte> json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json.ToArray());
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("client_id", out JsonElement id)
                || id.ValueKind != JsonValueKind.String
                || !string.Equals(id.GetString(), url, StringComparison.Ordinal))
            {
                return null;
            }

            if (root.TryGetProperty("token_endpoint_auth_method", out JsonElement method)
                && !(method.ValueKind == JsonValueKind.String && method.GetString() == "none"))
            {
                return null;
            }

            List<string>? redirects = ReadRedirectUris(root);
            if (redirects is null)
            {
                return null;
            }

            string? name = root.TryGetProperty("client_name", out JsonElement n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;

            return new McpClient(url, CleanName(name), redirects, Verified: true);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A non-empty list of acceptable redirect URIs, or null.</summary>
    internal static List<string>? ReadRedirectUris(JsonElement root)
    {
        if (!root.TryGetProperty("redirect_uris", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var uris = new List<string>();
        foreach (JsonElement entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || !IsAcceptableRedirectUri(entry.GetString()))
            {
                return null;
            }

            uris.Add(entry.GetString()!);
        }

        return uris.Count is > 0 and <= 20 ? uris : null;
    }

    /// <summary>
    /// Whether a <c>client_id</c> is a metadata-document URL: https, with a path, and nothing that
    /// would make two spellings of one document. Anything else is treated as a registered id.
    /// </summary>
    public static bool IsMetadataDocumentUrl(string clientId) =>
        Uri.TryCreate(clientId, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.AbsolutePath.Length > 1
        && uri.Fragment.Length == 0
        && string.IsNullOrEmpty(uri.UserInfo);

    /// <summary>
    /// Whether a redirect URI is one this server will ever send a code to (RFC 8252 / OAuth 2.1):
    /// https anywhere; http only to the loopback interface, where a desktop client listens; or a
    /// native app's private-use scheme. Never a fragment, which a code must not land in.
    /// </summary>
    public static bool IsAcceptableRedirectUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Fragment.Length > 0)
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            return uri.IsLoopback;
        }

        return !RefusedSchemes.Contains(uri.Scheme);
    }

    /// <summary>
    /// Whether an address is somewhere on the public internet — i.e. not loopback, private, link-local,
    /// carrier-grade NAT, multicast, unspecified, or an IPv6 form of any of those.
    /// </summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return !(b[0] == 0
                || b[0] == 10
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || b[0] == 127
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
                || b[0] >= 224);
        }

        // IPv6: global unicast (2000::/3) only, which rules out loopback, link-local, unique-local,
        // multicast, NAT64 (64:ff9b::/96) and the IPv4-compatible forms in one test. Then the three
        // global-looking ranges that are not: Teredo and 6to4 each embed an IPv4 address that may be
        // private, and documentation addresses route nowhere legitimate.
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] b = address.GetAddressBytes();
            bool globalUnicast = (b[0] & 0xE0) == 0x20;
            bool teredo = b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00;
            bool documentation = b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8;
            bool sixToFour = b[0] == 0x20 && b[1] == 0x02;
            return globalUnicast && !teredo && !documentation && !sixToFour;
        }

        return false;
    }

    private static async ValueTask<Stream> ConnectPublicOnlyAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);

        // Every address, not the first: a host that resolves to one public and one private address
        // is either misconfigured or an attempt to win a race, and neither should be fetched.
        if (addresses.Length == 0 || !addresses.All(IsPublicAddress))
        {
            throw new HttpRequestException(
                $"{context.DnsEndPoint.Host} does not resolve only to public addresses.");
        }

        // Dual-mode, and every address offered, so a host with no IPv6 route — a default Docker
        // bridge, say — falls through to the A record when the AAAA one sorts first and fails.
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>A display name with control characters removed and its length bounded, or null when blank.</summary>
    internal static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string cleaned = new string([.. name.Where(c => !char.IsControl(c))]).Trim();
        if (cleaned.Length > MaxNameLength)
        {
            cleaned = cleaned[..MaxNameLength].TrimEnd() + "…";
        }

        return cleaned.Length == 0 ? null : cleaned;
    }

    public void Dispose() => _http.Dispose();
}
