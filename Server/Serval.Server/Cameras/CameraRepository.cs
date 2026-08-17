using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Serval.Ai;
using Serval.Server.Configuration;
using Serval.Server.Ingest;
using Serval.Server.Storage;

namespace Serval.Server.Cameras;

/// <summary>
/// CRUD over the camera registry, plus the validation that keeps a malformed camera from
/// ever reaching the ingest pipeline (where a bad id would be a bad filesystem path).
/// </summary>
public sealed class CameraRepository
{
    private readonly IMongoCollection<Camera> _cameras;
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly FfmpegCapabilities _capabilities;

    public CameraRepository(
        MongoContext context, IOptionsMonitor<ServerOptions> options, FfmpegCapabilities capabilities)
    {
        _cameras = context.Cameras;
        _options = options;
        _capabilities = capabilities;
    }

    public async Task<List<Camera>> ListAsync(CancellationToken cancellationToken = default) =>
        await _cameras.Find(FilterDefinition<Camera>.Empty).ToListAsync(cancellationToken);

    public async Task<Camera?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        await _cameras.Find(c => c.Id == id).FirstOrDefaultAsync(cancellationToken);

    /// <summary>Inserts a new camera. Throws <see cref="CameraValidationException"/> on bad input.</summary>
    public async Task<Camera> CreateAsync(Camera camera, CancellationToken cancellationToken = default)
    {
        Validate(camera);
        ValidateTranscodes(camera, _options.CurrentValue.Ingest, _capabilities);

        if (await GetAsync(camera.Id, cancellationToken) is not null)
        {
            throw new CameraValidationException($"A camera with id '{camera.Id}' already exists.");
        }

        await _cameras.InsertOneAsync(camera, cancellationToken: cancellationToken);
        return camera;
    }

    /// <summary>Replaces an existing camera. Returns false if the id is unknown.</summary>
    public async Task<bool> UpdateAsync(Camera camera, CancellationToken cancellationToken = default)
    {
        Validate(camera);
        ValidateTranscodes(camera, _options.CurrentValue.Ingest, _capabilities);
        ReplaceOneResult result = await _cameras.ReplaceOneAsync(
            c => c.Id == camera.Id, camera, cancellationToken: cancellationToken);
        return result.MatchedCount > 0;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        DeleteResult result = await _cameras.DeleteOneAsync(c => c.Id == id, cancellationToken);
        return result.DeletedCount > 0;
    }

    /// <summary>
    /// Rules the rest of the system assumes: a safe id (it becomes a directory and a URL segment),
    /// streams the server can actually pull, and at most one of them recorded. Kept static so
    /// tests can exercise it without a database.
    ///
    /// The source URLs are scheme-checked here because the alternative is worse than a 400: an
    /// unreachable or unsupported source produces an ffmpeg that fails on every attempt, retried
    /// forever, visible only in the logs. Rejecting it at the API is the only place the mistake is
    /// still attached to the person who made it.
    /// </summary>
    public static void Validate(Camera camera)
    {
        if (string.IsNullOrWhiteSpace(camera.Id) || !IsSafeId(camera.Id))
        {
            throw new CameraValidationException(
                "Camera id must be non-empty and contain only letters, digits, '-', or '_'.");
        }

        if (string.IsNullOrWhiteSpace(camera.Name))
        {
            throw new CameraValidationException("Camera name is required.");
        }

        ValidateStreams(camera);

        if (camera.RetentionDays is { } days && days <= 0)
        {
            throw new CameraValidationException("RetentionDays must be positive when set.");
        }

        // Each tuning bag is bounded by its catalog entries, and an all-null override object is
        // collapsed to null — so the stored document, the advisories and the App agree on what
        // "this camera is tuned" means, whatever the client sent.
        camera.AudioTuning = TuningCatalog.Validate(camera.AudioTuning, TuningCatalog.Audio);
        camera.DetectionTuning = TuningCatalog.Validate(camera.DetectionTuning, TuningCatalog.Detection);
        camera.SoundTuning = TuningCatalog.Validate(camera.SoundTuning, TuningCatalog.Sound);
        camera.MotionTuning = TuningCatalog.Validate(camera.MotionTuning, TuningCatalog.Motion);

        if (camera.MotionTuning is { } motion
            && TuningCatalog.MotionCrossProblem(motion) is { } crossProblem)
        {
            throw new CameraValidationException(crossProblem);
        }

        ValidatePlaybackAudio(camera);

        if (!string.IsNullOrWhiteSpace(camera.OnvifUrl))
        {
            bool valid = Uri.TryCreate(camera.OnvifUrl, UriKind.Absolute, out Uri? onvif)
                && onvif.Scheme is "http" or "https";
            if (!valid)
            {
                throw new CameraValidationException("OnvifUrl must be an absolute http(s) URL when set.");
            }
        }
    }

    /// <summary>
    /// Bounds the two playback-audio settings.
    ///
    /// Rejected rather than clamped, like every other threshold here. The ceiling is 20 dB because
    /// that is where the volume control itself stops: it is also the ceiling of libwebrtc's own track
    /// volume, which the desktop live view goes through with no filter chain behind it, so it is the
    /// most any playback path can actually deliver. Accepting a larger number here would store a
    /// starting position the slider cannot represent. A negative value is refused rather than read as
    /// attenuation — listening more quietly is what the slider's own range below unity is for, and
    /// allowing it here would give two controls the same job with no way to tell which one silenced a
    /// camera.
    /// </summary>
    private static void ValidatePlaybackAudio(Camera camera)
    {
        if (camera.PlaybackGainDb is < 0 or > 20)
        {
            throw new CameraValidationException(
                "PlaybackGainDb must be between 0 and 20. 0 starts the camera at full volume with "
                + "nothing added and 20 is 10x, which is both the top of the volume control and the "
                + "most the live view can deliver. Use the volume control to listen more quietly "
                + "rather than a negative gain here.");
        }

        if (camera.PlaybackGateRmsThreshold is { } gate && gate is <= 0 or > 1)
        {
            throw new CameraValidationException(
                "PlaybackGateRmsThreshold must be greater than 0 and at most 1 when set. It is the "
                + "RMS of a 16 kHz window, so 1 is a full-scale square wave. Leave it unset to gate "
                + "nothing; 0 would hold the gate permanently open, which is the outcome reached by "
                + "what looks like turning a dial down.");
        }
    }

    private static void ValidateStreams(Camera camera)
    {
        if (camera.Streams is not { Count: > 0 })
        {
            throw new CameraValidationException("A camera needs at least one stream.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CameraStream stream in camera.Streams)
        {
            if (string.IsNullOrWhiteSpace(stream.Name) || !IsSafeId(stream.Name))
            {
                throw new CameraValidationException(
                    "Stream name must be non-empty and contain only letters, digits, '-', or '_'.");
            }

            if (!names.Add(stream.Name))
            {
                throw new CameraValidationException($"Duplicate stream name '{stream.Name}'.");
            }

            if (string.IsNullOrWhiteSpace(stream.Url))
            {
                throw new CameraValidationException($"Stream '{stream.Name}' needs a url.");
            }

            if (!Ingest.SourceArguments.IsSupported(stream.Url))
            {
                throw new CameraValidationException(
                    $"Stream '{stream.Name}' has an unsupported url scheme. Use rtsp(s), http(s), "
                    + "rtmp(s), srt, or a local file path.");
            }

            // A stream with no roles at all is not checked here and not rejected: it is stored,
            // never pulled, and keeps its address against a change of mind. The cost is that a
            // mistyped role list now validates cleanly, which is why CameraRegistryCheck names
            // every role-less stream in the log.
            //
            // So the transcode rule is about streams doing some *other* job. Only the recorded
            // stream is written to disk, and a transcode on the sub stream would be a core of CPU
            // nobody asked for. A role-less stream keeps one it already had — see
            // CameraStream.Transcode for why that is kept rather than cleared.
            if (stream.Roles is { Count: > 0 } && !stream.Roles.Contains(StreamRole.Record)
                && stream.Transcode is not null)
            {
                throw new CameraValidationException(
                    $"Stream '{stream.Name}' declares a transcode but does not carry the 'record' "
                    + "role. Transcoding applies only to the recorded stream — nothing else is "
                    + "written to disk. Remove it, or move the 'record' role to this stream.");
            }
        }

        int Count(StreamRole role) =>
            camera.Streams.Count(s => s.Roles.Contains(role));

        // Detect and live are assigned explicitly, exactly once. There is deliberately no fallback:
        // a detect or live role that quietly resolved to the recorded stream meant a 4K main stream
        // could be decoded once a second for thumbnails, or served over WebRTC, with nothing
        // anywhere saying so. Both are free to assign — any stream can carry them — so requiring
        // them costs nothing and keeps the dashboard wall and the focused view working for every
        // camera.
        foreach (StreamRole role in (StreamRole[])[StreamRole.Detect, StreamRole.Live])
        {
            if (Count(role) != 1)
            {
                throw new CameraValidationException(
                    $"Exactly one stream must have the '{role.ToString().ToLowerInvariant()}' role"
                    + $" — {RolePurpose(role)}. A camera with one stream declares "
                    + "[\"record\",\"detect\",\"live\"] on it; a camera with a sub stream typically "
                    + "gives 'detect' to the sub and keeps 'record' and 'live' on the main.");
            }
        }

        // Record is the one role a camera may go without: it is the only one that costs disk, and
        // "watch this and tell me, but keep nothing" is a real thing to want. Two holders is still
        // a mistake — resolving it by list order would silently pick one and look like it worked.
        if (Count(StreamRole.Record) > 1)
        {
            throw new CameraValidationException(
                "Two streams are both set to 'record' — it has to be one, or none. A camera with no "
                + "record stream is still watched and still viewable live; nothing is written to "
                + "disk, so it has no playback, no timeline and no clip export.");
        }

        // The one rule tying Camera.Recording to the streams. Off is legal with or without a record
        // stream — that is the whole point of the switch — but on with nothing to write is a camera
        // reporting that it records while keeping nothing, which is the state the flag exists to
        // make impossible to reach by accident.
        if (camera.Recording && Count(StreamRole.Record) == 0)
        {
            throw new CameraValidationException(
                "Recording is on but no stream is set to 'record'. Give a stream the 'record' role, "
                + "or turn Recording off — it cannot be on with nothing to write.");
        }
    }

    /// <summary>Why the role is required, for the two roles that are.</summary>
    private static string RolePurpose(StreamRole role) => role switch
    {
        StreamRole.Detect =>
            "it is the source of snapshots, motion detection, the dashboard wall and the AI",
        _ => "it is what the WebRTC focused live view serves",
    };

    /// <summary>
    /// The half of validation that depends on the host, kept separate from
    /// <see cref="Validate"/> so the hardware-independent rules stay a pure static with no
    /// ambient dependencies.
    ///
    /// Checking a transcode request against the encoders ffmpeg actually has is the difference
    /// between a 400 on the request that caused it and an ffmpeg that fails on every attempt,
    /// forever, with the reason visible only in the logs.
    /// </summary>
    public static void ValidateTranscodes(
        Camera camera, IngestOptions ingest, FfmpegCapabilities capabilities)
    {
        foreach (CameraStream stream in camera.Streams ?? [])
        {
            if (stream.Transcode is not { } transcode)
            {
                continue;
            }

            if (!EncoderSelector.IsSupported(transcode.Codec))
            {
                throw new CameraValidationException(
                    $"Stream '{stream.Name}' asks to transcode to '{transcode.Codec}', which Serval "
                    + "does not encode. Use one of: "
                    + $"{string.Join(", ", EncoderSelector.SupportedCodecs.Order(StringComparer.Ordinal))}. "
                    + $"(Serval will happily *record* {transcode.Codec} untouched if it is listed in "
                    + "Serval:Ingest:VideoPassthroughCodecs — leave 'transcode' unset for that.)");
            }

            if (transcode.Bitrate is { } bitrate && !BitratePattern.IsMatch(bitrate))
            {
                throw new CameraValidationException(
                    $"Stream '{stream.Name}' has bitrate '{bitrate}'. Use a number, optionally "
                    + "suffixed with k or M, e.g. '2M' or '2000k'.");
            }

            string encoder = EncoderSelector
                .Select(transcode.Codec, ingest.HwAccelDevice, ingest.Encoder,
                    transcode.Bitrate ?? ingest.Bitrate)
                .EncoderName;

            if (!capabilities.CanEncodeVideo(encoder))
            {
                // The message names the encoder *precedence chose*, not the codec asked for. That
                // is what makes HwAccelDevice silently winning over Encoder visible at the moment
                // it bites, rather than being a documented footnote nobody reads.
                throw new CameraValidationException(
                    $"Stream '{stream.Name}' asks to transcode to '{transcode.Codec}'. "
                    + WhyThatEncoder(ingest)
                    + $"which means the '{encoder}' encoder — and this host's ffmpeg does not have "
                    + "it. Pick a codec this host can encode, or change the hardware settings.");
            }
        }
    }

    private static string WhyThatEncoder(IngestOptions ingest)
    {
        if (!string.IsNullOrWhiteSpace(ingest.HwAccelDevice))
        {
            return $"Serval:Ingest:HwAccelDevice is set to '{ingest.HwAccelDevice}' (which takes "
                + "precedence over Serval:Ingest:Encoder), ";
        }

        return !string.IsNullOrWhiteSpace(ingest.Encoder)
            ? $"Serval:Ingest:Encoder is set to '{ingest.Encoder}', "
            : "No hardware encoder is configured, so this is the software encoder ";
    }

    /// <summary>ffmpeg <c>-b:v</c> syntax: a number, optionally suffixed with k or M.</summary>
    private static readonly Regex BitratePattern =
        new(@"^\d+(\.\d+)?[kKmM]?$", RegexOptions.Compiled);

    /// <summary>
    /// Filesystem- and URL-safe: no path separators, dots, or spaces to escape into. Used for both
    /// camera ids and stream names.
    /// </summary>
    public static bool IsSafeId(string id) =>
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>A camera failed validation.</summary>
public sealed class CameraValidationException(string message) : ValidationException(message);
