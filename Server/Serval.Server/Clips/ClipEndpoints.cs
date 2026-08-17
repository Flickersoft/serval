using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Serval.Server.Auth;
using Serval.Server.Cameras;
using Serval.Server.Configuration;
using Serval.Server.Media;
using Serval.Server.Recordings;

namespace Serval.Server.Clips;

/// <summary>
/// Saved clips: keeping one, listing them, and serving the files.
///
/// A shared library with owned edits. Everyone signed in sees every clip, because the footage is
/// the household's rather than the account's; only the person who saved one, or an Admin, may
/// rename or delete it, because those are the two operations nobody else can undo.
/// </summary>
public static class ClipEndpoints
{
    public static void MapClipEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/clips").WithTags("Clips");

        group.MapGet("", async (
            string? query, string? cameraId, ClipRepository clips, CancellationToken ct) =>
        {
            List<SavedClip> saved = await clips.ListAsync(query, cameraId, ct);
            return Results.Ok(saved.Select(ClipResponse.Summary));
        })
            .WithSummary("Every saved clip, newest first.")
            .WithDescription(
                "Ordered by when the footage happened, not when it was saved — which is how a clip "
                + "gets described later.\n\n"
                + "`query` matches the clip's name and everything said inside it, case-insensitively, "
                + "as a substring. `cameraId` narrows to one camera.\n\n"
                + "Only finished clips appear. One still being written is followed through "
                + "/api/clips/{id}/status by whoever asked for it, rather than shown to everyone as a "
                + "card that cannot be played.")
            .RequireAuthorization();

        // Ids bind as ObjectId via its TryParse, so a malformed one never reaches a handler.
        group.MapGet("/{id}", async (ObjectId id, ClipRepository clips, CancellationToken ct) =>
        {
            SavedClip? clip = await clips.GetAsync(id, ct);
            return clip is { State: ClipState.Ready } ? Results.Ok(ClipResponse.Detail(clip)) : Results.NotFound();
        })
            .WithSummary("One clip, with the telemetry frozen with it.")
            .WithDescription(
                "The speech, scenes, detections and sounds as they were when the clip was saved — "
                + "copies, not a live query, so they survive the footage they describe rolling off.\n\n"
                + "Speech carries `offsetSeconds` from the start of the clip as well as its wall "
                + "clock, since a player's position is the only clock the viewer has.")
            .RequireAuthorization();

        group.MapPost("", async (
            SaveClipRequest request,
            HttpContext http,
            ClipRepository clips,
            ClipWriteWorker writer,
            CameraRepository cameras,
            RecordingIndex recordings,
            IOptionsMonitor<ServerOptions> options,
            CancellationToken ct) =>
        {
            MediaOptions media = options.CurrentValue.Media;

            if (ClipRules.RejectSave(request.CameraId, request.Name, request.From, request.To, media.ClipMaxMinutes)
                is { } refusal)
            {
                return Results.BadRequest(new { error = refusal });
            }

            string name = request.Name!.Trim();

            Camera? camera = await cameras.GetAsync(request.CameraId, ct);
            if (camera is null)
            {
                return Results.NotFound();
            }

            List<RecordingSegment> segments =
                await recordings.InRangeAsync(request.CameraId, request.From, request.To, ct);

            if (ClipRules.RejectSegments(segments) is { } noFootage)
            {
                return Results.BadRequest(new { error = noFootage });
            }

            var clip = new SavedClip
            {
                Id = ObjectId.GenerateNewId(),
                CameraId = camera.Id,
                CameraName = camera.Name,
                Name = name,
                SavedBy = TokenService.GetUserId(http.User) ?? "unknown",
                From = request.From,
                To = request.To,
                SavedAt = DateTimeOffset.UtcNow,
                DurationSeconds = (request.To - request.From).TotalSeconds,
                State = ClipState.Writing,
                SearchText = name.ToLowerInvariant(),
            };

            await clips.InsertAsync(clip, ct);
            writer.Enqueue(clip.Id);

            return Results.Accepted($"/api/clips/{clip.Id}", ClipResponse.Summary(clip));
        })
            .WithSummary("Keep a range of footage as a clip.")
            .WithDescription(
                "Returns 202 as soon as the range is accepted; the video is written in the "
                + "background. Half an hour of a main stream is gigabytes and tens of seconds of "
                + "ffmpeg, which is not a request to hold open — poll /api/clips/{id}/status.\n\n"
                + "Everything a caller can get wrong is refused here rather than later: an unknown "
                + "camera, a backwards or over-long range, a range with no footage, and a range that "
                + "crosses a recording restart.\n\n"
                + "The range should already be a whole number of recording segments — see "
                + "/api/cameras/{id}/recordings. Anything else is widened to the segments overlapping "
                + "it, because a segment is the smallest thing that can be copied without re-encoding.")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .RequireAuthorization();

        group.MapGet("/{id}/status", async (ObjectId id, ClipRepository clips, ClipStorage storage, CancellationToken ct) =>
        {
            SavedClip? clip = await clips.GetAsync(id, ct);
            if (clip is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(new
            {
                state = clip.State.ToString().ToLowerInvariant(),
                bytesWritten = clip.State == ClipState.Writing ? storage.BytesWritten(id) : clip.SizeBytes,
                sizeBytes = clip.State == ClipState.Ready ? clip.SizeBytes : (long?)null,
                error = clip.Error,
            });
        })
            .WithSummary("How far a save has got.")
            .WithDescription(
                "`state` is writing, ready or failed. `bytesWritten` is the size of the file so far — "
                + "there is no total to divide it by until the clip is finished, so this is a count "
                + "rather than a percentage.")
            .RequireAuthorization();

        group.MapPatch("/{id}", async (
            ObjectId id,
            RenameClipRequest request,
            HttpContext http,
            ClipRepository clips,
            CancellationToken ct) =>
        {
            string name = (request.Name ?? "").Trim();
            if (name.Length == 0)
            {
                return Results.BadRequest(new { error = "A clip needs a name." });
            }

            if (name.Length > ClipRules.MaxNameLength)
            {
                return Results.BadRequest(new
                {
                    error = $"That name is too long — {ClipRules.MaxNameLength} characters at most.",
                });
            }

            SavedClip? clip = await clips.GetAsync(id, ct);
            if (clip is null)
            {
                return Results.NotFound();
            }

            if (!MayEdit(http, clip))
            {
                return Forbidden("rename");
            }

            await clips.RenameAsync(id, name, ct);
            return Results.NoContent();
        })
            .WithSummary("Rename a clip.")
            .WithDescription("The person who saved it, or an Admin. Anyone else gets 403.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .RequireAuthorization();

        group.MapDelete("/{id}", async (
            ObjectId id,
            HttpContext http,
            ClipRepository clips,
            ClipStorage storage,
            CancellationToken ct) =>
        {
            SavedClip? clip = await clips.GetAsync(id, ct);
            if (clip is null)
            {
                return Results.NoContent();
            }

            if (!MayEdit(http, clip))
            {
                return Forbidden("delete");
            }

            // Files first. A row without its directory is a card that fails to play; a directory
            // without its row is bytes nothing will ever reference or reclaim.
            storage.Remove(id);
            await clips.DeleteAsync(id, ct);

            return Results.NoContent();
        })
            .WithSummary("Delete a clip and its files.")
            .WithDescription(
                "The person who saved it, or an Admin. Anyone else gets 403.\n\n"
                + "This is the only way a clip goes away — retention never touches them.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status403Forbidden)
            .RequireAuthorization();

        // The two file routes take a stream token as well as a bearer header, because the things
        // that fetch them cannot set headers: a <video> element and libmpv are handed a URL. Same
        // policy, and the same reasoning, as the HLS routes in MediaEndpoints.
        group.MapGet("/{id}/clip.mp4", async (ObjectId id, ClipRepository clips, ClipStorage storage, CancellationToken ct) =>
        {
            SavedClip? clip = await clips.GetAsync(id, ct);
            string path = storage.VideoFor(id);

            if (clip is not { State: ClipState.Ready } || !File.Exists(path))
            {
                return Results.NotFound();
            }

            // Buffered rather than the streaming shape the live export uses: this is a real file, so
            // it has a real length and can be range-served. That is what lets a player scrub it and
            // a download resume — neither of which the streamed export can offer.
            return Results.File(path, "video/mp4", ClipRules.FileNameFor(clip), enableRangeProcessing: true);
        })
            .WithSummary("The clip's video.")
            .WithDescription("A plain faststart MP4 with audio. Supports range requests.")
            .RequireAuthorization("MediaAccess");

        group.MapGet("/{id}/poster.jpg", async (ObjectId id, ClipRepository clips, ClipStorage storage, CancellationToken ct) =>
        {
            SavedClip? clip = await clips.GetAsync(id, ct);
            string path = storage.PosterFor(id);

            if (clip is not { State: ClipState.Ready } || !File.Exists(path))
            {
                return Results.NotFound();
            }

            return Results.File(path, "image/jpeg");
        })
            .WithSummary("A frame from the middle of the clip.")
            .WithDescription("404 when the poster could not be made; a clip without one still plays.")
            .RequireAuthorization("MediaAccess");
    }

    private static bool MayEdit(HttpContext http, SavedClip clip) =>
        ClipRules.MayEdit(TokenService.GetUserId(http.User), TokenService.GetRole(http.User), clip);

    private static IResult Forbidden(string verb) => Results.Json(
        new { error = $"Only the person who saved this clip, or an Admin, can {verb} it." },
        statusCode: StatusCodes.Status403Forbidden);
}

public sealed record SaveClipRequest(string CameraId, DateTimeOffset From, DateTimeOffset To, string? Name);

public sealed record RenameClipRequest(string? Name);
