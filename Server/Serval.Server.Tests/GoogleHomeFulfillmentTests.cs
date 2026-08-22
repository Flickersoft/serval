using System.Text.Json;
using Serval.Ai;
using Serval.Server.Cameras;
using Serval.Server.GoogleHome;
using Serval.Server.Ingest;
using Serval.Server.Snapshots;

namespace Serval.Server.Tests;

/// <summary>
/// What Google is told about the cameras, and which cameras it is told about at all.
///
/// <para>Everything here runs over a stated camera list with no host and no database, because the
/// payload shapes and the eligibility rule are the parts that can be wrong in a way nothing else
/// would notice — a JSON field misspelled by a rename, or a device offered that go2rtc cannot
/// serve.</para>
/// </summary>
public class GoogleHomeFulfillmentTests
{
    private static Camera Camera(
        string id,
        string? name = null,
        string? location = null,
        bool enabled = true,
        string url = "rtsp://cam/sub") => new()
        {
            Id = id,
            Name = name ?? id,
            Location = location,
            Enabled = enabled,
            Streams = [new CameraStream { Name = "sub", Url = url, Roles = [StreamRole.Live] }],
        };

    /// <summary>A camera that is actually being recorded, so its segments exist to be cast.</summary>
    private static Camera Recorded(string id, bool recording = true) => new()
    {
        Id = id,
        Name = id,
        Recording = recording,
        Streams =
        [
            new CameraStream
            {
                Name = "main",
                Url = "rtsp://cam/main",
                Roles = [StreamRole.Record, StreamRole.Live],
            },
        ],
    };

    // ------------------------------------------------------ which cameras

    /// <summary>
    /// <b>The load-bearing test in this file.</b> The devices Google is offered must be exactly the
    /// streams the go2rtc sync worker registers — not a set that happens to match today. A camera
    /// in Google's list that go2rtc has no stream for does not degrade gracefully: the voice command
    /// succeeds, EXECUTE hands back a signaling URL, and then the display spins forever with the
    /// failure two systems away from anything that logs it.
    ///
    /// <para>Driving one list through both is the only check that survives somebody adding a
    /// condition to one side. It passes trivially while they share a predicate — which is the
    /// point: it fails the moment they stop.</para>
    /// </summary>
    [Fact]
    public void The_cameras_offered_to_Google_are_the_ones_go2rtc_serves()
    {
        Camera[] all =
        [
            Camera("front-door"),
            Camera("disabled-cam", enabled: false),
            Camera("file-cam", url: "/media/samples/loop.mp4"),
            Camera("blank-url", url: "   "),
            new Camera
            {
                Id = "no-live-role",
                Name = "No live role",
                Streams =
                [
                    new CameraStream
                    {
                        Name = "main",
                        Url = "rtsp://cam/main",
                        Roles = [StreamRole.Record],
                    },
                ],
            },
        ];

        string[] offered = [.. CameraDeviceMapper.Eligible(all).Select(c => c.Id).Order(StringComparer.Ordinal)];
        string[] registered = [.. all.Where(Go2RtcSyncWorker.IsWebRtcEligible).Select(c => c.Id).Order(StringComparer.Ordinal)];

        Assert.Equal(registered, offered);
        Assert.Equal(["front-door"], offered);
    }

    // ---------------------------------------------------------------- SYNC

    [Fact]
    public void A_camera_syncs_as_a_webrtc_camera()
    {
        SyncDevice device = CameraDeviceMapper.ToDevice(Camera("front-door", "Front Door", "Driveway"));

        Assert.Equal("front-door", device.Id);
        Assert.Equal("action.devices.types.CAMERA", device.Type);
        // CameraStream alone: this call configures no PIN, and the switch is only offered where
        // it can be protected. See The_switch_is_only_offered_when_it_can_be_protected.
        Assert.Equal(["action.devices.traits.CameraStream"], device.Traits);
        Assert.Equal("Front Door", device.Name.Name);
        Assert.Equal("Driveway", device.RoomHint);
        // No record stream, so nothing to serve over HLS — WebRTC alone.
        Assert.Equal(["webrtc"], device.Attributes.SupportedProtocols);

        // False because a Cast receiver cannot send an Authorization header; the ticket travels in
        // the URL for both protocols instead.
        Assert.False(device.Attributes.NeedAuthToken);

        // False here because this call declares no HomeGraph key. See below: it is a promise,
        // and it is only made where Report State can actually be delivered.
        Assert.False(device.WillReportState);
    }

    /// <summary>
    /// <c>willReportState</c> follows whether Report State can actually be delivered, which means a
    /// HomeGraph key.
    ///
    /// <para>Declaring it without one is not a harmless overstatement: HomeGraph then waits for
    /// reports that never arrive, and Google's Test Suite refuses to test a device it believes
    /// nothing about — the failure reads as "device is not online", which points at the cameras
    /// rather than at the promise.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_camera_promises_state_reports_only_when_they_can_be_sent(bool hasKey) =>
        Assert.Equal(
            hasKey, CameraDeviceMapper.ToDevice(Camera("front-door"), hasKey).WillReportState);

    /// <summary>
    /// A recorded camera offers HLS too, and <b>after</b> WebRTC. The attribute is documented as
    /// ordered by preference, and WebRTC is what every surface that can take it should get: it is
    /// sub-second and keeps the media on the LAN. HLS exists for the Cast receiver, which cannot
    /// speak WebRTC at all — so a Google TV is never offered a camera advertising WebRTC alone.
    /// </summary>
    [Fact]
    public void A_recorded_camera_offers_hls_after_webrtc() =>
        Assert.Equal(
            ["webrtc", "hls"],
            CameraDeviceMapper.ToDevice(Recorded("front-door")).Attributes.SupportedProtocols);

    /// <summary>
    /// Recording switched off means there are no segments, so HLS is not advertised for that
    /// camera. Advertising it anyway would resolve to a 404 on the device, which a viewer reads as
    /// a broken camera rather than one that cannot be cast.
    /// </summary>
    [Fact]
    public void A_camera_that_is_not_recording_offers_webrtc_only() =>
        Assert.Equal(
            ["webrtc"],
            CameraDeviceMapper.ToDevice(Recorded("front-door", recording: false))
                .Attributes.SupportedProtocols);

    /// <summary>
    /// A blank location is omitted, not sent empty — an empty string becomes a room literally named
    /// "" in the Google Home app, which the user then has to tidy up by hand.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_camera_with_no_location_offers_no_room_hint(string? location) =>
        Assert.Null(CameraDeviceMapper.ToDevice(Camera("front-door", location: location)).RoomHint);

    /// <summary>A camera with no name falls back to its id rather than appearing unnamed.</summary>
    [Fact]
    public void A_nameless_camera_is_named_by_its_id() =>
        Assert.Equal(
            "front-door", CameraDeviceMapper.ToDevice(Camera("front-door", name: "  ")).Name.Name);

    /// <summary>
    /// <b>Google parses by field name, and this server defaults to camelCase.</b> The
    /// <c>action.devices.*</c> vocabulary is not derivable from a C# property name, so a rename
    /// during a refactor would produce valid JSON that Google silently cannot read. Serializing the
    /// real record is the only thing that catches it.
    /// </summary>
    [Fact]
    public void The_sync_payload_uses_the_names_Google_reads()
    {
        var payload = new SyncPayload(
            "agent-1", [CameraDeviceMapper.ToDevice(Camera("front-door", "Front Door", "Driveway"))]);

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        JsonElement root = json.RootElement;

        Assert.Equal("agent-1", root.GetProperty("agentUserId").GetString());

        JsonElement device = root.GetProperty("devices")[0];
        Assert.Equal("front-door", device.GetProperty("id").GetString());
        Assert.Equal("action.devices.types.CAMERA", device.GetProperty("type").GetString());
        Assert.Equal("Front Door", device.GetProperty("name").GetProperty("name").GetString());
        Assert.Equal("Driveway", device.GetProperty("roomHint").GetString());
        Assert.False(device.GetProperty("willReportState").GetBoolean());

        JsonElement attributes = device.GetProperty("attributes");
        Assert.Equal(
            "webrtc",
            attributes.GetProperty("cameraStreamSupportedProtocols")[0].GetString());
        Assert.False(attributes.GetProperty("cameraStreamNeedAuthToken").GetBoolean());

        Assert.Equal("Serval", device.GetProperty("deviceInfo").GetProperty("manufacturer").GetString());
    }

    [Fact]
    public void A_room_hint_is_absent_from_the_json_when_there_is_none()
    {
        var payload = new SyncPayload("agent-1", [CameraDeviceMapper.ToDevice(Camera("front-door"))]);

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(payload));

        Assert.False(json.RootElement.GetProperty("devices")[0].TryGetProperty("roomHint", out _));
    }

    // --------------------------------------------------------------- QUERY

    [Fact]
    public void A_camera_with_a_fresh_snapshot_is_online()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var snapshot = new Snapshot("front-door", [1, 2, 3], now.AddSeconds(-2));

        Assert.True(CameraDeviceMapper.IsOnline(snapshot, now));
    }

    [Fact]
    public void A_camera_whose_snapshot_has_gone_stale_is_offline()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var snapshot = new Snapshot("front-door", [1, 2, 3], now - CameraDeviceMapper.StaleAfter.Add(TimeSpan.FromSeconds(1)));

        Assert.False(CameraDeviceMapper.IsOnline(snapshot, now));
    }

    /// <summary>
    /// <b>Never measured is reported online, deliberately.</b> Snapshots come from the
    /// record/detect ffmpeg pipeline, so a camera carrying only a <c>live</c> role produces none
    /// and still has a working WebRTC view. Reporting it offline would stop Google even attempting
    /// the stream — a working camera made unreachable with nothing to explain it. The App makes the
    /// same call by a different name, saying <em>connecting</em> rather than <em>offline</em> for a
    /// camera it has never heard from; Google has no third value to say it with.
    /// </summary>
    [Fact]
    public void A_camera_that_has_never_produced_a_snapshot_is_reported_online() =>
        Assert.True(CameraDeviceMapper.IsOnline(latest: null, DateTimeOffset.UtcNow));

    /// <summary>
    /// The staleness window is the App's, not a second opinion. Two constants that agreed by
    /// coincidence would be one edit away from Google and the wall disagreeing about whether a
    /// camera is up.
    /// </summary>
    [Fact]
    public void The_staleness_window_matches_the_App() =>
        Assert.Equal(TimeSpan.FromSeconds(15), CameraDeviceMapper.StaleAfter);

    [Fact]
    public void The_query_payload_omits_online_on_an_error()
    {
        var payload = new QueryPayload(new Dictionary<string, QueryDeviceState>
        {
            ["gone"] = new("ERROR", Online: null, On: null, "deviceNotFound"),
        });

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        JsonElement device = json.RootElement.GetProperty("devices").GetProperty("gone");

        Assert.Equal("ERROR", device.GetProperty("status").GetString());
        Assert.Equal("deviceNotFound", device.GetProperty("errorCode").GetString());
        Assert.False(device.TryGetProperty("online", out _));
    }

    // ------------------------------------------------------------- EXECUTE

    /// <summary>
    /// Google nests EXECUTE three deep, and the protocol list is what says whether the receiver on
    /// the other end can play WebRTC at all. It is the surface's own answer, not ours to infer:
    /// phones do play WebRTC, whatever the device tables suggest.
    /// </summary>
    [Fact]
    public void Execute_targets_are_read_out_of_Googles_nesting()
    {
        JsonElement payload = JsonDocument.Parse(
            """
            {
              "commands": [{
                "devices": [{ "id": "front-door" }, { "id": "garage" }],
                "execution": [{
                  "command": "action.devices.commands.GetCameraStream",
                  "params": { "StreamToChromecast": true, "SupportedStreamProtocols": ["webrtc"] }
                }]
              }]
            }
            """).RootElement;

        Assert.Equal(
            [("front-door", true, false), ("garage", true, false)],
            SmartHomeFulfillment.ExecuteTargets(payload));
    }

    /// <summary>
    /// A receiver listing only HLS is read as wanting HLS and not WebRTC, which is what every Cast
    /// device other than a Nest display or a Chromecast with Google TV asks for. Google's own list
    /// is the answer here; the surface is never inferred from anything else in the request.
    /// </summary>
    [Fact]
    public void A_receiver_that_cannot_do_webrtc_is_flagged()
    {
        JsonElement payload = JsonDocument.Parse(
            """
            {
              "commands": [{
                "devices": [{ "id": "front-door" }],
                "execution": [{
                  "command": "action.devices.commands.GetCameraStream",
                  "params": { "SupportedStreamProtocols": ["hls", "dash"] }
                }]
              }]
            }
            """).RootElement;

        Assert.Equal([("front-door", false, true)], SmartHomeFulfillment.ExecuteTargets(payload));
    }

    /// <summary>
    /// <c>StreamToChromecast</c> is read for the log and nothing else — it is the only thing in a
    /// request that says a TV asked rather than a phone, and Google does not otherwise tell us
    /// which surface is calling. Absent is its own answer, distinct from false.
    /// </summary>
    [Theory]
    [InlineData("""{ "StreamToChromecast": true, "SupportedStreamProtocols": ["webrtc"] }""", "yes")]
    [InlineData("""{ "StreamToChromecast": false, "SupportedStreamProtocols": ["webrtc"] }""", "no")]
    [InlineData("""{ "SupportedStreamProtocols": ["webrtc"] }""", "(not stated)")]
    public void The_cast_destination_is_reported_for_the_log(string parameters, string expected)
    {
        JsonElement payload = JsonDocument.Parse(
            $$"""
            {
              "commands": [{
                "devices": [{ "id": "front-door" }],
                "execution": [{
                  "command": "action.devices.commands.GetCameraStream",
                  "params": {{parameters}}
                }]
              }]
            }
            """).RootElement;

        Assert.Equal(expected, SmartHomeFulfillment.StreamToChromecast(payload));
    }

    /// <summary>Some other trait's command must not be read as a stream request.</summary>
    [Fact]
    public void A_different_command_does_not_ask_for_a_stream()
    {
        JsonElement payload = JsonDocument.Parse(
            """
            {
              "commands": [{
                "devices": [{ "id": "front-door" }],
                "execution": [{ "command": "action.devices.commands.OnOff", "params": { "on": true } }]
              }]
            }
            """).RootElement;

        Assert.Equal([("front-door", false, false)], SmartHomeFulfillment.ExecuteTargets(payload));
    }

    /// <summary>
    /// Malformed and empty payloads yield nothing rather than throwing. This runs on input from
    /// the public internet, and an exception here is a 500 on a voice command.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "commands": [] }""")]
    [InlineData("""{ "commands": {} }""")]
    [InlineData("""{ "commands": [{ "execution": [] }] }""")]
    [InlineData("""{ "commands": [{ "devices": [{}] }] }""")]
    public void A_malformed_execute_payload_yields_no_targets(string json) =>
        Assert.Empty(SmartHomeFulfillment.ExecuteTargets(JsonDocument.Parse(json).RootElement));

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "devices": [] }""")]
    [InlineData("""{ "devices": "nonsense" }""")]
    [InlineData("""{ "devices": [{ "noid": 1 }] }""")]
    public void A_malformed_query_payload_yields_no_ids(string json) =>
        Assert.Empty(SmartHomeFulfillment.DeviceIds(JsonDocument.Parse(json).RootElement));

    /// <summary>
    /// The signaling URL is built from the configured public origin and carries the ticket, and it
    /// names no camera — the ticket is the only thing that does.
    /// </summary>
    [Fact]
    public void The_signaling_url_carries_the_ticket_and_names_no_camera()
    {
        string url = SmartHomeFulfillment.SignalingUrl(
            new Uri("https://serval.example.com"), "tick-et+value/with=padding");

        Assert.StartsWith(
            "https://serval.example.com/api/google/camerastream/signal?t=", url, StringComparison.Ordinal);
        Assert.DoesNotContain("front-door", url, StringComparison.Ordinal);

        // Escaped, so a ticket containing URL-significant characters cannot break out of the query.
        Assert.DoesNotContain("+value/with=padding", url, StringComparison.Ordinal);
    }

    /// <summary>A base URL carrying a path is not allowed to swallow the route.</summary>
    [Fact]
    public void The_signaling_url_is_absolute_regardless_of_the_base_path()
    {
        string url = SmartHomeFulfillment.SignalingUrl(
            new Uri("https://serval.example.com/nvr/"), "abc");

        Assert.StartsWith(
            "https://serval.example.com/api/google/camerastream/signal", url, StringComparison.Ordinal);
    }

    [Fact]
    public void The_execute_payload_omits_the_offer_so_Google_makes_one()
    {
        var payload = new ExecutePayload(
        [
            new ExecuteCommandResult(
                ["front-door"],
                "SUCCESS",
                new CameraStreamState(
                    "webrtc", "https://x/y?t=1", AccessUrl: null, "tick",
                    IceServers: null, Offer: null),
                ErrorCode: null),
        ]);

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        JsonElement states = json.RootElement.GetProperty("commands")[0].GetProperty("states");

        Assert.Equal("webrtc", states.GetProperty("cameraStreamProtocol").GetString());
        Assert.Equal("https://x/y?t=1", states.GetProperty("cameraStreamSignalingUrl").GetString());
        Assert.Equal("tick", states.GetProperty("cameraStreamAuthToken").GetString());

        // Absent, which is what makes Google generate the offer — the direction go2rtc answers in.
        Assert.False(states.TryGetProperty("cameraStreamOffer", out _));

        // Absent, so Google falls back to its own STUN. Unused anyway on a LAN.
        Assert.False(states.TryGetProperty("cameraStreamIceServers", out _));

        // Absent: this is the WebRTC branch, and the two URL fields are mutually exclusive.
        Assert.False(states.TryGetProperty("cameraStreamAccessUrl", out _));
    }

    /// <summary>
    /// The HLS branch fills the other URL field and leaves the WebRTC one out. Google reads
    /// <c>cameraStreamAccessUrl</c> for a non-WebRTC protocol and would have nothing to fetch if it
    /// were named wrongly or sent alongside a signaling URL.
    /// </summary>
    [Fact]
    public void The_hls_execute_payload_carries_an_access_url_and_no_signaling_url()
    {
        var payload = new ExecutePayload(
        [
            new ExecuteCommandResult(
                ["front-door"],
                "SUCCESS",
                new CameraStreamState(
                    "hls",
                    SignalingUrl: null,
                    AccessUrl: "https://x/api/google/camerastream/hls/front-door/index.m3u8?t=1",
                    AuthToken: "tick",
                    IceServers: null,
                    Offer: null),
                ErrorCode: null),
        ]);

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        JsonElement states = json.RootElement.GetProperty("commands")[0].GetProperty("states");

        Assert.Equal("hls", states.GetProperty("cameraStreamProtocol").GetString());
        Assert.Equal(
            "https://x/api/google/camerastream/hls/front-door/index.m3u8?t=1",
            states.GetProperty("cameraStreamAccessUrl").GetString());
        Assert.False(states.TryGetProperty("cameraStreamSignalingUrl", out _));

        // Absent unless a Cast application was registered, which is the default and a working
        // deployment: Google then uses its own receiver and plays the URL above as ordinary HLS.
        Assert.False(states.TryGetProperty("cameraStreamReceiverAppId", out _));
    }

    /// <summary>
    /// Naming a receiver is what puts WebRTC on a Cast device Google will not do WebRTC for.
    ///
    /// <para>Google plays WebRTC itself only on a Nest display and a Chromecast with Google TV;
    /// everything else asks for <c>hls</c> and launches a Cast Web Receiver. This field replaces
    /// that receiver with Serval's own, which negotiates a peer connection and uses the playlist
    /// only if that fails — so the protocol stays honestly <c>hls</c> either way.</para>
    /// </summary>
    [Fact]
    public void A_configured_receiver_app_id_rides_with_the_hls_stream()
    {
        var payload = new ExecutePayload(
        [
            new ExecuteCommandResult(
                ["front-door"],
                "SUCCESS",
                new CameraStreamState(
                    "hls",
                    SignalingUrl: null,
                    AccessUrl: "https://x/api/google/camerastream/hls/front-door/index.m3u8?t=1",
                    AuthToken: "tick",
                    IceServers: null,
                    Offer: null,
                    ReceiverAppId: "1G2F89213HG"),
                ErrorCode: null),
        ]);

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        JsonElement states = json.RootElement.GetProperty("commands")[0].GetProperty("states");

        Assert.Equal("1G2F89213HG", states.GetProperty("cameraStreamReceiverAppId").GetString());

        // Still HLS, and still carrying a real playlist. The receiver prefers WebRTC but this URL
        // is what it falls back to, so advertising the protocol truthfully is not a formality.
        Assert.Equal("hls", states.GetProperty("cameraStreamProtocol").GetString());
    }

    /// <summary>
    /// The camera is in the path, not only in the ticket, because a Cast receiver resolves the
    /// segment names in the playlist relative to this URL — so the path is what puts them in the
    /// right camera's directory. The ticket rides in the query because the generic receiver cannot
    /// set a header.
    /// </summary>
    [Fact]
    public void The_hls_url_names_the_camera_in_its_path()
    {
        string url = SmartHomeFulfillment.HlsUrl(
            new Uri("https://serval.example.com"), "front-door", "tick et");

        Assert.Equal(
            "https://serval.example.com/api/google/camerastream/hls/front-door/index.m3u8?t=tick%20et",
            url);
    }

    // ------------------------------------------------ the Home app's switch

    /// <summary>
    /// The switch Google sends when somebody turns a camera off in the Home app. It governs whether
    /// Serval offers <em>Google</em> a stream — recording, detection and the Serval app carry on
    /// regardless, which is the whole reason this state lives in its own collection.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_onoff_command_is_read_for_its_devices(bool on)
    {
        JsonElement payload = JsonDocument.Parse(
            $$"""
            {
              "commands": [{
                "devices": [{ "id": "front-door" }, { "id": "garage" }],
                "execution": [{
                  "command": "action.devices.commands.OnOff",
                  "params": { "on": {{(on ? "true" : "false")}} }
                }]
              }]
            }
            """).RootElement;

        Assert.Equal(
            [("front-door", on, null), ("garage", on, null)],
            SmartHomeFulfillment.OnOffTargets(payload));
    }

    /// <summary>
    /// A stream request is not a switch. The two commands share the same nesting, and reading one
    /// as the other would turn "show me the front door" into "turn the front door off".
    /// </summary>
    [Fact]
    public void A_stream_request_is_not_read_as_a_switch()
    {
        JsonElement payload = JsonDocument.Parse(
            """
            {
              "commands": [{
                "devices": [{ "id": "front-door" }],
                "execution": [{
                  "command": "action.devices.commands.GetCameraStream",
                  "params": { "SupportedStreamProtocols": ["webrtc"] }
                }]
              }]
            }
            """).RootElement;

        Assert.Empty(SmartHomeFulfillment.OnOffTargets(payload));
    }

    /// <summary>
    /// A command with no usable <c>on</c> is ignored rather than read as false. Defaulting would
    /// switch every camera it names off on a malformed request, which is the expensive direction to
    /// be wrong in — and this endpoint takes input from the public internet.
    /// </summary>
    [Theory]
    [InlineData("""{ "command": "action.devices.commands.OnOff", "params": {} }""")]
    [InlineData("""{ "command": "action.devices.commands.OnOff", "params": { "on": "yes" } }""")]
    [InlineData("""{ "command": "action.devices.commands.OnOff" }""")]
    public void A_switch_with_no_usable_state_is_ignored(string execution)
    {
        JsonElement payload = JsonDocument.Parse(
            $$"""
            {
              "commands": [{
                "devices": [{ "id": "front-door" }],
                "execution": [{{execution}}]
              }]
            }
            """).RootElement;

        Assert.Empty(SmartHomeFulfillment.OnOffTargets(payload));
    }

    /// <summary>
    /// QUERY answers both, and they are independent. <c>online</c> is reachability; <c>on</c> is
    /// whether we are offering the camera to Google. A camera switched off is still online — saying
    /// otherwise is what greys out the control that would switch it back.
    /// </summary>
    [Fact]
    public void The_query_payload_carries_on_beside_online()
    {
        var payload = new QueryPayload(new Dictionary<string, QueryDeviceState>
        {
            ["front-door"] = new("SUCCESS", Online: true, On: false, ErrorCode: null),
        });

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        JsonElement device = json.RootElement.GetProperty("devices").GetProperty("front-door");

        Assert.True(device.GetProperty("online").GetBoolean());
        Assert.False(device.GetProperty("on").GetBoolean());
    }

    /// <summary>The switch's own answer, in the shape Google reads it.</summary>
    [Fact]
    public void The_onoff_execute_payload_reports_the_new_state()
    {
        var payload = new ExecutePayload(
            [new ExecuteCommandResult(["front-door"], "SUCCESS", new OnOffState(false), null)]);

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(payload));

        Assert.False(json.RootElement
            .GetProperty("commands")[0].GetProperty("states").GetProperty("on").GetBoolean());
    }

    /// <summary>
    /// <b>The second factor, and it is not paperwork.</b> Google requires a <c>pinNeeded</c>
    /// challenge for <c>OnOff</c> on a camera because a voice carries — through an open window, or
    /// out of a television. Without it, anyone in earshot of an Assistant can disable a security
    /// camera by asking. Google sends the command twice: once bare, then again with the PIN it
    /// collected.
    /// </summary>
    [Fact]
    public void A_switch_carries_the_pin_once_the_assistant_has_collected_it()
    {
        JsonElement payload = JsonDocument.Parse(
            """
            {
              "commands": [{
                "devices": [{ "id": "front-door" }],
                "execution": [{
                  "command": "action.devices.commands.OnOff",
                  "params": { "on": false },
                  "challenge": { "pin": "1234" }
                }]
              }]
            }
            """).RootElement;

        Assert.Equal(
            [("front-door", false, "1234")], SmartHomeFulfillment.OnOffTargets(payload));
    }

    /// <summary>
    /// The first pass carries no challenge, and that has to read as "not asked yet" rather than as
    /// an empty PIN — the two get different answers, and confusing them would either skip the
    /// challenge or reject the one legitimate first attempt.
    /// </summary>
    [Fact]
    public void A_switch_with_no_challenge_yet_reports_no_pin() =>
        Assert.Equal(
            [("front-door", true, (string?)null)],
            SmartHomeFulfillment.OnOffTargets(JsonDocument.Parse(
                """
                {
                  "commands": [{
                    "devices": [{ "id": "front-door" }],
                    "execution": [{
                      "command": "action.devices.commands.OnOff",
                      "params": { "on": true }
                    }]
                  }]
                }
                """).RootElement));

    /// <summary>
    /// The challenge response Google reads. <c>challengeNeeded</c> beside the error code is what
    /// turns the Assistant's reply from "something went wrong" into "what is your PIN?" — its
    /// absence is the difference between a prompt and a dead end.
    /// </summary>
    [Fact]
    public void A_challenge_response_asks_for_a_pin()
    {
        var payload = new ExecutePayload(
        [
            new ExecuteCommandResult(
                ["front-door"],
                "ERROR",
                States: null,
                "challengeNeeded",
                new ChallengeNeeded("pinNeeded")),
        ]);

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        JsonElement command = json.RootElement.GetProperty("commands")[0];

        Assert.Equal("ERROR", command.GetProperty("status").GetString());
        Assert.Equal("challengeNeeded", command.GetProperty("errorCode").GetString());
        Assert.Equal(
            "pinNeeded", command.GetProperty("challengeNeeded").GetProperty("type").GetString());
    }

    /// <summary>
    /// No PIN configured means no switch is offered at all. Declaring the trait without one would
    /// leave a security camera disableable by anyone who can be heard, which is the failure this
    /// whole mechanism exists to prevent — so it fails closed.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_switch_is_only_offered_when_it_can_be_protected(bool switchable)
    {
        SyncDevice device = CameraDeviceMapper.ToDevice(
            Camera("front-door"), willReportState: false, switchable: switchable);

        Assert.Equal(
            switchable
                ? ["action.devices.traits.CameraStream", "action.devices.traits.OnOff"]
                : ["action.devices.traits.CameraStream"],
            device.Traits);
    }

    /// <summary>
    /// <b>The challenge is one-way, and that asymmetry is the point.</b> Switching a security
    /// camera off is the sensitive act; switching it back on restores the safe state. Challenging
    /// both directions strands a camera that is already off behind a prompt the Home app may never
    /// present — which happened, and needed a database edit to undo.
    /// </summary>
    [Fact]
    public void Only_switching_off_is_the_sensitive_direction() =>
        Assert.True(
            SmartHomeFulfillment.NeedsChallenge(on: false)
            && !SmartHomeFulfillment.NeedsChallenge(on: true));
}
