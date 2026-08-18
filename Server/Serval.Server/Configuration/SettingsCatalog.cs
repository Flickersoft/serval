using System.Globalization;
using Serval.Ai;

namespace Serval.Server.Configuration;

/// <summary>What kind of value a setting holds, which is what decides how the App draws it.</summary>
public enum SettingKind
{
    Bool,
    Int,
    Number,
    Text,
    /// <summary>A list of strings, stored as indexed configuration keys.</summary>
    TextList,
    /// <summary>Text constrained to <see cref="SettingDescriptor.Choices"/>.</summary>
    Choice,
}

/// <summary>
/// One editable setting: where it lives in configuration, what it accepts, and what to tell the
/// person changing it.
/// </summary>
/// <param name="Key">Configuration path in colon form, e.g. <c>Serval:Media:RetentionDays</c>.</param>
/// <param name="Group">Which section of the settings page this belongs under.</param>
/// <param name="Label">Short name, as the App labels the field.</param>
/// <param name="Help">
/// What this actually does and what changing it costs, in one or two sentences. This is the text
/// the App shows beside or behind the field, and it is the only explanation most people will read —
/// the XML docs on the option classes are the long version, not a substitute for this one.
/// </param>
/// <param name="RestartRequired">
/// True when the value is captured while the process is being composed and cannot be swapped
/// underneath what has already been built — a model that is loaded once, a switch that decides
/// whether a service is registered at all. The App marks these and says a restart is needed.
/// </param>
/// <param name="Min">Inclusive lower bound for <see cref="SettingKind.Int"/>/<see cref="SettingKind.Number"/>.</param>
/// <param name="Max">Inclusive upper bound.</param>
/// <param name="Unit">Unit suffix for display, e.g. "days", "seconds". Null for a bare number.</param>
/// <param name="Choices">Allowed values for <see cref="SettingKind.Choice"/>, or suggestions for a list.</param>
/// <param name="Fallback">
/// What a <see cref="SettingKind.TextList"/> resolves to while it is left empty.
///
/// <para>Several of the lists here are empty by default and mean "use the built-in set" rather than
/// "match nothing" — <c>Detection:Classes</c> falls back to eight of COCO's eighty, and
/// <c>Sound:AlertLabels</c> to glass and alarms. Without this the App would draw an empty box for a
/// setting that is quietly doing something, which is the one thing a settings page must never do.
/// The App shows it as placeholder text, not as a value.</para>
/// </param>
/// <param name="Advanced">
/// True for a setting nobody running a house needs to touch: a model file, a thread count, a
/// filter's process noise, a tile's overlap fraction.
///
/// <para><b>This is about audience, not risk.</b> Plenty of everyday settings will empty a disk or
/// silence every alert; that is not what this marks. It marks the ones whose <em>label cannot be
/// made intelligible</em> to someone who owns the cameras and has never read the code — where the
/// honest answer to "what should I set this to" is "leave it alone unless something told you
/// otherwise".</para>
///
/// <para><b>Drawn below a divider, never hidden.</b> The App sorts these to the end of their group
/// under an <c>ADVANCED</c> rule. A disclosure was considered and rejected: somebody following a
/// support thread needs the field on screen, and a click between them and it buys nothing that the
/// divider does not already say.</para>
///
/// <para>A group where <em>every</em> setting is advanced is a group the operator never opens, and
/// is a sign the settings belong inside a neighbouring group instead. <c>Object tracking</c> is the
/// one deliberate exception, pinned by <c>SettingsCatalogTests</c>.</para>
/// </param>
/// <param name="AppliesWhen">
/// Another setting whose value decides whether this one is read at all, or null when it always is.
///
/// <para><b>The App dims rather than hides.</b> A setting the running configuration ignores looks
/// exactly like one that is working, which is the failure this exists to end — but a hidden field
/// cannot tell anyone its value is being ignored, and a compose file setting an inert value is
/// precisely the case worth surfacing. So the card stays where it is, greyed, with the reason where
/// the help text was.</para>
///
/// <para><b>The App resolves it against the value being edited, not the running one.</b> The settings
/// that control others are restart-gated, so judging from the server's live configuration would dim
/// the fields belonging to the newly chosen value — exactly the ones that then need setting before
/// the restart. That is why this ships as data rather than being evaluated here.</para>
/// </param>
public sealed record SettingDescriptor(
    string Key,
    string Group,
    string Label,
    string Help,
    SettingKind Kind,
    bool RestartRequired = false,
    double? Min = null,
    double? Max = null,
    string? Unit = null,
    IReadOnlyList<string>? Choices = null,
    IReadOnlyList<string>? Fallback = null,
    bool Advanced = false,
    SettingDependency? AppliesWhen = null);

/// <summary>One setting's dependency on another's value.</summary>
/// <param name="Key">The controlling setting, which must itself be in the catalogue.</param>
/// <param name="Values">
/// The controlling values that make the dependent setting live. A subset of that setting's
/// <see cref="SettingDescriptor.Choices"/> — a value outside them would be a rule that can never
/// match, so <see cref="SettingsCatalog"/> has a test asserting it.
/// </param>
/// <param name="Reason">
/// What to say when it does not apply, phrased as the fact rather than as a refusal: the operator has
/// not done anything wrong, they are looking at a knob this device does not have.
/// </param>
public sealed record SettingDependency(string Key, IReadOnlyList<string> Values, string Reason);

/// <summary>
/// Every setting a user may change from the App, and everything needed to render, validate and
/// explain it.
///
/// <para><b>One table, three consumers.</b> The endpoint validates against it, the App draws its
/// form from it, and the docs describe it. A key that is not here cannot be written — which is what
/// keeps the infrastructure settings (where the database is, what signs a token, which render node
/// to encode on) out of reach of anything but the deployment, and means a new option property is
/// invisible to the UI until someone deliberately writes a line here explaining it.</para>
///
/// <para>Deliberately absent, and staying environment-only: <c>Serval:Mongo:*</c> (the server cannot
/// look up where it is stored in the place it is stored), <c>Serval:Media:Root</c>,
/// <c>Serval:Ingest:FfmpegPath</c>/<c>FfprobePath</c>/<c>HwAccelDevice</c>/<c>Encoder</c>,
/// <c>Serval:Auth:SigningKey</c> and the bootstrap admin fields, and the exposure settings
/// <c>Serval:ApiKey</c>, <c>Serval:Cors:AllowedOrigins</c> and <c>Serval:OpenApi:Enabled</c>. Each is
/// either load-bearing for the server's ability to start, or a thing that changing through the UI
/// could lock the operator out of the UI.</para>
///
/// <para><c>Serval:Ai:Detection:Masks</c> is also absent, for a different reason: it is polygons
/// over one camera's view, so it is edited on the camera, not here.</para>
/// </summary>
public static class SettingsCatalog
{
    /// <summary>
    /// The sections of the settings page.
    ///
    /// <para><b>A camera section that overrides one of these carries the same name.</b> Six do —
    /// <c>Streams</c>, <c>Recording</c>, <c>Objects &amp; alerts</c>, <c>Motion detection</c>,
    /// <c>Speech &amp; transcription</c> and <c>Sound recognition</c> — and the camera editor's
    /// <c>CameraSection</c> enum names them identically on purpose, so that moving between the two
    /// pages is reading one vocabulary rather than two. Renaming one without the other is the
    /// change that undoes this.</para>
    ///
    /// <para><c>Speech &amp; transcription</c> and <c>Voice identification</c> are separate because
    /// a camera can override the first and nothing at all in the second: they answer <em>what was
    /// said</em> and <em>who said it</em>, on different models. Merging them would leave the camera
    /// section promising speaker identification it cannot touch.</para>
    /// </summary>
    public const string GroupRecording = "Recording";
    public const string GroupStreams = "Streams";
    public const string GroupLive = "Live view";
    public const string GroupPtz = "Pan, tilt & zoom";
    public const string GroupAi = "Detection engine";
    public const string GroupObjects = "Objects & alerts";
    public const string GroupTracking = "Object tracking";
    public const string GroupMotion = "Motion detection";
    public const string GroupScene = "Scene descriptions";
    public const string GroupSpeech = "Speech & transcription";
    public const string GroupVoices = "Voice identification";
    public const string GroupSound = "Sound recognition";
    public const string GroupAlerts = "Alerts";
    public const string GroupNotifications = "Notifications";
    public const string GroupHealth = "System health";
    public const string GroupSessions = "Sign-in sessions";

    /// <summary>
    /// The groups in the order the App should draw them: what the cameras do, then what is made of
    /// what they see, then what is made of what they hear, then the machine itself.
    /// </summary>
    public static readonly IReadOnlyList<string> Groups =
    [
        GroupRecording, GroupStreams, GroupLive, GroupPtz,
        GroupAi, GroupObjects, GroupTracking, GroupMotion, GroupScene,
        GroupSpeech, GroupVoices, GroupSound,
        GroupAlerts, GroupNotifications, GroupHealth, GroupSessions,
    ];

    /// <summary>A tuning setting only the ONNX runtime reads, with what to say on a device that does
    /// not. The value list comes from the factory rather than being written out again here, so a
    /// device added there cannot leave these rules quietly naming a shorter world.</summary>
    private static SettingDependency OnnxOnly(string reason) =>
        new("Serval:Ai:Detection:Device", ObjectDetectorFactory.OnnxDevices, reason);

    public static readonly IReadOnlyList<SettingDescriptor> All =
    [
        // ---- Recording -------------------------------------------------------------------------
        new("Serval:Media:RetentionDays", GroupRecording, "Keep recordings for",
            "How long footage is kept before the sweep deletes it. This is the default — a camera "
            + "can keep its own footage for longer or shorter in its settings. Retention is by age "
            + "only; there is no size cap, so a bigger number needs a bigger disk.",
            SettingKind.Int, Min: 1, Max: 365, Unit: "days"),

        new("Serval:Media:RetentionSweepMinutes", GroupRecording, "Delete old footage every",
            "How often the sweep looks for footage past its retention. Deleting is cheap; this only "
            + "decides how long expired recordings linger on disk before they go.",
            SettingKind.Number, Min: 1, Max: 1440, Unit: "minutes", Advanced: true),

        new("Serval:Media:ClipMaxMinutes", GroupRecording, "Longest saved clip",
            "How much footage one saved clip may cover. Saved clips are copies that never roll off, "
            + "so this is really a disk limit — half an hour of a 2K camera is a couple of gigabytes, "
            + "kept until somebody deletes it. The clip screen shows whatever this says.",
            SettingKind.Int, Min: 1, Max: 240, Unit: "minutes"),

        // ---- Alerts --------------------------------------------------------------------------
        new("Serval:Media:AlertRetentionDays", GroupAlerts, "Keep alerts for",
            "How long an alert and its preview clip are kept. Separate from recording retention on "
            + "purpose, and usually longer: the point of a preview is that it outlives the footage "
            + "it was cut from, so an alert from last month still shows what happened even though "
            + "the recording went days ago. Dismissing an alert does not delete it any sooner — "
            + "clearing the queue is about attention, not evidence.",
            SettingKind.Int, Min: 1, Max: 365, Unit: "days"),

        new("Serval:Media:AlertPreRollSeconds", GroupAlerts, "Preview starts before",
            "How much of a preview clip comes before the alert fired. This is the difference "
            + "between watching someone walk up to the door and finding them already standing at "
            + "it. It cannot exceed what the preview buffer is holding.",
            SettingKind.Number, Min: 0, Max: 60, Unit: "seconds"),

        new("Serval:Media:AlertPostRollSeconds", GroupAlerts, "Preview continues for",
            "How much of a preview clip comes after the alert fired — and, because the footage has "
            + "to exist before it can be cut, roughly how long an alert takes to become watchable. "
            + "A preview is a moment, not the whole visit: the object may still be there long after "
            + "this, which is what Watch opens the recording for.",
            SettingKind.Number, Min: 1, Max: 120, Unit: "seconds"),

        new("Serval:Ingest:PreviewBufferSeconds", GroupAlerts, "Preview buffer",
            "How many seconds of each camera's detect stream are kept on a rolling buffer so that "
            + "an alert can be given a clip which starts before it fired. Nothing can reconstruct "
            + "those seconds afterwards, so they have to have been written already — and on a "
            + "camera you are not recording, this is the only place they exist. It must comfortably "
            + "exceed the two padding figures above. The cost is small and fixed: about 5 MB per "
            + "camera at 90 seconds of a 640x360 sub stream, however long the server runs. Zero "
            + "turns the buffer off, which leaves alerts on unrecorded cameras with no preview.",
            SettingKind.Number, Min: 0, Max: 600, Unit: "seconds", RestartRequired: false, Advanced: true),

        new("Serval:Ingest:SegmentSeconds", GroupRecording, "Length of each recording file",
            "Recordings are written in chunks of this length. It is the granularity at which "
            + "playback seeks and retention prunes — shorter means finer seeking and many more "
            + "files, longer means fewer files and coarser scrubbing.",
            SettingKind.Number, Min: 1, Max: 60, Unit: "seconds", Advanced: true),

        // ---- Live view ---------------------------------------------------------------------
        new("Serval:Ingest:SnapshotFps", GroupLive, "How often wall pictures update",
            "How often each camera produces the still image the dashboard wall shows. One per "
            + "second is plenty; raising it costs a JPEG encode per camera per frame.",
            SettingKind.Number, Min: 0.1, Max: 10, Unit: "per second"),

        new("Serval:Ingest:DecodeThreads", GroupLive, "Threads for wall pictures",
            "How many threads decode the stream that feeds the wall and object detection. This "
            + "buys latency, not speed: decoding on many threads means holding that many frames "
            + "before the first one comes out, which at a sub stream's rate is seconds rather than "
            + "milliseconds. Leaving it to the host's core count is what puts a big machine further "
            + "behind its cameras than a small one — on a 16-core host, all-cores measured 3-4 "
            + "seconds from shutter to wall against about 1 second at two threads, and detections "
            + "were late by the same amount. Raise it only if a camera's decode cannot keep pace, "
            + "which for a sub stream means a very large one.",
            SettingKind.Int, Min: 1, Max: 32, Advanced: true),

        new("Serval:Ingest:DetectFps", GroupAi, "Pictures checked each second",
            "How many frames a second each camera hands to object detection. Separate from how often "
            + "wall pictures update because it answers a different question: at one frame a second a "
            + "car crossing a driveway is seen once or not at all. Raising it is cheap on the camera "
            + "side — those frames are already being decoded — but it multiplies how much work the "
            + "detector is asked to do. Zero turns detection frames off entirely.",
            SettingKind.Number, Min: 0, Max: 15, Unit: "per second"),

        new("Serval:Ingest:DetectFrameWidth", GroupAi, "Width of pictures checked",
            "How wide the frames handed to detection are. Not the same as the detector's own input "
            + "size, and deliberately larger: this is the detail a distant subject still has when it "
            + "reaches the model. A cap, not a resize — a smaller source is left alone.",
            SettingKind.Int, Min: 320, Max: 3840, Unit: "pixels"),

        new("Serval:Ingest:SnapshotMaxMegapixels", GroupLive, "Largest wall picture",
            "How much picture a snapshot keeps, counted in megapixels rather than in width so that "
            + "the shape of the camera stops deciding the answer. A cap, not a resize — a camera "
            + "already under it pays nothing, and nothing is ever upscaled. It matters most for a 4K "
            + "camera with no separate detect stream, which would otherwise push 8-megapixel frames "
            + "into the vision model and every dashboard tile once a second. At 0.25 a 16:9 camera "
            + "lands on 666x374 and a 32:9 one on 942x264 — the same amount of picture, which is what "
            + "a width limit could not give them. Raising it sharpens every camera by the same "
            + "factor. Zero removes the cap.",
            SettingKind.Number, Min: 0, Max: 4, Unit: "megapixels", Advanced: true),

        // ---- Streams -----------------------------------------------------------------------
        new("Serval:Ingest:Bitrate", GroupStreams, "Re-encoded video quality",
            "Target video bitrate for a stream that is re-encoded and does not name its own, in "
            + "ffmpeg's syntax — 2M, 800k. Only applies to streams that opt into re-encoding; a "
            + "camera recorded as it arrives never reaches an encoder.",
            SettingKind.Text),

        new("Serval:Ingest:ReconnectSeconds", GroupStreams, "Wait before trying a camera again",
            "A camera that drops is retried, never abandoned. Backoff starts here and doubles on "
            + "each failure.",
            SettingKind.Number, Min: 0.5, Max: 60, Unit: "seconds", Advanced: true),

        new("Serval:Ingest:MaxReconnectSeconds", GroupStreams, "Longest wait between tries",
            "The ceiling the reconnect backoff doubles up to, so an offline camera settles into "
            + "retrying at a steady, cheap interval instead of hammering.",
            SettingKind.Number, Min: 1, Max: 3600, Unit: "seconds", Advanced: true),

        new("Serval:Ingest:StallTimeoutSeconds", GroupStreams,
            "Restart a camera that stops sending after",
            "A camera whose connection drops outright is reconnected already. This catches the "
            + "quieter failure: the stream goes dead but the process stays alive, so the camera "
            + "still looks healthy while nothing reaches disk. Recording and snapshots are watched "
            + "separately, because they are separate processes and one can wedge while the other "
            + "carries on. Either one producing nothing for this long is restarted. Keep it well "
            + "above your longest gap between recording files — that gap is the camera's keyframe "
            + "interval, not the file length. Zero turns this off.",
            SettingKind.Number, Min: 0, Max: 3600, Unit: "seconds", Advanced: true),

        new("Serval:Ingest:VideoPassthroughCodecs", GroupStreams,
            "Record these video formats as they arrive",
            "Video in these formats is written to disk with no decode and no re-encode. A camera "
            + "sending anything else fails loudly and names the format, rather than being silently "
            + "re-encoded behind your back — narrowing this list causes rejections, not re-encodes. "
            + "Widening it is a decision about playback: an HEVC or AV1 archive is smaller but not "
            + "universally playable.",
            SettingKind.TextList, Choices: ["h264", "hevc", "av1", "vp9"],
            Fallback: IngestOptions.DefaultVideoPassthroughCodecs, Advanced: true),

        new("Serval:Ingest:AudioPassthroughCodecs", GroupStreams,
            "Record these audio formats as they arrive",
            "Audio in these formats is copied as-is; anything else becomes 64 kbps mono AAC. Most IP "
            + "cameras emit G.711, which the recording container cannot carry at all, so this list "
            + "is a constraint of the format rather than a preference.",
            SettingKind.TextList, Choices: ["aac", "opus", "mp3"],
            Fallback: IngestOptions.DefaultAudioPassthroughCodecs, Advanced: true),

        new("Serval:WebRtc:Enabled", GroupLive, "Low-latency live view",
            "Serves the focused single-camera view over WebRTC through the go2rtc sidecar, which is "
            + "what makes pan/tilt and talk-back feel immediate. Recording is unaffected either way "
            + "— turning this off just means the focused view falls back to the recording stream.",
            SettingKind.Bool),

        new("Serval:WebRtc:Go2RtcUrl", GroupLive, "go2rtc address",
            "Where the go2rtc sidecar's API lives, as this server reaches it. Your browser never "
            + "uses this address — video flows directly from go2rtc to the browser.",
            SettingKind.Text, RestartRequired: true, Advanced: true),

        // ---- PTZ ------------------------------------------------------------------------------
        new("Serval:Ptz:MoveTimeoutSeconds", GroupPtz, "Auto-stop a move after",
            "Every move command tells the camera to stop on its own after this long if nothing else "
            + "arrives — a safety net so a dropped connection cannot leave a camera panning forever. "
            + "Holding a direction re-sends the command before this elapses, so keep it short.",
            SettingKind.Number, Min: 0.1, Max: 10, Unit: "seconds"),

        new("Serval:Ptz:RequestTimeoutSeconds", GroupPtz, "Give up on a camera after",
            "How long to wait on an unresponsive camera before abandoning a pan/tilt request.",
            SettingKind.Number, Min: 1, Max: 60, Unit: "seconds", Advanced: true),

        new("Serval:Ptz:CapabilityCacheMinutes", GroupPtz, "Re-check camera capabilities every",
            "How long a camera's probed presets and movement axes stay trusted. Short because "
            + "presets get saved on the camera's own web page with nothing to tell us, and needing "
            + "a server restart to notice would be wrong.",
            SettingKind.Number, Min: 0, Max: 1440, Unit: "minutes", Advanced: true),

        // ---- Vitals ---------------------------------------------------------------------------
        new("Serval:Vitals:Enabled", GroupHealth, "Measure this server",
            "Reads processor, memory, GPU and disk figures on a timer for the status page. Loads no "
            + "models and holds nothing in memory beyond a small history.",
            SettingKind.Bool),

        new("Serval:Vitals:SampleSeconds", GroupHealth, "Sample every",
            "How often processor, memory and GPU are re-read. This is also the window the processor "
            + "percentage is measured over, so shortening it makes the number noisier rather than "
            + "fresher, and lengthening it makes a transcode starting take longer to show up.",
            SettingKind.Number, Min: 1, Max: 300, Unit: "seconds", Advanced: true),

        new("Serval:Vitals:DiskScanMinutes", GroupHealth, "Measure per-camera disk use every",
            "Walks each camera's folder to total its size. This is the one expensive measurement "
            + "here — a week of footage is around 150,000 files per camera — so it refreshes one "
            + "camera per tick in the background. A camera added since the last walk is measured on "
            + "the next tick rather than waiting its turn, so this is also how long a new camera "
            + "takes to appear in the breakdown. Zero switches the per-camera figures off entirely "
            + "and keeps the whole-volume free space number, which is what the alerts use.",
            SettingKind.Number, Min: 0, Max: 1440, Unit: "minutes", Advanced: true),

        new("Serval:Vitals:HistoryMinutes", GroupHealth, "Keep vitals history for",
            "How much processor, memory and GPU history the sparklines show. Costs memory only — "
            + "samples are taken on the schedule above regardless, and this decides how many are "
            + "kept rather than discarded. History is lost on restart. Zero keeps only the current "
            + "reading.",
            SettingKind.Number, Min: 0, Max: 1440, Unit: "minutes", Advanced: true),

        new("Serval:Vitals:DiskWarnPercentFree", GroupHealth, "Warn when free space falls below",
            "Raises a warning on the status page. Worth believing, because retention is by age only "
            + "— nothing stops recording when the volume fills, writes simply start failing.",
            SettingKind.Number, Min: 0, Max: 100, Unit: "% free"),

        new("Serval:Vitals:DiskCriticalPercentFree", GroupHealth, "Critical when free space falls below",
            "The louder version of the warning above. There is no automatic defence at this point; "
            + "this alert is the only thing that says the volume is about to stop accepting footage.",
            SettingKind.Number, Min: 0, Max: 100, Unit: "% free"),

        new("Serval:Vitals:MemoryWarnPercent", GroupHealth, "Warn when memory use exceeds",
            "Only meaningful when the container has a memory limit set; with no limit there is "
            + "nothing to be near and the warning is suppressed.",
            SettingKind.Number, Min: 0, Max: 100, Unit: "%"),

        // ---- Notifications ---------------------------------------------------------------------
        new("Serval:Push:Enabled", GroupNotifications, "Send push notifications",
            "Whether alerts are pushed to browsers that have asked for them. What gets pushed is "
            + "the alert queue and nothing else, so this changes delivery rather than what counts "
            + "as an alert. Turning it off leaves every subscription in place, so turning it back "
            + "on does not ask anybody to grant permission again.\n\n"
            + "Notifications need HTTPS. Browsers refuse to register a service worker on a plain "
            + "HTTP page, so on a deployment without TLS this is on and nothing arrives.",
            SettingKind.Bool),

        new("Serval:Push:CooldownSeconds", GroupNotifications,
            "Wait before notifying about the same thing",
            "How long a camera is left alone after it has interrupted somebody about one thing, "
            + "before it may interrupt them about that same thing again. Somebody walking out of "
            + "shot and back is a second arrival and a second alert, correctly — but it is not a "
            + "second thing worth reaching for a phone about, and a phone that buzzes for every row "
            + "the queue gains is a phone people put face down.\n\n"
            + "Only the notification is held back. Every alert still lands in the queue with its "
            + "clip, and a different object or sound on the same camera is different news and goes "
            + "straight out. Counted per person, so one household's phones do not silence another's. "
            + "Each person can set their own for a camera on their notifications screen; this is "
            + "what they inherit until they do, and zero sends every one.",
            SettingKind.Int, Min: 0, Max: PushOptions.MaxCooldownSeconds, Unit: "seconds"),

        new("Serval:Push:ContactUri", GroupNotifications, "Contact address for push services",
            "How Google, Mozilla or Apple reach whoever runs this server if it starts misbehaving — "
            + "they relay the notifications and ask for a contact in return. It is sent to them "
            + "only, never to a person receiving an alert. A mailto: or https: URL; some services "
            + "reject anything else.",
            SettingKind.Text, Advanced: true),

        new("Serval:Push:TtlSeconds", GroupNotifications, "Hold undelivered notifications for",
            "How long a push service keeps trying to reach a device that is offline. Long enough to "
            + "cover a phone in a tunnel is the point; much longer only means somebody opening their "
            + "phone in the morning is told about the night one alert at a time, which the queue "
            + "already does better.",
            SettingKind.Int, Min: 0, Max: 86400, Unit: "seconds", Advanced: true),

        new("Serval:Push:MaxFailures", GroupNotifications, "Give up on a device after",
            "How many delivery failures in a row retire a subscription. A browser that has thrown "
            + "its subscription away says so plainly and is dropped at once regardless of this — "
            + "this is for the ambiguous case, where the push service itself is unwell and the "
            + "device is still real. The App re-registers on next launch either way.",
            SettingKind.Int, Min: 1, Max: 1000, Unit: "failures", Advanced: true),

        // ---- Sessions -------------------------------------------------------------------------
        new("Serval:Auth:AccessTokenMinutes", GroupSessions, "Access token lifetime",
            "How long a signed-in session's token is good for before it silently renews. Short on "
            + "purpose — it is the refresh lifetime below, not this, that decides how much a stolen "
            + "credential is worth.",
            SettingKind.Int, Min: 1, Max: 1440, Unit: "minutes", Advanced: true),

        new("Serval:Auth:RefreshTokenDays", GroupSessions, "Stay signed in for",
            "How long an unused session stays valid before the person has to sign in again.",
            SettingKind.Int, Min: 1, Max: 365, Unit: "days"),

        // ---- Detection engine -------------------------------------------------------------------
        // Not "Run detection on this server", which it was: this switch gates the description,
        // speech and sound models as well as the object detector, so naming it for detection put
        // two switches in this group that sounded like the same one and were not.
        new("Serval:ServerAi:Enabled", GroupAi, "Run analysis on this server",
            "The master switch for everything this server works out from what its cameras see and "
            + "hear — objects, descriptions, speech and sounds alike. Off means this server only "
            + "records. Turning it on loads real models into memory, so a server that just records "
            + "should leave it off. Cameras still opt in individually — this only makes it possible.",
            SettingKind.Bool, RestartRequired: true),

        new("Serval:ServerAi:ReconcileSeconds", GroupAi, "Apply camera changes within",
            "How often the server compares what each camera asks for against what is running, and "
            + "restarts a camera's detection when they differ. This is what makes a tuning change "
            + "take effect without a restart.",
            SettingKind.Number, Min: 1, Max: 300, Unit: "seconds", Advanced: true),

        new("Serval:Ai:Detection:Enabled", GroupAi, "Look for objects",
            "Runs an object detector on each camera's frames. When on it replaces watching for "
            + "movement entirely rather than sitting behind it. This is the difference between knowing "
            + "something changed and knowing a person has been at the door for forty seconds — and "
            + "it is the only one of the two that can see something present but not moving.",
            SettingKind.Bool, RestartRequired: true),

        new("Serval:Ai:Detection:Device", GroupAi, "Detection runs on",
            "What runs the detector, named as a runtime and a piece of silicon. 'onnx-cpu' is the "
            + "default and works everywhere. 'tflite-edgetpu' uses attached Coral Edge TPUs and "
            + "refuses to start if none is found, rather than quietly running at CPU speed. The three "
            + "GPU providers need an image built with them and are greyed out otherwise. The runtime "
            + "half also says how the model file below is read, so changing it usually means changing "
            + "that too.",
            SettingKind.Choice, RestartRequired: true, Choices: [.. ObjectDetectorFactory.Devices]),

        new("Serval:Ai:Detection:ModelPath", GroupAi, "Detection model file",
            "Path to the detection weights inside the container — one file for whichever device is "
            + "selected above, read as ONNX for an 'onnx-' device and as the edgetpu_compiler output "
            + "for a 'tflite-' one. Pointing it at the wrong family is refused at startup, by the "
            + "file's own contents rather than its name.",
            SettingKind.Text, RestartRequired: true, Advanced: true),

        new("Serval:Ai:Detection:LabelsPath", GroupAi, "Detection labels file",
            "Path to the class list that ships with those weights. The two must match — a mismatch "
            + "produces confidently wrong names for everything, forever. Note that the two model "
            + "families do not share a class list: a COCO-90 accelerator model spells the classes the "
            + "same but numbers them differently, so its own labels file has to travel with it.",
            SettingKind.Text, RestartRequired: true, Advanced: true),

        new("Serval:Ai:Detection:MaxConcurrency", GroupAi, "Detections at once",
            "How many frames the detector works on simultaneously, each on its own copy of the "
            + "model. Zero — the default — works it out from the host. This is what stops a "
            + "multi-core machine being capped at one inference at a time: measured on four cores, "
            + "two at once ran 1.98x as many, and on 32 threads four at once ran 3.30x. The cost is "
            + "memory, since each one carries its own weights and its own scratch space, which is "
            + "why it is capped rather than simply set to the core count.",
            SettingKind.Int, RestartRequired: true, Min: 0, Max: 8, Advanced: true,
            AppliesWhen: OnnxOnly(
                "An Edge TPU runs one inference at a time, so concurrency is the device count.")),

        new("Serval:Ai:Detection:InputPixels", GroupAi, "Detection input budget",
            "How much every inference is allowed to cost, in pixels. Each camera is given the shape "
            + "nearest its own aspect at about this size, so a 16:9 driveway gets 640x384, a 3:4 "
            + "doorbell 416x576 and a 32:9 panoramic 960x256 — the same cost each, none of them "
            + "spending it on grey padding. Halving this roughly halves detection cost and gives up "
            + "the far field to get there. Only used by a model exported with dynamic input axes; a "
            + "fixed-shape model carries its own shape and this is ignored.",
            SettingKind.Int, RestartRequired: true, Min: 16384, Max: 1638400, Unit: "pixels",
            Advanced: true, AppliesWhen: OnnxOnly(
                "A compiled Edge TPU model has its input shape baked in, so there is no budget to "
                + "spend.")),

        new("Serval:Ai:Detection:NumThreads", GroupAi, "Detection threads",
            "Kept low on purpose. What hurts is not this model's speed — it is tens of milliseconds "
            + "— but its competition with the description model, which saturates every core for "
            + "seconds at a time.",
            SettingKind.Int, RestartRequired: true, Min: 1, Max: 32, Advanced: true,
            AppliesWhen: OnnxOnly(
                "The work happens on the Edge TPU, so its lane uses a single host thread.")),

        new("Serval:Ai:Detection:MaxFps", GroupObjects, "How often to look for objects",
            "A ceiling on how often any one camera is looked at, however fast frames arrive. Zero — "
            + "the default — removes the ceiling and lets the pictures-checked rate decide. Use it to "
            + "make one camera cheaper than the rest: a spare room can be worth a look every ten "
            + "seconds while a drive is worth every frame. Below about 2 a second the tracker stops "
            + "being able to follow anything, so the objects on that camera become a fresh sighting "
            + "every frame rather than things with a history.",
            SettingKind.Number, Min: 0, Max: 30, Unit: "per second"),

        // ---- Where it looks ---------------------------------------------------------------------
        new("Serval:Ai:Detection:Regions:Mode", GroupAi, "Zoom in on movement",
            "Instead of shrinking the whole frame to fit the model, cut a crop around whatever moved "
            + "and show the model that at full resolution. It is the difference between a person at "
            + "the end of a driveway arriving 27 pixels tall and arriving 200 — which is the "
            + "difference between finding them and not. Worth it only when frames are much larger "
            + "than the model's input, so 'auto' decides from that ratio and says which way it went "
            + "in the log.",
            SettingKind.Choice, Choices: ["auto", "on", "off"]),

        new("Serval:Ai:Detection:Regions:AutoMinRatio", GroupAi, "Zoom in when a crop would be at least",
            "What 'auto' above decides on: how much bigger a subject arrives in a close-up than in the "
            + "whole picture shrunk to fit the model. Lowering it does more than add close-ups — once "
            + "they are on, the whole picture is checked every few seconds instead of every frame. On a "
            + "camera with little to recover that buys close-ups which are nearly the whole picture "
            + "anyway, so below about 1.5 it costs more than it finds.",
            SettingKind.Number, Min: 1, Max: 8, Unit: "x", Advanced: true),

        new("Serval:Ai:Detection:Regions:TiledFloor", GroupAi,
            "Check the picture in overlapping squares",
            "How the guaranteed whole-frame look is made. Off, it is one pass with the frame shrunk to "
            + "fit the model — which covers every pixel but, on a wide camera squeezed hard, at a scale "
            + "that has already lost the far field. On, it checks native-scale tiles instead. A short "
            + "sweep is checked all at once every frame; a longer one takes a tile at a time, and that "
            + "one also needs zooming in on movement, because it looks at the edges of the picture only "
            + "once a pass and a new object seen that rarely is never confirmed. Only engages on "
            + "cameras squeezed past the threshold below.",
            SettingKind.Bool, RestartRequired: true, Advanced: true),

        new("Serval:Ai:Detection:Regions:TiledFloorMinGain", GroupAi, "Tile the floor above",
            "How much the frame has to be shrunk before tiling is worth it, as the same magnification "
            + "ratio that decides cropping. A camera already arriving near full scale gains nothing "
            + "from tiles. Note this asks whether tiling buys anything, not what it costs: a camera "
            + "squeezed harder needs more tiles, so a low threshold admits the cheap cameras rather "
            + "than the expensive ones.",
            SettingKind.Number, RestartRequired: true, Min: 1, Max: 10, Unit: "x", Advanced: true),

        new("Serval:Ai:Detection:Regions:SweepAtOnce", GroupAi, "Examine a whole sweep in one frame up to",
            "A short sweep is checked all at once, every frame, covering the picture exactly as often "
            + "as the single shrunken look it replaces and in full detail. A longer one goes a tile at "
            + "a time, covering the picture only once every few seconds and leaning on movement in "
            + "between. Two tiles is what an ordinary 16:9 camera costs; raising this multiplies the "
            + "guaranteed work every affected camera does, so check the detection budget afterwards.",
            SettingKind.Number, Min: 1, Max: 8, Unit: "tiles", Advanced: true),

        new("Serval:Ai:Detection:Regions:TileOverlapFraction", GroupAi, "Tile overlap",
            "How much neighbouring tiles share. Not a nicety: an object lying across a tile boundary is "
            + "cut in two and detected as neither half, so this has to be larger than the biggest thing "
            + "worth finding as a share of one tile.",
            SettingKind.Number, RestartRequired: true, Min: 0, Max: 0.9, Advanced: true),

        new("Serval:Ai:Detection:Regions:FloorSeconds", GroupAi, "Check the whole picture every",
            "How often the whole frame is looked at regardless of movement. This is what keeps "
            + "'it is still there' a measurement rather than a guess from silence: without it, a "
            + "parked car nothing moves near would stop being detected and the record would say it "
            + "left. It also finds whatever arrived while movement was being ignored — during the "
            + "night-mode switch, say — and then stayed still.",
            SettingKind.Number, Min: 1, Max: 120, Unit: "seconds"),

        new("Serval:Ai:Detection:Regions:MaxPerFrame", GroupAi, "Close-up looks per picture",
            "The most crops any one frame is worth. Without a cap a hedge in the wind proposes one "
            + "per gust and spends the detection budget every other camera needed.",
            SettingKind.Int, Min: 1, Max: 10, Unit: "crops", Advanced: true),

        new("Serval:Ai:Detection:Regions:MinCells", GroupAi, "Ignore movement under",
            "How much has to change in one place before it is worth a closer look, in cells of the "
            + "motion grid. Below this it is sensor noise, a leaf, or rain.",
            SettingKind.Int, Min: 1, Max: 200, Unit: "cells", Advanced: true),

        new("Serval:Ai:Detection:Regions:MinSizeFraction", GroupAi, "Smallest crop",
            "How small a crop may get, as a share of the frame. Zooming further in sounds like it "
            + "should help and does the opposite: a crop tight enough to cut a vehicle in half was "
            + "measured to find nothing at all — not the person it was centred on, nor the vehicle. "
            + "A detector needs the whole of a thing and some of what is around it.",
            SettingKind.Number, Min: 0.05, Max: 1, Advanced: true),

        new("Serval:Ai:Detection:Regions:MinRegionScale", GroupAi, "Largest crop",
            "The counterpart to the smallest crop, and the more important one on a wide camera: how "
            + "far a crop may be shrunk to reach the detector. Past a point a small model does not "
            + "just miss things, it invents them — a flower pot was read correctly at three quarter "
            + "scale and called a person below it, more confidently the further it shrank. A crop "
            + "bigger than this is examined in overlapping pieces instead. Zero switches it off.",
            SettingKind.Number, Min: 0, Max: 1, Advanced: true),

        new("Serval:Ai:Detection:Regions:MaxRegionScale", GroupAi, "Never magnify a crop past",
            "A crop smaller than the model's input has to be blown up to fill it, and blowing a picture "
            + "up does not add detail — it invents it. Measured against a car found in all sixty of "
            + "sixty whole frames: at 1.25x it was still found in all sixty, at 2x in twenty-seven, and "
            + "at 2.75x — what the smallest crop above asks for on an ordinary camera — in none at all. "
            + "1 means a crop is never enlarged, only ever shrunk, and costs no magnification that was "
            + "ever real. A camera whose pictures are smaller than the model's input cannot honour this, "
            + "because the whole picture is already being enlarged before any crop is cut. Zero switches "
            + "it off.",
            SettingKind.Number, Min: 0, Max: 4, Unit: "x", Advanced: true),

        // ---- Following objects between frames ----------------------------------------------------
        new("Serval:Ai:Detection:Tracking:ConfirmSeconds", GroupObjects, "Believe it after",
            "How long something must keep being seen before it is treated as really there, on top "
            + "of needing at least two separate sightings. This is what removes the confident "
            + "one-frame ghost — an infrared-lit bush read as a person — because a ghost rarely "
            + "repeats in the same place while a real person stays put. In seconds rather than "
            + "frames on purpose: raising the frame rate then buys more evidence for the same wait, "
            + "instead of quietly cutting the wait.",
            SettingKind.Number, RestartRequired: true, Min: 0, Max: 30, Unit: "seconds"),

        new("Serval:Ai:Detection:Tracking:CoastSeconds", GroupObjects, "Keep following it for",
            "How long an object is still considered present after the detector stops finding it. "
            + "This is what carries somebody behind a parked car and out the other side as one "
            + "person rather than two. Match it to your site: a view full of things to walk behind "
            + "wants longer. Too long instead claims something is still there after it has gone, "
            + "which is worse, because how long it was there is what gets recorded.",
            SettingKind.Number, RestartRequired: true, Min: 0, Max: 60, Unit: "seconds"),

        // ---- Object tracking ---------------------------------------------------------------------
        // Every setting here is Advanced, which is deliberate and pinned by SettingsCatalogTests:
        // these are the filter's internals, and a group with nothing everyday in it is the signal
        // that they do not belong beside the class lists an operator actually tunes.
        new("Serval:Ai:Detection:Tracking:MinIou", GroupTracking, "Match boxes overlapping by",
            "How much a new box must overlap where an object was predicted to be for the two to be "
            + "the same thing. Raise it and a subject that changes direction sharply becomes two "
            + "objects; lower it and two people passing each other can be merged into one. It also "
            + "decides whether something reappearing after being lost rejoins the record it already "
            + "had, so raising it gives a flickering distant object a fresh row every time it drops "
            + "out. Reach for this when tracks break up, not before.",
            SettingKind.Number, RestartRequired: true, Min: 0.05, Max: 0.9, Advanced: true),

        new("Serval:Ai:Detection:Tracking:ProcessNoise", GroupTracking,
            "Trust movement over the detector",
            "How far an object is allowed to drift per second when nothing is measured, as a share "
            + "of the frame. Higher follows something that speeds up or turns, at the cost of "
            + "jittering along with the detector's own box noise; lower is smoother but lags "
            + "anything that changes direction. The default suits people and vehicles at walking "
            + "and driving speeds.",
            SettingKind.Number, RestartRequired: true, Min: 0.001, Max: 1, Advanced: true),

        new("Serval:Ai:Detection:Tracking:MeasurementNoise", GroupTracking, "Doubt each box by",
            "How much a single detection is distrusted, as a share of the frame. Boxes on a subject "
            + "that is standing perfectly still still wobble by a few percent between frames, and "
            + "this is what keeps that wobble out of the estimate of where the subject is going.",
            SettingKind.Number, RestartRequired: true, Min: 0.001, Max: 1, Advanced: true),

        new("Serval:Ai:Detection:Tracking:MaxTracks", GroupTracking, "Follow at most",
            "A ceiling on how many objects one camera follows at once, so a pathological frame "
            + "cannot grow the list without bound. A guard rather than a dial — a scene genuinely "
            + "holding this many objects has a problem this setting will not fix.",
            SettingKind.Int, RestartRequired: true, Min: 1, Max: 512, Unit: "objects",
            Advanced: true),

        // ---- Objects & alerts -------------------------------------------------------------------
        new("Serval:Ai:Detection:Classes", GroupObjects, "Record these objects",
            "Which of the model's classes are worth writing down, as the default for every camera. "
            + "The full list includes a great many things (toasters, giraffes) that a security "
            + "camera reporting would only be reporting a mistake.",
            SettingKind.TextList, Fallback: DetectionOptions.DefaultClasses),

        new("Serval:Ai:Detection:DescribeClasses", GroupObjects, "Describe these objects",
            "Which objects are worth waking the description model for. Deliberately shorter than "
            + "the list above: knowing a car has been on the driveway since 18:00 is worth "
            + "recording, but spending seconds of inference to be told about it is not.",
            SettingKind.TextList, Fallback: DetectionOptions.DefaultDescribeClasses),

        new("Serval:Ai:Detection:AlertClasses", GroupObjects, "Alert on these objects",
            "Which objects raise an alert rather than just a record, held to the higher confidence "
            + "below.",
            SettingKind.TextList, Fallback: DetectionOptions.DefaultAlertClasses),

        new("Serval:Ai:Detection:ScoreThreshold", GroupObjects, "How sure it must be",
            "How sure the detector must be before it reports anything at all. A coarse filter — "
            + "keep it permissive, because nothing downstream can go below it, and a camera that "
            + "wants to be stricter raises its own floor in its settings.",
            SettingKind.Number, Min: 0, Max: 1),

        new("Serval:Ai:Detection:MinObjectFraction", GroupObjects, "Ignore anything smaller than",
            "How small a thing can be and still count, as a share of the picture's area. Zero — the "
            + "default — means no floor. This is the one thing a confidence setting cannot do for "
            + "you: when crops are on, magnifying a few-pixel speck until it fills the model's input "
            + "makes the model more sure of it, not less. Measured on a real drive, a lamp post 7 "
            + "pixels wide was reported as a person five times at up to 0.41, while the distant cars "
            + "the crops existed to find scored 0.49 to 0.57 — so no confidence floor separates "
            + "them, and a size floor does. Reach for it when one static thing keeps being reported; "
            + "0.0001 to 0.0002 was the range that worked there.",
            SettingKind.Number, Min: 0, Max: 0.5),

        new("Serval:Ai:Detection:AlertMinConfidence", GroupObjects, "How sure it must be to alert",
            "Deliberately higher than the ordinary floor. A false record costs a row in a feed; a "
            + "false alert costs trust in every alert after it. Something that turns up raises one "
            + "alert as soon as it is seen this clearly, at whatever point in the sighting that "
            + "happens — a visitor walking up a path is smallest and least certain in the moment they "
            + "first appear.",
            SettingKind.Number, Min: 0, Max: 1),

        new("Serval:Ai:Detection:AbsenceSeconds", GroupObjects, "Consider it gone after",
            "How long something must be out of sight before its episode is closed. Not optional: a "
            + "person turning sideways or stepping behind a pillar drops below the confidence floor "
            + "constantly, and without this each flicker would end one episode and start another. "
            + "Measured over 5.5 hours, five seconds produced 263 episodes where thirty produced 36, "
            + "for identical descriptions.",
            SettingKind.Number, Min: 1, Max: 3600, Unit: "seconds"),

        new("Serval:Ai:Detection:NoveltySeconds", GroupObjects, "Counts as arriving if gone for",
            "How long something must have been away before turning up counts as an arrival. This "
            + "is what separates an event from the furniture. A driveway camera sees a parked car in "
            + "every single frame for hours and a living room sees a couch forever; treating those "
            + "as arrivals would be louder and less useful than no detection at all. Asked of each "
            + "object rather than of its class, so a second car pulling onto a drive is an arrival "
            + "even while the first is still parked on it.",
            SettingKind.Number, Min: 1, Max: 86400, Unit: "seconds"),

        new("Serval:Ai:Detection:MaxEpisodeSeconds", GroupObjects, "Start a new event after",
            "A hard cut for something that never leaves, so a car parked for days becomes finished "
            + "records rather than one event that never closes. Pure bookkeeping — the continuation "
            + "is never treated as an arrival and never asks for a description.",
            SettingKind.Number, Min: 60, Max: 86400, Unit: "seconds"),

        new("Serval:Ai:Detection:MinMovementFraction", GroupObjects, "Counts as moving at",
            "How fast something must travel to be treated as moving, as a share of the frame width "
            + "per second. Movement is the other reason to describe something, and it catches what "
            + "arrival cannot — a car parked since before the camera started watching, which then "
            + "drives off. Also what keeps furniture quiet: a couch scores zero here forever. A "
            + "speed rather than a distance per frame, so changing how many pictures are checked "
            + "does not change what counts as moving.",
            SettingKind.Number, Min: 0, Max: 1, Unit: "of the frame a second"),

        new("Serval:Ai:Detection:TrackMinMovementFraction", GroupTracking,
            "Record a new track point every",
            "How far a box must shift before the episode's replay track records another point. Its "
            + "own setting rather than reusing the one above, which gates seconds of description "
            + "work — raising that one to quiet a chatty describer would otherwise silently coarsen "
            + "every replay overlay.",
            SettingKind.Number, Min: 0, Max: 1, Advanced: true),

        new("Serval:Ai:Detection:TrackMaxSamples", GroupTracking, "Track points per event",
            "The most positions one event's replay track may hold. Past it, every other point is "
            + "dropped rather than the track being cut short, so a long event keeps its whole "
            + "shape at gradually coarser detail.",
            SettingKind.Int, Min: 10, Max: 10000, Unit: "points", Advanced: true),

        new("Serval:Ai:Detection:DescribeCooldownSeconds", GroupObjects,
            "Wait before describing the same thing again",
            "Minimum gap between descriptions asked for the same class, counted per class so a "
            + "person and a car never silence each other. Without it, one person walking across the "
            + "view for a minute is a description every second.",
            SettingKind.Number, Min: 0, Max: 3600, Unit: "seconds"),

        // ---- Motion detection ---------------------------------------------------------------------
        new("Serval:Ai:Motion:Enabled", GroupMotion, "Watch for movement",
            "Compares each frame to the last and asks for a description when enough of the picture "
            + "changed. This is the fallback for when looking for objects is off — the two never run "
            + "together, because a pre-filter loose enough not to miss a distant figure admits "
            + "nearly every frame of an outdoor camera at night anyway.",
            SettingKind.Bool),

        new("Serval:Ai:Motion:PixelDelta", GroupMotion, "Counts as changed if brightness shifts",
            "How much one point of the picture must change in brightness to count. Below this is "
            + "camera noise and compression artefacts rather than anything happening.",
            SettingKind.Int, Min: 0, Max: 255),

        new("Serval:Ai:Motion:MinChangedFraction", GroupMotion,
            "Movement when this much has changed",
            "What fraction of the picture must change before it counts as movement. Raise it if a "
            + "tree outside the window keeps waking the describer; lower it if someone walking at "
            + "the far end of a garden is being missed.",
            SettingKind.Number, Min: 0, Max: 1),

        new("Serval:Ai:Motion:MaxChangedFraction", GroupMotion,
            "Ignore when more than this has changed",
            "An upper bound, and not a redundant one. When nearly every point changes at once it is "
            + "almost never movement — it is the camera switching to night mode, a light coming on, "
            + "or exposure hunting. Without this, dusk would trigger a description every single day.",
            SettingKind.Number, Min: 0, Max: 1),

        new("Serval:Ai:Motion:CompareWidth", GroupMotion, "Comparison width",
            "Frames are shrunk to this size before comparing. Movement is a coarse signal, so "
            + "shrinking costs almost nothing, removes sensor noise for free, and makes the "
            + "comparison behave the same whatever resolution the camera sends.",
            SettingKind.Int, Min: 16, Max: 640, Unit: "pixels", Advanced: true),

        new("Serval:Ai:Motion:CompareHeight", GroupMotion, "Comparison height",
            "The other half of the comparison size above.",
            SettingKind.Int, Min: 16, Max: 480, Unit: "pixels", Advanced: true),

        // ---- Scene descriptions -------------------------------------------------------------------
        new("Serval:Ai:Vision:Enabled", GroupScene, "Describe what cameras see",
            "Loads the vision-language model that writes a sentence about what is in frame. By far "
            + "the most expensive thing here — a description costs seconds of processor time, and "
            + "the model is gigabytes of memory.",
            SettingKind.Bool, RestartRequired: true),

        new("Serval:Ai:Vision:ModelPath", GroupScene, "Description model file",
            "Path to the GGUF weights inside the container.",
            SettingKind.Text, RestartRequired: true, Advanced: true),

        new("Serval:Ai:Vision:MmprojPath", GroupScene, "Description projector file",
            "Path to the multimodal projector that ships beside those weights — the part that turns "
            + "an image into something the language model can read.",
            SettingKind.Text, RestartRequired: true, Advanced: true),

        new("Serval:Ai:Vision:Prompt", GroupScene, "What to ask about one picture",
            "The instruction sent with one still image. Keep it short and ask for one sentence — "
            + "the longer the answer, the longer every camera waits for the one model.",
            SettingKind.Text),

        new("Serval:Ai:Vision:MotionPrompt", GroupScene, "What to ask about several pictures",
            "The instruction sent when several consecutive pictures go together, which is what lets "
            + "the answer describe change rather than just contents. {count} and {seconds} are "
            + "filled in with how many pictures and how far apart they were.",
            SettingKind.Text),

        new("Serval:Ai:Vision:MaxFrames", GroupScene, "Pictures per description",
            "How many consecutive pictures to send at once. More give a better account of what "
            + "changed and cost proportionally more to process.",
            SettingKind.Int, Min: 1, Max: 8, Unit: "pictures"),

        new("Serval:Ai:Vision:MaxTokens", GroupScene, "Longest description",
            "A ceiling on the answer length. This is a direct time cost — the model writes one word "
            + "at a time, and every camera is waiting behind the current one.",
            SettingKind.Int, Min: 16, Max: 1024, Unit: "tokens"),

        new("Serval:Ai:Vision:MinSecondsBetweenDescriptions", GroupScene, "Minimum gap between descriptions",
            "A floor on how often the model runs at all, across every camera. Deliberately shared "
            + "rather than per-camera: there is one model and it works on one thing at a time, so a "
            + "per-camera figure would let one busy camera starve all the others.",
            SettingKind.Number, Min: 0, Max: 3600, Unit: "seconds"),

        new("Serval:Ai:Vision:NumThreads", GroupScene, "Description threads",
            "Processor threads the description model may use. The main lever on how long each "
            + "description takes.",
            SettingKind.Int, RestartRequired: true, Min: 1, Max: 32, Advanced: true),

        new("Serval:Ai:Vision:GpuLayers", GroupScene, "Layers on the GPU",
            "How much of the model to offload to a graphics processor. Zero keeps it entirely on "
            + "the processor. Only useful on a build with GPU support and a card with room for it.",
            SettingKind.Int, RestartRequired: true, Min: 0, Max: 99, Unit: "layers", Advanced: true),

        new("Serval:Ai:Vision:ProjectorOnGpu", GroupScene, "Projector on the GPU",
            "Whether the image-reading half also goes on the graphics processor. Worth turning off "
            + "first when memory is tight.",
            SettingKind.Bool, RestartRequired: true, Advanced: true),

        new("Serval:Ai:Vision:ContextSize", GroupScene, "Context size",
            "How much the model can hold at once, in tokens. Images take a large share of this, so "
            + "reducing it to save memory can make multi-picture descriptions fail.",
            SettingKind.Int, RestartRequired: true, Min: 512, Max: 32768, Unit: "tokens",
            Advanced: true),

        // ---- Speech & transcription --------------------------------------------------------------
        new("Serval:Ai:AudioGate:Enabled", GroupSpeech, "Skip silence before transcribing",
            "A cheap loudness check in front of speech detection, which is an inference pass on "
            + "every fraction of a second of audio and almost entirely wasted in a quiet room. It "
            + "keeps a moment of audio from before it opened, so it cannot clip the start of a word.",
            SettingKind.Bool),

        new("Serval:Ai:AudioGate:RmsThreshold", GroupSpeech, "Counts as silence below",
            "How quiet audio must be to be treated as silence. Raise it if speech detection is "
            + "being woken constantly by room tone; lower it if quiet speech is being missed. "
            + "Cameras can set their own — the right value is a property of the room.",
            SettingKind.Number, Min: 0, Max: 1),

        new("Serval:Ai:AudioGate:PreRollWindows", GroupSpeech, "Keep this much audio from before",
            "How much audio from before the gate opened is replayed into the detector, so the "
            + "attack of the first word is never cut off.",
            SettingKind.Int, Min: 0, Max: 100, Unit: "windows", Advanced: true),

        new("Serval:Ai:AudioGate:HangoverSeconds", GroupSpeech, "Keep listening after silence for",
            "How long the gate stays open after the last loud moment. This is what makes skipping "
            + "quiet audio safe — it only ever closes well after speech has ended, so the detector "
            + "is never cut off mid-sentence.",
            SettingKind.Number, Min: 0, Max: 30, Unit: "seconds", Advanced: true),

        new("Serval:Ai:Vad:Threshold", GroupSpeech, "Speech confidence floor",
            "How sure the speech detector must be that it is hearing a voice. Cameras can override "
            + "this individually.",
            SettingKind.Number, Min: 0, Max: 1),

        new("Serval:Ai:Vad:MinSilenceSeconds", GroupSpeech, "Sentence ends after silence of",
            "How much quiet marks the end of something said. Too short chops sentences at every "
            + "pause for breath; too long runs two people's turns together.",
            SettingKind.Number, Min: 0, Max: 10, Unit: "seconds"),

        new("Serval:Ai:Vad:MinSpeechSeconds", GroupSpeech, "Ignore speech shorter than",
            "Sounds shorter than this are discarded rather than transcribed — a cough or a door "
            + "click is not a sentence.",
            SettingKind.Number, Min: 0, Max: 10, Unit: "seconds"),

        new("Serval:Ai:Vad:MaxSpeechSeconds", GroupSpeech, "Cut speech longer than",
            "A hard cut for someone who does not stop, so a long monologue becomes transcripts as "
            + "it happens rather than one that arrives when they finish.",
            SettingKind.Number, Min: 1, Max: 120, Unit: "seconds"),

        new("Serval:Ai:Asr:Language", GroupSpeech, "Transcribe as",
            "Which language to transcribe. Name the one this camera hears. Automatic re-decides on "
            + "every utterance, and a few seconds of one voice across a room is rarely enough to "
            + "tell from — it produces the occasional line in a language nobody spoke, and reads "
            + "the rest less accurately too.",
            SettingKind.Choice, RestartRequired: true,
            Choices: ["en", "zh", "ja", "ko", "yue", "auto"]),

        new("Serval:Ai:Asr:UseInverseTextNormalization", GroupSpeech, "Write numbers as digits",
            "Turns spoken forms into written ones — \"twenty twenty six\" into \"2026\", and "
            + "punctuation where the model hears it.",
            SettingKind.Bool, RestartRequired: true),

        new("Serval:Ai:Asr:NumThreads", GroupSpeech, "Transcription threads",
            "Processor threads for transcription. Transcription is fast; the reason to keep this "
            + "modest is competition with the description model rather than its own speed.",
            SettingKind.Int, RestartRequired: true, Min: 1, Max: 32, Advanced: true),

        // ---- Voice identification ------------------------------------------------------------
        // Separate from transcription because they answer different questions on different models
        // — what was said, against who said it — and because a camera can override the two gates
        // above and nothing here. A camera section can only carry a group's name honestly if the
        // overrides line up, so merging these two groups would break that.
        new("Serval:Ai:Speaker:Enabled", GroupVoices, "Tell voices apart",
            "Groups speech by who is speaking and follows a conversation across turns. A second set "
            + "of models on top of transcription.",
            SettingKind.Bool, RestartRequired: true),

        new("Serval:Ai:Speaker:LiveThreshold", GroupVoices, "Same-voice confidence, live",
            "How similar two pieces of speech must sound to be called the same person while a "
            + "conversation is happening. Too low merges two people; too high splits one person "
            + "into several.",
            SettingKind.Number, Min: 0, Max: 1, Advanced: true),

        new("Serval:Ai:Speaker:ClusterThreshold", GroupVoices, "Same-voice confidence, afterwards",
            "The same judgement made again once a conversation has finished, when the whole "
            + "recording is available. Higher than the live figure because there is more to go on.",
            SettingKind.Number, Min: 0, Max: 1, Advanced: true),

        new("Serval:Ai:Speaker:MinSecondsToRegister", GroupVoices, "Need this much speech to identify",
            "How much of a voice must be heard before it is worth trying to identify at all. A "
            + "single word is not enough to tell two people apart.",
            SettingKind.Number, Min: 0, Max: 30, Unit: "seconds"),

        new("Serval:Ai:Speaker:SilenceTimeoutMinutes", GroupVoices,
            "Conversation ends after silence of",
            "How long a room must stay quiet before an open conversation is considered finished and "
            + "written up.",
            SettingKind.Number, Min: 0.1, Max: 120, Unit: "minutes"),

        new("Serval:Ai:Speaker:MaxConversationMinutes", GroupVoices, "Longest conversation",
            "A hard cut so a room that is never quiet still produces finished transcripts instead of "
            + "one conversation that never ends.",
            SettingKind.Number, Min: 1, Max: 1440, Unit: "minutes"),

        // ---- Sound recognition --------------------------------------------------------------------
        new("Serval:Ai:Sound:Enabled", GroupSound, "Listen for sounds",
            "Identifies non-speech sounds — glass, alarms, dogs, vehicles. Runs alongside speech "
            + "rather than behind it, since speech detection rejects everything that is not a voice "
            + "and a car horn would never reach it.",
            SettingKind.Bool, RestartRequired: true),

        new("Serval:Ai:Sound:ModelPath", GroupSound, "Sound model file",
            "Path to the audio tagging weights inside the container.",
            SettingKind.Text, RestartRequired: true, Advanced: true),

        new("Serval:Ai:Sound:LabelsPath", GroupSound, "Sound labels file",
            "Path to the class list that ships with those weights.",
            SettingKind.Text, RestartRequired: true, Advanced: true),

        new("Serval:Ai:Sound:NumThreads", GroupSound, "Sound threads",
            "Kept low for the same reason as the detector's: what bites is competition with the "
            + "description model, not this model's own speed.",
            SettingKind.Int, RestartRequired: true, Min: 1, Max: 32, Advanced: true),

        new("Serval:Ai:Sound:Gate:Enabled", GroupSound, "Skip silence before listening",
            "This path's own loudness gate, separate from the speech one because the two want "
            + "different tuning.",
            SettingKind.Bool),

        new("Serval:Ai:Sound:Gate:RmsThreshold", GroupSound, "Counts as silence below",
            "How quiet audio must be before sound recognition ignores it. Cameras can set their own.",
            SettingKind.Number, Min: 0, Max: 1),

        new("Serval:Ai:Sound:Gate:PreRollWindows", GroupSound, "Keep this much audio from before",
            "More than the speech gate keeps, because the beginning of a bang is the most "
            + "informative part of it.",
            SettingKind.Int, Min: 0, Max: 100, Unit: "windows", Advanced: true),

        new("Serval:Ai:Sound:Gate:HangoverSeconds", GroupSound, "Keep listening after silence for",
            "How long to keep recording a sound after it quietens. Longer than the speech gate's: "
            + "here this decides the tail of the clip being identified, and a bark cut short is a "
            + "bark the model has too little of to place.",
            SettingKind.Number, Min: 0, Max: 30, Unit: "seconds", Advanced: true),

        new("Serval:Ai:Sound:MinSegmentSeconds", GroupSound, "Ignore sounds shorter than",
            "Clips shorter than this are thrown away — a single click identifies as noise.",
            SettingKind.Number, Min: 0, Max: 30, Unit: "seconds"),

        new("Serval:Ai:Sound:MaxSegmentSeconds", GroupSound, "Cut sounds longer than",
            "A hard cut for a sound that never stops. The model is trained on ten-second clips, and "
            + "without this a running lawnmower would hold the gate open and never produce a record "
            + "at all.",
            SettingKind.Number, Min: 1, Max: 60, Unit: "seconds"),

        new("Serval:Ai:Sound:MinConfidence", GroupSound, "How sure it must be",
            "How sure the model must be before writing a sound down. Low on purpose — a wrong label "
            + "here costs one row in a feed.",
            SettingKind.Number, Min: 0, Max: 1),

        new("Serval:Ai:Sound:AlertMinConfidence", GroupSound, "How sure it must be to alert",
            "Higher than the ordinary floor, and not the same bet: a false \"dog\" costs a feed row, "
            + "a false \"gunshot\" costs trust in every alert after it.",
            SettingKind.Number, Min: 0, Max: 1),

        new("Serval:Ai:Sound:CooldownSeconds", GroupSound, "Wait before reporting the same sound",
            "Minimum gap between two records of the same sound, counted per label so a dog and a "
            + "siren never silence each other. Load-bearing rather than tidy: a noisy outdoor "
            + "camera can hold the gate open indefinitely, and this is the only thing between that "
            + "and a record every few seconds, forever.",
            SettingKind.Number, Min: 0, Max: 3600, Unit: "seconds"),

        new("Serval:Ai:Sound:AlertCooldownSeconds", GroupSound,
            "Wait before alerting on the same sound",
            "Shorter than the gap above: a repeated alarm is information, a repeated dog is not.",
            SettingKind.Number, Min: 0, Max: 3600, Unit: "seconds"),

        new("Serval:Ai:Sound:TopK", GroupSound, "Guesses to keep for each sound",
            "How many candidate labels to keep. The best one decides the record; the rest are stored "
            + "so a threshold can be worked out later from real recordings rather than guessed again.",
            SettingKind.Int, Min: 1, Max: 20, Advanced: true),

        new("Serval:Ai:Sound:AlertLabels", GroupSound, "Alert on these sounds",
            "Sounds worth raising an alert for, spelled exactly as the model spells them. Prefer the "
            + "general name over the specific one — the labels are hierarchical and the general one "
            + "usually scores higher, so a list naming only \"Civil defense siren\" never matches a "
            + "siren that came back as \"Siren\".",
            SettingKind.TextList, Fallback: SoundOptions.DefaultAlertLabels),

        new("Serval:Ai:Sound:IgnoredLabels", GroupSound, "Never report these sounds",
            "Labels dropped before a winner is picked. Speech and sound are recognised independently "
            + "over the same audio and are meant to overlap — a conversation producing both a "
            + "transcript and a \"Speech\" sound is not a fault — so this is an escape hatch for a "
            + "label a particular site finds noisy, not routine tidying.",
            SettingKind.TextList),
    ];

    private static readonly Dictionary<string, SettingDescriptor> ByKey =
        All.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);

    public static SettingDescriptor? Find(string key) =>
        ByKey.TryGetValue(key, out SettingDescriptor? descriptor) ? descriptor : null;

    /// <summary>
    /// Whether a configuration path is one the settings API may write. This is the allowlist that
    /// keeps the environment-only settings environment-only.
    ///
    /// <para>List settings are stored as indexed children (<c>…:AlertLabels:0</c>), so a key whose
    /// parent is a <see cref="SettingKind.TextList"/> is writable too.</para>
    /// </summary>
    public static bool IsWritable(string key)
    {
        if (ByKey.ContainsKey(key))
        {
            return true;
        }

        int separator = key.LastIndexOf(':');
        return separator > 0
            && Find(key[..separator]) is { Kind: SettingKind.TextList }
            && int.TryParse(key[(separator + 1)..], out _);
    }

    /// <summary>
    /// Checks one stored overlay key and value — a key in the form the overlay actually holds,
    /// which is either a scalar path or an indexed child of a list.
    ///
    /// <para>The entry point for anything validating a key it did not construct: a restored
    /// configuration backup, or a hand-edited settings document. <see cref="Validate"/> takes a
    /// descriptor, and an indexed child like <c>Serval:Ai:Sound:AlertLabels:0</c> has none — so a
    /// caller holding only a key has nowhere to go. Resolving the key here rather than at each
    /// caller is what keeps the answer the same in every write path.</para>
    /// </summary>
    public static string? ValidateStored(string key, string? value)
    {
        if (Find(key) is { } descriptor)
        {
            // A list is stored as indexed children, so the bare parent key is a shape the overlay
            // never writes. Reaching here means a malformed file, not a value out of range.
            return descriptor.Kind == SettingKind.TextList
                ? $"{descriptor.Label} is a list, and is stored one entry per key "
                  + $"({descriptor.Key}:0, {descriptor.Key}:1, …). '{key}' on its own holds no value."
                : Validate(descriptor, value);
        }

        int separator = key.LastIndexOf(':');
        if (separator > 0
            && Find(key[..separator]) is { Kind: SettingKind.TextList } parent
            && int.TryParse(key[(separator + 1)..], out int index)
            && index >= 0)
        {
            return ValidateListEntry(parent, value);
        }

        return $"'{key}' is not a setting this Server can change. It may be environment-only, or "
            + "from a newer version of Serval.";
    }

    /// <summary>
    /// Checks one entry of a <see cref="SettingKind.TextList"/>. Separate from
    /// <see cref="Validate"/> because a list's constraints are on its entries, not on the list:
    /// <see cref="SettingDescriptor.Choices"/> on a list means "each entry must be one of these",
    /// which is a check <see cref="Validate"/>'s <c>TextList</c> arm has no single value to make.
    /// </summary>
    public static string? ValidateListEntry(SettingDescriptor descriptor, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return $"{descriptor.Label} cannot contain a blank entry.";
        }

        return descriptor.Choices is { } choices && !choices.Contains(value, StringComparer.OrdinalIgnoreCase)
            ? $"{descriptor.Label} does not accept '{value}'. Allowed: {string.Join(", ", choices)}."
            : null;
    }

    /// <summary>
    /// Checks one value against its descriptor, returning an error to show the user or null when it
    /// is fine. Pure, so it is testable without a database or a host.
    ///
    /// <para>A <see cref="SettingKind.TextList"/> checked here is the whole list, and there is no
    /// whole-list value to check — the entries are what carry the constraints. Use
    /// <see cref="ValidateListEntry"/> for those.</para>
    /// </summary>
    public static string? Validate(SettingDescriptor descriptor, string? value)
    {
        // Null means "remove this override and use the deployment default", which is always legal.
        if (value is null)
        {
            return null;
        }

        switch (descriptor.Kind)
        {
            case SettingKind.Bool:
                return bool.TryParse(value, out _)
                    ? null
                    : $"{descriptor.Label} must be true or false.";

            case SettingKind.Int:
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole))
                {
                    return $"{descriptor.Label} must be a whole number.";
                }

                return Range(descriptor, whole);

            case SettingKind.Number:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                {
                    return $"{descriptor.Label} must be a number.";
                }

                return Range(descriptor, number);

            case SettingKind.Choice:
                return descriptor.Choices is { } choices && !choices.Contains(value, StringComparer.OrdinalIgnoreCase)
                    ? $"{descriptor.Label} must be one of: {string.Join(", ", choices)}."
                    : null;

            case SettingKind.Text:
            case SettingKind.TextList:
            default:
                return string.IsNullOrWhiteSpace(value)
                    ? $"{descriptor.Label} cannot be blank. Reset it instead to use the default."
                    : null;
        }
    }

    private static string? Range(SettingDescriptor descriptor, double value)
    {
        string unit = descriptor.Unit is null ? "" : " " + descriptor.Unit;

        if (descriptor.Min is { } min && value < min)
        {
            return $"{descriptor.Label} cannot be below {Format(min)}{unit}.";
        }

        return descriptor.Max is { } max && value > max
            ? $"{descriptor.Label} cannot be above {Format(max)}{unit}."
            : null;
    }

    private static string Format(double value) =>
        value == Math.Floor(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);
}
