using System.Net;
using Serval.Server.WebRtc;

namespace Serval.Server.Tests;

/// <summary>
/// The host candidates added to go2rtc's answers, which are what let live view connect on a LAN with
/// nothing configured. See <see cref="SdpCandidates"/>.
/// </summary>
public class SdpCandidatesTests
{
    private const string Answer =
        "v=0\r\n"
        + "o=- 1 1 IN IP4 0.0.0.0\r\n"
        + "s=-\r\n"
        + "t=0 0\r\n"
        + "a=group:BUNDLE 0 1\r\n"
        + "m=video 9 UDP/TLS/RTP/SAVPF 96\r\n"
        + "c=IN IP4 0.0.0.0\r\n"
        + "a=mid:0\r\n"
        + "a=candidate:1 1 udp 2130706431 203.0.113.4 8666 typ srflx raddr 0.0.0.0 rport 8666\r\n"
        + "a=end-of-candidates\r\n"
        + "m=audio 9 UDP/TLS/RTP/SAVPF 111\r\n"
        + "a=mid:1\r\n";

    [Fact]
    public void AddsUdpAndTcpCandidatesToTheFirstMediaSectionBeforeEndOfCandidates()
    {
        string result = SdpCandidates.AddHostCandidates(Answer, [IPAddress.Parse("192.168.1.20")], 8666);
        string[] lines = result.Split("\r\n");

        int udp = Array.FindIndex(lines, l => l.Contains(" udp ") && l.Contains("192.168.1.20 8666 typ host"));
        int tcp = Array.FindIndex(lines, l => l.Contains(" tcp ") && l.Contains("192.168.1.20 8666 typ host tcptype passive"));
        int end = Array.IndexOf(lines, "a=end-of-candidates");
        int secondMedia = Array.FindIndex(lines, l => l.StartsWith("m=audio", StringComparison.Ordinal));

        Assert.True(udp > 0 && tcp > 0, result);
        Assert.True(udp < end && tcp < end, "candidates must precede end-of-candidates");
        Assert.True(end < secondMedia);
        Assert.EndsWith("\r\n", result);
    }

    [Fact]
    public void LeavesTheAnswerAloneWhenGo2RtcAlreadyOffersThatAddress()
    {
        string result = SdpCandidates.AddHostCandidates(Answer, [IPAddress.Parse("203.0.113.4")], 8666);

        Assert.Equal(Answer, result);
    }

    [Fact]
    public void PortZeroOrNoAddressesChangesNothing()
    {
        Assert.Equal(Answer, SdpCandidates.AddHostCandidates(Answer, [IPAddress.Parse("192.168.1.20")], 0));
        Assert.Equal(Answer, SdpCandidates.AddHostCandidates(Answer, [], 8666));
    }

    [Fact]
    public void WritesAMappedAddressAsPlainIPv4()
    {
        IPAddress mapped = IPAddress.Parse("192.168.1.20").MapToIPv6();

        string result = SdpCandidates.AddHostCandidates(Answer, [mapped], 8666);

        Assert.Contains(" 192.168.1.20 8666 typ host", result);
        Assert.DoesNotContain("::ffff:", result);
    }

    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20")]
    [InlineData("[2001:db8::20]", "2001:db8::20")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    public async Task ResolvesALiteralHostWithoutDns(string host, string expected)
    {
        IReadOnlyList<IPAddress> addresses = await SdpCandidates.ResolveAsync(host, TestContext.Current.CancellationToken);

        Assert.Equal([IPAddress.Parse(expected)], addresses);
    }

    [Fact]
    public async Task AnUnresolvableNameGivesNoAddresses()
    {
        IReadOnlyList<IPAddress> addresses =
            await SdpCandidates.ResolveAsync("does-not-exist.invalid", TestContext.Current.CancellationToken);

        Assert.Empty(addresses);
    }
}
