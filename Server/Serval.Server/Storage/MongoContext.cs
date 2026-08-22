using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Serval.Contracts;
using Serval.Server.Alerts;
using Serval.Server.Auth;
using Serval.Server.Cameras;
using Serval.Server.Clips;
using Serval.Server.Configuration;
using Serval.Server.GoogleHome;
using Serval.Server.Preferences;
using Serval.Server.Push;
using Serval.Server.Recordings;

namespace Serval.Server.Storage;

/// <summary>
/// The single point that knows about MongoDB. Hands out typed collections and, on startup,
/// creates the indexes the query paths rely on. Registered as a singleton — the MongoDB
/// driver's client is thread-safe and pools connections internally.
/// </summary>
public sealed class MongoContext
{
    private readonly IMongoDatabase _database;

    public MongoContext(IOptions<ServerOptions> options)
    {
        MongoOptions mongo = options.Value.Mongo;
        var client = new MongoClient(mongo.ConnectionString);
        _database = client.GetDatabase(mongo.Database);
    }

    public IMongoCollection<Camera> Cameras => _database.GetCollection<Camera>("cameras");
    public IMongoCollection<UtteranceDocument> Utterances => _database.GetCollection<UtteranceDocument>("utterances");
    public IMongoCollection<DiarizationDocument> Diarizations => _database.GetCollection<DiarizationDocument>("diarizations");
    public IMongoCollection<RecordingSegment> Recordings => _database.GetCollection<RecordingSegment>("recordings");

    /// <summary>
    /// Standalone scene descriptions. Their own collection because they are not utterances: a
    /// motion-triggered description happens when nobody is speaking, so there is no transcript to
    /// hang it on.
    /// </summary>
    public IMongoCollection<SceneDocument> Scenes => _database.GetCollection<SceneDocument>("scenes");

    /// <summary>
    /// Object-detection episodes — one class's continuous presence in front of one camera, rather
    /// than one frame it was seen in. Their own collection for the reason scenes and sounds have
    /// one: a detection happens at a specific instant and needs an 11 MB model, while a description
    /// is produced on a multi-second floor and needs a 2.3 GB one, so there is nothing to hang it on.
    /// </summary>
    public IMongoCollection<DetectionDocument> Detections =>
        _database.GetCollection<DetectionDocument>("detections");

    /// <summary>The settled, speaker-attributed transcript of a finished conversation.</summary>
    public IMongoCollection<ConversationTranscriptDocument> ConversationTranscripts =>
        _database.GetCollection<ConversationTranscriptDocument>("conversation_transcripts");

    /// <summary>
    /// Non-speech sound events. Their own collection for the same reason scenes have one: a car
    /// horn has no utterance to hang on, and the VAD that produces utterances rejects it anyway.
    /// </summary>
    public IMongoCollection<SoundDocument> Sounds => _database.GetCollection<SoundDocument>("sounds");

    /// <summary>
    /// Clips the user asked to keep. Unlike every other collection here this one is not an index
    /// of something the pipeline produced — it is the record of a deliberate act, and the only
    /// footage in Serval that retention never touches.
    /// </summary>
    public IMongoCollection<SavedClip> Clips => _database.GetCollection<SavedClip>("clips");

    /// <summary>
    /// The alert queue. Its own collection rather than a flag on <see cref="Detections"/> because
    /// the queue is cross-camera, carries state that changes, and has to interleave two kinds of
    /// detection from two collections — see <see cref="Alert"/> for the full argument.
    /// </summary>
    public IMongoCollection<Alert> Alerts => _database.GetCollection<Alert>("alerts");

    public IMongoCollection<User> Users => _database.GetCollection<User>("users");

    public IMongoCollection<RefreshToken> RefreshTokens =>
        _database.GetCollection<RefreshToken>("refresh_tokens");

    /// <summary>
    /// Per-account state that belongs to a person rather than to the deployment. Keyed by the
    /// user id, so it needs no index of its own — the _id is the lookup.
    /// </summary>
    public IMongoCollection<UserPreferences> UserPreferences =>
        _database.GetCollection<UserPreferences>("user_preferences");

    /// <summary>
    /// Browsers that have asked to be told about alerts. Keyed by a hash of the push endpoint, so
    /// a device re-registering overwrites its own row rather than accumulating one per visit.
    /// </summary>
    public IMongoCollection<PushSubscription> PushSubscriptions =>
        _database.GetCollection<PushSubscription>("push_subscriptions");

    /// <summary>
    /// This deployment's VAPID identity — one document, generated on first use. Its own collection
    /// rather than a row in <see cref="Settings"/> because it is key material rather than
    /// configuration; see <see cref="Push.VapidKeyStore"/> for why replacing it is not a small act.
    /// </summary>
    public IMongoCollection<VapidKeyDocument> PushKeys =>
        _database.GetCollection<VapidKeyDocument>("push_keys");

    /// <summary>
    /// Authorization codes issued to Google during account linking. Short-lived and single-use;
    /// see <see cref="GoogleHome.GoogleAuthorizationCode"/> for why consuming one is an update
    /// rather than a read.
    /// </summary>
    public IMongoCollection<GoogleAuthorizationCode> GoogleAuthorizationCodes =>
        _database.GetCollection<GoogleAuthorizationCode>("google_auth_codes");

    /// <summary>Access and refresh tokens issued to Google, stored as hashes only.</summary>
    public IMongoCollection<GoogleToken> GoogleTokens =>
        _database.GetCollection<GoogleToken>("google_tokens");

    /// <summary>
    /// The one Google account this deployment is linked to, keyed by the agent user id — so it
    /// needs no index of its own, and there is at most one document.
    /// </summary>
    public IMongoCollection<GoogleLink> GoogleLinks =>
        _database.GetCollection<GoogleLink>("google_links");

    /// <summary>
    /// Per-camera on/off as set from the Google Home app. Google-facing only: it decides whether a
    /// camera is offered a stream there, and never touches ingest — see
    /// <see cref="GoogleHome.GoogleCameraSwitch"/>. Only cameras switched off have a row.
    /// </summary>
    public IMongoCollection<GoogleCameraSwitch> GoogleCameraSwitches =>
        _database.GetCollection<GoogleCameraSwitch>("google_camera_switches");

    /// <summary>
    /// Create indexes. Idempotent — Mongo ignores a CreateOne for an index that already
    /// exists, so this runs safely on every boot.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Utterances and diarizations are queried by camera and time window.
        await Utterances.Indexes.CreateOneAsync(
            new CreateIndexModel<UtteranceDocument>(
                Builders<UtteranceDocument>.IndexKeys.Ascending(u => u.CameraId).Descending(u => u.Timestamp)),
            cancellationToken: cancellationToken);

        await Diarizations.Indexes.CreateOneAsync(
            new CreateIndexModel<DiarizationDocument>(
                Builders<DiarizationDocument>.IndexKeys.Ascending(d => d.CameraId).Descending(d => d.StartedAt)),
            cancellationToken: cancellationToken);

        await Scenes.Indexes.CreateOneAsync(
            new CreateIndexModel<SceneDocument>(
                Builders<SceneDocument>.IndexKeys.Ascending(s => s.CameraId).Descending(s => s.Timestamp)),
            cancellationToken: cancellationToken);

        await Detections.Indexes.CreateOneAsync(
            new CreateIndexModel<DetectionDocument>(
                Builders<DetectionDocument>.IndexKeys.Ascending(d => d.CameraId).Descending(d => d.Timestamp)),
            cancellationToken: cancellationToken);

        // Same argument as the sound alert index: "what needs attention at this camera" is its own
        // query, not a time window that happens to contain some alerts.
        await Detections.Indexes.CreateOneAsync(
            new CreateIndexModel<DetectionDocument>(
                Builders<DetectionDocument>.IndexKeys
                    .Ascending(d => d.CameraId).Ascending(d => d.IsAlert).Descending(d => d.Timestamp)),
            cancellationToken: cancellationToken);

        await ConversationTranscripts.Indexes.CreateOneAsync(
            new CreateIndexModel<ConversationTranscriptDocument>(
                Builders<ConversationTranscriptDocument>.IndexKeys
                    .Ascending(c => c.CameraId).Descending(c => c.StartedAt)),
            cancellationToken: cancellationToken);

        await Sounds.Indexes.CreateOneAsync(
            new CreateIndexModel<SoundDocument>(
                Builders<SoundDocument>.IndexKeys.Ascending(s => s.CameraId).Descending(s => s.Timestamp)),
            cancellationToken: cancellationToken);

        // Alerts are a query shape in their own right — "what needs attention at this camera" is
        // not a time window that happens to contain some alerts — and there are few enough of them
        // that scanning the whole camera's sounds to find them would be most of the work.
        await Sounds.Indexes.CreateOneAsync(
            new CreateIndexModel<SoundDocument>(
                Builders<SoundDocument>.IndexKeys
                    .Ascending(s => s.CameraId).Ascending(s => s.IsAlert).Descending(s => s.Timestamp)),
            cancellationToken: cancellationToken);

        // Recording lookup is always "this camera, this time range".
        await Recordings.Indexes.CreateOneAsync(
            new CreateIndexModel<RecordingSegment>(
                Builders<RecordingSegment>.IndexKeys.Ascending(r => r.CameraId).Ascending(r => r.StartedAt)),
            cancellationToken: cancellationToken);

        // Except when ingest asks "have I stored this one already", which is by name, not by time.
        // On the (CameraId, StartedAt) index above that question degrades to a scan of every
        // segment the camera has ever recorded — once per new segment, growing with retention.
        // Unique because (camera, filename) genuinely identifies a segment, which is what lets
        // RecordingIndex.AddIfNewAsync be a single upsert rather than a read followed by a write.
        await Recordings.Indexes.CreateOneAsync(
            new CreateIndexModel<RecordingSegment>(
                Builders<RecordingSegment>.IndexKeys.Ascending(r => r.CameraId).Ascending(r => r.FileName),
                new CreateIndexOptions { Unique = true }),
            cancellationToken: cancellationToken);

        // The clips screen is one list of everything, newest first, optionally narrowed to a
        // camera. Sorting on From rather than SavedAt because clips are grouped by when the thing
        // happened, not when someone got round to keeping it.
        await Clips.Indexes.CreateOneAsync(
            new CreateIndexModel<SavedClip>(
                Builders<SavedClip>.IndexKeys.Ascending(c => c.State).Descending(c => c.From)),
            cancellationToken: cancellationToken);

        await Clips.Indexes.CreateOneAsync(
            new CreateIndexModel<SavedClip>(
                Builders<SavedClip>.IndexKeys.Ascending(c => c.CameraId).Descending(c => c.From)),
            cancellationToken: cancellationToken);

        // The alert queue is one list of every camera, newest first — the one shape none of the
        // telemetry indexes above can serve, because every one of them leads with CameraId. Leading
        // with DismissedAt rather than including it later is what keeps a queue somebody has been
        // clearing for a year from being a scan of everything they cleared.
        await Alerts.Indexes.CreateOneAsync(
            new CreateIndexModel<Alert>(
                Builders<Alert>.IndexKeys.Ascending(a => a.DismissedAt).Descending(a => a.At)),
            cancellationToken: cancellationToken);

        // And the same queue narrowed to one camera, which is what the screen's camera filter asks
        // for. Not covered by the index above: that one orders every camera together, so filtering
        // it would mean reading the whole queue to keep a fraction of it.
        await Alerts.Indexes.CreateOneAsync(
            new CreateIndexModel<Alert>(
                Builders<Alert>.IndexKeys.Ascending(a => a.CameraId).Descending(a => a.At)),
            cancellationToken: cancellationToken);

        // Refresh tokens are looked up by hash on every /api/auth/refresh call, and revoked in
        // bulk by family on reuse detection. The TTL index lets Mongo drop expired and long-dead
        // rows on its own — there is no sweep worker for this collection, unlike Recordings.
        await RefreshTokens.Indexes.CreateOneAsync(
            new CreateIndexModel<RefreshToken>(
                Builders<RefreshToken>.IndexKeys.Ascending(t => t.TokenHash)),
            cancellationToken: cancellationToken);

        await RefreshTokens.Indexes.CreateOneAsync(
            new CreateIndexModel<RefreshToken>(
                Builders<RefreshToken>.IndexKeys.Ascending(t => t.FamilyId)),
            cancellationToken: cancellationToken);

        await RefreshTokens.Indexes.CreateOneAsync(
            new CreateIndexModel<RefreshToken>(
                Builders<RefreshToken>.IndexKeys.Ascending(t => t.ExpiresAt),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }),
            cancellationToken: cancellationToken);

        // Push subscriptions are read whole on every alert, which needs no index, and listed per
        // account whenever somebody opens the notifications screen, which does. Not unique: two
        // accounts signed in on the same browser hold genuinely different subscriptions.
        await PushSubscriptions.Indexes.CreateOneAsync(
            new CreateIndexModel<PushSubscription>(
                Builders<PushSubscription>.IndexKeys.Ascending(s => s.UserId)),
            cancellationToken: cancellationToken);

        // Google's credentials are looked up by hash on every call — the code once at exchange,
        // the access token on every fulfillment request.
        await GoogleAuthorizationCodes.Indexes.CreateOneAsync(
            new CreateIndexModel<GoogleAuthorizationCode>(
                Builders<GoogleAuthorizationCode>.IndexKeys.Ascending(c => c.CodeHash),
                new CreateIndexOptions { Unique = true }),
            cancellationToken: cancellationToken);

        await GoogleAuthorizationCodes.Indexes.CreateOneAsync(
            new CreateIndexModel<GoogleAuthorizationCode>(
                Builders<GoogleAuthorizationCode>.IndexKeys.Ascending(c => c.ExpiresAt),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }),
            cancellationToken: cancellationToken);

        await GoogleTokens.Indexes.CreateOneAsync(
            new CreateIndexModel<GoogleToken>(
                Builders<GoogleToken>.IndexKeys.Ascending(t => t.TokenHash),
                new CreateIndexOptions { Unique = true }),
            cancellationToken: cancellationToken);

        // Expiring access tokens clean themselves up. Refresh tokens carry no ExpiresAt at all and
        // are therefore skipped by this index rather than being given a far-future date to dodge
        // it — Mongo ignores a document whose indexed field is not a date. See GoogleToken for why
        // they must not expire.
        await GoogleTokens.Indexes.CreateOneAsync(
            new CreateIndexModel<GoogleToken>(
                Builders<GoogleToken>.IndexKeys.Ascending(t => t.ExpiresAt),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }),
            cancellationToken: cancellationToken);
    }
}
