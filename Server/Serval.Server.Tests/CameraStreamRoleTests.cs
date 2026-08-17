using Serval.Server.Cameras;

namespace Serval.Server.Tests;

/// <summary>
/// How a camera's roles resolve to streams: simply "the stream that declared it", with no fallback
/// anywhere.
///
/// The anti-fallback assertions are the point of the file. Resolved streams are
/// <c>[JsonIgnore]</c>, so a role that quietly resolved to the recorded stream would have a camera
/// decoding its 4K main stream once a second for thumbnails, or serving it over WebRTC, with
/// nothing anywhere saying so.
/// </summary>
public class CameraStreamRoleTests
{
    private static CameraStream Stream(string name, params StreamRole[] roles) =>
        new() { Name = name, Url = $"rtsp://cam/{name}", Roles = [.. roles] };

    private static Camera With(params CameraStream[] streams) =>
        new() { Id = "cam", Name = "Cam", Streams = [.. streams] };

    [Fact]
    public void A_single_stream_camera_declares_every_role_on_it()
    {
        Camera camera = With(Stream("main", StreamRole.Record, StreamRole.Detect, StreamRole.Live));

        Assert.Equal("main", camera.RecordStream!.Name);
        Assert.Equal("main", camera.DetectStream!.Name);
        Assert.Equal("main", camera.LiveStream!.Name);
    }

    [Fact]
    public void Each_role_resolves_to_the_stream_that_declares_it()
    {
        Camera camera = With(
            Stream("main", StreamRole.Record, StreamRole.Live),
            Stream("sub", StreamRole.Detect));

        Assert.Equal("main", camera.RecordStream!.Name);
        Assert.Equal("sub", camera.DetectStream!.Name);
        Assert.Equal("main", camera.LiveStream!.Name);
    }

    [Fact]
    public void The_live_view_can_be_moved_to_the_cheap_stream()
    {
        Camera camera = With(
            Stream("main", StreamRole.Record),
            Stream("sub", StreamRole.Detect, StreamRole.Live));

        Assert.Equal("main", camera.RecordStream!.Name);
        Assert.Equal("sub", camera.LiveStream!.Name);
    }

    [Fact]
    public void A_stream_without_the_detect_role_never_becomes_the_detect_stream()
    {
        // A fallback would answer "main" here, silently pointing snapshots, motion, the dashboard
        // wall and the vision model at the recorded stream.
        Camera camera = With(Stream("main", StreamRole.Record, StreamRole.Live));

        Assert.Null(camera.DetectStream);
    }

    [Fact]
    public void A_stream_without_the_live_role_never_becomes_the_live_stream()
    {
        // A fallback would answer "main", registering the full-quality recorded stream with go2rtc
        // without anyone asking for it.
        Camera camera = With(Stream("main", StreamRole.Record, StreamRole.Detect));

        Assert.Null(camera.LiveStream);
    }

    /// <summary>
    /// A legal camera, not a broken one: record is the role a camera may go without. Ingest reads
    /// this null and runs the snapshot session alone.
    /// </summary>
    [Fact]
    public void A_camera_that_keeps_nothing_still_resolves_detect_and_live()
    {
        Camera camera = With(Stream("main", StreamRole.Detect, StreamRole.Live));

        Assert.Null(camera.RecordStream);
        Assert.Equal("main", camera.DetectStream!.Name);
        Assert.Equal("main", camera.LiveStream!.Name);
    }

    /// <summary>
    /// Validation rejects a camera with no live role, but the properties are read by ingest, the AI
    /// coordinator and the WebRTC worker on documents that predate a rule — so they answer "nothing
    /// to do" rather than throwing. <see cref="CameraRegistryCheck"/> is what makes such a camera
    /// loud instead of silent.
    /// </summary>
    [Fact]
    public void A_camera_with_only_a_detect_stream_resolves_to_nothing_else()
    {
        Camera camera = With(Stream("sub", StreamRole.Detect));

        Assert.Null(camera.RecordStream);
        Assert.Equal("sub", camera.DetectStream!.Name);
        Assert.Null(camera.LiveStream);
    }

    [Fact]
    public void An_empty_stream_list_resolves_to_nothing()
    {
        Camera camera = With();

        Assert.Null(camera.RecordStream);
        Assert.Null(camera.DetectStream);
        Assert.Null(camera.LiveStream);
    }
}
