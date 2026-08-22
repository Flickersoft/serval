using System.Text.Json.Serialization;

namespace Serval.Server.GoogleHome;

/// <summary>
/// The fulfillment wire format.
///
/// <para><b>Every field name is spelled out, and it has to be.</b> Google's intent payloads are
/// camelCase, which happens to match this server's default — but the <c>action.devices.*</c>
/// vocabulary is not derivable from a C# name, and a property renamed during a refactor would
/// silently stop matching. Naming them here makes the contract explicit rather than incidental.</para>
/// </summary>
public sealed record SmartHomeRequest(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("inputs")] IReadOnlyList<SmartHomeInput> Inputs);

/// <param name="Intent">
/// One of <c>action.devices.SYNC</c>, <c>.QUERY</c>, <c>.EXECUTE</c>, <c>.DISCONNECT</c>.
/// </param>
/// <param name="Payload">
/// Shaped differently per intent, so it stays a <see cref="System.Text.Json.JsonElement"/> and each
/// handler reads what it needs. Modelling four unrelated shapes as one type would only move the
/// branching somewhere less honest.
/// </param>
public sealed record SmartHomeInput(
    [property: JsonPropertyName("intent")] string Intent,
    [property: JsonPropertyName("payload")] System.Text.Json.JsonElement Payload);

/// <summary>The envelope every intent answers in. <see cref="RequestId"/> is echoed unchanged.</summary>
public sealed record SmartHomeResponse(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("payload")] object Payload);

/// <summary>
/// SYNC's answer: who the user is to us, and every device they have.
/// </summary>
public sealed record SyncPayload(
    [property: JsonPropertyName("agentUserId")] string AgentUserId,
    [property: JsonPropertyName("devices")] IReadOnlyList<SyncDevice> Devices);

/// <summary>
/// One camera as Google models it.
///
/// <para><see cref="WillReportState"/> is true only where Report State can actually be delivered,
/// which means a HomeGraph key is loaded — see <c>CameraDeviceMapper.ToDevice</c>.
/// <see cref="RoomHint"/> is omitted rather than sent empty — an empty string is a room named "" in
/// the Google Home app, which the user then has to clean up by hand.</para>
/// </summary>
public sealed record SyncDevice(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("traits")] IReadOnlyList<string> Traits,
    [property: JsonPropertyName("name")] SyncDeviceName Name,
    [property: JsonPropertyName("willReportState")] bool WillReportState,
    [property: JsonPropertyName("attributes")] CameraStreamAttributes Attributes,
    [property: JsonPropertyName("deviceInfo")] SyncDeviceInfo DeviceInfo,
    [property: JsonPropertyName("roomHint")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RoomHint);

public sealed record SyncDeviceName(
    [property: JsonPropertyName("name")] string Name);

public sealed record SyncDeviceInfo(
    [property: JsonPropertyName("manufacturer")] string Manufacturer,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("swVersion")] string SwVersion);

/// <summary>
/// What the camera can do, declared at SYNC so Google knows what to ask for later.
///
/// <para><c>webrtc</c> first, then <c>hls</c> for a camera that is recorded — see
/// <c>CameraDeviceMapper.ProtocolsFor</c>. The order is the preference: WebRTC is sub-second and
/// keeps the media on the LAN between the display and go2rtc, while HLS has the Cast device fetch
/// the playlist and every segment through the operator's public origin. HLS is offered anyway
/// because a Cast Web Receiver speaks nothing else, so a camera advertising WebRTC alone cannot be
/// shown on a TV at all.</para>
/// </summary>
public sealed record CameraStreamAttributes(
    [property: JsonPropertyName("cameraStreamSupportedProtocols")]
    IReadOnlyList<string> SupportedProtocols,
    [property: JsonPropertyName("cameraStreamNeedAuthToken")] bool NeedAuthToken);

/// <summary>QUERY's answer, keyed by device id.</summary>
public sealed record QueryPayload(
    [property: JsonPropertyName("devices")] IReadOnlyDictionary<string, QueryDeviceState> Devices);

/// <param name="Status">"SUCCESS" or "ERROR".</param>
/// <param name="Online">
/// Whether the camera is producing frames — reachability, and Google's own concept. Deliberately
/// <b>not</b> driven by <paramref name="On"/>: a camera switched off in the Home app is still
/// perfectly reachable, and reporting it offline is what greys out the control that would switch it
/// back on. Omitted on an error.
/// </param>
/// <param name="On">
/// Whether Serval will offer this camera to Google, as set from the Home app. Omitted on an error,
/// and for a device that does not carry the trait.
/// </param>
/// <param name="ErrorCode">Google's vocabulary, e.g. "deviceNotFound". Omitted on success.</param>
public sealed record QueryDeviceState(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("online")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Online,
    [property: JsonPropertyName("on")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? On,
    [property: JsonPropertyName("errorCode")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorCode);

/// <summary>The states an <c>OnOff</c> execution answers with.</summary>
public sealed record OnOffState([property: JsonPropertyName("on")] bool On);

/// <summary>EXECUTE's answer: one entry per group of device ids that share an outcome.</summary>
public sealed record ExecutePayload(
    [property: JsonPropertyName("commands")] IReadOnlyList<ExecuteCommandResult> Commands);

public sealed record ExecuteCommandResult(
    [property: JsonPropertyName("ids")] IReadOnlyList<string> Ids,
    [property: JsonPropertyName("status")] string Status,
    // Typed as object because the shape depends on the command: GetCameraStream answers with a
    // CameraStreamState and OnOff with an OnOffState. Google returns both in this one slot.
    [property: JsonPropertyName("states")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    object? States,
    [property: JsonPropertyName("errorCode")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorCode,

    /// <summary>
    /// Present when the command needs a second factor. It is what turns the Assistant's reply from
    /// "sorry, something went wrong" into "what's your PIN?", so its absence is not a small error —
    /// it is the difference between a challenge and a failure.
    /// </summary>
    [property: JsonPropertyName("challengeNeeded")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ChallengeNeeded? Challenge = null);

/// <param name="Type">"pinNeeded" or "ackNeeded".</param>
public sealed record ChallengeNeeded(
    [property: JsonPropertyName("type")] string Type);

/// <summary>
/// What Google needs to open the stream. Which fields are populated depends on
/// <see cref="Protocol"/>: WebRTC uses <see cref="SignalingUrl"/> and HLS uses
/// <see cref="AccessUrl"/>, and each omits the other's — one record for both because Google
/// returns them in the same <c>states</c> slot.
///
/// <para><see cref="Offer"/> is deliberately never populated: leaving it out is what makes Google
/// generate the SDP offer, and go2rtc answers offers rather than making them.
/// <see cref="IceServers"/> is a JSON-encoded <em>string</em> rather than an array — Google's
/// choice, not ours — and is omitted when unconfigured, which is the normal case for a Hub and
/// go2rtc on one LAN.</para>
///
/// <para><see cref="AuthToken"/> is sent for both, and is the same camera-scoped ticket that the
/// URL already carries. It is redundant on the HLS path — the Cast receiver that fetches those
/// segments cannot set a header at all, which is why <c>cameraStreamNeedAuthToken</c> is false —
/// and harmless to include.</para>
/// </summary>
public sealed record CameraStreamState(
    [property: JsonPropertyName("cameraStreamProtocol")] string Protocol,
    [property: JsonPropertyName("cameraStreamSignalingUrl")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SignalingUrl,
    [property: JsonPropertyName("cameraStreamAccessUrl")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? AccessUrl,
    [property: JsonPropertyName("cameraStreamAuthToken")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? AuthToken,
    [property: JsonPropertyName("cameraStreamIceServers")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? IceServers,
    [property: JsonPropertyName("cameraStreamOffer")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Offer,

    /// <summary>
    /// The Cast application to open the stream with, in place of Google's own receiver.
    ///
    /// <para>Only meaningful alongside a non-WebRTC protocol: Google's words are "Cast receiver ID
    /// to process the camera stream when the <c>StreamToChromecast</c> parameter is true; default
    /// receiver will be used if not provided", and for <c>webrtc</c> it plays with its own player
    /// and never consults this. That asymmetry is the whole reason this field is worth having —
    /// WebRTC reaches only Nest displays and Chromecast with Google TV, so on any other Cast
    /// device the receiver named here is the only thing that can open a WebRTC connection.</para>
    ///
    /// <para>Null unless <c>Serval:GoogleHome:CastReceiverAppId</c> is set, which is the default.
    /// Then Google uses its own receiver and plays <see cref="AccessUrl"/> as ordinary HLS.</para>
    /// </summary>
    [property: JsonPropertyName("cameraStreamReceiverAppId")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ReceiverAppId = null);

/// <summary>
/// The signaling request Google POSTs to <c>cameraStreamSignalingUrl</c>, and the answer it
/// expects back.
/// </summary>
/// <param name="Action">"offer", "answer", or "end".</param>
/// <param name="DeviceId">The camera, as named in SYNC. Cross-checked against the ticket.</param>
/// <param name="Sdp">The session description. Empty for "end".</param>
public sealed record SignalingRequest(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("deviceId")] string? DeviceId,
    [property: JsonPropertyName("sdp")] string? Sdp);

/// <summary><see cref="Action"/> must be the literal "answer"; Google checks it.</summary>
public sealed record SignalingResponse(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("sdp")] string Sdp);

/// <summary>The names Google uses, in one place rather than as literals across three files.</summary>
public static class SmartHome
{
    public const string SyncIntent = "action.devices.SYNC";
    public const string QueryIntent = "action.devices.QUERY";
    public const string ExecuteIntent = "action.devices.EXECUTE";
    public const string DisconnectIntent = "action.devices.DISCONNECT";

    public const string CameraType = "action.devices.types.CAMERA";
    public const string CameraStreamTrait = "action.devices.traits.CameraStream";
    public const string GetCameraStreamCommand = "action.devices.commands.GetCameraStream";

    /// <summary>
    /// Declared so the Google Home app has something to switch. It governs whether Serval offers
    /// <em>Google</em> a stream and nothing else — the camera keeps recording either way. See
    /// <see cref="GoogleCameraSwitch"/>.
    /// </summary>
    public const string OnOffTrait = "action.devices.traits.OnOff";

    public const string OnOffCommand = "action.devices.commands.OnOff";

    public const string WebRtcProtocol = "webrtc";

    /// <summary>
    /// The fallback protocol, and the only one a Cast Web Receiver speaks. A Google TV does not
    /// ask for WebRTC: casting launches a receiver on the device, and that receiver plays
    /// <c>hls</c>/<c>dash</c>/<c>smooth_stream</c>/<c>progressive_mp4</c> and nothing else.
    /// </summary>
    public const string HlsProtocol = "hls";

    public const string Success = "SUCCESS";
    public const string Error = "ERROR";

    /// <summary>
    /// Asks the Assistant to collect a PIN and send the command again. Google requires this for
    /// <c>OnOff</c> on a camera — see <see cref="ServerOptions.GoogleHome"/>'s VerificationPin.
    /// </summary>
    public const string ChallengeNeeded = "challengeNeeded";

    /// <summary>The PIN was wrong. A distinct code, so the Assistant re-prompts rather than gives up.</summary>
    public const string ChallengeFailedPinNeeded = "challengeFailedPinNeeded";

    public const string PinNeeded = "pinNeeded";

    public const string DeviceNotFound = "deviceNotFound";
    public const string DeviceOffline = "deviceOffline";
    public const string FunctionNotSupported = "functionNotSupported";
}
