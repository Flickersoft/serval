using Serval.Server.Onvif;

namespace Serval.Server.Tests;

/// <summary>
/// The check on the service address a camera reports from GetCapabilities. That address is the one
/// value in the ONVIF exchange the far end chooses, and every later call carries the camera's
/// WS-Security digest to it — so a camera that could name any host could point the Server's own
/// credentials at anything on the internal network.
/// </summary>
public class OnvifServiceAddressTests
{
    private const string Configured = "http://192.0.2.10/onvif/device_service";

    /// <summary>
    /// The case this exists for. go2rtc's API is unauthenticated and takes an exec: source; Mongo
    /// needs no credential at all. Neither is reachable from outside, which is the whole reason
    /// being able to reach them from inside would be worth something.
    /// </summary>
    [Theory]
    [InlineData("http://go2rtc:1984/api/streams?src=exec:sh")]
    [InlineData("http://mongo:27017/")]
    [InlineData("http://127.0.0.1:8080/api/cameras")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://192.0.2.99/onvif/ptz")] // a different camera on the same subnet
    public void An_address_on_another_host_is_refused(string xaddr)
    {
        OnvifException ex = Assert.Throws<OnvifException>(
            () => OnvifClient.RequireCameraOwnHost(xaddr, Configured, "front-door", "PTZ"));

        Assert.Contains("front-door", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cameras really do answer on :80 and advertise their services on another port, so requiring
    /// the port to match would break working hardware to close nothing — the pivot being stopped is
    /// to a different machine, and a port is not one.
    /// </summary>
    [Theory]
    [InlineData("http://192.0.2.10/onvif/ptz_service")]
    [InlineData("http://192.0.2.10:8000/onvif/ptz_service")]
    [InlineData("http://192.0.2.10:80/onvif/Media")]
    public void The_same_host_on_any_port_is_accepted(string xaddr) =>
        Assert.Equal(xaddr, OnvifClient.RequireCameraOwnHost(xaddr, Configured, "front-door", "PTZ"));

    /// <summary>
    /// Plain http has to keep working. Most ONVIF cameras have no certificate and never will, and a
    /// check that quietly required TLS would disable PTZ on the hardware this is written for.
    /// </summary>
    [Fact]
    public void Plain_http_is_not_treated_as_a_problem() =>
        Assert.Equal(
            "http://192.0.2.10/onvif/ptz_service",
            OnvifClient.RequireCameraOwnHost(
                "http://192.0.2.10/onvif/ptz_service", Configured, "front-door", "PTZ"));

    [Fact]
    public void Https_is_accepted_too() =>
        Assert.Equal(
            "https://192.0.2.10/onvif/ptz_service",
            OnvifClient.RequireCameraOwnHost(
                "https://192.0.2.10/onvif/ptz_service", Configured, "front-door", "PTZ"));

    [Fact]
    public void Hostnames_compare_without_regard_to_case() =>
        Assert.Equal(
            "http://CAM-01.lan/onvif/ptz",
            OnvifClient.RequireCameraOwnHost(
                "http://CAM-01.lan/onvif/ptz", "http://cam-01.lan/onvif/device_service", "c", "PTZ"));

    [Theory]
    [InlineData("file:///etc/shadow")]
    [InlineData("ftp://192.0.2.10/")]
    [InlineData("gopher://192.0.2.10:70/")]
    public void A_scheme_that_is_not_http_is_refused(string xaddr) =>
        Assert.Throws<OnvifException>(
            () => OnvifClient.RequireCameraOwnHost(xaddr, Configured, "front-door", "PTZ"));

    [Theory]
    [InlineData("not a url")]
    [InlineData("/onvif/ptz_service")] // relative: some cameras do this, and it is still unusable
    [InlineData("")]
    public void An_address_that_will_not_parse_is_refused(string xaddr) =>
        Assert.Throws<OnvifException>(
            () => OnvifClient.RequireCameraOwnHost(xaddr, Configured, "front-door", "PTZ"));

    [Fact]
    public void An_unusable_configured_url_is_refused_rather_than_ignored() =>
        Assert.Throws<OnvifException>(
            () => OnvifClient.RequireCameraOwnHost(
                "http://192.0.2.10/onvif/ptz", "not a url", "front-door", "PTZ"));
}
