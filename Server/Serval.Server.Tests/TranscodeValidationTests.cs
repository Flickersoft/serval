using Serval.Server.Cameras;
using Serval.Server.Configuration;
using Serval.Server.Ingest;

namespace Serval.Server.Tests;

/// <summary>
/// Checking a transcode request against the encoders this host's ffmpeg actually has.
///
/// Without it, asking for a codec the hardware cannot encode produced an ffmpeg that failed on
/// every attempt and was retried forever, with the reason visible only to someone reading logs.
/// The whole value is that the rejection lands on the request, while the mistake is still attached
/// to the person who made it — so these assert on the message as much as on the throw.
/// </summary>
public class TranscodeValidationTests
{
    private static Camera With(StreamTranscode? transcode) => new()
    {
        Id = "front-door",
        Name = "Front Door",
        Streams =
        [
            new CameraStream
            {
                Name = "main",
                Url = "rtsp://cam/main",
                Roles = [StreamRole.Record, StreamRole.Detect, StreamRole.Live],
                Transcode = transcode,
            },
        ],
    };

    private static IngestOptions Options(string? device = null, string? encoder = null) =>
        new() { HwAccelDevice = device, Encoder = encoder };

    [Fact]
    public void A_camera_with_no_transcode_needs_no_encoder()
    {
        // The default path: a host with no encoders at all still records every camera.
        CameraRepository.ValidateTranscodes(With(null), Options(), new FfmpegCapabilities([]));
    }

    [Fact]
    public void A_transcode_the_host_can_encode_passes()
    {
        CameraRepository.ValidateTranscodes(
            With(new StreamTranscode { Codec = "h264" }),
            Options(),
            new FfmpegCapabilities(["libx264"]));
    }

    [Fact]
    public void A_software_encoder_the_host_lacks_is_rejected_by_name()
    {
        var ex = Assert.Throws<CameraValidationException>(() =>
            CameraRepository.ValidateTranscodes(
                With(new StreamTranscode { Codec = "av1" }),
                Options(),
                new FfmpegCapabilities(["libx264"])));

        Assert.Contains("libsvtav1", ex.Message);
    }

    [Fact]
    public void A_vaapi_encoder_the_gpu_lacks_is_rejected_by_name()
    {
        // The real case this was written for: AV1 encode needs RDNA3, so asking for it on an older
        // AMD part fails inside ffmpeg, per camera, at runtime unless it is caught here.
        var ex = Assert.Throws<CameraValidationException>(() =>
            CameraRepository.ValidateTranscodes(
                With(new StreamTranscode { Codec = "av1" }),
                Options(device: "/dev/dri/renderD128"),
                new FfmpegCapabilities(["libx264", "h264_vaapi", "libsvtav1"])));

        Assert.Contains("av1_vaapi", ex.Message);
        Assert.Contains("/dev/dri/renderD128", ex.Message);
    }

    [Fact]
    public void The_message_names_the_encoder_precedence_actually_chose()
    {
        // HwAccelDevice silently beats Encoder. This host *can* run h264_nvenc, and the request
        // still fails — so the message has to say h264_vaapi, or the operator reads "this host
        // does not have h264_nvenc", looks, finds it, and has nowhere to go.
        var ex = Assert.Throws<CameraValidationException>(() =>
            CameraRepository.ValidateTranscodes(
                With(new StreamTranscode { Codec = "h264" }),
                Options(device: "/dev/dri/renderD128", encoder: "h264_nvenc"),
                new FfmpegCapabilities(["libx264", "h264_nvenc"])));

        Assert.Contains("h264_vaapi", ex.Message);
        Assert.Contains("precedence", ex.Message);
    }

    [Fact]
    public void An_encoder_override_the_host_lacks_is_rejected()
    {
        var ex = Assert.Throws<CameraValidationException>(() =>
            CameraRepository.ValidateTranscodes(
                With(new StreamTranscode { Codec = "av1" }),
                Options(encoder: "av1_nvenc"),
                new FfmpegCapabilities(["libx264"])));

        Assert.Contains("av1_nvenc", ex.Message);
    }

    [Fact]
    public void A_codec_serval_does_not_encode_is_rejected_and_says_it_can_still_record_it()
    {
        // hevc is recordable but not an encode target, and "unsupported codec" alone would read as
        // "Serval cannot handle HEVC", which is the opposite of true.
        var ex = Assert.Throws<CameraValidationException>(() =>
            CameraRepository.ValidateTranscodes(
                With(new StreamTranscode { Codec = "hevc" }),
                Options(),
                new FfmpegCapabilities(["libx264"])));

        Assert.Contains("record", ex.Message);
        Assert.Contains("VideoPassthroughCodecs", ex.Message);
    }

    [Theory]
    [InlineData("2M")]
    [InlineData("2000k")]
    [InlineData("1.5M")]
    [InlineData("4000000")]
    public void Valid_bitrates_are_accepted(string bitrate) =>
        CameraRepository.ValidateTranscodes(
            With(new StreamTranscode { Codec = "h264", Bitrate = bitrate }),
            Options(),
            new FfmpegCapabilities(["libx264"]));

    [Theory]
    [InlineData("2mb")]
    [InlineData("2 M")]
    [InlineData("fast")]
    [InlineData("")]
    public void Invalid_bitrates_are_rejected(string bitrate) =>
        Assert.Throws<CameraValidationException>(() =>
            CameraRepository.ValidateTranscodes(
                With(new StreamTranscode { Codec = "h264", Bitrate = bitrate }),
                Options(),
                new FfmpegCapabilities(["libx264"])));
}
