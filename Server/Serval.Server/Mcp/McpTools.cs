using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using MongoDB.Bson;
using Serval.Contracts;
using Serval.Server.Alerts;
using Serval.Server.Cameras;
using Serval.Server.Clips;
using Serval.Server.Media;
using Serval.Server.Snapshots;
using Serval.Server.Telemetry;

namespace Serval.Server.Mcp;

/// <summary>
/// The tools an assistant can call. Every one is read-only — nothing here writes to Mongo, moves a
/// camera, or so much as marks an alert read — and the annotations say so, which lets a client
/// skip asking the person before each call.
///
/// <para><b>Shaped for a model, not mirrored from the REST API.</b> The REST routes are one camera
/// and one kind at a time because that is how the App draws them; a question like "what happened
/// this afternoon" spans every camera and every kind, so <c>get_activity</c> merges them into one
/// timeline. Fields a model cannot use (track samples, bounding boxes, schema versions) are left
/// out, which keeps a day's activity inside a context window.</para>
///
/// <para><b>Bad arguments are <see cref="McpException"/>s</b>, which the SDK returns to the model as
/// a tool error carrying the message, so it can correct itself — "unknown camera, call
/// list_cameras" rather than a generic failure. Any other exception is reported without detail.</para>
/// </summary>
[McpServerToolType]
public static class McpTools
{
    private static readonly TimeSpan DefaultActivityWindow = TimeSpan.FromHours(24);
    private static readonly TimeSpan DefaultSearchWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// The longest window one call may cover. Long enough for "last week"; short enough that a
    /// text search, which reads every record in the window, stays quick on a small box with ten
    /// cameras. A longer question is several calls.
    /// </summary>
    internal static readonly TimeSpan MaxWindow = TimeSpan.FromDays(7);

    internal static readonly IReadOnlyList<string> AllKinds = ["scene", "detection", "speech", "conversation", "sound"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---------------------------------------------------------------- cameras

    [McpServerTool(Name = "list_cameras", Title = "List cameras", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Lists every camera on this Serval with its id, name and location, plus the server's current time "
        + "and time zone. Call this first: every other tool takes camera ids from here, and the current time "
        + "is what to measure \"this morning\" or \"an hour ago\" against. Each camera's url opens it live in "
        + "the Serval app.")]
    public static async Task<string> ListCameras(CameraRepository cameras, McpGate gate, CancellationToken ct)
    {
        List<Camera> all = await cameras.ListAsync(ct);
        string? issuer = gate.Issuer;

        return Serialize(new
        {
            now = DateTimeOffset.Now,
            timeZone = TimeZoneInfo.Local.Id,
            cameras = all.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => new
            {
                id = c.Id,
                name = c.Name,
                location = c.Location,
                enabled = c.Enabled,
                recording = c.Recording,
                analysesVideo = c.AiVision,
                analysesAudio = c.AiAudio,
                url = McpLinks.Camera(issuer, c.Id),
            }),
        });
    }

    // --------------------------------------------------------------- activity

    [McpServerTool(Name = "get_activity", Title = "Get camera activity", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Everything the cameras noticed in a time window, newest first, as one timeline: scene descriptions "
        + "(prose written by a vision model about what a camera saw), detections (objects such as person, car "
        + "or dog, with how long each stayed), speech (individual transcribed utterances), conversations "
        + "(finished multi-speaker transcripts) and sounds (non-speech audio such as a doorbell or a dog "
        + "barking). Covers every camera unless cameraIds narrows it. A detection with no endedAt was still "
        + "present at the end of the window. When truncated is true there was more than limit; narrow the "
        + "window, the cameras or the kinds rather than raising limit. Each item's url opens that camera's "
        + "recording a few seconds before the moment, in the Serval app.")]
    public static async Task<string> GetActivity(
        CameraRepository cameras,
        TelemetryRepository telemetry,
        McpGate gate,
        [Description("Start of the window, ISO-8601 with an offset. Defaults to 24 hours before 'to'.")] DateTimeOffset? from = null,
        [Description("End of the window, ISO-8601 with an offset. Defaults to now. At most 7 days after 'from'.")] DateTimeOffset? to = null,
        [Description("Camera ids from list_cameras. Omit for every camera.")] string[]? cameraIds = null,
        [Description("Any of: scene, detection, speech, conversation, sound. Omit for all of them.")] string[]? kinds = null,
        [Description("Most records to return, 1-500. Default 100.")] int limit = 100,
        CancellationToken ct = default)
    {
        (DateTimeOffset f, DateTimeOffset t) = Window(from, to, DefaultActivityWindow);
        return await TimelineAsync(cameras, telemetry, gate.Issuer, f, t, cameraIds, kinds, text: null, Math.Clamp(limit, 1, 500), ct);
    }

    [McpServerTool(Name = "search_activity", Title = "Search camera activity", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Finds activity whose text contains a word or phrase (case-insensitive, literal — not a regular "
        + "expression and not semantic): scene descriptions, speech transcripts, conversation transcripts, and "
        + "the labels of detections and sounds. Use it for \"when did someone mention the package\" or \"was a "
        + "red truck seen\". Descriptions are a model's prose, so try the plain word a describer would use "
        + "(\"truck\", not \"pickup\"), and try synonyms if the first search finds nothing. Searches the last "
        + "7 days unless from/to say otherwise; a window may be at most 7 days, so search further back in "
        + "several calls. Each item carries the same url as in get_activity.")]
    public static async Task<string> SearchActivity(
        CameraRepository cameras,
        TelemetryRepository telemetry,
        McpGate gate,
        [Description("The word or phrase to find.")] string query,
        [Description("Start of the window, ISO-8601 with an offset. Defaults to 7 days before 'to'.")] DateTimeOffset? from = null,
        [Description("End of the window, ISO-8601 with an offset. Defaults to now.")] DateTimeOffset? to = null,
        [Description("Camera ids from list_cameras. Omit for every camera.")] string[]? cameraIds = null,
        [Description("Any of: scene, detection, speech, conversation, sound. Omit for all of them.")] string[]? kinds = null,
        [Description("Most matches to return, 1-200. Default 50.")] int limit = 50,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new McpException("query is empty. Give a word or phrase to look for.");
        }

        (DateTimeOffset f, DateTimeOffset t) = Window(from, to, DefaultSearchWindow);
        return await TimelineAsync(cameras, telemetry, gate.Issuer, f, t, cameraIds, kinds, query, Math.Clamp(limit, 1, 200), ct);
    }

    private static async Task<string> TimelineAsync(
        CameraRepository cameraRepository,
        TelemetryRepository telemetry,
        string? issuer,
        DateTimeOffset from,
        DateTimeOffset to,
        string[]? cameraIds,
        string[]? kinds,
        string? text,
        int limit,
        CancellationToken ct)
    {
        Dictionary<string, string> names = await CameraNamesAsync(cameraRepository, ct);
        string[] ids = SelectCameras(names, cameraIds);
        HashSet<string> wanted = SelectKinds(kinds);

        // One more than asked for from each kind, so a merge that comes up exactly at the limit can
        // still say whether anything was left behind.
        int each = limit + 1;
        var items = new List<ActivityItem>();

        if (wanted.Contains("scene"))
        {
            items.AddRange((await telemetry.FindScenesAsync(ids, from, to, text, each, ct))
                .Select(s => new ActivityItem("scene", s.Timestamp, s.CameraId, Name(names, s.CameraId))
                {
                    Text = s.Description,
                    Trigger = s.Trigger,
                }));
        }

        if (wanted.Contains("detection"))
        {
            items.AddRange((await telemetry.FindDetectionsAsync(ids, from, to, text, each, ct))
                .Select(d => new ActivityItem("detection", d.Timestamp, d.CameraId, Name(names, d.CameraId))
                {
                    Label = d.Label,
                    EndedAt = d.EndedAt,
                    Confidence = Math.Round(d.PeakConfidence, 2),
                    IsAlert = d.IsAlert ? true : null,
                }));
        }

        if (wanted.Contains("speech"))
        {
            items.AddRange((await telemetry.FindUtterancesAsync(ids, from, to, text, each, ct))
                .Select(u => new ActivityItem("speech", u.Timestamp, u.CameraId, Name(names, u.CameraId))
                {
                    Text = u.Transcript,
                    Speaker = u.Speaker,
                    Language = u.Language,
                    DurationSeconds = Math.Round(u.DurationSeconds, 1),
                }));
        }

        if (wanted.Contains("conversation"))
        {
            items.AddRange((await telemetry.FindConversationTranscriptsAsync(ids, from, to, text, each, ct))
                .Select(c => new ActivityItem("conversation", c.StartedAt, c.CameraId, Name(names, c.CameraId))
                {
                    Text = c.Text,
                    Speakers = c.SpeakerCount,
                    Language = c.Language,
                    DurationSeconds = Math.Round(c.AudioSeconds, 1),
                }));
        }

        if (wanted.Contains("sound"))
        {
            items.AddRange((await telemetry.FindSoundsAsync(ids, from, to, text, each, ct))
                .Select(s => new ActivityItem("sound", s.Timestamp, s.CameraId, Name(names, s.CameraId))
                {
                    Label = s.Label,
                    Confidence = Math.Round(s.Confidence, 2),
                    IsAlert = s.IsAlert ? true : null,
                    DurationSeconds = Math.Round(s.DurationSeconds, 1),
                }));
        }

        List<ActivityItem> newest =
        [
            .. items.OrderByDescending(i => i.At).Take(limit).Select(i => i with
            {
                Url = i.CameraId is null ? null : McpLinks.CameraAt(issuer, i.CameraId, i.At),
            }),
        ];

        return Serialize(new { from, to, query = text, truncated = items.Count > limit, items = newest });
    }

    // ----------------------------------------------------------------- alerts

    [McpServerTool(Name = "list_alerts", Title = "List alerts", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "The alert queue, newest first: the detections and sounds this household chose to be alerted about "
        + "(a person at the door, a smoke alarm), excluding any somebody dismissed. Reading them here does not "
        + "mark them read. Page backwards by passing the returned nextBefore as 'before'. get_alert_image shows "
        + "the frame an alert fired on. Each alert's url opens its card in the Serval app, which can play the "
        + "recording.")]
    public static async Task<string> ListAlerts(
        AlertRepository alerts,
        CameraRepository cameras,
        McpGate gate,
        [Description("A camera id from list_cameras. Omit for every camera.")] string? cameraId = null,
        [Description("Only alerts strictly before this instant, ISO-8601. For paging.")] DateTimeOffset? before = null,
        [Description("Most alerts to return, 1-100. Default 25.")] int limit = 25,
        CancellationToken ct = default)
    {
        Dictionary<string, string> names = await CameraNamesAsync(cameras, ct);
        if (cameraId is not null)
        {
            SelectCameras(names, [cameraId]);
        }

        int take = Math.Clamp(limit, 1, 100);
        List<Alert> page = await alerts.ListAsync(cameraId, take, before, ct);
        string? issuer = gate.Issuer;

        return Serialize(new
        {
            alerts = page.Select(a => new
            {
                id = a.Id,
                cameraId = a.CameraId,
                camera = Name(names, a.CameraId),
                kind = a.Kind == AlertKind.Sound ? "sound" : "object",
                label = a.Label,
                title = a.Title,
                at = a.At,
                read = a.Read,
                url = McpLinks.Alert(issuer, a.Id),
            }),
            nextBefore = page.Count == take ? page[^1].At : (DateTimeOffset?)null,
        });
    }

    // ------------------------------------------------------------------ clips

    [McpServerTool(Name = "list_clips", Title = "List saved clips", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "Clips somebody deliberately saved, newest first, with a summary of what each shows where one has "
        + "been written. Saved clips are kept forever, unlike ordinary recordings, so they are where to look "
        + "for something older than retention. 'query' matches the clip's name and what was said in it. "
        + "get_clip_image shows a still from one. Each clip's url plays it in the Serval app.")]
    public static async Task<string> ListClips(
        ClipRepository clips,
        CameraRepository cameras,
        McpGate gate,
        [Description("Text to find in the clip's name or transcript.")] string? query = null,
        [Description("A camera id from list_cameras. Omit for every camera.")] string? cameraId = null,
        [Description("Most clips to return, 1-100. Default 25.")] int limit = 25,
        CancellationToken ct = default)
    {
        if (cameraId is not null)
        {
            SelectCameras(await CameraNamesAsync(cameras, ct), [cameraId]);
        }

        List<SavedClip> found = await clips.ListAsync(query, cameraId, ct);
        string? issuer = gate.Issuer;

        return Serialize(new
        {
            clips = found.Take(Math.Clamp(limit, 1, 100)).Select(c => new
            {
                id = c.Id.ToString(),
                name = c.Name,
                cameraId = c.CameraId,
                camera = c.CameraName,
                from = c.From,
                to = c.To,
                durationSeconds = Math.Round(c.DurationSeconds, 1),
                summary = c.Summary,
                savedBy = c.SavedBy,
                savedAt = c.SavedAt,
                url = McpLinks.Clip(issuer, c.Id.ToString()),
            }),
            total = found.Count,
        });
    }

    // ----------------------------------------------------------------- images

    [McpServerTool(Name = "get_snapshot", Title = "Look through a camera", ReadOnly = true, Idempotent = false, OpenWorld = false)]
    [Description(
        "A still image from a camera: what it sees right now, or — given 'at' — what its footage shows at that "
        + "instant. Use it to check something a description mentioned, or to answer a question no text "
        + "covers. Past frames exist only where there is footage: recordings are kept for a limited number of "
        + "days, and a camera that is not recording keeps only about the last minute. The caption ends with a "
        + "url that opens the camera at that moment in the Serval app, as video.")]
    public static async Task<CallToolResult> GetSnapshot(
        CameraRepository cameras,
        SnapshotBroadcaster snapshots,
        RecordedFrameReader recorded,
        McpGate gate,
        [Description("A camera id from list_cameras.")] string cameraId,
        [Description("The instant to show, ISO-8601 with an offset. Omit for the live picture.")] DateTimeOffset? at = null,
        CancellationToken ct = default)
    {
        Dictionary<string, string> names = await CameraNamesAsync(cameras, ct);
        SelectCameras(names, [cameraId]);
        string name = names[cameraId];

        if (at is not { } instant)
        {
            Snapshot live = snapshots.Latest(cameraId)
                ?? throw new McpException(
                    $"{name} has no live picture yet. It may be disabled, offline, or still starting.");

            return Image(live.Jpeg, $"{name}, live, captured {live.CapturedAt:O}.", McpLinks.Camera(gate.Issuer, cameraId));
        }

        if (instant > DateTimeOffset.UtcNow)
        {
            throw new McpException("'at' is in the future. Omit it for the live picture.");
        }

        RecordedFrame frame = await recorded.ReadAsync(cameraId, instant, ct)
            ?? throw new McpException(
                $"No footage of {name} covers {instant:O}. The camera may not have been recording then, or "
                + "the footage has aged out.");

        return Image(frame.Jpeg, $"{name}, recorded, at {frame.At:O}.", McpLinks.CameraAt(gate.Issuer, cameraId, frame.At));
    }

    [McpServerTool(Name = "get_alert_image", Title = "Show an alert", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "The frame an alert fired on — the moment the object was most clearly seen — at full resolution where the "
        + "footage is still kept, otherwise as a small thumbnail. Takes an id from list_alerts. The caption ends "
        + "with the alert's url in the Serval app.")]
    public static async Task<CallToolResult> GetAlertImage(
        AlertRepository alerts,
        AlertStorage storage,
        RecordedFrameReader recorded,
        McpGate gate,
        [Description("An alert id from list_alerts.")] string alertId,
        CancellationToken ct = default)
    {
        Alert alert = await alerts.GetAsync(alertId, ct)
            ?? throw new McpException($"There is no alert {alertId}. Ids come from list_alerts.");

        string? url = McpLinks.Alert(gate.Issuer, alert.Id);

        // The stored poster is a 480×270 card thumbnail — too small to read a face or a label from.
        // The same instant from the recording is the picture worth sending, while it exists.
        if (await recorded.ReadAsync(alert.CameraId, alert.PeakAt, ct) is { } frame)
        {
            return Image(frame.Jpeg, $"{alert.Title}, at {frame.At:O}.", url);
        }

        byte[] poster = await ReadNonEmptyAsync(storage.PosterFor(alert.Id), ct)
            ?? throw new McpException("That alert has no picture. Its clip may still be being cut, or could not be.");

        return Image(poster, $"{alert.Title}, at {alert.At:O} (thumbnail; the footage is no longer kept).", url);
    }

    [McpServerTool(Name = "get_clip_image", Title = "Show a saved clip", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description(
        "A full-resolution still from the middle of a saved clip. Takes an id from list_clips. The caption ends "
        + "with the url that plays the clip in the Serval app.")]
    public static async Task<CallToolResult> GetClipImage(
        ClipRepository clips,
        ClipStorage storage,
        RecordedFrameReader recorded,
        McpGate gate,
        [Description("A clip id from list_clips.")] string clipId,
        CancellationToken ct = default)
    {
        if (!ObjectId.TryParse(clipId, out ObjectId id) || await clips.GetAsync(id, ct) is not { State: ClipState.Ready } clip)
        {
            throw new McpException($"There is no saved clip {clipId}. Ids come from list_clips.");
        }

        string caption = $"{clip.Name} ({clip.CameraName}), {clip.From:O} to {clip.To:O}.";
        string? url = McpLinks.Clip(gate.Issuer, clip.Id.ToString());

        string video = storage.VideoFor(clip.Id);
        if (File.Exists(video) && await recorded.ReadFileAsync(video, clip.DurationSeconds / 2, ct) is { } frame)
        {
            return Image(frame, caption, url);
        }

        byte[] poster = await ReadNonEmptyAsync(storage.PosterFor(clip.Id), ct)
            ?? throw new McpException("That clip has no picture.");

        return Image(poster, caption, url);
    }

    /// <summary>
    /// A file's bytes, or null when it is missing or empty. Empty matters: a failed ffmpeg seek can
    /// leave a zero-byte poster behind, and an empty image is a request a model API rejects
    /// outright — the same guard the alert and clip routes put in front of their posters.
    /// </summary>
    private static async Task<byte[]?> ReadNonEmptyAsync(string path, CancellationToken ct) =>
        new FileInfo(path) is { Exists: true, Length: > 0 } ? await File.ReadAllBytesAsync(path, ct) : null;

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// The window a call covers. Defaults are measured back from <c>to</c>, so asking for "the day
    /// before X" needs only <c>to</c>. Spans longer than <see cref="MaxWindow"/> are refused with a
    /// message saying so rather than quietly shortened, because a silently truncated answer to "did
    /// anyone come last month" reads as "no".
    /// </summary>
    internal static (DateTimeOffset From, DateTimeOffset To) Window(
        DateTimeOffset? from, DateTimeOffset? to, TimeSpan defaultSpan)
    {
        DateTimeOffset t = to ?? DateTimeOffset.UtcNow;
        DateTimeOffset f = from ?? t - defaultSpan;

        if (f > t)
        {
            throw new McpException("'from' is after 'to'.");
        }

        if (t - f > MaxWindow)
        {
            throw new McpException(
                $"That window is {(t - f).TotalDays:0.#} days; one call may cover at most {MaxWindow.TotalDays:0} days. "
                + "Split it into several calls.");
        }

        return (f, t);
    }

    internal static HashSet<string> SelectKinds(string[]? kinds)
    {
        if (kinds is null || kinds.Length == 0)
        {
            return [.. AllKinds];
        }

        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (string kind in kinds)
        {
            // Each kind and its plural spelled out: trimming a trailing 's' turns "speeches" into
            // "speeche".
            string normalised = kind.Trim().ToLowerInvariant() switch
            {
                "scene" or "scenes" => "scene",
                "detection" or "detections" => "detection",
                "speech" or "speeches" => "speech",
                "conversation" or "conversations" => "conversation",
                "sound" or "sounds" => "sound",
                _ => throw new McpException($"Unknown kind '{kind}'. Use any of: {string.Join(", ", AllKinds)}."),
            };

            selected.Add(normalised);
        }

        return selected;
    }

    /// <summary>The cameras a call covers: those named, all checked to exist, or every camera when none are.</summary>
    internal static string[] SelectCameras(IReadOnlyDictionary<string, string> names, string[]? cameraIds)
    {
        if (cameraIds is null || cameraIds.Length == 0)
        {
            return [.. names.Keys];
        }

        string[] unknown = [.. cameraIds.Where(id => !names.ContainsKey(id))];
        if (unknown.Length > 0)
        {
            throw new McpException($"Unknown camera id(s): {string.Join(", ", unknown)}. Call list_cameras for the ids.");
        }

        return [.. cameraIds.Distinct(StringComparer.Ordinal)];
    }

    private static async Task<Dictionary<string, string>> CameraNamesAsync(CameraRepository cameras, CancellationToken ct) =>
        (await cameras.ListAsync(ct)).ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);

    private static string Name(IReadOnlyDictionary<string, string> names, string? cameraId) =>
        cameraId is not null && names.TryGetValue(cameraId, out string? name) ? name : cameraId ?? "unknown";

    /// <summary>
    /// A picture and its caption. The link goes in the caption rather than a separate block because
    /// the caption is the one part every client passes to the model — some drop the image.
    /// </summary>
    internal static CallToolResult Image(byte[] jpeg, string caption, string? url) => new()
    {
        Content =
        [
            new TextContentBlock { Text = url is null ? caption : $"{caption} Watch it in Serval: {url}" },
            ImageContentBlock.FromBytes(jpeg, "image/jpeg"),
        ],
    };

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    /// <summary>One row of the merged timeline. Every kind sets <c>Text</c> or <c>Label</c>; the rest is per kind.</summary>
    internal sealed record ActivityItem(string Kind, DateTimeOffset At, string? CameraId, string Camera)
    {
        public DateTimeOffset? EndedAt { get; init; }
        public string? Text { get; init; }
        public string? Label { get; init; }
        public double? Confidence { get; init; }
        public bool? IsAlert { get; init; }
        public string? Speaker { get; init; }
        public int? Speakers { get; init; }
        public string? Language { get; init; }
        public double? DurationSeconds { get; init; }
        public string? Trigger { get; init; }
        public string? Url { get; init; }
    }
}
