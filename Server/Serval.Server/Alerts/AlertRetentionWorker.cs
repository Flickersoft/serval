using Microsoft.Extensions.Options;
using Serval.Server.Cameras;
using Serval.Server.Configuration;
using Serval.Server.Ingest;
using Serval.Server.Recordings;

namespace Serval.Server.Alerts;

/// <summary>
/// Prunes alerts past their retention, and sweeps up any preview-buffer files nothing is writing.
///
/// <para><b>Its own worker rather than a branch inside <see cref="Recordings.RetentionWorker"/>.</b>
/// That worker has exactly one rule — it deletes only inside <c>Root/{cameraId}</c>, and only
/// filenames its index handed back — and the rule is what makes it safe to reason about. Alert
/// media lives outside every camera directory, and ring files live inside one while being in no
/// index at all: both are the cases that rule exists to exclude. Teaching it two more exceptions
/// would cost more than a second loop on the same timer.</para>
/// </summary>
public sealed class AlertRetentionWorker : PeriodicWorker
{
    private readonly AlertRepository _alerts;
    private readonly AlertStorage _storage;
    private readonly CameraRepository _cameras;
    private readonly PreviewRingIndex _previewRing;
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly ILogger<AlertRetentionWorker> _logger;

    public AlertRetentionWorker(
        AlertRepository alerts,
        AlertStorage storage,
        CameraRepository cameras,
        PreviewRingIndex previewRing,
        IOptionsMonitor<ServerOptions> options,
        ILogger<AlertRetentionWorker> logger)
        : base(logger)
    {
        _alerts = alerts;
        _storage = storage;
        _cameras = cameras;
        _previewRing = previewRing;
        _options = options;
        _logger = logger;
    }

    private MediaOptions Media => _options.CurrentValue.Media;

    protected override string Activity => "Alert retention sweep";

    /// <summary>The same cadence recordings are pruned on — there is nothing about alerts that wants
    /// a different one, and one dial is easier to reason about than two.</summary>
    protected override TimeSpan Interval =>
        TimeSpan.FromMinutes(Math.Max(Media.RetentionSweepMinutes, 1));

    protected override async Task TickAsync(CancellationToken stoppingToken)
    {
        await SweepAlertsAsync(stoppingToken);
        await SweepOrphanedRingFilesAsync(stoppingToken);
    }

    /// <summary>
    /// Drops alerts older than the cutoff and their files.
    ///
    /// By <c>At</c> rather than by when the row was written, so an alert ages from the moment it is
    /// about — the same instant the queue groups it under. Dismissal does not enter into it: a
    /// cleared alert keeps its clip for exactly as long as an uncleared one, because clearing the
    /// queue is a statement about attention.
    /// </summary>
    private async Task SweepAlertsAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Max(Media.AlertRetentionDays, 1));

        List<Alert> expired = await _alerts.DeleteBeforeAsync(cutoff, cancellationToken);
        if (expired.Count == 0)
        {
            return;
        }

        foreach (Alert alert in expired)
        {
            _storage.Remove(alert.Id);
        }

        _logger.LogInformation(
            "Alert retention: pruned {Count} alert(s) older than {Days}d.",
            expired.Count, Media.AlertRetentionDays);
    }

    /// <summary>
    /// Deletes preview-buffer files left behind by a session that is not coming back.
    ///
    /// The ring prunes itself while a session runs, and <see cref="PreviewRing.Reset"/> clears the
    /// last one when a session starts. Neither covers a camera that was deleted, disabled, or left
    /// unable to start: its last buffer's worth of segments sits in a directory the recording sweep
    /// is built never to touch, in no index, forever. This is the only thing that reclaims them.
    ///
    /// <para>A camera with a live ring is skipped whole. Age alone would not do: the init segment is
    /// written once when a session starts and never touched again, so a camera that has been up for
    /// a day has a day-old file that every one of its previews depends on.</para>
    /// </summary>
    private async Task SweepOrphanedRingFilesAsync(CancellationToken cancellationToken)
    {
        string root = Media.Root;
        List<Camera> cameras = await _cameras.ListAsync(cancellationToken);
        int deleted = 0;

        foreach (Camera camera in cameras)
        {
            // Something is writing this one. Its own pruning is authoritative, and every file left
            // is one a preview being cut right now may be reading.
            if (_previewRing.HasRing(camera.Id))
            {
                continue;
            }

            string cameraDir = Path.Combine(root, camera.Id);
            if (!Directory.Exists(cameraDir))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(cameraDir, $"{PreviewRing.FilePrefix}*"))
            {
                try
                {
                    File.Delete(path);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left for the next sweep.
                }
            }
        }

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Alert retention: removed {Count} preview buffer file(s) no session was writing.", deleted);
        }
    }
}
