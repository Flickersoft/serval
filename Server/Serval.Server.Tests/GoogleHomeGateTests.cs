using System.Text.Json;
using Serval.Server.Configuration;
using Serval.Server.GoogleHome;

namespace Serval.Server.Tests;

/// <summary>
/// The six conditions that have to hold before the Google Home routes answer anything but 503,
/// each one closed on its own.
///
/// <para><b>Why one test per condition rather than one for the whole predicate.</b> The gate's
/// value is not that it says no — a single <c>Enabled</c> check would do that — it is that it says
/// <em>which</em>. An operator who has filled in five of six values gets one sentence naming the
/// sixth, and a bug that collapses two conditions into the same message costs an hour of looking
/// at the wrong setting. That failure is invisible to a test that only asserts
/// <c>Effective == false</c>.</para>
/// </summary>
public class GoogleHomeGateTests
{
    /// <summary>
    /// Everything set correctly. Every test below takes this and breaks exactly one thing, so a
    /// condition added to the gate without a test here turns this fixture red rather than passing
    /// silently.
    /// </summary>
    private static ServerOptions Configured() => new()
    {
        WebRtc = { Enabled = true },
        GoogleHome =
        {
            Enabled = true,
            PublicBaseUrl = "https://serval.example.com",
            ProjectId = "serval-house-1234",
            ClientId = "P3nfCk2Zq9wR4tYuIoPaSdFgHjKlZxCv",
            ClientSecret = "vBnMqWeRtYuIoPaSdFgHjKlZxCvBnM12",
            HomeGraphKeyPath = "/app/secrets/homegraph.json",
        },
    };

    [Fact]
    public void A_fully_configured_deployment_is_effective()
    {
        GoogleHomeStatus status = GoogleHomeGate.Evaluate(Configured());

        Assert.True(status.Effective);
        Assert.Equal(GoogleHomeBlocker.None, status.Blocker);
        Assert.Null(status.Reason);
        Assert.Equal("https://serval.example.com", status.PublicBaseUrl);
        Assert.True(status.HomeGraphKeyConfigured);
    }

    [Fact]
    public void The_master_switch_closes_it()
    {
        ServerOptions options = Configured();
        options.GoogleHome.Enabled = false;

        Assert.Equal(GoogleHomeBlocker.Disabled, GoogleHomeGate.Evaluate(options).Blocker);
    }

    /// <summary>
    /// The dependency worth pinning: every camera offered to Google is streamed by the go2rtc
    /// sidecar, so WebRTC being off is not a coincidence to route around — there is nothing to
    /// sign into. Without this the failure surfaces two layers later, as a stream request that
    /// Google accepts and the display never connects.
    /// </summary>
    [Fact]
    public void WebRtc_being_off_closes_it()
    {
        ServerOptions options = Configured();
        options.WebRtc.Enabled = false;

        Assert.Equal(GoogleHomeBlocker.WebRtcDisabled, GoogleHomeGate.Evaluate(options).Blocker);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    // Plain HTTP: Google will not post to it, so accepting it here would only move the failure to
    // a place where the error message is Google's rather than ours.
    [InlineData("http://serval.example.com")]
    // Relative, which is what a hostname typed without a scheme produces.
    [InlineData("serval.example.com")]
    [InlineData("//serval.example.com")]
    public void A_public_base_url_that_is_not_absolute_https_closes_it(string value)
    {
        ServerOptions options = Configured();
        options.GoogleHome.PublicBaseUrl = value;

        Assert.Equal(
            GoogleHomeBlocker.PublicBaseUrlInvalid, GoogleHomeGate.Evaluate(options).Blocker);
    }

    [Fact]
    public void A_missing_project_id_closes_it()
    {
        ServerOptions options = Configured();
        options.GoogleHome.ProjectId = "";

        Assert.Equal(GoogleHomeBlocker.ProjectIdMissing, GoogleHomeGate.Evaluate(options).Blocker);
    }

    /// <summary>
    /// Empty closes rather than opens, the rule <see cref="ServerOptions.ApiKey"/> already follows:
    /// unset means no client has been granted access, not that every caller has it. The client id
    /// is the only gate on who may link an account, so getting this backwards would leave the
    /// cameras reachable by any Google account that found the endpoint.
    /// </summary>
    [Fact]
    public void A_missing_client_id_closes_it()
    {
        ServerOptions options = Configured();
        options.GoogleHome.ClientId = "";

        Assert.Equal(GoogleHomeBlocker.ClientIdMissing, GoogleHomeGate.Evaluate(options).Blocker);
    }

    [Fact]
    public void A_missing_client_secret_closes_it()
    {
        ServerOptions options = Configured();
        options.GoogleHome.ClientSecret = "";

        Assert.Equal(
            GoogleHomeBlocker.ClientSecretMissing, GoogleHomeGate.Evaluate(options).Blocker);
    }

    /// <summary>
    /// The HomeGraph key is the one Google Home setting that is <em>not</em> a condition. It buys
    /// requestSync and nothing else, so a deployment without one works — the device list just goes
    /// stale until someone re-links. Pinned because the obvious mistake is to treat every
    /// configured-looking value as required.
    /// </summary>
    [Fact]
    public void A_missing_home_graph_key_is_not_a_blocker()
    {
        ServerOptions options = Configured();
        options.GoogleHome.HomeGraphKeyPath = "";

        GoogleHomeStatus status = GoogleHomeGate.Evaluate(options);

        Assert.True(status.Effective);
        Assert.False(status.HomeGraphKeyConfigured);
    }

    /// <summary>
    /// <b>The blocker crosses the wire as a name, not as its ordinal.</b>
    ///
    /// <para>This is pinned because it already broke once, and broke invisibly. System.Text.Json
    /// writes an unattributed enum as a number, so the App received <c>"blocker": 1</c> where it
    /// expected a string, threw while parsing, and drew no card at all — a feature that looked
    /// simply absent, with nothing in any log. The App's tolerance for the wrong shape is a second
    /// line of defence; this is the first.</para>
    ///
    /// <para>Ordinals would be a storage-format trap besides: inserting a member would silently
    /// renumber every value, which is the same reason <c>StreamRole</c> and <c>Role</c> carry
    /// converters.</para>
    /// </summary>
    [Fact]
    public void The_blocker_crosses_the_wire_as_a_name()
    {
        ServerOptions options = Configured();
        options.GoogleHome.ClientId = "";

        using JsonDocument json = JsonDocument.Parse(
            JsonSerializer.Serialize(GoogleHomeGate.Evaluate(options), Wire));
        JsonElement blocker = json.RootElement.GetProperty("blocker");

        Assert.Equal(JsonValueKind.String, blocker.ValueKind);
        Assert.Equal("clientIdMissing", blocker.GetString());
    }

    /// <summary>Every value, so a member added without checking cannot slip back to an ordinal.</summary>
    [Fact]
    public void Every_blocker_value_serializes_as_a_name()
    {
        foreach (GoogleHomeBlocker blocker in Enum.GetValues<GoogleHomeBlocker>())
        {
            using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(blocker, Wire));
            Assert.Equal(JsonValueKind.String, json.RootElement.ValueKind);
        }
    }

    /// <summary>
    /// The options a minimal API actually serializes with. Using
    /// <see cref="JsonSerializer.Serialize{TValue}(TValue, JsonSerializerOptions)"/> bare would
    /// test PascalCase property names the App never sees, and would have quietly passed this
    /// file's first attempt at the test above.
    /// </summary>
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Every blocker names itself. A new one added to the enum without a sentence would answer
    /// null here, which is the state the operator experiences as "it is off and nothing says why".
    /// </summary>
    [Fact]
    public void Every_blocker_carries_an_explanation()
    {
        foreach (GoogleHomeBlocker blocker in Enum.GetValues<GoogleHomeBlocker>())
        {
            string? reason = GoogleHomeGate.Describe(blocker);

            if (blocker == GoogleHomeBlocker.None)
            {
                Assert.Null(reason);
                continue;
            }

            Assert.False(string.IsNullOrWhiteSpace(reason));

            // Naming the key is the whole job: "Google Home is off" is true of all of them.
            Assert.Contains("Serval:", reason, StringComparison.Ordinal);
        }
    }
}
