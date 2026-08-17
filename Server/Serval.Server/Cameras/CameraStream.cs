using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace Serval.Server.Cameras;

/// <summary>
/// What a stream is used for. A camera typically offers a high-quality main stream and a small
/// sub stream, and the two are good at different jobs: record the main, run detection on the sub.
/// </summary>
[JsonConverter(typeof(StreamRoleConverter))]
public enum StreamRole
{
    /// <summary>
    /// Written to disk as HLS and indexed for playback. At most one per camera, and the only role
    /// a camera may go without: a camera with no record stream is watched and viewable but keeps
    /// nothing.
    /// </summary>
    Record,

    /// <summary>
    /// Source of the ~1 fps snapshots that feed motion detection, scene description, the dashboard
    /// wall, and <c>/snapshot.jpg</c> — plus the audio the AI transcribes. Exactly one per camera.
    /// </summary>
    Detect,

    /// <summary>
    /// The low-latency WebRTC view, registered with go2rtc. Exactly one per camera.
    /// </summary>
    Live,
}

/// <summary>
/// Writes roles as <c>"record"</c> / <c>"detect"</c> / <c>"live"</c> rather than as numbers or
/// as their PascalCase member names. A subclass because <c>[JsonConverter]</c> takes a type, not
/// a constructed converter, and the naming policy is a constructor argument.
///
/// It matters more than cosmetics: this is a hand-edited field. Cameras are added through the
/// Scalar UI, and a role list that reads back differently from how it is documented and written
/// invites the guess that the casing is significant.
/// </summary>
public sealed class StreamRoleConverter()
    : JsonStringEnumConverter<StreamRole>(JsonNamingPolicy.CamelCase);

/// <summary>
/// One source URL a camera offers, and what the server uses it for.
///
/// Splitting a camera into streams is what lets detection cost be chosen independently of
/// recording quality: a 4K main stream can be recorded untouched while a 640x360 sub stream does
/// the per-second decoding that motion and vision need.
/// </summary>
public sealed class CameraStream
{
    /// <summary>
    /// Stable key within the camera — conventionally <c>main</c> / <c>sub</c>. Only used to
    /// identify the stream in the registry and in logs, so it carries the same safe-character
    /// rule as a camera id rather than being free text.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Where to pull from: <c>rtsp(s)://</c>, <c>http(s)://</c> (including HTTP-FLV),
    /// <c>rtmp(s)://</c>, <c>srt://</c>, or a local path to a video file, which is looped in
    /// realtime as a stand-in for a live camera.
    /// </summary>
    public required string Url { get; set; }

    /// <summary>
    /// What this stream is for. <c>detect</c> and <c>live</c> must each be claimed by exactly one of
    /// the camera's streams and <c>record</c> by at most one, so a one-stream camera declares all
    /// three: <c>["record","detect","live"]</c>.
    ///
    /// Empty is legal and means the stream is stored but never pulled — a source kept against a
    /// change of mind, with its address and its transcode intact. <see cref="CameraRegistryCheck"/>
    /// names such a stream in the log, since a typo in a role list looks the same from here.
    /// </summary>
    public List<StreamRole> Roles { get; set; } = [];

    /// <summary>
    /// Re-encode this stream instead of recording it as it arrives.
    ///
    /// Null — the default, and the right answer for almost every camera — means <c>-c:v copy</c>:
    /// no decode, no encode, the camera's own bits land in the archive. Set it when the camera
    /// sends something fMP4 cannot carry, or when you want a smaller archive than the camera will
    /// produce. Transcoding costs roughly a core (or a share of a GPU) per camera, continuously,
    /// and changes what you archived, so it is a per-stream decision rather than a server default.
    ///
    /// Only meaningful on the stream carrying <see cref="StreamRole.Record"/> — nothing else is
    /// written to disk — and validation rejects it on a stream doing some other job rather than
    /// ignoring it. A stream with no roles at all keeps one it already had, inert, so taking a
    /// stream out of service and putting it back does not lose the setting.
    /// </summary>
    [BsonIgnoreIfNull]
    public StreamTranscode? Transcode { get; set; }

    /// <summary>
    /// A copy whose <see cref="Url"/> has had any <c>user:password@</c> removed, leaving an address
    /// that still says where the stream comes from without saying how to open it.
    /// See <see cref="Camera.WithoutSecrets"/> for why that distinction is worth drawing.
    /// </summary>
    internal CameraStream WithoutSecrets()
    {
        var copy = (CameraStream)MemberwiseClone();
        copy.Url = StripUserInfo(Url);
        return copy;
    }

    /// <summary>
    /// Removes the userinfo from a URL, by hand rather than through <see cref="Uri"/>.
    ///
    /// <see cref="Uri"/> is not usable here for the same reason
    /// <see cref="Ingest.SourceArguments"/> avoids it: a camera URL is whatever the camera's web UI
    /// printed, and real ones carry characters <see cref="Uri"/> rejects outright. A parse that
    /// throws on the awkward URLs would redact only the tidy ones, which is the wrong half.
    ///
    /// The '@' is found from the right, because a password containing an unescaped '@' is common in
    /// the wild even though the RFC says it should have been encoded.
    /// </summary>
    internal static string StripUserInfo(string url)
    {
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
        {
            return url; // a bare file path — no authority, so nothing to strip
        }

        int authority = scheme + 3;
        int authorityEnd = url.IndexOfAny(['/', '?', '#'], authority);
        if (authorityEnd < 0)
        {
            authorityEnd = url.Length;
        }

        if (authorityEnd <= authority)
        {
            return url;
        }

        int at = url.LastIndexOf('@', authorityEnd - 1, authorityEnd - authority);
        return at < 0 ? url : string.Concat(url[..authority], url[(at + 1)..]);
    }
}

/// <summary>
/// What one stream is re-encoded to. The codec is one of <see cref="Ingest.EncoderSelector"/>'s
/// supported targets — h264, vp9, av1 — validated at the API against what this host's ffmpeg can
/// actually encode, so an impossible pairing is a 400 rather than a camera that never records.
/// </summary>
public sealed class StreamTranscode
{
    public required string Codec { get; set; }

    /// <summary>
    /// ffmpeg <c>-b:v</c> syntax, e.g. <c>4M</c> or <c>2000k</c>. Null falls back to
    /// <c>Serval:Ingest:Bitrate</c>.
    /// </summary>
    public string? Bitrate { get; set; }
}
