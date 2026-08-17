using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Serval.Server.Configuration;
using Serval.Server.Preferences;

namespace Serval.Server.Auth;

/// <summary>
/// Login, token refresh, logout, the two ticket-minting routes the WebSocket/HLS endpoints
/// depend on (see <see cref="StreamTicketService"/> and <see cref="TokenService"/> for why those
/// exist), Admin-only account management, and the two things any account may do to itself: change
/// its own password and end its own sessions. There is no self-registration — accounts are created
/// either by the startup bootstrap (see <c>Program.cs</c>) or by an existing Admin here.
///
/// Ending a session means revoking a refresh token, which is stored, and not the access token,
/// which is not. So "signed out everywhere" takes effect within the access token's lifetime rather
/// than instantly. Shortening that window means checking a revocation list on every request, which
/// is a per-request database read to close ten minutes.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting("login")
            .WithSummary("Exchange a username and password for an access token and a refresh token.");

        group.MapPost("/refresh", RefreshAsync)
            .AllowAnonymous()
            .WithSummary("Exchange a refresh token for a new access token, rotating the refresh token. "
                + "A revoked or expired token being presented here revokes its whole rotation chain — "
                + "the signal that it was likely stolen.");

        group.MapPost("/logout", LogoutAsync)
            .AllowAnonymous()
            .WithSummary("Revokes a refresh token, ending that session.");

        group.MapPost("/ws-ticket", WsTicketAsync)
            .RequireAuthorization()
            .WithSummary("Mints a single-use, ~30s ticket for the dashboard/events/audio-levels "
                + "WebSocket endpoints, which cannot carry an Authorization header. Pass it back as ?ticket=.");

        group.MapPost("/stream-token", StreamTokenAsync)
            .RequireAuthorization()
            .WithSummary("Mints a short-lived, GET-only-scoped token for video playback URLs, which "
                + "cannot carry an Authorization header. Pass it back as ?stream_token=.");

        group.MapPut("/password", ChangeOwnPasswordAsync)
            .RequireAuthorization()
            .RequireRateLimiting("login")
            .WithSummary("Change your own password.")
            .WithDescription(
                "Requires the current password even though the caller is already authenticated: an "
                + "access token lasts ten minutes and a stolen one must not be convertible into a "
                + "permanent account takeover. Set signOutAllSessions to end every other session at "
                + "the same time — the right answer when the reason for changing it is that someone "
                + "else may know it.");

        RouteGroupBuilder users = group.MapGroup("/users").RequireAuthorization("Admin").WithTags("Auth");

        users.MapGet("", ListUsersAsync).WithSummary("List every account.");
        users.MapPost("", CreateUserAsync).WithSummary("Create an account.");
        users.MapPut("/{username}", UpdateUserAsync)
            .WithSummary("Change an account's role and/or password.")
            .WithDescription(
                "Setting a password does not end that account's existing sessions unless "
                + "signOutAllSessions is true. It usually should be: a password reset that leaves "
                + "the old sessions running does not remove whoever prompted the reset, and their "
                + "refresh token is good for another 30 days.");
        users.MapPost("/{username}/sign-out", SignOutUserAsync)
            .WithSummary("End every session for one account.");
        users.MapDelete("/{username}", DeleteUserAsync)
            .WithSummary("Delete an account. Refuses to delete the last remaining Admin.");
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        UserRepository users,
        RefreshTokenRepository refreshTokens,
        TokenService tokens,
        IOptionsMonitor<ServerOptions> options,
        CancellationToken ct)
    {
        User? existing = await users.GetAsync(request.Username, ct);

        // Checked before touching the password hash at all — a locked account shouldn't pay the
        // PBKDF2 cost, and a locked-out household member gets told so rather than a generic error
        // (an accepted account-enumeration tradeoff for a closed household system).
        if (existing is not null && UserRepository.IsLocked(existing))
        {
            return Results.Json(
                new { error = $"Account '{existing.Id}' is locked until {existing.LockedUntil:O}." },
                statusCode: StatusCodes.Status423Locked);
        }

        User? user = await users.VerifyPasswordAsync(request.Username, request.Password, ct);
        if (user is null)
        {
            // Only record a failure against an account that exists — recording against a
            // nonexistent one would be observable (a second GetAsync inside RecordFailedLoginAsync
            // no-ops) and gains nothing, since there's no account to protect.
            if (existing is not null)
            {
                await users.RecordFailedLoginAsync(request.Username, ct);
            }

            return Results.Unauthorized();
        }

        await users.ClearFailedLoginsAsync(user.Id, ct);
        TokenResponse response = await IssueTokensAsync(
            user, Guid.NewGuid(), refreshTokens, tokens, options.CurrentValue.Auth, ct);
        return Results.Ok(response);
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        UserRepository users,
        RefreshTokenRepository refreshTokens,
        TokenService tokens,
        IOptionsMonitor<ServerOptions> options,
        CancellationToken ct)
    {
        RefreshToken? existing = await refreshTokens.GetByRawTokenAsync(request.RefreshToken, ct);
        if (existing is null)
        {
            return Results.Unauthorized();
        }

        if (existing.RevokedAt is not null || existing.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            await refreshTokens.RevokeFamilyAsync(existing.FamilyId, ct);
            return Results.Unauthorized();
        }

        User? user = await users.GetAsync(existing.UserId, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        TokenResponse response = await IssueTokensAsync(
            user, existing.FamilyId, refreshTokens, tokens, options.CurrentValue.Auth, ct, replaces: existing);
        return Results.Ok(response);
    }

    private static async Task<IResult> LogoutAsync(
        LogoutRequest request, RefreshTokenRepository refreshTokens, CancellationToken ct)
    {
        RefreshToken? existing = await refreshTokens.GetByRawTokenAsync(request.RefreshToken, ct);
        if (existing is { RevokedAt: null })
        {
            await refreshTokens.RevokeAsync(existing.Id, replacedByTokenId: null, ct);
        }

        return Results.NoContent();
    }

    private static async Task<TokenResponse> IssueTokensAsync(
        User user,
        Guid familyId,
        RefreshTokenRepository refreshTokens,
        TokenService tokens,
        AuthOptions options,
        CancellationToken ct,
        RefreshToken? replaces = null)
    {
        (string accessToken, DateTimeOffset accessExpires) = tokens.CreateAccessToken(user.Id, user.Role);

        string rawRefreshToken = TokenService.CreateRefreshTokenValue();
        DateTimeOffset refreshExpires = DateTimeOffset.UtcNow.AddDays(options.RefreshTokenDays);
        var refreshToken = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = RefreshTokenRepository.Hash(rawRefreshToken),
            FamilyId = familyId,
            ExpiresAt = refreshExpires,
        };
        await refreshTokens.InsertAsync(refreshToken, ct);

        // Mark the token being rotated away from as spent, pointing at what replaced it, only
        // after the new one exists — so a crash between insert and revoke leaves the old token
        // still valid rather than both dead.
        if (replaces is not null)
        {
            await refreshTokens.RevokeAsync(replaces.Id, refreshToken.Id, ct);
        }

        return new TokenResponse(
            accessToken, accessExpires, rawRefreshToken, refreshExpires, user.Id, user.Role);
    }

    private static IResult WsTicketAsync(HttpContext context, StreamTicketService tickets)
    {
        string ticket = tickets.Mint(
            TokenService.RequireUserId(context.User), TokenService.GetRole(context.User));
        return Results.Ok(new TicketResponse(ticket, DateTimeOffset.UtcNow.AddSeconds(30)));
    }

    private static IResult StreamTokenAsync(HttpContext context, TokenService tokens)
    {
        (string token, DateTimeOffset expires) = tokens.CreateStreamToken(
            TokenService.RequireUserId(context.User), TokenService.GetRole(context.User));
        return Results.Ok(new StreamTokenResponse(token, expires));
    }

    /// <summary>
    /// Self-service password change. The current password is checked here rather than trusted from
    /// the token: being signed in proves the session was opened at some point, not that whoever is
    /// holding it now knows the password.
    /// </summary>
    private static async Task<IResult> ChangeOwnPasswordAsync(
        ChangePasswordRequest request,
        HttpContext context,
        UserRepository users,
        RefreshTokenRepository refreshTokens,
        CancellationToken ct)
    {
        string userId = TokenService.RequireUserId(context.User);

        // Same generic answer for a wrong current password as login gives for a wrong password,
        // and no lockout is recorded — the account is already authenticated, so a wrong guess here
        // is far more likely a typo than an attack, and locking on it would let a stolen session
        // lock the real owner out.
        if (await users.VerifyPasswordAsync(userId, request.CurrentPassword, ct) is null)
        {
            return Results.Unauthorized();
        }

        if (!await users.SetPasswordAsync(userId, request.NewPassword, ct))
        {
            return Results.NotFound();
        }

        if (request.SignOutAllSessions)
        {
            await refreshTokens.RevokeAllForUserAsync(userId, ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> SignOutUserAsync(
        string username, UserRepository users, RefreshTokenRepository refreshTokens, CancellationToken ct)
    {
        if (await users.GetAsync(username, ct) is not { } user)
        {
            return Results.NotFound();
        }

        await refreshTokens.RevokeAllForUserAsync(user.Id, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListUsersAsync(UserRepository users, CancellationToken ct) =>
        Results.Ok((await users.ListAsync(ct)).Select(ToResponse));

    private static async Task<IResult> CreateUserAsync(
        CreateUserRequest request, UserRepository users, CancellationToken ct)
    {
        User user = await users.CreateAsync(
            request.Username, request.DisplayName ?? request.Username, request.Password, request.Role, ct);
        return Results.Created($"/api/auth/users/{user.Id}", ToResponse(user));
    }

    private static async Task<IResult> UpdateUserAsync(
        string username,
        UpdateUserRequest request,
        UserRepository users,
        RefreshTokenRepository refreshTokens,
        CancellationToken ct)
    {
        if (request.Password is { } password && !await users.SetPasswordAsync(username, password, ct))
        {
            return Results.NotFound();
        }

        if (request.Role is { } role && !await users.UpdateRoleAsync(username, role, ct))
        {
            return Results.NotFound();
        }

        User? user = await users.GetAsync(username, ct);
        if (user is null)
        {
            return Results.NotFound();
        }

        // After the change, not before: a revocation that ran first would leave the sessions
        // dead and the old password still working if the write below failed.
        if (request.SignOutAllSessions)
        {
            await refreshTokens.RevokeAllForUserAsync(user.Id, ct);
        }

        return Results.Ok(ToResponse(user));
    }

    private static async Task<IResult> DeleteUserAsync(
        string username, UserRepository users, UserPreferencesRepository preferences, CancellationToken ct)
    {
        if (!await users.DeleteAsync(username, ct))
        {
            return Results.NotFound();
        }

        // After the account is gone, and only then: a username can be created again, and
        // inheriting the previous holder's wall would be somebody else's arrangement appearing
        // on a brand new account. Not awaited before the delete because a preferences failure
        // must not leave the account itself standing.
        await preferences.DeleteAsync(UserRepository.Normalize(username), ct);
        return Results.NoContent();
    }

    private static UserResponse ToResponse(User user) =>
        new(user.Id, user.DisplayName, user.Role, user.CreatedAt);
}

public sealed record LoginRequest(string Username, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(string RefreshToken);

public sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    string Username,
    Role Role);

public sealed record TicketResponse(string Ticket, DateTimeOffset ExpiresAt);

public sealed record StreamTokenResponse(string Token, DateTimeOffset ExpiresAt);

public sealed record CreateUserRequest(string Username, string? DisplayName, string Password, Role Role);

/// <summary>
/// <paramref name="SignOutAllSessions"/> defaults to false so that changing only a role leaves the
/// account signed in, which is the common edit. An Admin setting a password should generally send
/// true — see the endpoint description.
/// </summary>
public sealed record UpdateUserRequest(Role? Role, string? Password, bool SignOutAllSessions = false);

/// <summary>
/// A self-service password change. <paramref name="CurrentPassword"/> is required — see
/// <c>PUT /api/auth/password</c> for why an authenticated caller still has to prove it.
/// </summary>
public sealed record ChangePasswordRequest(
    string CurrentPassword, string NewPassword, bool SignOutAllSessions = false);

public sealed record UserResponse(string Username, string DisplayName, Role Role, DateTimeOffset CreatedAt);
