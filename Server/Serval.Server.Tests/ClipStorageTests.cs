using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Serval.Contracts;
using Serval.Server.Clips;
using Serval.Server.Configuration;
using Serval.Server.Storage;

namespace Serval.Server.Tests;

/// <summary>
/// Where a clip's bytes go, and how a clip is stored.
///
/// The path matters more than it looks. The retention sweep deletes inside
/// <c>Media.Root/{cameraId}</c> and nowhere else, so a clip landing under a camera would be pruned
/// with the footage it was made to outlive — and the disk scan does not recurse, so a clip one
/// level down would measure as zero bytes and the only footage that never rolls off would be the
/// only footage missing from the storage figures.
/// </summary>
public class ClipStorageTests
{
    public ClipStorageTests()
    {
        BsonRegistration.Register();
        TelemetryClassMaps.Register();
    }

    private static ClipStorage Storage(string root = "/srv/media", string clips = "clips") =>
        new(Options.Create(new ServerOptions { Media = new MediaOptions { Root = root, ClipsRoot = clips } }));

    [Fact]
    public void Clips_live_beside_the_camera_directories_rather_than_inside_one()
    {
        Assert.Equal(Path.Combine("/srv/media", "clips"), Storage().Root);
    }

    [Fact]
    public void An_absolute_clips_root_points_clips_at_another_volume()
    {
        // Path.Combine keeps an absolute second argument, which is how a deployment puts clips on
        // slower, larger storage than the recording volume.
        Assert.Equal("/mnt/archive/clips", Storage(clips: "/mnt/archive/clips").Root);
    }

    [Fact]
    public void A_clip_is_two_flat_files_named_for_it()
    {
        // Flat rather than a directory each, because DiskUsageScanner refuses to recurse.
        var id = ObjectId.GenerateNewId();
        ClipStorage storage = Storage();

        Assert.Equal(Path.Combine(storage.Root, $"{id}.mp4"), storage.VideoFor(id));
        Assert.Equal(Path.Combine(storage.Root, $"{id}.jpg"), storage.PosterFor(id));
        Assert.Equal(storage.Root, Path.GetDirectoryName(storage.VideoFor(id)));
    }

    [Fact]
    public void Removing_a_clip_that_is_already_gone_is_not_an_error()
    {
        // Deleting a clip whose files somebody removed by hand must still remove the row.
        Storage(root: Path.Combine(Path.GetTempPath(), "serval-clip-tests-absent")).Remove(ObjectId.GenerateNewId());
    }

    [Fact]
    public void Progress_is_zero_before_ffmpeg_has_written_anything()
    {
        Assert.Equal(
            0,
            Storage(root: Path.Combine(Path.GetTempPath(), "serval-clip-tests-absent"))
                .BytesWritten(ObjectId.GenerateNewId()));
    }

    [Fact]
    public void The_state_of_a_clip_is_stored_by_name_not_by_ordinal()
    {
        // Same trap as StreamRole: an enum stored by ordinal makes the declaration order of
        // ClipState a storage format, so inserting a member would silently re-label every stored
        // clip with nothing to compile against.
        var clip = new SavedClip
        {
            CameraId = "front-door",
            CameraName = "Front door",
            Name = "Parcel",
            SavedBy = "jeremiah",
            From = DateTimeOffset.UtcNow,
            To = DateTimeOffset.UtcNow.AddSeconds(55),
            SavedAt = DateTimeOffset.UtcNow,
            State = ClipState.Ready,
        };

        BsonDocument stored = clip.ToBsonDocument();

        Assert.Equal(BsonType.String, stored["State"].BsonType);
        Assert.Equal("Ready", stored["State"].AsString);
    }

    [Fact]
    public void A_clip_round_trips_with_its_frozen_documents()
    {
        // The embedded telemetry is shared contract types mapped by class map rather than by
        // attribute, and nothing else in the server embeds them inside another document — so this
        // is the only place that would catch a map that works at the top level and not nested.
        var clip = new SavedClip
        {
            Id = ObjectId.GenerateNewId(),
            CameraId = "front-door",
            CameraName = "Front door",
            Name = "Parcel behind the planter",
            SavedBy = "jeremiah",
            From = new DateTimeOffset(2026, 8, 9, 16, 3, 12, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 8, 9, 16, 4, 7, TimeSpan.Zero),
            SavedAt = DateTimeOffset.UtcNow,
            DurationSeconds = 55,
            SizeBytes = 88_080_384,
            State = ClipState.Ready,
            Summary = "A courier sets a parcel down behind the planter.",
            Documents = new ClipDocuments
            {
                Utterances =
                [
                    new UtteranceDocument
                    {
                        Id = "utt-1",
                        CameraId = "front-door",
                        Timestamp = new DateTimeOffset(2026, 8, 9, 16, 3, 18, TimeSpan.Zero),
                        Transcript = "Delivery for number twelve.",
                    },
                ],
                Detections =
                [
                    new DetectionDocument
                    {
                        Id = "det-1",
                        CameraId = "front-door",
                        Timestamp = new DateTimeOffset(2026, 8, 9, 16, 3, 14, TimeSpan.Zero),
                        Label = "person",
                        IsAlert = true,
                        BestBox = new DetectionBox { X = 0.36, Y = 0.26, Width = 0.19, Height = 0.47 },
                    },
                ],
            },
        };

        SavedClip read = BsonSerializer.Deserialize<SavedClip>(clip.ToBsonDocument());

        Assert.Equal(clip.Name, read.Name);
        Assert.Equal(clip.From, read.From);
        Assert.Equal(ClipState.Ready, read.State);
        Assert.Equal("Delivery for number twelve.", Assert.Single(read.Documents.Utterances).Transcript);

        DetectionDocument detection = Assert.Single(read.Documents.Detections);
        Assert.True(detection.IsAlert);
        Assert.Equal(0.36, detection.BestBox!.X, precision: 6);
    }

    [Fact]
    public void Search_text_is_the_name_and_everything_said_lowercased()
    {
        var documents = new ClipDocuments
        {
            Utterances =
            [
                new UtteranceDocument
                {
                    Id = "utt-1",
                    Timestamp = DateTimeOffset.UtcNow,
                    Transcript = "Could you leave it behind the PLANTER?",
                },
            ],
        };

        string text = ClipRepository.BuildSearchText("Parcel Behind The Planter", documents);

        Assert.Equal(text, text.ToLowerInvariant());
        Assert.Contains("parcel behind the planter", text);
        Assert.Contains("could you leave it", text);
    }
}
