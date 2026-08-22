using System.Text;

namespace Serval.Server.GoogleHome;

/// <summary>
/// A one-line description of an SDP, for the log.
///
/// <para><b>Why this exists rather than logging the SDP itself.</b> An offer is several kilobytes
/// and unreadable at a glance, but the handful of facts that decide whether media will actually
/// flow are all in there: which media sections survived negotiation, what codecs they carry, and —
/// the question this was written to answer — <em>what kind of ICE candidates the far end is
/// offering</em>. Host candidates mean the peer is on the same network; server-reflexive or relay
/// candidates mean it is somewhere else entirely and reaching it depends on NAT or a TURN server.
/// The full SDP is still logged at Debug for when the summary is not enough.</para>
///
/// <para>Deliberately tolerant: this parses attacker-adjacent text from another party's WebRTC
/// stack purely to write a log line, so a shape it does not recognise must produce a worse summary
/// rather than an exception on a live stream request.</para>
/// </summary>
internal static class SdpSummary
{
    public static string Describe(string? sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp))
        {
            return "(empty)";
        }

        var media = new List<string>();
        var candidates = new List<string>();
        var payloads = new Dictionary<string, string>(StringComparer.Ordinal);
        string? setup = null;
        bool bundle = false;

        foreach (string raw in sdp.Split('\n'))
        {
            string line = raw.Trim();

            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                // "m=video 9 UDP/TLS/RTP/SAVPF 96 97" — a port of 0 means the section was rejected,
                // which is the quiet way a negotiation ends up with no media at all.
                string[] parts = line[2..].Split(' ');
                if (parts.Length >= 2)
                {
                    media.Add(parts[1] == "0" ? $"{parts[0]}:REJECTED" : parts[0]);
                }
            }
            else if (line.StartsWith("a=rtpmap:", StringComparison.Ordinal))
            {
                string[] parts = line[9..].Split(' ', 2);
                if (parts.Length == 2)
                {
                    payloads[parts[0]] = parts[1].Split('/')[0];
                }
            }
            else if (line.StartsWith("a=candidate:", StringComparison.Ordinal))
            {
                // "a=candidate:1 1 udp 2130706431 192.168.1.20 8555 typ host"
                string[] f = line[12..].Split(' ');
                int typ = Array.IndexOf(f, "typ");
                if (typ >= 0 && typ + 1 < f.Length && f.Length > 5)
                {
                    candidates.Add($"{f[typ + 1]} {f[4]}:{f[5]}/{f[2]}");
                }
            }
            else if (line.StartsWith("a=setup:", StringComparison.Ordinal))
            {
                setup = line[8..];
            }
            else if (line.StartsWith("a=group:BUNDLE", StringComparison.Ordinal))
            {
                bundle = true;
            }
        }

        var summary = new StringBuilder();
        summary.Append(media.Count == 0 ? "no media" : string.Join('+', media));

        if (payloads.Count > 0)
        {
            summary.Append(" codecs=").Append(string.Join(',', payloads.Values.Distinct(StringComparer.OrdinalIgnoreCase)));
        }

        summary.Append(" candidates=");
        summary.Append(candidates.Count == 0
            // Not a fault on its own: trickle-ICE offers arrive with none, and the far end sends
            // them separately. It is a fault here, because this signaling contract is one exchange
            // with nowhere for a later candidate to arrive.
            ? "none"
            : string.Join(" | ", candidates));

        if (setup is not null)
        {
            summary.Append(" setup=").Append(setup);
        }

        summary.Append(bundle ? " bundle" : " no-bundle");
        summary.Append(" bytes=").Append(sdp.Length);

        return summary.ToString();
    }
}
