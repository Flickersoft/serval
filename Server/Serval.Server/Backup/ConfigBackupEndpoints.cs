using System.Text.Json;
using Serval.Server.Auth;

namespace Serval.Server.Backup;

/// <summary>
/// Downloading this Server's configuration as a file, and merging one back in.
///
/// <para>Admin-only on both sides, for different reasons. The download is the only request in the
/// system that returns every camera credential and every password hash at once — before it, that
/// took access to Mongo. The upload rewrites the registry and the accounts. Both are logged with
/// the account that made them, because an export that leaves no trace would be the one privileged
/// read here that nobody can audit.</para>
/// </summary>
public static class ConfigBackupEndpoints
{
    public static void MapConfigBackupEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/config").WithTags("Configuration");

        group.MapGet("/backup", async (
            ConfigBackupBuilder builder,
            HttpContext http,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            string? actor = TokenService.GetUserId(http.User);
            ConfigBackupFile file = await builder.BuildAsync(actor, ct);

            loggers.CreateLogger(typeof(ConfigBackupEndpoints))
                .LogInformation("Configuration backup downloaded by {User}.", actor);

            // Results.File rather than the streaming shape MediaEndpoints uses for a clip: this
            // payload is known in full and measured in kilobytes, so buffering it costs nothing and
            // buys a real Content-Length alongside the attachment disposition. Content-Disposition
            // is already in the CORS exposed-header list, so the web build can read the filename.
            return Results.File(
                JsonSerializer.SerializeToUtf8Bytes(file, ConfigBackupFile.Json),
                "application/json",
                ConfigBackupFile.FileNameFor(file.CreatedAt));
        })
            .WithSummary("Download every setting, camera, account and preference as one file.")
            .WithDescription(
                "CONTAINS SECRETS. The file holds each camera's ONVIF password, any user:password "
                + "inside a stream URL, and every account's password hash, all in plain text. It is "
                + "meant to be stored wherever those passwords are stored.\n\n"
                + "Configuration only: no recorded footage, detections, transcripts or telemetry. "
                + "The settings it carries are the overridden ones — what somebody changed — not "
                + "the values that came from an environment variable or a compose file, so "
                + "restoring it onto a differently-deployed Server restores the choices rather than "
                + "overwriting that deployment.\n\n"
                + "Detection masks travel with their camera, in detectionTuning.masks.")
            .Produces<ConfigBackupFile>()
            .RequireAuthorization("Admin");

        group.MapPost("/restore", async (
            HttpContext http,
            ConfigRestoreService restore,
            CancellationToken ct) =>
        {
            ConfigBackupFile? file;
            try
            {
                // Parsed by hand rather than bound as a parameter, so a file that is not JSON gets a
                // sentence rather than the model binder's ProblemDetails — this is the one endpoint
                // whose body a person picked out of a file manager, so "that is not the right file"
                // is the most likely failure and deserves to read like one.
                file = await JsonSerializer.DeserializeAsync<ConfigBackupFile>(
                    http.Request.Body, ConfigBackupFile.Json, ct);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new
                {
                    error = "That file is not valid JSON, so it cannot be a Serval configuration backup.",
                });
            }

            if (file is null || !string.Equals(file.Kind, ConfigBackupFile.FileKind, StringComparison.Ordinal))
            {
                return Results.BadRequest(new
                {
                    error = "That file is not a Serval configuration backup.",
                });
            }

            return Results.Ok(await restore.RestoreAsync(file, TokenService.GetUserId(http.User), ct));
        })
            .WithSummary("Merge a downloaded configuration back into this Server.")
            .WithDescription(
                "MERGES, and never deletes. Every camera, setting, account and preference the file "
                + "names is created or overwritten whole; anything this Server has that the file "
                + "does not name is left exactly as it is. A camera added since the backup survives "
                + "the restore. Overwriting is not field-level: a camera in the file replaces the "
                + "one here entirely, including a password that has been rotated since.\n\n"
                + "Best-effort. A camera whose transcode codec this host's ffmpeg cannot encode, a "
                + "setting that is environment-only here, an account that would leave the Server "
                + "without an Admin — each is skipped and named in the response with the reason, "
                + "and everything else is still applied. Fix what was skipped and restore the same "
                + "file again; it is idempotent.\n\n"
                + "Your own account's role and password are never changed by a restore, so it "
                + "cannot sign you out of the Server you are restoring. Other accounts whose "
                + "password the file changes are signed out of every device.\n\n"
                + "A file from a newer version of Serval is refused outright rather than partly "
                + "applied.")
            .Produces<ConfigRestoreResult>()
            .Produces(StatusCodes.Status400BadRequest)
            .RequireAuthorization("Admin");
    }
}
