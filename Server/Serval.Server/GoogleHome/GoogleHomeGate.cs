using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Serval.Server.Configuration;

namespace Serval.Server.GoogleHome;

/// <summary>
/// Which condition is stopping the Google Home integration from working, named individually.
///
/// <para>An enum rather than a bare bool because "Google Home is off" with five possible causes is
/// the message that costs an operator an hour. Every route answers 503 the same way; only the log
/// line and <c>GET /api/google/status</c> say which of these it was.</para>
///
/// <para><b>Carries a converter, and must.</b> System.Text.Json writes an unattributed enum as its
/// <em>ordinal</em>, so this went onto the wire as <c>"blocker": 1</c> and the App — which expects
/// a name — threw while parsing and silently drew nothing. Every other enum crossing this API has
/// the same attribute for the same reason; see <c>StreamRoleConverter</c> and
/// <c>RoleConverter</c>. Camel-cased to match them.</para>
/// </summary>
[JsonConverter(typeof(GoogleHomeBlockerConverter))]
public enum GoogleHomeBlocker
{
    /// <summary>Nothing — the integration is effective.</summary>
    None,

    /// <summary><c>Serval:GoogleHome:Enabled</c> is false. The ordinary state.</summary>
    Disabled,

    /// <summary>
    /// <c>Serval:WebRtc:Enabled</c> is false. A dependency rather than a coincidence: every camera
    /// this offers Google is streamed by go2rtc, and with the sidecar dormant there is nothing to
    /// sign into.
    /// </summary>
    WebRtcDisabled,

    /// <summary><c>Serval:GoogleHome:PublicBaseUrl</c> is missing, relative, or not <c>https</c>.</summary>
    PublicBaseUrlInvalid,

    /// <summary><c>Serval:GoogleHome:ProjectId</c> is empty, so no redirect URI can be trusted.</summary>
    ProjectIdMissing,

    /// <summary><c>Serval:GoogleHome:ClientId</c> is empty, so account linking has no gate.</summary>
    ClientIdMissing,

    /// <summary><c>Serval:GoogleHome:ClientSecret</c> is empty, so no code could be exchanged anyway.</summary>
    ClientSecretMissing,
}

/// <summary>
/// Writes <see cref="GoogleHomeBlocker"/> as a camelCase name rather than an ordinal, matching
/// every other enum this API puts on the wire.
/// </summary>
public sealed class GoogleHomeBlockerConverter()
    : JsonStringEnumConverter<GoogleHomeBlocker>(JsonNamingPolicy.CamelCase);

/// <summary>
/// What <c>GET /api/google/status</c> answers, and what the boot log is built from.
/// </summary>
/// <param name="Effective">Whether the integration will actually serve a request.</param>
/// <param name="Blocker">The first unmet condition, or <see cref="GoogleHomeBlocker.None"/>.</param>
/// <param name="Reason">An operator-facing sentence naming the fix. Null when effective.</param>
/// <param name="PublicBaseUrl">Echoed back so the console values can be checked against it.</param>
/// <param name="HomeGraphKeyConfigured">
/// Whether a key path is set. Not whether it loaded — that is the key store's answer, and it
/// arrives with the store.
/// </param>
public sealed record GoogleHomeStatus(
    bool Effective,
    GoogleHomeBlocker Blocker,
    string? Reason,
    string? PublicBaseUrl,
    bool HomeGraphKeyConfigured,

    /// <summary>
    /// Whether a Cast application is registered, which is what puts WebRTC on a television Google
    /// would otherwise only send HLS to.
    ///
    /// <para>Not a blocker, and deliberately not one: unset is a working deployment. It is reported
    /// because it is the setting with no other symptom — an operator who registered a Cast
    /// application and mistyped the id gets a picture either way, just the slow one, and nothing
    /// anywhere says which player they are watching.</para>
    /// </summary>
    bool CastReceiverConfigured);

/// <summary>
/// The single "is this integration on" predicate, checked by every Google Home route before it
/// does anything else.
///
/// <para><b>Fail-closed, and 503 rather than 404 or 401.</b> 503 is the honest answer — this server
/// does not offer this — and it is what <c>WebRtcEndpoints</c> already returns when its own switch
/// is off, so the two disabled features behave alike. It also means a misconfigured operator gets
/// the same status from Google's console test as from <c>curl</c>.</para>
///
/// <para><b>Read through <see cref="IOptionsMonitor{TOptions}"/> at request time, not captured at
/// registration.</b> Same reason <c>Serval:Push:Enabled</c> is checked at enqueue rather than at
/// service registration: toggling the feature should not need a restart.</para>
/// </summary>
public sealed class GoogleHomeGate
{
    private readonly IOptionsMonitor<ServerOptions> _options;

    public GoogleHomeGate(IOptionsMonitor<ServerOptions> options) => _options = options;

    /// <summary>The current answer, recomputed from configuration on every call.</summary>
    public GoogleHomeStatus Status => Evaluate(_options.CurrentValue);

    /// <summary>Shorthand for the route guard, which only cares whether to proceed.</summary>
    public bool IsEffective => Status.Effective;

    /// <summary>
    /// The Cast application to open camera streams with, or null when none is registered — which is
    /// the default and a working deployment. See
    /// <see cref="GoogleHomeOptions.CastReceiverAppId"/>.
    /// </summary>
    public string? CastReceiverAppId =>
        _options.CurrentValue.GoogleHome.CastReceiverAppId is { Length: > 0 } id ? id : null;

    /// <summary>
    /// The public origin as a URI, or null when it is unusable. Callers that have already passed
    /// the gate can treat null as unreachable.
    /// </summary>
    public Uri? PublicBaseUri => ParsePublicBaseUrl(_options.CurrentValue.GoogleHome.PublicBaseUrl);

    /// <summary>
    /// The configured ICE servers as the JSON <em>string</em> the CameraStream trait wants, or null
    /// when none are set — which is the normal case, and leaves Google to fall back to its own
    /// public STUN. Unused either way when the display and go2rtc are on one LAN, where host
    /// candidates connect directly.
    ///
    /// <para>Each entry is emitted as an RTCIceServer object. Entries are configured as bare URLs
    /// (<c>stun:stun.example.com:3478</c>) because that is what an operator has to hand; wrapping
    /// them here means the deployment file never has to contain JSON inside a list inside YAML.</para>
    /// </summary>
    public string? IceServersJson
    {
        get
        {
            List<string> urls = _options.CurrentValue.GoogleHome.IceServers;
            string[] usable = [.. urls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim())];

            return usable.Length == 0
                ? null
                : System.Text.Json.JsonSerializer.Serialize(usable.Select(u => new { urls = u }));
        }
    }

    /// <summary>
    /// Evaluated against a stated <see cref="ServerOptions"/> so the conditions can be tested one
    /// at a time without a host.
    ///
    /// <para>Order matters only for which sentence an operator sees first, and it runs cheapest and
    /// most likely first: the master switch, then its dependency, then the three values that have
    /// to be typed correctly.</para>
    /// </summary>
    public static GoogleHomeStatus Evaluate(ServerOptions options)
    {
        GoogleHomeOptions google = options.GoogleHome;
        bool keyConfigured = !string.IsNullOrWhiteSpace(google.HomeGraphKeyPath);
        string? baseUrl = string.IsNullOrWhiteSpace(google.PublicBaseUrl) ? null : google.PublicBaseUrl;

        GoogleHomeBlocker blocker = FirstBlocker(options);

        return new GoogleHomeStatus(
            Effective: blocker == GoogleHomeBlocker.None,
            Blocker: blocker,
            Reason: Describe(blocker),
            PublicBaseUrl: baseUrl,
            HomeGraphKeyConfigured: keyConfigured,
            CastReceiverConfigured: !string.IsNullOrWhiteSpace(google.CastReceiverAppId));
    }

    private static GoogleHomeBlocker FirstBlocker(ServerOptions options)
    {
        GoogleHomeOptions google = options.GoogleHome;

        if (!google.Enabled)
        {
            return GoogleHomeBlocker.Disabled;
        }

        if (!options.WebRtc.Enabled)
        {
            return GoogleHomeBlocker.WebRtcDisabled;
        }

        if (ParsePublicBaseUrl(google.PublicBaseUrl) is null)
        {
            return GoogleHomeBlocker.PublicBaseUrlInvalid;
        }

        if (string.IsNullOrWhiteSpace(google.ProjectId))
        {
            return GoogleHomeBlocker.ProjectIdMissing;
        }

        if (string.IsNullOrWhiteSpace(google.ClientId))
        {
            return GoogleHomeBlocker.ClientIdMissing;
        }

        if (string.IsNullOrWhiteSpace(google.ClientSecret))
        {
            return GoogleHomeBlocker.ClientSecretMissing;
        }

        return GoogleHomeBlocker.None;
    }

    /// <summary>
    /// Absolute and <c>https</c>, or nothing. Google will not POST to plain HTTP and will not
    /// follow a relative value anywhere, so accepting either would only move the failure to a place
    /// where the reason is Google's error message rather than ours.
    /// </summary>
    public static Uri? ParsePublicBaseUrl(string value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? parsed)
        && parsed.Scheme == Uri.UriSchemeHttps
            ? parsed
            : null;

    /// <summary>
    /// The operator-facing sentence for each blocker: what is wrong, and what to do about it.
    /// Shown by the App's status card and logged once at boot.
    /// </summary>
    public static string? Describe(GoogleHomeBlocker blocker) => blocker switch
    {
        GoogleHomeBlocker.None => null,

        GoogleHomeBlocker.Disabled =>
            "Serval:GoogleHome:Enabled is false, so the Google Home routes are closed. This is the "
            + "default. See Docs/google-home.md for what it needs before turning it on.",

        GoogleHomeBlocker.WebRtcDisabled =>
            "Google Home needs Serval:WebRtc:Enabled, because every camera it offers is streamed by "
            + "the go2rtc sidecar. With WebRTC off there is nothing for Google to connect to.",

        GoogleHomeBlocker.PublicBaseUrlInvalid =>
            "Serval:GoogleHome:PublicBaseUrl must be an absolute https URL, e.g. "
            + "https://serval.example.com. It is the origin Google reaches this server on and the "
            + "one the camera signaling URL is built from; Google will not post to plain HTTP.",

        GoogleHomeBlocker.ProjectIdMissing =>
            "Serval:GoogleHome:ProjectId is not set. It is the Google Home Developer Console project "
            + "id, and it fixes the only redirect URIs account linking will honour.",

        GoogleHomeBlocker.ClientIdMissing =>
            "Serval:GoogleHome:ClientId is not set, so account linking rejects every request. "
            + "Generate one with `openssl rand -base64 32` and paste the same value into the Google "
            + "console — it is a secret here, and the only gate on who may link.",

        GoogleHomeBlocker.ClientSecretMissing =>
            "Serval:GoogleHome:ClientSecret is not set, so no authorization code could be exchanged. "
            + "Generate one with `openssl rand -base64 32` and paste the same value into the Google "
            + "console.",

        _ => null,
    };
}
