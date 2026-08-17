using Serval.Server.Ingest;

namespace Serval.Server.Tests;

/// <summary>
/// The scheme dispatch for ffmpeg's input options.
///
/// The regression suite for a real outage: emitting <c>-rtsp_transport tcp</c> for anything that is
/// not a local file makes a camera registered with an HTTP-FLV URL produce <c>Option rtsp_transport
/// not found</c> on every attempt, retried forever, with the reason visible only in the ffmpeg log.
/// </summary>
public class SourceArgumentsTests
{
    [Theory]
    [InlineData("rtsp://cam/stream")]
    [InlineData("rtsps://cam/stream")]
    [InlineData("RTSP://cam/stream")] // scheme comparison is case-insensitive
    public void An_rtsp_source_gets_tcp_transport(string url)
    {
        IReadOnlyList<string> args = SourceArguments.InputArgs(url);

        Assert.Equal(["-rtsp_transport", "tcp", "-i", url], args);
    }

    [Fact]
    public void An_rtsp_audio_tap_asks_for_the_audio_stream_only()
    {
        IReadOnlyList<string> args = SourceArguments.InputArgs("rtsp://cam/stream", audioOnly: true);

        Assert.Equal(
            ["-rtsp_transport", "tcp", "-allowed_media_types", "audio", "-i", "rtsp://cam/stream"],
            args);
    }

    /// <summary>
    /// The case that broke. Both options are RTSP-demuxer-private, so neither may appear here —
    /// ffmpeg exits immediately rather than ignoring them.
    /// </summary>
    [Theory]
    [InlineData("http://cam/flv?port=1935&app=bcs&stream=channel0_ext.bcs")]
    [InlineData("https://cam/stream.flv")]
    [InlineData("rtmp://cam/live/stream")]
    [InlineData("srt://cam:9000")]
    public void A_non_rtsp_network_source_gets_no_rtsp_options(string url)
    {
        Assert.Equal(["-i", url], SourceArguments.InputArgs(url));
        Assert.Equal(["-i", url], SourceArguments.InputArgs(url, audioOnly: true));
    }

    [Theory]
    [InlineData("/videos/sample.mp4")]
    [InlineData("file:///videos/sample.mp4")]
    [InlineData("relative/clip.mp4")]
    public void A_file_source_is_looped_in_realtime(string url)
    {
        Assert.Equal(["-stream_loop", "-1", "-re", "-i", url], SourceArguments.InputArgs(url));
    }

    /// <summary>
    /// ffprobe shares ffmpeg's demuxer options but not <c>-stream_loop</c>/<c>-re</c>, which are
    /// output-pacing ones — passing them makes it exit on an unrecognised option, which would
    /// silently downgrade a copyable source into a transcode.
    /// </summary>
    [Fact]
    public void Probing_a_file_omits_the_ffmpeg_only_options()
    {
        Assert.Equal(["-i", "/videos/sample.mp4"], SourceArguments.ProbeArgs("/videos/sample.mp4"));
    }

    [Fact]
    public void Probing_an_rtsp_source_keeps_tcp_transport()
    {
        Assert.Equal(
            ["-rtsp_transport", "tcp", "-i", "rtsp://cam/stream"],
            SourceArguments.ProbeArgs("rtsp://cam/stream"));
    }

    [Fact]
    public void Probing_a_non_rtsp_source_passes_only_the_url()
    {
        Assert.Equal(["-i", "rtmp://cam/live"], SourceArguments.ProbeArgs("rtmp://cam/live"));
    }

    [Theory]
    [InlineData("rtsp://cam/stream", true)]
    [InlineData("rtsps://cam/stream", true)]
    [InlineData("http://cam/stream.flv", false)]
    [InlineData("/videos/sample.mp4", false)]
    public void IsRtsp_identifies_the_schemes_with_rtsp_only_behaviour(string url, bool expected) =>
        Assert.Equal(expected, SourceArguments.IsRtsp(url));

    [Theory]
    [InlineData("/videos/sample.mp4", true)]
    [InlineData("file:///videos/sample.mp4", true)]
    [InlineData("C:/videos/sample.mp4", true)] // a drive letter is not a scheme
    [InlineData("rtsp://cam/stream", false)]
    [InlineData("http://cam/stream.flv", false)]
    public void IsFile_identifies_local_sources(string url, bool expected) =>
        Assert.Equal(expected, SourceArguments.IsFile(url));

    [Theory]
    [InlineData("rtsp://cam/stream", true)]
    [InlineData("http://cam/stream.flv", true)]
    [InlineData("srt://cam:9000", true)]
    [InlineData("/videos/sample.mp4", true)]
    [InlineData("ftp://cam/stream", false)]
    [InlineData("smb://nas/share/clip.mp4", false)]
    public void IsSupported_gates_what_validation_accepts(string url, bool expected) =>
        Assert.Equal(expected, SourceArguments.IsSupported(url));
}
