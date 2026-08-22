using Serval.Server.GoogleHome;

namespace Serval.Server.Tests;

/// <summary>
/// The log line that exists to answer "why did the handshake succeed and the picture never arrive".
///
/// <para>Its whole job is to be readable in a log and never to throw: it parses SDP written by
/// somebody else's WebRTC stack, on a live stream request, purely to produce a diagnostic. A shape
/// it does not recognise must degrade the summary, not the request.</para>
/// </summary>
public class SdpSummaryTests
{
    /// <summary>
    /// A host candidate means the far end is on a network that can reach us directly; srflx or
    /// relay means it is not. That distinction is the reason this class exists.
    /// </summary>
    [Fact]
    public void It_names_the_media_codecs_and_candidate_types()
    {
        const string sdp = """
            v=0
            o=- 1 2 IN IP4 127.0.0.1
            a=group:BUNDLE 0 1
            m=video 9 UDP/TLS/RTP/SAVPF 96
            a=rtpmap:96 H264/90000
            a=setup:actpass
            a=candidate:1 1 udp 2130706431 192.168.1.20 8555 typ host
            a=candidate:2 1 udp 1694498815 203.0.113.7 34567 typ srflx
            m=audio 9 UDP/TLS/RTP/SAVPF 111
            a=rtpmap:111 opus/48000/2
            """;

        string summary = SdpSummary.Describe(sdp);

        Assert.Contains("video+audio", summary, StringComparison.Ordinal);
        Assert.Contains("H264", summary, StringComparison.Ordinal);
        Assert.Contains("opus", summary, StringComparison.Ordinal);
        Assert.Contains("host 192.168.1.20:8555/udp", summary, StringComparison.Ordinal);
        Assert.Contains("srflx 203.0.113.7:34567/udp", summary, StringComparison.Ordinal);
        Assert.Contains("setup=actpass", summary, StringComparison.Ordinal);
        Assert.Contains("bundle", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// A port of 0 is how a negotiation says "not this one". It is the quiet path to a session
    /// that connects and shows nothing, so it has to be visible rather than read as ordinary.
    /// </summary>
    [Fact]
    public void A_rejected_media_section_is_called_out()
    {
        const string sdp = """
            v=0
            m=video 0 UDP/TLS/RTP/SAVPF 96
            a=rtpmap:96 H264/90000
            """;

        Assert.Contains("video:REJECTED", SdpSummary.Describe(sdp), StringComparison.Ordinal);
    }

    /// <summary>
    /// No candidates at all is worth seeing plainly: this signaling contract is a single exchange,
    /// so there is nowhere for a trickled candidate to arrive later.
    /// </summary>
    [Fact]
    public void An_offer_with_no_candidates_says_so()
    {
        const string sdp = """
            v=0
            m=video 9 UDP/TLS/RTP/SAVPF 96
            a=rtpmap:96 VP8/90000
            """;

        Assert.Contains("candidates=none", SdpSummary.Describe(sdp), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_at_all_is_described_rather_than_thrown(string? sdp) =>
        Assert.Equal("(empty)", SdpSummary.Describe(sdp));

    /// <summary>
    /// Truncated and malformed lines are the realistic failure, since this runs on input from
    /// another party's stack. None of them may throw on a live stream request.
    /// </summary>
    [Theory]
    [InlineData("m=")]
    [InlineData("m=video")]
    [InlineData("a=rtpmap:")]
    [InlineData("a=candidate:")]
    [InlineData("a=candidate:1 1 udp typ")]
    [InlineData("a=setup:")]
    [InlineData("\n\n\n")]
    [InlineData("not an sdp at all")]
    public void Malformed_input_never_throws(string sdp) =>
        Assert.False(string.IsNullOrEmpty(SdpSummary.Describe(sdp)));
}
