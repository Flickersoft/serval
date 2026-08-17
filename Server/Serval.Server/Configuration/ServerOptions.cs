namespace Serval.Server.Configuration;

/// <summary>
/// Everything the server needs that isn't a per-camera setting. Bound from the "Serval"
/// section of appsettings and overridable by environment variable (Serval__MediaRoot=...),
/// the same convention the CameraModule uses.
///
/// Settings editable at runtime carry their operator-facing documentation in
/// <see cref="SettingsCatalog"/> — the text the App shows — and a one-liner here. Settings that
/// exist only in configuration files keep their full story here, because this is their one home.
/// </summary>
public sealed class ServerOptions
{
    public const string SectionName = "Serval";

    public MongoOptions Mongo { get; set; } = new();
    public MediaOptions Media { get; set; } = new();
    public IngestOptions Ingest { get; set; } = new();
    public WebRtcOptions WebRtc { get; set; } = new();
    public PtzOptions Ptz { get; set; } = new();
    public OpenApiOptions OpenApi { get; set; } = new();
    public CorsOptions Cors { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();
    public VitalsOptions Vitals { get; set; } = new();
    public PushOptions Push { get; set; } = new();

    /// <summary>
    /// The shared detection library's settings — the same option types the CameraModule binds, so
    /// an edge camera and a server-side camera are configured the same way and produce comparable
    /// results. Which cameras it actually runs for is per-camera (<c>AiVision</c>/<c>AiAudio</c>).
    /// </summary>
    public Serval.Ai.AiOptions Ai { get; set; } = new();

    /// <summary>Server-side detection settings that have no edge equivalent.</summary>
    public ServerAiOptions ServerAi { get; set; } = new();

    /// <summary>
    /// Shared secret the CameraModule presents (X-Api-Key) when POSTing telemetry. Empty is the
    /// default and it closes the route: telemetry ingest is off until a key is set, and the server
    /// says so once at boot. Unset means no module has been granted access, not that every caller
    /// has it.
    ///
    /// Deliberately separate from <see cref="Auth"/>: this authenticates an edge device
    /// delivering telemetry, not a person. Unlike <see cref="AuthOptions.SigningKey"/> it does
    /// not block startup — a Server with no modules is an ordinary deployment.
    /// </summary>
    public string ApiKey { get; set; } = "";
}

/// <summary>
/// Settings for running detection inside the Server, which the edge module has no equivalent for:
/// it watches one camera, the Server watches many with one model between them.
/// </summary>
public sealed class ServerAiOptions
{
    /// <summary>Master switch for loading the detection models into the Server process.</summary>
    public bool Enabled { get; set; }

    /// <summary>How often the coordinator reconciles the camera registry against running sessions.</summary>
    public double ReconcileSeconds { get; set; } = 5.0;

    /// <summary>
    /// Where per-camera conversation audio is buffered while a conversation is open. Under the
    /// media root by default so it shares the volume already sized for video.
    /// </summary>
    public string ConversationAudioRoot { get; set; } = "conversations";
}

public sealed class MongoOptions
{
    public string ConnectionString { get; set; } = "mongodb://localhost:27017";
    public string Database { get; set; } = "serval";
}

/// <summary>
/// Delivering the alert queue to a browser that is not looking at it. Nothing here decides what is
/// an <em>alert</em> — the queue is settled before any of this runs — though
/// <see cref="CooldownSeconds"/> does decide what is <em>sent</em>. The VAPID signing key is
/// deliberately absent: it is generated once and stored (see <see cref="Push.VapidKeyStore"/>).
/// </summary>
public sealed class PushOptions
{
    /// <summary>
    /// The longest cooldown anybody may configure, here or on their own notification rule.
    ///
    /// One constant for two callers — the catalogue's bound and
    /// <c>UserPreferencesRepository.ValidateRules</c> — because two literals drift, and drift here
    /// means the settings page and the preferences endpoint disagree about what is legal.
    /// </summary>
    public const int MaxCooldownSeconds = 3600;

    /// <summary>Master switch. On by default; no subscriptions means no work.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>RFC 8292 <c>sub</c> claim — how a push service contacts this deployment's operator.</summary>
    public string ContactUri { get; set; } = "mailto:serval@localhost";

    /// <summary>How long a push service should hold a message for an offline device.</summary>
    public int TtlSeconds { get; set; } = 600;

    /// <summary>Consecutive ambiguous delivery failures before a subscription is retired.</summary>
    public int MaxFailures { get; set; } = 10;

    /// <summary>
    /// How long a camera is left alone after interrupting somebody about one thing, before it may
    /// interrupt them about that same thing again. What a person inherits until they set their own
    /// on <c>CameraNotificationRule.CooldownSeconds</c>; zero sends every alert.
    ///
    /// <para>Two minutes to match <c>Detection:NoveltySeconds</c>. The two windows are answering the
    /// same question from opposite ends — how long ago does something have to have left before
    /// coming back is news — and a notification window shorter than the arrival window would push
    /// twice for one walk past.</para>
    /// </summary>
    public int CooldownSeconds { get; set; } = 120;
}

/// <summary>
/// What the server measures about itself — processor, memory, GPU and the media volume — and the
/// thresholds at which it says something is wrong.
/// </summary>
public sealed class VitalsOptions
{
    /// <summary>Master switch.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often processor, memory and GPU are re-read; also the CPU delta window.</summary>
    public double SampleSeconds { get; set; } = 5.0;

    /// <summary>How often each camera's media directory is walked for its size. Zero disables the walk.</summary>
    public double DiskScanMinutes { get; set; } = 15.0;

    /// <summary>How much sample history to keep for the App's sparklines. Zero keeps only the current sample.</summary>
    public double HistoryMinutes { get; set; } = 60.0;

    /// <summary>Free space below this fraction of the volume raises a warning.</summary>
    public double DiskWarnPercentFree { get; set; } = 10.0;

    /// <summary>And below this, a critical one.</summary>
    public double DiskCriticalPercentFree { get; set; } = 5.0;

    /// <summary>Container memory this close to its cgroup limit raises a warning. Suppressed with no limit set.</summary>
    public double MemoryWarnPercent { get; set; } = 90.0;

    // Deliberately no CPU or GPU threshold. A pinned GPU during a VAAPI transcode is the encoder
    // doing its job, and neither compose file sets a CPU limit, so there is no ceiling to be near
    // — high CPU is the expected steady state with one ffmpeg per camera plus a CPU-resident
    // vision model. A banner that fires for "working as configured" is a banner people stop
    // reading, and the disk one needs to be believed.
}

public sealed class MediaOptions
{
    /// <summary>
    /// Root under which per-camera HLS segments, playlists, and snapshots live. One
    /// subdirectory per camera id. Video is far too large for Mongo, so only its index goes
    /// there; the bytes stay on the filesystem.
    /// </summary>
    public string Root { get; set; } = "media";

    /// <summary>
    /// Where saved clips live, as a sibling of the per-camera directories under
    /// <see cref="Root"/> rather than inside one of them.
    ///
    /// The placement is the mechanism, not a preference. The retention sweep only ever deletes
    /// inside <c>Root/{cameraId}</c>, and only filenames its index handed back — so a clip stored
    /// anywhere else is exempt from rolloff with no rule to keep in step. Nesting these under a
    /// camera would put the one kind of footage that must never be pruned inside the one directory
    /// that is pruned.
    /// </summary>
    public string ClipsRoot { get; set; } = "clips";

    /// <summary>Default recording retention when a camera doesn't specify its own.</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>How often the retention sweep runs.</summary>
    public double RetentionSweepMinutes { get; set; } = 30.0;

    /// <summary>Longest range a single saved clip may cover. The App renders its caption from this.</summary>
    public int ClipMaxMinutes { get; set; } = 30;

    /// <summary>
    /// Where alert preview clips and their posters live, as a sibling of the per-camera
    /// directories for the reason <see cref="ClipsRoot"/> is one. Unlike clips these have their
    /// own sweep on <see cref="AlertRetentionDays"/> — the placement buys them a lifetime of
    /// their own, not an unlimited one.
    /// </summary>
    public string AlertsRoot { get; set; } = "alerts";

    /// <summary>How long an alert and its preview clip are kept, independent of recording retention and dismissal.</summary>
    public int AlertRetentionDays { get; set; } = 14;

    /// <summary>Seconds of a preview clip that come before the alert fired.</summary>
    public double AlertPreRollSeconds { get; set; } = 5.0;

    /// <summary>Seconds after. Also the delay before the clip can be cut, since the footage has to exist.</summary>
    public double AlertPostRollSeconds { get; set; } = 15.0;
}

public sealed class IngestOptions
{
    /// <summary>ffmpeg binary. A bare name is resolved on PATH; override with a full path.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>ffprobe binary, used to read a source's codecs before recording it.</summary>
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>
    /// Source video codecs written into the recording untouched. Anything else is a hard failure
    /// that names the codec, never a silent re-encode — re-encoding is opted into per stream
    /// (<c>CameraStream.Transcode</c>). Null rather than an initialised list on purpose — see
    /// <see cref="DefaultVideoPassthroughCodecs"/>.
    /// </summary>
    public List<string>? VideoPassthroughCodecs { get; set; }

    /// <summary>
    /// Used when nothing configured <see cref="VideoPassthroughCodecs"/>. It lives here rather than
    /// as a property initialiser because the configuration binder <em>adds to</em> a list that is
    /// already populated instead of replacing it: an initialiser plus the same key in
    /// appsettings.json produced every codec twice, and a deployment that narrowed the list got the
    /// union of its list and the default — i.e. narrowing silently did nothing. Binding into a null
    /// property gives exactly what was configured.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultVideoPassthroughCodecs =
        ["h264", "hevc", "av1", "vp9"];

    /// <summary>What was configured, or the default when nothing was. Empty stays empty.</summary>
    public IReadOnlyList<string> ResolvedVideoPassthroughCodecs =>
        VideoPassthroughCodecs ?? DefaultVideoPassthroughCodecs;

    /// <summary>
    /// Source audio codecs fMP4 can carry untouched. Anything else is transcoded to AAC 64 kbps
    /// mono — most IP cameras emit G.711, which has no fMP4 sample entry at all, so this is a
    /// constraint of the recording container rather than a codec policy.
    /// </summary>
    public List<string>? AudioPassthroughCodecs { get; set; }

    /// <summary>
    /// Used when nothing configured <see cref="AudioPassthroughCodecs"/>. Null-by-default for the
    /// same binder reason as <see cref="DefaultVideoPassthroughCodecs"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultAudioPassthroughCodecs =
        ["aac", "opus", "mp3"];

    /// <summary>What was configured, or the default when nothing was. Empty stays empty.</summary>
    public IReadOnlyList<string> ResolvedAudioPassthroughCodecs =>
        AudioPassthroughCodecs ?? DefaultAudioPassthroughCodecs;

    /// <summary>
    /// VAAPI render node (e.g. <c>/dev/dri/renderD128</c>). When set, a stream that asks to be
    /// transcoded is encoded on the GPU via VAAPI; empty means software encoding. Only ever
    /// applies to streams that declare a transcode — a camera recorded by copy never reaches an
    /// encoder at all.
    /// </summary>
    public string? HwAccelDevice { get; set; }

    /// <summary>
    /// Explicit ffmpeg encoder name (e.g. <c>h264_nvenc</c>, <c>vp9_qsv</c>) for hardware VAAPI
    /// can't reach. Ignored when <see cref="HwAccelDevice"/> is set.
    /// </summary>
    public string? Encoder { get; set; }

    /// <summary>Target video bitrate for a transcoding stream that leaves its own unset (ffmpeg <c>-b:v</c> syntax).</summary>
    public string Bitrate { get; set; } = "2M";

    /// <summary>Upper bound on how much picture a snapshot carries, in megapixels. Zero disables the cap.</summary>
    public double SnapshotMaxMegapixels { get; set; } = 0.25;

    /// <summary>Target seconds per segment — the granularity at which playback seeks and retention prunes.</summary>
    public double SegmentSeconds { get; set; } = 4.0;

    /// <summary>Snapshot refresh rate for the dashboard wall.</summary>
    public double SnapshotFps { get; set; } = 1.0;

    /// <summary>Decoder threads for the process that produces snapshots and detect frames — a latency knob, not throughput.</summary>
    public int DecodeThreads { get; set; } = 2;

    /// <summary>Rate at which raw frames are produced for object detection. Two is the floor for tracking.</summary>
    public double DetectFps { get; set; } = 2.0;

    /// <summary>Width of the raw frames handed to detection; height follows the source aspect.</summary>
    public int DetectFrameWidth { get; set; } = 1920;

    /// <summary>
    /// Where raw detect frames are staged. <b>Must be tmpfs</b> — a busy host writes tens of MB a
    /// second through here, and a disk-backed path would be pure write amplification on the same
    /// device holding the recordings.
    ///
    /// Absolute, because <c>FfmpegRunner</c> sets each session's working directory to the camera's
    /// media directory and a relative path would land there. Outside the media root for the same
    /// reason, and because <c>DiskUsageScanner</c> enumerates that directory.
    /// </summary>
    public string DetectFrameDir { get; set; } = "/dev/shm/serval/detect";

    /// <summary>
    /// Frames allowed to accumulate for one camera before the oldest are dropped — the bound that
    /// makes staging on tmpfs safe, since ffmpeg writes files and never blocks on a slow consumer.
    /// </summary>
    public int DetectFrameBacklog { get; set; } = 8;

    /// <summary>
    /// Seconds of the detect stream kept on disk as a rolling ring, so an alert's preview clip can
    /// start before it fired. Zero disables the ring. Must comfortably exceed
    /// <c>AlertPreRollSeconds + AlertPostRollSeconds</c>.
    /// </summary>
    public double PreviewBufferSeconds { get; set; } = 90.0;

    /// <summary>
    /// A dead RTSP source is retried, never abandoned. Backoff starts here and doubles up to
    /// <see cref="MaxReconnectSeconds"/>.
    /// </summary>
    public double ReconnectSeconds { get; set; } = 2.0;

    public double MaxReconnectSeconds { get; set; } = 60.0;

    /// <summary>
    /// How long an ingest session may produce nothing before ffmpeg is killed and the reconnect
    /// takes over — the defence against a half-open TCP socket, which leaves ffmpeg alive and
    /// blocked forever. Must comfortably clear the gap between outputs (for recording, the
    /// camera's GOP under <c>-c:v copy</c>). Zero disables the watchdog.
    /// </summary>
    public double StallTimeoutSeconds { get; set; } = 60.0;
}

/// <summary>
/// Low-latency WebRTC live view, served by a go2rtc sidecar alongside the recording stream: HLS
/// stays the recorder and archive playback, WebRTC serves only the focused single-camera view
/// where sub-second latency matters (PTZ, talk-back). The server never carries WebRTC media — it
/// mirrors the camera registry into go2rtc and proxies signaling.
/// </summary>
public sealed class WebRtcOptions
{
    /// <summary>When false, the sync worker and signaling proxy stay dormant; recording is unaffected.</summary>
    public bool Enabled { get; set; }

    /// <summary>Base URL of the go2rtc sidecar's API, e.g. <c>http://go2rtc:1984</c>. The browser never uses this URL.</summary>
    public string Go2RtcUrl { get; set; } = "http://go2rtc:1984";
}

/// <summary>The generated OpenAPI document and the Scalar UI over it.</summary>
public sealed class OpenApiOptions
{
    /// <summary>
    /// Serves <c>/openapi/v1.json</c> and <c>/scalar/v1</c>. On by default, which is right for a
    /// trusted LAN and wrong anywhere else.
    ///
    /// The routes it describes are all behind a login — Scalar's own Authorize button is how it
    /// gets one — so what is exposed here is the description rather than the data: every route,
    /// its parameters, and the <c>Camera</c> schema down to the name of the field holding a
    /// camera's password. That is reconnaissance made free rather than a way in. Turn it off
    /// before exposing the server.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Who may call the API from a browser.
///
/// This exists for the App's web build, and it is not optional there: WebSockets are exempt from
/// the same-origin policy while REST is not, so with no policy at all a browser client connects
/// <c>/api/dashboard</c> and <c>/api/events</c> happily and then fails every REST read — a
/// half-working state that looks like a bug in the App rather than a missing header on the Server.
/// Native builds (Linux desktop, mobile) are unaffected either way.
/// </summary>
public sealed class CorsOptions
{
    /// <summary>
    /// Origins allowed to call the API, e.g. <c>["http://localhost:8080"]</c>. **Empty (the
    /// default) allows any origin**, which is the LAN default and is announced at boot.
    ///
    /// What that does and does not give away is worth being exact about. Credentials are never
    /// allowed — nothing here reads a cookie — and this API authenticates with a bearer header,
    /// which a hostile page cannot read out of the App's origin. So an arbitrary origin cannot
    /// ride a signed-in session. What it can do is reach the routes that take no session at all,
    /// from any page someone on this network happens to visit: the liveness probe, the OpenAPI
    /// document above, and <c>/api/auth/login</c> — that last one making a viewer's own browser
    /// into a place to try passwords from.
    ///
    /// List the App's real origin before exposing the server, alongside turning
    /// <see cref="OpenApiOptions.Enabled"/> off.
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = [];
}

/// <summary>
/// User login: password verification, JWT access tokens, and the DB-backed refresh tokens that
/// renew them. Unlike <see cref="ServerOptions.ApiKey"/>, there is no safe empty value here —
/// once auth guards nearly every route, an unset <see cref="SigningKey"/> means the server cannot
/// issue or verify a token at all, so the server refuses to start rather than run open or broken.
/// </summary>
public sealed class AuthOptions
{
    /// <summary>
    /// HMAC-SHA256 signing key for access tokens, refresh-token-adjacent bookkeeping, WebSocket
    /// connect tickets, and stream tokens — one key for all four, since they are all short-lived
    /// credentials issued by this server to itself. Generate with e.g. <c>openssl rand -base64
    /// 32</c>. Required; the server fails to start if this is empty or under 32 characters.
    /// </summary>
    public string SigningKey { get; set; } = "";

    /// <summary>Access token lifetime. Short by design — the refresh token is what a stolen credential's blast radius depends on.</summary>
    public int AccessTokenMinutes { get; set; } = 10;

    /// <summary>How long an unused refresh token stays valid before the user must log in again.</summary>
    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>
    /// Creates this Admin account on startup, but only when the Users collection is still empty —
    /// there being no self-registration and no admin UI otherwise, this is the only way to get
    /// the first account in. Ignored (with a startup warning) once any user exists, so a stale
    /// value left in a compose file cannot silently reset a password on every restart.
    /// </summary>
    public string? BootstrapAdminUsername { get; set; }

    /// <summary>Password for <see cref="BootstrapAdminUsername"/>. Same first-run-only rule.</summary>
    public string? BootstrapAdminPassword { get; set; }
}

/// <summary>
/// Pan/tilt/zoom control over ONVIF. PTZ is per-camera (a camera opts in with its ONVIF fields);
/// these are the server-wide knobs for how the SOAP calls behave.
/// </summary>
public sealed class PtzOptions
{
    /// <summary>Auto-stop timeout sent with every continuous move (ONVIF <c>Timeout</c>).</summary>
    public double MoveTimeoutSeconds { get; set; } = 1.0;

    /// <summary>How long to wait on an ONVIF SOAP call before giving up on an unresponsive camera.</summary>
    public double RequestTimeoutSeconds { get; set; } = 5.0;

    /// <summary>How long a probed capability set stays good before a re-probe.</summary>
    public double CapabilityCacheMinutes { get; set; } = 10.0;
}
