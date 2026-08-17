using Microsoft.Extensions.Options;
using Serval.Server.Cameras;
using Serval.Server.Configuration;

namespace Serval.Server.Recordings;

/// <summary>
/// Prunes recordings past their retention so storage can't grow without bound — the server-side
/// counterpart to the module's DeleteAfterSync. On each sweep it drops index entries older than
/// each camera's cutoff and deletes their segment files. Files with no index entry (e.g. a
/// segment written but not yet indexed) are left for a later sweep rather than risk deleting a
/// live one.
/// </summary>
public sealed class RetentionWorker : PeriodicWorker
{
    private readonly CameraRepository _cameras;
    private readonly RecordingIndex _recordings;
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly ILogger<RetentionWorker> _logger;

    public RetentionWorker(
        CameraRepository cameras,
        RecordingIndex recordings,
        IOptionsMonitor<ServerOptions> options,
        ILogger<RetentionWorker> logger)
        : base(logger)
    {
        _cameras = cameras;
        _recordings = recordings;
        _options = options;
        _logger = logger;
    }

    private MediaOptions Media => _options.CurrentValue.Media;

    protected override string Activity => "Retention sweep";

    protected override TimeSpan Interval =>
        TimeSpan.FromMinutes(Math.Max(Media.RetentionSweepMinutes, 1));

    protected override async Task TickAsync(CancellationToken cancellationToken)
    {
        MediaOptions media = Media;

        foreach (Camera camera in await _cameras.ListAsync(cancellationToken))
        {
            int days = camera.RetentionDays ?? media.RetentionDays;
            DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-days);

            List<RecordingSegment> expired = await _recordings.DeleteBeforeAsync(camera.Id, cutoff, cancellationToken);
            if (expired.Count == 0)
            {
                continue;
            }

            string cameraDir = Path.Combine(media.Root, camera.Id);
            int deleted = 0;
            foreach (RecordingSegment segment in expired)
            {
                string path = Path.Combine(cameraDir, segment.FileName);
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        deleted++;
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Could not delete expired segment {Path}.", path);
                }
            }

            _logger.LogInformation(
                "Retention: pruned {Count} segment(s) older than {Days}d for camera {CameraId}.",
                deleted, days, camera.Id);
        }
    }
}
