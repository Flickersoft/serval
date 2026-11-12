using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Serval.Server.Configuration;
using Serval.Server.Mcp;
using Serval.Server.Telemetry;

namespace Serval.Server.Tests;

/// <summary>
/// The protocol rules of Serval's MCP authorization server: PKCE, redirect matching, the redirect
/// it sends back, and what it will accept as a client. Each is a place where being slightly lenient
/// hands somebody else a token for the cameras, so each is pinned exactly.
/// </summary>
public sealed class McpOAuthTests
{
    // ---------------------------------------------------------------- PKCE

    /// <summary>RFC 7636 Appendix B's worked example, so this is checked against the spec rather than against itself.</summary>
    [Fact]
    public void PkceMatchesTheRfcExample()
    {
        Assert.True(McpOAuthRules.VerifyPkce(
            "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk",
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
    }

    [Fact]
    public void PkceRefusesTheWrongVerifier()
    {
        Assert.False(McpOAuthRules.VerifyPkce(
            "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXX",
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
    }

    /// <summary>
    /// A verifier shorter than 43 characters is guessable and the spec forbids it — even one whose
    /// hash happens to match, which this constructs on purpose.
    /// </summary>
    [Fact]
    public void PkceRefusesAShortVerifierEvenWhenItsHashMatches()
    {
        const string shortVerifier = "too-short";
        string challenge = System.Buffers.Text.Base64Url.EncodeToString(
            System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(shortVerifier)));

        Assert.False(McpOAuthRules.VerifyPkce(shortVerifier, challenge));
        Assert.False(McpOAuthRules.VerifyPkce(null, challenge));
    }

    [Theory]
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", true)]
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-c", false)]
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw+cM", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ChallengeShape(string? challenge, bool valid) =>
        Assert.Equal(valid, McpOAuthRules.IsValidChallenge(challenge));

    // ------------------------------------------------------------- redirects

    [Fact]
    public void RedirectMustMatchExactly()
    {
        string[] registered = ["https://claude.ai/api/mcp/auth_callback"];

        Assert.Equal(registered[0], McpOAuthRules.MatchRedirectUri(registered[0], registered));
        Assert.Null(McpOAuthRules.MatchRedirectUri("https://claude.ai/api/mcp/auth_callback/", registered));
        Assert.Null(McpOAuthRules.MatchRedirectUri("https://claude.ai/api/mcp/auth_callback?x=1", registered));
        Assert.Null(McpOAuthRules.MatchRedirectUri("https://CLAUDE.ai/api/mcp/auth_callback", registered));
        Assert.Null(McpOAuthRules.MatchRedirectUri("https://evil.example/api/mcp/auth_callback", registered));
    }

    /// <summary>RFC 8252 §7.3: a desktop client listens on whatever loopback port is free, so the port alone may differ.</summary>
    [Fact]
    public void LoopbackRedirectMayDifferInPortOnly()
    {
        string[] registered = ["http://127.0.0.1:33418/callback"];

        Assert.Equal(
            "http://127.0.0.1:51000/callback",
            McpOAuthRules.MatchRedirectUri("http://127.0.0.1:51000/callback", registered));
        Assert.Null(McpOAuthRules.MatchRedirectUri("http://127.0.0.1:51000/other", registered));
        Assert.Null(McpOAuthRules.MatchRedirectUri("http://localhost:51000/callback", registered));
    }

    [Fact]
    public void AnOmittedRedirectUsesTheOnlyOneRegistered()
    {
        Assert.Equal("https://a.example/cb", McpOAuthRules.MatchRedirectUri(null, ["https://a.example/cb"]));
        Assert.Null(McpOAuthRules.MatchRedirectUri("", ["https://a.example/cb", "https://a.example/cb2"]));
    }

    /// <summary>RFC 9207: every authorization response names the issuer, errors included, and state comes back untouched.</summary>
    [Fact]
    public void TheRedirectCarriesStateAndIssuer()
    {
        string url = McpOAuthRules.BuildRedirect(
            "https://client.example/cb?keep=1", "https://serval.example", "a b&c", code: "xyz");

        Assert.Equal("https://client.example/cb?keep=1&code=xyz&state=a%20b%26c&iss=https%3A%2F%2Fserval.example", url);

        string error = McpOAuthRules.BuildRedirect("cursor://cb", "https://serval.example", null, error: "access_denied");
        Assert.Equal("cursor://cb?error=access_denied&iss=https%3A%2F%2Fserval.example", error);
    }

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", "claude.ai")]
    [InlineData("http://127.0.0.1:33418/callback", "127.0.0.1:33418")]
    [InlineData("cursor://anysphere.cursor-mcp/oauth/callback", "cursor://anysphere.cursor-mcp")]
    [InlineData("com.example.app:/callback", "com.example.app:")]
    public void RedirectsAreDescribedForAPerson(string uri, string described) =>
        Assert.Equal(described, McpOAuthRules.DescribeRedirect(uri));

    // ---------------------------------------------------------------- clients

    [Theory]
    [InlineData("https://claude.ai/oauth/mcp-oauth-client-metadata", true)]
    [InlineData("https://claude.ai/", false)]
    [InlineData("https://claude.ai", false)]
    [InlineData("http://claude.ai/metadata", false)]
    [InlineData("https://user@claude.ai/metadata", false)]
    [InlineData("https://claude.ai/metadata#x", false)]
    [InlineData("3qZr8w-registered-id", false)]
    public void WhichClientIdsAreMetadataDocuments(string clientId, bool isDocument) =>
        Assert.Equal(isDocument, McpClientResolver.IsMetadataDocumentUrl(clientId));

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback", true)]
    [InlineData("http://127.0.0.1:7777/cb", true)]
    [InlineData("http://localhost:7777/cb", true)]
    [InlineData("http://[::1]:7777/cb", true)]
    [InlineData("cursor://anysphere.cursor-mcp/oauth/callback", true)]
    [InlineData("http://evil.example/cb", false)]
    [InlineData("https://a.example/cb#frag", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,hi", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("/relative/cb", false)]
    [InlineData("", false)]
    public void WhichRedirectsAreAcceptable(string uri, bool acceptable) =>
        Assert.Equal(acceptable, McpClientResolver.IsAcceptableRedirectUri(uri));

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:192.168.1.10", false)]
    // IPv6 forms that carry, or translate to, an IPv4 address that may be private.
    [InlineData("64:ff9b::a9fe:a9fe", false)]  // NAT64 → 169.254.169.254
    [InlineData("2002:c0a8:0101::1", false)]   // 6to4 around 192.168.1.1
    [InlineData("2001:0:4136:e378::1", false)] // Teredo
    [InlineData("2001:db8::1", false)]         // documentation
    [InlineData("::a9fe:a9fe", false)]         // IPv4-compatible
    [InlineData("100::1", false)]              // discard
    public void OnlyPublicAddressesMayBeFetched(string address, bool isPublic) =>
        Assert.Equal(isPublic, McpClientResolver.IsPublicAddress(IPAddress.Parse(address)));

    private const string DocumentUrl = "https://client.example/mcp-client.json";

    [Fact]
    public void AMetadataDocumentIsAccepted()
    {
        McpClient? client = McpClientResolver.ParseMetadataDocument(DocumentUrl, Encoding.UTF8.GetBytes(
            """{"client_id":"https://client.example/mcp-client.json","client_name":"Example","redirect_uris":["https://client.example/cb"],"token_endpoint_auth_method":"none"}"""));

        Assert.NotNull(client);
        Assert.True(client.Verified);
        Assert.Equal("Example", client.Name);
        Assert.Equal("client.example", client.VerifiedHost);
        Assert.Equal(["https://client.example/cb"], client.RedirectUris);
    }

    /// <summary>A document naming any other client_id could be one client's description served from another's address.</summary>
    [Fact]
    public void AMetadataDocumentMustNameItsOwnUrl()
    {
        Assert.Null(McpClientResolver.ParseMetadataDocument(DocumentUrl, Encoding.UTF8.GetBytes(
            """{"client_id":"https://other.example/mcp-client.json","redirect_uris":["https://client.example/cb"]}""")));
    }

    [Theory]
    [InlineData("""{"client_id":"https://client.example/mcp-client.json","redirect_uris":[]}""")]
    [InlineData("""{"client_id":"https://client.example/mcp-client.json","redirect_uris":["http://evil.example/cb"]}""")]
    [InlineData("""{"client_id":"https://client.example/mcp-client.json","redirect_uris":["https://a/cb"],"token_endpoint_auth_method":"private_key_jwt"}""")]
    [InlineData("""["not","an","object"]""")]
    [InlineData("""not json""")]
    public void UnusableMetadataDocumentsAreRefused(string json) =>
        Assert.Null(McpClientResolver.ParseMetadataDocument(DocumentUrl, Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void ClientNamesAreCleanedForDisplay()
    {
        Assert.Null(McpClientResolver.CleanName("   "));
        Assert.Equal("Claude", McpClientResolver.CleanName("  Cla\u0000ude\n "));
        Assert.Equal(81, McpClientResolver.CleanName(new string('x', 200))!.Length);
    }

    // ------------------------------------------------------------------ gate

    [Fact]
    public void TheGateIsClosedByDefault()
    {
        McpStatus status = McpGate.Evaluate(new McpOptions());

        Assert.False(status.Effective);
        Assert.Equal(McpBlocker.Disabled, status.Blocker);
        Assert.Null(status.ServerUrl);
    }

    [Theory]
    [InlineData("https://serval.example.com", "https://serval.example.com/mcp")]
    [InlineData("https://serval.example.com/", "https://serval.example.com/mcp")]
    [InlineData("https://serval.example.com:8443", "https://serval.example.com:8443/mcp")]
    [InlineData("http://localhost:8080", "http://localhost:8080/mcp")]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080/mcp")]
    public void AUsableAddressOpensTheGate(string baseUrl, string serverUrl)
    {
        McpStatus status = McpGate.Evaluate(new McpOptions { Enabled = true, PublicBaseUrl = baseUrl });

        Assert.True(status.Effective);
        Assert.Equal(serverUrl, status.ServerUrl);
    }

    /// <summary>
    /// Plain http beyond loopback would send tokens in the clear; a path would publish an issuer
    /// that does not match where its metadata is found.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("serval.example.com")]
    [InlineData("http://serval.example.com")]
    [InlineData("http://192.168.1.20:8080")]
    [InlineData("https://serval.example.com/serval")]
    [InlineData("https://serval.example.com/?x=1")]
    public void AnUnusableAddressKeepsTheGateClosed(string baseUrl)
    {
        McpStatus status = McpGate.Evaluate(new McpOptions { Enabled = true, PublicBaseUrl = baseUrl });

        Assert.False(status.Effective);
        Assert.Equal(McpBlocker.PublicBaseUrlInvalid, status.Blocker);
    }

    [Fact]
    public void TheIssuerAndResourceFollowTheLiveSettings()
    {
        var options = new TestOptionsMonitor<ServerOptions>(new ServerOptions());
        var gate = new McpGate(options);
        Assert.Null(gate.Issuer);

        options.Set(new ServerOptions { Mcp = new McpOptions { Enabled = true, PublicBaseUrl = "https://serval.example/" } });

        Assert.Equal("https://serval.example", gate.Issuer);
        Assert.Equal("https://serval.example/mcp", gate.Resource);
    }

    /// <summary>
    /// The settings page refuses an address the gate would refuse, on the field being edited,
    /// rather than saving it and closing the endpoint with the reason on another screen.
    /// </summary>
    [Theory]
    [InlineData("https://serval.example.com", true)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("https://serval.example.com/mcp", false)]
    [InlineData("http://192.168.1.20:8080", false)]
    public void ThePublicAddressIsValidatedWhereItIsEdited(string value, bool accepted)
    {
        SettingDescriptor descriptor = SettingsCatalog.Find(SettingsCatalog.McpPublicBaseUrlKey)!;
        Assert.Equal(accepted, SettingsCatalog.Validate(descriptor, value) is null);
    }

    [Theory]
    [InlineData("/mcp", 404)]
    [InlineData("/.well-known/oauth-authorization-server", 404)]
    [InlineData("/api/mcp/oauth/token", 404)]
    [InlineData("/api/mcp/status", 200)]
    [InlineData("/api/mcp/grants", 200)]
    [InlineData("/api/mcp/grants/abc", 200)]
    [InlineData("/api/cameras", 200)]
    public async Task AClosedGateHidesEverythingButTheAppsTwoRoutes(string path, int status)
    {
        var gate = new McpGate(new TestOptionsMonitor<ServerOptions>(new ServerOptions()));
        var middleware = new McpGateMiddleware(
            context => { context.Response.StatusCode = 200; return Task.CompletedTask; },
            gate);
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        Assert.Equal(status, context.Response.StatusCode);
    }

    // ---------------------------------------------------------------- policy

    /// <summary>
    /// /mcp's policy names the assistant scheme and only that one. Listing the default JWT scheme
    /// beside it would let an App session — which can write — act as an assistant, and the line
    /// that would add it is one word long.
    /// </summary>
    [Fact]
    public void TheMcpPolicyAcceptsOnlyAssistantTokens()
    {
        var builder = new AuthorizationPolicyBuilder();
        McpBearerHandler.ConfigurePolicy(builder);
        AuthorizationPolicy policy = builder.Build();

        Assert.Equal([McpBearerHandler.SchemeName], policy.AuthenticationSchemes);
    }

    // ----------------------------------------------------------------- tools

    [Fact]
    public void AWindowDefaultsBackFromItsEnd()
    {
        var to = new DateTimeOffset(2026, 11, 12, 18, 0, 0, TimeSpan.Zero);

        (DateTimeOffset from, DateTimeOffset end) = McpTools.Window(null, to, TimeSpan.FromHours(24));

        Assert.Equal(to.AddHours(-24), from);
        Assert.Equal(to, end);
    }

    /// <summary>Refused rather than shortened: a silently truncated "did anyone come last month" reads as "no".</summary>
    [Fact]
    public void AWindowLongerThanAWeekIsRefusedNotTruncated()
    {
        var to = new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

        McpException thrown = Assert.Throws<McpException>(() => McpTools.Window(to.AddDays(-30), to, TimeSpan.FromHours(24)));
        Assert.Contains("at most 7 days", thrown.Message, StringComparison.Ordinal);

        Assert.Throws<McpException>(() => McpTools.Window(to, to.AddHours(-1), TimeSpan.FromHours(24)));
    }

    [Fact]
    public void KindsAcceptPluralsAndRefuseUnknowns()
    {
        Assert.Equal(["scene", "sound"], McpTools.SelectKinds(["Scenes", "sound"]).Order());
        Assert.Equal(["speech"], McpTools.SelectKinds(["speeches"]));
        Assert.Equal(McpTools.AllKinds.Count, McpTools.SelectKinds(null).Count);

        McpException thrown = Assert.Throws<McpException>(() => McpTools.SelectKinds(["video"]));
        Assert.Contains("scene, detection, speech, conversation, sound", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CamerasAreCheckedAndDefaultToAll()
    {
        var names = new Dictionary<string, string> { ["front"] = "Front door", ["yard"] = "Yard" };

        Assert.Equal(["front", "yard"], McpTools.SelectCameras(names, null).Order());
        Assert.Equal(["yard"], McpTools.SelectCameras(names, ["yard", "yard"]));

        McpException thrown = Assert.Throws<McpException>(() => McpTools.SelectCameras(names, ["garage"]));
        Assert.Contains("list_cameras", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>The search text is a phrase, not a pattern: a model asking for "a.b(" must not get a regex error or a wildcard.</summary>
    [Fact]
    public void SearchTextIsMatchedLiterally()
    {
        Assert.Null(TelemetryRepository.Contains("   "));

        MongoDB.Bson.BsonRegularExpression pattern = TelemetryRepository.Contains(" red truck (maybe) ")!;
        Assert.Equal(@"red\ truck\ \(maybe\)", pattern.Pattern);
        Assert.Equal("i", pattern.Options);
    }

    [Fact]
    public void RecordingLinksOpenFiveSecondsEarlyInUtc()
    {
        // 17:09:30 at -05:00 is 22:09:30Z; less the lead-in is 22:09:25Z. No '+', which a query
        // string would read as a space.
        string? url = McpLinks.CameraAt("https://serval.example", "front door", DateTimeOffset.Parse("2026-11-12T17:09:30.734-05:00"));

        Assert.Equal("https://serval.example/camera/front%20door?at=2026-11-12T22:09:25Z", url);
    }

    [Fact]
    public void LinksNameTheAppsOwnRoutes()
    {
        Assert.Equal("https://serval.example/camera/7", McpLinks.Camera("https://serval.example", "7"));
        Assert.Equal("https://serval.example/alerts/a1", McpLinks.Alert("https://serval.example", "a1"));
        Assert.Equal("https://serval.example/clips/c1", McpLinks.Clip("https://serval.example", "c1"));
    }

    [Fact]
    public void WithoutAPublicAddressThereAreNoLinks()
    {
        Assert.Null(McpLinks.Camera(null, "7"));
        Assert.Null(McpLinks.CameraAt(null, "7", DateTimeOffset.UtcNow));
        Assert.Null(McpLinks.Alert(null, "a1"));
        Assert.Null(McpLinks.Clip(null, "c1"));
    }

    [Fact]
    public void AnImageCarriesItsLinkInTheCaption()
    {
        CallToolResult result = McpTools.Image([0xFF, 0xD8], "Front Door, live.", "https://serval.example/camera/7");

        var caption = Assert.IsType<TextContentBlock>(result.Content[0]);
        Assert.Equal("Front Door, live. Watch it in Serval: https://serval.example/camera/7", caption.Text);
        Assert.IsType<ImageContentBlock>(result.Content[1]);
    }

    [Fact]
    public void ServerInfoPointsItsIconsAtThePublicAddress()
    {
        var info = McpRegistration.DescribeServer("https://serval.example");

        Assert.Equal("Serval", info.Title);
        Assert.Equal("https://serval.example", info.WebsiteUrl);
        Assert.Equal(
            ["https://serval.example/icons/Icon-192.png", "https://serval.example/icons/Icon-512.png"],
            info.Icons!.Select(icon => icon.Source));
        Assert.All(info.Icons!, icon => Assert.Equal("image/png", icon.MimeType));
    }

    [Fact]
    public void ServerInfoWithoutAPublicAddressOffersNoIcons()
    {
        var info = McpRegistration.DescribeServer(issuer: null);

        Assert.Null(info.Icons);
        Assert.Null(info.WebsiteUrl);
    }
}
