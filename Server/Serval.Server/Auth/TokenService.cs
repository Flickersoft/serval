using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Serval.Server.Configuration;

namespace Serval.Server.Auth;

/// <summary>
/// Mints the JWTs this server issues to itself: access tokens (used as a normal
/// <c>Authorization: Bearer</c> header) and stream tokens (same lifetime, carrying a "stream"
/// scope claim, used as a <c>?stream_token=</c> query parameter by the video player — see
/// <c>Media/MediaEndpoints.cs</c> for why a query parameter is unavoidable there). Both share one
/// signing key; it is <c>Program.cs</c>'s two named JWT bearer schemes that actually tell them
/// apart, by requiring the "stream" claim on one scheme and rejecting it on the other.
///
/// A third kind rides the stream scheme rather than adding a scheme of its own: an alert image
/// token, which is a stream token carrying an extra claim naming the single alert whose poster it
/// may fetch — see <see cref="CreateAlertImageToken"/> for why it is longer-lived and narrower than
/// everything else here.
///
/// The one-time WebSocket connect ticket is deliberately not a JWT — see
/// <see cref="StreamTicketService"/> — since it needs server-side single-use enforcement a JWT
/// cannot provide on its own.
/// </summary>
public sealed class TokenService
{
    public const string ScopeClaimType = "scope";
    public const string StreamScope = "stream";

    /// <summary>
    /// Names the one alert whose picture a token may fetch. Present only on an alert image token;
    /// its absence is what makes every other credential unrestricted.
    /// </summary>
    public const string AlertClaimType = "alert";

    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly SymmetricSecurityKey _signingKey;

    public TokenService(IOptionsMonitor<ServerOptions> options)
    {
        _options = options;

        // The one Auth value read once rather than per call, and it has to be: the same key is
        // baked into the JWT validation parameters when the host is composed, so a key swapped
        // underneath this would sign tokens the server then refuses to accept. It is
        // environment-only for that reason, and the settings API cannot reach it.
        _signingKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(options.CurrentValue.Auth.SigningKey));
    }

    private AuthOptions Auth => _options.CurrentValue.Auth;

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(string userId, Role role) =>
        CreateToken(userId, role, TimeSpan.FromMinutes(Auth.AccessTokenMinutes), scope: null);

    /// <summary>Same lifetime as an access token, but scoped to GET-only media routes — a leaked
    /// stream token (it travels in a URL, see <c>Media/MediaEndpoints.cs</c>) can only watch video,
    /// never mutate anything.</summary>
    public (string Token, DateTimeOffset ExpiresAt) CreateStreamToken(string userId, Role role) =>
        CreateToken(userId, role, TimeSpan.FromMinutes(Auth.AccessTokenMinutes), scope: StreamScope);

    /// <summary>
    /// A stream token narrowed to one alert's poster, for the picture on a push notification.
    ///
    /// <para><b>Why it may live for hours where every other URL-borne token lives for minutes.</b>
    /// A browser fetches a notification's image itself, with no <c>Authorization</c> header
    /// available to it, and it does so whenever the notification is drawn — which may be long after
    /// the push was composed. A stream token's ten minutes routinely expires first, and the
    /// notification then shows no picture at all. Lengthening a *general* stream token to cover
    /// that would hand out hours of access to every media route on the deployment; this hands out
    /// hours of access to one JPEG.</para>
    ///
    /// <para><b>Why a separate claim rather than a third scope.</b> <c>Program.cs</c> tells its two
    /// bearer schemes apart with <c>hasStreamScope != requireStreamScope</c> — a boolean — so a
    /// token carrying some other scope value reads as <em>not</em> a stream token, is refused by the
    /// <c>StreamToken</c> scheme and is <em>accepted by the default one</em>: a picture credential
    /// would quietly become a full Serval session. Keeping <see cref="StreamScope"/> and adding a
    /// claim beside it leaves that boolean untouched. The same trap is written up at length on
    /// <see cref="GoogleHome.CameraStreamTicketService"/>, which answered it by not being a JWT at
    /// all — a choice unavailable here, since an in-memory ticket dies on restart and a notification
    /// outlives one.</para>
    ///
    /// <para>What is given up is revocation, which a JWT cannot do. The bound on that is the claim:
    /// the worst a leaked one buys is a single alert's picture until it expires.</para>
    /// </summary>
    public (string Token, DateTimeOffset ExpiresAt) CreateAlertImageToken(
        string userId, string alertId, TimeSpan lifetime) =>
        CreateToken(userId, Role.Viewer, lifetime, scope: StreamScope, alertId: alertId);

    /// <summary>
    /// Whether this principal may read <paramref name="alertId"/>'s picture. True for any ordinary
    /// credential; an alert image token names the one alert it was minted for and opens nothing else.
    ///
    /// <para>Every claim rather than the first: a policy listing two schemes authenticates both and
    /// merges the identities into one principal, so a request carrying a header token <em>and</em> an
    /// alert token in its query holds the claim either way. "No claim disagrees" is the question,
    /// not "what does the first one say".</para>
    /// </summary>
    public static bool MayViewAlert(ClaimsPrincipal principal, string alertId) =>
        principal.FindAll(AlertClaimType)
            .All(c => string.Equals(c.Value, alertId, StringComparison.Ordinal));

    private (string, DateTimeOffset) CreateToken(
        string userId, Role role, TimeSpan lifetime, string? scope, string? alertId = null)
    {
        DateTimeOffset expires = DateTimeOffset.UtcNow.Add(lifetime);

        List<Claim> claims =
        [
            new(JwtRegisteredClaimNames.Sub, userId),
            new(ClaimTypes.Role, role.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        ];

        if (scope is not null)
        {
            claims.Add(new Claim(ScopeClaimType, scope));
        }

        if (alertId is not null)
        {
            claims.Add(new Claim(AlertClaimType, alertId));
        }

        var token = new JwtSecurityToken(
            claims: claims,
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    /// <summary>
    /// A random 256-bit refresh token, URL-safe base64. Opaque on purpose — nothing about it is
    /// ever decoded, only hashed and looked up (see <see cref="RefreshTokenRepository"/>), so it
    /// carries no claims and needs no signature.
    /// </summary>
    public static string CreateRefreshTokenValue() =>
        System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>The "sub" claim, read back exactly as issued — relies on
    /// <c>JwtBearerOptions.MapInboundClaims = false</c> in Program.cs, without which the framework
    /// silently remaps "sub" to a different claim type on the way in.</summary>
    public static string? GetUserId(ClaimsPrincipal principal) =>
        principal.FindFirstValue(JwtRegisteredClaimNames.Sub);

    /// <summary>
    /// The "sub" claim on a request that has already passed authorization. Every token this
    /// server mints carries it, so on such a request it cannot be absent — and if that invariant
    /// ever breaks, a loud 500 beats a 401 nothing can reproduce.
    /// </summary>
    public static string RequireUserId(ClaimsPrincipal principal) =>
        GetUserId(principal)
        ?? throw new InvalidOperationException("An authenticated principal has no 'sub' claim.");

    public static Role GetRole(ClaimsPrincipal principal) =>
        Enum.TryParse(principal.FindFirstValue(ClaimTypes.Role), out Role role) ? role : Role.Viewer;
}
