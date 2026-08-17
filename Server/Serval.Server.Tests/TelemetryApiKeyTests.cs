using Microsoft.AspNetCore.Http;
using Serval.Server.Telemetry;

namespace Serval.Server.Tests;

/// <summary>
/// The X-Api-Key check on telemetry ingest — the one machine-to-machine route, and so the one
/// route outside the login everything else sits behind.
/// </summary>
public class TelemetryApiKeyTests
{
    private static HttpContext WithHeader(string? value)
    {
        var context = new DefaultHttpContext();
        if (value is not null)
        {
            context.Request.Headers["X-Api-Key"] = value;
        }

        return context;
    }

    /// <summary>
    /// The property this route turns on. An unconfigured key means no module has been granted
    /// access yet, which is a reason to refuse — reading it as permission would leave a default
    /// deployment taking transcripts and detections for any camera from anyone who can reach the
    /// port, and showing them live to every connected client.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("anything-at-all")]
    public void No_configured_key_means_nothing_is_accepted(string? presented) =>
        Assert.False(TelemetryApiKeyTests.Check(presented, apiKey: ""));

    [Fact]
    public void The_matching_key_is_accepted() =>
        Assert.True(Check("s3cret-module-key", apiKey: "s3cret-module-key"));

    [Theory]
    [InlineData(null)]              // header absent entirely
    [InlineData("")]                // header present but empty
    [InlineData("wrong")]
    [InlineData("s3cret-module-ke")]  // a prefix
    [InlineData("s3cret-module-key ")] // trailing space: not the same key
    [InlineData("S3CRET-MODULE-KEY")]  // a secret is compared exactly, not case-insensitively
    public void Anything_else_is_refused(string? presented) =>
        Assert.False(Check(presented, apiKey: "s3cret-module-key"));

    /// <summary>
    /// Two X-Api-Key headers is not a way to submit two guesses per request, nor a shape whose
    /// concatenation might land on the key by accident.
    /// </summary>
    [Fact]
    public void A_repeated_header_is_refused()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = new[] { "wrong", "s3cret-module-key" };

        Assert.False(TelemetryEndpoints.IsAuthorized(context, "s3cret-module-key"));
    }

    /// <summary>Non-ASCII keys survive the UTF-8 round trip the comparison does.</summary>
    [Fact]
    public void A_unicode_key_still_matches_itself() =>
        Assert.True(Check("clé-caméra-Ω", apiKey: "clé-caméra-Ω"));

    private static bool Check(string? presented, string apiKey) =>
        TelemetryEndpoints.IsAuthorized(WithHeader(presented), apiKey);
}
