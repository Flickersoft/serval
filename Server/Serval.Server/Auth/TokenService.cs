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
/// The one-time WebSocket connect ticket is deliberately not a JWT — see
/// <see cref="StreamTicketService"/> — since it needs server-side single-use enforcement a JWT
/// cannot provide on its own.
/// </summary>
public sealed class TokenService
{
    public const string ScopeClaimType = "scope";
    public const string StreamScope = "stream";

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

    private (string, DateTimeOffset) CreateToken(string userId, Role role, TimeSpan lifetime, string? scope)
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
