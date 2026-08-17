using Serval.Ai;
using Serval.Server.Ai;
using Serval.Server.Cameras;

namespace Serval.Server.Tests;

/// <summary>
/// The per-camera settings must be a copy, never a write to the shared instance.
///
/// Both failure modes this guards are silent. Mutating the shared options would half-apply to any
/// gate already running — <see cref="AudioLevelGate"/> re-reads its threshold every window but
/// bakes pre-roll and hangover in at construction — and would simultaneously retune every other
/// camera, so tuning a quiet indoor camera would change what the one watching the driveway hears.
/// Neither shows up in a log: the value the operator asked for is exactly the value in force.
/// </summary>
public class CameraAiOptionsTests
{
    private static CameraAudioTuning Tuning(
        float? speechGate = null, float? vad = null, float? soundGate = null) => new()
    {
        SpeechGateRmsThreshold = speechGate,
        VadThreshold = vad,
        SoundGateRmsThreshold = soundGate,
    };

    [Fact]
    public void A_camera_with_no_overrides_shares_the_server_options()
    {
        var global = new AiOptions();

        // Same instance, not merely equal: a deployment where nothing has been tuned must behave
        // object-for-object as it did before per-camera settings existed.
        Assert.Same(global, CameraAiOptions.For(global, null));
    }

    [Fact]
    public void An_empty_tuning_object_shares_the_server_options()
    {
        var global = new AiOptions();

        Assert.Same(global, CameraAiOptions.For(global, Tuning()));
    }

    [Fact]
    public void An_override_never_touches_the_shared_options()
    {
        var global = new AiOptions();
        global.AudioGate.RmsThreshold = 0.01f;

        CameraAiOptions.For(global, Tuning(speechGate: 0.0015f));

        Assert.Equal(0.01f, global.AudioGate.RmsThreshold);
    }

    [Fact]
    public void Two_cameras_get_independent_gate_options()
    {
        var global = new AiOptions();

        AiOptions indoor = CameraAiOptions.For(global, Tuning(speechGate: 0.0015f));
        AiOptions outdoor = CameraAiOptions.For(global, Tuning(speechGate: 0.02f));

        Assert.NotSame(indoor.AudioGate, outdoor.AudioGate);
        Assert.Equal(0.0015f, indoor.AudioGate.RmsThreshold);
        Assert.Equal(0.02f, outdoor.AudioGate.RmsThreshold);
    }

    [Fact]
    public void Overriding_the_speech_gate_leaves_the_other_branches_shared()
    {
        var global = new AiOptions();

        AiOptions resolved = CameraAiOptions.For(global, Tuning(speechGate: 0.0015f));

        Assert.NotSame(global.AudioGate, resolved.AudioGate);
        Assert.Same(global.Vad, resolved.Vad);
        Assert.Same(global.Sound, resolved.Sound);
        Assert.Same(global.Speaker, resolved.Speaker);
        Assert.Same(global.Asr, resolved.Asr);
    }

    [Fact]
    public void The_sound_gate_override_copies_the_nested_gate()
    {
        var global = new AiOptions();
        global.Sound.Gate.RmsThreshold = 0.01f;

        AiOptions resolved = CameraAiOptions.For(global, Tuning(soundGate: 0.0015f));

        Assert.NotSame(global.Sound, resolved.Sound);
        Assert.NotSame(global.Sound.Gate, resolved.Sound.Gate);
        Assert.Equal(0.0015f, resolved.Sound.Gate.RmsThreshold);
        Assert.Equal(0.01f, global.Sound.Gate.RmsThreshold);
    }

    [Fact]
    public void The_vad_override_copies_only_the_vad()
    {
        var global = new AiOptions();
        global.Vad.Threshold = 0.5f;

        AiOptions resolved = CameraAiOptions.For(global, Tuning(vad: 0.7f));

        Assert.NotSame(global.Vad, resolved.Vad);
        Assert.Equal(0.7f, resolved.Vad.Threshold);
        Assert.Equal(0.5f, global.Vad.Threshold);
        Assert.Same(global.AudioGate, resolved.AudioGate);
    }

    [Fact]
    public void All_three_overrides_apply_together()
    {
        var global = new AiOptions();

        AiOptions resolved = CameraAiOptions.For(
            global, Tuning(speechGate: 0.0015f, vad: 0.7f, soundGate: 0.002f));

        Assert.Equal(0.0015f, resolved.AudioGate.RmsThreshold);
        Assert.Equal(0.7f, resolved.Vad.Threshold);
        Assert.Equal(0.002f, resolved.Sound.Gate.RmsThreshold);
    }

    /// <summary>
    /// The scalar members are carried by the compiler-generated <c>with</c>, so what is left to
    /// pin is the part <c>with</c> gets wrong on its own: the reference-typed members must be
    /// fresh instances, or one camera's override writes through into every other camera's.
    /// </summary>
    [Fact]
    public void A_copied_sound_shares_no_mutable_state()
    {
        var configured = new SoundOptions
        {
            IgnoredLabels = ["Speech"],
            AlertLabels = ["Siren"],
        };
        configured.Gate.RmsThreshold = 0.004f;

        SoundOptions copy = configured.Copy();

        Assert.NotSame(configured.Gate, copy.Gate);
        Assert.Equal(0.004f, copy.Gate.RmsThreshold);
        Assert.NotSame(configured.IgnoredLabels, copy.IgnoredLabels);
        Assert.NotSame(configured.AlertLabels, copy.AlertLabels);
        Assert.Equal(configured.IgnoredLabels, copy.IgnoredLabels);
        Assert.Equal(configured.AlertLabels, copy.AlertLabels);
    }

    // --- Detection tuning ----------------------------------------------------------------------

    private static CameraDetectionTuning DetectTuning(Action<CameraDetectionTuning>? configure = null)
    {
        var tuning = new CameraDetectionTuning();
        configure?.Invoke(tuning);
        return tuning;
    }

    [Fact]
    public void An_untuned_camera_still_gets_the_shared_instance()
    {
        // The fast path has to survive a second tuning type being added, or every deployment that
        // has tuned nothing quietly starts allocating a settings object per camera per session.
        var global = new AiOptions();

        Assert.Same(global, CameraAiOptions.For(global, null, null));
        Assert.Same(global, CameraAiOptions.For(global, Tuning(), DetectTuning()));
    }

    [Fact]
    public void Detection_overrides_alone_are_enough_to_produce_a_copy()
    {
        var global = new AiOptions();

        AiOptions resolved = CameraAiOptions.For(
            global, null, DetectTuning(t => t.ScoreThreshold = 0.5));

        Assert.NotSame(global, resolved);
        Assert.NotSame(global.Detection, resolved.Detection);
        Assert.Equal(0.5f, resolved.Detection.ScoreThreshold);
    }

    [Fact]
    public void A_cameras_detection_override_never_reaches_the_shared_defaults()
    {
        var global = new AiOptions();
        float before = global.Detection.ScoreThreshold;
        string[] classesBefore = [.. global.Detection.Classes];

        CameraAiOptions.For(global, null, DetectTuning(t =>
        {
            t.ScoreThreshold = 0.9;
            t.Classes = ["person"];
        }));

        Assert.Equal(before, global.Detection.ScoreThreshold);
        Assert.Equal(classesBefore, global.Detection.Classes);
    }

    [Fact]
    public void A_cameras_class_list_is_its_own_array()
    {
        // Aliasing the caller's array would let a later edit to the stored camera document reach
        // into a running session's policy, which reads it once into a set at construction.
        var global = new AiOptions();
        string[] mine = ["person"];

        AiOptions resolved = CameraAiOptions.For(global, null, DetectTuning(t => t.Classes = mine));

        Assert.NotSame(mine, resolved.Detection.Classes);
        Assert.Equal(mine, resolved.Detection.Classes);
    }

    [Fact]
    public void Several_detection_overrides_all_survive_together()
    {
        // The detection branches all live on one object, unlike the audio ones. Copying per
        // overridden field instead of once would leave only the last edit standing.
        var global = new AiOptions();

        AiOptions resolved = CameraAiOptions.For(global, null, DetectTuning(t =>
        {
            t.ScoreThreshold = 0.45;
            t.TrackConfirmSeconds = 4;
            t.Classes = ["person", "dog"];
            t.DescribeClasses = ["person"];
            t.Masks = [new DetectionMask { Name = "road", Points = [0, 0, 1, 0, 1, 0.3] }];
        }));

        Assert.Equal(0.45f, resolved.Detection.ScoreThreshold);
        Assert.Equal(4, resolved.Detection.Tracking.ConfirmSeconds);
        Assert.Equal(["person", "dog"], resolved.Detection.Classes);
        Assert.Equal(["person"], resolved.Detection.DescribeClasses);
        Assert.Single(resolved.Detection.Masks);
    }

    [Fact]
    public void Audio_and_detection_overrides_compose_on_one_camera()
    {
        var global = new AiOptions();

        AiOptions resolved = CameraAiOptions.For(
            global,
            Tuning(speechGate: 0.0015f),
            DetectTuning(t => t.ScoreThreshold = 0.6));

        Assert.Equal(0.0015f, resolved.AudioGate.RmsThreshold);
        Assert.Equal(0.6f, resolved.Detection.ScoreThreshold);
        Assert.NotSame(global.AudioGate, resolved.AudioGate);
        Assert.NotSame(global.Detection, resolved.Detection);
    }

    [Fact]
    public void An_unset_detection_field_keeps_the_server_default()
    {
        var global = new AiOptions();
        global.Detection.Tracking.ConfirmSeconds = 3;

        AiOptions resolved = CameraAiOptions.For(
            global, null, DetectTuning(t => t.ScoreThreshold = 0.5));

        Assert.Equal(3, resolved.Detection.Tracking.ConfirmSeconds);
    }

    // ------------------------------------------------------- sound and movement

    [Fact]
    public void A_cameras_alert_sounds_replace_the_server_list_rather_than_adding_to_it()
    {
        var global = new AiOptions();
        global.Sound.AlertLabels = ["Glass", "Siren", "Fire alarm"];

        AiOptions resolved = CameraAiOptions.For(
            global, null, null, new CameraSoundTuning { AlertLabels = ["Crying, sobbing"] });

        Assert.Equal(["Crying, sobbing"], resolved.Sound.EffectiveAlertLabels);
        Assert.Equal(["Glass", "Siren", "Fire alarm"], global.Sound.AlertLabels);
    }

    [Fact]
    public void A_cameras_sound_thresholds_do_not_touch_the_shared_instance()
    {
        var global = new AiOptions();

        AiOptions resolved = CameraAiOptions.For(
            global, null, null, new CameraSoundTuning
            {
                MinConfidence = 0.5,
                AlertMinConfidence = 0.8,
                CooldownSeconds = 120,
                AlertCooldownSeconds = 5,
                IgnoredLabels = ["Speech"],
            });

        Assert.NotSame(global.Sound, resolved.Sound);
        Assert.Equal(0.5f, resolved.Sound.MinConfidence);
        Assert.Equal(0.8f, resolved.Sound.AlertMinConfidence);
        Assert.Equal(120, resolved.Sound.CooldownSeconds);
        Assert.Equal(5, resolved.Sound.AlertCooldownSeconds);
        Assert.Equal(["Speech"], resolved.Sound.IgnoredLabels);

        Assert.Equal(0.35f, global.Sound.MinConfidence);
        Assert.Empty(global.Sound.IgnoredLabels);
    }

    /// <summary>
    /// The one ordering hazard in the resolver. The audio branch replaces
    /// <see cref="AiOptions.Sound"/> to set the gate threshold and the sound branch replaces it
    /// again for its own fields, so a camera setting both must end up with both — not with
    /// whichever branch ran second.
    /// </summary>
    [Fact]
    public void A_camera_can_set_its_sound_gate_and_its_alert_labels_at_once()
    {
        var global = new AiOptions();

        AiOptions resolved = CameraAiOptions.For(
            global,
            Tuning(soundGate: 0.002f),
            null,
            new CameraSoundTuning { AlertLabels = ["Siren"] });

        Assert.Equal(0.002f, resolved.Sound.Gate.RmsThreshold);
        Assert.Equal(["Siren"], resolved.Sound.EffectiveAlertLabels);

        // And neither reached the shared instance.
        Assert.Equal(0.01f, global.Sound.Gate.RmsThreshold);
        Assert.Empty(global.Sound.AlertLabels);
    }

    [Fact]
    public void A_cameras_movement_gate_does_not_touch_the_shared_instance()
    {
        var global = new AiOptions();

        AiOptions resolved = CameraAiOptions.For(
            global, null, null, null,
            new CameraMotionTuning { MinChangedFraction = 0.08, PixelDelta = 40 });

        Assert.NotSame(global.Motion, resolved.Motion);
        Assert.Equal(0.08, resolved.Motion.MinChangedFraction);
        Assert.Equal(40, resolved.Motion.PixelDelta);

        // Unset fields still follow the Server.
        Assert.Equal(global.Motion.MaxChangedFraction, resolved.Motion.MaxChangedFraction);
        Assert.Equal(0.02, global.Motion.MinChangedFraction);
    }

    [Fact]
    public void The_wider_detection_overrides_are_applied()
    {
        var global = new AiOptions();

        AiOptions resolved = CameraAiOptions.For(global, null, new CameraDetectionTuning
        {
            AlertClasses = ["car"],
            AlertMinConfidence = 0.85,
            MaxFps = 0.25,
            MinMovementFraction = 0.05,
            AbsenceSeconds = 90,
            NoveltySeconds = 600,
        });

        Assert.Equal(["car"], resolved.Detection.EffectiveAlertClasses);
        Assert.Equal(0.85f, resolved.Detection.AlertMinConfidence);
        Assert.Equal(0.25, resolved.Detection.MaxFps);
        Assert.Equal(0.05, resolved.Detection.MinMovementFraction);
        Assert.Equal(90, resolved.Detection.AbsenceSeconds);
        Assert.Equal(600, resolved.Detection.NoveltySeconds);

        Assert.Equal(0.6f, global.Detection.AlertMinConfidence);
        Assert.Equal(0, global.Detection.MaxFps);
    }

    [Fact]
    public void An_empty_sound_or_movement_object_still_shares_the_server_options()
    {
        var global = new AiOptions();

        Assert.Same(
            global,
            CameraAiOptions.For(
                global, null, null, new CameraSoundTuning(), new CameraMotionTuning()));
    }
}
