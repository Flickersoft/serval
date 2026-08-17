using Serval.Ai;
using Serval.Server.Cameras;

namespace Serval.Server.Tests;

/// <summary>
/// Validation is the gate between untrusted API input and the ingest pipeline, where a bad id
/// would become a bad filesystem path and an unpullable source would become an ffmpeg that fails
/// forever with the reason visible only in the logs. These pin the rules the rest of the system
/// assumes.
/// </summary>
public class CameraValidationTests
{
    private static CameraStream Stream(string name, string url, params StreamRole[] roles) =>
        new() { Name = name, Url = url, Roles = [.. roles] };

    /// <summary>All three roles, which is what a one-stream camera that records declares.</summary>
    private static CameraStream Solo(string name, string url) =>
        Stream(name, url, StreamRole.Record, StreamRole.Detect, StreamRole.Live);

    private static Camera Valid() => new()
    {
        Id = "front-door",
        Name = "Front Door",
        Streams = [Solo("main", "rtsp://cam.local/stream")],
    };

    [Fact]
    public void A_single_stream_camera_passes()
    {
        CameraRepository.Validate(Valid()); // does not throw
    }

    [Fact]
    public void A_main_plus_sub_camera_passes()
    {
        Camera camera = Valid();
        camera.Streams =
        [
            Stream("main", "rtsp://cam.local/main", StreamRole.Record, StreamRole.Live),
            Stream("sub", "rtsp://cam.local/sub", StreamRole.Detect),
        ];
        CameraRepository.Validate(camera);
    }

    [Fact]
    public void A_file_source_passes()
    {
        Camera camera = Valid();
        camera.Streams = [Solo("main", "/videos/sample.mp4")];
        CameraRepository.Validate(camera);
    }

    /// <summary>
    /// Deliberately accepted even though go2rtc cannot serve a file, so there is no WebRTC view.
    /// Rejecting it would interlock with "every camera needs a live role" into "you cannot create
    /// the documented hardware-free test camera at all". It is reported as an advisory by
    /// <see cref="CameraRegistryCheck"/> instead.
    /// </summary>
    [Fact]
    public void A_file_url_on_the_live_stream_is_accepted()
    {
        Camera camera = Valid();
        camera.Streams = [Solo("main", "/videos/sample.mp4")];
        CameraRepository.Validate(camera);
    }

    /// <summary>
    /// A stream nothing points at is stored and never pulled, which is how a source is held out of
    /// service without losing its address. The cost is that a mistyped role list validates cleanly
    /// too, so <see cref="CameraRegistryCheck"/> names it in the log — see
    /// <see cref="CameraRegistryCheckTests"/>.
    /// </summary>
    [Fact]
    public void A_stream_with_no_roles_is_accepted()
    {
        Camera camera = Valid();
        camera.Streams =
        [
            Solo("main", "rtsp://cam.local/main"),
            Stream("sub", "rtsp://cam.local/sub"),
        ];
        CameraRepository.Validate(camera);
    }

    [Theory]
    [InlineData(StreamRole.Detect)]
    [InlineData(StreamRole.Live)]
    public void A_missing_role_is_rejected(StreamRole missing)
    {
        Camera camera = Valid();
        camera.Streams =
        [
            Stream("main", "rtsp://cam.local/main",
                [.. Enum.GetValues<StreamRole>().Where(r => r != missing)]),
        ];

        var ex = Assert.Throws<CameraValidationException>(() => CameraRepository.Validate(camera));
        Assert.Contains(missing.ToString().ToLowerInvariant(), ex.Message);
    }

    /// <summary>
    /// Record is the one role a camera may leave unassigned, because it is the only one that costs
    /// disk. "Watch this and tell me, but keep nothing" is a real thing to want: a doorbell you only
    /// want notified about, or a camera pointed somewhere that must not be archived.
    /// </summary>
    [Fact]
    public void A_camera_with_no_record_stream_passes()
    {
        Camera camera = Valid();
        camera.Recording = false;
        camera.Streams =
        [
            Stream("main", "rtsp://cam.local/main", StreamRole.Detect, StreamRole.Live),
        ];
        CameraRepository.Validate(camera);
    }

    [Fact]
    public void A_main_plus_sub_camera_with_no_record_stream_passes()
    {
        Camera camera = Valid();
        camera.Recording = false;
        camera.Streams =
        [
            Stream("main", "rtsp://cam.local/main", StreamRole.Live),
            Stream("sub", "rtsp://cam.local/sub", StreamRole.Detect),
        ];
        CameraRepository.Validate(camera);
    }

    /// <summary>
    /// The one rule tying <see cref="Camera.Recording"/> to the streams. On with nothing to write
    /// is a camera that reports it records and keeps nothing, which is the state the flag exists to
    /// make unreachable — and it defaults to true, so a hand-written watch-only camera has to say
    /// so rather than arriving at it by omission.
    /// </summary>
    [Fact]
    public void Recording_on_with_no_record_stream_is_rejected()
    {
        Camera camera = Valid();
        camera.Recording = true;
        camera.Streams =
        [
            Stream("main", "rtsp://cam.local/main", StreamRole.Detect, StreamRole.Live),
        ];

        var ex = Assert.Throws<CameraValidationException>(() => CameraRepository.Validate(camera));
        Assert.Contains("Recording is on", ex.Message);
    }

    /// <summary>
    /// The switch this whole field exists for: recording off, the record role left exactly where it
    /// was. Rejecting this would mean turning recording off was a reconfiguration rather than a
    /// switch, and the way back on would have to guess which stream to hand the role to.
    /// </summary>
    [Fact]
    public void Recording_off_over_a_record_stream_passes()
    {
        Camera camera = Valid();
        camera.Recording = false;
        CameraRepository.Validate(camera);

        Assert.Equal("main", camera.RecordStream?.Name);
    }

    /// <summary>
    /// One or none, never two. Zero is a choice; two is a mistake that list order would resolve
    /// silently — see <see cref="Two_streams_with_the_same_role_are_rejected"/>.
    /// </summary>
    [Fact]
    public void Two_record_streams_are_still_rejected()
    {
        Camera camera = Valid();
        camera.Streams =
        [
            Solo("main", "rtsp://cam.local/main"),
            Stream("sub", "rtsp://cam.local/sub", StreamRole.Record),
        ];
        Assert.Throws<CameraValidationException>(() => CameraRepository.Validate(camera));
    }

    /// <summary>
    /// Nothing is written to disk, so there is nothing for the transcode to apply to — caught by
    /// the same per-stream rule that rejects a transcode on a sub stream. The message is asserted
    /// because this camera also has Recording on with nothing to record, and a bare Throws would
    /// pass on that instead without ever exercising the rule under test.
    /// </summary>
    [Fact]
    public void A_transcode_on_a_camera_that_records_nothing_is_rejected()
    {
        Camera camera = Valid();
        camera.Recording = false;
        camera.Streams =
        [
            new CameraStream
            {
                Name = "main",
                Url = "rtsp://cam.local/main",
                Roles = [StreamRole.Detect, StreamRole.Live],
                Transcode = new StreamTranscode { Codec = "h264" },
            },
        ];

        var ex = Assert.Throws<CameraValidationException>(() => CameraRepository.Validate(camera));
        Assert.Contains("does not carry the 'record' role", ex.Message);
    }

    /// <summary>
    /// A stream taken out of service keeps its re-encode setting, inert, so putting it back does
    /// not mean setting it again — the same treatment audio thresholds get while AiAudio is off.
    /// The rejection above is about a stream doing some *other* job, where the setting would be a
    /// core of CPU nobody asked for.
    /// </summary>
    [Fact]
    public void A_transcode_on_a_stream_with_no_roles_is_accepted()
    {
        Camera camera = Valid();
        camera.Streams =
        [
            Solo("main", "rtsp://cam.local/main"),
            new CameraStream
            {
                Name = "spare",
                Url = "rtsp://cam.local/spare",
                Roles = [],
                Transcode = new StreamTranscode { Codec = "h264" },
            },
        ];
        CameraRepository.Validate(camera);
    }

    [Fact]
    public void A_transcode_on_a_non_record_stream_is_rejected()
    {
        // Nothing but the recorded stream is written to disk, so a transcode anywhere else would
        // be silently ignored — which is exactly the kind of setting-with-no-effect this pass
        // exists to remove.
        Camera camera = Valid();
        camera.Streams =
        [
            Stream("main", "rtsp://cam.local/main", StreamRole.Record, StreamRole.Live),
            new CameraStream
            {
                Name = "sub",
                Url = "rtsp://cam.local/sub",
                Roles = [StreamRole.Detect],
                Transcode = new StreamTranscode { Codec = "h264" },
            },
        ];
        Assert.Throws<CameraValidationException>(() => CameraRepository.Validate(camera));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("../escape")]
    [InlineData("dot.dot")]
    [InlineData("")]
    public void Unsafe_ids_are_rejected(string id)
    {
        Camera camera = Valid();
        camera.Id = id;
        Assert.Throws<CameraValidationException>(() => CameraRepository.Validate(camera));
    }

    [Theory]
    [InlineData("front-door")]
    [InlineData("Cam_2")]
    [InlineData("garage3")]
    public void Safe_ids_are_accepted(string id) => Assert.True(CameraRepository.IsSafeId(id));

    [Fact]
    public void No_streams_at_all_is_rejected()
    {
        Camera camera = Valid();
        camera.Streams = [];
        Assert.Throws<CameraValidationException>(() => CameraRepository.Validate(camera));
    }

    /// <summary>
    /// Two streams claiming the same role is a mistake, not a preference — resolving it by list
    /// order would silently pick one and look like it worked.
    /// </summary>
    [Theory]
    [InlineData(StreamRole.Record)]
    [InlineData(StreamRole.Detect)]
    [InlineData(StreamRole.Live)]
    public void Two_streams_with_the_same_role_are_rejected(StreamRole role)
    {
        Camera camera = Valid();
        camera.Streams =
        [
            Solo("main", "rtsp://cam.local/main"),
            Stream("sub", "rtsp://cam.local/sub", role),
        ];
        Assert.Throws<CameraValidationException>(() => CameraRepository.Validate(camera));
    }

    [Fact]
    public void Duplicate_stream_names_are_rejected()
    {
        Camera camera = Valid();
        camera.Streams =
        [
            Stream("main", "rtsp://cam.local/main", StreamRole.Record, StreamRole.Live),
            Stream("main", "rtsp://cam.local/sub", StreamRole.Detect),
        ];
        Assert.Throws<CameraValidationException>(() => CameraRepository.Validate(camera));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("")]
    public void Unsafe_stream_names_are_rejected(string name)
    {
        Camera camera = Valid();
        camera.Streams = [Solo(name, "rtsp://cam.local/main")];
        Assert.Throws<CameraValidationException>(() => CameraRepository.Validate(camera));
    }

    [Fact]
    public void A_stream_with_no_url_is_rejected()
    {
        Camera camera = Valid();
        camera.Streams = [Solo("main", "  ")];
        Assert.Throws<CameraValidationException>(() => CameraRepository.Validate(camera));
    }

    /// <summary>
    /// The schemes ffmpeg is actually driven with. This is the check that would have caught the
    /// afternoon lost to an HTTP-FLV URL sitting in a field the pipeline assumed was RTSP.
    /// </summary>
    [Theory]
    [InlineData("rtsp://cam.local/stream")]
    [InlineData("rtsps://cam.local/stream")]
    [InlineData("http://cam.local/flv?port=1935&app=bcs&stream=channel0_ext.bcs")]
    [InlineData("https://cam.local/stream.flv")]
    [InlineData("rtmp://cam.local/live/stream")]
    [InlineData("srt://cam.local:9000")]
    [InlineData("/videos/sample.mp4")]
    [InlineData("file:///videos/sample.mp4")]
    public void Supported_source_schemes_are_accepted(string url)
    {
        Camera camera = Valid();
        camera.Streams = [Solo("main", url)];
        CameraRepository.Validate(camera);
    }

    [Theory]
    [InlineData("ftp://cam.local/stream")]
    [InlineData("smb://nas/share/clip.mp4")]
    [InlineData("gopher://cam.local/stream")]
    public void Unsupported_source_schemes_are_rejected(string url)
    {
        Camera camera = Valid();
        camera.Streams = [Solo("main", url)];
        Assert.Throws<CameraValidationException>(() => CameraRepository.Validate(camera));
    }

    [Fact]
    public void A_missing_name_is_rejected()
    {
        Camera camera = Valid();
        camera.Name = "  ";
        Assert.Throws<CameraValidationException>(() => CameraRepository.Validate(camera));
    }

    [Fact]
    public void Non_positive_retention_is_rejected()
    {
        Camera camera = Valid();
        camera.RetentionDays = 0;
        Assert.Throws<CameraValidationException>(() => CameraRepository.Validate(camera));
    }

    private static Camera Tuned(Action<CameraAudioTuning> configure)
    {
        Camera camera = Valid();
        camera.AudioTuning = new CameraAudioTuning();
        configure(camera.AudioTuning);
        return camera;
    }

    [Fact]
    public void Audio_thresholds_left_null_are_accepted()
    {
        Camera camera = Valid();
        camera.AudioTuning = new CameraAudioTuning { SpeechGateRmsThreshold = 0.0015f };

        CameraRepository.Validate(camera); // does not throw
    }

    /// <summary>
    /// Zero is the interesting one. RMS is never negative, so <c>rms >= 0</c> always holds and a
    /// zero threshold wedges the gate permanently open — the operator asks for "no threshold" and
    /// gets "no gate", which is exactly the cost the gate exists to avoid.
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-0.001f)]
    [InlineData(1.5f)]
    public void An_out_of_range_speech_gate_threshold_is_rejected(float value) =>
        Assert.Throws<CameraValidationException>(() =>
            CameraRepository.Validate(Tuned(t => t.SpeechGateRmsThreshold = value)));

    [Theory]
    [InlineData(0f)]
    [InlineData(-0.001f)]
    [InlineData(1.5f)]
    public void An_out_of_range_sound_gate_threshold_is_rejected(float value) =>
        Assert.Throws<CameraValidationException>(() =>
            CameraRepository.Validate(Tuned(t => t.SoundGateRmsThreshold = value)));

    /// <summary>
    /// Both ends are exclusive: 0 makes every window speech and 1 makes none, so each is a way of
    /// switching the detector off that reads like turning a dial to its limit.
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(-0.1f)]
    [InlineData(1.5f)]
    public void A_vad_threshold_outside_zero_to_one_is_rejected(float value) =>
        Assert.Throws<CameraValidationException>(() =>
            CameraRepository.Validate(Tuned(t => t.VadThreshold = value)));

    [Fact]
    public void A_full_scale_rms_threshold_is_accepted() =>
        CameraRepository.Validate(Tuned(t => t.SpeechGateRmsThreshold = 1f)); // does not throw

    /// <summary>
    /// Three nulls behave identically to no overrides, but stored they would defeat
    /// <c>[BsonIgnoreIfNull]</c> and make "this camera is tuned" true for one that is not.
    /// </summary>
    [Fact]
    public void An_all_null_tuning_is_stored_as_no_tuning()
    {
        Camera camera = Valid();
        camera.AudioTuning = new CameraAudioTuning();

        CameraRepository.Validate(camera);

        Assert.Null(camera.AudioTuning);
    }

    [Fact]
    public void A_tuning_with_one_override_survives_validation()
    {
        Camera camera = Valid();
        camera.AudioTuning = new CameraAudioTuning { VadThreshold = 0.7f };

        CameraRepository.Validate(camera);

        Assert.NotNull(camera.AudioTuning);
        Assert.Equal(0.7f, camera.AudioTuning.VadThreshold);
    }

    // --- Detection tuning ----------------------------------------------------------------------

    private static Camera WithDetection(CameraDetectionTuning tuning)
    {
        Camera camera = Valid();
        camera.DetectionTuning = tuning;
        return camera;
    }

    [Fact]
    public void An_empty_detection_override_is_collapsed_to_none()
    {
        // All-nulls is not an override. Storing it would make "this camera has custom detection"
        // true for a camera that has none.
        Camera camera = WithDetection(new CameraDetectionTuning());

        CameraRepository.Validate(camera);

        Assert.Null(camera.DetectionTuning);
    }

    [Fact]
    public void An_empty_class_list_is_rejected_rather_than_interpreted()
    {
        // It could defensibly mean "everything" or "nothing", and a camera silently detecting
        // nothing while looking deliberately configured is the worse of the two readings.
        Assert.Throws<CameraValidationException>(() =>
            CameraRepository.Validate(WithDetection(new CameraDetectionTuning { Classes = [] })));
    }

    [Fact]
    public void An_empty_describe_list_is_rejected_too() =>
        Assert.Throws<CameraValidationException>(() =>
            CameraRepository.Validate(WithDetection(new CameraDetectionTuning { DescribeClasses = [] })));

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.5)]
    public void A_score_threshold_at_either_extreme_is_rejected(double score) =>
        Assert.Throws<CameraValidationException>(() =>
            CameraRepository.Validate(WithDetection(new CameraDetectionTuning { ScoreThreshold = score })));

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void A_size_floor_outside_the_frame_is_rejected(double fraction) =>
        Assert.Throws<CameraValidationException>(() =>
            CameraRepository.Validate(
                WithDetection(new CameraDetectionTuning { MinObjectFraction = fraction })));

    [Fact]
    public void A_negative_confirmation_window_is_rejected() =>
        Assert.Throws<CameraValidationException>(() =>
            CameraRepository.Validate(WithDetection(new CameraDetectionTuning { TrackConfirmSeconds = -1 })));

    [Fact]
    public void A_negative_coast_window_is_rejected() =>
        Assert.Throws<CameraValidationException>(() =>
            CameraRepository.Validate(WithDetection(new CameraDetectionTuning { TrackCoastSeconds = -1 })));

    [Fact]
    public void A_mask_with_too_few_points_is_rejected()
    {
        // Two points cannot enclose a region, and a mask that silently matches nothing is
        // indistinguishable from one that works.
        Assert.Throws<CameraValidationException>(() =>
            CameraRepository.Validate(WithDetection(new CameraDetectionTuning
            {
                Masks = [new DetectionMask { Points = [0.1, 0.1, 0.9, 0.9] }],
            })));
    }

    [Fact]
    public void A_mask_point_outside_the_frame_is_rejected()
    {
        // Points are fractions of the frame, so a coordinate above 1 describes a region no
        // detection could ever fall inside.
        Assert.Throws<CameraValidationException>(() =>
            CameraRepository.Validate(WithDetection(new CameraDetectionTuning
            {
                Masks = [new DetectionMask { Points = [0, 0, 640, 0, 640, 360] }],
            })));
    }

    [Fact]
    public void A_valid_detection_override_survives_normalisation()
    {
        Camera camera = WithDetection(new CameraDetectionTuning
        {
            Classes = ["person"],
            ScoreThreshold = 0.4,
            TrackConfirmSeconds = 2,
            Masks = [new DetectionMask { Name = "road", Points = [0, 0, 1, 0, 1, 0.3] }],
        });

        CameraRepository.Validate(camera);

        Assert.NotNull(camera.DetectionTuning);
        Assert.Equal(["person"], camera.DetectionTuning.Classes!);
    }

    // ------------------------------------------------------- sound and movement overrides

    private static Camera WithSound(CameraSoundTuning? tuning)
    {
        Camera camera = Valid();
        camera.SoundTuning = tuning;
        return camera;
    }

    private static Camera WithMotion(CameraMotionTuning? tuning)
    {
        Camera camera = Valid();
        camera.MotionTuning = tuning;
        return camera;
    }

    /// <summary>
    /// An all-null object behaves the same as no object but is not the same thing stored: it
    /// defeats <c>[BsonIgnoreIfNull]</c>, and it makes "this camera has custom sounds" true for one
    /// that has none. Collapsed on the way in, so the document, the API and the App agree.
    /// </summary>
    [Fact]
    public void An_empty_sound_override_is_collapsed_away()
    {
        Camera camera = WithSound(new CameraSoundTuning());

        CameraRepository.Validate(camera);

        Assert.Null(camera.SoundTuning);
    }

    [Fact]
    public void An_empty_movement_override_is_collapsed_away()
    {
        Camera camera = WithMotion(new CameraMotionTuning());

        CameraRepository.Validate(camera);

        Assert.Null(camera.MotionTuning);
    }

    [Fact]
    public void An_empty_alert_sound_list_is_refused_rather_than_guessed_at()
    {
        // "None" and "all" are both defensible readings, and a camera silently alerting on nothing
        // while looking configured is the worse one.
        Assert.Throws<CameraValidationException>(() =>
            CameraRepository.Validate(WithSound(new CameraSoundTuning { AlertLabels = [] })));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1.4)]
    public void A_sound_confidence_outside_zero_to_one_is_refused(double value)
    {
        Assert.Throws<CameraValidationException>(() =>
            CameraRepository.Validate(
                WithSound(new CameraSoundTuning { MinConfidence = value })));
    }

    [Fact]
    public void A_negative_sound_cooldown_is_refused()
    {
        Assert.Throws<CameraValidationException>(() =>
            CameraRepository.Validate(
                WithSound(new CameraSoundTuning { CooldownSeconds = -1 })));
    }

    [Fact]
    public void A_valid_sound_override_survives_normalisation()
    {
        Camera camera = WithSound(new CameraSoundTuning
        {
            AlertLabels = ["Glass", "Crying, sobbing"],
            MinConfidence = 0.4,
            CooldownSeconds = 90,
        });

        CameraRepository.Validate(camera);

        Assert.NotNull(camera.SoundTuning);
        Assert.Equal(["Glass", "Crying, sobbing"], camera.SoundTuning.AlertLabels!);
    }

    /// <summary>
    /// The cross-field rule, and the reason it exists: movement is declared <em>between</em> the
    /// two fractions, so a minimum at or above the maximum is a gate that can never open. No
    /// single-field range check catches it, and from outside it looks exactly like a camera
    /// watching a room where nothing happens.
    /// </summary>
    [Fact]
    public void A_movement_gate_that_can_never_open_is_refused()
    {
        CameraValidationException error = Assert.Throws<CameraValidationException>(() =>
            CameraRepository.Validate(WithMotion(new CameraMotionTuning
            {
                MinChangedFraction = 0.6,
                MaxChangedFraction = 0.5,
            })));

        Assert.Contains("never report", error.Message);
    }

    [Fact]
    public void A_brightness_step_outside_a_byte_is_refused()
    {
        Assert.Throws<CameraValidationException>(() =>
            CameraRepository.Validate(WithMotion(new CameraMotionTuning { PixelDelta = 300 })));
    }

    [Fact]
    public void A_valid_movement_override_survives_normalisation()
    {
        Camera camera = WithMotion(new CameraMotionTuning
        {
            MinChangedFraction = 0.05,
            MaxChangedFraction = 0.6,
            PixelDelta = 30,
        });

        CameraRepository.Validate(camera);

        Assert.NotNull(camera.MotionTuning);
        Assert.Equal(30, camera.MotionTuning.PixelDelta);
    }

    [Fact]
    public void A_negative_examination_rate_is_refused()
    {
        Camera camera = Valid();
        camera.DetectionTuning = new CameraDetectionTuning { MaxFps = -1 };

        Assert.Throws<CameraValidationException>(() => CameraRepository.Validate(camera));
    }

    [Fact]
    public void An_empty_alert_class_list_is_refused()
    {
        Camera camera = Valid();
        camera.DetectionTuning = new CameraDetectionTuning { AlertClasses = [] };

        Assert.Throws<CameraValidationException>(() => CameraRepository.Validate(camera));
    }

    [Fact]
    public void No_playback_gain_is_the_default_and_passes()
    {
        Camera camera = Valid();

        Assert.Equal(0, camera.PlaybackGainDb);
        Assert.Null(camera.PlaybackGateRmsThreshold);
        CameraRepository.Validate(camera); // does not throw
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(20)]
    public void A_playback_gain_inside_the_range_passes(double db)
    {
        Camera camera = Valid();
        camera.PlaybackGainDb = db;

        CameraRepository.Validate(camera); // does not throw
    }

    /// <summary>
    /// 20 dB is where the App's volume control stops, and also the ceiling of libwebrtc's own track
    /// volume — which the desktop live view goes through with nothing behind it. A larger value would
    /// be a starting position the slider cannot represent and no path can deliver.
    /// </summary>
    [Theory]
    [InlineData(21)]
    [InlineData(40)]
    public void A_playback_gain_past_the_ceiling_is_refused(double db)
    {
        Camera camera = Valid();
        camera.PlaybackGainDb = db;

        Assert.Throws<CameraValidationException>(() => CameraRepository.Validate(camera));
    }

    /// <summary>
    /// Refused rather than read as attenuation. Listening more quietly is what the slider's own range
    /// below unity is for, and allowing it here would give two controls the same effect with no way to
    /// tell which one silenced a camera.
    /// </summary>
    [Fact]
    public void A_negative_playback_gain_is_refused()
    {
        Camera camera = Valid();
        camera.PlaybackGainDb = -6;

        Assert.Throws<CameraValidationException>(() => CameraRepository.Validate(camera));
    }

    [Fact]
    public void A_playback_gate_threshold_inside_the_range_passes()
    {
        Camera camera = Valid();
        camera.PlaybackGateRmsThreshold = 0.0006;

        CameraRepository.Validate(camera); // does not throw
    }

    /// <summary>
    /// Zero would hold the gate permanently open — RMS is never negative, so <c>rms >= 0</c> always
    /// holds — which is the same outcome as no gate at all, reached by what looks like turning a dial
    /// down. Unset is how you say "no gate". Same reasoning as the three detector thresholds.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void An_out_of_range_playback_gate_threshold_is_refused(double rms)
    {
        Camera camera = Valid();
        camera.PlaybackGateRmsThreshold = rms;

        Assert.Throws<CameraValidationException>(() => CameraRepository.Validate(camera));
    }
}
