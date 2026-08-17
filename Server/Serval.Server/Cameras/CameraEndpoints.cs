using System.Security.Claims;
using Serval.Server.Auth;

namespace Serval.Server.Cameras;

/// <summary>
/// REST over the camera registry. The ingest manager reconciles against this registry on its
/// own loop, so adding, disabling, or deleting a camera here is all that's needed to start or
/// stop its stream — no explicit wiring from these handlers.
///
/// These are also the operations driven by hand from the Scalar UI (<c>/scalar/v1</c>), so they
/// carry response types and summaries the other endpoint groups don't need: the generated schema
/// for <see cref="Camera"/> is what turns "add a camera" into a filled-in form.
/// </summary>
public static class CameraEndpoints
{
    public static void MapCameraEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/cameras").WithTags("Cameras");

        // Both reads are open to any signed-in account, and both strip the camera's credentials for
        // everyone below Admin — see Camera.WithoutSecrets. The writes below stay Admin-only and
        // echo back what the Admin sent, so the settings form still round-trips a password it just
        // saved without needing to read one back.
        group.MapGet("", async (ClaimsPrincipal user, CameraRepository repository, CancellationToken ct) =>
        {
            List<Camera> cameras = await repository.ListAsync(ct);
            return Results.Ok(TokenService.GetRole(user) == Role.Admin
                ? cameras
                : [.. cameras.Select(camera => camera.WithoutSecrets())]);
        })
            .WithSummary("List every registered camera.")
            .WithDescription(
                "Camera credentials — onvifPassword, and any user:password in a stream url — are "
                + "returned only to an Admin. Every other role sees them removed.")
            .Produces<IReadOnlyList<Camera>>()
            .RequireAuthorization();

        group.MapGet("/{id}", async (
            string id, ClaimsPrincipal user, CameraRepository repository, CancellationToken ct) =>
            await repository.GetAsync(id, ct) is { } camera
                ? Results.Ok(TokenService.GetRole(user) == Role.Admin ? camera : camera.WithoutSecrets())
                : Results.NotFound())
            .WithSummary("Fetch one camera by id.")
            .WithDescription(
                "Camera credentials — onvifPassword, and any user:password in a stream url — are "
                + "returned only to an Admin. Every other role sees them removed.")
            .Produces<Camera>()
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        group.MapPost("", async (Camera camera, CameraRepository repository, CancellationToken ct) =>
        {
            Camera created = await repository.CreateAsync(camera, ct);
            return Results.Created($"/api/cameras/{created.Id}", created);
        })
            .WithSummary("Register a camera and start ingesting it.")
            .WithDescription(
                "Requires an id (letters, digits, '-' and '_' only — it becomes a directory name "
                + "and a URL segment), a name, and at least one stream.\n\n"
                + "ROLES. Exactly one stream must carry 'detect' (snapshots, motion, the dashboard "
                + "wall, the AI) and one 'live' (the WebRTC focused view); at most one carries "
                + "'record' (written to disk). A single-stream camera declares "
                + "[\"record\",\"detect\",\"live\"] on its one stream. A camera offering a sub "
                + "stream typically gives it [\"detect\"] and keeps [\"record\",\"live\"] on the "
                + "main, which is what lets a 4K main stream be recorded untouched while motion and "
                + "AI run on something cheap. A stream may carry no roles at all: it is stored and "
                + "never pulled, which is how a source is held out of service without losing its "
                + "address.\n\n"
                + "NOT RECORDING, TWO WAYS. Set \"recording\": false to stop writing while leaving "
                + "the 'record' role where it is — a switch, so turning it back on needs no other "
                + "decision. Or leave 'record' off every stream, for a camera that is never meant "
                + "to keep anything. Either way the camera is watched and viewable live but nothing "
                + "is written — no playback, no timeline, no clip export, and recordAudio stops "
                + "meaning anything. Footage already on disk stays playable and still expires under "
                + "retentionDays. 'recording' defaults to true and may not be true with no 'record' "
                + "stream, so a watch-only camera has to say \"recording\": false rather than "
                + "arriving there by omission.\n\n"
                + "CODECS. Video is recorded exactly as the camera sends it — no decode, no "
                + "re-encode — provided the codec is one Serval:Ingest:VideoPassthroughCodecs "
                + "lists. A camera sending anything else is rejected at ingest with the codec "
                + "named, never silently transcoded. To re-encode a camera, set "
                + "\"transcode\": { \"codec\": \"h264\", \"bitrate\": \"4M\" } on its record "
                + "stream; the codec is validated against the encoders this host's ffmpeg actually "
                + "has, so an impossible request is a 400 here rather than a camera that never "
                + "records.\n\n"
                + "Stream URLs may be rtsp(s), http(s) (including HTTP-FLV), rtmp(s), srt, or a "
                + "local file path. A file URL on the 'live' stream is accepted but has no WebRTC "
                + "view — go2rtc cannot serve a file.")
            .Produces<Camera>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .RequireAuthorization("Admin");

        group.MapPut("/{id}", async (string id, Camera camera, CameraRepository repository, CancellationToken ct) =>
        {
            camera.Id = id; // the URL is authoritative; a mismatched body id can't reassign it
            return await repository.UpdateAsync(camera, ct)
                ? Results.Ok(camera)
                : Results.NotFound();
        })
            .WithSummary("Replace a camera's settings.")
            .WithDescription("The whole camera is replaced, not merged — send every field you want kept.")
            .Produces<Camera>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status400BadRequest)
            .RequireAuthorization("Admin");

        group.MapDelete("/{id}", async (string id, CameraRepository repository, CancellationToken ct) =>
            await repository.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .WithSummary("Unregister a camera and stop its stream.")
            .WithDescription("Recorded media on disk is left alone; the retention sweep ages it out.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization("Admin");
    }
}
