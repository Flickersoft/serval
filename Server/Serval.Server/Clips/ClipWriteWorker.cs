using System.Threading.Channels;
using MongoDB.Bson;
using Serval.Server.Media;
using Serval.Server.Recordings;

namespace Serval.Server.Clips;

/// <summary>
/// Writes the accepted clips, one at a time, off the request thread.
///
/// The deferral is not tidiness. Half an hour of a main stream is a couple of gigabytes and tens of
/// seconds of ffmpeg, which is longer than a request should be held open and far longer than a
/// dialog can appear to be doing nothing. So the endpoint validates everything a caller can get
/// wrong, writes the row as <see cref="ClipState.Writing"/>, and hands the id here.
///
/// One at a time, deliberately: this is a container rewrite reading and writing the same volume the
/// recorders are writing to, and running several at once on the target hardware would slow the
/// thing that must not be slowed. A queued clip waits; a recording never does.
/// </summary>
public sealed class ClipWriteWorker : BackgroundService
{
    private readonly Channel<ObjectId> _queue = Channel.CreateUnbounded<ObjectId>();

    private readonly ClipRepository _clips;
    private readonly ClipStorage _storage;
    private readonly ClipExporter _exporter;
    private readonly ClipMedia _media;
    private readonly RecordingIndex _recordings;
    private readonly ClipSummaryWorker _summaries;
    private readonly ILogger<ClipWriteWorker> _logger;

    public ClipWriteWorker(
        ClipRepository clips,
        ClipStorage storage,
        ClipExporter exporter,
        ClipMedia media,
        RecordingIndex recordings,
        ClipSummaryWorker summaries,
        ILogger<ClipWriteWorker> logger)
    {
        _clips = clips;
        _storage = storage;
        _exporter = exporter;
        _media = media;
        _recordings = recordings;
        _summaries = summaries;
        _logger = logger;
    }

    /// <summary>Queues an already-inserted clip for writing.</summary>
    public void Enqueue(ObjectId id) => _queue.Writer.TryWrite(id);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        await foreach (ObjectId id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await WriteAsync(id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutting down mid-write. The row stays Writing and the next boot's recovery
                // fails it — better than failing it here, where the process may not live long
                // enough to finish the update.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Clip {ClipId} could not be written.", id);
                await FailAsync(id, ex.Message, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Fails every clip left mid-write by a restart.
    ///
    /// A queued clip lived in a channel inside the process that stopped, so nothing will ever pick
    /// these up again and no amount of waiting changes that. Left alone they would sit in
    /// <see cref="ClipState.Writing"/> forever — invisible in the list, unfinishable, and holding a
    /// half-written file nothing will ever reclaim.
    /// </summary>
    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        List<SavedClip> stranded;

        try
        {
            stranded = await _clips.ListWritingAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Mongo may not be up yet on a cold start. Recovery is a tidy-up, not a precondition
            // for serving, so a failure here must not stop the worker from taking new work.
            _logger.LogWarning(ex, "Could not sweep clips left mid-write.");
            return;
        }

        foreach (SavedClip clip in stranded)
        {
            _logger.LogWarning(
                "Clip {ClipId} was still being written when the server stopped; marking it failed.", clip.Id);

            await FailAsync(clip.Id, "The server restarted while this clip was being written.", cancellationToken);
        }
    }

    private async Task WriteAsync(ObjectId id, CancellationToken cancellationToken)
    {
        SavedClip? clip = await _clips.GetAsync(id, cancellationToken);

        if (clip is null || clip.State != ClipState.Writing)
        {
            // Deleted, or already handled. Neither is an error.
            return;
        }

        List<RecordingSegment> segments =
            await _recordings.InRangeAsync(clip.CameraId, clip.From, clip.To, cancellationToken);

        IReadOnlyList<RecordingSegment> run = ClipExporter.LeadingRun(segments);
        if (run.Count == 0)
        {
            // The endpoint checked this, but retention runs on its own schedule and the segments
            // could have been pruned between accepting the clip and reaching it in the queue.
            await FailAsync(id, "The footage for this clip is no longer on disk.", cancellationToken);
            return;
        }

        _storage.EnsureRoot();

        string video = _storage.VideoFor(id);
        await _exporter.WriteFileAsync(clip.CameraId, run, video, cancellationToken);

        double duration = (clip.To - clip.From).TotalSeconds;
        duration = await _media.TryReadDurationAsync(video, cancellationToken) ?? duration;

        // The middle rather than the first frame: a clip opens on whatever was happening a moment
        // before the thing worth keeping, and a card showing an empty doorway is a card nobody can
        // find later.
        await _media.TryWritePosterAsync(video, _storage.PosterFor(id), duration / 2, cancellationToken);

        ClipDocuments documents = await _clips.FreezeAsync(clip.CameraId, clip.From, clip.To, cancellationToken);

        await _clips.MarkReadyAsync(
            id,
            new FileInfo(video).Length,
            duration,
            documents,
            ClipRepository.BuildSearchText(clip.Name, documents),
            cancellationToken);

        _logger.LogInformation(
            "Saved clip {ClipId} for camera {CameraId}: {Seconds:0.#}s from {Segments} segments.",
            id, clip.CameraId, duration, run.Count);

        _summaries.Enqueue(id);
    }

    private async Task FailAsync(ObjectId id, string reason, CancellationToken cancellationToken)
    {
        try
        {
            _storage.Remove(id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove the files of failed clip {ClipId}.", id);
        }

        try
        {
            await _clips.MarkFailedAsync(id, reason, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not mark clip {ClipId} failed.", id);
        }
    }
}
