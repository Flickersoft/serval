namespace Serval.Server.Ingest;

/// <summary>
/// The ffmpeg arguments to encode one video, split by where they go on the command line:
/// <see cref="InputArgs"/> before <c>-i</c> (hardware device setup), <see cref="FilterArgs"/>
/// as a <c>-vf</c> chain, and <see cref="VideoArgs"/> the <c>-c:v</c> and its options.
/// </summary>
/// <param name="EncoderName">
/// The <c>-c:v</c> value this plan will run, lifted out of <see cref="VideoArgs"/> so callers can
/// name it without parsing an argument list. It is what makes a codec request checkable *before*
/// ffmpeg is started: the capability check and the error message both need to talk about the
/// encoder the selection rules actually chose, not the codec the user asked for.
/// </param>
public sealed record EncoderPlan(
    string EncoderName,
    IReadOnlyList<string> InputArgs,
    IReadOnlyList<string> FilterArgs,
    IReadOnlyList<string> VideoArgs);

/// <summary>
/// Chooses the ffmpeg encoder for the configured codec and hardware. Pure — no process, no
/// probing — so it's unit-testable and the ingest session just splices the result into its
/// command line. Because deployers' hardware varies, the selection is deliberately explicit:
///
/// 1. <b>HwAccelDevice set</b> → VAAPI, which covers Intel (incl. QSV silicon) and AMD on Linux
///    and is the realistic hardware path for VP9/AV1.
/// 2. <b>Encoder override set</b> → used verbatim (e.g. an NVENC or QSV-specific encoder).
/// 3. <b>Neither</b> → the software encoder for the codec.
/// </summary>
public static class EncoderSelector
{
    /// <summary>
    /// The codecs Serval can encode <em>to</em>. Deliberately narrower than what it can record:
    /// a source arriving as HEVC is stream-copied happily, but asking to transcode <em>into</em>
    /// HEVC is not offered — it would be a patent-encumbered target chosen on a deployer's behalf,
    /// and the codecs here cover every reason to re-encode at all.
    /// </summary>
    public static IReadOnlySet<string> SupportedCodecs { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h264", "vp9", "av1" };

    /// <summary>
    /// Whether <see cref="Select"/> will accept this codec. Kept in step with it by
    /// <c>EncoderSelectorTests.IsSupported_agrees_with_Select</c> — a validator that disagrees
    /// with the runtime is worse than no validator, because it rejects working configurations.
    /// </summary>
    public static bool IsSupported(string? codec) =>
        !string.IsNullOrWhiteSpace(codec) && SupportedCodecs.Contains(codec.Trim());

    public static EncoderPlan Select(string codec, string? hwAccelDevice, string? encoderOverride, string bitrate)
    {
        string c = (codec ?? "").Trim().ToLowerInvariant();
        if (!IsSupported(c))
        {
            throw new ArgumentException(
                $"Unsupported transcode codec '{codec}'. Use one of: "
                + $"{string.Join(", ", SupportedCodecs.Order(StringComparer.Ordinal))}.",
                nameof(codec));
        }

        // 1) VAAPI hardware encode. Decode stays on the CPU; frames are uploaded to the GPU for
        //    encoding, which works uniformly across camera source codecs.
        if (!string.IsNullOrWhiteSpace(hwAccelDevice))
        {
            string vaapiEncoder = c switch
            {
                "h264" => "h264_vaapi",
                "vp9" => "vp9_vaapi",
                _ => "av1_vaapi",
            };
            return new EncoderPlan(
                EncoderName: vaapiEncoder,
                InputArgs: ["-vaapi_device", hwAccelDevice],
                FilterArgs: ["-vf", "format=nv12,hwupload"],
                VideoArgs: ["-c:v", vaapiEncoder, "-b:v", bitrate]);
        }

        // 2) Explicit encoder override for hardware VAAPI doesn't reach (NVENC, QSV, ...).
        if (!string.IsNullOrWhiteSpace(encoderOverride))
        {
            string encoder = encoderOverride.Trim();
            return new EncoderPlan(encoder, [], [], ["-c:v", encoder, "-b:v", bitrate]);
        }

        // 3) Software fallback, tuned for realtime so it can keep up with a live camera.
        string software = c switch
        {
            "h264" => "libx264",
            "vp9" => "libvpx-vp9",
            _ => "libsvtav1", // av1
        };
        List<string> video = c switch
        {
            "h264" => ["-c:v", software, "-preset", "veryfast", "-tune", "zerolatency", "-b:v", bitrate],
            "vp9" => ["-c:v", software, "-deadline", "realtime", "-cpu-used", "8", "-row-mt", "1", "-b:v", bitrate],
            _ => ["-c:v", software, "-preset", "10", "-b:v", bitrate], // av1
        };
        return new EncoderPlan(software, [], [], video);
    }
}
