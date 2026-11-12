using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Serval.Server.Configuration;
using Serval.Server.Storage;

namespace Serval.Server.Auth;

/// <summary>
/// The setup page's two routes: whether this server still needs its first Admin, and creating it.
///
/// <para><b>Open only while there are no accounts at all.</b> Once one exists — from here, from
/// <c>Serval:Auth:BootstrapAdmin*</c>, or from a restored backup — both routes report that setup is
/// done and the create route refuses. That is the whole security model: on a fresh install the first
/// person to open the page owns the server, as with any self-hosted app that asks for its admin in the
/// browser. An operator who wants the account in place before the port is reachable sets the
/// bootstrap variables instead, and this page never appears.</para>
///
/// <para><b>One winner, even for simultaneous requests.</b> Counting users and then creating one
/// leaves a window where two browsers both see zero, so the create takes an insert-once claim document
/// first and only the request whose insert succeeds goes on. A claim whose account never got created —
/// the process died between the two — is ignored once it is a minute old, so it cannot lock setup out
/// forever.</para>
/// </summary>
public static class FirstAdminSetup
{
    /// <summary>The claim document's id in <c>server_secrets</c>.</summary>
    public const string ClaimId = "first-admin-claim";

    private static readonly TimeSpan StaleClaim = TimeSpan.FromMinutes(1);

    public static void MapFirstAdminSetup(this RouteGroupBuilder auth)
    {
        auth.MapGet("/setup", GetStatusAsync)
            .AllowAnonymous()
            .WithSummary("Whether this server still needs its first Admin account.");

        auth.MapPost("/setup", CreateFirstAdminAsync)
            .AllowAnonymous()
            .RequireRateLimiting("login")
            .WithSummary("Create the first Admin account and sign in as it.")
            .WithDescription(
                "Only while no account exists; 409 once one does. Returns the same tokens as a "
                + "login, so the browser that finished setup is signed in.");
    }

    private static async Task<SetupStatusResponse> GetStatusAsync(UserRepository users, CancellationToken ct) =>
        new(Required: await users.CountAsync(ct) == 0);

    private static async Task<IResult> CreateFirstAdminAsync(
        SetupRequest request,
        UserRepository users,
        MongoContext mongo,
        RefreshTokenRepository refreshTokens,
        TokenService tokens,
        IOptionsMonitor<ServerOptions> options,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        // Validated before the claim, so a too-short password does not use up the one attempt.
        UserRepository.Validate(request.Username, request.Password);

        if (await users.CountAsync(ct) > 0)
        {
            return AlreadySetUp();
        }

        IMongoCollection<ServerSecretDocument> secrets = mongo.ServerSecrets;
        await secrets.DeleteOneAsync(
            s => s.Id == ClaimId && s.CreatedAt < DateTimeOffset.UtcNow - StaleClaim, ct);

        try
        {
            await secrets.InsertOneAsync(
                new ServerSecretDocument
                {
                    Id = ClaimId,
                    Value = UserRepository.Normalize(request.Username),
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                options: null,
                ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return AlreadySetUp();
        }

        User user;
        try
        {
            user = await users.CreateAsync(
                request.Username, request.Username, request.Password, Role.Admin, ct);
        }
        catch
        {
            // Nothing was created, so the next attempt must not find the claim taken.
            await secrets.DeleteOneAsync(s => s.Id == ClaimId, CancellationToken.None);
            throw;
        }

        loggers.CreateLogger("Serval.Server.Auth.Bootstrap").LogInformation(
            "Created the first Admin account '{Username}' from the setup page.", user.Id);

        TokenResponse response = await AuthEndpoints.IssueTokensAsync(
            user, Guid.NewGuid(), refreshTokens, tokens, options.CurrentValue.Auth, ct);
        return Results.Ok(response);
    }

    private static IResult AlreadySetUp() =>
        Results.Json(
            new { error = "This server already has an Admin account. Sign in instead." },
            statusCode: StatusCodes.Status409Conflict);
}

public sealed record SetupStatusResponse(bool Required);

public sealed record SetupRequest(string Username, string Password);
