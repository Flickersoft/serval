using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Serval.Server.Storage;

namespace Serval.Server.GoogleHome;

/// <summary>
/// Whether one camera is switched on <em>for Google</em>.
///
/// <para><b>This is a Google-facing state and nothing else.</b> Switching a camera off here stops
/// Serval offering it a stream and reports it off in the Home app. It does not stop the camera:
/// recording, detection, alerts and the App's own live view carry on exactly as before, and
/// nothing in <c>Cameras/</c> or in the App can see this flag. That separation is the whole point —
/// somebody turning a camera off on a kitchen display must not silently stop the recording that
/// the house is relying on.</para>
///
/// <para><b>Its own collection rather than a field on the camera.</b> A field would put Google's
/// vocabulary in the document every other subsystem reads, and would travel in a configuration
/// backup restored onto another machine — where it would arrive as a camera mysteriously switched
/// off. Keyed by camera id, so a deleted camera's row is simply never read again.</para>
///
/// <para><b>Absent means on.</b> Every camera is available until somebody says otherwise, so a
/// fresh deployment writes nothing and a camera added later needs no row. Only the deliberate act
/// of switching one off is stored.</para>
/// </summary>
[BsonIgnoreExtraElements] // extra-element tolerance, as every model here keeps
public sealed class GoogleCameraSwitch
{
    /// <summary>The camera id, which is also this integration's device id.</summary>
    [BsonId]
    public required string CameraId { get; set; }

    public required bool On { get; set; }

    /// <summary>When it was last switched, for the admin card and for support questions.</summary>
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Reads and writes <see cref="GoogleCameraSwitch"/>.</summary>
public sealed class GoogleCameraSwitchStore
{
    private readonly MongoContext _mongo;

    public GoogleCameraSwitchStore(MongoContext mongo) => _mongo = mongo;

    /// <summary>
    /// The cameras that are switched <b>off</b>, by id. Only the exceptions are stored, so this is
    /// empty on any deployment where nobody has switched anything off — which is most of them.
    /// </summary>
    public async Task<HashSet<string>> OffAsync(CancellationToken ct)
    {
        List<GoogleCameraSwitch> off = await _mongo.GoogleCameraSwitches
            .Find(s => !s.On)
            .ToListAsync(ct);

        return [.. off.Select(s => s.CameraId)];
    }

    /// <summary>Whether one camera is on. Absent means on — see <see cref="GoogleCameraSwitch"/>.</summary>
    public async Task<bool> IsOnAsync(string cameraId, CancellationToken ct)
    {
        GoogleCameraSwitch? state = await _mongo.GoogleCameraSwitches
            .Find(s => s.CameraId == cameraId)
            .FirstOrDefaultAsync(ct);

        return state?.On ?? true;
    }

    /// <summary>
    /// Records a camera as on or off.
    ///
    /// <para>An upsert rather than an insert-or-update pair: Google will happily send the same
    /// command twice, and the second one must be a no-op rather than a duplicate-key error on a
    /// voice command.</para>
    /// </summary>
    public Task SetAsync(string cameraId, bool on, CancellationToken ct) =>
        _mongo.GoogleCameraSwitches.UpdateOneAsync(
            s => s.CameraId == cameraId,
            Builders<GoogleCameraSwitch>.Update
                .Set(s => s.On, on)
                .Set(s => s.ChangedAt, DateTimeOffset.UtcNow)
                .SetOnInsert(s => s.CameraId, cameraId),
            new UpdateOptions { IsUpsert = true },
            ct);

    /// <summary>
    /// Forgets every switch, which is to say switches everything back on.
    ///
    /// <para>Called when the account is unlinked: the switches were set from the Google Home app,
    /// so they belong to that link. Leaving them would have a camera silently unavailable to
    /// whoever links next, with the reason recorded nowhere they can see.</para>
    /// </summary>
    public Task ClearAsync(CancellationToken ct) =>
        _mongo.GoogleCameraSwitches.DeleteManyAsync(FilterDefinition<GoogleCameraSwitch>.Empty, ct);
}
