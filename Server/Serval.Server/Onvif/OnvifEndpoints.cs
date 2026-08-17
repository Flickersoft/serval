using Serval.Server.Cameras;

namespace Serval.Server.Onvif;

/// <summary>
/// What a camera says about itself over ONVIF, as distinct from what it is configured to do.
///
/// Its own route rather than a field on the camera record, for the reason
/// <c>/ptz/capabilities</c> is: reading this is a live SOAP round trip to the device, and the App
/// awaits <c>GET /api/cameras</c> before its first frame — so folding it in would put an ONVIF
/// timeout per unreachable camera on every cold start, to fill in a subtitle.
/// </summary>
public static class OnvifEndpoints
{
    public static void MapOnvifEndpoints(this IEndpointRouteBuilder app)
    {
        // Read-only device info — any authenticated user (a Viewer can see what a camera is,
        // just not change what it does).
        RouteGroupBuilder group = app.MapGroup("/api/cameras/{id}")
            .WithTags("ONVIF")
            .RequireAuthorization();

        // The shared PTZ guard fits because PtzConfigured is "an ONVIF endpoint is set" — the
        // flag is named for its first use, not its only one.
        group.MapGet("/device-information",
            (string id, bool? refresh, CameraRepository cameras, OnvifClient onvif, CancellationToken ct) =>
            Ptz.PtzEndpoints.InvokeAsync(id, cameras, ct, async (camera, token) =>
            {
                OnvifDeviceInformation information = await onvif.GetDeviceInformationAsync(camera, refresh ?? false, token);
                return Results.Ok(new
                {
                    cameraId = id,
                    information.Manufacturer,
                    information.Model,
                    information.FirmwareVersion,
                    information.SerialNumber,
                    information.HardwareId,
                    readAt = DateTimeOffset.UtcNow,
                });
            }))
            .WithSummary("Make, model and firmware, as the camera reports them.")
            .WithDescription(
                "ONVIF GetDeviceInformation, cached per camera until its ONVIF settings change; "
                + "pass ?refresh=true after a firmware update. Every field is optional — cameras "
                + "omit them despite the specification, and null means the camera did not say. "
                + "There is no uptime: ONVIF's Device service exposes the system clock, not how "
                + "long the camera has been up.");
    }
}
