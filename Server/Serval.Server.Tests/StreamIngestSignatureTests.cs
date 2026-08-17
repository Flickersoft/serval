using Serval.Server.Cameras;
using Serval.Server.Configuration;
using Serval.Server.Ingest;

namespace Serval.Server.Tests;

/// <summary>
/// What counts as "the ffmpeg commands must be rebuilt".
///
/// Every input to the command line has to be in here. Anything omitted is a setting a user can
/// change through the API with no effect until the process happens to die — which is worse than not
/// offering the setting at all, because it looks like it worked.
/// </summary>
public class StreamIngestSignatureTests
{
    private static Camera With(StreamTranscode? transcode) => new()
    {
        Id = "front-door",
        Name = "Front Door",
        Streams =
        [
            new CameraStream
            {
                Name = "main",
                Url = "rtsp://cam/main",
                Roles = [StreamRole.Record, StreamRole.Detect, StreamRole.Live],
                Transcode = transcode,
            },
        ],
    };

    /// <summary>
    /// The signature against untouched server-wide ingest settings. The manager's own overload takes
    /// them as an argument now that changing one — the segment length, the snapshot cap — must
    /// rebuild the command too, so the per-camera cases below hold the server half still.
    /// </summary>
    private static string Signature(Camera camera) =>
        StreamIngestManager.Signature(camera, new IngestOptions());

    [Fact]
    public void Adding_a_transcode_changes_the_signature() =>
        Assert.NotEqual(
            Signature(With(null)),
            Signature(With(new StreamTranscode { Codec = "h264" })));

    [Fact]
    public void Changing_the_transcode_codec_changes_the_signature() =>
        Assert.NotEqual(
            Signature(With(new StreamTranscode { Codec = "h264" })),
            Signature(With(new StreamTranscode { Codec = "av1" })));

    [Fact]
    public void Changing_only_the_transcode_bitrate_changes_the_signature() =>
        Assert.NotEqual(
            Signature(With(new StreamTranscode { Codec = "h264", Bitrate = "2M" })),
            Signature(With(new StreamTranscode { Codec = "h264", Bitrate = "8M" })));

    [Fact]
    public void An_unchanged_camera_keeps_its_signature() =>
        Assert.Equal(
            Signature(With(new StreamTranscode { Codec = "h264", Bitrate = "2M" })),
            Signature(With(new StreamTranscode { Codec = "h264", Bitrate = "2M" })));

    /// <summary>
    /// The stall timeout reaches no command line, but a session reads it once when it arms its
    /// watchdog, so it is fixed for that session's life exactly like the arguments are. Leaving it
    /// out would let an operator lengthen the timeout for a slow camera and have the old one keep
    /// killing it.
    /// </summary>
    [Fact]
    public void Changing_the_stall_timeout_changes_the_signature() =>
        Assert.NotEqual(
            StreamIngestManager.Signature(With(null), new IngestOptions { StallTimeoutSeconds = 60 }),
            StreamIngestManager.Signature(With(null), new IngestOptions { StallTimeoutSeconds = 90 }));

    /// <summary>
    /// The snapshot budget reaches the filter graph, which is fixed once ffmpeg is running. Without
    /// this an operator raises it, sees the settings page accept the value, and keeps getting the
    /// old frames until something else happens to restart the camera.
    /// </summary>
    [Fact]
    public void Changing_the_snapshot_budget_changes_the_signature() =>
        Assert.NotEqual(
            StreamIngestManager.Signature(
                With(null), new IngestOptions { SnapshotMaxMegapixels = 0.25 }),
            StreamIngestManager.Signature(
                With(null), new IngestOptions { SnapshotMaxMegapixels = 1.0 }));

    /// <summary>
    /// Switching recording off decides whether the recorder runs at all — and with it whether the
    /// snapshot session has to be started separately, since a record stream carrying 'detect' is no
    /// longer there to produce them. Two processes rebuilt, so nothing about this can wait for the
    /// old ones to happen to die.
    /// </summary>
    [Fact]
    public void Switching_recording_off_changes_the_signature()
    {
        Camera off = With(null);
        off.Recording = false;

        Assert.NotEqual(Signature(With(null)), Signature(off));
    }

    /// <summary>
    /// The inverse of every test above, and the reason it is worth writing: the playback audio
    /// settings reach no command line at all. They are applied by the App as it plays, and the
    /// recordings on disk are untouched — so including them here would tear down a camera's ffmpeg,
    /// drop its live view and put a gap in its recording every time someone nudged a volume.
    /// </summary>
    [Fact]
    public void Changing_the_playback_gain_does_not_change_the_signature()
    {
        Camera lifted = With(null);
        lifted.PlaybackGainDb = 12;
        lifted.PlaybackGateRmsThreshold = 0.0006;

        Assert.Equal(Signature(With(null)), Signature(lifted));
    }

    [Fact]
    public void Moving_a_role_between_streams_changes_the_signature()
    {
        // It changes which stream is recorded and which produces snapshots.
        Camera before = With(null);
        Camera after = With(null);
        after.Streams =
        [
            new CameraStream
            {
                Name = "main",
                Url = "rtsp://cam/main",
                Roles = [StreamRole.Record, StreamRole.Live],
            },
            new CameraStream { Name = "sub", Url = "rtsp://cam/sub", Roles = [StreamRole.Detect] },
        ];

        Assert.NotEqual(Signature(before), Signature(after));
    }
}
