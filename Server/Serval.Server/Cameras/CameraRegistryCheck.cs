using Serval.Server.Configuration;
using Serval.Server.Ingest;

namespace Serval.Server.Cameras;

/// <summary>
/// Applies the API's rules to a camera that is already stored.
///
/// <see cref="CameraRepository.Validate"/> runs on write and nowhere else, so a document written
/// straight into Mongo can sit in the registry doing nothing. Dropping it silently would stop that
/// camera recording with nothing anywhere saying why. This is the same rules applied on read, so
/// the answer here and the answer at the API can never disagree.
/// </summary>
public static class CameraRegistryCheck
{
    /// <summary>
    /// Null when the camera is fit to ingest; otherwise the reason, phrased for someone who has to
    /// fix it.
    /// </summary>
    public static string? Fault(Camera camera, IngestOptions ingest, FfmpegCapabilities capabilities)
    {
        // Streams is `required` in C#, but the Mongo driver does not enforce that — a document
        // without the field deserializes with it null rather than failing. Validate would report
        // "needs at least one stream", which is true but says nothing about why.
        if (camera.Streams is null)
        {
            return "its stored document has no 'streams' field. Re-POST it with a streams array "
                + "(PUT /api/cameras/{id}).";
        }

        try
        {
            CameraRepository.Validate(camera);
            CameraRepository.ValidateTranscodes(camera, ingest, capabilities);
            return null;
        }
        catch (CameraValidationException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Configurations that are legal but will not do what the operator expects. Logged, never
    /// rejected — each of these has a legitimate use, and a 400 whose fix lives in a different
    /// file is worse than a warning.
    /// </summary>
    public static IReadOnlyList<string> Advisories(Camera camera, ServerOptions options)
    {
        var advisories = new List<string>();

        if (camera.Streams is null)
        {
            return advisories;
        }

        NotRecordedAdvisories(camera, advisories);
        UnusedStreamAdvisories(camera, advisories);

        // "Recording untouched is free" is only true when snapshots come from somewhere else.
        // Otherwise ffmpeg decodes every frame anyway to produce the JPEG, and the copy saves only
        // the encode — which on a 4K camera is the smaller half. Silent while nothing is being
        // written, because then it describes a cost nobody is paying.
        if (camera.Recording
            && camera.RecordStream is { Transcode: null } record
            && record.Roles.Contains(StreamRole.Detect))
        {
            advisories.Add(
                $"Camera {camera.Id} records by copy but produces its snapshots from the same "
                + "stream, so ffmpeg decodes every frame regardless. Giving it a smaller 'detect' "
                + "sub stream is what makes the recording a true zero-decode copy.");
        }

        if (options.WebRtc.Enabled
            && camera.LiveStream is { Url: var liveUrl }
            && SourceArguments.IsFile(liveUrl))
        {
            advisories.Add(
                $"Camera {camera.Id} has no WebRTC live view: its 'live' stream is a local file "
                + "path, which go2rtc cannot serve. Expected for a file-backed test camera; if this "
                + "is a real camera, point the 'live' role at its network URL.");
        }

        if (camera.AiVision && !options.ServerAi.Enabled)
        {
            advisories.Add(
                $"Camera {camera.Id} has AiVision set but Serval:ServerAi:Enabled is false, so no "
                + "scene descriptions will be produced.");
        }

        if (camera.AiVision && options.ServerAi.Enabled && !options.Ai.Vision.Enabled)
        {
            advisories.Add(
                $"Camera {camera.Id} has AiVision set but Serval:Ai:Vision:Enabled is false, so no "
                + "scene descriptions will be produced.");
        }

        if (camera.AiAudio && !options.ServerAi.Enabled)
        {
            advisories.Add(
                $"Camera {camera.Id} has AiAudio set but Serval:ServerAi:Enabled is false, so "
                + "nothing will be transcribed.");
        }

        AudioTuningAdvisories(camera, options, advisories);

        return advisories;
    }

    /// <summary>
    /// A camera writing nothing to disk, and the settings that quietly stop meaning anything while
    /// it is not.
    ///
    /// Keeping nothing is a legitimate choice, so the first line is a statement rather than a
    /// complaint — but it is the one configuration where the absence of footage is the
    /// configuration working, and a log that never said so leaves an operator hunting a recorder
    /// that was never asked to run. The two that follow are settings stored and reported in the
    /// App while having no effect at all, which is the same trap as tuning a camera by ear and
    /// then turning its audio off.
    ///
    /// There are two ways to arrive here and they are not the same thing to fix, so the reason is
    /// named: no stream carries the role, or <see cref="Camera.Recording"/> is switched off over a
    /// stream that does. Both are read the same way by everything downstream.
    /// </summary>
    private static void NotRecordedAdvisories(Camera camera, List<string> advisories)
    {
        if (camera.Recording && camera.RecordStream is not null)
        {
            return;
        }

        string because = camera.RecordStream is { Name: var recordStream }
            ? $"Recording is switched off (its 'record' stream '{recordStream}' is kept and applies "
                + "again the moment it is switched back on)"
            : "no stream is set to 'record'";

        advisories.Add(
            $"Camera {camera.Id} writes nothing to disk because {because}: no playback, no "
            + "timeline, no clip export. Detection, alerts and the WebRTC live view all still run, "
            + "and footage already on disk stays playable until it expires.");

        if (camera.RecordAudio)
        {
            advisories.Add(
                $"Camera {camera.Id} has RecordAudio set but is not recording, so there is nothing "
                + "for the audio to be recorded into.");
        }

        if (camera.RetentionDays is not null && camera.RecordStream is null)
        {
            advisories.Add(
                $"Camera {camera.Id} sets RetentionDays but has no 'record' stream, so there is "
                + "nothing to expire.");
        }
    }

    /// <summary>
    /// Streams carrying no roles, which nothing pulls.
    ///
    /// This is the advisory that pays for dropping the validation rule. A role-less stream is a
    /// supported way to hold a source out of service with its address intact — but it is also
    /// exactly what a typo in a role list produces, and the two are indistinguishable from the
    /// document. Naming the stream is what keeps the second from looking like a working
    /// configuration.
    /// </summary>
    private static void UnusedStreamAdvisories(Camera camera, List<string> advisories)
    {
        foreach (CameraStream stream in camera.Streams)
        {
            if (stream.Roles is { Count: > 0 })
            {
                continue;
            }

            advisories.Add(
                $"Camera {camera.Id} has a stream '{stream.Name}' with no roles, so nothing is "
                + "pulled from it. Expected if you are holding it out of service; if not, it is "
                + "missing one of 'record', 'detect' or 'live'.");

            if (stream.Transcode is { Codec: var codec })
            {
                advisories.Add(
                    $"Camera {camera.Id} stream '{stream.Name}' is set to transcode to '{codec}' "
                    + "but carries no roles, so nothing reads it. It is kept, and takes effect if "
                    + "the stream is given the 'record' role.");
            }
        }
    }

    /// <summary>
    /// Thresholds that are stored but inert, and thresholds that are set so low they undo the gate
    /// they configure. Warnings rather than rejections throughout: a genuinely quiet microphone is
    /// a real reason to want a number that would be wrong anywhere else, and that judgement is the
    /// operator's.
    /// </summary>
    private static void AudioTuningAdvisories(
        Camera camera, ServerOptions options, List<string> advisories)
    {
        if (camera.AudioTuning is not { } tuning
            || !TuningCatalog.HasAnyOverride(tuning, TuningCatalog.Audio))
        {
            return;
        }

        // The likeliest way to end up here is tuning a camera by ear and then turning its audio
        // off — the settings survive, silently, and the next person reads them as being in force.
        if (!camera.AiAudio)
        {
            advisories.Add(
                $"Camera {camera.Id} sets audio thresholds but AiAudio is false, so nothing reads "
                + "them. They are kept, and take effect if AiAudio is turned back on.");
        }

        if (tuning.SoundGateRmsThreshold is not null && !options.Ai.Sound.Enabled)
        {
            advisories.Add(
                $"Camera {camera.Id} sets SoundGateRmsThreshold but Serval:Ai:Sound:Enabled is "
                + "false, so no sounds will be tagged for any camera.");
        }

        NoiseFloorAdvisory(tuning.SpeechGateRmsThreshold, nameof(tuning.SpeechGateRmsThreshold));
        NoiseFloorAdvisory(tuning.SoundGateRmsThreshold, nameof(tuning.SoundGateRmsThreshold));

        void NoiseFloorAdvisory(double? value, string name)
        {
            if (value is { } rms && rms < 0.0005)
            {
                advisories.Add(
                    $"Camera {camera.Id} sets {name} to {rms}, at or below the noise floor of a "
                    + "16-bit capture. The gate will never close and the model will run on room "
                    + "tone continuously — which is the cost the gate exists to avoid.");
            }
        }
    }
}
