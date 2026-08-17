using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace Serval.Server.Cameras;

/// <summary>
/// A camera the server ingests from. Its video comes from one or more <see cref="Streams"/>, each
/// tagged with what it is for — so a camera that offers both a high-quality main stream and a
/// cheap sub stream can be recorded at full quality while detection runs on the small one. The id
/// is also the media subdirectory name and the path segment the CameraModule POSTs telemetry to,
/// so it must be filesystem- and URL-safe.
/// </summary>
[BsonIgnoreExtraElements] // tolerate fields a newer schema may add, rather than crash reads
public sealed class Camera
{
    [BsonId]
    public required string Id { get; set; }

    public required string Name { get; set; }

    public string? Location { get; set; }

    /// <summary>
    /// The camera's sources. Exactly one stream must carry <see cref="StreamRole.Detect"/> and one
    /// <see cref="StreamRole.Live"/>; at most one carries <see cref="StreamRole.Record"/>, and a
    /// camera with none keeps nothing. A stream may carry no roles at all, in which case it is
    /// stored and never pulled. A camera with a single stream therefore declares one entry with
    /// <c>["record","detect","live"]</c>; a camera offering a sub stream typically gives it
    /// <c>["detect"]</c> and keeps <c>["record","live"]</c> on the main.
    /// </summary>
    public required List<CameraStream> Streams { get; set; }

    /// <summary>Disabled cameras are registered but not ingested.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Write this camera's footage to disk. True by default — a camera with a record stream records.
    ///
    /// Separate from <see cref="StreamRole.Record"/> because the two answer different questions.
    /// The role says *which* stream would be written, and survives being switched off, so turning
    /// recording back on does not mean deciding that again — which is what makes this a temporary
    /// switch rather than a reconfiguration. Validation rejects true with no record stream, so it
    /// is never on and inert.
    ///
    /// Footage already on disk is untouched either way: it stays playable and ages out under
    /// <see cref="RetentionDays"/>.
    /// </summary>
    public bool Recording { get; set; } = true;

    /// <summary>Per-camera retention override; null falls back to the server default.</summary>
    public int? RetentionDays { get; set; }

    /// <summary>
    /// ONVIF device service URL (e.g. <c>http://192.168.1.50:80/onvif/device_service</c>). Set it,
    /// with <see cref="OnvifUsername"/>/<see cref="OnvifPassword"/>, to enable PTZ control. The
    /// server discovers the camera's PTZ service and media profile from here. Null means no PTZ.
    /// </summary>
    public string? OnvifUrl { get; set; }

    /// <summary>ONVIF account used for PTZ SOAP calls (WS-Security UsernameToken).</summary>
    public string? OnvifUsername { get; set; }

    /// <summary>Password for <see cref="OnvifUsername"/>. Sent only as a WS-Security digest, never in clear.</summary>
    public string? OnvifPassword { get; set; }

    /// <summary>
    /// Media profile token to drive PTZ against. Optional: when null the server uses the camera's
    /// first PTZ-capable profile, discovered via ONVIF. Set it to pin a specific profile.
    /// </summary>
    public string? OnvifProfileToken { get; set; }

    /// <summary>
    /// Opt in to two-way audio (talk-back) for cameras that support an RTSP/ONVIF backchannel.
    /// When true, go2rtc keeps its backchannel enabled so a viewer's microphone reaches the camera
    /// over the same WebRTC session as the live view. When false the backchannel is disabled at the
    /// source (<c>#backchannel=0</c>) — go2rtc probes it by default, and that probe breaks some
    /// cameras (e.g. certain doorbells), so talk-back is off unless a camera is known to handle it.
    /// </summary>
    public bool TwoWayAudio { get; set; }

    /// <summary>
    /// Record the camera's audio track alongside its video, in the same segment files.
    ///
    /// Off by default and per-camera, not global: recording sound is treated differently from
    /// recording pictures in many jurisdictions, so it is an explicit decision per camera rather
    /// than something that arrives with an upgrade. A camera whose source has no audio track
    /// records video regardless.
    /// </summary>
    public bool RecordAudio { get; set; }

    /// <summary>
    /// Where this camera's volume slider starts, as a lift in dB above its recorded level. 0 starts it
    /// at full volume with nothing added.
    ///
    /// A starting point, not the applied gain. Nothing on the Server reads it, and the App reads it
    /// only until a client has a position of its own: the slider is kept per camera on the machine
    /// doing the listening, because how loud you want to listen is a property of what you are sitting
    /// at and syncing it would let a phone dictate a desktop's volume. This is what stops a camera
    /// somebody has already calibrated from arriving silent on every new browser.
    ///
    /// It lives on the camera because how far a camera needs lifting is a property of its microphone
    /// rather than of who is listening: measured across this deployment, the quietest camera's audio
    /// sits 15-20 dB below the others at every percentile, so one number shared by all of them is
    /// wrong for at least one.
    ///
    /// Applied at playback rather than baked in at record time so it works on footage already on
    /// disk, costs nothing, and stays reversible. Recording it in would also be irreversible for the
    /// rare loud transient: every camera here reaches within a few dB of full scale occasionally, and
    /// a permanent gain would clip those forever.
    /// </summary>
    public double PlaybackGainDb { get; set; }

    /// <summary>
    /// The level below which this camera's audio is treated as silence during playback; null gates
    /// nothing.
    ///
    /// The companion to <see cref="PlaybackGainDb"/> and the reason a large gain is usable at all.
    /// These streams spend most of their time on the noise floor, so a gain big enough to make the
    /// quiet content audible also amplifies the codec's own quantisation noise into audible hiss. A
    /// gate ahead of the gain keeps silence silent. There is room for it: the floor and the content
    /// are separated by 23 dB or more on every camera here.
    ///
    /// Note a compressor would be the wrong instrument, not a gentler one — an AGC applies its
    /// greatest gain exactly when there is nothing but noise to find.
    ///
    /// Expressed as the RMS of a 16 kHz window, the same unit as
    /// <see cref="CameraAudioTuning.SpeechGateRmsThreshold"/>, so it reads against the same live
    /// meter. It is otherwise unrelated to those: this one feeds the speakers, they feed the detector.
    /// </summary>
    [BsonIgnoreIfNull]
    public double? PlaybackGateRmsThreshold { get; set; }

    /// <summary>
    /// Run server-side scene description for this camera, gated on motion.
    ///
    /// This is the point of the shared detection library: a camera with no edge module still gets
    /// AI, run by the Server on its behalf. Off by default because the vision model costs seconds
    /// of CPU per description and one model instance is shared across every camera.
    /// </summary>
    public bool AiVision { get; set; }

    /// <summary>
    /// Run server-side transcription, speaker labelling and conversation reprocessing for this
    /// camera. Requires the camera to actually carry an audio track; the Server pulls it in a
    /// second, audio-only RTSP session so a fault there cannot disturb recording.
    /// </summary>
    public bool AiAudio { get; set; }

    /// <summary>
    /// Per-camera overrides for the audio detection thresholds; null falls back to the server
    /// defaults under <c>Serval:Ai</c>. See <see cref="CameraAudioTuning"/> for why the right
    /// value is a property of the room rather than something the server can pick once.
    /// </summary>
    [BsonIgnoreIfNull]
    public CameraAudioTuning? AudioTuning { get; set; }

    /// <summary>
    /// Per-camera overrides for object detection; null falls back to the server defaults under
    /// <c>Serval:Ai:Detection</c>. See <see cref="CameraDetectionTuning"/> — masks especially have
    /// no sensible global value, because where a property line runs is a fact about one camera.
    /// </summary>
    [BsonIgnoreIfNull]
    public CameraDetectionTuning? DetectionTuning { get; set; }

    /// <summary>
    /// Per-camera overrides for which sounds this camera reports and which of them are alarming;
    /// null falls back to <c>Serval:Ai:Sound</c>. See <see cref="CameraSoundTuning"/> — a driveway
    /// and a nursery want different alert labels, and one list serves both badly.
    /// </summary>
    [BsonIgnoreIfNull]
    public CameraSoundTuning? SoundTuning { get; set; }

    /// <summary>
    /// Per-camera overrides for the movement gate; null falls back to <c>Serval:Ai:Motion</c>. Only
    /// reached on a server with object detection switched off, which is where it is the only thing
    /// deciding whether the description model runs — see <see cref="CameraMotionTuning"/>.
    /// </summary>
    [BsonIgnoreIfNull]
    public CameraMotionTuning? MotionTuning { get; set; }

    /// <summary>
    /// Whether this camera is *set up* for PTZ — an ONVIF endpoint is configured, so there is
    /// something to ask.
    ///
    /// Deliberately not named for capability: it says nothing about which axes the camera has, or
    /// whether it has any at all. A fixed-lens pan/tilt dome and a motorised zoom both answer true
    /// here. For what the camera can actually do, probe
    /// <c>GET /api/cameras/{id}/ptz/capabilities</c>, which asks the camera itself.
    /// </summary>
    [BsonIgnore]
    public bool PtzConfigured => !string.IsNullOrWhiteSpace(OnvifUrl);

    /// <summary>
    /// A copy with the camera's own credentials removed, for callers who are allowed to see that a
    /// camera exists but not to log in to it.
    ///
    /// There are two of them, and the second is the one that gets forgotten: the ONVIF password
    /// sits in its own field, and the stream URLs carry another in their userinfo
    /// (<c>rtsp://admin:hunter2@…</c>). Both are usually the camera's *administrative* credentials
    /// and are usually reused across every camera in the house, so handing them to an account that
    /// exists only to watch the wall gives that account the cameras themselves — outside Serval,
    /// where none of these roles apply.
    ///
    /// A copy rather than an edit in place: the instances this is called on come straight from the
    /// registry, and a redaction that leaked back into a write would erase the real credential.
    /// </summary>
    public Camera WithoutSecrets()
    {
        var copy = (Camera)MemberwiseClone();
        copy.OnvifPassword = null;
        copy.Streams = [.. Streams.Select(stream => stream.WithoutSecrets())];
        return copy;
    }

    /// <summary>
    /// The stream written to disk and played back, or null for a camera that keeps nothing.
    /// </summary>
    [BsonIgnore]
    [JsonIgnore]
    public CameraStream? RecordStream => Pick(StreamRole.Record);

    /// <summary>
    /// Where snapshots, motion detection, the dashboard wall and the AI's audio come from.
    /// </summary>
    [BsonIgnore]
    [JsonIgnore]
    public CameraStream? DetectStream => Pick(StreamRole.Detect);

    /// <summary>What go2rtc serves over WebRTC for the focused single-camera view.</summary>
    [BsonIgnore]
    [JsonIgnore]
    public CameraStream? LiveStream => Pick(StreamRole.Live);

    /// <summary>
    /// The stream that declared this role, or null.
    ///
    /// For <see cref="StreamRole.Record"/> null is an ordinary camera that keeps nothing. For the
    /// other two it is reachable only for a camera the API never accepted — a document written
    /// straight into Mongo. There is deliberately no fallback to the recorded stream: that would be
    /// invisible over the API, so a camera could be decoding its 4K main stream once a second for
    /// thumbnails, or serving it over WebRTC, with nothing anywhere saying so. Every consumer
    /// null-guards, and <see cref="CameraRegistryCheck"/> is what makes such a camera visible
    /// instead of silent.
    /// </summary>
    private CameraStream? Pick(StreamRole role) =>
        Streams.FirstOrDefault(s => s.Roles.Contains(role));
}
