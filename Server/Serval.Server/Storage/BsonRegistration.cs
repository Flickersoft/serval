using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using Serval.Server.Auth;
using Serval.Server.Cameras;

namespace Serval.Server.Storage;

/// <summary>
/// Every process-wide MongoDB serialization decision, in one place, run once before anything is
/// serialized. The driver's registry is global mutable state that throws on a second registration
/// for the same type, so the alternative — scattering <c>RegisterSerializer</c> calls at each
/// call site — makes the order load-bearing and the test suite's parallel fixtures unsafe.
/// </summary>
public static class BsonRegistration
{
    private static readonly Lock Gate = new();
    private static bool _registered;

    /// <summary>
    /// Idempotent and thread-safe, so a test fixture can call it without knowing whether another
    /// fixture already did. Must precede the first serialization; Program.cs calls it first thing.
    /// </summary>
    public static void Register()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            _registered = true;

            // Store DateTimeOffset as a BSON UTC date, not the driver's default [ticks, offset]
            // array, so time-range queries and sorts on timestamps work natively in Mongo.
            BsonSerializer.RegisterSerializer(new DateTimeOffsetSerializer(BsonType.DateTime));

            // RefreshToken.FamilyId is a Guid, and the driver refuses to serialize one at all
            // unless told which byte layout to use — there is no default. Standard is the
            // cross-driver-compatible layout (as opposed to CSharpLegacy's driver-specific one),
            // which matters here only in that it's the one every current guide recommends.
            BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));

            // Store StreamRole by name rather than by its ordinal. The default is the ordinal,
            // which makes the *declaration order* of the enum a storage format: inserting a member
            // or reordering them silently reassigns the role of every camera already in the
            // database, and a document missing the field reads back as Record. Neither failure
            // touches a compiler or a test.
            //
            // Storage is PascalCase ("Record") while the JSON wire format is camelCase ("record",
            // via StreamRoleConverter). That asymmetry is deliberate: the JSON is hand-edited in
            // the Scalar UI and documented in camelCase, the BSON is not hand-edited at all.
            BsonSerializer.RegisterSerializer(new EnumSerializer<StreamRole>(BsonType.String));

            // Same reasoning as StreamRole above: a user's Role must not depend on where it sits
            // in the enum declaration.
            BsonSerializer.RegisterSerializer(new EnumSerializer<Role>(BsonType.String));

            // The telemetry documents are shared with the CameraModule, so they carry no Mongo
            // attributes — an edge worker has no business taking a database driver dependency to
            // describe its own output. Their storage mapping is registered here instead.
            TelemetryClassMaps.Register();
        }
    }
}
