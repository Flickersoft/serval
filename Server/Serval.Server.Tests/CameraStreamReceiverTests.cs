using Serval.Server.GoogleHome;

namespace Serval.Server.Tests;

/// <summary>
/// Serval's Cast Web Receiver — the page a Cast device runs when the EXECUTE response names it.
///
/// <para>Almost all of this page's behaviour lives in a browser on a television and cannot be
/// asserted from here. What can be, and what these cover, is the part that fails silently: the page
/// reaching a Cast device with a placeholder still in it, or with an ICE literal that is not valid
/// JavaScript. Either one is a black screen with nothing in any log, because the receiver never
/// gets far enough to report anything.</para>
/// </summary>
public class CameraStreamReceiverTests
{
    /// <summary>
    /// The normal case: no TURN configured, which is right for a Cast device and go2rtc on one LAN.
    ///
    /// <para><c>[]</c> rather than nothing, because the substitution lands in the middle of a
    /// JavaScript expression. An empty string there is a syntax error, and a syntax error in this
    /// file stops the page before it can fall back to HLS — so the one deployment shape that needs
    /// no configuration at all would be the one that breaks.</para>
    /// </summary>
    [Fact]
    public void No_ice_servers_renders_an_empty_array()
    {
        string page = CameraStreamReceiver.Render(null);

        Assert.Contains("var ICE_SERVERS = [];", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// A configured TURN or STUN server reaches the page. It cannot arrive any other way: the
    /// <c>cameraStreamIceServers</c> field Google carries is read by Google's player, which is not
    /// the one running when this page is.
    /// </summary>
    [Fact]
    public void Configured_ice_servers_are_inlined()
    {
        string page = CameraStreamReceiver.Render("""[{"urls":"turn:turn.example.com:3478"}]""");

        Assert.Contains(
            """var ICE_SERVERS = [{"urls":"turn:turn.example.com:3478"}];""",
            page,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// No placeholder survives into what is served. A page shipped with one still in it parses as
    /// far as the substitution and then stops, which on a television is indistinguishable from a
    /// camera that will not start.
    /// </summary>
    [Fact]
    public void Nothing_is_left_unsubstituted()
    {
        string page = CameraStreamReceiver.Render(null);

        Assert.DoesNotContain("$ICE_SERVERS$", page, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two things the page is for, asserted so a refactor cannot quietly drop either: it loads
    /// Google's receiver framework, and it signals against the same route Google's own cloud uses.
    /// </summary>
    [Fact]
    public void The_page_loads_the_cast_framework_and_signals_against_Servals_own_route()
    {
        string page = CameraStreamReceiver.Render(null);

        Assert.Contains("cast_receiver_framework.js", page, StringComparison.Ordinal);
        Assert.Contains("/api/google/camerastream/signal?t=", page, StringComparison.Ordinal);
    }
}
