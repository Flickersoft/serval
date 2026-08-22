using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Serval.Server.GoogleHome;
using Serval.Server.Storage;

namespace Serval.Server.Tests;

/// <summary>
/// The parts of the OAuth provider that decide whether a stranger can reach the cameras, and the
/// parts Google parses.
///
/// <para><b>What is not here, and why.</b> The code and token lifecycle — single use, expiry, the
/// refresh grant — lives in <see cref="GoogleOAuthStore"/> against MongoDB, and nothing in this
/// suite touches a database. Those are verified end to end against the Google console's
/// account-linking test, which is the step in <c>Docs/google-home.md</c> that exists for it. What
/// is covered here is everything that can be got wrong without a database: the redirect allowlist,
/// the constant-time compares, the wire format, and how the documents are stored.</para>
/// </summary>
public class GoogleOAuthTests
{
    private const string Project = "serval-house-1234";

    public GoogleOAuthTests() => BsonRegistration.Register();

    // -------------------------------------------------- the redirect allowlist

    /// <summary>
    /// The two destinations Google publishes, and the only two an authorization code is ever sent
    /// to.
    /// </summary>
    [Theory]
    [InlineData("https://oauth-redirect.googleusercontent.com/r/serval-house-1234")]
    [InlineData("https://oauth-redirect-sandbox.googleusercontent.com/r/serval-house-1234")]
    public void Googles_own_redirect_uris_are_allowed(string uri) =>
        Assert.True(GoogleOAuthEndpoints.IsAllowedRedirect(uri, Project));

    /// <summary>
    /// <b>The open-redirect guard, which is the sharpest edge in this feature.</b> The authorize
    /// route hands out an authorization code by redirecting to whatever it is told, so a check that
    /// can be talked past turns this endpoint into a way to have Serval deliver a credential to an
    /// attacker. Every case below is a way somebody would try.
    /// </summary>
    [Theory]
    // Somewhere else entirely.
    [InlineData("https://evil.example.com/r/serval-house-1234")]
    // The right path on a lookalike host — the trick a naive "contains googleusercontent" would
    // fall for.
    [InlineData("https://oauth-redirect.googleusercontent.com.evil.example.com/r/serval-house-1234")]
    [InlineData("https://evil.example.com/oauth-redirect.googleusercontent.com/r/serval-house-1234")]
    // Right host, someone else's project — this is what stops a stranger's Google project from
    // linking to this house.
    [InlineData("https://oauth-redirect.googleusercontent.com/r/some-other-project")]
    // Right host and project with something appended, which a StartsWith check would accept.
    [InlineData("https://oauth-redirect.googleusercontent.com/r/serval-house-1234/../../evil")]
    [InlineData("https://oauth-redirect.googleusercontent.com/r/serval-house-1234?next=evil")]
    [InlineData("https://oauth-redirect.googleusercontent.com/r/serval-house-1234#x")]
    // Plain HTTP to the right place.
    [InlineData("http://oauth-redirect.googleusercontent.com/r/serval-house-1234")]
    // Not a URL at all.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/r/serval-house-1234")]
    public void Anything_else_is_refused(string uri) =>
        Assert.False(GoogleOAuthEndpoints.IsAllowedRedirect(uri, Project));

    /// <summary>
    /// With no project id configured nothing is allowed, so a half-configured deployment cannot
    /// accidentally accept a redirect built from an empty string — which is what
    /// <c>https://oauth-redirect.googleusercontent.com/r/</c> would be.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void No_project_id_allows_nothing(string projectId)
    {
        Assert.False(GoogleOAuthEndpoints.IsAllowedRedirect(
            "https://oauth-redirect.googleusercontent.com/r/", projectId));
        Assert.False(GoogleOAuthEndpoints.IsAllowedRedirect(
            $"https://oauth-redirect.googleusercontent.com/r/{projectId}", projectId));
    }

    // ------------------------------------------------ the constant-time compare

    [Fact]
    public void A_matching_secret_is_accepted() =>
        Assert.True(GoogleOAuthEndpoints.FixedTimeEquals("s3cret-value", "s3cret-value"));

    [Theory]
    [InlineData("s3cret-valu")]      // short by one
    [InlineData("s3cret-values")]    // long by one
    [InlineData("S3cret-value")]     // case matters
    [InlineData("wrong")]
    public void A_wrong_secret_is_refused(string provided) =>
        Assert.False(GoogleOAuthEndpoints.FixedTimeEquals(provided, "s3cret-value"));

    /// <summary>
    /// <b>Unset closes the route rather than opening it</b> — the rule <c>Serval:ApiKey</c> already
    /// follows for telemetry ingest. Getting this backwards is the difference between an
    /// unconfigured deployment being inert and it accepting an empty client id from anyone who
    /// finds the endpoint, and it is exactly the shape a naive equality check produces.
    /// </summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("anything", "")]
    [InlineData("", "s3cret-value")]
    public void An_empty_expected_or_provided_value_never_matches(string provided, string expected) =>
        Assert.False(GoogleOAuthEndpoints.FixedTimeEquals(provided, expected));

    // ------------------------------------------------ where credentials arrive

    /// <summary>
    /// <b>The console checkbox that broke a working integration.</b> Google offers "transmit Client
    /// ID and secret via HTTP basic auth header"; unchecked, they arrive in the form body, which is
    /// all this endpoint originally read. Ticking it turned a linked integration into a silent 401.
    /// Both are accepted now, so the switch is not load-bearing.
    /// </summary>
    [Fact]
    public void Credentials_are_read_from_a_basic_auth_header()
    {
        const string id = "client-id-value";
        const string secret = "s3cret+with/base64=chars";

        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = "Basic " + Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(
                $"{Uri.EscapeDataString(id)}:{Uri.EscapeDataString(secret)}"));

        (string readId, string readSecret) =
            GoogleOAuthEndpoints.ReadClientCredentials(http, new FormCollection(null));

        Assert.Equal(id, readId);

        // The round trip through percent-encoding is the point: `openssl rand -base64 32` routinely
        // produces '+' and '/', and those do not survive a naive split.
        Assert.Equal(secret, readSecret);
    }

    [Fact]
    public void Credentials_are_read_from_the_form_body_when_there_is_no_header()
    {
        var form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["client_id"] = "from-body",
            ["client_secret"] = "secret-from-body",
        });

        (string id, string secret) =
            GoogleOAuthEndpoints.ReadClientCredentials(new DefaultHttpContext(), form);

        Assert.Equal("from-body", id);
        Assert.Equal("secret-from-body", secret);
    }

    /// <summary>
    /// A malformed header falls back rather than throwing. The caller is refused either way, but an
    /// exception here would reach Google as a 500 — reported as an outage rather than a bad
    /// credential.
    /// </summary>
    [Theory]
    [InlineData("Basic not-base64!!")]
    [InlineData("Basic ")]
    [InlineData("Bearer something")]
    public void A_malformed_authorization_header_falls_back_to_the_body(string header)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = header;

        var form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["client_id"] = "from-body",
            ["client_secret"] = "secret-from-body",
        });

        (string id, _) = GoogleOAuthEndpoints.ReadClientCredentials(http, form);

        Assert.Equal("from-body", id);
    }

    // ----------------------------------------------------------- the wire shape

    /// <summary>
    /// <b>RFC 6749 field names, which this server does not otherwise produce.</b> No serializer
    /// options are configured anywhere in <c>Program.cs</c>, so the default is camelCase and every
    /// one of these would ship as <c>tokenType</c>/<c>accessToken</c>/<c>expiresIn</c> without the
    /// attributes on the record. Google would receive well-formed JSON it cannot read, account
    /// linking would fail, and nothing on this side would log a problem — which is why this is
    /// pinned rather than trusted.
    /// </summary>
    [Fact]
    public void The_token_response_uses_the_names_Google_reads()
    {
        var response = new GoogleTokenResponse("Bearer", "at-value", "rt-value", 3600);

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(response));
        JsonElement root = json.RootElement;

        Assert.Equal("Bearer", root.GetProperty("token_type").GetString());
        Assert.Equal("at-value", root.GetProperty("access_token").GetString());
        Assert.Equal("rt-value", root.GetProperty("refresh_token").GetString());
        Assert.Equal(3600, root.GetProperty("expires_in").GetInt32());
    }

    /// <summary>
    /// The refresh grant answers without a refresh token, and the field is <em>absent</em> rather
    /// than null. Google's contract keeps the original valid forever; sending an explicit null
    /// invites a client to store it over the working one.
    /// </summary>
    [Fact]
    public void The_refresh_grant_omits_the_refresh_token_entirely()
    {
        var response = new GoogleTokenResponse("Bearer", "at-value", RefreshToken: null, 3600);

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(response));

        Assert.False(json.RootElement.TryGetProperty("refresh_token", out _));
        Assert.Equal("at-value", json.RootElement.GetProperty("access_token").GetString());
    }

    // -------------------------------------------------------------- storage

    /// <summary>
    /// A credential is stored as a hash and nothing else. Hex of SHA-256, matching
    /// <c>RefreshTokenRepository.Hash</c> — not because anything compares the two, but because one
    /// hashing convention in a codebase is one thing to get right.
    /// </summary>
    [Fact]
    public void A_raw_credential_is_never_recoverable_from_its_hash()
    {
        string hash = GoogleOAuthStore.Hash("code-value");

        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain("code-value", hash, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(hash, GoogleOAuthStore.Hash("code-value"));
        Assert.NotEqual(hash, GoogleOAuthStore.Hash("code-valuf"));
    }

    /// <summary>
    /// Secrets are URL-safe, because every one of them ends up in a query string or a form body.
    /// Base64's '+' and '/' do not survive that unescaped, and the failure would be intermittent —
    /// only the tokens that happened to contain one.
    /// </summary>
    [Fact]
    public void A_generated_secret_is_url_safe_and_not_repeated()
    {
        string[] secrets = [.. Enumerable.Range(0, 32).Select(_ => GoogleOAuthStore.NewSecret())];

        Assert.Equal(secrets.Length, secrets.Distinct(StringComparer.Ordinal).Count());

        foreach (string secret in secrets)
        {
            Assert.DoesNotContain('+', secret);
            Assert.DoesNotContain('/', secret);
            Assert.DoesNotContain('=', secret);

            // 256 bits, which is the point of it.
            Assert.True(secret.Length >= 42, secret);
        }
    }

    /// <summary>
    /// <see cref="GoogleTokenKind"/> is stored by name. The driver's default is the ordinal, which
    /// would make the enum's declaration order a storage format — insert a member and every stored
    /// access token silently becomes a refresh token. Same class of silent corruption
    /// <c>CameraBsonSerializationTests</c> guards for <c>StreamRole</c>, and the reason the
    /// attribute is on the property rather than left to a global serializer registration.
    /// </summary>
    [Fact]
    public void A_token_kind_is_stored_by_name()
    {
        var token = new GoogleToken
        {
            TokenHash = "abc",
            Kind = GoogleTokenKind.Refresh,
            AgentUserId = "agent-1",
        };

        BsonDocument stored = token.ToBsonDocument();

        Assert.Equal("Refresh", stored["Kind"].AsString);
    }

    /// <summary>
    /// <b>A refresh token stores no expiry at all, and that is what keeps the TTL index off it.</b>
    /// MongoDB's TTL index only acts on a document whose indexed field is a date, so a null here
    /// means "never swept" rather than "swept immediately" — which is the behaviour a refresh token
    /// needs, since Google's contract never issues it a replacement. Writing a far-future date
    /// instead would work by accident and break on the day it arrived.
    /// </summary>
    [Fact]
    public void A_refresh_token_stores_no_expiry_for_the_ttl_index_to_act_on()
    {
        var refresh = new GoogleToken
        {
            TokenHash = "abc",
            Kind = GoogleTokenKind.Refresh,
            AgentUserId = "agent-1",
            ExpiresAt = null,
        };

        Assert.Equal(BsonNull.Value, refresh.ToBsonDocument()["ExpiresAt"]);

        // An access token does carry one, so the index has something to act on there.
        var access = new GoogleToken
        {
            TokenHash = "def",
            Kind = GoogleTokenKind.Access,
            AgentUserId = "agent-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        };

        Assert.Equal(BsonType.DateTime, access.ToBsonDocument()["ExpiresAt"].BsonType);
    }

    /// <summary>
    /// The agent user id is the link's <c>_id</c>, so there is one document and no index needed.
    /// It is also a generated GUID rather than a username — Google is told this value and sends it
    /// back on every call.
    /// </summary>
    [Fact]
    public void A_link_is_keyed_by_its_agent_user_id()
    {
        var link = new GoogleLink { AgentUserId = "0f8fad5bd9cb469fa16570867728950e" };

        BsonDocument stored = link.ToBsonDocument();

        Assert.Equal("0f8fad5bd9cb469fa16570867728950e", stored["_id"].AsString);
        Assert.False(stored.Contains("AgentUserId"));
    }

    /// <summary>
    /// Extra elements are tolerated on every one of these, the project-wide rule: a document
    /// written by a newer build must not make an older one throw on read.
    /// </summary>
    [Fact]
    public void Every_google_document_tolerates_unknown_fields()
    {
        foreach (Type type in new[]
        {
            typeof(GoogleAuthorizationCode), typeof(GoogleToken), typeof(GoogleLink),
        })
        {
            Assert.True(
                type.GetCustomAttributes(
                    typeof(MongoDB.Bson.Serialization.Attributes.BsonIgnoreExtraElementsAttribute),
                    inherit: false).Length > 0,
                $"{type.Name} must carry [BsonIgnoreExtraElements].");
        }
    }
}
