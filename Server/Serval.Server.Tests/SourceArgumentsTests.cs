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

        Assert.Equal(["-rtsp_transport", "tcp", "-timeout", "30000000", "-i", url], args);
    }

    [Fact]
    public void An_rtsp_audio_tap_asks_for_the_audio_stream_only()
    {
        IReadOnlyList<string> args = SourceArguments.InputArgs("rtsp://cam/stream", audioOnly: true);

        Assert.Equal(
            [
                "-rtsp_transport", "tcp", "-allowed_media_types", "audio",
                "-timeout", "30000000", "-i", "rtsp://cam/stream",
            ],
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
        Assert.Equal(["-rw_timeout", "30000000", "-i", url], SourceArguments.InputArgs(url));
        Assert.Equal(
            ["-rw_timeout", "30000000", "-i", url],
            SourceArguments.InputArgs(url, audioOnly: true));
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
            ["-rtsp_transport", "tcp", "-timeout", "15000000", "-i", "rtsp://cam/stream"],
            SourceArguments.ProbeArgs("rtsp://cam/stream"));
    }

    [Fact]
    public void Probing_a_non_rtsp_source_passes_only_the_url()
    {
        Assert.Equal(
            ["-rw_timeout", "15000000", "-i", "rtmp://cam/live"],
            SourceArguments.ProbeArgs("rtmp://cam/live"));
    }

    // --- the socket deadline ------------------------------------------------------------------

    /// <summary>
    /// The regression suite for a leak that cost 1.6 GB per incident: a camera that stops answering
    /// without closing its TCP connection leaves ffmpeg in <c>poll()</c> forever, because an RTSP
    /// session carries no keepalive to discover the peer is gone. The deadline is what ends it, and
    /// it is worthless after <c>-i</c>, where it binds to no input.
    /// </summary>
    [Theory]
    [InlineData("rtsp://cam/stream")]
    [InlineData("http://cam/stream.flv")]
    [InlineData("srt://cam:9000")]
    public void A_network_source_carries_its_deadline_before_the_input(string url)
    {
        List<string> args = [.. SourceArguments.InputArgs(url)];
        int deadline = args.IndexOf("-timeout") >= 0
            ? args.IndexOf("-timeout")
            : args.IndexOf("-rw_timeout");

        Assert.True(deadline >= 0);
        Assert.True(deadline < args.IndexOf("-i"));
    }

    /// <summary>
    /// Which option carries the deadline is protocol-private in the same way
    /// <c>-rtsp_transport</c> is, and getting it wrong is worse than inert: on the RTMP protocol
    /// <c>-timeout</c> means listen-seconds and implies <c>-rtmp_listen 1</c>, which would turn a
    /// camera pull into a server waiting for an inbound connection.
    /// </summary>
    [Theory]
    [InlineData("http://cam/stream.flv")]
    [InlineData("rtmp://cam/live/stream")]
    [InlineData("srt://cam:9000")]
    public void Only_rtsp_gets_the_demuxers_own_timeout_option(string url)
    {
        Assert.DoesNotContain("-timeout", SourceArguments.InputArgs(url));
        Assert.DoesNotContain("-timeout", SourceArguments.ProbeArgs(url));
        Assert.Contains("-rw_timeout", SourceArguments.InputArgs(url));
    }

    /// <summary>
    /// <c>-rw_timeout</c> is silently ignored by the RTSP demuxer, so the two are an either/or
    /// rather than a belt-and-braces pair.
    /// </summary>
    [Fact]
    public void An_rtsp_source_never_gets_the_generic_avio_option()
    {
        Assert.DoesNotContain("-rw_timeout", SourceArguments.InputArgs("rtsp://cam/stream"));
        Assert.DoesNotContain("-rw_timeout", SourceArguments.ProbeArgs("rtsp://cam/stream"));
    }

    /// <summary>A local read cannot half-open, so there is no deadline worth setting on one.</summary>
    [Theory]
    [InlineData("/videos/sample.mp4")]
    [InlineData("file:///videos/sample.mp4")]
    public void A_file_source_gets_no_deadline(string url)
    {
        Assert.DoesNotContain("-timeout", SourceArguments.InputArgs(url));
        Assert.DoesNotContain("-rw_timeout", SourceArguments.InputArgs(url));
        Assert.DoesNotContain("-rw_timeout", SourceArguments.ProbeArgs(url));
    }

    /// <summary>
    /// ffmpeg takes microseconds. The conversion is culture-invariant because the one host that
    /// would break — a comma-decimal locale — would produce a command line that fails on every
    /// camera at once, and only there.
    /// </summary>
    [Fact]
    public void The_deadline_is_written_in_invariant_microseconds()
    {
        List<string> args = [.. SourceArguments.ProbeArgs("rtsp://cam/stream")];
        string value = args[args.IndexOf("-timeout") + 1];

        Assert.Equal(
            ((long)SourceArguments.ProbeTimeout.TotalMicroseconds).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            value);
        Assert.DoesNotContain(',', value);
        Assert.DoesNotContain('.', value);
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
