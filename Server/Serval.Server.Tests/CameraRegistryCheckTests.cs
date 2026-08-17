using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Serval.Server.Cameras;
using Serval.Server.Configuration;
using Serval.Server.Ingest;
using Serval.Server.Storage;

namespace Serval.Server.Tests;

/// <summary>
/// The read-side check, which exists because validation runs on write and nowhere else.
///
/// A document written straight into Mongo can therefore sit in the registry doing nothing.
/// Dropping it with no log line would stop that camera recording with nothing saying why.
/// </summary>
public class CameraRegistryCheckTests
{
    public CameraRegistryCheckTests() => BsonRegistration.Register();

    private static IngestOptions Ingest() => new();

    private static FfmpegCapabilities Capabilities() => new(["libx264"]);

    private static Camera Valid() => new()
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
            },
        ],
    };

    /// <summary>The same camera with its 'record' role taken off — watched and viewable, kept nowhere.</summary>
    private static Camera NotRecorded()
    {
        Camera camera = Valid();
        camera.Recording = false;
        camera.Streams[0].Roles = [StreamRole.Detect, StreamRole.Live];
        return camera;
    }

    /// <summary>
    /// The other route to keeping nothing: the record role still assigned, the switch off. Read the
    /// same way as <see cref="NotRecorded"/> by everything downstream, and deliberately not the
    /// same thing to fix.
    /// </summary>
    private static Camera RecordingOff()
    {
        Camera camera = Valid();
        camera.Recording = false;
        return camera;
    }

    [Fact]
    public void A_well_formed_camera_has_no_fault() =>
        Assert.Null(CameraRegistryCheck.Fault(Valid(), Ingest(), Capabilities()));

    [Fact]
    public void A_camera_missing_a_role_is_faulted_with_the_role_named()
    {
        Camera camera = Valid();
        camera.Streams[0].Roles = [StreamRole.Record, StreamRole.Detect];

        string? fault = CameraRegistryCheck.Fault(camera, Ingest(), Capabilities());

        Assert.NotNull(fault);
        Assert.Contains("live", fault);
    }

    [Fact]
    public void A_document_with_no_streams_field_is_faulted_with_a_specific_message()
    {
        // Streams is `required` in C#, but the Mongo driver does not enforce that, so a document
        // without the field deserializes with it null. "Needs at least one stream" would be true
        // and useless; the operator needs to know the field is missing from the document.
        var camera = BsonSerializer.Deserialize<Camera>(
            new BsonDocument { { "_id", "front-door" }, { "Name", "Front Door" } });

        string? fault = CameraRegistryCheck.Fault(camera, Ingest(), Capabilities());

        Assert.NotNull(fault);
        Assert.Contains("streams", fault);
        Assert.Contains("PUT /api/cameras", fault);
    }

    [Fact]
    public void A_transcode_the_host_cannot_run_is_a_fault_not_just_an_api_error()
    {
        // The API rejects this on write, but a document written straight into Mongo bypasses that
        // — and the alternative to catching it here is an ffmpeg that fails forever.
        Camera camera = Valid();
        camera.Streams[0].Transcode = new StreamTranscode { Codec = "av1" };

        Assert.NotNull(CameraRegistryCheck.Fault(camera, Ingest(), Capabilities()));
    }

    [Fact]
    public void Recording_and_detecting_from_one_stream_earns_an_advisory()
    {
        // "Recording untouched is free" is only true when snapshots come from somewhere else;
        // otherwise ffmpeg decodes every frame anyway to produce the JPEG.
        IReadOnlyList<string> advisories =
            CameraRegistryCheck.Advisories(Valid(), new ServerOptions());

        Assert.Contains(advisories, a => a.Contains("zero-decode", StringComparison.Ordinal));
    }

    [Fact]
    public void A_separate_detect_stream_earns_no_decode_advisory()
    {
        Camera camera = Valid();
        camera.Streams =
        [
            new CameraStream
            {
                Name = "main",
                Url = "rtsp://cam/main",
                Roles = [StreamRole.Record, StreamRole.Live],
            },
            new CameraStream { Name = "sub", Url = "rtsp://cam/sub", Roles = [StreamRole.Detect] },
        ];

        IReadOnlyList<string> advisories =
            CameraRegistryCheck.Advisories(camera, new ServerOptions());

        Assert.DoesNotContain(advisories, a => a.Contains("zero-decode", StringComparison.Ordinal));
    }

    /// <summary>
    /// Keeping nothing is a legal configuration, so it is never a fault — but it is the one
    /// configuration where the absence of footage is the configuration working, and a log that
    /// never said so leaves an operator hunting a recorder that was never asked to run.
    /// </summary>
    [Fact]
    public void A_camera_that_records_nothing_earns_an_advisory_but_is_never_a_fault()
    {
        Camera camera = NotRecorded();

        Assert.Null(CameraRegistryCheck.Fault(camera, Ingest(), Capabilities()));
        Assert.Contains(
            CameraRegistryCheck.Advisories(camera, new ServerOptions()),
            a => a.Contains("writes nothing to disk", StringComparison.Ordinal));
    }

    /// <summary>
    /// Both routes to keeping nothing are reported, and the advisory names which one it is: the
    /// fix for a missing role is under Streams and the fix for a flipped switch is not, so an
    /// advisory that said only "nothing is written" would send half its readers to the wrong page.
    /// </summary>
    [Fact]
    public void The_advisory_names_which_of_the_two_reasons_it_is()
    {
        Assert.Contains(
            CameraRegistryCheck.Advisories(NotRecorded(), new ServerOptions()),
            a => a.Contains("no stream is set to 'record'", StringComparison.Ordinal));

        Assert.Contains(
            CameraRegistryCheck.Advisories(RecordingOff(), new ServerOptions()),
            a => a.Contains("Recording is switched off", StringComparison.Ordinal)
                && a.Contains("'main'", StringComparison.Ordinal));
    }

    [Fact]
    public void Recording_settings_on_a_camera_that_records_nothing_are_called_out_as_inert()
    {
        Camera camera = NotRecorded();
        camera.RecordAudio = true;
        camera.RetentionDays = 14;

        IReadOnlyList<string> advisories =
            CameraRegistryCheck.Advisories(camera, new ServerOptions());

        Assert.Contains(advisories, a => a.Contains("RecordAudio", StringComparison.Ordinal));
        Assert.Contains(advisories, a => a.Contains("RetentionDays", StringComparison.Ordinal));
    }

    /// <summary>
    /// RetentionDays is the one of the two that keeps working while the switch is off: the
    /// retention worker expires what is already on disk whether or not anything is being added to
    /// it. Calling it inert here would be advice to change a setting that is doing its job.
    /// </summary>
    [Fact]
    public void Retention_is_not_called_inert_merely_because_recording_is_switched_off()
    {
        Camera camera = RecordingOff();
        camera.RetentionDays = 14;

        Assert.DoesNotContain(
            CameraRegistryCheck.Advisories(camera, new ServerOptions()),
            a => a.Contains("RetentionDays", StringComparison.Ordinal));
    }

    [Fact]
    public void A_recording_camera_earns_no_not_recorded_advisory() =>
        Assert.DoesNotContain(
            CameraRegistryCheck.Advisories(Valid(), new ServerOptions()),
            a => a.Contains("writes nothing to disk", StringComparison.Ordinal));

    /// <summary>
    /// The advisory that pays for dropping the validation rule. Holding a source out of service and
    /// mistyping its role list produce the same document, so the log names the stream rather than
    /// letting the second look like a working configuration.
    /// </summary>
    [Fact]
    public void A_stream_with_no_roles_is_named_in_an_advisory_but_is_never_a_fault()
    {
        Camera camera = Valid();
        camera.Streams.Add(new CameraStream { Name = "spare", Url = "rtsp://cam/spare", Roles = [] });

        Assert.Null(CameraRegistryCheck.Fault(camera, Ingest(), Capabilities()));
        Assert.Contains(
            CameraRegistryCheck.Advisories(camera, new ServerOptions()),
            a => a.Contains("'spare' with no roles", StringComparison.Ordinal));
    }

    [Fact]
    public void A_transcode_on_a_stream_with_no_roles_is_called_out_as_kept_and_inert()
    {
        Camera camera = Valid();
        camera.Streams.Add(new CameraStream
        {
            Name = "spare",
            Url = "rtsp://cam/spare",
            Roles = [],
            Transcode = new StreamTranscode { Codec = "h264" },
        });

        Assert.Contains(
            CameraRegistryCheck.Advisories(camera, new ServerOptions()),
            a => a.Contains("It is kept", StringComparison.Ordinal));
    }

    /// <summary>
    /// The decode advisory describes a cost, and nothing is paying it while the recorder is not
    /// running. Left in, it would be advice to buy a sub stream to speed up a process that is not
    /// started.
    /// </summary>
    [Fact]
    public void The_decode_advisory_is_silent_while_recording_is_switched_off() =>
        Assert.DoesNotContain(
            CameraRegistryCheck.Advisories(RecordingOff(), new ServerOptions()),
            a => a.Contains("zero-decode", StringComparison.Ordinal));

    [Fact]
    public void A_file_live_url_earns_a_webrtc_advisory_but_is_never_a_fault()
    {
        // Deliberately not rejected: requiring a live role *and* rejecting a file live URL would
        // make the documented hardware-free test camera unregisterable.
        Camera camera = Valid();
        camera.Streams[0].Url = "/videos/sample.mp4";

        var options = new ServerOptions();
        options.WebRtc.Enabled = true;

        Assert.Null(CameraRegistryCheck.Fault(camera, Ingest(), Capabilities()));
        Assert.Contains(
            CameraRegistryCheck.Advisories(camera, options),
            a => a.Contains("WebRTC", StringComparison.Ordinal));
    }

    [Fact]
    public void Ai_enabled_on_a_camera_but_not_on_the_server_earns_an_advisory()
    {
        Camera camera = Valid();
        camera.AiVision = true;
        camera.AiAudio = true;

        IReadOnlyList<string> advisories =
            CameraRegistryCheck.Advisories(camera, new ServerOptions());

        Assert.Contains(advisories, a => a.Contains("AiVision", StringComparison.Ordinal));
        Assert.Contains(advisories, a => a.Contains("AiAudio", StringComparison.Ordinal));
    }

    private static ServerOptions ServerAiOn()
    {
        var options = new ServerOptions();
        options.ServerAi.Enabled = true;
        return options;
    }

    /// <summary>
    /// The likeliest way to reach this: tune a camera by ear, then turn its audio off. The
    /// thresholds survive and the next person reads them as being in force.
    /// </summary>
    [Fact]
    public void Audio_thresholds_set_with_AiAudio_off_earn_an_advisory()
    {
        Camera camera = Valid();
        camera.AiAudio = false;
        camera.AudioTuning = new CameraAudioTuning { SpeechGateRmsThreshold = 0.0015f };

        IReadOnlyList<string> advisories = CameraRegistryCheck.Advisories(camera, ServerAiOn());

        Assert.Contains(advisories, a => a.Contains("nothing reads them", StringComparison.Ordinal));
    }

    [Fact]
    public void A_sound_gate_threshold_set_with_sound_tagging_off_earns_an_advisory()
    {
        Camera camera = Valid();
        camera.AiAudio = true;
        camera.AudioTuning = new CameraAudioTuning { SoundGateRmsThreshold = 0.002f };

        IReadOnlyList<string> advisories = CameraRegistryCheck.Advisories(camera, ServerAiOn());

        Assert.Contains(
            advisories, a => a.Contains("Serval:Ai:Sound:Enabled", StringComparison.Ordinal));
    }

    /// <summary>
    /// A warning rather than a rejection: a genuinely quiet microphone is a real reason to want a
    /// number that would be wrong anywhere else, and that judgement is the operator's.
    /// </summary>
    [Fact]
    public void A_threshold_below_the_noise_floor_earns_an_advisory()
    {
        Camera camera = Valid();
        camera.AiAudio = true;
        camera.AudioTuning = new CameraAudioTuning { SpeechGateRmsThreshold = 0.0001f };

        IReadOnlyList<string> advisories = CameraRegistryCheck.Advisories(camera, ServerAiOn());

        Assert.Contains(advisories, a => a.Contains("noise floor", StringComparison.Ordinal));
    }

    [Fact]
    public void A_workable_threshold_earns_no_advisory()
    {
        Camera camera = Valid();
        camera.AiAudio = true;
        camera.AudioTuning = new CameraAudioTuning { SpeechGateRmsThreshold = 0.0015f };

        IReadOnlyList<string> advisories = CameraRegistryCheck.Advisories(camera, ServerAiOn());

        Assert.DoesNotContain(advisories, a => a.Contains("noise floor", StringComparison.Ordinal));
        Assert.DoesNotContain(advisories, a => a.Contains("nothing reads them", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_tuning_object_produces_no_advisory()
    {
        Camera camera = Valid();
        camera.AiAudio = false;
        camera.AudioTuning = new CameraAudioTuning();

        IReadOnlyList<string> advisories = CameraRegistryCheck.Advisories(camera, ServerAiOn());

        Assert.DoesNotContain(advisories, a => a.Contains("nothing reads them", StringComparison.Ordinal));
    }
}
