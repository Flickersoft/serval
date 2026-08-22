using System.Text.Json;
using Serval.Ai;
using Serval.Server.Auth;
using Serval.Server.Backup;
using Serval.Server.Cameras;
using Serval.Server.Preferences;

namespace Serval.Server.Tests;

/// <summary>
/// What survives the trip out to a file and back.
///
/// <para>The backup carries the registry's own <c>Camera</c> rather than a copy of it, which buys
/// one thing worth pinning: a camera in the file is the same document the API speaks. The risk that
/// comes with it is enum shape — a <c>StreamRole</c> written as <c>0</c> instead of
/// <c>"record"</c> would bake this enum's declaration order into every file ever written, and would
/// silently reassign roles the day somebody reorders it. Same for <c>Role</c>. Those two round
/// trips are what these tests are mostly for.</para>
/// </summary>
public class ConfigBackupFormatTests
{
    private static ConfigBackupFile Sample(
        IReadOnlyList<Camera>? cameras = null,
        IReadOnlyList<BackupUser>? users = null,
        IReadOnlyDictionary<string, string>? settings = null,
        IReadOnlyList<BackupPreferences>? preferences = null) =>
        new(
            Kind: ConfigBackupFile.FileKind,
            Warning: ConfigBackupFile.SecretWarning,
            CreatedAt: new DateTimeOffset(2026, 8, 8, 14, 3, 11, TimeSpan.Zero),
            CreatedBy: "jeremiah",
            CreatedOn: "serval-test",
            Settings: settings ?? new Dictionary<string, string>(),
            Cameras: cameras ?? [],
            Users: users ?? [],
            Preferences: preferences ?? []);

    private static ConfigBackupFile RoundTrip(ConfigBackupFile file) =>
        JsonSerializer.Deserialize<ConfigBackupFile>(
            JsonSerializer.Serialize(file, ConfigBackupFile.Json), ConfigBackupFile.Json)!;

    /// <summary>
    /// Somebody who opens this file in whatever they had to hand should learn what it is and what
    /// is in it before they read any of it. That is a property of the field order, so it is a
    /// property worth a test — a reordered record would still round-trip perfectly.
    /// </summary>
    [Fact]
    public void What_the_file_is_and_what_it_holds_come_first()
    {
        using JsonDocument document = JsonDocument.Parse(
            JsonSerializer.Serialize(Sample(), ConfigBackupFile.Json));

        string[] keys = [.. document.RootElement.EnumerateObject().Select(p => p.Name).Take(2)];

        Assert.Equal(["kind", "warning"], keys);
        Assert.Contains("PLAIN TEXT", document.RootElement.GetProperty("warning").GetString());
    }

    /// <summary>
    /// <b>The Google Home link and its credentials are not in a backup, and must not become so.</b>
    /// They are excluded by construction — the file is a fixed record of four sections and none of
    /// them is a token store — so this pins a property nothing else would notice being lost.
    ///
    /// <para>The reasoning, since the obvious instinct is that a backup should hold everything.
    /// Re-linking in the Google Home app takes thirty seconds, so almost nothing is saved. What it
    /// would cost is worse than that trade: these are live bearer tokens against an endpoint that
    /// is, by this feature's nature, reachable from the public internet — and a backup restored
    /// onto a second machine would leave two deployments both believing they own the same Google
    /// account's cameras, with the losing one issuing signaling tickets for cameras it does not
    /// have. Same register as <c>VapidKeyStore</c>, which opts out for its own reasons.</para>
    /// </summary>
    [Fact]
    public void No_Google_Home_credential_travels_in_a_backup()
    {
        using JsonDocument document = JsonDocument.Parse(
            JsonSerializer.Serialize(Sample(), ConfigBackupFile.Json));

        string[] sections = [.. document.RootElement.EnumerateObject().Select(p => p.Name)];

        Assert.Equal(
            ["kind", "warning", "createdAt", "createdBy", "createdOn", "settings", "cameras", "users", "preferences"],
            sections);

        // And the environment-only keys cannot arrive through the settings overlay either, since
        // the catalogue refuses to store them — SettingsCatalogTests pins that end of it.
        Assert.DoesNotContain(
            "GoogleHome",
            JsonSerializer.Serialize(Sample(), ConfigBackupFile.Json),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A notification rule's three inheriting fields all mean something by being absent, and a
    /// backup that flattened any of them would restore somebody a different set of rules from the
    /// one they had. Null must come back null rather than as a zero or an empty list.
    /// </summary>
    [Fact]
    public void A_notification_rules_choices_survive_a_round_trip()
    {
        ConfigBackupFile file = RoundTrip(Sample(preferences:
        [
            new BackupPreferences(
                UserId: "jeremiah",
                WallLayout: [],
                NotificationsEnabled: true,
                Notifications:
                [
                    new CameraNotificationRulePayload(
                        "front-door", true, ["person"], null, 900),
                    new CameraNotificationRulePayload(
                        "driveway", true, null, null, 0),
                    new CameraNotificationRulePayload(
                        "back-yard", true, null, [], null),
                ]),
        ]));

        IReadOnlyList<CameraNotificationRulePayload> rules = file.Preferences[0].Notifications;

        Assert.Equal(900, rules[0].CooldownSeconds);
        Assert.Equal(["person"], rules[0].ObjectClasses!);
        Assert.Null(rules[0].SoundLabels);

        // Zero is a decision to always be notified; null is never having made one.
        Assert.Equal(0, rules[1].CooldownSeconds);
        Assert.Null(rules[2].CooldownSeconds);
        Assert.Empty(rules[2].SoundLabels!);
    }

    [Fact]
    public void Stream_roles_travel_by_name_not_by_number()
    {
        var camera = new Camera
        {
            Id = "front-door",
            Name = "Front door",
            Streams =
            [
                new CameraStream { Name = "main", Url = "rtsp://host/main", Roles = [StreamRole.Record, StreamRole.Live] },
                new CameraStream { Name = "sub", Url = "rtsp://host/sub", Roles = [StreamRole.Detect] },
            ],
        };

        string json = JsonSerializer.Serialize(Sample([camera]), ConfigBackupFile.Json);

        Assert.Contains("\"record\"", json, StringComparison.Ordinal);
        Assert.Contains("\"detect\"", json, StringComparison.Ordinal);
        Assert.Contains("\"live\"", json, StringComparison.Ordinal);

        Camera restored = Assert.Single(RoundTrip(Sample([camera])).Cameras);
        Assert.Equal([StreamRole.Record, StreamRole.Live], restored.Streams[0].Roles);
        Assert.Equal([StreamRole.Detect], restored.Streams[1].Roles);
    }

    [Fact]
    public void Account_roles_travel_by_name_not_by_number()
    {
        BackupUser admin = new("jeremiah", "Jeremiah", "AQAAAA==", Role.Admin, DateTimeOffset.UtcNow);
        BackupUser viewer = new("sam", "Sam", "AQAAAA==", Role.Viewer, DateTimeOffset.UtcNow);

        string json = JsonSerializer.Serialize(Sample(users: [admin, viewer]), ConfigBackupFile.Json);
        Assert.Contains("\"admin\"", json, StringComparison.Ordinal);
        Assert.Contains("\"viewer\"", json, StringComparison.Ordinal);

        IReadOnlyList<BackupUser> restored = RoundTrip(Sample(users: [admin, viewer])).Users;
        Assert.Equal(Role.Admin, restored[0].Role);
        Assert.Equal(Role.Viewer, restored[1].Role);
    }

    /// <summary>
    /// Every per-camera override, out and back. These are the fields somebody spent an evening
    /// tuning against one view; losing one silently is the failure this whole feature exists to
    /// prevent.
    /// </summary>
    [Fact]
    public void A_fully_tuned_camera_survives_intact()
    {
        var camera = new Camera
        {
            Id = "garage",
            Name = "Garage",
            Location = "Side",
            Streams = [new CameraStream
            {
                Name = "main",
                Url = "rtsp://admin:hunter2@192.168.1.50/Streaming/101",
                Roles = [StreamRole.Record, StreamRole.Detect, StreamRole.Live],
                Transcode = new StreamTranscode { Codec = "h264", Bitrate = "4M" },
            }],
            Enabled = false,
            RetentionDays = 30,
            OnvifUrl = "http://192.168.1.50/onvif/device_service",
            OnvifUsername = "admin",
            OnvifPassword = "hunter2",
            OnvifProfileToken = "Profile_1",
            TwoWayAudio = true,
            RecordAudio = true,
            AiVision = true,
            AiAudio = true,
            AudioTuning = new CameraAudioTuning { VadThreshold = 0.62 },
            DetectionTuning = new CameraDetectionTuning
            {
                Classes = ["person", "car"],
                AlertClasses = ["person"],
                ScoreThreshold = 0.45,
                Masks = [new DetectionMask { Name = "Pavement", Points = [0.1, 0.1, 0.9, 0.1, 0.9, 0.4] }],
            },
            SoundTuning = new CameraSoundTuning { AlertLabels = ["Glass"], MinConfidence = 0.5 },
            MotionTuning = new CameraMotionTuning { PixelDelta = 18 },
        };

        Camera restored = Assert.Single(RoundTrip(Sample([camera])).Cameras);

        Assert.Equal("hunter2", restored.OnvifPassword);
        Assert.Equal("rtsp://admin:hunter2@192.168.1.50/Streaming/101", restored.Streams[0].Url);
        Assert.Equal("4M", restored.Streams[0].Transcode!.Bitrate);
        Assert.False(restored.Enabled);
        Assert.Equal(30, restored.RetentionDays);
        Assert.Equal(0.62, restored.AudioTuning!.VadThreshold);
        Assert.Equal(["person", "car"], restored.DetectionTuning!.Classes!);
        Assert.Equal(0.45, restored.DetectionTuning.ScoreThreshold);
        Assert.Equal([0.1, 0.1, 0.9, 0.1, 0.9, 0.4], restored.DetectionTuning.Masks![0].Points);
        Assert.Equal("Pavement", restored.DetectionTuning.Masks[0].Name);
        Assert.Equal(["Glass"], restored.SoundTuning!.AlertLabels!);
        Assert.Equal(18, restored.MotionTuning!.PixelDelta);
    }

    /// <summary>
    /// <c>PtzConfigured</c> is derived from the ONVIF URL and has no setter, so it is written for a
    /// reader's benefit and dropped on the way back in. Pinned because the tempting tidy-up — hiding
    /// it from the file — would change what <c>GET /api/cameras</c> returns, and the file is
    /// deliberately the same shape as that.
    /// </summary>
    [Fact]
    public void The_derived_ptz_flag_is_written_and_ignored_on_the_way_back()
    {
        var camera = new Camera
        {
            Id = "front-door",
            Name = "Front door",
            OnvifUrl = "http://192.168.1.50/onvif/device_service",
            Streams = [new CameraStream { Name = "main", Url = "rtsp://host/main", Roles = [StreamRole.Record] }],
        };

        Assert.Contains("\"ptzConfigured\": true",
            JsonSerializer.Serialize(Sample([camera]), ConfigBackupFile.Json), StringComparison.Ordinal);

        Assert.True(Assert.Single(RoundTrip(Sample([camera])).Cameras).PtzConfigured);
    }

    /// <summary>
    /// The overlay travels in the form it is stored in — colon keys and string values, indexed list
    /// children included — so that no translation sits between the file and the document.
    /// </summary>
    [Fact]
    public void The_settings_overlay_travels_as_stored()
    {
        Dictionary<string, string> overlay = new()
        {
            ["Serval:Media:RetentionDays"] = "21",
            ["Serval:Ai:Sound:AlertLabels:0"] = "Glass",
            ["Serval:Ai:Sound:AlertLabels:1"] = "Siren",
        };

        IReadOnlyDictionary<string, string> restored = RoundTrip(Sample(settings: overlay)).Settings;

        Assert.Equal("21", restored["Serval:Media:RetentionDays"]);
        Assert.Equal("Glass", restored["Serval:Ai:Sound:AlertLabels:0"]);
        Assert.Equal("Siren", restored["Serval:Ai:Sound:AlertLabels:1"]);
    }

    [Fact]
    public void The_file_name_sorts_by_when_it_was_taken()
    {
        var at = new DateTimeOffset(2026, 8, 8, 14, 3, 11, TimeSpan.Zero);
        string name = ConfigBackupFile.FileNameFor(at);

        Assert.StartsWith("serval-config-", name, StringComparison.Ordinal);
        Assert.EndsWith(".json", name, StringComparison.Ordinal);
        Assert.Contains(at.ToLocalTime().ToString("yyyyMMdd-HHmmss"), name, StringComparison.Ordinal);
    }
}
