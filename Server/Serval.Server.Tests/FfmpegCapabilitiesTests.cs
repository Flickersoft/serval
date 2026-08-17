using Serval.Server.Ingest;

namespace Serval.Server.Tests;

/// <summary>
/// The encoder table parse. It is the input to every transcode validation, so a parse that
/// silently matched nothing would reject every transcode request on a perfectly capable host —
/// a failure that points squarely at the wrong problem. Exercised against a verbatim capture of
/// real <c>ffmpeg -encoders</c> output rather than a hand-written approximation.
/// </summary>
public class FfmpegCapabilitiesTests
{
    private const string RealOutput = """
        Encoders:
         V..... = Video
         A..... = Audio
         S..... = Subtitle
         .F.... = Frame-level multithreading
         ..S... = Slice-level multithreading
         ...X.. = Codec is experimental
         ....B. = Supports draw_horiz_band
         .....D = Supports direct rendering method 1
         ------
         V....D a64multi             Multicolor charset for Commodore 64
         V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10
         V....D libx264rgb           libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 RGB
         V....D h264_vaapi           H.264/AVC (VAAPI)
         V....D h264_nvenc           NVIDIA NVENC H.264 encoder
         V....D libvpx-vp9           libvpx VP9
         V....D libsvtav1            SVT-AV1(Scalable Video Technology for AV1) encoder
         A....D aac                  AAC (Advanced Audio Coding)
         A....D libopus              libopus Opus
         S..... ass                  ASS (Advanced SubStation Alpha) subtitle
         S..... srt                  SubRip subtitle

        """;

    [Fact]
    public void Video_encoders_are_picked_out_of_the_table()
    {
        IReadOnlyList<string> encoders = FfmpegCapabilities.ParseVideoEncoders(RealOutput);

        Assert.Contains("libx264", encoders);
        Assert.Contains("h264_vaapi", encoders);
        Assert.Contains("h264_nvenc", encoders);
        Assert.Contains("libvpx-vp9", encoders);
        Assert.Contains("libsvtav1", encoders);
    }

    [Fact]
    public void Audio_and_subtitle_encoders_are_not_video_encoders()
    {
        // 'aac' as a video encoder would make a nonsense transcode request validate.
        IReadOnlyList<string> encoders = FfmpegCapabilities.ParseVideoEncoders(RealOutput);

        Assert.DoesNotContain("aac", encoders);
        Assert.DoesNotContain("libopus", encoders);
        Assert.DoesNotContain("ass", encoders);
        Assert.DoesNotContain("srt", encoders);
    }

    [Fact]
    public void The_flag_legend_preamble_is_not_mistaken_for_encoders()
    {
        // "V..... = Video" appears above the dashes and has the shape of a table row. Requiring
        // the dashes line before reading anything is what keeps it out.
        IReadOnlyList<string> encoders = FfmpegCapabilities.ParseVideoEncoders(RealOutput);

        Assert.DoesNotContain("=", encoders);
        Assert.DoesNotContain("Video", encoders);
    }

    [Fact]
    public void Output_with_no_table_yields_nothing()
    {
        // Probe turns this into a hard startup failure rather than "this host has no encoders".
        Assert.Empty(FfmpegCapabilities.ParseVideoEncoders("command not found"));
        Assert.Empty(FfmpegCapabilities.ParseVideoEncoders(""));
    }

    [Fact]
    public void Encoder_lookup_is_case_insensitive_and_null_safe()
    {
        var capabilities = new FfmpegCapabilities(["libx264", "h264_vaapi"]);

        Assert.True(capabilities.CanEncodeVideo("libx264"));
        Assert.True(capabilities.CanEncodeVideo("LibX264"));
        Assert.True(capabilities.CanEncodeVideo(" h264_vaapi "));
        Assert.False(capabilities.CanEncodeVideo("av1_vaapi"));
        Assert.False(capabilities.CanEncodeVideo(null));
        Assert.False(capabilities.CanEncodeVideo("  "));
    }

    [Fact]
    public void Probing_a_binary_that_does_not_exist_fails_with_an_actionable_message()
    {
        var ex = Assert.Throws<FfmpegUnavailableException>(() =>
            FfmpegCapabilities.Probe("/nonexistent/ffmpeg", TimeSpan.FromSeconds(5)));

        Assert.Contains("/nonexistent/ffmpeg", ex.Message);
        Assert.Contains("Serval:Ingest:FfmpegPath", ex.Message);
    }
}
