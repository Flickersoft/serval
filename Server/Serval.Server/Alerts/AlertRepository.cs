using MongoDB.Driver;
using Serval.Server.Storage;

namespace Serval.Server.Alerts;

/// <summary>
/// Storage for the alert queue. Owns the collection; the files beside it are
/// <see cref="AlertClipWorker"/>'s.
/// </summary>
public sealed class AlertRepository
{
    private readonly MongoContext _context;

    public AlertRepository(MongoContext context) => _context = context;

    private IMongoCollection<Alert> Alerts => _context.Alerts;

    /// <summary>
    /// Records an alert if this detection has not already raised one, and says whether it did.
    ///
    /// An insert that tolerates a duplicate key rather than a read followed by a write. The raise
    /// path is called from the detection loop once a frame for as long as an alert-worthy object is
    /// in view, so all but the first call are duplicates by construction — and the caller needs to
    /// know which one it was, because that is what decides whether a clip gets queued.
    /// </summary>
    public async Task<bool> RaiseAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        try
        {
            await Alerts.InsertOneAsync(alert, options: null, cancellationToken);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    /// <summary>
    /// The queue: everything not dismissed, newest first, optionally narrowed to one camera.
    ///
    /// Dismissed alerts are excluded rather than deleted — their files outlive them until retention
    /// — so this filter is what "the queue" means, and it is the one the cross-camera index is
    /// built for. <paramref name="before"/> pages backwards through it; the screen has no pager,
    /// but a queue nobody clears for a month should not have to arrive in one response.
    /// </summary>
    public async Task<List<Alert>> ListAsync(
        string? cameraId,
        int limit,
        DateTimeOffset? before,
        CancellationToken cancellationToken = default)
    {
        FilterDefinitionBuilder<Alert> by = Builders<Alert>.Filter;
        FilterDefinition<Alert> filter = by.Eq(a => a.DismissedAt, null);

        if (!string.IsNullOrWhiteSpace(cameraId))
        {
            filter &= by.Eq(a => a.CameraId, cameraId);
        }

        if (before is { } cutoff)
        {
            filter &= by.Lt(a => a.At, cutoff);
        }

        return await Alerts.Find(filter)
            .SortByDescending(a => a.At)
            .Limit(limit)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// How many undismissed alerts nobody has opened — the header's count and the rail's dot.
    ///
    /// Counted rather than derived from the page above, because the page is limited and the count
    /// is not: "4 new" has to stay true when the fifth is below the fold.
    /// </summary>
    public async Task<long> UnreadCountAsync(
        string? cameraId, CancellationToken cancellationToken = default)
    {
        FilterDefinitionBuilder<Alert> by = Builders<Alert>.Filter;
        FilterDefinition<Alert> filter = by.Eq(a => a.DismissedAt, null) & by.Eq(a => a.Read, false);

        if (!string.IsNullOrWhiteSpace(cameraId))
        {
            filter &= by.Eq(a => a.CameraId, cameraId);
        }

        return await Alerts.CountDocumentsAsync(filter, options: null, cancellationToken);
    }

    public async Task<Alert?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        await Alerts.Find(a => a.Id == id).FirstOrDefaultAsync(cancellationToken);

    /// <summary>Marks one read. Returns false if there is no such alert.</summary>
    public async Task<bool> MarkReadAsync(string id, CancellationToken cancellationToken = default)
    {
        UpdateResult result = await Alerts.UpdateOneAsync(
            a => a.Id == id,
            Builders<Alert>.Update.Set(a => a.Read, true),
            options: null,
            cancellationToken);

        return result.MatchedCount > 0;
    }

    /// <summary>
    /// Clears one from the queue.
    ///
    /// Also marks it read: a row somebody dealt with cannot sensibly still be counted as something
    /// nobody has seen, and the desktop's <em>Dismiss all</em> would otherwise leave a rail badge
    /// lit over an empty screen.
    /// </summary>
    public async Task<bool> DismissAsync(
        string id, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        UpdateResult result = await Alerts.UpdateOneAsync(
            a => a.Id == id && a.DismissedAt == null,
            Builders<Alert>.Update.Set(a => a.DismissedAt, now).Set(a => a.Read, true),
            options: null,
            cancellationToken);

        return result.MatchedCount > 0;
    }

    /// <summary>Clears the whole queue, or one camera's part of it. Returns how many went.</summary>
    public async Task<long> DismissAllAsync(
        string? cameraId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        FilterDefinitionBuilder<Alert> by = Builders<Alert>.Filter;
        FilterDefinition<Alert> filter = by.Eq(a => a.DismissedAt, null);

        if (!string.IsNullOrWhiteSpace(cameraId))
        {
            filter &= by.Eq(a => a.CameraId, cameraId);
        }

        UpdateResult result = await Alerts.UpdateManyAsync(
            filter,
            Builders<Alert>.Update.Set(a => a.DismissedAt, now).Set(a => a.Read, true),
            options: null,
            cancellationToken);

        return result.ModifiedCount;
    }

    /// <summary>What the clip worker learned: how the cut went, and whether there is a recording to
    /// send the viewer to.</summary>
    public async Task SetClipAsync(
        string id,
        AlertClipState state,
        double? seconds,
        string? error,
        bool? recorded,
        CancellationToken cancellationToken = default) =>
        await Alerts.UpdateOneAsync(
            a => a.Id == id,
            Builders<Alert>.Update
                .Set(a => a.ClipState, state)
                .Set(a => a.ClipSeconds, seconds)
                .Set(a => a.ClipError, error)
                .Set(a => a.Recorded, recorded),
            options: null,
            cancellationToken);

    /// <summary>
    /// Resolves every alert raised before <paramref name="raisedBefore"/> that is still waiting for
    /// a clip.
    ///
    /// A pending alert's footage was in a rolling buffer that the stopped process held the index
    /// to. Both are gone: the index was in memory and the files are cleared when the session
    /// restarts. There is nothing to re-cut, so saying so is the only honest option — leaving them
    /// pending would be a spinner that never resolves.
    ///
    /// <para>Bounded by time rather than sweeping everything pending, because this runs on a server
    /// whose cameras are already detecting: an alert raised during startup is not stranded, it is
    /// queued, and clearing it would cancel a clip that was about to be cut.</para>
    /// </summary>
    public async Task<long> FailPendingAsync(
        DateTimeOffset raisedBefore, CancellationToken cancellationToken = default)
    {
        UpdateResult result = await Alerts.UpdateManyAsync(
            a => a.ClipState == AlertClipState.Pending && a.CreatedAt < raisedBefore,
            Builders<Alert>.Update.Set(a => a.ClipState, AlertClipState.Unavailable),
            options: null,
            cancellationToken);

        return result.ModifiedCount;
    }

    /// <summary>
    /// Deletes alerts older than <paramref name="cutoff"/>, returning them so their files can go
    /// too — the same shape as <c>RecordingIndex.DeleteBeforeAsync</c>, and for the same reason:
    /// the index is what knows the filenames.
    /// </summary>
    public async Task<List<Alert>> DeleteBeforeAsync(
        DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        List<Alert> expired = await Alerts
            .Find(a => a.At < cutoff)
            .ToListAsync(cancellationToken);

        if (expired.Count > 0)
        {
            await Alerts.DeleteManyAsync(a => a.At < cutoff, cancellationToken);
        }

        return expired;
    }
}
