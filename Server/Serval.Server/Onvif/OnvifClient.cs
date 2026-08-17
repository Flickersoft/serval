using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Serval.Server.Cameras;
using Serval.Server.Configuration;

namespace Serval.Server.Onvif;

/// <summary>
/// What the camera says it is — the settings screen's <c>Reolink RLC-810A · firmware 3.1.0.956</c>
/// line.
///
/// Every field is optional in practice: ONVIF requires them, and cameras omit them anyway. A null
/// is "the camera did not say", which the UI should render as absence rather than as "unknown".
///
/// No uptime here. The design's <c>up 26 days</c> has no ONVIF call behind it — the Device service
/// exposes the system date and time, not how long the thing has been running — so it stays absent
/// rather than becoming a number derived from something else.
/// </summary>
public sealed record OnvifDeviceInformation(
    string? Manufacturer,
    string? Model,
    string? FirmwareVersion,
    string? SerialNumber,
    string? HardwareId);

/// <summary>
/// A camera's own account of its pan/tilt/zoom, so the App can draw the controls that exist rather
/// than the ones an ONVIF URL implies.
///
/// <see cref="PanTilt"/> and <see cref="Zoom"/> are decided on the *continuous velocity* spaces,
/// because a continuous move is how both axes are driven. <see cref="AbsoluteZoom"/> is the one
/// exception and reads the absolute space, because zoom is the axis with a position worth showing:
/// a slider that can be dropped on a place, and read back afterwards, is a different control from
/// one that can only be nudged. Pan and tilt stay velocity-only — the pad is a direction, not a
/// destination, so an absolute pan/tilt space would be a capability nothing here would call.
/// </summary>
/// <param name="PanTilt">The camera reports a ContinuousPanTiltVelocitySpace.</param>
/// <param name="Zoom">The camera reports a ContinuousZoomVelocitySpace. False on a fixed lens.</param>
/// <param name="AbsoluteZoom">
/// The camera reports an AbsoluteZoomPositionSpace, so zoom can be driven to a position rather
/// than nudged at a velocity. Read in addition to <paramref name="Zoom"/> rather than instead of
/// it: the two are independent in ONVIF, and a camera offering both is driven absolutely because
/// that is the only way the slider can mean a place rather than a guess.
/// </param>
/// <param name="Home">The camera reports HomeSupported, so GotoHomePosition works.</param>
/// <param name="MaximumPresets">How many presets the camera will store, when it says.</param>
/// <param name="Presets">Stored positions, by the token GotoPreset takes. Names are optional in ONVIF.</param>
/// <param name="ProfileToken">The media profile these were read against, or null for a camera with no PTZ at all.</param>
/// <param name="NodeToken">The PTZ node driving that profile.</param>
public sealed record PtzCapabilities(
    bool PanTilt,
    bool Zoom,
    bool AbsoluteZoom,
    bool Home,
    int? MaximumPresets,
    IReadOnlyList<PtzPreset> Presets,
    string? ProfileToken,
    string? NodeToken)
{
    /// <summary>
    /// A camera that has an ONVIF endpoint and no PTZ at all — a fixed bullet camera, which is the
    /// common case rather than an edge one. A definite answer, not a failure: the caller should
    /// draw no controls and say nothing, exactly as for a camera with no ONVIF at all.
    /// </summary>
    public static readonly PtzCapabilities None =
        new(PanTilt: false, Zoom: false, AbsoluteZoom: false, Home: false, MaximumPresets: null,
            Presets: [], ProfileToken: null, NodeToken: null);
}

/// <summary>
/// Where the lens is, as the camera reports it — ONVIF <c>GetStatus</c>.
///
/// Every axis is nullable because <c>PTZStatus/Position</c> is optional in the specification and
/// plenty of cameras answer <c>GetStatus</c> with a move status and a clock and nothing else. Null
/// means *the camera did not say*, which the App must render as "we do not know" rather than as a
/// position — the whole reason this exists is that the alternative was counting our own commands.
///
/// The values are normalised in ONVIF's generic position spaces: zoom 0..1, pan and tilt -1..1.
/// A camera answering in some vendor space reports null instead, because a number whose range we
/// do not know cannot be put on a track — see <see cref="OnvifClient.ParseStatus"/>.
///
/// There is no magnification here and there cannot be. The generic zoom space is a fraction of the
/// lens's travel; nothing in ONVIF publishes the optical range that would turn it into an
/// <c>x</c> factor, and the curve between the two is vendor-specific and nonlinear.
/// </summary>
public sealed record PtzStatus(double? Zoom, double? Pan, double? Tilt)
{
    /// <summary>The camera answered and reported no position at all.</summary>
    public static readonly PtzStatus Unknown = new(Zoom: null, Pan: null, Tilt: null);
}

/// <summary>One stored position. <paramref name="Token"/> is what GotoPreset takes — never the name, never the index.</summary>
public sealed record PtzPreset(string Token, string? Name);

/// <summary>
/// The camera's ONVIF side channel — pan/tilt/zoom, and what the device says about itself.
/// ONVIF is SOAP: each call is a SOAP 1.2 envelope
/// carrying a WS-Security UsernameToken (the password never travels in clear — only a
/// <c>Base64(SHA1(nonce + created + password))</c> digest) POSTed to the camera's PTZ service.
///
/// A camera only exposes its device service URL; the PTZ service address and a usable media
/// profile token have to be discovered (GetCapabilities → GetProfiles). Those don't change, so
/// the first call for a camera discovers and caches them; later calls are a single POST.
///
/// The media itself has nothing to do with this — PTZ is a side channel to the camera, orthogonal
/// to both the recording and the WebRTC view it makes interactive.
/// </summary>
public sealed class OnvifClient
{
    // ONVIF / WS-* namespaces, pinned so responses parse regardless of the prefixes a camera uses.
    private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    private static readonly XNamespace Wsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
    private static readonly XNamespace Device = "http://www.onvif.org/ver10/device/wsdl";
    private static readonly XNamespace Media = "http://www.onvif.org/ver10/media/wsdl";
    private static readonly XNamespace Ptz = "http://www.onvif.org/ver20/ptz/wsdl";
    private static readonly XNamespace Schema = "http://www.onvif.org/ver10/schema";

    // ONVIF's generic position spaces, the only ones whose range is defined by the specification:
    // zoom 0..1, pan/tilt -1..1. A camera answering in a vendor space is reporting a number in a
    // range nobody here knows, which is why ParseStatus drops it rather than scaling it.
    private const string ZoomPositionSpace =
        "http://www.onvif.org/ver10/tptz/ZoomSpaces/PositionGenericSpace";
    private const string PanTiltPositionSpace =
        "http://www.onvif.org/ver10/tptz/PanTiltSpaces/PositionGenericSpace";

    private const string PasswordDigestType =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest";
    private const string Base64EncodingType =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary";

    private readonly HttpClient _http;
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly ILogger<OnvifClient> _logger;

    // Discovered (PTZ service URL, profile token) per camera, keyed by a signature of its ONVIF
    // settings so a re-configured camera re-discovers rather than using stale addresses.
    private readonly ConcurrentDictionary<string, Resolved> _resolved = new();

    // Probed capabilities per camera, on a TTL — see GetCapabilitiesAsync for why these do not
    // share the cache above.
    private readonly ConcurrentDictionary<string, CachedCapabilities> _capabilities = new();

    // What the camera says it is. No TTL — see GetDeviceInformationAsync.
    private readonly ConcurrentDictionary<string, CachedDeviceInformation> _deviceInformation = new();

    public OnvifClient(HttpClient http, IOptionsMonitor<ServerOptions> options, ILogger<OnvifClient> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Read per call rather than captured, so changing a timeout takes effect on the next request.
    /// Named for what it holds rather than its section, because <c>Ptz</c> here is the ONVIF XML
    /// namespace.
    /// </summary>
    private PtzOptions Timeouts => _options.CurrentValue.Ptz;

    /// <summary>What every per-camera cache is keyed against, so a re-configured camera
    /// re-discovers rather than using stale addresses.</summary>
    private static string Signature(Camera camera) =>
        $"{camera.OnvifUrl}|{camera.OnvifUsername}|{camera.OnvifProfileToken}";

    public async Task ContinuousMoveAsync(Camera camera, double pan, double tilt, double zoom, CancellationToken cancellationToken)
    {
        Resolved target = await ResolveAsync(camera, cancellationToken);

        string timeout = XmlConvert.ToString(TimeSpan.FromSeconds(Timeouts.MoveTimeoutSeconds));
        var body = new XElement(Ptz + "ContinuousMove",
            new XElement(Ptz + "ProfileToken", target.ProfileToken),
            new XElement(Ptz + "Velocity",
                new XElement(Schema + "PanTilt",
                    new XAttribute("x", Coord(pan)), new XAttribute("y", Coord(tilt))),
                new XElement(Schema + "Zoom", new XAttribute("x", Coord(zoom)))),
            new XElement(Ptz + "Timeout", timeout));

        await PostAsync(target.PtzServiceUrl, camera, body, cancellationToken);
    }

    public async Task StopAsync(Camera camera, CancellationToken cancellationToken)
    {
        Resolved target = await ResolveAsync(camera, cancellationToken);
        var body = new XElement(Ptz + "Stop",
            new XElement(Ptz + "ProfileToken", target.ProfileToken),
            new XElement(Ptz + "PanTilt", "true"),
            new XElement(Ptz + "Zoom", "true"));

        await PostAsync(target.PtzServiceUrl, camera, body, cancellationToken);
    }

    /// <summary>
    /// Drives zoom to a place rather than nudging it at a speed, for cameras reporting an absolute
    /// zoom space. Only Zoom is sent: an AbsoluteMove carrying a PanTilt would re-aim the camera as
    /// a side effect of a zoom drag, and the position we would have to put there is one we may not
    /// have been told.
    /// </summary>
    public async Task AbsoluteZoomAsync(Camera camera, double position, CancellationToken cancellationToken)
    {
        Resolved target = await ResolveAsync(camera, cancellationToken);

        var body = new XElement(Ptz + "AbsoluteMove",
            new XElement(Ptz + "ProfileToken", target.ProfileToken),
            new XElement(Ptz + "Position",
                new XElement(Schema + "Zoom",
                    new XAttribute("x", Coord(Math.Clamp(position, 0, 1))),
                    new XAttribute("space", ZoomPositionSpace))));

        await PostAsync(target.PtzServiceUrl, camera, body, cancellationToken);
    }

    /// <summary>
    /// Asks the camera where it is pointing. Deliberately uncached and not retried: it is read on
    /// opening a camera and again once a zoom drag settles, and a stale answer is worse than none
    /// because it would be indistinguishable from a lens that had not moved.
    /// </summary>
    public async Task<PtzStatus> GetStatusAsync(Camera camera, CancellationToken cancellationToken)
    {
        Resolved target = await ResolveAsync(camera, cancellationToken);

        XDocument status = await PostAsync(
            target.PtzServiceUrl, camera,
            new XElement(Ptz + "GetStatus", new XElement(Ptz + "ProfileToken", target.ProfileToken)),
            cancellationToken);

        return ParseStatus(status);
    }

    public async Task GotoPresetAsync(Camera camera, string presetToken, CancellationToken cancellationToken)
    {
        Resolved target = await ResolveAsync(camera, cancellationToken);
        var body = new XElement(Ptz + "GotoPreset",
            new XElement(Ptz + "ProfileToken", target.ProfileToken),
            new XElement(Ptz + "PresetToken", presetToken));

        await PostAsync(target.PtzServiceUrl, camera, body, cancellationToken);
    }

    public async Task GotoHomeAsync(Camera camera, CancellationToken cancellationToken)
    {
        Resolved target = await ResolveAsync(camera, cancellationToken);
        var body = new XElement(Ptz + "GotoHomePosition",
            new XElement(Ptz + "ProfileToken", target.ProfileToken));

        await PostAsync(target.PtzServiceUrl, camera, body, cancellationToken);
    }

    /// <summary>
    /// Asks the camera what it can do: GetNodes for the axes and the home position, GetPresets for
    /// the stored positions.
    ///
    /// Cached separately from <see cref="_resolved"/> and on a TTL, because the two age
    /// differently. The service URL and profile token never change and sit on the hot path — a
    /// held pad button re-resolves several times a second — whereas presets change the moment
    /// somebody saves one on the camera's own web UI, and a Server restart is not an acceptable
    /// way to notice. Failures are deliberately not cached: a camera unplugged during a probe
    /// should be retried on the next open, not remembered as broken for ten minutes.
    /// </summary>
    public async Task<PtzCapabilities> GetCapabilitiesAsync(Camera camera, bool refresh, CancellationToken cancellationToken)
    {
        string signature = Signature(camera);

        if (!refresh
            && _capabilities.TryGetValue(camera.Id, out CachedCapabilities? cached)
            && cached.Signature == signature
            && DateTimeOffset.UtcNow - cached.FetchedAt < TimeSpan.FromMinutes(Timeouts.CapabilityCacheMinutes))
        {
            return cached.Capabilities;
        }

        PtzCapabilities capabilities;
        try
        {
            Resolved target = await ResolveAsync(camera, cancellationToken);

            XDocument nodes = await PostAsync(
                target.PtzServiceUrl, camera, new XElement(Ptz + "GetNodes"), cancellationToken);

            XDocument presets = await PostAsync(
                target.PtzServiceUrl, camera,
                new XElement(Ptz + "GetPresets", new XElement(Ptz + "ProfileToken", target.ProfileToken)),
                cancellationToken);

            capabilities = ParseCapabilities(nodes, presets, target.ProfileToken, target.NodeToken);
        }
        catch (OnvifNotSupportedException)
        {
            // The camera answered, and its answer was "no PTZ here". That is a capability report,
            // not a gateway failure — measured against the live NVR, one of its two cameras is a
            // fixed bullet, and surfacing that as an error would put a red banner on half the wall.
            capabilities = PtzCapabilities.None;
        }

        _capabilities[camera.Id] = new CachedCapabilities(signature, capabilities, DateTimeOffset.UtcNow);
        _logger.LogInformation(
            "Probed ONVIF PTZ capabilities for camera {CameraId}: panTilt={PanTilt}, zoom={Zoom}, home={Home}, presets={Presets}.",
            camera.Id, capabilities.PanTilt, capabilities.Zoom, capabilities.Home, capabilities.Presets.Count);

        return capabilities;
    }

    /// <summary>
    /// Reads a GetNodes and a GetPresets response into <see cref="PtzCapabilities"/>. Pure, so the
    /// whole of the interesting logic here is testable against canned XML rather than a camera.
    ///
    /// Everything is matched on local name: real cameras vary their prefixes, and a few put the
    /// node elements in a different namespace than the specification suggests.
    /// </summary>
    internal static PtzCapabilities ParseCapabilities(
        XDocument nodes, XDocument presets, string profileToken, string? nodeToken)
    {
        var all = nodes.Descendants()
            .Where(e => e.Name.LocalName == "PTZNode")
            .ToList();

        // GetNodes returns every node the camera has, not the one driving this profile. Match the
        // profile's node when it named one; otherwise the first is the only sensible guess.
        XElement? node = (nodeToken is null
            ? null
            : all.FirstOrDefault(n => n.Attribute("token")?.Value == nodeToken))
            ?? all.FirstOrDefault();

        bool HasSpace(string name) => node?.Descendants()
            .Any(e => e.Name.LocalName == name) is true;

        int? maximum = int.TryParse(
            node?.Descendants().FirstOrDefault(e => e.Name.LocalName == "MaximumNumberOfPresets")?.Value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out int parsed)
            ? parsed
            : null;

        bool home = string.Equals(
            node?.Descendants().FirstOrDefault(e => e.Name.LocalName == "HomeSupported")?.Value,
            "true",
            StringComparison.OrdinalIgnoreCase);

        return new PtzCapabilities(
            PanTilt: HasSpace("ContinuousPanTiltVelocitySpace"),
            Zoom: HasSpace("ContinuousZoomVelocitySpace"),
            AbsoluteZoom: HasSpace("AbsoluteZoomPositionSpace"),
            Home: home,
            MaximumPresets: maximum,
            Presets: ParsePresets(presets),
            ProfileToken: profileToken,
            NodeToken: node?.Attribute("token")?.Value ?? nodeToken);
    }

    /// <summary>
    /// Reads a GetStatus response into <see cref="PtzStatus"/>. Pure, for the same reason
    /// <see cref="ParseCapabilities"/> is: getting it wrong does not throw, it just puts the zoom
    /// knob somewhere the lens is not.
    ///
    /// Three things make an axis null rather than a number, and all three are real devices:
    /// <c>Position</c> absent entirely (the element is optional and many cameras omit it), the
    /// axis absent from a Position that carries the other one (a fixed lens on a pan/tilt head),
    /// and a <c>space</c> attribute naming something other than the generic position space. That
    /// last one is the subtle case — a vendor space is a number in a range the specification does
    /// not define, so putting it on a 0..1 track would be inventing the scale. A missing
    /// <c>space</c> means the profile's default, which for these axes is the generic space.
    /// </summary>
    internal static PtzStatus ParseStatus(XDocument status)
    {
        XElement? position = status.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Position");

        if (position is null) return PtzStatus.Unknown;

        double? Axis(string localName, string genericSpace, string attribute)
        {
            XElement? axis = position.Elements()
                .FirstOrDefault(e => e.Name.LocalName == localName);

            string? space = axis?.Attribute("space")?.Value;
            if (space is not null && space != genericSpace) return null;

            // Finiteness is checked separately because TryParse accepts "NaN" and "INF", and both
            // survive a clamp — a knob positioned at NaN lands nowhere and reads as a real value.
            return double.TryParse(
                axis?.Attribute(attribute)?.Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double parsed) && double.IsFinite(parsed)
                ? parsed
                : null;
        }

        // Clamped rather than rejected out of range: a camera reporting 1.0000001 is telling us it
        // is at the far end, not answering in a different space.
        double? zoom = Axis("Zoom", ZoomPositionSpace, "x");
        double? pan = Axis("PanTilt", PanTiltPositionSpace, "x");
        double? tilt = Axis("PanTilt", PanTiltPositionSpace, "y");

        return new PtzStatus(
            Zoom: zoom is { } z ? Math.Clamp(z, 0, 1) : null,
            Pan: pan is { } p ? Math.Clamp(p, -1, 1) : null,
            Tilt: tilt is { } t ? Math.Clamp(t, -1, 1) : null);
    }

    /// <summary>
    /// The stored positions. A preset with no <c>token</c> is dropped rather than given a
    /// synthesised one — the token is the only thing GotoPreset accepts, so an entry without one
    /// is a button that cannot work. The name is optional in ONVIF and often absent.
    /// </summary>
    internal static IReadOnlyList<PtzPreset> ParsePresets(XDocument presets) =>
    [
        .. presets.Descendants()
            .Where(e => e.Name.LocalName == "Preset")
            .Select(e => (
                Token: e.Attribute("token")?.Value,
                Name: e.Descendants().FirstOrDefault(n => n.Name.LocalName == "Name")?.Value))
            .Where(p => !string.IsNullOrWhiteSpace(p.Token))
            .Select(p => new PtzPreset(p.Token!, string.IsNullOrWhiteSpace(p.Name) ? null : p.Name)),
    ];

    /// <summary>
    /// Asks the camera what it is. Cached per camera without a TTL, unlike the PTZ capabilities:
    /// make, model and serial never change, and firmware changes about as often as somebody
    /// flashes the camera — so this is refreshed on demand rather than on a clock.
    /// </summary>
    public async Task<OnvifDeviceInformation> GetDeviceInformationAsync(Camera camera, bool refresh, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(camera.OnvifUrl))
        {
            throw new InvalidOperationException($"Camera '{camera.Id}' has no ONVIF endpoint configured.");
        }

        string signature = Signature(camera);

        if (!refresh
            && _deviceInformation.TryGetValue(camera.Id, out CachedDeviceInformation? cached)
            && cached.Signature == signature)
        {
            return cached.Information;
        }

        // Straight to the device service — this needs neither the PTZ service address nor a media
        // profile, so a camera with no PTZ at all still answers.
        XDocument response = await PostAsync(
            camera.OnvifUrl!, camera, new XElement(Device + "GetDeviceInformation"), cancellationToken);

        OnvifDeviceInformation information = ParseDeviceInformation(response);

        _deviceInformation[camera.Id] = new CachedDeviceInformation(signature, information);
        _logger.LogInformation(
            "Read ONVIF device information for camera {CameraId}: {Manufacturer} {Model}, firmware {Firmware}.",
            camera.Id, information.Manufacturer, information.Model, information.FirmwareVersion);

        return information;
    }

    /// <summary>
    /// Reads a GetDeviceInformationResponse. Pure, and matched on local name like everything else
    /// here — a blank value becomes null, so "the camera sent an empty element" and "the camera
    /// said nothing" are the same thing to a caller.
    /// </summary>
    internal static OnvifDeviceInformation ParseDeviceInformation(XDocument response)
    {
        string? Read(string name)
        {
            string? value = response.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == name)?.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        return new OnvifDeviceInformation(
            Manufacturer: Read("Manufacturer"),
            Model: Read("Model"),
            FirmwareVersion: Read("FirmwareVersion"),
            SerialNumber: Read("SerialNumber"),
            HardwareId: Read("HardwareId"));
    }

    /// <summary>ONVIF velocities are clamped to [-1, 1] and formatted invariantly for the XML.</summary>
    private static string Coord(double v) =>
        Math.Clamp(v, -1.0, 1.0).ToString("0.0######", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Discovers (and caches) the camera's PTZ service URL and a PTZ-capable media profile token
    /// from its device service. GetCapabilities gives the PTZ and Media service addresses;
    /// GetProfiles gives a token — preferring a profile that actually has a PTZ configuration.
    /// </summary>
    private async Task<Resolved> ResolveAsync(Camera camera, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(camera.OnvifUrl))
        {
            throw new InvalidOperationException($"Camera '{camera.Id}' has no ONVIF endpoint configured.");
        }

        string signature = Signature(camera);
        if (_resolved.TryGetValue(camera.Id, out Resolved? cached) && cached.Signature == signature)
        {
            return cached;
        }

        // GetCapabilities on the device service → PTZ + Media service addresses.
        XDocument caps = await PostAsync(
            camera.OnvifUrl!, camera,
            new XElement(Device + "GetCapabilities", new XElement(Device + "Category", "All")),
            cancellationToken);

        string ptzUrl = RequireCameraOwnHost(
            FindServiceXAddr(caps, "PTZ")
                ?? throw new OnvifNotSupportedException($"Camera '{camera.Id}' reports no PTZ service."),
            camera.OnvifUrl!, camera.Id, "PTZ");

        // A pinned profile token skips the media round-trip; otherwise discover one. The node
        // token comes free from the same document and is what picks this profile's PTZ node out
        // of GetNodes, which returns every node the camera has.
        (string profileToken, string? nodeToken) = camera.OnvifProfileToken is { } pinned
            ? (pinned, null)
            : await DiscoverProfileAsync(caps, camera, cancellationToken);

        var resolved = new Resolved(signature, ptzUrl, profileToken, nodeToken);
        _resolved[camera.Id] = resolved;
        _logger.LogInformation(
            "Resolved ONVIF PTZ for camera {CameraId}: service {PtzUrl}, profile {Profile}.",
            camera.Id, ptzUrl, profileToken);
        return resolved;
    }

    private async Task<(string ProfileToken, string? NodeToken)> DiscoverProfileAsync(
        XDocument caps, Camera camera, CancellationToken cancellationToken)
    {
        string mediaUrl = RequireCameraOwnHost(
            FindServiceXAddr(caps, "Media")
                ?? throw new OnvifException($"Camera '{camera.Id}' reports no Media service to read profiles from."),
            camera.OnvifUrl!, camera.Id, "Media");

        XDocument profiles = await PostAsync(
            mediaUrl, camera, new XElement(Media + "GetProfiles"), cancellationToken);

        return ParseProfile(profiles)
            ?? throw new OnvifException($"Camera '{camera.Id}' returned no media profiles.");
    }

    /// <summary>
    /// Picks the profile to drive, and the PTZ node behind it: prefer a profile carrying a PTZ
    /// configuration, fall back to the first. Pure over the GetProfiles document so it is testable
    /// without a camera.
    /// </summary>
    internal static (string ProfileToken, string? NodeToken)? ParseProfile(XDocument profiles)
    {
        var all = profiles.Descendants()
            .Where(e => e.Name.LocalName == "Profiles")
            .ToList();

        XElement? withPtz = all.FirstOrDefault(p =>
            p.Descendants().Any(e => e.Name.LocalName == "PTZConfiguration"));

        XElement? chosen = withPtz ?? all.FirstOrDefault();
        string? token = chosen?.Attribute("token")?.Value;
        if (token is null)
        {
            return null;
        }

        string? node = chosen!.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "NodeToken")?.Value;

        return (token, string.IsNullOrWhiteSpace(node) ? null : node);
    }

    /// <summary>
    /// Under Capabilities the service address for a category sits at <c>&lt;Category&gt;&lt;XAddr&gt;</c>
    /// (e.g. PTZ/XAddr). Matched by local name so any prefix works.
    /// </summary>
    private static string? FindServiceXAddr(XDocument caps, string category) =>
        caps.Descendants().FirstOrDefault(e => e.Name.LocalName == category)
            ?.Elements().FirstOrDefault(e => e.Name.LocalName == "XAddr")
            ?.Value;

    /// <summary>
    /// Confirms a service address the camera reported still points at that camera.
    ///
    /// GetCapabilities is answered by the device, so its XAddr is the one part of this exchange the
    /// far end chooses — and every later call carries the camera's WS-Security digest to whatever
    /// it named. A camera that has been tampered with, or a plaintext ONVIF exchange someone sits
    /// in the middle of, can therefore aim an authenticated POST at anything the Server can reach:
    /// go2rtc's API on the compose network takes an <c>exec:</c> source, and Mongo needs no
    /// credential at all. Neither is reachable from outside, which is exactly why being able to
    /// send from inside is worth something.
    ///
    /// The host must match; the port need not. Cameras really do answer on :80 and advertise
    /// services on :8000, and rejecting that would break working hardware to close nothing — the
    /// pivot being prevented is to a different machine.
    /// </summary>
    /// <param name="xaddr">The address as the camera reported it.</param>
    /// <param name="onvifUrl">The address an operator configured, which is the authority here.</param>
    internal static string RequireCameraOwnHost(string xaddr, string onvifUrl, string cameraId, string category)
    {
        if (!Uri.TryCreate(xaddr, UriKind.Absolute, out Uri? reported))
        {
            throw new OnvifException(
                $"Camera '{cameraId}' reported an unusable {category} service address '{xaddr}'.");
        }

        // http and https both pass — plenty of cameras speak only plaintext ONVIF and that is
        // normal. This rejects the schemes that would make the address something other than a
        // request to the camera, such as file: or ftp:.
        if (!reported.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !reported.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new OnvifException(
                $"Camera '{cameraId}' reported a {category} service address with an unsupported "
                + $"scheme '{reported.Scheme}'. Only http and https are used for ONVIF.");
        }

        if (!Uri.TryCreate(onvifUrl, UriKind.Absolute, out Uri? configured))
        {
            throw new OnvifException(
                $"Camera '{cameraId}' has an unusable OnvifUrl '{onvifUrl}'.");
        }

        if (!reported.Host.Equals(configured.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new OnvifException(
                $"Camera '{cameraId}' reported a {category} service address on host "
                + $"'{reported.Host}', which is not the camera at '{configured.Host}'. Ignoring it: "
                + "a camera does not get to redirect its own credentials elsewhere on the network.");
        }

        return xaddr;
    }

    /// <summary>
    /// Wraps a SOAP body in an envelope with a fresh WS-Security header and POSTs it to an ONVIF
    /// service, returning the parsed response. A SOAP Fault (or HTTP error) becomes an
    /// <see cref="OnvifException"/>.
    /// </summary>
    private async Task<XDocument> PostAsync(string serviceUrl, Camera camera, XElement body, CancellationToken cancellationToken)
    {
        var envelope = new XElement(Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", Soap.NamespaceName),
            BuildSecurityHeader(camera.OnvifUsername ?? "", camera.OnvifPassword ?? "",
                RandomNumberGenerator.GetBytes(16), DateTimeOffset.UtcNow),
            new XElement(Soap + "Body", body));

        var doc = new XDocument(envelope);

        using var request = new HttpRequestMessage(HttpMethod.Post, serviceUrl)
        {
            Content = new StringContent(doc.ToString(SaveOptions.DisableFormatting), Encoding.UTF8),
        };
        // SOAP 1.2 carries its action in the Content-Type, not a SOAPAction header.
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Timeouts.RequestTimeoutSeconds));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OnvifException($"ONVIF request to camera '{camera.Id}' timed out.");
        }

        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(cancellationToken);
            XDocument parsed = SafeParse(text, camera.Id);

            // ONVIF returns SOAP Faults with a 400/500 status; surface the reason if present.
            if (!response.IsSuccessStatusCode || parsed.Descendants().Any(e => e.Name.LocalName == "Fault"))
            {
                string? reason = parsed.Descendants().FirstOrDefault(e => e.Name.LocalName == "Text")?.Value;
                throw new OnvifException(
                    $"ONVIF call to camera '{camera.Id}' failed ({(int)response.StatusCode}): {reason ?? "unknown fault"}.");
            }

            return parsed;
        }
    }

    private static XDocument SafeParse(string xml, string cameraId)
    {
        try
        {
            return XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new OnvifException($"Camera '{cameraId}' returned a non-XML ONVIF response.", ex);
        }
    }

    /// <summary>
    /// Builds the WS-Security UsernameToken header. Pure and deterministic in its inputs (kept so
    /// for the digest test): <c>PasswordDigest = Base64(SHA1(nonce + created + password))</c>,
    /// with <paramref name="nonce"/> as raw bytes and created/password as UTF-8.
    /// </summary>
    internal static XElement BuildSecurityHeader(string username, string password, byte[] nonce, DateTimeOffset created)
    {
        string createdText = created.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        string digest = PasswordDigest(password, nonce, createdText);

        return new XElement(Soap + "Header",
            new XElement(Wsse + "Security",
                new XAttribute(Soap + "mustUnderstand", "1"),
                new XElement(Wsse + "UsernameToken",
                    new XElement(Wsse + "Username", username),
                    new XElement(Wsse + "Password",
                        new XAttribute("Type", PasswordDigestType), digest),
                    new XElement(Wsse + "Nonce",
                        new XAttribute("EncodingType", Base64EncodingType), Convert.ToBase64String(nonce)),
                    new XElement(Wsu + "Created", createdText))));
    }

    /// <summary>The OASIS UsernameToken digest: <c>Base64(SHA1(nonce + created + password))</c>.</summary>
    internal static string PasswordDigest(string password, byte[] nonce, string created)
    {
        byte[] createdBytes = Encoding.UTF8.GetBytes(created);
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);

        byte[] combined = new byte[nonce.Length + createdBytes.Length + passwordBytes.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(createdBytes, 0, combined, nonce.Length, createdBytes.Length);
        Buffer.BlockCopy(passwordBytes, 0, combined, nonce.Length + createdBytes.Length, passwordBytes.Length);

        return Convert.ToBase64String(SHA1.HashData(combined));
    }

    private sealed record Resolved(string Signature, string PtzServiceUrl, string ProfileToken, string? NodeToken);

    private sealed record CachedCapabilities(string Signature, PtzCapabilities Capabilities, DateTimeOffset FetchedAt);

    private sealed record CachedDeviceInformation(string Signature, OnvifDeviceInformation Information);
}

/// <summary>An ONVIF call failed — a SOAP fault, an unreachable camera, or a malformed response.</summary>
public class OnvifException : Exception
{
    public OnvifException(string message) : base(message) { }
    public OnvifException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The camera answered, and said it cannot do this — it advertises no PTZ service. Distinct from
/// its base because the two want opposite treatment: a failure is a 502 worth showing, whereas
/// "this camera has no pan/tilt" is an ordinary fact about a fixed camera.
/// </summary>
public sealed class OnvifNotSupportedException : OnvifException
{
    public OnvifNotSupportedException(string message) : base(message) { }
}
