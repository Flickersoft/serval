using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Serval.Server.GoogleHome;

/// <summary>
/// What HomeGraph is told about one camera.
///
/// <para>Two independent things, and keeping them independent is the point. <see cref="Online"/> is
/// reachability — whether the camera is producing frames. <see cref="On"/> is whether Serval is
/// offering it to Google, as set from the Home app. A camera switched off is still online, which is
/// what leaves its control usable so it can be switched back on.</para>
/// </summary>
public readonly record struct CameraState(bool Online, bool On);

/// <summary>
/// Tells Google to re-run SYNC, so a camera added or renamed here shows up there without anyone
/// re-linking.
///
/// <para><b>The key buys two calls, and both are on this client.</b> <c>requestSync</c> makes a
/// camera added or renamed here appear there; <c>reportStateAndNotification</c> keeps HomeGraph's
/// idea of whether each camera is up in step with ours. Without a key neither happens: the device
/// list goes stale until someone re-links, and SYNC reports <c>willReportState: false</c> because
/// promising reports that cannot be sent is worse than not promising them.</para>
///
/// <para><b>A camera does have reportable state, contrary to an earlier reading here.</b> It has no
/// trait state — <c>CameraStream</c> declares no attributes that change — but <c>online</c> is
/// reportable and Google expects it to be reported. A device that never reports is a device
/// HomeGraph believes nothing about, which is what makes Google's own Test Suite refuse to run
/// against it: it checks the device is online *before* testing, and reads that from HomeGraph.</para>
///
/// <para><b>Hand-rolled against the BCL, with no new dependency.</b> The exchange is an RS256
/// assertion POSTed for an access token, and <c>JwtSecurityTokenHandler</c> plus
/// <c>RSA.ImportFromPem</c> are already referenced by the auth path. This is deliberately *not* the
/// hand-written JWS that <c>Push/VapidSigner</c> uses: that exists for one specific documented
/// reason — ES256 needs a raw r‖s signature where most helpers emit DER — and RS256's PKCS#1
/// signature has no equivalent trap. Copying the hand-rolling without copying the reason would be
/// cargo cult.</para>
///
/// <para>This is the second HTTP client in the process pointed at the public internet, after Web
/// Push. What crosses here is an agent user id and nothing else — no camera name, no image, no
/// telemetry.</para>
/// </summary>
public sealed class HomeGraphClient
{
    private const string Scope = "https://www.googleapis.com/auth/homegraph";
    private const string RequestSyncUrl = "https://homegraph.googleapis.com/v1/devices:requestSync";

    private const string ReportStateUrl =
        "https://homegraph.googleapis.com/v1/devices:reportStateAndNotification";

    /// <summary>
    /// Google's tokens last an hour. Re-fetch five minutes early so one is never handed to a
    /// request that still has to travel — the margin <c>VapidSigner</c> keeps for the same reason.
    /// </summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly HomeGraphKeyStore _keys;
    private readonly TimeProvider _time;
    private readonly ILogger<HomeGraphClient> _logger;
    private readonly Lock _gate = new();

    private string? _accessToken;
    private DateTimeOffset _accessTokenExpires;

    public HomeGraphClient(
        HttpClient http,
        HomeGraphKeyStore keys,
        TimeProvider time,
        ILogger<HomeGraphClient> logger)
    {
        _http = http;
        _keys = keys;
        _time = time;
        _logger = logger;
    }

    /// <summary>Whether there is a key at all. False is normal and means sync is simply off.</summary>
    public bool IsConfigured => _keys.Key is not null;

    /// <summary>
    /// Asks Google to re-run SYNC for one linked account. Returns false when there is no key, or
    /// when Google refused — the caller retries on the next change rather than looping, because a
    /// Google outage is routine and a stale device list is a small thing to carry until it clears.
    /// </summary>
    public async Task<bool> RequestSyncAsync(string agentUserId, CancellationToken ct)
    {
        HomeGraphKey? key = _keys.Key;
        if (key is null)
        {
            return false;
        }

        string? token = await AccessTokenAsync(key, ct);
        if (token is null)
        {
            return false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, RequestSyncUrl)
        {
            Content = JsonContent.Create(new { agentUserId, async = true }),
        };
        request.Headers.Authorization = new("Bearer", token);

        using HttpResponseMessage response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            // The body carries Google's own reason, and it is the difference between "the key is
            // for the wrong project" and "nobody has linked" — neither of which is guessable from
            // the status alone.
            string body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "HomeGraph requestSync was refused ({Status}): {Body}", (int)response.StatusCode, body);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Tells HomeGraph whether each camera is up.
    ///
    /// <para>The whole set is sent every time rather than only what moved. It is a handful of
    /// booleans, it is idempotent, and it self-heals: a report Google never received would
    /// otherwise leave HomeGraph permanently wrong about one camera, with nothing to trigger a
    /// correction until that camera changed again.</para>
    ///
    /// <para>Returns false on any refusal, and the caller retries on the next change — the
    /// <see cref="RequestSyncAsync"/> disposition, and for the same reason.</para>
    /// </summary>
    public async Task<bool> ReportStateAsync(
        string agentUserId, IReadOnlyDictionary<string, CameraState> states, CancellationToken ct)
    {
        HomeGraphKey? key = _keys.Key;
        if (key is null || states.Count == 0)
        {
            return false;
        }

        string? token = await AccessTokenAsync(key, ct);
        if (token is null)
        {
            return false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, ReportStateUrl)
        {
            Content = JsonContent.Create(ReportStateBody(agentUserId, Guid.NewGuid().ToString(), states)),
        };
        request.Headers.Authorization = new("Bearer", token);

        using HttpResponseMessage response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "HomeGraph reportState was refused ({Status}): {Body}", (int)response.StatusCode, body);
            return false;
        }

        return true;
    }

    /// <summary>
    /// The <c>reportStateAndNotification</c> body: a request id, who the devices belong to, and the
    /// states themselves nested three deep under <c>payload.devices.states</c>, keyed by device id.
    ///
    /// <para>Internal and pure so a test can pin the field names. They are Google's vocabulary and
    /// not derivable from anything here, and the nesting is deep enough that getting it subtly
    /// wrong produces a 200 that reports nothing.</para>
    ///
    /// <para><paramref name="requestId"/> is passed in rather than generated here for the same
    /// reason: so the shape can be asserted against a fixed value.</para>
    /// </summary>
    internal static object ReportStateBody(
        string agentUserId, string requestId, IReadOnlyDictionary<string, CameraState> states) => new
        {
            requestId,
            agentUserId,
            payload = new
            {
                devices = new
                {
                    states = states.ToDictionary(
                        pair => pair.Key,
                        pair => (object)new { online = pair.Value.Online, on = pair.Value.On },
                        StringComparer.Ordinal),
                },
            },
        };

    /// <summary>
    /// A cached access token for the HomeGraph scope, obtained by the JWT-bearer grant: sign an
    /// assertion with the service account's private key, and exchange it.
    /// </summary>
    private async Task<string?> AccessTokenAsync(HomeGraphKey key, CancellationToken ct)
    {
        DateTimeOffset now = _time.GetUtcNow();

        lock (_gate)
        {
            if (_accessToken is not null && _accessTokenExpires - RefreshMargin > now)
            {
                return _accessToken;
            }
        }

        string assertion;
        try
        {
            assertion = SignAssertion(key, now);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            _logger.LogWarning(
                ex,
                "The HomeGraph key's private_key could not be used to sign. Google will not be "
                + "told about camera changes; nothing else is affected.");
            return null;
        }

        using HttpResponseMessage response = await _http.PostAsync(
            key.EffectiveTokenUri,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = assertion,
            }),
            ct);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "Exchanging the HomeGraph key for an access token failed ({Status}): {Body}",
                (int)response.StatusCode, body);
            return null;
        }

        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        if (!json.RootElement.TryGetProperty("access_token", out JsonElement token)
            || token.GetString() is not { Length: > 0 } value)
        {
            _logger.LogWarning("Google's token response carried no access_token.");
            return null;
        }

        int expiresIn = json.RootElement.TryGetProperty("expires_in", out JsonElement expiry)
            && expiry.TryGetInt32(out int seconds)
                ? seconds
                : 3600;

        lock (_gate)
        {
            _accessToken = value;
            _accessTokenExpires = now.AddSeconds(expiresIn);
        }

        return value;
    }

    /// <summary>
    /// The RS256 assertion Google's JWT-bearer grant takes: the service account asking, in its own
    /// name, for the HomeGraph scope.
    ///
    /// <para>Internal so a test can check the header and claims against the key's public half
    /// without a network — a malformed assertion otherwise fails as an opaque 400 from Google.</para>
    /// </summary>
    internal static string SignAssertion(HomeGraphKey key, DateTimeOffset now)
    {
        string header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(
            new { alg = "RS256", typ = "JWT" }));

        string payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = key.ClientEmail,
            scope = Scope,
            aud = key.EffectiveTokenUri,
            iat = now.ToUnixTimeSeconds(),

            // An hour is Google's maximum for an assertion, and it is never held: it is signed,
            // exchanged, and discarded within one request.
            exp = now.AddHours(1).ToUnixTimeSeconds(),
        }));

        string signingInput = $"{header}.{payload}";

        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(key.PrivateKeyPem);

        byte[] signature = rsa.SignData(
            Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) =>
        System.Buffers.Text.Base64Url.EncodeToString(bytes);
}
