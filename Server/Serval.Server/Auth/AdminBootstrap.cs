using Serval.Server.Configuration;

namespace Serval.Server.Auth;

/// <summary>
/// Creates the first Admin account on an empty <c>Users</c> collection, from
/// <c>Serval:Auth:BootstrapAdminUsername</c>/<c>BootstrapAdminPassword</c> — the only way in,
/// since there is no self-registration and no admin UI until an Admin already exists. Runs once
/// at startup, after <c>MongoContext.InitializeAsync</c>, the same place <c>CameraRegistrySweep</c> runs.
/// </summary>
public static class AdminBootstrap
{
    public static async Task RunAsync(
        UserRepository users, AuthOptions options, ILogger logger, CancellationToken cancellationToken)
    {
        long existingUsers = await users.CountAsync(cancellationToken);

        if (existingUsers > 0)
        {
            // Ignored rather than reapplied — a stale BootstrapAdminPassword left in a compose
            // file must not silently reset a real admin's password on every restart. Same
            // treatment Program.cs already gives other config keys that are only safe to read once.
            if (!string.IsNullOrEmpty(options.BootstrapAdminUsername)
                || !string.IsNullOrEmpty(options.BootstrapAdminPassword))
            {
                logger.LogWarning(
                    "Serval:Auth:BootstrapAdmin* is set but the Users collection already has "
                    + "{Count} account(s); ignoring it. Manage passwords via POST /api/auth/users "
                    + "or PUT /api/auth/users/{{username}} instead.", existingUsers);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(options.BootstrapAdminUsername)
            || string.IsNullOrWhiteSpace(options.BootstrapAdminPassword))
        {
            // Not fatal: cameras should keep recording even with no admin configured yet. But
            // this is impossible to miss, because it means the API is currently unusable.
            logger.LogWarning(
                "No user accounts exist, and Serval:Auth:BootstrapAdminUsername/BootstrapAdminPassword "
                + "are not both set. Every endpoint except telemetry ingest and the liveness probe "
                + "is unreachable until an Admin account exists. Set both and restart.");
            return;
        }

        try
        {
            await users.CreateAsync(
                options.BootstrapAdminUsername, options.BootstrapAdminUsername,
                options.BootstrapAdminPassword, Role.Admin, cancellationToken);
            logger.LogInformation(
                "Created bootstrap Admin account '{Username}'.",
                options.BootstrapAdminUsername.Trim().ToLowerInvariant());
        }
        catch (AuthValidationException ex)
        {
            logger.LogError(
                "Serval:Auth:BootstrapAdminUsername/BootstrapAdminPassword failed validation: "
                + "{Message} No Admin account was created.", ex.Message);
        }
    }
}
