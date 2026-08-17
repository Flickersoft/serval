using System.Xml.Linq;
using Serval.Server.Onvif;

namespace Serval.Server.Tests;

/// <summary>
/// Reading where a camera says its lens is. Pure over the SOAP document for the same reason the
/// capability parsing is: a wrong answer here does not throw, it parks the zoom knob somewhere the
/// lens is not — indistinguishable, from the outside, from dead reckoning.
///
/// The null cases carry the weight. <c>Position</c> is optional in the specification, cameras omit
/// it freely, and every one of those has to come back "we do not know" rather than zero: a zoom
/// knob resting at the wide end is a claim, and it is wrong exactly when the lens is zoomed in.
/// </summary>
public class OnvifPtzStatusTests
{
    private const string Tptz = "http://www.onvif.org/ver20/ptz/wsdl";
    private const string Tt = "http://www.onvif.org/ver10/schema";

    private const string ZoomSpace =
        "http://www.onvif.org/ver10/tptz/ZoomSpaces/PositionGenericSpace";
    private const string PanTiltSpace =
        "http://www.onvif.org/ver10/tptz/PanTiltSpaces/PositionGenericSpace";

    /// <summary>A GetStatus response. Null for either part leaves that element out entirely.</summary>
    private static XDocument Status(
        (double X, double Y, string? Space)? panTilt,
        (double X, string? Space)? zoom,
        bool withPosition = true)
    {
        XNamespace tptz = Tptz;
        XNamespace tt = Tt;

        return new XDocument(
            new XElement(tptz + "GetStatusResponse",
                new XElement(tptz + "PTZStatus",
                    withPosition
                        ? new XElement(tt + "Position",
                            panTilt is { } pt
                                ? new XElement(tt + "PanTilt",
                                    new XAttribute("x", pt.X.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                                    new XAttribute("y", pt.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                                    pt.Space is null ? null : new XAttribute("space", pt.Space))
                                : null,
                            zoom is { } z
                                ? new XElement(tt + "Zoom",
                                    new XAttribute("x", z.X.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                                    z.Space is null ? null : new XAttribute("space", z.Space))
                                : null)
                        : null,
                    new XElement(tt + "MoveStatus",
                        new XElement(tt + "PanTilt", "IDLE"),
                        new XElement(tt + "Zoom", "IDLE")))));
    }

    [Fact]
    public void A_full_position_reads_every_axis()
    {
        PtzStatus status = OnvifClient.ParseStatus(
            Status(panTilt: (0.25, -0.5, PanTiltSpace), zoom: (0.4, ZoomSpace)));

        Assert.Equal(0.4, status.Zoom);
        Assert.Equal(0.25, status.Pan);
        Assert.Equal(-0.5, status.Tilt);
    }

    [Fact]
    public void A_response_with_no_position_reports_nothing_rather_than_zero()
    {
        // The case that matters most: plenty of cameras answer GetStatus with a move status and a
        // clock and no position at all. Zero here would park the knob at the wide end and claim it.
        PtzStatus status = OnvifClient.ParseStatus(
            Status(panTilt: null, zoom: null, withPosition: false));

        Assert.Null(status.Zoom);
        Assert.Null(status.Pan);
        Assert.Null(status.Tilt);
    }

    [Fact]
    public void A_fixed_lens_on_a_moving_head_reports_pan_tilt_and_no_zoom()
    {
        // The live NVR's pan/tilt camera. Position carries PanTilt and simply omits Zoom.
        PtzStatus status = OnvifClient.ParseStatus(
            Status(panTilt: (0.1, 0.2, PanTiltSpace), zoom: null));

        Assert.Null(status.Zoom);
        Assert.Equal(0.1, status.Pan);
        Assert.Equal(0.2, status.Tilt);
    }

    [Fact]
    public void A_zoom_only_camera_reports_zoom_and_no_pan_tilt()
    {
        PtzStatus status = OnvifClient.ParseStatus(
            Status(panTilt: null, zoom: (0.75, ZoomSpace)));

        Assert.Equal(0.75, status.Zoom);
        Assert.Null(status.Pan);
        Assert.Null(status.Tilt);
    }

    [Fact]
    public void An_absent_space_attribute_is_taken_as_the_generic_one()
    {
        // ONVIF says a missing space means the profile's default, which for these axes is generic.
        // Cameras leave it off constantly, and rejecting those would put most devices on dead
        // reckoning for no reason.
        PtzStatus status = OnvifClient.ParseStatus(
            Status(panTilt: (0.3, 0.4, null), zoom: (0.6, null)));

        Assert.Equal(0.6, status.Zoom);
        Assert.Equal(0.3, status.Pan);
        Assert.Equal(0.4, status.Tilt);
    }

    [Fact]
    public void A_vendor_space_is_dropped_rather_than_scaled()
    {
        // The subtle one. A vendor space is a number in a range the specification does not define —
        // it could be 0..100 or millimetres of focal length — so putting it on a 0..1 track would
        // be inventing the scale. Unknown is the honest answer.
        PtzStatus status = OnvifClient.ParseStatus(
            Status(
                panTilt: (12, 30, "urn:vendor:PanTiltSpaces/Degrees"),
                zoom: (44, "urn:vendor:ZoomSpaces/Steps")));

        Assert.Null(status.Zoom);
        Assert.Null(status.Pan);
        Assert.Null(status.Tilt);
    }

    [Fact]
    public void A_position_slightly_out_of_range_is_clamped_not_dropped()
    {
        // A camera reporting 1.0000001 is saying it is at the far end, not answering in a
        // different space. Dropping that would flip a working read-back to unknown at one extreme.
        PtzStatus status = OnvifClient.ParseStatus(
            Status(panTilt: (-1.0000001, 1.0000001, PanTiltSpace), zoom: (1.0000001, ZoomSpace)));

        Assert.Equal(1, status.Zoom);
        Assert.Equal(-1, status.Pan);
        Assert.Equal(1, status.Tilt);
    }

    [Fact]
    public void An_unparseable_coordinate_reports_nothing()
    {
        var raw = XDocument.Parse(
            """
            <Envelope xmlns="http://www.w3.org/2003/05/soap-envelope">
              <Body>
                <GetStatusResponse xmlns="http://www.onvif.org/ver20/ptz/wsdl">
                  <PTZStatus>
                    <Position xmlns="http://www.onvif.org/ver10/schema">
                      <Zoom x="NaN"/>
                    </Position>
                  </PTZStatus>
                </GetStatusResponse>
              </Body>
            </Envelope>
            """);

        Assert.Null(OnvifClient.ParseStatus(raw).Zoom);
    }

    [Fact]
    public void Elements_are_matched_by_local_name_whatever_the_prefix()
    {
        // Same reason as the capability parser: devices vary prefixes freely, and a few put these
        // in namespaces the specification does not suggest.
        var raw = XDocument.Parse(
            """
            <Envelope xmlns="http://www.w3.org/2003/05/soap-envelope">
              <Body>
                <weird:GetStatusResponse xmlns:weird="urn:example:not-onvif">
                  <weird:PTZStatus>
                    <weird:Position>
                      <weird:PanTilt x="0.5" y="-0.25"/>
                      <weird:Zoom x="0.8"/>
                    </weird:Position>
                  </weird:PTZStatus>
                </weird:GetStatusResponse>
              </Body>
            </Envelope>
            """);

        PtzStatus status = OnvifClient.ParseStatus(raw);

        Assert.Equal(0.8, status.Zoom);
        Assert.Equal(0.5, status.Pan);
        Assert.Equal(-0.25, status.Tilt);
    }

    [Fact]
    public void A_nested_position_elsewhere_in_the_envelope_is_not_mistaken_for_the_axes()
    {
        // Position's children are read as elements of Position itself rather than by descending,
        // so a Preset carrying its own PTZPosition cannot supply an axis the status left out.
        var raw = XDocument.Parse(
            """
            <Envelope xmlns="http://www.w3.org/2003/05/soap-envelope">
              <Body>
                <GetStatusResponse xmlns="http://www.onvif.org/ver20/ptz/wsdl">
                  <PTZStatus>
                    <Position xmlns="http://www.onvif.org/ver10/schema">
                      <Zoom x="0.2"/>
                    </Position>
                  </PTZStatus>
                </GetStatusResponse>
              </Body>
            </Envelope>
            """);

        PtzStatus status = OnvifClient.ParseStatus(raw);

        Assert.Equal(0.2, status.Zoom);
        Assert.Null(status.Pan);
        Assert.Null(status.Tilt);
    }
}
