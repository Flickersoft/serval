using System.Linq.Expressions;
using MongoDB.Bson.Serialization;
using Serval.Contracts;

namespace Serval.Server.Storage;

/// <summary>
/// Teaches the MongoDB driver how to store the shared telemetry documents.
///
/// This exists as class maps rather than attributes for one reason: the documents live in
/// Serval.Contracts, which the CameraModule also references, and an edge worker has no business
/// acquiring a database driver just to describe its own output. Persistence concerns belong to the
/// side that persists.
///
/// Must run before the first serialization, so it is called at the very top of Program.cs.
/// </summary>
public static class TelemetryClassMaps
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        // The natural key is the id the producer already assigned, in every case. That is what
        // makes ingest idempotent: re-delivering a batch after a failed acknowledgement replaces
        // records rather than duplicating them, which is what lets the module's outbox retry
        // freely. For a conversation-keyed record, re-processing a crash-recovered conversation
        // replaces it in place.
        MapDocument<UtteranceDocument>(d => d.Id);
        MapDocument<DiarizationDocument>(d => d.ConversationId);
        MapDocument<ConversationTranscriptDocument>(d => d.ConversationId);
        MapDocument<SceneDocument>(d => d.Id);
        MapDocument<DetectionDocument>(d => d.Id);
        MapDocument<SoundDocument>(d => d.Id);

        // The nested types need their own registrations: AutoMap keeps the C# casing on them, so
        // without these a box stores as "X"/"Y"/"Width"/"Height" against a wire that says
        // "x"/"y"/"width"/"height", and the App silently reads nulls.
        MapNested<DetectionBox>();
        MapNested<DetectionTrackSample>();
        MapNested<SoundAlternate>();
        MapNested<DiarizationSegment>();
        MapNested<TranscriptTurn>();
    }

    private static void MapDocument<T>(Expression<Func<T, string>> id) where T : IOutputRecord =>
        BsonClassMap.RegisterClassMap<T>(map =>
        {
            map.AutoMap();
            map.SetIgnoreExtraElements(true); // a field the type does not know must not fail the read
            map.MapIdMember(id);
            map.UnmapProperty(nameof(IOutputRecord.Type)); // constant per collection
            MapSnakeCase(map);
        });

    private static void MapNested<T>() =>
        BsonClassMap.RegisterClassMap<T>(map =>
        {
            map.AutoMap();
            map.SetIgnoreExtraElements(true);
            MapSnakeCase(map);
        });

    /// <summary>
    /// Stores each field under the name it has on the wire.
    ///
    /// Worth the reflection: the App reads these documents straight back out, so keeping the
    /// stored names identical to the JSON ones means there is exactly one vocabulary across the
    /// module, the database and the client — and no mapping layer to fall out of step.
    /// </summary>
    private static void MapSnakeCase<T>(BsonClassMap<T> map)
    {
        foreach (BsonMemberMap member in map.DeclaredMemberMaps.ToList())
        {
            // The id member is already mapped to _id. Renaming it to its wire name here would
            // undo that and leave the collection without a meaningful primary key, which is
            // precisely what makes re-delivery idempotent.
            if (ReferenceEquals(member, map.IdMemberMap))
            {
                continue;
            }

            object[] attributes = member.MemberInfo
                .GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), inherit: false);

            if (attributes.Length > 0
                && attributes[0] is System.Text.Json.Serialization.JsonPropertyNameAttribute json)
            {
                member.SetElementName(json.Name);
            }
        }
    }
}
