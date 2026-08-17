using Serval.Server.Cameras;
using Serval.Server.Configuration;
using Serval.Server.Ingest;

namespace Serval.Server.Tests;

/// <summary>
/// The decision that governs what happens to every recorded frame: copy, transcode, or refuse.
///
/// The rule it enforces is that Serval does not re-encode video nobody asked it to. The "refuse"
/// cases matter as much as the "copy" ones: each is a place where quietly normalising instead would
/// cost a camera a core forever for a reason that appeared nowhere.
/// </summary>
public class IngestPlannerTests
{
    private static IngestOptions Options() => new();

    private static FfmpegCapabilities EveryEncoder() => new(
        ["libx264", "libvpx-vp9", "libsvtav1", "h264_vaapi", "vp9_vaapi", "av1_vaapi"]);

    private static CameraStream Stream(StreamTranscode? transcode = null) =>
        new() { Name = "main", Url = "rtsp://cam/main", Roles = [StreamRole.Record], Transcode = transcode };

    // --- copy ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("h264")]
    [InlineData("hevc")]
    [InlineData("av1")]
    [InlineData("vp9")]
    public void A_listed_source_codec_is_recorded_untouched(string codec)
    {
        VideoPlan plan = IngestPlanner.ResolveVideo(Stream(), codec, Options(), EveryEncoder());

        Assert.Equal(VideoMode.Copy, plan.Mode);
        Assert.Null(plan.Encoder);
        Assert.Equal(codec, plan.Codec);
    }

    [Theory]
    [InlineData("H264")]
    [InlineData(" h264 ")]
    [InlineData("HEVC")]
    public void Source_codec_matching_is_case_and_whitespace_tolerant(string codec)
    {
        // ffprobe's exact output is not something to be precious about, and a near-miss here would
        // silently reject a perfectly recordable camera.
        VideoPlan plan = IngestPlanner.ResolveVideo(Stream(), codec, Options(), EveryEncoder());

        Assert.Equal(VideoMode.Copy, plan.Mode);
    }

    [Fact]
    public void Hevc_is_tagged_hvc1_and_h264_is_not()
    {
        // ffmpeg's mp4 muxer writes hev1 by default, and Safari — the one browser family that can
        // decode HEVC at all — only plays hvc1. Without the tag, recording HEVC by default ships
        // an archive that plays in VLC and nowhere a user actually watches it.
        Assert.Equal("hvc1",
            IngestPlanner.ResolveVideo(Stream(), "hevc", Options(), EveryEncoder()).CodecTag);
        Assert.Null(
            IngestPlanner.ResolveVideo(Stream(), "h264", Options(), EveryEncoder()).CodecTag);
        Assert.Null(
            IngestPlanner.ResolveVideo(Stream(), "av1", Options(), EveryEncoder()).CodecTag);
    }

    // --- refuse -------------------------------------------------------------------------------

    [Theory]
    [InlineData("mjpeg")]
    [InlineData("mpeg4")]
    [InlineData("h263")]
    public void An_unlisted_source_codec_is_an_error_not_a_transcode(string codec)
    {
        var ex = Assert.Throws<IngestConfigurationException>(() =>
            IngestPlanner.ResolveVideo(Stream(), codec, Options(), EveryEncoder()));

        Assert.Contains(codec, ex.Message);
        Assert.Contains("transcode", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unknown_source_codec_is_an_error_not_a_transcode(string? codec)
    {
        // Falling back to a transcode here would let a two-second blip at probe time pin a 4K
        // camera to a permanent re-encode nobody asked for and nobody could see.
        Assert.Throws<IngestConfigurationException>(() =>
            IngestPlanner.ResolveVideo(Stream(), codec, Options(), EveryEncoder()));
    }

    [Fact]
    public void An_empty_passthrough_list_refuses_everything_rather_than_transcoding_it()
    {
        IngestOptions options = Options();
        options.VideoPassthroughCodecs = [];

        Assert.Throws<IngestConfigurationException>(() =>
            IngestPlanner.ResolveVideo(Stream(), "h264", options, EveryEncoder()));
    }

    // --- transcode ----------------------------------------------------------------------------

    [Fact]
    public void A_declared_transcode_wins_over_a_recordable_source()
    {
        VideoPlan plan = IngestPlanner.ResolveVideo(
            Stream(new StreamTranscode { Codec = "h264" }), "h264", Options(), EveryEncoder());

        Assert.Equal(VideoMode.Transcode, plan.Mode);
        Assert.Equal("libx264", plan.Encoder!.EncoderName);
    }

    [Fact]
    public void A_transcode_bitrate_falls_back_to_the_server_default()
    {
        IngestOptions options = Options();
        options.Bitrate = "6M";

        VideoPlan plan = IngestPlanner.ResolveVideo(
            Stream(new StreamTranscode { Codec = "h264" }), null, options, EveryEncoder());

        Assert.Contains("6M", plan.Encoder!.VideoArgs);
    }

    [Fact]
    public void A_stream_bitrate_overrides_the_server_default()
    {
        IngestOptions options = Options();
        options.Bitrate = "2M";

        VideoPlan plan = IngestPlanner.ResolveVideo(
            Stream(new StreamTranscode { Codec = "h264", Bitrate = "8M" }), null, options,
            EveryEncoder());

        Assert.Contains("8M", plan.Encoder!.VideoArgs);
        Assert.DoesNotContain("2M", plan.Encoder.VideoArgs);
    }

    [Fact]
    public void A_transcode_the_host_cannot_encode_names_the_missing_encoder()
    {
        IngestOptions options = Options();
        options.HwAccelDevice = "/dev/dri/renderD128";

        var ex = Assert.Throws<IngestConfigurationException>(() =>
            IngestPlanner.ResolveVideo(
                Stream(new StreamTranscode { Codec = "av1" }),
                null,
                options,
                new FfmpegCapabilities(["libx264", "h264_vaapi"])));

        Assert.Contains("av1_vaapi", ex.Message);
    }

    [Fact]
    public void A_transcode_to_a_codec_serval_does_not_encode_is_rejected()
    {
        // Unreachable through the API, which validates on write — reachable for a document written
        // straight into Mongo.
        var ex = Assert.Throws<IngestConfigurationException>(() =>
            IngestPlanner.ResolveVideo(
                Stream(new StreamTranscode { Codec = "hevc" }), null, Options(), EveryEncoder()));

        Assert.Contains("hevc", ex.Message);
    }

    // --- audio --------------------------------------------------------------------------------

    [Theory]
    [InlineData("aac")]
    [InlineData("opus")]
    [InlineData("mp3")]
    [InlineData("AAC")]
    public void Audio_fMP4_can_carry_is_copied(string codec)
    {
        AudioPlan plan = IngestPlanner.ResolveAudio(recordAudio: true, codec, Options());

        Assert.True(plan.Include);
        Assert.True(plan.Copy);
    }

    [Theory]
    [InlineData("pcm_mulaw")]
    [InlineData("pcm_alaw")]
    public void Audio_fMP4_cannot_carry_is_transcoded(string codec)
    {
        // Deliberately unlike video: G.711 has no fMP4 sample entry at all, so copying it produces
        // a file nothing can open. One legal target, ~64 kbps — a container constraint rather than
        // a codec choice made on the operator's behalf.
        AudioPlan plan = IngestPlanner.ResolveAudio(recordAudio: true, codec, Options());

        Assert.True(plan.Include);
        Assert.False(plan.Copy);
        Assert.Equal(codec, plan.SourceCodec);
    }

    [Theory]
    [InlineData(false, "aac")]
    [InlineData(true, null)]
    [InlineData(true, "")]
    public void Audio_is_omitted_when_not_wanted_or_not_present(bool recordAudio, string? codec)
    {
        AudioPlan plan = IngestPlanner.ResolveAudio(recordAudio, codec, Options());

        Assert.False(plan.Include);
    }
}
