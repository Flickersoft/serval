using System.Text.Json;
using Serval.Contracts;

namespace Serval.Ai.Tests;

/// <summary>
/// Pins the wire contract: the exact JSON the module produces and the Server consumes.
///
/// One declaration and one suite is the point of Serval.Contracts: declared per side, a drift
/// between two sets of field names can only be caught by somebody noticing it.
/// </summary>
public class TelemetryContractTests
{
    private static JsonDocument Serialize(IOutputRecord record) =>
        JsonDocument.Parse(TelemetryJson.Serialize(record));

    private static UtteranceDocument MinimalUtterance() => new()
    {
        Id = "id-1",
        Timestamp = DateTimeOffset.Parse("2026-07-15T20:33:08Z"),
        Transcript = "hello",
    };

    [Fact]
    public void Null_fields_are_omitted_entirely()
    {
        // A minimal record: no emotion, language, vision, speaker, conversation. None of those
        // keys may appear at all — an absent field means "undetermined", never null.
        using var doc = Serialize(MinimalUtterance());
        JsonElement root = doc.RootElement;

        foreach (string absent in new[]
                 { "emotion", "language", "audio_event",
                   "speaker", "speaker_source", "conversation_id" })
        {
            Assert.False(root.TryGetProperty(absent, out _), $"'{absent}' should be omitted when null.");
        }
    }

    [Fact]
    public void Type_discriminators_are_stable()
    {
        // The Server dispatches an incoming batch on these strings. Renaming one silently routes
        // its records to the reject pile.
        Assert.Equal("utterance", MinimalUtterance().Type);
        Assert.Equal("diarization", SampleDiarization().Type);
        Assert.Equal("conversation_transcript", SampleTranscript().Type);
        Assert.Equal("scene", SampleScene().Type);
        Assert.Equal("detection", SampleDetection().Type);
        Assert.Equal("sound", SampleSound().Type);
    }

    [Fact]
    public void Every_record_type_carries_the_current_schema_version()
    {
        foreach (IOutputRecord record in
                 new IOutputRecord[]
                 {
                     MinimalUtterance(), SampleDiarization(), SampleTranscript(), SampleScene(),
                     SampleDetection(), SampleSound(),
                 })
        {
            using var doc = Serialize(record);
            Assert.Equal(
                TelemetryJson.SchemaVersion,
                doc.RootElement.GetProperty("schema_version").GetInt32());
        }
    }

    [Fact]
    public void Utterance_uses_the_documented_snake_case_names()
    {
        var full = new UtteranceDocument
        {
            Id = "id-3",
            Timestamp = DateTimeOffset.UtcNow,
            Transcript = "hi",
            Language = "en",
            Emotion = "neutral",
            AudioEvent = "Speech",
            DurationSeconds = 5.5,
            ConversationId = "conv-1",
            Speaker = "speaker_0",
            SpeakerSource = "live",
        };

        using var doc = Serialize(full);
        foreach (string expected in new[]
                 { "type", "schema_version", "id", "conversation_id", "timestamp", "transcript",
                   "language", "emotion", "audio_event", "duration_seconds",
                   "speaker", "speaker_source", "source" })
        {
            Assert.True(doc.RootElement.TryGetProperty(expected, out _), $"missing '{expected}'.");
        }
    }

    [Fact]
    public void An_utterance_carries_no_scene_description()
    {
        // Every completed description is published as its own scene record — including the
        // speech-triggered ones — so a copy here would be duplication, and a staleness figure
        // beside it would freeze a judgement the consumer is better placed to make. Pinned because
        // adding one would look like a harmless convenience.
        var full = new UtteranceDocument
        {
            Id = "id-4",
            Timestamp = DateTimeOffset.UtcNow,
            Transcript = "hi",
        };

        using var doc = Serialize(full);
        Assert.False(doc.RootElement.TryGetProperty("vision", out _));
        Assert.False(doc.RootElement.TryGetProperty("vision_age_seconds", out _));
    }

    [Fact]
    public void Serializing_through_the_interface_would_drop_every_field()
    {
        // The trap TelemetryJson.Serialize exists to avoid: handing System.Text.Json a value
        // statically typed as IOutputRecord emits only the interface's members, collapsing the
        // record to {"type":"utterance"}. Both sinks pass through the helper for this reason.
        IOutputRecord record = MinimalUtterance();

        using var correct = JsonDocument.Parse(TelemetryJson.Serialize(record));
        Assert.Equal("hello", correct.RootElement.GetProperty("transcript").GetString());

        using var collapsed = JsonDocument.Parse(JsonSerializer.Serialize(record, TelemetryJson.Options));
        Assert.False(collapsed.RootElement.TryGetProperty("transcript", out _));
    }

    [Fact]
    public void Diarization_document_carries_segments_and_speaker_count()
    {
        using var doc = Serialize(SampleDiarization());
        JsonElement root = doc.RootElement;

        Assert.Equal("conv-9", root.GetProperty("conversation_id").GetString());
        Assert.Equal(2, root.GetProperty("speaker_count").GetInt32());

        JsonElement segments = root.GetProperty("segments");
        Assert.Equal(2, segments.GetArrayLength());
        Assert.Equal(0.0, segments[0].GetProperty("start").GetDouble(), 6);
        Assert.Equal(0, segments[0].GetProperty("speaker").GetInt32());
    }

    [Fact]
    public void Conversation_transcript_carries_attributed_turns()
    {
        using var doc = Serialize(SampleTranscript());
        JsonElement root = doc.RootElement;

        Assert.Equal("conv-9", root.GetProperty("conversation_id").GetString());
        Assert.Equal("hello there general", root.GetProperty("text").GetString());
        Assert.Equal(1, root.GetProperty("retranscribed_turns").GetInt32());

        JsonElement turns = root.GetProperty("turns");
        Assert.Equal(2, turns.GetArrayLength());
        Assert.Equal("hello there", turns[0].GetProperty("text").GetString());
        Assert.Equal(0, turns[0].GetProperty("speaker").GetInt32());
        Assert.Equal(1, turns[1].GetProperty("speaker").GetInt32());
    }

    [Fact]
    public void Scene_document_records_what_triggered_it()
    {
        // A motion-triggered description has no utterance to ride on, so the trigger and the
        // motion score are the only evidence of why it was produced — and the only way to tell
        // afterwards whether a camera's threshold is set sensibly.
        using var doc = Serialize(SampleScene());
        JsonElement root = doc.RootElement;

        Assert.Equal("motion", root.GetProperty("trigger").GetString());
        Assert.Equal(0.0431, root.GetProperty("motion_score").GetDouble(), 6);
        Assert.Equal(2, root.GetProperty("frame_count").GetInt32());
        Assert.Equal(2.0, root.GetProperty("frame_span_seconds").GetDouble(), 6);
        Assert.Equal("a person walking left to right", root.GetProperty("description").GetString());
    }

    [Fact]
    public void A_scene_without_motion_omits_the_score_rather_than_reporting_zero()
    {
        // Zero would read as "nothing moved", which is a different claim from "motion was not
        // what caused this description".
        var scene = SampleScene();
        scene.Trigger = SceneTrigger.Speech;
        scene.MotionScore = null;

        using var doc = Serialize(scene);
        Assert.False(doc.RootElement.TryGetProperty("motion_score", out _));
    }

    [Fact]
    public void Source_distinguishes_edge_from_server_side_detection()
    {
        // The same detection code runs in two places. A consumer that cannot tell them apart
        // cannot explain why two cameras have different coverage.
        var utterance = MinimalUtterance();
        utterance.Source = TelemetrySource.Server;

        using var doc = Serialize(utterance);
        Assert.Equal("server", doc.RootElement.GetProperty("source").GetString());
        Assert.Equal("module", MinimalUtterance().Source);
    }

    [Fact]
    public void Documents_round_trip_through_the_shared_options()
    {
        // What the Server does on ingest. If serialization and deserialization disagree, a batch
        // is accepted and stored as blanks rather than rejected loudly.
        string json = TelemetryJson.Serialize(SampleTranscript());
        var parsed = JsonSerializer.Deserialize<ConversationTranscriptDocument>(json, TelemetryJson.Options);

        Assert.NotNull(parsed);
        Assert.Equal("conv-9", parsed.ConversationId);
        Assert.Equal(2, parsed.Turns.Count);
        Assert.Equal("general", parsed.Turns[1].Text);
        Assert.Equal(TelemetryJson.SchemaVersion, parsed.SchemaVersion);
    }

    private static DiarizationDocument SampleDiarization() => new()
    {
        ConversationId = "conv-9",
        StartedAt = DateTimeOffset.Parse("2026-07-15T20:31:00Z"),
        AudioSeconds = 12.3,
        SpeakerCount = 2,
        Segments =
        [
            new DiarizationSegment { Start = 0.0, End = 4.2, Speaker = 0 },
            new DiarizationSegment { Start = 4.5, End = 9.1, Speaker = 1 },
        ],
    };

    private static ConversationTranscriptDocument SampleTranscript() => new()
    {
        ConversationId = "conv-9",
        StartedAt = DateTimeOffset.Parse("2026-07-15T20:31:00Z"),
        AudioSeconds = 12.3,
        SpeakerCount = 2,
        Language = "en",
        Text = "hello there general",
        RetranscribedTurns = 1,
        Turns =
        [
            new TranscriptTurn { Start = 0.0, End = 4.2, Speaker = 0, Text = "hello there" },
            new TranscriptTurn { Start = 4.5, End = 9.1, Speaker = 1, Text = "general" },
        ],
    };

    private static SceneDocument SampleScene() => new()
    {
        Id = "scene-1",
        Timestamp = DateTimeOffset.Parse("2026-07-15T20:31:00Z"),
        Description = "a person walking left to right",
        Trigger = SceneTrigger.Motion,
        MotionScore = 0.0431,
        FrameCount = 2,
        FrameSpanSeconds = 2.0,
    };

    private static DetectionDocument SampleDetection() => new()
    {
        Id = "det-1",
        Timestamp = DateTimeOffset.Parse("2026-08-04T12:00:00Z"),
        EndedAt = DateTimeOffset.Parse("2026-08-04T12:00:42Z"),
        Label = "person",
        PeakConfidence = 0.91,
        PeakFrameAt = DateTimeOffset.Parse("2026-08-04T12:00:12Z"),
        FrameCount = 40,
        BestBox = new DetectionBox { X = 0.1, Y = 0.2, Width = 0.3, Height = 0.4, Score = 0.91 },
        Track =
        [
            new DetectionTrackSample
            {
                At = DateTimeOffset.Parse("2026-08-04T12:00:00Z"),
                Box = new DetectionBox { X = 0.1, Y = 0.2, Width = 0.3, Height = 0.4, Score = 0.91 },
            },
            new DetectionTrackSample { At = DateTimeOffset.Parse("2026-08-04T12:00:30Z") },
        ],
        IsAlert = true,
    };

    private static SoundDocument SampleSound() => new()
    {
        Id = "sound-1",
        Timestamp = DateTimeOffset.Parse("2026-07-15T20:31:00Z"),
        Label = "Vehicle horn, car horn, honking",
        Confidence = 0.812,
        IsAlert = false,
        DurationSeconds = 2.4,
        Alternates =
        [
            new SoundAlternate { Label = "Vehicle horn, car horn, honking", Confidence = 0.812 },
            new SoundAlternate { Label = "Vehicle", Confidence = 0.441 },
        ],
    };

    [Fact]
    public void Sound_keeps_the_models_own_label_verbatim()
    {
        // AudioSet labels are comma-bearing English phrases and are stored exactly as the model
        // spells them. Tidying them into slugs server-side would make grouping a decision baked
        // into storage, when it is a presentation choice the client should be free to revisit.
        using var doc = Serialize(SampleSound());
        JsonElement root = doc.RootElement;

        Assert.Equal("Vehicle horn, car horn, honking", root.GetProperty("label").GetString());
        Assert.Equal(0.812, root.GetProperty("confidence").GetDouble(), 6);
        Assert.Equal(2.4, root.GetProperty("duration_seconds").GetDouble(), 6);

        JsonElement alternates = root.GetProperty("alternates");
        Assert.Equal(2, alternates.GetArrayLength());
        Assert.Equal(
            "Vehicle horn, car horn, honking", alternates[0].GetProperty("label").GetString());
        Assert.Equal(0.441, alternates[1].GetProperty("confidence").GetDouble(), 6);
    }

    [Fact]
    public void Sound_always_states_whether_it_is_an_alert()
    {
        // The one field here a person is woken up by. It is written even when false, so a consumer
        // reading absence as "not an alert" would be right by contract rather than by accident.
        using var ordinary = Serialize(SampleSound());
        Assert.True(ordinary.RootElement.TryGetProperty("is_alert", out JsonElement flag));
        Assert.False(flag.GetBoolean());

        SoundDocument alert = SampleSound();
        alert.Label = "Glass";
        alert.IsAlert = true;

        using var raised = Serialize(alert);
        Assert.True(raised.RootElement.GetProperty("is_alert").GetBoolean());
    }

    [Fact]
    public void Sound_uses_the_documented_snake_case_names()
    {
        using var doc = Serialize(SampleSound());
        foreach (string expected in new[]
                 { "type", "schema_version", "id", "timestamp", "label", "confidence",
                   "alternates", "is_alert", "duration_seconds", "source" })
        {
            Assert.True(doc.RootElement.TryGetProperty(expected, out _), $"missing '{expected}'.");
        }

        // Sound records correlate against scene records by timestamp; they never carry a copy.
        Assert.False(doc.RootElement.TryGetProperty("vision", out _));
    }

    [Fact]
    public void Sound_round_trips_through_the_shared_options()
    {
        string json = TelemetryJson.Serialize(SampleSound());
        var parsed = JsonSerializer.Deserialize<SoundDocument>(json, TelemetryJson.Options);

        Assert.NotNull(parsed);
        Assert.Equal("Vehicle horn, car horn, honking", parsed.Label);
        Assert.Equal(2, parsed.Alternates.Count);
        Assert.Equal("Vehicle", parsed.Alternates[1].Label);
        Assert.Equal(TelemetryJson.SchemaVersion, parsed.SchemaVersion);
    }

    [Fact]
    public void A_detection_serialises_every_field_in_snake_case()
    {
        using JsonDocument doc = Serialize(SampleDetection());
        JsonElement root = doc.RootElement;

        Assert.Equal("detection", root.GetProperty("type").GetString());
        Assert.Equal("person", root.GetProperty("label").GetString());
        Assert.Equal(0.91, root.GetProperty("peak_confidence").GetDouble(), 6);
        Assert.Equal(40, root.GetProperty("frame_count").GetInt32());
        Assert.True(root.GetProperty("is_alert").GetBoolean());

        JsonElement box = root.GetProperty("best_box");
        Assert.Equal(0.1, box.GetProperty("x").GetDouble(), 6);
        Assert.Equal(0.4, box.GetProperty("height").GetDouble(), 6);
        Assert.Equal(0.91, box.GetProperty("score").GetDouble(), 6);
    }

    [Fact]
    public void An_open_episode_says_so_by_omitting_its_end()
    {
        // TelemetryJson drops nulls, so "still present" is the absence of ended_at. A consumer has
        // to be able to tell that from a closed episode, and this is the whole difference.
        DetectionDocument open = SampleDetection();
        open.EndedAt = null;

        using JsonDocument doc = Serialize(open);

        Assert.False(doc.RootElement.TryGetProperty("ended_at", out _));
        Assert.True(Serialize(SampleDetection()).RootElement.TryGetProperty("ended_at", out _));
    }

    [Fact]
    public void A_detection_round_trips_through_json()
    {
        using JsonDocument doc = Serialize(SampleDetection());

        DetectionDocument? restored = doc.Deserialize<DetectionDocument>(TelemetryJson.Options);

        Assert.NotNull(restored);
        Assert.Equal("person", restored.Label);
        Assert.NotNull(restored.BestBox);
        Assert.Equal(0.3, restored.BestBox.Width, 6);
    }

    [Fact]
    public void A_track_serialises_run_length_encoded_with_a_gap_as_an_empty_box_list()
    {
        // A gap is a sample with no box rather than a sample that went missing. That is the whole of
        // what a gap means — "nothing here from now on" — and it has to survive the round trip,
        // because a consumer that read it as an ordinary sample would hold the previous box over
        // footage the object had already left.
        using JsonDocument doc = Serialize(SampleDetection());

        JsonElement track = doc.RootElement.GetProperty("track");
        Assert.Equal(2, track.GetArrayLength());
        Assert.Equal(0.3, track[0].GetProperty("box").GetProperty("width").GetDouble(), 6);
        Assert.False(track[1].TryGetProperty("box", out _));

        DetectionDocument restored = doc.Deserialize<DetectionDocument>(TelemetryJson.Options)!;

        Assert.NotNull(restored.Track);
        Assert.Equal(2, restored.Track.Count);
        Assert.NotNull(restored.Track[0].Box);
        Assert.Equal(DateTimeOffset.Parse("2026-08-04T12:00:30Z"), restored.Track[1].At);
        Assert.Null(restored.Track[1].Box);
    }
}
