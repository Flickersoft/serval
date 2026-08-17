using Serval.Ai;

namespace Serval.Server.Cameras;

/// <summary>
/// Every per-camera tuning knob, described once: how to read it off its override bag, what makes
/// a value illegal, and how to write it into the camera's resolved options. The bags themselves
/// stay dumb nullable shells — they are the JSON/BSON wire shape — and the override check, the
/// validation clause and the apply clause all drive off this one table, so a knob added here
/// cannot be validated and then silently never applied.
///
/// <para>Both ends of each range are rejected rather than clamped, because every out-of-range
/// value here is a way of switching a detector off that reads like tuning. A zero RMS threshold
/// is the clearest: RMS is never negative, so <c>rms &gt;= 0</c> always holds and the gate wedges
/// permanently open — the caller asked for "no threshold" and would get "no gate", which is the
/// CPU cost the gate exists to avoid, arrived at by what looks like turning a dial down. The
/// empty-array cases are rejected rather than interpreted for a similar reason: an empty class
/// list could defensibly mean "everything" or "nothing", and a camera that silently detects
/// nothing while its configuration looks deliberate is the worse of the two readings.</para>
/// </summary>
internal static class TuningCatalog
{
    internal interface IKnob<in TBag, in TTarget>
    {
        bool IsSet(TBag bag);

        /// <summary>Why the knob's value is illegal, or null when it is unset or fine.</summary>
        string? Problem(TBag bag);

        void Apply(TBag bag, TTarget target);
    }

    private sealed record ValueKnob<TBag, TTarget, TValue>(
        Func<TBag, TValue?> Get,
        Func<TValue, string?> Validate,
        Action<TTarget, TValue> Write) : IKnob<TBag, TTarget>
        where TValue : struct
    {
        public bool IsSet(TBag bag) => Get(bag) is not null;

        public string? Problem(TBag bag) => Get(bag) is { } value ? Validate(value) : null;

        public void Apply(TBag bag, TTarget target)
        {
            if (Get(bag) is { } value)
            {
                Write(target, value);
            }
        }
    }

    private sealed record ListKnob<TBag, TTarget, TValue>(
        Func<TBag, TValue?> Get,
        Func<TValue, string?> Validate,
        Action<TTarget, TValue> Write) : IKnob<TBag, TTarget>
        where TValue : class
    {
        public bool IsSet(TBag bag) => Get(bag) is not null;

        public string? Problem(TBag bag) => Get(bag) is { } value ? Validate(value) : null;

        public void Apply(TBag bag, TTarget target)
        {
            if (Get(bag) is { } value)
            {
                Write(target, value);
            }
        }
    }

    internal static readonly IReadOnlyList<IKnob<CameraDetectionTuning, DetectionOptions>> Detection =
    [
        new ListKnob<CameraDetectionTuning, DetectionOptions, string[]>(
            t => t.Classes,
            v => v.Length == 0
                ? "Classes must name at least one class when set. Omit it to inherit the server "
                    + "default; an empty list would leave this camera detecting nothing while looking "
                    + "deliberately configured."
                : null,
            (d, v) => d.Classes = [.. v]),
        new ListKnob<CameraDetectionTuning, DetectionOptions, string[]>(
            t => t.DescribeClasses,
            v => v.Length == 0
                ? "DescribeClasses must name at least one class when set. Omit it to inherit the "
                    + "server default. To record objects without ever describing them, set it to a "
                    + "class this camera does not see rather than leaving it empty."
                : null,
            (d, v) => d.DescribeClasses = [.. v]),
        new ValueKnob<CameraDetectionTuning, DetectionOptions, double>(
            t => t.ScoreThreshold,
            v => v is <= 0 or >= 1
                ? "ScoreThreshold is a confidence and must be between 0 and 1 exclusive when set. "
                    + "0 admits every box the model considered and 1 admits none, so both switch the "
                    + "detector off rather than tuning it."
                : null,
            (d, v) => d.ScoreThreshold = (float)v),
        new ValueKnob<CameraDetectionTuning, DetectionOptions, double>(
            t => t.MinObjectFraction,
            v => v is < 0 or >= 1
                ? "MinObjectFraction is a fraction of the frame's area and must be at least 0 and "
                    + "below 1 when set. Zero means no floor; 1 would reject everything smaller than the "
                    + "whole picture, which is everything."
                : null,
            (d, v) => d.MinObjectFraction = v),
        new ValueKnob<CameraDetectionTuning, DetectionOptions, double>(
            t => t.TrackConfirmSeconds,
            v => v < 0
                ? "TrackConfirmSeconds cannot be negative. Zero is the floor — an object is believed "
                    + "on its second sighting whenever that lands — so there is no value below it to "
                    + "express."
                : null,
            (d, v) => d.Tracking.ConfirmSeconds = v),
        new ValueKnob<CameraDetectionTuning, DetectionOptions, double>(
            t => t.TrackCoastSeconds,
            v => v < 0
                ? "TrackCoastSeconds cannot be negative. Zero means an object is dropped the moment a "
                    + "frame misses it, which is the least this can do."
                : null,
            (d, v) => d.Tracking.CoastSeconds = v),
        new ListKnob<CameraDetectionTuning, DetectionOptions, DetectionMask[]>(
            t => t.Masks,
            MaskProblem,
            (d, v) => d.Masks = [.. v]),
        new ListKnob<CameraDetectionTuning, DetectionOptions, string[]>(
            t => t.AlertClasses,
            v => v.Length == 0
                ? "AlertClasses must name at least one class when set. Omit it to inherit the server "
                    + "default. To record objects without ever alerting on them, set it to a class this "
                    + "camera does not see rather than leaving it empty."
                : null,
            (d, v) => d.AlertClasses = [.. v]),
        new ValueKnob<CameraDetectionTuning, DetectionOptions, double>(
            t => t.AlertMinConfidence,
            v => v is <= 0 or >= 1
                ? "AlertMinConfidence is a confidence and must be between 0 and 1 exclusive when set, "
                    + "for the reason ScoreThreshold is."
                : null,
            (d, v) => d.AlertMinConfidence = (float)v),
        new ValueKnob<CameraDetectionTuning, DetectionOptions, double>(
            t => t.MaxFps,
            v => v < 0
                ? "MaxFps cannot be negative. Zero means no ceiling — every frame is examined — so "
                    + "there is no value below it to express."
                : null,
            (d, v) => d.MaxFps = v),
        new ValueKnob<CameraDetectionTuning, DetectionOptions, double>(
            t => t.MinMovementFraction,
            v => v is < 0 or > 1
                ? "MinMovementFraction is a fraction of the frame and must be between 0 and 1 when "
                    + "set."
                : null,
            (d, v) => d.MinMovementFraction = v),
        new ValueKnob<CameraDetectionTuning, DetectionOptions, double>(
            t => t.AbsenceSeconds,
            v => v <= 0
                ? "AbsenceSeconds must be greater than 0 when set. Zero would close an episode on the "
                    + "first frame a detection flickered out of, which is what this setting exists to "
                    + "prevent."
                : null,
            (d, v) => d.AbsenceSeconds = v),
        new ValueKnob<CameraDetectionTuning, DetectionOptions, double>(
            t => t.NoveltySeconds,
            v => v < 0
                ? "NoveltySeconds cannot be negative when set. Zero makes every reappearance an "
                    + "arrival, which is legal but turns parked cars and furniture back into events."
                : null,
            (d, v) => d.NoveltySeconds = v),
    ];

    /// <summary>
    /// The audio knobs write straight onto the resolved <see cref="AiOptions"/>, replacing whole
    /// branches, because the three of them land on three different objects. The sound-gate knob
    /// replaces <see cref="AiOptions.Sound"/>, which is why the sound knobs below must be applied
    /// after these — see <see cref="Ai.CameraAiOptions.For"/>.
    /// </summary>
    internal static readonly IReadOnlyList<IKnob<CameraAudioTuning, AiOptions>> Audio =
    [
        new ValueKnob<CameraAudioTuning, AiOptions, double>(
            t => t.SpeechGateRmsThreshold,
            v => RmsProblem(v, nameof(CameraAudioTuning.SpeechGateRmsThreshold)),
            (ai, v) => ai.AudioGate = ai.AudioGate with { RmsThreshold = (float)v }),
        new ValueKnob<CameraAudioTuning, AiOptions, double>(
            t => t.VadThreshold,
            v => v is <= 0 or >= 1
                ? "VadThreshold is a speech probability and must be between 0 and 1 exclusive when "
                    + "set. 0 makes every window speech and 1 makes none, so both switch the detector "
                    + "off rather than tuning it."
                : null,
            (ai, v) => ai.Vad = ai.Vad with { Threshold = (float)v }),
        new ValueKnob<CameraAudioTuning, AiOptions, double>(
            t => t.SoundGateRmsThreshold,
            v => RmsProblem(v, nameof(CameraAudioTuning.SoundGateRmsThreshold)),
            (ai, v) =>
            {
                ai.Sound = ai.Sound.Copy();
                ai.Sound.Gate.RmsThreshold = (float)v;
            }),
    ];

    internal static readonly IReadOnlyList<IKnob<CameraSoundTuning, SoundOptions>> Sound =
    [
        new ListKnob<CameraSoundTuning, SoundOptions, string[]>(
            t => t.AlertLabels,
            v => v.Length == 0
                ? "AlertLabels must name at least one sound when set. Omit it to inherit the server "
                    + "default. To record sounds without ever alerting on them, name a label this "
                    + "camera does not hear rather than leaving it empty."
                : null,
            (s, v) => s.AlertLabels = [.. v]),
        new ListKnob<CameraSoundTuning, SoundOptions, string[]>(
            t => t.IgnoredLabels,
            v => v.Length == 0
                ? "IgnoredLabels must name at least one sound when set. Omit it to inherit the server "
                    + "default — an empty list and an absent one mean the same thing, and storing the "
                    + "difference would make this camera look configured when it is not."
                : null,
            (s, v) => s.IgnoredLabels = [.. v]),
        new ValueKnob<CameraSoundTuning, SoundOptions, double>(
            t => t.MinConfidence,
            v => ConfidenceProblem(v, nameof(CameraSoundTuning.MinConfidence)),
            (s, v) => s.MinConfidence = (float)v),
        new ValueKnob<CameraSoundTuning, SoundOptions, double>(
            t => t.AlertMinConfidence,
            v => ConfidenceProblem(v, nameof(CameraSoundTuning.AlertMinConfidence)),
            (s, v) => s.AlertMinConfidence = (float)v),
        new ValueKnob<CameraSoundTuning, SoundOptions, double>(
            t => t.CooldownSeconds,
            v => CooldownProblem(v, nameof(CameraSoundTuning.CooldownSeconds)),
            (s, v) => s.CooldownSeconds = v),
        new ValueKnob<CameraSoundTuning, SoundOptions, double>(
            t => t.AlertCooldownSeconds,
            v => CooldownProblem(v, nameof(CameraSoundTuning.AlertCooldownSeconds)),
            (s, v) => s.AlertCooldownSeconds = v),
    ];

    internal static readonly IReadOnlyList<IKnob<CameraMotionTuning, MotionOptions>> Motion =
    [
        new ValueKnob<CameraMotionTuning, MotionOptions, int>(
            t => t.PixelDelta,
            v => v is < 0 or > 255
                ? "PixelDelta is an 8-bit brightness difference and must be between 0 and 255 when "
                    + "set."
                : null,
            (m, v) => m.PixelDelta = v),
        new ValueKnob<CameraMotionTuning, MotionOptions, double>(
            t => t.MinChangedFraction,
            v => v is < 0 or > 1
                ? "MinChangedFraction is a fraction of the frame and must be between 0 and 1 when set."
                : null,
            (m, v) => m.MinChangedFraction = v),
        new ValueKnob<CameraMotionTuning, MotionOptions, double>(
            t => t.MaxChangedFraction,
            v => v is < 0 or > 1
                ? "MaxChangedFraction is a fraction of the frame and must be between 0 and 1 when set."
                : null,
            (m, v) => m.MaxChangedFraction = v),
    ];

    /// <summary>
    /// The one cross-field rule: a minimum at or above the maximum is a gate that can never open,
    /// which no single-knob range check catches and which looks from the outside exactly like a
    /// camera where nothing ever happens.
    /// </summary>
    internal static string? MotionCrossProblem(CameraMotionTuning tuning) =>
        tuning is { MinChangedFraction: { } low, MaxChangedFraction: { } high } && low >= high
            ? $"MinChangedFraction ({low}) must be below MaxChangedFraction ({high}). Movement is "
                + "declared between the two, so this camera could never report any — which is "
                + "indistinguishable from a camera watching a room where nothing happens."
            : null;

    private static string? MaskProblem(DetectionMask[] masks)
    {
        foreach (DetectionMask mask in masks)
        {
            if (!mask.IsUsable)
            {
                return $"Mask '{mask.Name ?? "(unnamed)"}' needs at least three points, as a flat "
                    + "x1,y1,x2,y2,... list of even length. Fewer cannot enclose a region, and a "
                    + "mask that silently matches nothing is indistinguishable from one that works.";
            }

            foreach (double value in mask.Points)
            {
                if (value is < 0 or > 1 || double.IsNaN(value))
                {
                    return $"Mask '{mask.Name ?? "(unnamed)"}' has a point outside 0..1. Points are "
                        + "fractions of the frame, so that region could never match a detection.";
                }
            }
        }

        return null;
    }

    private static string? RmsProblem(double value, string name) =>
        value is <= 0 or > 1
            ? $"{name} must be greater than 0 and at most 1 when set. It is the RMS of a "
                + "16 kHz window, so 1 is a full-scale square wave and 0 would hold the gate "
                + "permanently open rather than disabling it."
            : null;

    private static string? ConfidenceProblem(double value, string name) =>
        value is <= 0 or >= 1
            ? $"{name} is a confidence and must be between 0 and 1 exclusive when set. 0 "
                + "publishes every guess the model made and 1 publishes none."
            : null;

    private static string? CooldownProblem(double value, string name) =>
        value < 0
            ? $"{name} cannot be negative. Zero is legal and means no gap at all, which on a "
                + "permanently noisy camera is a record every few seconds forever."
            : null;

    /// <summary>
    /// The shared validation shape: nothing set collapses the bag to null — so the stored
    /// document, the advisories and the App agree on what "this camera is tuned" means, whatever
    /// the client sent — and the first illegal knob throws.
    /// </summary>
    internal static TBag? Validate<TBag, TTarget>(
        TBag? bag, IReadOnlyList<IKnob<TBag, TTarget>> knobs)
        where TBag : class
    {
        if (bag is null)
        {
            return null;
        }

        if (!knobs.Any(k => k.IsSet(bag)))
        {
            return null;
        }

        foreach (IKnob<TBag, TTarget> knob in knobs)
        {
            if (knob.Problem(bag) is { } problem)
            {
                throw new CameraValidationException(problem);
            }
        }

        return bag;
    }

    internal static bool HasAnyOverride<TBag, TTarget>(
        TBag? bag, IReadOnlyList<IKnob<TBag, TTarget>> knobs)
        where TBag : class =>
        bag is not null && knobs.Any(k => k.IsSet(bag));

    internal static void Apply<TBag, TTarget>(
        TBag bag, TTarget target, IReadOnlyList<IKnob<TBag, TTarget>> knobs)
    {
        foreach (IKnob<TBag, TTarget> knob in knobs)
        {
            knob.Apply(bag, target);
        }
    }
}
