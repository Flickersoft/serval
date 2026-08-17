using Serval.Server.Cameras;

namespace Serval.Server.Alerts;

/// <summary>
/// The alert queue: reading it, clearing it, and serving the preview clips.
///
/// <para>Unlike telemetry, which is read one camera at a time, this is deliberately cross-camera by
/// default. An alert queue narrowed to a camera is a queue you have to already know which camera to
/// look at, which is the opposite of what being told about something is for.</para>
///
/// <para>Everyone signed in sees the same queue and may clear it, because an alert is the
/// household's rather than the account's — the same rule saved clips follow. Read and dismissed are
/// therefore properties of the alert, not of the person looking at it.</para>
/// </summary>
public static class AlertEndpoints
{
    /// <summary>
    /// Ceiling on one page of the queue, and its default. Generous because the screen has no pager:
    /// the list is meant to be scrolled to the end, and a queue nobody has cleared for a week should
    /// arrive in one response.
    /// </summary>
    private const int MaxLimit = 500;

    public static void MapAlertEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/alerts").WithTags("Alerts");

        group.MapGet("", async (
            string? cameraId,
            int? limit,
            DateTimeOffset? before,
            AlertRepository alerts,
            CameraRepository cameras,
            CancellationToken ct) =>
        {
            int take = Math.Clamp(limit ?? MaxLimit, 1, MaxLimit);

            List<Alert> queue = await alerts.ListAsync(cameraId, take, before, ct);
            long unread = await alerts.UnreadCountAsync(cameraId, ct);

            return Results.Ok(new AlertPage(await NameAsync(queue, cameras, ct), unread));
        })
            .WithSummary("The alert queue, newest first.")
            .WithDescription(
                "Every camera unless `cameraId` narrows it, and everything that has not been "
                + "dismissed. Alerts that have been seen stay in the list — clearing it is a "
                + "decision somebody makes, not something time does.\n\n"
                + "`unread` counts the whole queue rather than this page, so the header's figure "
                + "stays true when the rest is below the fold. `before` pages backwards by `at`.")
            .RequireAuthorization();

        group.MapGet("/{id}", async (
            string id, AlertRepository alerts, CameraRepository cameras, CancellationToken ct) =>
        {
            Alert? alert = await alerts.GetAsync(id, ct);
            if (alert is null)
            {
                return Results.NotFound();
            }

            Camera? camera = await cameras.GetAsync(alert.CameraId, ct);
            return Results.Ok(AlertResponse.From(alert, camera?.Name ?? alert.CameraId));
        })
            .WithSummary("One alert.")
            .WithDescription(
                "The same shape the list returns — an alert has no detail the queue does not "
                + "already carry. Dismissed alerts are still readable here, because a push "
                + "notification tapped after somebody else cleared the queue has to land somewhere.")
            .RequireAuthorization();

        group.MapPost("/{id}/read", async (string id, AlertRepository alerts, CancellationToken ct) =>
            await alerts.MarkReadAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .WithSummary("Marks an alert as seen.")
            .WithDescription(
                "Called when the card is opened. Seen is not dismissed: the row stays in the queue, "
                + "drops back visually, and stops counting towards `unread`.")
            .RequireAuthorization();

        group.MapPost("/{id}/dismiss", async (string id, AlertRepository alerts, CancellationToken ct) =>
            await alerts.DismissAsync(id, DateTimeOffset.UtcNow, ct)
                ? Results.NoContent()
                : Results.NotFound())
            .WithSummary("Clears one alert from the queue.")
            .WithDescription(
                "The row and its clip stay on disk until retention takes them — dismissing is about "
                + "attention, not evidence, so a dismissed alert is still reachable by id.")
            .RequireAuthorization();

        group.MapPost("/dismiss-all", async (
            string? cameraId, AlertRepository alerts, CancellationToken ct) =>
        {
            long cleared = await alerts.DismissAllAsync(cameraId, DateTimeOffset.UtcNow, ct);
            return Results.Ok(new { cleared });
        })
            .WithSummary("Clears the queue.")
            .WithDescription("Everything undismissed, or one camera's part of it when `cameraId` is given.")
            .RequireAuthorization();

        // The two file routes take a stream token as well as a bearer header, because the things
        // that fetch them cannot set one: a <video> element and libmpv are handed a URL. Same
        // policy, and the same reasoning, as the clip and HLS routes.
        group.MapGet("/{id}/clip.mp4", async (
            string id, AlertRepository alerts, AlertStorage storage, CancellationToken ct) =>
        {
            Alert? alert = await alerts.GetAsync(id, ct);
            string path = storage.VideoFor(id);

            if (alert is not { ClipState: AlertClipState.Ready } || !File.Exists(path))
            {
                return Results.NotFound();
            }

            return Results.File(path, "video/mp4", enableRangeProcessing: true);
        })
            .WithSummary("The alert's preview clip.")
            .WithDescription(
                "A short faststart MP4 cut from the camera's detect stream, padded either side of "
                + "the moment the alert fired. Present whether or not the camera was recording — "
                + "that is what it is for. Supports range requests.\n\n"
                + "404 while the clip is still being cut, and permanently when there was no footage "
                + "to cut it from; the alert's `clip_state` says which.")
            .RequireAuthorization("MediaAccess");

        // The file alone decides this, deliberately — the clip state does not.
        //
        // An alert gets a poster when it is raised, from the camera's live snapshot, and that is
        // replaced by the exact frame once there is footage to cut one from. Gating on
        // ClipState.Ready would 404 the stand-in for the whole sixteen seconds it exists to cover,
        // and would go on 404ing forever for an alert there was never any footage for — which still
        // had a picture at the moment it fired.
        // No repository read either: the id is sanitised by AlertStorage on the way to a path, which
        // is where that check belongs, and the file existing is the whole question.
        group.MapGet("/{id}/poster.jpg", (string id, AlertStorage storage) =>
        {
            string path = storage.PosterFor(id);
            return File.Exists(path) ? Results.File(path, "image/jpeg") : Results.NotFound();
        })
            .WithSummary("The frame the alert fired on.")
            .WithDescription(
                "Not a poster from the middle of the clip: this is the moment of detection, which "
                + "is the frame the alert's `box` describes. Draw the box over it rather than "
                + "expecting it to be drawn in.\n\n"
                + "Present from the moment the alert is raised. Until the clip settles this is the "
                + "camera's live snapshot from that moment, which is within a second of the frame "
                + "that fired rather than exactly it; the `box` is measured against the exact one, "
                + "so it can sit slightly off its subject until `clip_state` reaches `ready`.")
            .RequireAuthorization("MediaAccess");
    }

    /// <summary>
    /// Attaches each alert's camera name.
    ///
    /// One registry read for the whole page rather than one per row — a queue is mostly a handful of
    /// cameras repeated, and the alternative is a Mongo round trip per alert to answer the same
    /// question fifty times.
    /// </summary>
    private static async Task<IReadOnlyList<AlertResponse>> NameAsync(
        List<Alert> queue, CameraRepository cameras, CancellationToken cancellationToken)
    {
        if (queue.Count == 0)
        {
            return [];
        }

        Dictionary<string, string> names = (await cameras.ListAsync(cancellationToken))
            .ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);

        return
        [
            .. queue.Select(a => AlertResponse.From(
                a, names.TryGetValue(a.CameraId, out string? name) ? name : a.CameraId)),
        ];
    }
}
