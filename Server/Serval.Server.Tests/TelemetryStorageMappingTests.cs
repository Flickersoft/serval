using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Serval.Contracts;
using Serval.Server.Storage;

namespace Serval.Server.Tests;

/// <summary>
/// Pins how the shared telemetry documents are stored.
///
/// The documents themselves carry no Mongo attributes — they are shared with the CameraModule, and
/// an edge worker should not acquire a database driver to describe its own output — so the mapping
/// lives in <see cref="TelemetryClassMaps"/> and is invisible from the type. That makes it exactly
/// the kind of thing that can break without anything looking wrong, hence these.
///
/// No database is touched: BSON serialization is a pure in-memory transformation.
/// </summary>
public class TelemetryStorageMappingTests
{
    // Through BsonRegistration rather than TelemetryClassMaps directly: these documents carry
    // DateTimeOffset, and serializing one before the DateTimeOffsetSerializer is registered makes
    // the driver cache its default and the later registration throw. Going through the one entry
    // point removes the ordering hazard between parallel fixtures.
    public TelemetryStorageMappingTests() => BsonRegistration.Register();

    private static UtteranceDocument Utterance() => new()
    {
        Id = "utt-1",
        CameraId = "front-door",
        ReceivedAt = DateTimeOffset.Parse("2026-07-15T20:33:10Z"),
        Timestamp = DateTimeOffset.Parse("2026-07-15T20:33:08Z"),
        Transcript = "hello",
        ConversationId = "conv-1",
        Speaker = "speaker_0",
        SpeakerSource = "live",
        DurationSeconds = 1.5,
    };

    [Fact]
    public void The_producer_assigned_id_becomes_the_mongo_id()
    {
        // This is what makes ingest idempotent. Without it every re-delivery after a failed
        // acknowledgement would insert a duplicate, and the module's outbox retries by design.
        BsonDocument stored = Utterance().ToBsonDocument();

        Assert.Equal("utt-1", stored["_id"].AsString);
        Assert.False(stored.Contains("id"), "the id must not also be stored under its wire name.");
    }

    [Fact]
    public void A_conversation_has_exactly_one_diarization_and_one_transcript()
    {
        // Both are keyed by conversation id, so reprocessing a crash-recovered conversation
        // replaces its records rather than accumulating contradictory ones.
        var diarization = new DiarizationDocument
        {
            ConversationId = "conv-9",
            StartedAt = DateTimeOffset.UtcNow,
        };

        var transcript = new ConversationTranscriptDocument
        {
            ConversationId = "conv-9",
            StartedAt = DateTimeOffset.UtcNow,
        };

        Assert.Equal("conv-9", diarization.ToBsonDocument()["_id"].AsString);
        Assert.Equal("conv-9", transcript.ToBsonDocument()["_id"].AsString);
    }

    [Fact]
    public void Fields_are_stored_under_their_wire_names()
    {
        // One vocabulary across the module, the database and the App. If storage used C# names
        // instead, every query and every client would need a translation layer that could drift.
        BsonDocument stored = Utterance().ToBsonDocument();

        foreach (string expected in new[]
                 { "camera_id", "received_at", "schema_version", "conversation_id", "timestamp",
                   "transcript", "duration_seconds", "speaker", "speaker_source", "source" })
        {
            Assert.True(stored.Contains(expected), $"missing '{expected}' in the stored document.");
        }
    }

    [Fact]
    public void The_constant_type_discriminator_is_not_stored()
    {
        // Every document in a collection has the same type; storing it would be a byte per record
        // that says what the collection already says.
        Assert.False(Utterance().ToBsonDocument().Contains("type"));
        Assert.False(new SceneDocument { Id = "s-1" }.ToBsonDocument().Contains("type"));
    }

    [Fact]
    public void Scene_documents_store_the_evidence_for_why_they_exist()
    {
        // A motion-triggered description has no utterance to hang on, so the trigger and score
        // are the only record of what caused it — and the only way to judge a camera's threshold
        // after the fact.
        var scene = new SceneDocument
        {
            Id = "scene-1",
            CameraId = "front-door",
            Timestamp = DateTimeOffset.UtcNow,
            Description = "a person walking left to right",
            Trigger = SceneTrigger.Motion,
            MotionScore = 0.0431,
            FrameCount = 2,
            FrameSpanSeconds = 2.0,
            Source = TelemetrySource.Server,
        };

        BsonDocument stored = scene.ToBsonDocument();

        Assert.Equal("scene-1", stored["_id"].AsString);
        Assert.Equal("motion", stored["trigger"].AsString);
        Assert.Equal(0.0431, stored["motion_score"].AsDouble, 6);
        Assert.Equal(2, stored["frame_count"].AsInt32);
        Assert.Equal("server", stored["source"].AsString);
    }

    [Fact]
    public void Sound_documents_store_the_label_and_its_shortlist_under_wire_names()
    {
        var sound = new SoundDocument
        {
            Id = "sound-1",
            CameraId = "front-door",
            Timestamp = DateTimeOffset.UtcNow,
            Label = "Vehicle horn, car horn, honking",
            Confidence = 0.812,
            IsAlert = false,
            DurationSeconds = 2.4,
            Alternates =
            [
                new SoundAlternate { Label = "Vehicle horn, car horn, honking", Confidence = 0.812 },
                new SoundAlternate { Label = "Vehicle", Confidence = 0.441 },
            ],
            Source = TelemetrySource.Server,
        };

        BsonDocument stored = sound.ToBsonDocument();

        Assert.Equal("sound-1", stored["_id"].AsString);
        Assert.False(stored.Contains("type")); // the collection already says it
        Assert.Equal("front-door", stored["camera_id"].AsString);

        // The model's own string, commas intact. Storage is not a place to tidy labels.
        Assert.Equal("Vehicle horn, car horn, honking", stored["label"].AsString);

        // Written even when false: this is the field an alert query filters on, and a missing
        // one would silently exclude every ordinary sound from a "not an alert" match.
        Assert.False(stored["is_alert"].AsBoolean);

        BsonArray alternates = stored["alternates"].AsBsonArray;
        Assert.Equal(2, alternates.Count);
        Assert.Equal("Vehicle", alternates[1]["label"].AsString);
        Assert.Equal(0.441, alternates[1]["confidence"].AsDouble, 6);
    }

    [Fact]
    public void Sound_documents_round_trip_through_bson()
    {
        var sound = new SoundDocument
        {
            Id = "sound-2",
            CameraId = "drive",
            Timestamp = DateTimeOffset.Parse("2026-07-15T20:31:00Z"),
            Label = "Glass",
            Confidence = 0.71,
            IsAlert = true,
            DurationSeconds = 1.8,
            Alternates = [new SoundAlternate { Label = "Glass", Confidence = 0.71 }],
        };

        var restored = MongoDB.Bson.Serialization.BsonSerializer
            .Deserialize<SoundDocument>(sound.ToBsonDocument());

        Assert.Equal("sound-2", restored.Id);
        Assert.Equal("Glass", restored.Label);
        Assert.True(restored.IsAlert);
        Assert.Single(restored.Alternates);
        Assert.Equal(0.71, restored.Alternates[0].Confidence, 6);
    }

    [Fact]
    public void An_utterance_stores_no_scene_description()
    {
        // Removed in schema 5. Descriptions live in their own collection and are correlated by
        // timestamp; a copy here would be a second place for the same text to drift.
        var utterance = new UtteranceDocument
        {
            Id = "utt-1",
            CameraId = "front-door",
            Timestamp = DateTimeOffset.UtcNow,
            Transcript = "hello",
        };

        BsonDocument stored = utterance.ToBsonDocument();

        Assert.False(stored.Contains("vision"));
        Assert.False(stored.Contains("vision_age_seconds"));
    }

    [Fact]
    public void Documents_round_trip_through_bson()
    {
        var transcript = new ConversationTranscriptDocument
        {
            ConversationId = "conv-9",
            CameraId = "front-door",
            StartedAt = DateTimeOffset.Parse("2026-07-15T20:31:00Z"),
            SpeakerCount = 2,
            Text = "hello there general",
            RetranscribedTurns = 1,
            Turns =
            [
                new TranscriptTurn { Start = 0.0, End = 4.2, Speaker = 0, Text = "hello there" },
                new TranscriptTurn { Start = 4.5, End = 9.1, Speaker = 1, Text = "general" },
            ],
        };

        var restored = MongoDB.Bson.Serialization.BsonSerializer
            .Deserialize<ConversationTranscriptDocument>(transcript.ToBsonDocument());

        Assert.Equal("conv-9", restored.ConversationId);
        Assert.Equal("front-door", restored.CameraId);
        Assert.Equal(2, restored.Turns.Count);
        Assert.Equal("general", restored.Turns[1].Text);
        Assert.Equal(1, restored.RetranscribedTurns);
    }

    [Fact]
    public void Unknown_fields_from_a_newer_schema_do_not_break_reads()
    {
        // A module upgraded ahead of its server will send fields this build has never heard of.
        // Dropping them is right; refusing to read the record would take out the whole camera.
        BsonDocument stored = Utterance().ToBsonDocument();
        stored.Add("something_from_schema_5", "surprise");

        var restored = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<UtteranceDocument>(stored);

        Assert.Equal("hello", restored.Transcript);
    }

    private static DetectionDocument Detection() => new()
    {
        Id = "det-1",
        CameraId = "front-door",
        Timestamp = new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero),
        EndedAt = new DateTimeOffset(2026, 8, 4, 12, 0, 42, TimeSpan.Zero),
        Label = "person",
        PeakConfidence = 0.91,
        PeakFrameAt = new DateTimeOffset(2026, 8, 4, 12, 0, 12, TimeSpan.Zero),
        FrameCount = 40,
        BestBox = new DetectionBox { X = 0.1, Y = 0.2, Width = 0.3, Height = 0.4, Score = 0.91 },
        IsAlert = true,
        Source = TelemetrySource.Server,
    };

    [Fact]
    public void Detection_documents_store_snake_case_including_the_nested_box()
    {
        // The nested type is the risk. AutoMap keeps C# casing on it, so without its own class map
        // a box stores as "X"/"Y"/"Width"/"Height" against a wire that says lowercase — and the
        // App reads four nulls with nothing failing anywhere.
        BsonDocument stored = Detection().ToBsonDocument();

        Assert.Equal("det-1", stored["_id"].AsString);
        Assert.Equal("person", stored["label"].AsString);
        Assert.Equal(0.91, stored["peak_confidence"].AsDouble, 6);
        Assert.Equal(40, stored["frame_count"].AsInt32);
        Assert.True(stored["is_alert"].AsBoolean);

        BsonDocument box = stored["best_box"].AsBsonDocument;
        Assert.Equal(0.1, box["x"].AsDouble, 6);
        Assert.Equal(0.4, box["height"].AsDouble, 6);
        Assert.Equal(0.91, box["score"].AsDouble, 6);
    }

    [Fact]
    public void A_detection_track_stores_snake_case_with_its_gaps_intact()
    {
        // Same nested-type hazard as the box, one level deeper: without its own class map a sample
        // stores as "At"/"Box" and the App reads an episode whose object never moved. The gap has to
        // survive too, since it is the only thing distinguishing "not there" from "still where it
        // was".
        DetectionDocument detection = Detection();
        detection.Track =
        [
            new DetectionTrackSample
            {
                At = new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero),
                Box = new DetectionBox { X = 0.1, Y = 0.2, Width = 0.3, Height = 0.4, Score = 0.91 },
            },
            new DetectionTrackSample
            {
                At = new DateTimeOffset(2026, 8, 4, 12, 0, 20, TimeSpan.Zero),
            },
        ];

        BsonArray track = detection.ToBsonDocument()["track"].AsBsonArray;

        Assert.Equal(2, track.Count);

        BsonDocument first = track[0].AsBsonDocument;
        Assert.True(first.Contains("at"));
        Assert.Equal(0.3, first["box"].AsBsonDocument["width"].AsDouble, 6);

        // Stored as an explicit null rather than left out, the same way an open episode's end is:
        // a reader distinguishing "no box here" from "this field was never written" needs the
        // difference to survive storage.
        Assert.Equal(BsonNull.Value, track[1].AsBsonDocument["box"]);
    }

    [Fact]
    public void An_open_detection_stores_a_null_end_rather_than_omitting_it()
    {
        // Null is the wire's way of saying "still there". A consumer distinguishing "open" from
        // "this field was not written" needs the difference to survive storage.
        DetectionDocument open = Detection();
        open.EndedAt = null;

        BsonDocument stored = open.ToBsonDocument();

        Assert.True(stored.Contains("ended_at"));
        Assert.Equal(BsonNull.Value, stored["ended_at"]);
    }

    [Fact]
    public void A_detection_round_trips()
    {
        DetectionDocument restored = BsonSerializer.Deserialize<DetectionDocument>(
            Detection().ToBsonDocument());

        Assert.Equal("person", restored.Label);
        Assert.NotNull(restored.BestBox);
        Assert.Equal(0.3, restored.BestBox.Width, 6);
        Assert.True(restored.IsAlert);
    }
}
