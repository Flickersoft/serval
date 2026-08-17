using Serval.Server.Configuration;
using Serval.Server.Ingest;

namespace Serval.Server.Cameras;

/// <summary>
/// Reports the state of the whole camera registry once, at startup.
///
/// The ingest loop already skips a camera it cannot run and says so, but only after the server is
/// up and only once per five-second tick. This is the version an operator sees while watching the
/// thing boot, which is when they are looking — and it is the only place a camera that is merely
/// misconfigured gets the same visibility as one that crashed.
/// </summary>
public static class CameraRegistrySweep
{
    /// <summary>
    /// Never throws. One malformed document must not stop a server that has other cameras to
    /// record, and a Mongo blip here should not turn into a failed boot.
    /// </summary>
    public static async Task RunAsync(
        CameraRepository repository,
        ServerOptions options,
        FfmpegCapabilities capabilities,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            List<Camera> cameras = await repository.ListAsync(cancellationToken);

            int faulted = 0;
            int advisoryCount = 0;

            foreach (Camera camera in cameras)
            {
                if (CameraRegistryCheck.Fault(camera, options.Ingest, capabilities) is { } fault)
                {
                    faulted++;
                    logger.LogError(
                        "Camera {CameraId} will not be ingested: {Fault}", camera.Id, fault);
                    continue;
                }

                foreach (string advisory in CameraRegistryCheck.Advisories(camera, options))
                {
                    advisoryCount++;
                    logger.LogWarning("{Advisory}", advisory);
                }
            }

            logger.LogInformation(
                "Camera registry: {Total} cameras, {Ingestable} ingestable, {Faulted} with "
                + "problems, {Advisories} advisories.",
                cameras.Count, cameras.Count - faulted, faulted, advisoryCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Camera registry sweep failed; continuing startup.");
        }
    }
}
