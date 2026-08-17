using Serval.Server.Cameras;

namespace Serval.Server.Tests;

/// <summary>
/// What a signed-in account below Admin is allowed to read back about a camera. Two secrets travel
/// on a <see cref="Camera"/> and the second is the one that gets missed: the ONVIF password has a
/// field of its own, and the stream URLs carry another inside their userinfo.
/// </summary>
public class CameraSecretRedactionTests
{
    private static Camera Sample() => new()
    {
        Id = "front-door",
        Name = "Front Door",
        Streams =
        [
            new CameraStream
            {
                Name = "main",
                Url = "rtsp://admin:hunter2@192.0.2.10:554/Streaming/Channels/101",
                Roles = [StreamRole.Record, StreamRole.Live],
            },
            new CameraStream
            {
                Name = "sub",
                Url = "rtsp://admin:hunter2@192.0.2.10:554/Streaming/Channels/102",
                Roles = [StreamRole.Detect],
            },
        ],
        OnvifUrl = "http://192.0.2.10/onvif/device_service",
        OnvifUsername = "admin",
        OnvifPassword = "hunter2",
    };

    [Fact]
    public void The_onvif_password_is_gone()
    {
        Assert.Null(Sample().WithoutSecrets().OnvifPassword);
    }

    [Fact]
    public void Every_stream_url_loses_its_userinfo()
    {
        Camera redacted = Sample().WithoutSecrets();

        Assert.All(redacted.Streams, stream =>
        {
            Assert.DoesNotContain("hunter2", stream.Url, StringComparison.Ordinal);
            Assert.DoesNotContain("@", stream.Url, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Redaction removes the credential, not the address — a Viewer still needs to see which camera
    /// is which, and an operator reading a support log still needs to know where it points.
    /// </summary>
    [Fact]
    public void What_is_left_still_says_where_the_stream_is()
    {
        Camera redacted = Sample().WithoutSecrets();

        Assert.Equal("rtsp://192.0.2.10:554/Streaming/Channels/101", redacted.Streams[0].Url);
        Assert.Equal("front-door", redacted.Id);
        Assert.Equal("admin", redacted.OnvifUsername);
        Assert.Equal("http://192.0.2.10/onvif/device_service", redacted.OnvifUrl);
    }

    /// <summary>
    /// The instance handed to this comes straight out of the registry. A redaction that edited it
    /// in place would blank the real credential on its way to the next writer.
    /// </summary>
    [Fact]
    public void The_original_is_untouched()
    {
        Camera original = Sample();
        original.WithoutSecrets();

        Assert.Equal("hunter2", original.OnvifPassword);
        Assert.Contains("hunter2", original.Streams[0].Url, StringComparison.Ordinal);
    }

    [Theory]
    // The ordinary case, and the same URL once it has already been redacted.
    [InlineData("rtsp://user:pass@host:554/path", "rtsp://host:554/path")]
    [InlineData("rtsp://host:554/path", "rtsp://host:554/path")]
    // A password containing an unescaped '@' — against the RFC, common in the wild. Splitting on
    // the last '@' in the authority is what keeps the host intact here.
    [InlineData("rtsp://user:p@ss@host/path", "rtsp://host/path")]
    // Username with no password at all.
    [InlineData("rtsp://user@host/path", "rtsp://host/path")]
    // No path after the authority, so there is no '/' to stop at.
    [InlineData("rtsp://user:pass@host", "rtsp://host")]
    // A query or fragment ends the authority just as a '/' does.
    [InlineData("http://user:pass@host?a=b", "http://host?a=b")]
    // A local file camera: no scheme, no authority, nothing to strip. A '@' in a filename stays.
    [InlineData("/media/samples/loop@2x.mp4", "/media/samples/loop@2x.mp4")]
    [InlineData("", "")]
    public void Userinfo_is_stripped_without_disturbing_the_rest(string url, string expected) =>
        Assert.Equal(expected, CameraStream.StripUserInfo(url));
}
