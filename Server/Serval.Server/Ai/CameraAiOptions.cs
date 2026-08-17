using Serval.Ai;
using Serval.Server.Cameras;

namespace Serval.Server.Ai;

/// <summary>
/// Resolves one camera's <see cref="AiOptions"/> from the server-wide settings and whatever that
/// camera overrides.
///
/// <para>The whole job is to produce a settings object the camera's detectors can be built from
/// <em>without</em> mutating anything shared. Mutation is the obvious implementation and it is
/// wrong twice over: <see cref="AudioLevelGate"/> re-reads its threshold on every window but bakes
/// its pre-roll and hangover in at construction, so writing to a live instance half-applies to
/// gates already running; and every camera holds the same instance, so tuning one would retune all
/// of them. Both faults are silent — the logs would show the value the operator asked for.</para>
///
/// <para>Called once per session start, which is also what makes it correct with respect to
/// <c>PostConfigure&lt;ServerOptions&gt;</c> in <c>Program.cs</c>: that resolves
/// <see cref="SpeakerOptions.ConversationAudioDirectory"/> against the media root at container
/// build time, so a copy taken here — from a hosted service, afterwards — carries it, while one
/// taken at DI registration might not.</para>
/// </summary>
internal static class CameraAiOptions
{
    /// <summary>
    /// The settings this camera's detectors should run against.
    ///
    /// Returns <paramref name="global"/> itself when nothing is overridden — not a
    /// micro-optimisation, but what makes an untuned deployment behave identically, object-for-
    /// object, to one with no per-camera tuning in the picture at all.
    /// </summary>
    public static AiOptions For(
        AiOptions global,
        CameraAudioTuning? tuning,
        CameraDetectionTuning? detection = null,
        CameraSoundTuning? sound = null,
        CameraMotionTuning? motion = null)
    {
        bool hasAudio = TuningCatalog.HasAnyOverride(tuning, TuningCatalog.Audio);
        bool hasDetection = TuningCatalog.HasAnyOverride(detection, TuningCatalog.Detection);
        bool hasSound = TuningCatalog.HasAnyOverride(sound, TuningCatalog.Sound);
        bool hasMotion = TuningCatalog.HasAnyOverride(motion, TuningCatalog.Motion);

        if (!hasAudio && !hasDetection && !hasSound && !hasMotion)
        {
            return global;
        }

        // Branches this camera does not override stay shared by reference. They are read-only
        // here, and the models behind Asr/Vision are one instance across every camera anyway, so
        // copying them would suggest a per-camera-ness that does not exist.
        var resolved = new AiOptions
        {
            Vad = global.Vad,
            AudioGate = global.AudioGate,
            Asr = global.Asr,
            Vision = global.Vision,
            Motion = global.Motion,
            Detection = global.Detection,
            Speaker = global.Speaker,
            Sound = global.Sound,
        };

        // Each overridden group is copied once and then written through, so no knob's edit can
        // reach the shared instance every other camera is reading. Apply casts the stored double
        // to the float the models take here rather than on the properties, so the registry stores
        // exactly what a client sent — narrowing on the way in would make every GET disagree with
        // the PUT that produced it.
        if (hasAudio)
        {
            TuningCatalog.Apply(tuning!, resolved, TuningCatalog.Audio);
        }

        if (hasDetection)
        {
            DetectionOptions copy = global.Detection.Copy();
            TuningCatalog.Apply(detection!, copy, TuningCatalog.Detection);
            resolved.Detection = copy;
        }

        // After the audio group, deliberately. Both write to Sound: the audio tuning replaces the
        // instance to set the gate threshold, so this copies resolved.Sound — the audio-modified
        // instance when there is one — rather than global.Sound, which would discard it.
        if (hasSound)
        {
            SoundOptions copy = resolved.Sound.Copy();
            TuningCatalog.Apply(sound!, copy, TuningCatalog.Sound);
            resolved.Sound = copy;
        }

        if (hasMotion)
        {
            MotionOptions copy = global.Motion with { };
            TuningCatalog.Apply(motion!, copy, TuningCatalog.Motion);
            resolved.Motion = copy;
        }

        return resolved;
    }
}
