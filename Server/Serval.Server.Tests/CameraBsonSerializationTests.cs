using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Serval.Ai;
using Serval.Server.Cameras;
using Serval.Server.Storage;

namespace Serval.Server.Tests;

/// <summary>
/// Pins how a camera is stored, because none of it is visible from the type.
///
/// One of these guards silent data corruption rather than a crash: an enum stored by ordinal makes
/// the declaration order of <see cref="StreamRole"/> a storage format, so inserting or reordering
/// a member would reassign the role of every stored camera with nothing to compile against.
///
/// No database is touched: BSON serialization is a pure in-memory transformation.
/// </summary>
public class CameraBsonSerializationTests
{
    public CameraBsonSerializationTests() => BsonRegistration.Register();

    private static Camera Camera() => new()
    {
        Id = "front-door",
        Name = "Front Door",
        Streams =
        [
            new CameraStream
            {
                Name = "main",
                Url = "rtsp://cam/main",
                Roles = [StreamRole.Record, StreamRole.Live],
            },
            new CameraStream { Name = "sub", Url = "rtsp://cam/sub", Roles = [StreamRole.Detect] },
        ],
    };

    [Fact]
    public void Roles_are_stored_by_name_not_by_ordinal()
    {
        // The ordinal is the driver's default and it is a trap: inserting a member into StreamRole
        // or reordering it would silently reassign the role of every camera already stored, with
        // nothing to compile against and no read error.
        BsonDocument stored = Camera().ToBsonDocument();
        BsonArray roles = stored["Streams"][0]["Roles"].AsBsonArray;

        Assert.Equal(["Record", "Live"], roles.Select(r => r.AsString));
    }

    /// <summary>
    /// Every camera stored before <see cref="Camera.Recording"/> existed has no such field, and a
    /// bool deserializing to its CLR default would stop all of them recording on the next restart —
    /// silently, since nothing about the document or the role assignment would have changed. What
    /// prevents it is the property initializer: the driver constructs first and sets only the
    /// members the document carries.
    /// </summary>
    [Fact]
    public void A_document_with_no_recording_field_deserializes_to_true()
    {
        var stored = new BsonDocument { { "_id", "front-door" }, { "Name", "Front Door" } };

        var camera = BsonSerializer.Deserialize<Camera>(stored);

        Assert.True(camera.Recording);
    }

    [Fact]
    public void Recording_round_trips_through_bson()
    {
        Camera camera = Camera();
        camera.Recording = false;

        var read = BsonSerializer.Deserialize<Camera>(camera.ToBsonDocument());

        Assert.False(read.Recording);
    }

    /// <summary>
    /// An untuned camera stores no <c>AudioTuning</c> field at all (see
    /// <see cref="An_untuned_camera_stores_no_audioTuning_field"/>), so reading one back has to
    /// give null rather than throw.
    /// </summary>
    [Fact]
    public void A_document_with_no_audioTuning_field_deserializes_to_null()
    {
        var stored = new BsonDocument { { "_id", "front-door" }, { "Name", "Front Door" } };

        var camera = BsonSerializer.Deserialize<Camera>(stored);

        Assert.Null(camera.AudioTuning);
    }

    [Fact]
    public void An_untuned_camera_stores_no_audioTuning_field()
    {
        // [BsonIgnoreIfNull], so the documents of cameras nobody has tuned are unchanged.
        BsonDocument stored = Camera().ToBsonDocument();

        Assert.False(stored.Contains("AudioTuning"));
    }

    /// <summary>
    /// The gain is a plain double with no <c>[BsonIgnoreIfNull]</c>, so it is always present; the gate
    /// is nullable and absent until set. Read back from a document written before either existed, both
    /// have to land on "as recorded, not gated" rather than throwing — which is the state of every
    /// camera already stored.
    /// </summary>
    [Fact]
    public void A_document_with_no_playback_audio_fields_deserializes_to_no_gain()
    {
        var stored = new BsonDocument { { "_id", "front-door" }, { "Name", "Front Door" } };

        var camera = BsonSerializer.Deserialize<Camera>(stored);

        Assert.Equal(0, camera.PlaybackGainDb);
        Assert.Null(camera.PlaybackGateRmsThreshold);
    }

    [Fact]
    public void An_ungated_camera_stores_no_playback_gate_field()
    {
        BsonDocument stored = Camera().ToBsonDocument();

        Assert.False(stored.Contains("PlaybackGateRmsThreshold"));
    }

    [Fact]
    public void Playback_audio_round_trips_through_bson()
    {
        Camera camera = Camera();
        camera.PlaybackGainDb = 12;
        camera.PlaybackGateRmsThreshold = 0.0006;

        var restored = BsonSerializer.Deserialize<Camera>(camera.ToBsonDocument());

        // Exactly, not approximately, for the reason the threshold test below gives: the App compares
        // what it sent against what it read back to decide whether a save changed anything.
        Assert.Equal(12, restored.PlaybackGainDb);
        Assert.Equal(0.0006, restored.PlaybackGateRmsThreshold);
    }

    [Fact]
    public void Audio_tuning_round_trips_through_bson()
    {
        Camera camera = Camera();
        camera.AudioTuning = new CameraAudioTuning
        {
            SpeechGateRmsThreshold = 0.0015f,
            VadThreshold = 0.7f,
            SoundGateRmsThreshold = 0.002f,
        };

        var restored = BsonSerializer.Deserialize<Camera>(camera.ToBsonDocument());

        Assert.NotNull(restored.AudioTuning);
        Assert.Equal(0.0015f, restored.AudioTuning.SpeechGateRmsThreshold);
        Assert.Equal(0.7f, restored.AudioTuning.VadThreshold);
        Assert.Equal(0.002f, restored.AudioTuning.SoundGateRmsThreshold);
    }

    /// <summary>
    /// A threshold must survive storage bit-for-bit, not approximately.
    ///
    /// These were <c>float</c> once, and the effect was not a rounding artefact anyone could shrug
    /// at: the App compares the record it sent against the record it read back to decide what is
    /// still unsaved, so a value that changed in its last digits on the way through made the
    /// settings screen report the change as pending forever, however many times it was saved.
    ///
    /// The value below is deliberately one <c>float</c> cannot hold — it is what the App's
    /// logarithmic slider actually produces, rather than a tidy decimal that would survive the
    /// narrowing by luck.
    /// </summary>
    [Fact]
    public void A_threshold_survives_storage_exactly()
    {
        const double fromSlider = 0.003162277660168379;

        Camera camera = Camera();
        camera.AudioTuning = new CameraAudioTuning { SpeechGateRmsThreshold = fromSlider };

        var restored = BsonSerializer.Deserialize<Camera>(camera.ToBsonDocument());

        // Assert.Equal with no tolerance, on purpose: "close enough" is what broke it.
        Assert.Equal(fromSlider, restored.AudioTuning!.SpeechGateRmsThreshold);
        Assert.NotEqual(
            (double)(float)fromSlider,
            restored.AudioTuning.SpeechGateRmsThreshold!.Value);
    }

    [Fact]
    public void A_partially_tuned_camera_keeps_its_unset_thresholds_null()
    {
        Camera camera = Camera();
        camera.AudioTuning = new CameraAudioTuning { SpeechGateRmsThreshold = 0.0015f };

        var restored = BsonSerializer.Deserialize<Camera>(camera.ToBsonDocument());

        Assert.Equal(0.0015f, restored.AudioTuning!.SpeechGateRmsThreshold);
        Assert.Null(restored.AudioTuning.VadThreshold);
        Assert.Null(restored.AudioTuning.SoundGateRmsThreshold);
    }

    [Fact]
    public void Detection_tuning_round_trips_including_its_masks()
    {
        // Masks are the one per-camera setting with real structure — a nested type inside a nested
        // type — so this is where the storage mapping is most likely to be quietly wrong.
        Camera camera = Camera();
        camera.DetectionTuning = new CameraDetectionTuning
        {
            Classes = ["person", "dog"],
            DescribeClasses = ["person"],
            ScoreThreshold = 0.42,
            TrackConfirmSeconds = 3,
            Masks =
            [
                new DetectionMask
                {
                    Name = "road",
                    Points = [0, 0, 1, 0, 1, 0.3, 0, 0.3],
                    Classes = ["car", "truck"],
                },
            ],
        };

        var restored = BsonSerializer.Deserialize<Camera>(camera.ToBsonDocument());

        Assert.NotNull(restored.DetectionTuning);
        Assert.Equal(["person", "dog"], restored.DetectionTuning.Classes!);
        Assert.Equal(["person"], restored.DetectionTuning.DescribeClasses!);
        Assert.Equal(0.42, restored.DetectionTuning.ScoreThreshold);
        Assert.Equal(3, restored.DetectionTuning.TrackConfirmSeconds);

        DetectionMask mask = Assert.Single(restored.DetectionTuning.Masks!);
        Assert.Equal("road", mask.Name);
        Assert.Equal([0, 0, 1, 0, 1, 0.3, 0, 0.3], mask.Points);
        Assert.Equal(["car", "truck"], mask.Classes!);
    }

    [Fact]
    public void An_untuned_camera_stores_no_detection_field_at_all()
    {
        // [BsonIgnoreIfNull], and it matters: a null field would make "this camera has custom
        // detection" true for every camera that has none.
        BsonDocument document = Camera().ToBsonDocument();

        Assert.False(document.Contains("DetectionTuning"));
    }
}
