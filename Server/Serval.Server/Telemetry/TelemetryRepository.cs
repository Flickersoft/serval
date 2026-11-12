using System.Text.RegularExpressions;
using MongoDB.Bson;
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
        OverlappingDetectionsAsync(
            Builders<DetectionDocument>.Filter.Eq(d => d.CameraId, cameraId), from, to, limit, cancellationToken);

    /// <summary>
    /// How far before a window an episode that has since <em>closed</em> may have started and still
    /// be found overlapping it. Episodes still open are found however old they are — see
    /// <see cref="OverlappingDetectionsAsync"/>.
    /// </summary>
    internal static readonly TimeSpan ClosedEpisodeLookback = TimeSpan.FromDays(7);

    /// <summary>
    /// Episodes overlapping [from, to], newest first, as two bounded queries rather than one open one.
    ///
    /// <para><b>Why two.</b> "Started before <c>to</c>, and not ended before <c>from</c>" has no lower
    /// bound on the start, and the end is not indexed — so on a quiet window, where fewer than
    /// <paramref name="limit"/> episodes match, Mongo walks the (CameraId, Timestamp) index back to
    /// the first detection the camera ever made, reading each one to test its end. Split instead:
    /// episodes that started within <see cref="ClosedEpisodeLookback"/> of the window, on the
    /// timestamp index; and episodes that are still open and started before that, on the
    /// (CameraId, EndedAt, Timestamp) index, where an open episode is an equality match on null. The
    /// only thing given up is an episode that lasted over a week and has since closed.</para>
    /// </summary>
    private async Task<List<DetectionDocument>> OverlappingDetectionsAsync(
        FilterDefinition<DetectionDocument> scope,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken cancellationToken)
    {
        FilterDefinitionBuilder<DetectionDocument> by = Builders<DetectionDocument>.Filter;
        DateTimeOffset floor = from - ClosedEpisodeLookback;

        FilterDefinition<DetectionDocument> recent = scope
            & by.Gte(d => d.Timestamp, floor)
            & by.Lte(d => d.Timestamp, to)
            & (by.Eq(d => d.EndedAt, null) | by.Gte(d => d.EndedAt, from));

        FilterDefinition<DetectionDocument> stillOpen = scope
            & by.Eq(d => d.EndedAt, null)
            & by.Lt(d => d.Timestamp, floor);

        List<DetectionDocument> found = await _context.Detections.Find(recent)
            .SortByDescending(d => d.Timestamp).Limit(limit).ToListAsync(cancellationToken);

        found.AddRange(await _context.Detections.Find(stillOpen)
            .SortByDescending(d => d.Timestamp).Limit(limit).ToListAsync(cancellationToken));

        return [.. found.OrderByDescending(d => d.Timestamp).Take(limit)];
    }

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

    // ---- Across cameras, optionally by text ------------------------------------------------------
    //
    // What the MCP tools ask: "everything on these cameras in this window", and the same narrowed
    // to records whose text contains a phrase. The cameras are always named — the caller passes
    // every camera rather than none — because an $in on CameraId is what lets each query ride the
    // (CameraId, Timestamp) index and merge the cameras' runs in order; a filter on time alone has
    // no index to use and would scan the collection.
    //
    // The text match is an escaped, case-insensitive substring regex, the rule ClipRepository's
    // search already uses. It cannot use an index, so it is only ever applied inside a bounded
    // window, where the index has already narrowed the scan to that window's records.

    public Task<List<SceneDocument>> FindScenesAsync(
        IReadOnlyCollection<string> cameraIds, DateTimeOffset from, DateTimeOffset to, string? text, int limit,
        CancellationToken cancellationToken = default)
    {
        FilterDefinitionBuilder<SceneDocument> by = Builders<SceneDocument>.Filter;
        FilterDefinition<SceneDocument> filter =
            by.In(s => s.CameraId, cameraIds) & by.Gte(s => s.Timestamp, from) & by.Lte(s => s.Timestamp, to);

        if (Contains(text) is { } pattern)
        {
            filter &= by.Regex(s => s.Description, pattern);
        }

        return _context.Scenes.Find(filter).SortByDescending(s => s.Timestamp).Limit(limit).ToListAsync(cancellationToken);
    }

    public Task<List<UtteranceDocument>> FindUtterancesAsync(
        IReadOnlyCollection<string> cameraIds, DateTimeOffset from, DateTimeOffset to, string? text, int limit,
        CancellationToken cancellationToken = default)
    {
        FilterDefinitionBuilder<UtteranceDocument> by = Builders<UtteranceDocument>.Filter;
        FilterDefinition<UtteranceDocument> filter =
            by.In(u => u.CameraId, cameraIds) & by.Gte(u => u.Timestamp, from) & by.Lte(u => u.Timestamp, to);

        if (Contains(text) is { } pattern)
        {
            filter &= by.Regex(u => u.Transcript, pattern);
        }

        return _context.Utterances.Find(filter).SortByDescending(u => u.Timestamp).Limit(limit).ToListAsync(cancellationToken);
    }

    public Task<List<ConversationTranscriptDocument>> FindConversationTranscriptsAsync(
        IReadOnlyCollection<string> cameraIds, DateTimeOffset from, DateTimeOffset to, string? text, int limit,
        CancellationToken cancellationToken = default)
    {
        FilterDefinitionBuilder<ConversationTranscriptDocument> by = Builders<ConversationTranscriptDocument>.Filter;
        FilterDefinition<ConversationTranscriptDocument> filter =
            by.In(c => c.CameraId, cameraIds) & by.Gte(c => c.StartedAt, from) & by.Lte(c => c.StartedAt, to);

        if (Contains(text) is { } pattern)
        {
            filter &= by.Regex(c => c.Text, pattern);
        }

        return _context.ConversationTranscripts.Find(filter)
            .SortByDescending(c => c.StartedAt).Limit(limit).ToListAsync(cancellationToken);
    }

    /// <summary>Episodes overlapping the window, as <see cref="QueryDetectionsAsync"/> defines it.</summary>
    public Task<List<DetectionDocument>> FindDetectionsAsync(
        IReadOnlyCollection<string> cameraIds, DateTimeOffset from, DateTimeOffset to, string? text, int limit,
        CancellationToken cancellationToken = default)
    {
        FilterDefinitionBuilder<DetectionDocument> by = Builders<DetectionDocument>.Filter;
        FilterDefinition<DetectionDocument> scope = by.In(d => d.CameraId, cameraIds);

        if (Contains(text) is { } pattern)
        {
            scope &= by.Regex(d => d.Label, pattern);
        }

        return OverlappingDetectionsAsync(scope, from, to, limit, cancellationToken);
    }

    public Task<List<SoundDocument>> FindSoundsAsync(
        IReadOnlyCollection<string> cameraIds, DateTimeOffset from, DateTimeOffset to, string? text, int limit,
        CancellationToken cancellationToken = default)
    {
        FilterDefinitionBuilder<SoundDocument> by = Builders<SoundDocument>.Filter;
        FilterDefinition<SoundDocument> filter =
            by.In(s => s.CameraId, cameraIds) & by.Gte(s => s.Timestamp, from) & by.Lte(s => s.Timestamp, to);

        if (Contains(text) is { } pattern)
        {
            filter &= by.Regex(s => s.Label, pattern);
        }

        return _context.Sounds.Find(filter).SortByDescending(s => s.Timestamp).Limit(limit).ToListAsync(cancellationToken);
    }

    /// <summary>An escaped, case-insensitive substring pattern, or null for no text filter.</summary>
    internal static BsonRegularExpression? Contains(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : new BsonRegularExpression(Regex.Escape(text.Trim()), "i");

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
