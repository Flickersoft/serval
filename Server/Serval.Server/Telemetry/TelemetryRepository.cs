using MongoDB.Driver;
using Serval.Contracts;
using Serval.Server.Storage;

namespace Serval.Server.Telemetry;

/// <summary>
/// Storage and queries for AI telemetry. Writes are idempotent upserts keyed by the record's
/// own id, so a batch the module re-delivers after a network gap updates in place instead of
/// duplicating — the server half of the outbox's at-least-once delivery guarantee.
///
/// The same write path serves both sources: records POSTed by an edge module, and records the
/// Server's own detection pipeline produces for cameras that have no module. They differ only in
/// their <c>source</c> field.
/// </summary>
public sealed class TelemetryRepository
{
    private static readonly ReplaceOptions Upsert = new() { IsUpsert = true };

    private readonly MongoContext _context;

    public TelemetryRepository(MongoContext context) => _context = context;

    public Task UpsertUtteranceAsync(UtteranceDocument document, CancellationToken cancellationToken = default) =>
        _context.Utterances.ReplaceOneAsync(u => u.Id == document.Id, document, Upsert, cancellationToken);

    public Task UpsertDiarizationAsync(DiarizationDocument document, CancellationToken cancellationToken = default) =>
        _context.Diarizations.ReplaceOneAsync(
            d => d.ConversationId == document.ConversationId, document, Upsert, cancellationToken);

    public Task UpsertSceneAsync(SceneDocument document, CancellationToken cancellationToken = default) =>
        _context.Scenes.ReplaceOneAsync(s => s.Id == document.Id, document, Upsert, cancellationToken);

    /// <summary>
    /// Writes an episode. Called twice under the same id — once when it opens, once when it closes
    /// — so the upsert is what makes the second write replace the first rather than accumulate.
    /// </summary>
    public Task UpsertDetectionAsync(
        DetectionDocument document, CancellationToken cancellationToken = default) =>
        _context.Detections.ReplaceOneAsync(d => d.Id == document.Id, document, Upsert, cancellationToken);

    public Task UpsertSoundAsync(SoundDocument document, CancellationToken cancellationToken = default) =>
        _context.Sounds.ReplaceOneAsync(s => s.Id == document.Id, document, Upsert, cancellationToken);

    public Task UpsertConversationTranscriptAsync(
        ConversationTranscriptDocument document, CancellationToken cancellationToken = default) =>
        _context.ConversationTranscripts.ReplaceOneAsync(
            c => c.ConversationId == document.ConversationId, document, Upsert, cancellationToken);

    public Task<List<UtteranceDocument>> QueryUtterancesAsync(
        string cameraId, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken = default) =>
        NewestFirstAsync(_context.Utterances,
            u => u.CameraId == cameraId && u.Timestamp >= from && u.Timestamp <= to,
            u => u.Timestamp, limit, cancellationToken);

    public Task<List<SceneDocument>> QueryScenesAsync(
        string cameraId, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken = default) =>
        NewestFirstAsync(_context.Scenes,
            s => s.CameraId == cameraId && s.Timestamp >= from && s.Timestamp <= to,
            s => s.Timestamp, limit, cancellationToken);

    /// <summary>
    /// Episodes overlapping a window, not merely starting in one. An episode that opened before
    /// <paramref name="from"/> and is still open — or closed inside the window — is present during
    /// it, and a query that filtered on start alone would hide exactly the long-running presences
    /// most worth asking about.
    /// </summary>
    public Task<List<DetectionDocument>> QueryDetectionsAsync(
        string cameraId, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken = default) =>
        NewestFirstAsync(_context.Detections,
            d => d.CameraId == cameraId && d.Timestamp <= to && (d.EndedAt == null || d.EndedAt >= from),
            d => d.Timestamp, limit, cancellationToken);

    public Task<List<SoundDocument>> QuerySoundsAsync(
        string cameraId, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken = default) =>
        NewestFirstAsync(_context.Sounds,
            s => s.CameraId == cameraId && s.Timestamp >= from && s.Timestamp <= to,
            s => s.Timestamp, limit, cancellationToken);

    public Task<List<ConversationTranscriptDocument>> QueryConversationTranscriptsAsync(
        string cameraId, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken = default) =>
        NewestFirstAsync(_context.ConversationTranscripts,
            c => c.CameraId == cameraId && c.StartedAt >= from && c.StartedAt <= to,
            c => c.StartedAt, limit, cancellationToken);

    private static Task<List<T>> NewestFirstAsync<T>(
        IMongoCollection<T> collection,
        System.Linq.Expressions.Expression<Func<T, bool>> filter,
        System.Linq.Expressions.Expression<Func<T, object>> timestamp,
        int limit,
        CancellationToken cancellationToken) =>
        collection.Find(filter).SortByDescending(timestamp).Limit(limit).ToListAsync(cancellationToken);

    /// <summary>
    /// The live utterances of one conversation, as the offline pass needs them.
    ///
    /// Read back out of storage rather than held in memory: a conversation can run for half an
    /// hour, and every utterance was durably written the moment it was transcribed — so this
    /// survives a restart mid-conversation, which an in-memory list would not.
    /// </summary>
    public async Task<IReadOnlyList<UtteranceDocument>> GetConversationUtterancesAsync(
        string cameraId, string conversationId, CancellationToken cancellationToken = default) =>
        await _context.Utterances
            .Find(u => u.CameraId == cameraId && u.ConversationId == conversationId)
            .SortBy(u => u.Timestamp)
            .ToListAsync(cancellationToken);
}
