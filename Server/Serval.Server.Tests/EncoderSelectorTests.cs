using Serval.Server.Ingest;

namespace Serval.Server.Tests;

/// <summary>
/// Encoder selection is what turns "transcode this stream to av1" into a specific encoder on a
/// specific deployer's hardware, so its branches are pinned: software default per codec, VAAPI
/// when a render device is set, an explicit override otherwise, and device-beats-override
/// precedence.
/// </summary>
public class EncoderSelectorTests
{
    [Theory]
    [InlineData("h264", "libx264")]
    [InlineData("vp9", "libvpx-vp9")]
    [InlineData("av1", "libsvtav1")]
    public void Software_default_picks_the_codecs_cpu_encoder(string codec, string expected)
    {
        EncoderPlan plan = EncoderSelector.Select(codec, hwAccelDevice: null, encoderOverride: null, bitrate: "2M");

        Assert.Empty(plan.InputArgs);
        Assert.Empty(plan.FilterArgs);
        Assert.Equal("-c:v", plan.VideoArgs[0]);
        Assert.Equal(expected, plan.VideoArgs[1]);
        Assert.Contains("2M", plan.VideoArgs);
    }

    [Theory]
    [InlineData("h264", "h264_vaapi")]
    [InlineData("vp9", "vp9_vaapi")]
    [InlineData("av1", "av1_vaapi")]
    public void A_render_device_selects_the_vaapi_encoder_with_upload(string codec, string expected)
    {
        EncoderPlan plan = EncoderSelector.Select(codec, "/dev/dri/renderD128", encoderOverride: null, bitrate: "3M");

        Assert.Equal(new[] { "-vaapi_device", "/dev/dri/renderD128" }, plan.InputArgs);
        Assert.Equal(new[] { "-vf", "format=nv12,hwupload" }, plan.FilterArgs);
        Assert.Contains(expected, plan.VideoArgs);
        Assert.Contains("3M", plan.VideoArgs);
    }

    [Fact]
    public void An_explicit_override_is_used_verbatim()
    {
        EncoderPlan plan = EncoderSelector.Select("av1", hwAccelDevice: null, encoderOverride: "av1_nvenc", bitrate: "4M");

        Assert.Empty(plan.InputArgs);
        Assert.Empty(plan.FilterArgs);
        Assert.Equal(new[] { "-c:v", "av1_nvenc", "-b:v", "4M" }, plan.VideoArgs);
    }

    [Fact]
    public void A_render_device_beats_an_override()
    {
        // Both set: VAAPI wins, since it's the more specific "use this GPU" instruction.
        EncoderPlan plan = EncoderSelector.Select("h264", "/dev/dri/renderD128", "h264_nvenc", "2M");

        Assert.Contains("h264_vaapi", plan.VideoArgs);
        Assert.DoesNotContain("h264_nvenc", plan.VideoArgs);
    }

    [Theory]
    [InlineData("hevc")]
    [InlineData("mjpeg")]
    [InlineData("")]
    public void An_unsupported_codec_is_rejected(string codec) =>
        Assert.Throws<ArgumentException>(() =>
            EncoderSelector.Select(codec, null, null, "2M"));

    [Fact]
    public void Codec_matching_is_case_insensitive()
    {
        EncoderPlan plan = EncoderSelector.Select("VP9", null, null, "2M");
        Assert.Contains("libvpx-vp9", plan.VideoArgs);
    }

    [Theory]
    [InlineData("h264", null, null)]
    [InlineData("vp9", null, null)]
    [InlineData("av1", null, null)]
    [InlineData("h264", "/dev/dri/renderD128", null)]
    [InlineData("av1", "/dev/dri/renderD128", null)]
    [InlineData("av1", null, "av1_nvenc")]
    [InlineData("h264", "/dev/dri/renderD128", "h264_nvenc")]
    public void EncoderName_matches_the_c_v_argument(string codec, string? device, string? over)
    {
        // The capability check and every "this host cannot encode X" message are built on
        // EncoderName. If it ever disagreed with the argument list, the validator would approve
        // one encoder and ffmpeg would run another — which is precisely the class of silent
        // mismatch the check exists to eliminate.
        EncoderPlan plan = EncoderSelector.Select(codec, device, over, "2M");

        int index = plan.VideoArgs.ToList().IndexOf("-c:v");
        Assert.Equal(plan.VideoArgs[index + 1], plan.EncoderName);
    }

    [Theory]
    [InlineData("h264")]
    [InlineData("vp9")]
    [InlineData("av1")]
    [InlineData("AV1")]
    [InlineData(" h264 ")]
    [InlineData("hevc")]
    [InlineData("mjpeg")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void IsSupported_agrees_with_Select(string? codec)
    {
        // A validator that disagrees with the runtime is worse than none: it either rejects a
        // configuration that would work, or approves one that throws inside a retry loop.
        bool supported = EncoderSelector.IsSupported(codec);
        bool selectable;
        try
        {
            EncoderSelector.Select(codec!, null, null, "2M");
            selectable = true;
        }
        catch (ArgumentException)
        {
            selectable = false;
        }

        Assert.Equal(supported, selectable);
    }
}
