using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Serval.Contracts;
using Serval.Server.Storage;

namespace Serval.Server.Clips;

/// <summary>
/// Storage for saved clips. Owns the collection; the files beside it are the write worker's.
/// </summary>
public sealed class ClipRepository
{
    /// <summary>
    /// Ceiling on how many of each kind of document a clip freezes.
    ///
    /// Matches the read routes' own clamp. A window busy enough to exceed it is one where the
    /// oldest records matter most, which is why <see cref="FreezeAsync"/> re-sorts ascending after
    /// the descending query rather than taking the newest N and calling it the window.
    /// </summary>
    private const int DocumentLimit = 1000;

    private readonly MongoContext _context;

    public ClipRepository(MongoContext context) => _context = context;

    private IMongoCollection<SavedClip> Clips => _context.Clips;

    /// <summary>
    /// Clips ready to watch, newest first.
    ///
    /// <see cref="ClipState.Writing"/> and <see cref="ClipState.Failed"/> are excluded: a clip
    /// whose bytes are still arriving would appear in the list as a card that cannot be played,
    /// and a failed one has no directory left to play from. The App follows a save it started
    /// through <c>/status</c> instead, which is scoped to the one clip it is waiting for.
    /// </summary>
    public async Task<List<SavedClip>> ListAsync(
        string? query, string? cameraId, CancellationToken cancellationToken = default)
    {
        FilterDefinitionBuilder<SavedClip> by = Builders<SavedClip>.Filter;
        FilterDefinition<SavedClip> filter = by.Eq(c => c.State, ClipState.Ready);

        if (!string.IsNullOrWhiteSpace(cameraId))
        {
            filter &= by.Eq(c => c.CameraId, cameraId);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            // Escaped, because this is a user's search box and an unescaped ".*(" is a query that
            // either throws or runs forever. Substring rather than prefix: people search for a word
            // from the middle of what was said, not for how a clip's name starts.
            filter &= by.Regex(
                c => c.SearchText,
                new BsonRegularExpression(Regex.Escape(query.Trim().ToLowerInvariant()), "i"));
        }

        return await Clips.Find(filter)
            .SortByDescending(c => c.From)
            .ToListAsync(cancellationToken);
    }

    public async Task<SavedClip?> GetAsync(ObjectId id, CancellationToken cancellationToken = default) =>
        await Clips.Find(c => c.Id == id).FirstOrDefaultAsync(cancellationToken);

    public Task InsertAsync(SavedClip clip, CancellationToken cancellationToken = default) =>
        Clips.InsertOneAsync(clip, cancellationToken: cancellationToken);

    public Task DeleteAsync(ObjectId id, CancellationToken cancellationToken = default) =>
        Clips.DeleteOneAsync(c => c.Id == id, cancellationToken);

    /// <summary>
    /// Renames a clip and rebuilds its search text, which embeds the name.
    /// </summary>
    public async Task RenameAsync(ObjectId id, string name, CancellationToken cancellationToken = default)
    {
        SavedClip? clip = await GetAsync(id, cancellationToken);
        if (clip is null)
        {
            return;
        }

        await Clips.UpdateOneAsync(
            c => c.Id == id,
            Builders<SavedClip>.Update
                .Set(c => c.Name, name)
                .Set(c => c.SearchText, BuildSearchText(name, clip.Documents)),
            cancellationToken: cancellationToken);
    }

    public Task SetSummaryAsync(ObjectId id, string summary, CancellationToken cancellationToken = default) =>
        Clips.UpdateOneAsync(
            c => c.Id == id,
            Builders<SavedClip>.Update.Set(c => c.Summary, summary),
            cancellationToken: cancellationToken);

    public Task MarkReadyAsync(
        ObjectId id,
        long sizeBytes,
        double durationSeconds,
        ClipDocuments documents,
        string searchText,
        CancellationToken cancellationToken = default) =>
        Clips.UpdateOneAsync(
            c => c.Id == id,
            Builders<SavedClip>.Update
                .Set(c => c.State, ClipState.Ready)
                .Set(c => c.SizeBytes, sizeBytes)
                .Set(c => c.DurationSeconds, durationSeconds)
                .Set(c => c.Documents, documents)
                .Set(c => c.SearchText, searchText),
            cancellationToken: cancellationToken);

    public Task MarkFailedAsync(ObjectId id, string error, CancellationToken cancellationToken = default) =>
        Clips.UpdateOneAsync(
            c => c.Id == id,
            Builders<SavedClip>.Update
                .Set(c => c.State, ClipState.Failed)
                .Set(c => c.Error, error),
            cancellationToken: cancellationToken);

    /// <summary>
    /// Clips still marked <see cref="ClipState.Writing"/>, which after a restart means clips
    /// nothing is writing.
    ///
    /// The queue that drove them lived in the process that died, so no amount of waiting will
    /// advance them — they have to be failed explicitly or they sit in that state forever, which is
    /// the shape of bug worth not shipping twice.
    /// </summary>
    public async Task<List<SavedClip>> ListWritingAsync(CancellationToken cancellationToken = default) =>
        await Clips.Find(c => c.State == ClipState.Writing).ToListAsync(cancellationToken);

    /// <summary>
    /// Copies the telemetry covering <paramref name="from"/>–<paramref name="to"/> out of the live
    /// collections.
    ///
    /// Each list is re-sorted ascending afterwards: the queries sort descending so their limit
    /// keeps the newest, and a clip read back in that order would play its transcript backwards.
    /// </summary>
    public async Task<ClipDocuments> FreezeAsync(
        string cameraId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var telemetry = new TelemetryRepositoryReader(_context);

        var documents = new ClipDocuments
        {
            Detections = await telemetry.DetectionsAsync(cameraId, from, to, DocumentLimit, cancellationToken),
            Scenes = await telemetry.ScenesAsync(cameraId, from, to, DocumentLimit, cancellationToken),
            Utterances = await telemetry.UtterancesAsync(cameraId, from, to, DocumentLimit, cancellationToken),
            ConversationTranscripts =
                await telemetry.TranscriptsAsync(cameraId, from, to, DocumentLimit, cancellationToken),
            Sounds = await telemetry.SoundsAsync(cameraId, from, to, DocumentLimit, cancellationToken),
        };

        documents.Detections.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        documents.Scenes.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        documents.Utterances.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        documents.ConversationTranscripts.Sort((a, b) => a.StartedAt.CompareTo(b.StartedAt));
        documents.Sounds.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        return documents;
    }

    /// <summary>
    /// The one field 13a's "search names and what was said" matches: the clip's name followed by
    /// everything spoken in it, lowercased.
    ///
    /// Settled transcripts and live utterances are both included and they overlap — a settled
    /// conversation replaces its own live utterances on screen, but for matching a word, having it
    /// twice costs nothing and missing it because only one half was indexed costs a search.
    /// </summary>
    public static string BuildSearchText(string name, ClipDocuments documents)
    {
        var text = new StringBuilder(name);

        foreach (UtteranceDocument utterance in documents.Utterances)
        {
            text.Append(' ').Append(utterance.Transcript);
        }

        foreach (ConversationTranscriptDocument transcript in documents.ConversationTranscripts)
        {
            text.Append(' ').Append(transcript.Text);
        }

        return text.ToString().ToLowerInvariant();
    }
}

/// <summary>
/// The telemetry reads a freeze needs, in one place.
///
/// A thin wrapper rather than a dependency on <c>TelemetryRepository</c> so the two cannot drift on
/// the point that matters here: these queries must have exactly the semantics the App's live reads
/// have, or a clip would show a different set of events than the screen it was taken from.
/// </summary>
internal sealed class TelemetryRepositoryReader
{
    private readonly MongoContext _context;

    public TelemetryRepositoryReader(MongoContext context) => _context = context;

    /// <summary>Episodes overlapping the window, matching the live route: one that opened before
    /// the clip and is still running was present during it.</summary>
    public async Task<List<DetectionDocument>> DetectionsAsync(
        string cameraId, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken) =>
        await _context.Detections
            .Find(d => d.CameraId == cameraId && d.Timestamp <= to && (d.EndedAt == null || d.EndedAt >= from))
            .SortByDescending(d => d.Timestamp)
            .Limit(limit)
            .ToListAsync(cancellationToken);

    public async Task<List<SceneDocument>> ScenesAsync(
        string cameraId, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken) =>
        await _context.Scenes
            .Find(s => s.CameraId == cameraId && s.Timestamp >= from && s.Timestamp <= to)
            .SortByDescending(s => s.Timestamp)
            .Limit(limit)
            .ToListAsync(cancellationToken);

    public async Task<List<UtteranceDocument>> UtterancesAsync(
        string cameraId, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken) =>
        await _context.Utterances
            .Find(u => u.CameraId == cameraId && u.Timestamp >= from && u.Timestamp <= to)
            .SortByDescending(u => u.Timestamp)
            .Limit(limit)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// How far back a conversation may have started and still be running when the clip does.
    ///
    /// A conversation is capped at half an hour by <c>MaxConversationMinutes</c>, so nothing
    /// starting earlier than this can still be open — the same widen-then-refine shape
    /// <c>RecordingIndex.InRangeAsync</c> uses, and for the same reason: the index is on the start
    /// time, and an overlap test written in the query would not use it.
    /// </summary>
    private static readonly TimeSpan LongestConversation = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Conversations that <em>overlap</em> the window rather than start in it.
    ///
    /// Deliberately wider than the live route, which filters on <c>StartedAt</c> alone. A
    /// conversation is a span, and a clip taken from the middle of a doorstep exchange would
    /// otherwise freeze no transcript at all — the one case where "said in it" matters most.
    /// </summary>
    public async Task<List<ConversationTranscriptDocument>> TranscriptsAsync(
        string cameraId, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken)
    {
        DateTimeOffset widened = from - LongestConversation;

        List<ConversationTranscriptDocument> candidates = await _context.ConversationTranscripts
            .Find(c => c.CameraId == cameraId && c.StartedAt >= widened && c.StartedAt <= to)
            .SortByDescending(c => c.StartedAt)
            .Limit(limit)
            .ToListAsync(cancellationToken);

        return [.. candidates.Where(c => c.StartedAt.AddSeconds(c.AudioSeconds) >= from)];
    }

    public async Task<List<SoundDocument>> SoundsAsync(
        string cameraId, DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken cancellationToken) =>
        await _context.Sounds
            .Find(s => s.CameraId == cameraId && s.Timestamp >= from && s.Timestamp <= to)
            .SortByDescending(s => s.Timestamp)
            .Limit(limit)
            .ToListAsync(cancellationToken);
}
