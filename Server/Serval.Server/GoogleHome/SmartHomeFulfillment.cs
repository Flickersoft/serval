using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Serval.Server.Cameras;
using Serval.Server.Configuration;
using Serval.Server.Snapshots;

namespace Serval.Server.GoogleHome;

/// <summary>
/// The four intents Google sends, and what each answers.
///
/// <para><b>SYNC</b> lists the cameras. <b>QUERY</b> says whether each is up. <b>EXECUTE</b> hands
/// back a signaling URL for one. <b>DISCONNECT</b> is the user unlinking from the Google Home app,
/// and must take everything away.</para>
///
/// <para>The device set is <see cref="CameraDeviceMapper.Eligible"/> throughout, which is the go2rtc
/// sync worker's own predicate — see there for why that is not merely tidy.</para>
/// </summary>
public sealed class SmartHomeFulfillment
{
    private readonly CameraRepository _cameras;
    private readonly SnapshotBroadcaster _snapshots;
    private readonly GoogleOAuthStore _store;
    private readonly CameraStreamTicketService _tickets;
    private readonly GoogleHomeGate _gate;
    private readonly HomeGraphKeyStore _homeGraphKeys;
    private readonly GoogleCameraSwitchStore _switches;
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly ILogger<SmartHomeFulfillment> _logger;

    public SmartHomeFulfillment(
        CameraRepository cameras,
        SnapshotBroadcaster snapshots,
        GoogleOAuthStore store,
        CameraStreamTicketService tickets,
        GoogleHomeGate gate,
        HomeGraphKeyStore homeGraphKeys,
        GoogleCameraSwitchStore switches,
        IOptionsMonitor<ServerOptions> options,
        ILogger<SmartHomeFulfillment> logger)
    {
        _cameras = cameras;
        _snapshots = snapshots;
        _store = store;
        _tickets = tickets;
        _gate = gate;
        _homeGraphKeys = homeGraphKeys;
        _switches = switches;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Whether the Home app's switch is offered, which is to say whether it can be protected.
    ///
    /// <para>Google requires a <c>pinNeeded</c> challenge for <c>OnOff</c> on a camera. With no PIN
    /// configured there is nothing to challenge with, so the trait is not declared, no switch is
    /// accepted, and — importantly — any switch already stored is ignored. That last part is what
    /// stops a camera being stranded off by a row written while a PIN was configured and left
    /// behind when it was removed.</para>
    /// </summary>
    private bool Switchable =>
        !string.IsNullOrEmpty(_options.CurrentValue.GoogleHome.VerificationPin);

    /// <summary>
    /// The Cast application to open an HLS stream with, or null to leave Google on its own
    /// receiver. Null is the default and a working deployment; see
    /// <see cref="GoogleHomeOptions.CastReceiverAppId"/>.
    /// </summary>
    private string? ReceiverAppId => _gate.CastReceiverAppId;

    /// <summary>
    /// Dispatches one request. Google sends a single input in practice but the contract is a list,
    /// so the first is taken and any others ignored rather than guessed at.
    /// </summary>
    public async Task<SmartHomeResponse> HandleAsync(
        SmartHomeRequest request, string agentUserId, CancellationToken ct)
    {
        SmartHomeInput? input = request.Inputs?.FirstOrDefault();

        // Every intent, named. Without this the only trace a request left was a Mongo timestamp,
        // which says one arrived and nothing about which or what it asked for.
        _logger.LogInformation("Google Home {Intent}.", input?.Intent ?? "(no intent)");

        object payload = input?.Intent switch
        {
            SmartHome.SyncIntent => await SyncAsync(agentUserId, ct),
            SmartHome.QueryIntent => await QueryAsync(input.Payload, ct),
            SmartHome.ExecuteIntent => await ExecuteAsync(input.Payload, ct),
            SmartHome.DisconnectIntent => await DisconnectAsync(agentUserId, ct),

            // Google's own vocabulary for "I do not know what you asked me". Answering with an
            // empty success instead would have it believe the account has no cameras.
            _ => new { errorCode = "notSupported" },
        };

        return new SmartHomeResponse(request.RequestId, payload);
    }

    private async Task<SyncPayload> SyncAsync(string agentUserId, CancellationToken ct)
    {
        List<Camera> cameras = await _cameras.ListAsync(ct);

        // Whether the key *loaded*, not whether a path is configured: a path pointing at an
        // unreadable file would otherwise have us promise reports nothing can send.
        bool reportsState = _homeGraphKeys.Key is not null;

        SyncDevice[] devices =
        [
            .. CameraDeviceMapper.Eligible(cameras)
                .Select(camera => CameraDeviceMapper.ToDevice(camera, reportsState, Switchable)),
        ];

        _logger.LogInformation(
            "Google Home SYNC: offering {Count} of {Total} cameras.", devices.Length, cameras.Count);

        return new SyncPayload(agentUserId, devices);
    }

    private async Task<QueryPayload> QueryAsync(JsonElement payload, CancellationToken ct)
    {
        List<Camera> cameras = await _cameras.ListAsync(ct);

        HashSet<string> eligible = [.. CameraDeviceMapper.Eligible(cameras).Select(c => c.Id)];
        HashSet<string> off = Switchable ? await _switches.OffAsync(ct) : [];
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var states = new Dictionary<string, QueryDeviceState>(StringComparer.Ordinal);

        foreach (string id in DeviceIds(payload))
        {
            states[id] = eligible.Contains(id)
                ? new QueryDeviceState(
                    SmartHome.Success,

                    // Reachability, unaffected by the switch: a camera switched off in the Home app
                    // is still answering, and saying otherwise is what would grey out the control
                    // that switches it back on.
                    CameraDeviceMapper.IsOnline(_snapshots.Latest(id), now),
                    On: Switchable ? !off.Contains(id) : null,
                    ErrorCode: null)

                // Disabled, deleted, or a file camera. Google keeps a device it was told about
                // until the next SYNC, so this is a normal answer rather than an exceptional one.
                : new QueryDeviceState(
                    SmartHome.Error, Online: null, On: null, SmartHome.DeviceNotFound);
        }

        return new QueryPayload(states);
    }

    /// <summary>
    /// Answers the one command this integration supports, per device.
    ///
    /// <para><b>Success here means "a ticket was minted", not "the stream works".</b> Nothing is
    /// asked of go2rtc — the camera could be unreachable and this still returns SUCCESS, after
    /// which the Assistant says "showing the front door" and the display fails to connect. That is
    /// inherent to the protocol: the media negotiation happens after this response. A pre-flight
    /// check would cost an RTSP connection on every voice command to buy a better error message on
    /// a rare one.</para>
    /// </summary>
    private async Task<ExecutePayload> ExecuteAsync(JsonElement payload, CancellationToken ct)
    {
        List<Camera> cameras = await _cameras.ListAsync(ct);
        Dictionary<string, Camera> eligible = CameraDeviceMapper.Eligible(cameras)
            .ToDictionary(c => c.Id, StringComparer.Ordinal);

        Uri? publicBase = _gate.PublicBaseUri;
        string? iceServers = _gate.IceServersJson;

        var results = new List<ExecuteCommandResult>();

        // What the requesting surface says it can play. This is the field that decides whether a
        // phone, a display or a TV gets a stream at all, and it is the only place the surface's
        // own capabilities are visible to us — Google does not say which device is asking.
        _logger.LogInformation(
            "Google Home EXECUTE requested protocols: {Protocols} (to a Cast device: {Chromecast}).",
            RequestedProtocols(payload),
            StreamToChromecast(payload));

        // The Home app's switch. Handled first so that switching a camera off and asking for its
        // stream in one payload cannot answer with a stream it has just withdrawn.
        foreach ((string id, bool on, string? pin) in OnOffTargets(payload))
        {
            if (!eligible.ContainsKey(id))
            {
                results.Add(new ExecuteCommandResult(
                    [id], SmartHome.Error, States: null, SmartHome.DeviceNotFound));
                continue;
            }

            if (!Switchable)
            {
                // No PIN configured, so the trait was never declared. Reachable only if Google is
                // holding an older SYNC, and refusing is the safe answer either way.
                results.Add(new ExecuteCommandResult(
                    [id], SmartHome.Error, States: null, SmartHome.FunctionNotSupported));
                continue;
            }

            // The second factor, and only in the direction that needs one.
            //
            // Switching a security camera *off* is the sensitive act — a voice carries through an
            // open window, and out of a television — and it is what Google singles out. Switching
            // one back on restores the safe state, so challenging it buys nothing and costs
            // something real: a camera left off with the way back gated behind a prompt the Home
            // app may never offer is a camera nobody can recover without touching the database.
            if (NeedsChallenge(on))
            {
                if (pin is null)
                {
                    results.Add(new ExecuteCommandResult(
                        [id],
                        SmartHome.Error,
                        States: null,
                        SmartHome.ChallengeNeeded,
                        new ChallengeNeeded(SmartHome.PinNeeded)));
                    continue;
                }

                if (!PinMatches(pin))
                {
                    // A distinct code from challengeNeeded, so the Assistant says the PIN was wrong
                    // and asks again rather than reporting a failure the speaker cannot act on.
                    _logger.LogWarning(
                        "Google Home: switching camera {CameraId} off was refused — wrong PIN.", id);

                    results.Add(new ExecuteCommandResult(
                        [id],
                        SmartHome.Error,
                        States: null,
                        SmartHome.ChallengeFailedPinNeeded,
                        new ChallengeNeeded(SmartHome.PinNeeded)));
                    continue;
                }
            }

            await _switches.SetAsync(id, on, ct);

            // A session already running would otherwise keep playing after the switch was thrown,
            // which reads as the switch not working. Dropping the tickets stops the next segment or
            // renegotiation; nothing in Serval's own pipeline is touched.
            if (!on)
            {
                _tickets.RevokeForCamera(id);
            }

            _logger.LogInformation(
                "Google Home: camera {CameraId} switched {State} from the Home app. Recording and "
                + "the Serval app are unaffected.", id, on ? "on" : "off");

            results.Add(new ExecuteCommandResult(
                [id], SmartHome.Success, new OnOffState(on), ErrorCode: null));
        }

        HashSet<string> switchedOff = Switchable ? await _switches.OffAsync(ct) : [];

        foreach ((string id, bool webRtcWanted, bool hlsWanted) in ExecuteTargets(payload))
        {
            if (!eligible.TryGetValue(id, out Camera? camera))
            {
                results.Add(new ExecuteCommandResult(
                    [id], SmartHome.Error, States: null, SmartHome.DeviceNotFound));
                continue;
            }

            if (switchedOff.Contains(id))
            {
                // Switched off in the Home app. deviceOffline rather than functionNotSupported:
                // the camera can do this, it is just not offering it right now, and Google words
                // that to the user as unavailable rather than unsupported.
                results.Add(new ExecuteCommandResult(
                    [id], SmartHome.Error, States: null, SmartHome.DeviceOffline));
                continue;
            }

            if (publicBase is null)
            {
                // Unreachable behind the gate, which already requires a valid https base URL. Kept
                // as a real branch rather than a null-forgiving operator so a future change to the
                // gate cannot turn this into a NullReferenceException on a live voice command.
                results.Add(new ExecuteCommandResult(
                    [id], SmartHome.Error, States: null, SmartHome.DeviceOffline));
                continue;
            }

            // WebRTC first whenever the receiver will take it: sub-second, media stays on the LAN,
            // and no dependence on the camera being recorded. Google offers it only from a Nest
            // display or a Chromecast with Google TV; every other Cast device asks for HLS, and
            // that branch is answered only for a camera being recorded, because those segments are
            // what it plays.
            if (webRtcWanted)
            {
                string ticket = _tickets.Mint(id);

                results.Add(new ExecuteCommandResult(
                    [id],
                    SmartHome.Success,
                    new CameraStreamState(
                        Protocol: SmartHome.WebRtcProtocol,
                        SignalingUrl: SignalingUrl(publicBase, ticket),
                        AccessUrl: null,
                        AuthToken: ticket,
                        IceServers: iceServers,

                        // Never set: omitting it is what makes Google generate the offer, which is
                        // the direction go2rtc can answer in.
                        Offer: null),
                    ErrorCode: null));
                continue;
            }

            if (hlsWanted && CameraDeviceMapper.CanPlayHls(camera))
            {
                string ticket = _tickets.MintForPlayback(id);

                results.Add(new ExecuteCommandResult(
                    [id],
                    SmartHome.Success,
                    new CameraStreamState(
                        Protocol: SmartHome.HlsProtocol,
                        SignalingUrl: null,
                        AccessUrl: HlsUrl(publicBase, id, ticket),
                        AuthToken: ticket,

                        // Both are read by Google's own player, which is not the one that runs when
                        // a receiver app is named below. Serval's receiver is handed its ICE
                        // configuration in the page instead — see CameraStreamReceiver.
                        IceServers: null,
                        Offer: null,

                        // Turns this from "play a playlist" into "run Serval's receiver, which will
                        // try WebRTC and use the playlist only if that fails". Null unless the
                        // operator registered a Cast application, which is the default.
                        ReceiverAppId: ReceiverAppId),
                    ErrorCode: null));
                continue;
            }

            // A receiver that can take neither — or one asking for HLS on a camera that is not
            // being recorded, which has no segments to serve.
            results.Add(new ExecuteCommandResult(
                [id], SmartHome.Error, States: null, SmartHome.FunctionNotSupported));
        }

        return new ExecutePayload(results);
    }

    /// <summary>
    /// The user unlinked in the Google Home app. Everything issued under the link goes, so a
    /// request arriving afterwards finds no token and is refused.
    /// </summary>
    private async Task<object> DisconnectAsync(string agentUserId, CancellationToken ct)
    {
        await _store.UnlinkAsync(agentUserId, ct);

        // The switches were set from the Home app, so they belong to the link that is going away.
        // Leaving them would have a camera silently unavailable to whoever links next, with the
        // reason recorded nowhere they would think to look.
        await _switches.ClearAsync(ct);

        _logger.LogInformation("Google Home DISCONNECT: link {AgentUserId} removed.", agentUserId);

        // An empty object is the whole contract.
        return new { };
    }

    /// <summary>
    /// EXECUTE's payload, flattened to (device id, requested on state) for <c>OnOff</c> commands.
    ///
    /// <para>A separate walk from <see cref="ExecuteTargets"/> rather than one pass returning a
    /// tagged union: the two commands share no parameters and no outcome, and reading each on its
    /// own terms is what keeps either one comprehensible.</para>
    /// </summary>
    internal static IEnumerable<(string Id, bool On, string? Pin)> OnOffTargets(JsonElement payload)
    {
        if (!payload.TryGetProperty("commands", out JsonElement commands)
            || commands.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement command in commands.EnumerateArray())
        {
            if (RequestedOnState(command) is not bool on)
            {
                continue;
            }

            string? pin = ChallengePin(command);

            if (!command.TryGetProperty("devices", out JsonElement devices)
                || devices.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement device in devices.EnumerateArray())
            {
                if (device.TryGetProperty("id", out JsonElement id)
                    && id.GetString() is { Length: > 0 } value)
                {
                    yield return (value, on, pin);
                }
            }
        }
    }

    /// <summary>
    /// The PIN Google collected for a command, or null if it sent none.
    ///
    /// <para>Google puts it on the execution as <c>challenge.pin</c>, alongside the params rather
    /// than inside them. Null means "not asked yet" and is the normal first pass — every switch
    /// arrives twice, once without a challenge and once with.</para>
    /// </summary>
    private static string? ChallengePin(JsonElement command)
    {
        if (!command.TryGetProperty("execution", out JsonElement executions)
            || executions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement execution in executions.EnumerateArray())
        {
            if (execution.TryGetProperty("command", out JsonElement name)
                && name.GetString() == SmartHome.OnOffCommand
                && execution.TryGetProperty("challenge", out JsonElement challenge)
                && challenge.TryGetProperty("pin", out JsonElement pin)
                && pin.GetString() is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether switching a camera to <paramref name="on"/> needs the PIN.
    ///
    /// <para>Only switching <em>off</em> does. That is what Google requires a <c>pinNeeded</c>
    /// challenge for, and it is the half that matters: a voice within earshot disabling a security
    /// camera. Turning one back on returns it to the safe state, and gating that as well leaves a
    /// camera stranded off whenever the Home app does not present the prompt.</para>
    /// </summary>
    internal static bool NeedsChallenge(bool on) => !on;

    /// <summary>
    /// Whether a supplied PIN is the configured one, compared in constant time — the discipline
    /// <c>TelemetryEndpoints</c> uses for its API key, and for the same reason: a comparison that
    /// returns early leaks the PIN one character at a time to anything that can time it.
    /// </summary>
    private bool PinMatches(string supplied) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(supplied),
            Encoding.UTF8.GetBytes(_options.CurrentValue.GoogleHome.VerificationPin));

    /// <summary>
    /// The <c>on</c> parameter of a command's <c>OnOff</c> execution, or null if it carries none.
    /// Null rather than false, so a malformed command is ignored instead of silently switching
    /// every camera it names off.
    /// </summary>
    private static bool? RequestedOnState(JsonElement command)
    {
        if (!command.TryGetProperty("execution", out JsonElement executions)
            || executions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement execution in executions.EnumerateArray())
        {
            if (execution.TryGetProperty("command", out JsonElement name)
                && name.GetString() == SmartHome.OnOffCommand
                && execution.TryGetProperty("params", out JsonElement parameters)
                && parameters.TryGetProperty("on", out JsonElement on)
                && on.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return on.GetBoolean();
            }
        }

        return null;
    }

    /// <summary>
    /// Google's <c>StreamToChromecast</c> parameter, for the log only.
    ///
    /// <para>Nothing routes on it — <c>SupportedStreamProtocols</c> is the field that actually says
    /// what the far end can play, and acting on both would mean two sources of truth for one
    /// decision. It is logged because it is the only thing in the request that distinguishes a TV
    /// asking from a phone asking, and "which surface asked" is otherwise invisible to us.</para>
    /// </summary>
    internal static string StreamToChromecast(JsonElement payload)
    {
        if (payload.TryGetProperty("commands", out JsonElement commands)
            && commands.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement command in commands.EnumerateArray())
            {
                if (!command.TryGetProperty("execution", out JsonElement executions)
                    || executions.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement execution in executions.EnumerateArray())
                {
                    if (execution.TryGetProperty("params", out JsonElement p)
                        && p.TryGetProperty("StreamToChromecast", out JsonElement cast)
                        && cast.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        return cast.GetBoolean() ? "yes" : "no";
                    }
                }
            }
        }

        return "(not stated)";
    }

    /// <summary>
    /// Every protocol named anywhere in an EXECUTE payload, for the log. Diagnostic only — the
    /// decision is made per command in <see cref="ExecuteTargets"/>.
    /// </summary>
    internal static string RequestedProtocols(JsonElement payload)
    {
        var found = new List<string>();

        if (payload.TryGetProperty("commands", out JsonElement commands)
            && commands.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement command in commands.EnumerateArray())
            {
                if (!command.TryGetProperty("execution", out JsonElement executions)
                    || executions.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement execution in executions.EnumerateArray())
                {
                    if (execution.TryGetProperty("params", out JsonElement p)
                        && p.TryGetProperty("SupportedStreamProtocols", out JsonElement protocols)
                        && protocols.ValueKind == JsonValueKind.Array)
                    {
                        found.AddRange(protocols.EnumerateArray()
                            .Select(x => x.GetString())
                            .Where(x => !string.IsNullOrEmpty(x))!);
                    }
                }
            }
        }

        return found.Count == 0 ? "(none stated)" : string.Join(",", found.Distinct(StringComparer.Ordinal));
    }

    internal static string SignalingUrl(Uri publicBase, string ticket) =>
        new Uri(publicBase, "/api/google/camerastream/signal").AbsoluteUri
        + "?t=" + Uri.EscapeDataString(ticket);

    /// <summary>
    /// The playlist URL handed to a Cast receiver.
    ///
    /// <para>The camera is in the path rather than carried only by the ticket, because the receiver
    /// resolves the segment names in the playlist <em>relative to this URL</em> — so the path is
    /// what puts them in the right camera's directory. The ticket is still what authorises it, and
    /// the handler refuses a ticket whose camera does not match this path.</para>
    /// </summary>
    internal static string HlsUrl(Uri publicBase, string cameraId, string ticket) =>
        new Uri(publicBase, $"/api/google/camerastream/hls/{Uri.EscapeDataString(cameraId)}/index.m3u8").AbsoluteUri
        + "?t=" + Uri.EscapeDataString(ticket);

    /// <summary>QUERY's payload: <c>{"devices":[{"id":"front-door"}]}</c>.</summary>
    internal static IEnumerable<string> DeviceIds(JsonElement payload)
    {
        if (!payload.TryGetProperty("devices", out JsonElement devices)
            || devices.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement device in devices.EnumerateArray())
        {
            if (device.TryGetProperty("id", out JsonElement id)
                && id.GetString() is { Length: > 0 } value)
            {
                yield return value;
            }
        }
    }

    /// <summary>
    /// EXECUTE's payload, flattened to (device id, protocols the receiver will take) rows.
    ///
    /// <para>Google nests it three deep — <c>commands[].devices[]</c> against
    /// <c>commands[].execution[]</c> — because one command may address several devices and carry
    /// several executions. Only <c>GetCameraStream</c> means anything here, and its
    /// <c>SupportedStreamProtocols</c> is what says what the receiver on the other end can play.
    /// It is the receiver's own answer and the only one worth having: what a surface supports is
    /// not something to infer from what kind of device we imagine is asking.</para>
    /// </summary>
    internal static IEnumerable<(string Id, bool WebRtc, bool Hls)> ExecuteTargets(JsonElement payload)
    {
        if (!payload.TryGetProperty("commands", out JsonElement commands)
            || commands.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement command in commands.EnumerateArray())
        {
            HashSet<string> wanted = SupportedProtocols(command);
            bool webRtc = wanted.Contains(SmartHome.WebRtcProtocol);
            bool hls = wanted.Contains(SmartHome.HlsProtocol);

            if (!command.TryGetProperty("devices", out JsonElement devices)
                || devices.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement device in devices.EnumerateArray())
            {
                if (device.TryGetProperty("id", out JsonElement id)
                    && id.GetString() is { Length: > 0 } value)
                {
                    yield return (value, webRtc, hls);
                }
            }
        }
    }

    /// <summary>Every protocol one command's <c>GetCameraStream</c> executions say they can play.</summary>
    private static HashSet<string> SupportedProtocols(JsonElement command)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);

        if (!command.TryGetProperty("execution", out JsonElement executions)
            || executions.ValueKind != JsonValueKind.Array)
        {
            return wanted;
        }

        foreach (JsonElement execution in executions.EnumerateArray())
        {
            if (!execution.TryGetProperty("command", out JsonElement name)
                || name.GetString() != SmartHome.GetCameraStreamCommand)
            {
                continue;
            }

            if (!execution.TryGetProperty("params", out JsonElement parameters)
                || !parameters.TryGetProperty("SupportedStreamProtocols", out JsonElement protocols)
                || protocols.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement protocol in protocols.EnumerateArray())
            {
                if (protocol.GetString() is { Length: > 0 } value)
                {
                    wanted.Add(value);
                }
            }
        }

        return wanted;
    }
}
