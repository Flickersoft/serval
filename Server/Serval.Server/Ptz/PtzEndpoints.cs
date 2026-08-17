using Serval.Server.Cameras;
using Serval.Server.Onvif;

namespace Serval.Server.Ptz;

/// <summary>
/// Pan/tilt/zoom control for the focused camera view, translated to ONVIF. Three verbs cover the
/// interaction: <c>move</c> (start a continuous move), <c>stop</c> (halt it), and <c>preset</c>
/// (recall a stored position). A held on-screen button re-sends <c>move</c> on a repeat and fires
/// <c>stop</c> on release; the move also auto-stops on the camera after a short timeout as a safety
/// net (see <see cref="Serval.Server.Configuration.PtzOptions"/>).
///
/// This pairs with the low-latency WebRTC view — PTZ is only usable when you can see the result
/// immediately — but is otherwise independent of it: the commands go straight to the camera.
/// </summary>
public static class PtzEndpoints
{
    public static void MapPtzEndpoints(this IEndpointRouteBuilder app)
    {
        // Any signed-in account: pointing a camera is operating it, not configuring it. The move is
        // transient and reversible, it belongs to the person watching the picture, and it sits on
        // the same screen as talk-back, which every role already has.
        RouteGroupBuilder group = app.MapGroup("/api/cameras/{id}/ptz")
            .WithTags("PTZ")
            .RequireAuthorization();

        group.MapPost("/move", (string id, PtzMoveRequest body, CameraRepository cameras, OnvifClient ptz, CancellationToken ct) =>
            InvokeAsync(id, cameras, ct, (camera, token) =>
                ptz.ContinuousMoveAsync(camera, body.Pan, body.Tilt, body.Zoom, token)));

        group.MapPost("/stop", (string id, CameraRepository cameras, OnvifClient ptz, CancellationToken ct) =>
            InvokeAsync(id, cameras, ct, (camera, token) => ptz.StopAsync(camera, token)));

        group.MapPost("/zoom", (string id, PtzZoomRequest body, CameraRepository cameras, OnvifClient ptz, CancellationToken ct) =>
            body.Position is < 0 or > 1 or double.NaN
                ? Task.FromResult(Results.BadRequest(new { error = "Position must be between 0 and 1." }))
                : InvokeAsync(id, cameras, ct, (camera, token) =>
                    ptz.AbsoluteZoomAsync(camera, body.Position, token)))
            .WithSummary("Drive zoom to an absolute position, 0..1.")
            .WithDescription(
                "Only for cameras whose /capabilities reports absoluteZoom; others answer 502 with "
                + "the camera's own refusal, and should be driven with /move instead. The position "
                + "is a fraction of the lens's travel in ONVIF's generic zoom space — not a "
                + "magnification, which ONVIF does not publish.");

        group.MapPost("/preset", (string id, PtzPresetRequest body, CameraRepository cameras, OnvifClient ptz, CancellationToken ct) =>
            string.IsNullOrWhiteSpace(body.Preset)
                ? Task.FromResult(Results.BadRequest(new { error = "A preset token is required." }))
                : InvokeAsync(id, cameras, ct, (camera, token) => ptz.GotoPresetAsync(camera, body.Preset, token)));

        group.MapPost("/home", (string id, CameraRepository cameras, OnvifClient ptz, CancellationToken ct) =>
            InvokeAsync(id, cameras, ct, (camera, token) => ptz.GotoHomeAsync(camera, token)))
            .WithSummary("Recall the camera's home position.")
            .WithDescription(
                "Only for cameras whose /capabilities reports home; others answer 502 with the "
                + "camera's own refusal.");

        // Deliberately its own route rather than a field on the camera record. Probing is a live
        // SOAP round trip to the camera, and the App awaits GET /api/cameras before its first
        // frame — so one unplugged camera would put an ONVIF timeout on every cold start. It is
        // also only ever wanted for the one camera being watched.
        group.MapGet("/capabilities",
            (string id, bool? refresh, CameraRepository cameras, OnvifClient ptz, CancellationToken ct) =>
            InvokeAsync(id, cameras, ct, async (camera, token) =>
            {
                PtzCapabilities capabilities = await ptz.GetCapabilitiesAsync(camera, refresh ?? false, token);
                return Results.Ok(new
                {
                    cameraId = id,
                    capabilities.PanTilt,
                    capabilities.Zoom,
                    capabilities.AbsoluteZoom,
                    capabilities.Home,
                    capabilities.MaximumPresets,
                    presets = capabilities.Presets.Select(p => new { p.Token, p.Name }),
                    capabilities.ProfileToken,
                    capabilities.NodeToken,
                    probedAt = DateTimeOffset.UtcNow,
                });
            }))
            .WithSummary("What this camera's PTZ can actually do.")
            .WithDescription(
                "Asked of the camera over ONVIF (GetNodes + GetPresets) and cached for "
                + "Serval:Ptz:CapabilityCacheMinutes; pass ?refresh=true to re-probe. panTilt and "
                + "zoom are decided on the continuous *velocity* spaces, because that is how both "
                + "axes are driven; absoluteZoom additionally reads the absolute zoom space.");

        // Uncached, unlike /capabilities: the point of this route is that it disagrees with what we
        // last commanded. Every axis is nullable — GetStatus carries an optional Position, and a
        // camera that omits it has told us nothing, which is not the same as telling us zero.
        group.MapGet("/status", (string id, CameraRepository cameras, OnvifClient ptz, CancellationToken ct) =>
            InvokeAsync(id, cameras, ct, async (camera, token) =>
            {
                try
                {
                    PtzStatus status = await ptz.GetStatusAsync(camera, token);
                    return Results.Ok(new
                    {
                        cameraId = id,
                        status.Zoom,
                        status.Pan,
                        status.Tilt,
                        readAt = DateTimeOffset.UtcNow,
                    });
                }
                catch (OnvifNotSupportedException)
                {
                    // The camera answered and its answer was "I do not do GetStatus". That is a
                    // fact about the camera, not a gateway failure, and it lands the App on dead
                    // reckoning exactly as a camera that omits Position does.
                    return Results.Ok(new
                    {
                        cameraId = id,
                        PtzStatus.Unknown.Zoom,
                        PtzStatus.Unknown.Pan,
                        PtzStatus.Unknown.Tilt,
                        readAt = DateTimeOffset.UtcNow,
                    });
                }
            }))
            .WithSummary("Where the camera says its lens is.")
            .WithDescription(
                "A live ONVIF GetStatus, never cached. zoom is 0..1 and pan/tilt are -1..1, in "
                + "ONVIF's generic position spaces; an axis the camera does not report — or "
                + "reports in a vendor space whose range is undefined — comes back null and must "
                + "be rendered as unknown rather than as a position. There is no magnification "
                + "field and there cannot be: ONVIF does not publish the optical range.");
    }

    /// <summary>One sentence for every ONVIF-backed route, so the App has a single condition to
    /// recognise rather than several spellings of it. It keys on the 400, but a person reads this.</summary>
    private static string NoPtzMessage(string id) =>
        $"Camera '{id}' has no ONVIF endpoint configured.";

    /// <summary>
    /// Shared guard + error handling for every route here: validate the id, require a PTZ-capable
    /// camera, run the ONVIF call, and map an ONVIF failure to 502 (the camera, not us, failed).
    /// </summary>
    internal static async Task<IResult> InvokeAsync(
        string id, CameraRepository cameras, CancellationToken ct,
        Func<Camera, CancellationToken, Task<IResult>> command)
    {
        if (!CameraRepository.IsSafeId(id))
        {
            return Results.NotFound();
        }

        Camera? camera = await cameras.GetAsync(id, ct);
        if (camera is null)
        {
            return Results.NotFound();
        }

        if (!camera.PtzConfigured)
        {
            return Results.BadRequest(new { error = NoPtzMessage(id) });
        }

        try
        {
            return await command(camera, ct);
        }
        catch (OnvifException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>The three fire-and-forget verbs share the guard and answer 204.</summary>
    private static Task<IResult> InvokeAsync(
        string id, CameraRepository cameras, CancellationToken ct, Func<Camera, CancellationToken, Task> command) =>
        InvokeAsync(id, cameras, ct, async (camera, token) =>
        {
            await command(camera, token);
            return Results.NoContent();
        });
}

/// <summary>Continuous-move velocities, each -1..1 (positive pan = right, tilt = up, zoom = in). Clamped server-side.</summary>
public sealed record PtzMoveRequest(double Pan, double Tilt, double Zoom);

/// <summary>An ONVIF preset token to recall.</summary>
public sealed record PtzPresetRequest(string Preset);

/// <summary>
/// An absolute zoom position, 0..1 in ONVIF's generic zoom space — a fraction of the lens's travel,
/// not a magnification.
/// </summary>
public sealed record PtzZoomRequest(double Position);
