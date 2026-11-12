using System.Net;
using System.Net.Sockets;

namespace Serval.Server.WebRtc;

/// <summary>
/// Adds host candidates to a go2rtc SDP answer: the address the browser reached this server at,
/// on go2rtc's published media port.
///
/// <para><b>Why the server has to.</b> go2rtc in a Docker bridge network only sees its container
/// address, and drops those from its answers (172.16.0.0/12, by design). Without a configured
/// candidate the browser is offered only what STUN finds — the public address, which a LAN client
/// usually cannot hairpin through. The address in the browser's own address bar is, by
/// construction, one it can reach this host at, and go2rtc's port is published on the same host.
/// go2rtc's documentation is explicit that extra candidates cost nothing, so this is added
/// alongside whatever go2rtc offered rather than instead of it.</para>
///
/// <para>Answers are not trickled — go2rtc returns every candidate inline — so the new lines go
/// into the first media section, which BUNDLE makes the one that carries the transport.</para>
/// </summary>
public static class SdpCandidates
{
    /// <summary>
    /// The addresses a browser that sent <paramref name="host"/> as its Host header can reach this
    /// server at: the literal itself, or what the name resolves to here. Empty when it resolves to
    /// nothing in time — the answer then goes out as go2rtc wrote it.
    /// </summary>
    public static async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        string bare = host.Trim().TrimStart('[').TrimEnd(']');
        if (bare.Length == 0)
        {
            return [];
        }

        if (IPAddress.TryParse(bare, out IPAddress? literal))
        {
            return [literal];
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            IPAddress[] resolved = await Dns.GetHostAddressesAsync(bare, timeout.Token);
            return resolved
                .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Distinct()
                .ToList();
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return [];
        }
    }

    /// <summary>
    /// <paramref name="sdp"/> with a UDP and a passive TCP host candidate for each address at
    /// <paramref name="port"/>, skipping any address and port the answer already offers.
    /// </summary>
    public static string AddHostCandidates(string sdp, IReadOnlyList<IPAddress> addresses, int port)
    {
        if (addresses.Count == 0 || port <= 0)
        {
            return sdp;
        }

        string newline = sdp.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        List<string> lines = [.. sdp.Split(newline)];

        int firstMedia = lines.FindIndex(l => l.StartsWith("m=", StringComparison.Ordinal));
        if (firstMedia < 0)
        {
            return sdp;
        }

        int sectionEnd = lines.FindIndex(firstMedia + 1, l => l.StartsWith("m=", StringComparison.Ordinal));
        if (sectionEnd < 0)
        {
            sectionEnd = lines.Count;
            // A trailing newline leaves an empty last element; insert before it, not after.
            while (sectionEnd > firstMedia + 1 && lines[sectionEnd - 1].Length == 0)
            {
                sectionEnd--;
            }
        }

        // Before a closing end-of-candidates, where one is present: nothing may follow it.
        int insertAt = lines.FindIndex(firstMedia, sectionEnd - firstMedia,
            l => l.StartsWith("a=end-of-candidates", StringComparison.Ordinal));
        if (insertAt < 0)
        {
            insertAt = sectionEnd;
        }

        var added = new List<string>();
        int foundation = 0;
        foreach (IPAddress address in addresses)
        {
            string ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
            if (sdp.Contains($" {ip} {port} typ ", StringComparison.Ordinal))
            {
                continue;
            }

            // Host-candidate priorities per RFC 8445 §5.1.2.1 (type preference 126 for UDP; TCP
            // ranked just below so a browser that can use either tries UDP first).
            added.Add($"a=candidate:serval{foundation} 1 udp 2130706431 {ip} {port} typ host");
            added.Add($"a=candidate:serval{foundation + 1} 1 tcp 1684797439 {ip} {port} typ host tcptype passive");
            foundation += 2;
        }

        if (added.Count == 0)
        {
            return sdp;
        }

        lines.InsertRange(insertAt, added);
        return string.Join(newline, lines);
    }
}
