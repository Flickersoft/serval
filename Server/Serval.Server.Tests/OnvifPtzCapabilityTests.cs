using System.Xml.Linq;
using Serval.Server.Onvif;

namespace Serval.Server.Tests;

/// <summary>
/// Reading a camera's own account of its PTZ. Decidable without a camera because the parsing is
/// pure over the SOAP documents — which is the whole point of splitting it out, since getting this
/// wrong does not throw: it draws a zoom slider on a fixed lens, or hides a pan/tilt pad that works.
///
/// The XML here is shaped after what real cameras return, prefixes and all — several of these
/// cases exist because devices vary the prefix or omit an element the specification marks optional.
/// </summary>
public class OnvifPtzCapabilityTests
{
    private const string Tptz = "http://www.onvif.org/ver20/ptz/wsdl";
    private const string Tt = "http://www.onvif.org/ver10/schema";

    /// <summary>A GetNodes response with the spaces named, so a test can say what a camera reports.</summary>
    private static XDocument Nodes(params (string Token, bool PanTilt, bool Zoom, bool Home, int? MaxPresets)[] nodes)
    {
        XNamespace tptz = Tptz;
        XNamespace tt = Tt;

        return new XDocument(
            new XElement(tptz + "GetNodesResponse",
                nodes.Select(n => new XElement(tptz + "PTZNode",
                    new XAttribute("token", n.Token),
                    new XElement(tt + "Name", n.Token),
                    new XElement(tt + "SupportedPTZSpaces",
                        // Always present, and deliberately not what the parser keys on: a camera
                        // can position absolutely and still refuse a continuous move.
                        new XElement(tt + "AbsolutePanTiltPositionSpace",
                            new XElement(tt + "URI", "http://www.onvif.org/ver10/tptz/PanTiltSpaces/PositionGenericSpace")),
                        n.PanTilt
                            ? new XElement(tt + "ContinuousPanTiltVelocitySpace",
                                new XElement(tt + "URI", "http://www.onvif.org/ver10/tptz/PanTiltSpaces/VelocityGenericSpace"))
                            : null,
                        n.Zoom
                            ? new XElement(tt + "ContinuousZoomVelocitySpace",
                                new XElement(tt + "URI", "http://www.onvif.org/ver10/tptz/ZoomSpaces/VelocityGenericSpace"))
                            : null),
                    n.MaxPresets is { } max ? new XElement(tt + "MaximumNumberOfPresets", max) : null,
                    new XElement(tt + "HomeSupported", n.Home ? "true" : "false")))));
    }

    private static XDocument Presets(params (string? Token, string? Name)[] presets)
    {
        XNamespace tptz = Tptz;
        XNamespace tt = Tt;

        return new XDocument(
            new XElement(tptz + "GetPresetsResponse",
                presets.Select(p => new XElement(tptz + "Preset",
                    p.Token is null ? null : new XAttribute("token", p.Token),
                    p.Name is null ? null : new XElement(tt + "Name", p.Name)))));
    }

    private static readonly XDocument NoPresets = Presets();

    [Fact]
    public void A_pan_tilt_camera_with_a_fixed_lens_reports_no_zoom()
    {
        // The case the whole feature exists for: today this camera gets a zoom slider that does
        // nothing, because "an ONVIF URL is set" was the only thing anybody asked.
        PtzCapabilities capabilities = OnvifClient.ParseCapabilities(
            Nodes(("node0", PanTilt: true, Zoom: false, Home: true, MaxPresets: 16)),
            NoPresets, "Profile_1", "node0");

        Assert.True(capabilities.PanTilt);
        Assert.False(capabilities.Zoom);
        Assert.True(capabilities.Home);
        Assert.Equal(16, capabilities.MaximumPresets);
    }

    [Fact]
    public void A_zoom_only_camera_reports_no_pan_tilt()
    {
        PtzCapabilities capabilities = OnvifClient.ParseCapabilities(
            Nodes(("node0", PanTilt: false, Zoom: true, Home: false, MaxPresets: null)),
            NoPresets, "Profile_1", "node0");

        Assert.False(capabilities.PanTilt);
        Assert.True(capabilities.Zoom);
        Assert.False(capabilities.Home);
        Assert.Null(capabilities.MaximumPresets);
    }

    [Fact]
    public void An_absolute_only_camera_reports_neither_axis()
    {
        // The parser keys on the continuous velocity spaces because ContinuousMove is the only PTZ
        // call this Server makes. An absolute space it cannot be driven with is not a capability.
        PtzCapabilities capabilities = OnvifClient.ParseCapabilities(
            Nodes(("node0", PanTilt: false, Zoom: false, Home: false, MaxPresets: null)),
            NoPresets, "Profile_1", "node0");

        Assert.False(capabilities.PanTilt);
        Assert.False(capabilities.Zoom);
    }

    [Fact]
    public void The_profiles_node_is_the_one_read_not_merely_the_first()
    {
        // GetNodes returns every node the camera has. A multi-head device whose second head is the
        // one this profile drives would otherwise be described by the first head's abilities.
        PtzCapabilities capabilities = OnvifClient.ParseCapabilities(
            Nodes(
                ("node0", PanTilt: true, Zoom: true, Home: true, MaxPresets: 32),
                ("node1", PanTilt: true, Zoom: false, Home: false, MaxPresets: 8)),
            NoPresets, "Profile_2", "node1");

        Assert.False(capabilities.Zoom);
        Assert.False(capabilities.Home);
        Assert.Equal(8, capabilities.MaximumPresets);
        Assert.Equal("node1", capabilities.NodeToken);
    }

    [Fact]
    public void An_unmatched_node_token_falls_back_to_the_first()
    {
        PtzCapabilities capabilities = OnvifClient.ParseCapabilities(
            Nodes(("node0", PanTilt: true, Zoom: true, Home: false, MaxPresets: null)),
            NoPresets, "Profile_1", "nonexistent");

        Assert.True(capabilities.PanTilt);
        Assert.Equal("node0", capabilities.NodeToken);
    }

    [Fact]
    public void A_camera_reporting_no_nodes_reports_no_axes_rather_than_throwing()
    {
        PtzCapabilities capabilities = OnvifClient.ParseCapabilities(
            Nodes(), NoPresets, "Profile_1", null);

        Assert.False(capabilities.PanTilt);
        Assert.False(capabilities.Zoom);
        Assert.False(capabilities.Home);
        Assert.Empty(capabilities.Presets);
    }

    [Fact]
    public void Presets_are_read_by_token_and_may_have_no_name()
    {
        // tt:Name is optional in ONVIF and plenty of cameras leave it off.
        IReadOnlyList<PtzPreset> presets = OnvifClient.ParsePresets(
            Presets(("1", "Gate"), ("2", null), ("3", "  ")));

        Assert.Equal(3, presets.Count);
        Assert.Equal(new PtzPreset("1", "Gate"), presets[0]);
        Assert.Null(presets[1].Name);
        Assert.Null(presets[2].Name);
    }

    [Fact]
    public void A_preset_with_no_token_is_dropped()
    {
        // The token is the only thing GotoPreset accepts, so an entry without one is a button that
        // cannot work. Better absent than offered.
        IReadOnlyList<PtzPreset> presets = OnvifClient.ParsePresets(
            Presets((null, "Broken"), ("2", "Street")));

        Assert.Single(presets);
        Assert.Equal("2", presets[0].Token);
    }

    [Fact]
    public void Elements_are_matched_by_local_name_whatever_the_prefix()
    {
        // Devices vary prefixes freely, and a few put these in namespaces the specification does
        // not suggest. Matching on local name is what keeps those cameras working.
        var raw = XDocument.Parse(
            """
            <Envelope xmlns="http://www.w3.org/2003/05/soap-envelope">
              <Body>
                <weird:GetNodesResponse xmlns:weird="urn:example:not-onvif">
                  <weird:PTZNode token="head">
                    <weird:SupportedPTZSpaces>
                      <weird:ContinuousPanTiltVelocitySpace/>
                      <weird:ContinuousZoomVelocitySpace/>
                    </weird:SupportedPTZSpaces>
                    <weird:HomeSupported>true</weird:HomeSupported>
                  </weird:PTZNode>
                </weird:GetNodesResponse>
              </Body>
            </Envelope>
            """);

        var rawPresets = XDocument.Parse(
            """
            <Envelope xmlns="http://www.w3.org/2003/05/soap-envelope">
              <Body>
                <x:GetPresetsResponse xmlns:x="urn:example:not-onvif">
                  <x:Preset token="7"><x:Name>Drive</x:Name></x:Preset>
                </x:GetPresetsResponse>
              </Body>
            </Envelope>
            """);

        PtzCapabilities capabilities = OnvifClient.ParseCapabilities(raw, rawPresets, "Profile_1", "head");

        Assert.True(capabilities.PanTilt);
        Assert.True(capabilities.Zoom);
        Assert.True(capabilities.Home);
        Assert.Equal(new PtzPreset("7", "Drive"), Assert.Single(capabilities.Presets));
    }

    /// <summary>
    /// A node naming exactly the spaces given, so a test can say what a camera offers without the
    /// tuple helper above having to grow a field every call site would then carry.
    /// </summary>
    private static XDocument NodeWithSpaces(params string[] spaces)
    {
        XNamespace tptz = Tptz;
        XNamespace tt = Tt;

        return new XDocument(
            new XElement(tptz + "GetNodesResponse",
                new XElement(tptz + "PTZNode",
                    new XAttribute("token", "node0"),
                    new XElement(tt + "SupportedPTZSpaces",
                        spaces.Select(s => new XElement(tt + s))),
                    new XElement(tt + "HomeSupported", "false"))));
    }

    [Fact]
    public void A_camera_offering_absolute_zoom_reports_it_alongside_continuous()
    {
        // The two are independent in ONVIF. A camera with both is driven absolutely, because that
        // is the only way the slider means a place rather than a count of our own commands.
        PtzCapabilities capabilities = OnvifClient.ParseCapabilities(
            NodeWithSpaces("ContinuousZoomVelocitySpace", "AbsoluteZoomPositionSpace"),
            NoPresets, "Profile_1", "node0");

        Assert.True(capabilities.Zoom);
        Assert.True(capabilities.AbsoluteZoom);
    }

    [Fact]
    public void Continuous_zoom_without_an_absolute_space_reports_no_absolute_zoom()
    {
        // The common case, and the one that has to keep working: velocity nudges plus a GetStatus
        // read-back. Claiming absolute here would send an AbsoluteMove the camera rejects.
        PtzCapabilities capabilities = OnvifClient.ParseCapabilities(
            NodeWithSpaces("ContinuousZoomVelocitySpace"),
            NoPresets, "Profile_1", "node0");

        Assert.True(capabilities.Zoom);
        Assert.False(capabilities.AbsoluteZoom);
    }

    [Fact]
    public void Absolute_pan_tilt_is_not_read_as_absolute_zoom()
    {
        // The helper above emits AbsolutePanTiltPositionSpace on every node, so a parser matching
        // loosely on "Absolute" would call every pan/tilt camera absolutely zoomable.
        PtzCapabilities capabilities = OnvifClient.ParseCapabilities(
            Nodes(("node0", PanTilt: true, Zoom: true, Home: false, MaxPresets: null)),
            NoPresets, "Profile_1", "node0");

        Assert.False(capabilities.AbsoluteZoom);
    }

    [Fact]
    public void HomeSupported_false_is_not_read_as_present()
    {
        PtzCapabilities capabilities = OnvifClient.ParseCapabilities(
            Nodes(("node0", PanTilt: true, Zoom: true, Home: false, MaxPresets: null)),
            NoPresets, "Profile_1", "node0");

        Assert.False(capabilities.Home);
    }

    [Fact]
    public void The_profile_chosen_is_one_carrying_a_PTZ_configuration()
    {
        // A camera commonly offers a low-res profile with no PTZ configuration first. Driving that
        // one works on some devices and is rejected on others, so the PTZ-carrying profile wins.
        XNamespace trt = "http://www.onvif.org/ver10/media/wsdl";
        XNamespace tt = Tt;

        var profiles = new XDocument(
            new XElement(trt + "GetProfilesResponse",
                new XElement(trt + "Profiles", new XAttribute("token", "sub")),
                new XElement(trt + "Profiles", new XAttribute("token", "main"),
                    new XElement(tt + "PTZConfiguration",
                        new XAttribute("token", "PTZCfg0"),
                        new XElement(tt + "NodeToken", "node9")))));

        (string ProfileToken, string? NodeToken)? chosen = OnvifClient.ParseProfile(profiles);

        Assert.NotNull(chosen);
        Assert.Equal("main", chosen!.Value.ProfileToken);
        Assert.Equal("node9", chosen.Value.NodeToken);
    }

    [Fact]
    public void With_no_PTZ_configuration_anywhere_the_first_profile_is_used_and_names_no_node()
    {
        XNamespace trt = "http://www.onvif.org/ver10/media/wsdl";

        var profiles = new XDocument(
            new XElement(trt + "GetProfilesResponse",
                new XElement(trt + "Profiles", new XAttribute("token", "first")),
                new XElement(trt + "Profiles", new XAttribute("token", "second"))));

        (string ProfileToken, string? NodeToken)? chosen = OnvifClient.ParseProfile(profiles);

        Assert.NotNull(chosen);
        Assert.Equal("first", chosen!.Value.ProfileToken);
        Assert.Null(chosen.Value.NodeToken);
    }

    [Fact]
    public void A_camera_returning_no_profiles_is_null_rather_than_a_guess()
    {
        XNamespace trt = "http://www.onvif.org/ver10/media/wsdl";
        var profiles = new XDocument(new XElement(trt + "GetProfilesResponse"));

        Assert.Null(OnvifClient.ParseProfile(profiles));
    }

    [Fact]
    public void A_camera_with_no_PTZ_at_all_is_a_definite_answer_not_an_error()
    {
        // Measured against the live NVR: one of its two cameras is a fixed bullet that advertises
        // no PTZ service. Reporting that as a 502 would put an error banner on an ordinary camera.
        PtzCapabilities none = PtzCapabilities.None;

        Assert.False(none.PanTilt);
        Assert.False(none.Zoom);
        Assert.False(none.Home);
        Assert.Empty(none.Presets);
        Assert.Null(none.ProfileToken);
        Assert.Null(none.MaximumPresets);
    }

    [Fact]
    public void Not_supported_is_an_OnvifException_so_the_command_paths_still_catch_it()
    {
        // move/stop/preset map every OnvifException to 502, and should keep doing so: asking a
        // fixed camera to pan is a real failure. Only the capability probe treats it as an answer.
        Assert.IsAssignableFrom<OnvifException>(new OnvifNotSupportedException("no PTZ"));
    }

    [Fact]
    public void Device_information_reads_what_the_camera_calls_itself()
    {
        var response = XDocument.Parse(
            """
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
              <s:Body>
                <tds:GetDeviceInformationResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
                  <tds:Manufacturer>Reolink</tds:Manufacturer>
                  <tds:Model>RLC-810A</tds:Model>
                  <tds:FirmwareVersion>3.1.0.956</tds:FirmwareVersion>
                  <tds:SerialNumber>00000000000000</tds:SerialNumber>
                  <tds:HardwareId>IPC_523128M8MP</tds:HardwareId>
                </tds:GetDeviceInformationResponse>
              </s:Body>
            </s:Envelope>
            """);

        OnvifDeviceInformation information = OnvifClient.ParseDeviceInformation(response);

        Assert.Equal("Reolink", information.Manufacturer);
        Assert.Equal("RLC-810A", information.Model);
        Assert.Equal("3.1.0.956", information.FirmwareVersion);
        Assert.Equal("IPC_523128M8MP", information.HardwareId);
    }

    [Fact]
    public void A_field_the_camera_omits_or_blanks_reads_as_null()
    {
        // ONVIF requires all five; cameras omit them anyway, and some send an empty element. The
        // caller needs one shape for "did not say" so it can leave the subtitle short rather than
        // printing "unknown".
        var response = XDocument.Parse(
            """
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
              <s:Body>
                <tds:GetDeviceInformationResponse xmlns:tds="http://www.onvif.org/ver10/device/wsdl">
                  <tds:Manufacturer>Acme</tds:Manufacturer>
                  <tds:Model>   </tds:Model>
                </tds:GetDeviceInformationResponse>
              </s:Body>
            </s:Envelope>
            """);

        OnvifDeviceInformation information = OnvifClient.ParseDeviceInformation(response);

        Assert.Equal("Acme", information.Manufacturer);
        Assert.Null(information.Model);
        Assert.Null(information.FirmwareVersion);
        Assert.Null(information.SerialNumber);
    }
}
