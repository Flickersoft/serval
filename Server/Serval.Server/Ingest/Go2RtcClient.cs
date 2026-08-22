using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Serval.Server.Ingest;

/// <summary>
/// The go2rtc operations the server needs: enumerate the sidecar's configured streams, add or
/// remove one, and forward a browser's SDP offer for a stream to get the answer back. Kept behind
/// an interface so <see cref="Go2RtcSyncWorker"/>'s reconcile logic can be unit-tested against a
/// fake with no sidecar running — the same seam the ingest side gets from its process boundary.
/// </summary>
public interface IGo2RtcClient
{
    /// <summary>Names of every stream go2rtc currently has configured.</summary>
    Task<IReadOnlySet<string>> ListStreamNamesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Adds (or replaces) a stream named <paramref name="name"/> drawing on <paramref name="sources"/>.
    ///
    /// <para>A go2rtc stream is a set of sources, not one: it negotiates per consumer across all of
    /// them, so a track a consumer asks for can come from a source that exists only to supply it.
    /// Later sources may name this stream, which is how one is built on top of another without
    /// opening a second session to the camera.</para>
    /// </summary>
    Task PutStreamAsync(string name, IReadOnlyList<string> sources, CancellationToken cancellationToken);

    /// <summary>Removes the stream named <paramref name="name"/>.</summary>
    Task DeleteStreamAsync(string name, CancellationToken cancellationToken);

    /// <summary>
    /// Forwards an SDP offer for <paramref name="streamName"/> to go2rtc's WebRTC endpoint and
    /// returns the SDP answer. This is the whole of the server's role in signaling — the media
    /// itself never touches the server.
    /// </summary>
    Task<string> ExchangeSdpAsync(string streamName, string offerSdp, CancellationToken cancellationToken);
}

/// <summary>
/// A thin typed <see cref="HttpClient"/> over go2rtc's HTTP API. go2rtc's stream API has one
/// quirk worth pinning down: <b>PUT</b> identifies the stream by <c>name</c> and takes its sources
/// as repeated <c>src</c> parameters, but <b>DELETE</b> identifies the stream by <c>src</c>. We map
/// <c>name</c> to the camera id throughout, so callers never see this.
/// </summary>
public sealed class Go2RtcClient : IGo2RtcClient
{
    private const string SdpContentType = "application/sdp";

    private readonly HttpClient _http;

    public Go2RtcClient(HttpClient http) => _http = http;

    public async Task<IReadOnlySet<string>> ListStreamNamesAsync(CancellationToken cancellationToken)
    {
        // GET /api/streams returns a JSON object keyed by stream name; we only need the keys.
        await using Stream body = await _http.GetStreamAsync("api/streams", cancellationToken);
        using JsonDocument doc = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty stream in doc.RootElement.EnumerateObject())
        {
            names.Add(stream.Name);
        }
        return names;
    }

    public async Task PutStreamAsync(string name, IReadOnlyList<string> sources, CancellationToken cancellationToken)
    {
        // `src` is read as a repeated parameter, not a single value, and the whole set replaces
        // whatever the stream had. Order is preserved and matters: a source naming this stream has
        // to come after the one that supplies it.
        string src = string.Join('&', sources.Select(s => $"src={Uri.EscapeDataString(s)}"));
        string url = $"api/streams?name={Uri.EscapeDataString(name)}&{src}";
        using HttpResponseMessage response = await _http.PutAsync(url, content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteStreamAsync(string name, CancellationToken cancellationToken)
    {
        // DELETE keys the stream off `src` (go2rtc's naming, not a bug on our side).
        string url = $"api/streams?src={Uri.EscapeDataString(name)}";
        using HttpResponseMessage response = await _http.DeleteAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string> ExchangeSdpAsync(string streamName, string offerSdp, CancellationToken cancellationToken)
    {
        string url = $"api/webrtc?src={Uri.EscapeDataString(streamName)}";
        using var content = new StringContent(offerSdp);
        content.Headers.ContentType = new MediaTypeHeaderValue(SdpContentType);

        using HttpResponseMessage response = await _http.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
