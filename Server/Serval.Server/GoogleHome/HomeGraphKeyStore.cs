using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Serval.Server.Configuration;

namespace Serval.Server.GoogleHome;

/// <summary>
/// The fields this server needs out of a Google service-account JSON key. The file carries a dozen
/// more; these three are the whole of what signing an assertion takes.
/// </summary>
public sealed record HomeGraphKey(
    [property: JsonPropertyName("client_email")] string ClientEmail,
    [property: JsonPropertyName("private_key")] string PrivateKeyPem,
    [property: JsonPropertyName("token_uri")] string? TokenUri)
{
    /// <summary>Google's own endpoint, and what every key file names — but defaulted rather than required.</summary>
    public string EffectiveTokenUri =>
        string.IsNullOrWhiteSpace(TokenUri) ? "https://oauth2.googleapis.com/token" : TokenUri;
}

/// <summary>
/// Loads the HomeGraph service-account key from the path the operator bind-mounted, once, and
/// remembers the answer.
///
/// <para><b>Absent is the ordinary state and must never be an error.</b> Most deployments will not
/// have this file. Without it <c>requestSync</c> is off and nothing else changes: the integration
/// links, lists cameras and streams them exactly as before — Google simply does not hear about a
/// camera being added or renamed until someone re-links or says "sync my devices". A missing key
/// therefore produces one boot warning and a null here, never an exception and never a failed
/// startup.</para>
///
/// <para><b>A file path rather than a setting or an upload.</b> The file is a couple of kilobytes
/// with an embedded PEM full of newlines, which no environment variable survives being pasted
/// into; and a settings-writable path would be a file-read primitive handed to anyone who can
/// reach the API. Bind-mounting read-only is already how this project ships model weights.</para>
///
/// <para>The load is cached because the answer cannot change while the process lives — the path
/// comes from environment-only configuration, which the settings overlay cannot touch.</para>
/// </summary>
public sealed class HomeGraphKeyStore
{
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly ILogger<HomeGraphKeyStore> _logger;
    private readonly Lock _gate = new();

    private bool _loaded;
    private HomeGraphKey? _key;

    public HomeGraphKeyStore(IOptionsMonitor<ServerOptions> options, ILogger<HomeGraphKeyStore> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// The key, or null when there is no path configured or the file cannot be read. A read failure
    /// is logged once and then answered the same way as an absent file, because the consequence is
    /// identical and a retry loop against a missing mount helps nobody.
    /// </summary>
    public HomeGraphKey? Key
    {
        get
        {
            lock (_gate)
            {
                if (!_loaded)
                {
                    _loaded = true;
                    _key = Load();
                }

                return _key;
            }
        }
    }

    private HomeGraphKey? Load()
    {
        string path = _options.CurrentValue.GoogleHome.HomeGraphKeyPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            HomeGraphKey? key = JsonSerializer.Deserialize<HomeGraphKey>(File.ReadAllText(path));

            if (key is null
                || string.IsNullOrWhiteSpace(key.ClientEmail)
                || string.IsNullOrWhiteSpace(key.PrivateKeyPem))
            {
                _logger.LogWarning(
                    "The HomeGraph key at {Path} is missing client_email or private_key, so Google "
                    + "will not be told about camera changes. Download a service-account key in "
                    + "JSON form — not the .p12 — from the Google Cloud console.", path);
                return null;
            }

            _logger.LogInformation(
                "HomeGraph key loaded for {ClientEmail}; camera changes will be pushed to Google.",
                key.ClientEmail);

            return key;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(
                ex,
                "The HomeGraph key at {Path} could not be read, so Google will not be told about "
                + "camera changes. Everything else about the integration is unaffected.", path);
            return null;
        }
    }
}
