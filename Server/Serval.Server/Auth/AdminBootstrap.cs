using Serval.Server.Configuration;

namespace Serval.Server.Auth;

/// <summary>
/// Creates the first Admin account on an empty <c>Users</c> collection, from
/// <c>Serval:Auth:BootstrapAdminUsername</c>/<c>BootstrapAdminPassword</c>, for a deployment that
/// wants the account in place before anyone opens the App. Without them the setup page creates it
/// instead (<see cref="FirstAdminSetup"/>). Runs once at startup, after
/// <c>MongoContext.InitializeAsync</c>, the same place <c>CameraRegistrySweep</c> runs.
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
            // The password alone: compose files default the username to "admin", which says
            // nothing about anyone having left a credential behind.
            if (!string.IsNullOrEmpty(options.BootstrapAdminPassword))
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
            // The normal first boot: the browser that opens the App first is sent to the setup page
            // (FirstAdminSetup). Logged as a warning because until then nobody can sign in.
            logger.LogWarning(
                "No user accounts exist yet. Open this server's address in a browser to create the "
                + "first Admin account, or set Serval:Auth:BootstrapAdminUsername/BootstrapAdminPassword "
                + "and restart to create it without the setup page.");
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
