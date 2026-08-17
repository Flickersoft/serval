using Serval.Ai;
using Serval.Server.Ai;
using Serval.Server.Cameras;

namespace Serval.Server.Tests;

/// <summary>
/// What counts as "this camera's AI session must be restarted".
///
/// Same rule as the ingest signature, and for the same reason: the audio thresholds are read once,
/// when the level gate and the Silero detector are constructed. A threshold missing from here is a
/// setting that can be changed through the API with no effect — the request succeeds, the document
/// is right, and the camera keeps using the old value until its session dies for some unrelated
/// reason. That is worse than not offering the setting, because it looks like it worked.
/// </summary>
public class CameraAiSignatureTests
{
    private static Camera With(CameraAudioTuning? tuning) => new()
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
        AiAudio = true,
        AudioTuning = tuning,
    };

    /// <summary>
    /// The signature against untouched server-wide settings. The coordinator's own overload takes
    /// them as an argument now that a change to <em>them</em> must restart a session too, so the
    /// per-camera cases below pin the override half by holding the server half still.
    /// </summary>
    private static string Signature(Camera camera) =>
        CameraAiCoordinator.Signature(camera, new AiOptions());

    [Fact]
    public void An_unchanged_camera_keeps_its_signature() =>
        Assert.Equal(
            Signature(With(new CameraAudioTuning { SpeechGateRmsThreshold = 0.0015f })),
            Signature(With(new CameraAudioTuning { SpeechGateRmsThreshold = 0.0015f })));

    [Fact]
    public void Adding_a_tuning_object_changes_the_signature() =>
        Assert.NotEqual(
            Signature(With(null)),
            Signature(With(new CameraAudioTuning { SpeechGateRmsThreshold = 0.0015f })));

    [Fact]
    public void Changing_the_speech_gate_threshold_changes_the_signature() =>
        Assert.NotEqual(
            Signature(With(new CameraAudioTuning { SpeechGateRmsThreshold = 0.0015f })),
            Signature(With(new CameraAudioTuning { SpeechGateRmsThreshold = 0.0030f })));

    [Fact]
    public void Changing_the_vad_threshold_changes_the_signature() =>
        Assert.NotEqual(
            Signature(With(new CameraAudioTuning { VadThreshold = 0.5f })),
            Signature(With(new CameraAudioTuning { VadThreshold = 0.7f })));

    [Fact]
    public void Changing_the_sound_gate_threshold_changes_the_signature() =>
        Assert.NotEqual(
            Signature(With(new CameraAudioTuning { SoundGateRmsThreshold = 0.01f })),
            Signature(With(new CameraAudioTuning { SoundGateRmsThreshold = 0.002f })));

    /// <summary>
    /// The two gates are separate settings, so moving one must not read as moving the other —
    /// otherwise a sound-gate edit would restart a session whose speech tuning did not change.
    /// </summary>
    [Fact]
    public void The_two_gates_are_distinguishable()
    {
        string speech = Signature(
            With(new CameraAudioTuning { SpeechGateRmsThreshold = 0.002f }));
        string sound = Signature(
            With(new CameraAudioTuning { SoundGateRmsThreshold = 0.002f }));

        Assert.NotEqual(speech, sound);
    }

    /// <summary>
    /// The reconcile loop compares these strings every few seconds. A format that rendered the same
    /// float differently — a trailing zero, or a comma separator on a non-invariant host — would
    /// restart every tuned camera on every tick, tearing down its ffmpeg and its conversation.
    /// </summary>
    [Fact]
    public void The_same_threshold_written_two_ways_is_not_a_change() =>
        Assert.Equal(
            Signature(With(new CameraAudioTuning { SpeechGateRmsThreshold = 0.001f })),
            Signature(With(new CameraAudioTuning { SpeechGateRmsThreshold = 0.0010f })));

    [Fact]
    public void Clearing_a_threshold_changes_the_signature() =>
        Assert.NotEqual(
            Signature(With(new CameraAudioTuning { SpeechGateRmsThreshold = 0.0015f })),
            Signature(With(new CameraAudioTuning())));

    // --- Detection tuning ----------------------------------------------------------------------
    //
    // Same hazard as the audio thresholds, and the same consequence: a class allowlist or a mask
    // edited through the API that never reaches the running session is a setting that looks like it
    // worked and did nothing.

    private static Camera Detect(CameraDetectionTuning? tuning) => new()
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
        AiVision = true,
        DetectionTuning = tuning,
    };

    [Fact]
    public void A_changed_detection_threshold_changes_the_signature() =>
        Assert.NotEqual(
            Signature(Detect(new CameraDetectionTuning { ScoreThreshold = 0.3 })),
            Signature(Detect(new CameraDetectionTuning { ScoreThreshold = 0.5 })));

    [Fact]
    public void A_changed_size_floor_changes_the_signature() =>
        Assert.NotEqual(
            Signature(Detect(new CameraDetectionTuning { MinObjectFraction = 0 })),
            Signature(Detect(new CameraDetectionTuning { MinObjectFraction = 0.0001 })));

    [Fact]
    public void A_changed_confirmation_requirement_changes_the_signature() =>
        Assert.NotEqual(
            Signature(Detect(new CameraDetectionTuning { TrackConfirmSeconds = 1.0 })),
            Signature(Detect(new CameraDetectionTuning { TrackConfirmSeconds = 2.0 })));

    [Fact]
    public void A_changed_coast_window_changes_the_signature() =>
        Assert.NotEqual(
            Signature(Detect(new CameraDetectionTuning { TrackCoastSeconds = 2.0 })),
            Signature(Detect(new CameraDetectionTuning { TrackCoastSeconds = 4.0 })));

    [Fact]
    public void A_changed_class_list_changes_the_signature() =>
        Assert.NotEqual(
            Signature(Detect(new CameraDetectionTuning { Classes = ["person"] })),
            Signature(Detect(new CameraDetectionTuning { Classes = ["person", "car"] })));

    [Fact]
    public void Reordering_a_class_list_is_not_a_change()
    {
        // The same classes in a different order is the same configuration. Without sorting, saving
        // the list from a UI that happens to reorder it would restart every camera's AI session.
        Assert.Equal(
            Signature(Detect(new CameraDetectionTuning { Classes = ["car", "person"] })),
            Signature(Detect(new CameraDetectionTuning { Classes = ["person", "car"] })));
    }

    [Fact]
    public void A_class_containing_a_separator_cannot_collide_with_two_classes()
    {
        // The reason the field is length-prefixed and joined on a unit separator rather than a
        // comma. Joined naively, ["a,b"] and ["a","b"] produce the same string, and one of the two
        // edits silently does nothing.
        Assert.NotEqual(
            Signature(Detect(new CameraDetectionTuning { Classes = ["a,b"] })),
            Signature(Detect(new CameraDetectionTuning { Classes = ["a", "b"] })));
    }

    [Fact]
    public void Inheriting_the_class_list_is_distinct_from_naming_one()
    {
        // Null means "use the server default" and is not the same session as one pinned to exactly
        // the classes that default happens to contain today.
        Assert.NotEqual(
            Signature(Detect(new CameraDetectionTuning { ScoreThreshold = 0.4 })),
            Signature(Detect(new CameraDetectionTuning
            {
                ScoreThreshold = 0.4,
                Classes = ["person"],
            })));
    }

    [Fact]
    public void Describe_classes_are_tracked_separately_from_the_detection_classes()
    {
        // They are different lists with different jobs. Folding them into one signature field would
        // let a change to either be mistaken for the other.
        Assert.NotEqual(
            Signature(Detect(new CameraDetectionTuning { Classes = ["person"] })),
            Signature(Detect(new CameraDetectionTuning { DescribeClasses = ["person"] })));
    }

    [Fact]
    public void A_changed_mask_changes_the_signature() =>
        Assert.NotEqual(
            Signature(Detect(new CameraDetectionTuning
            {
                Masks = [new DetectionMask { Points = [0, 0, 1, 0, 1, 0.3] }],
            })),
            Signature(Detect(new CameraDetectionTuning
            {
                Masks = [new DetectionMask { Points = [0, 0, 1, 0, 1, 0.5] }],
            })));

    [Fact]
    public void Renaming_a_mask_is_not_a_change()
    {
        // The name is for the UI. Only the geometry affects what the detector does, so a rename
        // must not restart the session.
        Assert.Equal(
            Signature(Detect(new CameraDetectionTuning
            {
                Masks = [new DetectionMask { Name = "road", Points = [0, 0, 1, 0, 1, 0.3] }],
            })),
            Signature(Detect(new CameraDetectionTuning
            {
                Masks = [new DetectionMask { Name = "the street", Points = [0, 0, 1, 0, 1, 0.3] }],
            })));
    }

    [Fact]
    public void Narrowing_a_mask_to_some_classes_is_a_change()
    {
        // It changes what the detector suppresses, so the session has to be rebuilt on it.
        Assert.NotEqual(
            Signature(Detect(new CameraDetectionTuning
            {
                Masks = [new DetectionMask { Points = [0, 0, 1, 0, 1, 0.3] }],
            })),
            Signature(Detect(new CameraDetectionTuning
            {
                Masks = [new DetectionMask { Points = [0, 0, 1, 0, 1, 0.3], Classes = ["car"] }],
            })));
    }

    [Fact]
    public void Reordering_a_masks_classes_is_not_a_change()
    {
        // A filter is a set. Sorted for the same reason every other word list here is sorted:
        // otherwise a re-ordered list restarts a camera's session for nothing.
        Assert.Equal(
            Signature(Detect(new CameraDetectionTuning
            {
                Masks =
                [
                    new DetectionMask { Points = [0, 0, 1, 0, 1, 0.3], Classes = ["car", "truck"] },
                ],
            })),
            Signature(Detect(new CameraDetectionTuning
            {
                Masks =
                [
                    new DetectionMask { Points = [0, 0, 1, 0, 1, 0.3], Classes = ["truck", "car"] },
                ],
            })));
    }

    [Fact]
    public void A_masks_classes_do_not_bleed_into_the_next_masks_points()
    {
        // The two fields are joined into one string per mask, so they need a separator that cannot
        // appear in either — the same collision `Words` guards against.
        Assert.NotEqual(
            Signature(Detect(new CameraDetectionTuning
            {
                Masks =
                [
                    new DetectionMask { Points = [0, 0, 1, 0, 1, 0.3], Classes = ["car"] },
                    new DetectionMask { Points = [0, 0, 1, 0, 1, 0.5] },
                ],
            })),
            Signature(Detect(new CameraDetectionTuning
            {
                Masks =
                [
                    new DetectionMask { Points = [0, 0, 1, 0, 1, 0.3] },
                    new DetectionMask { Points = [0, 0, 1, 0, 1, 0.5], Classes = ["car"] },
                ],
            })));
    }

    [Fact]
    public void Detection_tuning_does_not_collide_with_audio_tuning()
    {
        Assert.NotEqual(
            Signature(With(new CameraAudioTuning { VadThreshold = 0.5 })),
            Signature(Detect(new CameraDetectionTuning { ScoreThreshold = 0.5 })));
    }

    // ------------------------------------------------------- the server-wide half

    /// <summary>
    /// The reason the signature is computed over the <em>effective</em> settings rather than over
    /// the camera's overrides. Every value here has two possible sources, and a session cannot tell
    /// which it got — so a change to the server-wide one has to restart it exactly as a per-camera
    /// change does. Before the settings page existed there was no way to make this change at
    /// runtime, and the signature was written as though there never would be.
    /// </summary>
    [Fact]
    public void Changing_a_server_wide_threshold_changes_the_signature()
    {
        var quiet = new AiOptions();
        quiet.AudioGate.RmsThreshold = 0.002f;

        Assert.NotEqual(
            CameraAiCoordinator.Signature(With(null), new AiOptions()),
            CameraAiCoordinator.Signature(With(null), quiet));
    }

    [Fact]
    public void Changing_a_server_wide_detection_class_list_changes_the_signature()
    {
        var narrowed = new AiOptions();
        narrowed.Detection.Classes = ["person"];

        Assert.NotEqual(
            CameraAiCoordinator.Signature(With(null), new AiOptions()),
            CameraAiCoordinator.Signature(With(null), narrowed));
    }

    [Theory]
    [MemberData(nameof(RegionChanges))]
    public void Changing_a_region_setting_changes_the_signature(Action<RegionOptions> change)
    {
        // Every one of these is read when the planner is built, so without a restart behind it the
        // new value is stored, shown on the settings page, and not in use.
        var changed = new AiOptions();
        change(changed.Detection.Regions);

        Assert.NotEqual(
            CameraAiCoordinator.Signature(With(null), new AiOptions()),
            CameraAiCoordinator.Signature(With(null), changed));
    }

    public static TheoryData<Action<RegionOptions>> RegionChanges()
    {
        var data = new TheoryData<Action<RegionOptions>>();
        data.Add(r => r.Mode = RegionMode.Off);
        data.Add(r => r.AutoMinRatio = 2.5);
        data.Add(r => r.FloorSeconds = 30);
        data.Add(r => r.MaxPerFrame = 6);
        data.Add(r => r.MinCells = 12);
        data.Add(r => r.PaddingFraction = 0.2);
        data.Add(r => r.MinSizeFraction = 0.5);
        data.Add(r => r.MinRegionScale = 0.75);

        // The tiling knobs are read when the pipeline plans its floor tiles on the first frame,
        // so they are session-read like everything else here. The hand-written digest this
        // signature replaced left all three out — the exact omission serializing whole objects
        // exists to prevent.
        data.Add(r => r.TiledFloor = !r.TiledFloor);
        data.Add(r => r.TiledFloorMinGain = 3.5);
        data.Add(r => r.TileOverlapFraction = 0.33);
        return data;
    }

    [Theory]
    [MemberData(nameof(TrackingChanges))]
    public void Changing_a_tracking_setting_changes_the_signature(Action<TrackingOptions> change)
    {
        // Every one of these is read when ObjectTracker is constructed and never re-read, so a
        // change with no restart behind it is a value that is stored, shown on the settings page,
        // and not in use. The filter constants are covered alongside the obvious ones deliberately:
        // a field left out because it looked like an internal is exactly the omission that goes
        // unnoticed until somebody wonders why retuning did nothing.
        var changed = new AiOptions();
        change(changed.Detection.Tracking);

        Assert.NotEqual(
            CameraAiCoordinator.Signature(With(null), new AiOptions()),
            CameraAiCoordinator.Signature(With(null), changed));
    }

    public static TheoryData<Action<TrackingOptions>> TrackingChanges()
    {
        var data = new TheoryData<Action<TrackingOptions>>();
        data.Add(t => t.MinIou = 0.5f);
        data.Add(t => t.ConfirmSeconds = 3);
        data.Add(t => t.CoastSeconds = 8);
        data.Add(t => t.ProcessNoise = 0.5f);
        data.Add(t => t.MeasurementNoise = 0.5f);
        data.Add(t => t.MaxTracks = 8);
        return data;
    }

    [Fact]
    public void Region_settings_do_not_collide_with_the_motion_gate()
    {
        // They share a compare grid and a vocabulary, and a change to either would be plausible as
        // the other. Distinct signatures are what keep a restart attributable.
        var region = new AiOptions();
        region.Detection.Regions.MinCells = 9;

        var motion = new AiOptions();
        motion.Motion.PixelDelta = 9;

        Assert.NotEqual(
            CameraAiCoordinator.Signature(With(null), region),
            CameraAiCoordinator.Signature(With(null), motion));
    }

    [Fact]
    public void Changing_a_server_wide_sound_alert_list_changes_the_signature()
    {
        var narrowed = new AiOptions();
        narrowed.Sound.AlertLabels = ["Siren"];

        Assert.NotEqual(
            CameraAiCoordinator.Signature(With(null), new AiOptions()),
            CameraAiCoordinator.Signature(With(null), narrowed));
    }

    /// <summary>
    /// Spelling out the built-in list and leaving it empty are the same instruction, so moving
    /// between them must not restart every camera on the Server to arrive at what was already
    /// running.
    /// </summary>
    [Fact]
    public void Naming_the_built_in_class_list_explicitly_is_not_a_change()
    {
        var spelled = new AiOptions();
        spelled.Detection.Classes = [.. DetectionOptions.DefaultClasses];

        Assert.Equal(
            CameraAiCoordinator.Signature(With(null), new AiOptions()),
            CameraAiCoordinator.Signature(With(null), spelled));
    }

    /// <summary>
    /// A model path change is real, and restarting a camera's session does not apply it — the
    /// model is one instance loaded when the process starts, which is why the App marks these as
    /// needing a restart. Including them here would churn every session to arrive at the same
    /// weights.
    /// </summary>
    [Fact]
    public void Changing_a_model_path_does_not_restart_sessions()
    {
        var moved = new AiOptions();
        moved.Detection.ModelPath = "models/detect/other.onnx";
        moved.Detection.NumThreads = 8;

        Assert.Equal(
            CameraAiCoordinator.Signature(With(null), new AiOptions()),
            CameraAiCoordinator.Signature(With(null), moved));
    }

    // ------------------------------------------------------- the newer override bags

    private static Camera Sound(CameraSoundTuning tuning) =>
        WithBags(sound: tuning);

    private static Camera Motion(CameraMotionTuning tuning) =>
        WithBags(motion: tuning);

    private static Camera WithBags(
        CameraSoundTuning? sound = null,
        CameraMotionTuning? motion = null) => new()
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
        AiVision = true,
        AiAudio = true,
        SoundTuning = sound,
        MotionTuning = motion,
    };

    [Fact]
    public void Changing_a_cameras_alert_sounds_changes_the_signature() =>
        Assert.NotEqual(
            Signature(Sound(new CameraSoundTuning { AlertLabels = ["Siren"] })),
            Signature(Sound(new CameraSoundTuning { AlertLabels = ["Glass"] })));

    [Fact]
    public void Changing_a_cameras_movement_gate_changes_the_signature() =>
        Assert.NotEqual(
            Signature(Motion(new CameraMotionTuning { MinChangedFraction = 0.02 })),
            Signature(Motion(new CameraMotionTuning { MinChangedFraction = 0.05 })));

    [Fact]
    public void Changing_a_cameras_alert_classes_changes_the_signature() =>
        Assert.NotEqual(
            Signature(Detect(new CameraDetectionTuning { AlertClasses = ["person"] })),
            Signature(Detect(new CameraDetectionTuning { AlertClasses = ["car"] })));

    [Fact]
    public void Changing_a_cameras_examination_rate_changes_the_signature() =>
        Assert.NotEqual(
            Signature(Detect(new CameraDetectionTuning { MaxFps = 1.0 })),
            Signature(Detect(new CameraDetectionTuning { MaxFps = 0.2 })));
}
