using Serval.Server.Cameras;
using Serval.Server.Configuration;

namespace Serval.Server.Ingest;

/// <summary>
/// A camera is configured in a way that cannot produce a working ffmpeg — an unrecordable source
/// codec, or a transcode this host has no encoder for.
///
/// Distinct from a source that is merely unreachable, and the distinction is the whole reason for
/// the type: an unreachable camera should be retried at two seconds, forever, because it will come
/// back. A misconfigured one will not come back until someone changes something, so retrying it at
/// two seconds is a log flood that buries the message explaining what to change.
/// </summary>
public sealed class IngestConfigurationException(string message) : Exception(message);

internal enum VideoMode
{
    /// <summary><c>-c:v copy</c>: the camera's own bits, no decode, no encode.</summary>
    Copy,

    /// <summary>Re-encoded to the codec the stream asked for.</summary>
    Transcode,
}

/// <param name="Codec">The source codec on the copy branch, the target codec on the transcode branch.</param>
/// <param name="Encoder">Null when <paramref name="Mode"/> is <see cref="VideoMode.Copy"/>.</param>
/// <param name="CodecTag">A <c>-tag:v</c> value the copy branch needs, or null.</param>
internal sealed record VideoPlan(
    VideoMode Mode,
    string Codec,
    EncoderPlan? Encoder,
    string? CodecTag);

/// <param name="Include">Whether an audio track is mapped into the recording at all.</param>
/// <param name="Copy">Whether it is copied rather than re-encoded to AAC.</param>
internal sealed record AudioPlan(bool Include, bool Copy, string? SourceCodec);

/// <summary>
/// Decides what ffmpeg should do with a stream, given what the camera is sending and what the host
/// can do about it. Pure — no process, no I/O — so the decision is unit-testable in full and the
/// session is left only assembling a command line from the answer.
///
/// The governing rule is that Serval does not re-encode video the operator did not ask it to. A
/// source it can record goes to disk untouched; a source it cannot record is an error naming the
/// codec. Quietly substituting a transcode instead would have a camera costing a core forever for
/// a reason nobody ever saw.
/// </summary>
internal static class IngestPlanner
{
    public static VideoPlan ResolveVideo(
        CameraStream stream,
        string? probedVideoCodec,
        IngestOptions options,
        FfmpegCapabilities capabilities)
    {
        // 1) An explicit transcode wins over everything: the operator asked for this codec, so the
        //    source codec is irrelevant and was not even probed.
        if (stream.Transcode is { } transcode)
        {
            if (!EncoderSelector.IsSupported(transcode.Codec))
            {
                // Unreachable through the API, which validates on write. Reachable for a document
                // written straight into Mongo.
                throw new IngestConfigurationException(
                    $"Stream '{stream.Name}' asks to transcode to '{transcode.Codec}', which Serval "
                    + $"does not encode. Use one of: "
                    + $"{string.Join(", ", EncoderSelector.SupportedCodecs.Order(StringComparer.Ordinal))}.");
            }

            EncoderPlan plan = EncoderSelector.Select(
                transcode.Codec,
                options.HwAccelDevice,
                options.Encoder,
                transcode.Bitrate ?? options.Bitrate);

            if (!capabilities.CanEncodeVideo(plan.EncoderName))
            {
                throw new IngestConfigurationException(
                    $"Stream '{stream.Name}' asks to transcode to '{transcode.Codec}', which on this "
                    + $"host means the '{plan.EncoderName}' encoder — and this host's ffmpeg does not "
                    + "have it.");
            }

            return new VideoPlan(VideoMode.Transcode, transcode.Codec.Trim().ToLowerInvariant(), plan, null);
        }

        // 2) No transcode asked for, and no idea what the source is. Refusing to guess is the
        //    point: falling back to re-encoding would let a two-second network blip at probe time
        //    pin a 4K camera to a permanent transcode nobody had asked for.
        if (string.IsNullOrWhiteSpace(probedVideoCodec))
        {
            throw new IngestConfigurationException(
                $"Could not determine the source video codec for stream '{stream.Name}' (see the "
                + "ffprobe warning above). Serval will not guess: retrying until the probe answers.");
        }

        string codec = probedVideoCodec.Trim();

        // 3) Recordable as it stands.
        IReadOnlyList<string> recordable = options.ResolvedVideoPassthroughCodecs;
        if (Lists(recordable, codec))
        {
            return new VideoPlan(VideoMode.Copy, codec.ToLowerInvariant(), null, TagFor(codec));
        }

        // 4) Not recordable, and no transcode configured. An error, never a silent re-encode.
        throw new IngestConfigurationException(
            $"Stream '{stream.Name}' is sending '{codec}', which is not in "
            + $"Serval:Ingest:VideoPassthroughCodecs ({string.Join(", ", recordable)}) so it cannot "
            + "be recorded as-is. Either set a transcode on the stream "
            + "(\"transcode\": { \"codec\": \"h264\" }), or add the codec to VideoPassthroughCodecs "
            + "if this container and your players can actually carry it.");
    }

    public static AudioPlan ResolveAudio(bool recordAudio, string? probedAudioCodec, IngestOptions options)
    {
        if (!recordAudio || string.IsNullOrWhiteSpace(probedAudioCodec))
        {
            return new AudioPlan(Include: false, Copy: false, SourceCodec: null);
        }

        string codec = probedAudioCodec.Trim();
        return new AudioPlan(
            Include: true, Copy: Lists(options.ResolvedAudioPassthroughCodecs, codec), codec);
    }

    /// <summary>
    /// ffmpeg's mp4 muxer writes the <c>hev1</c> sample entry for HEVC by default, and Safari and
    /// QuickTime only play <c>hvc1</c> — the one browser family that can decode HEVC at all. Since
    /// HEVC is now recorded untouched by default, leaving the tag alone would ship an archive that
    /// plays in VLC and nowhere a user actually watches it. The other codecs have a single sample
    /// entry each and need nothing.
    /// </summary>
    private static string? TagFor(string sourceCodec) =>
        string.Equals(sourceCodec, "hevc", StringComparison.OrdinalIgnoreCase) ? "hvc1" : null;

    private static bool Lists(IReadOnlyList<string> configured, string codec) =>
        configured.Any(c => string.Equals(c?.Trim(), codec, StringComparison.OrdinalIgnoreCase));
}
