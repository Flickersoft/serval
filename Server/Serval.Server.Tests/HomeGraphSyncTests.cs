using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serval.Ai;
using Serval.Server.Cameras;
using Serval.Server.GoogleHome;
using Serval.Server.Snapshots;

namespace Serval.Server.Tests;

/// <summary>
/// The two halves of "Google finds out a camera changed": the assertion that buys an access token,
/// and the signature that decides whether to spend one.
/// </summary>
public class HomeGraphSyncTests
{
    private static Camera Camera(string id, string? name = null, string? location = null) => new()
    {
        Id = id,
        Name = name ?? id,
        Location = location,
        Streams = [new CameraStream { Name = "sub", Url = "rtsp://cam/sub", Roles = [StreamRole.Live] }],
    };

    // ------------------------------------------------------- the assertion

    /// <summary>
    /// A throwaway service-account key. Generating one is cheaper than checking a fixture in, and
    /// it means the verification below is against a key this test actually owns the public half of.
    /// </summary>
    private static (HomeGraphKey Key, RSA Rsa) NewKey()
    {
        RSA rsa = RSA.Create(2048);
        return (
            new HomeGraphKey(
                ClientEmail: "serval@example-project.iam.gserviceaccount.com",
                PrivateKeyPem: rsa.ExportPkcs8PrivateKeyPem(),
                TokenUri: null),
            rsa);
    }

    private static string Decode(string segment) =>
        Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(segment));

    /// <summary>
    /// <b>Verified against the key's own public half, not merely inspected.</b> A malformed
    /// assertion comes back from Google as an opaque 400 with no indication of which field was
    /// wrong, so the check has to happen here or nowhere.
    /// </summary>
    [Fact]
    public void The_assertion_is_a_valid_rs256_jwt()
    {
        (HomeGraphKey key, RSA rsa) = NewKey();
        using (rsa)
        {
            string assertion = HomeGraphClient.SignAssertion(key, DateTimeOffset.UnixEpoch.AddDays(1));

            string[] parts = assertion.Split('.');
            Assert.Equal(3, parts.Length);

            byte[] signature = System.Buffers.Text.Base64Url.DecodeFromChars($"{parts[2]}");

            Assert.True(rsa.VerifyData(
                Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1));
        }
    }

    /// <summary>
    /// The header and claims Google's JWT-bearer grant requires. <c>aud</c> in particular has to be
    /// the token endpoint rather than the API being called, which is the detail most easily got
    /// wrong.
    /// </summary>
    [Fact]
    public void The_assertion_claims_what_Google_expects()
    {
        (HomeGraphKey key, RSA rsa) = NewKey();
        using (rsa)
        {
            DateTimeOffset now = DateTimeOffset.UnixEpoch.AddDays(1);
            string[] parts = HomeGraphClient.SignAssertion(key, now).Split('.');

            using JsonDocument header = JsonDocument.Parse(Decode(parts[0]));
            Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
            Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());

            using JsonDocument claims = JsonDocument.Parse(Decode(parts[1]));
            JsonElement root = claims.RootElement;

            Assert.Equal(key.ClientEmail, root.GetProperty("iss").GetString());
            Assert.Equal(
                "https://www.googleapis.com/auth/homegraph", root.GetProperty("scope").GetString());
            Assert.Equal(
                "https://oauth2.googleapis.com/token", root.GetProperty("aud").GetString());
            Assert.Equal(now.ToUnixTimeSeconds(), root.GetProperty("iat").GetInt64());
            Assert.Equal(now.AddHours(1).ToUnixTimeSeconds(), root.GetProperty("exp").GetInt64());
        }
    }

    /// <summary>A key file naming its own token endpoint is honoured, and it signs for that audience.</summary>
    [Fact]
    public void A_key_may_name_its_own_token_endpoint()
    {
        (HomeGraphKey key, RSA rsa) = NewKey();
        using (rsa)
        {
            HomeGraphKey custom = key with { TokenUri = "https://oauth2.example.com/token" };

            string[] parts = HomeGraphClient.SignAssertion(custom, DateTimeOffset.UnixEpoch).Split('.');
            using JsonDocument claims = JsonDocument.Parse(Decode(parts[1]));

            Assert.Equal("https://oauth2.example.com/token", claims.RootElement.GetProperty("aud").GetString());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_key_with_no_token_uri_falls_back_to_Googles(string? tokenUri) =>
        Assert.Equal(
            "https://oauth2.googleapis.com/token",
            new HomeGraphKey("a@b.com", "pem", tokenUri).EffectiveTokenUri);

    // -------------------------------------------------------- the signature

    [Fact]
    public void An_unchanged_registry_produces_an_unchanged_signature()
    {
        Camera[] cameras = [Camera("front-door", "Front Door", "Driveway"), Camera("garage")];

        Assert.Equal(
            GoogleHomeSyncWorker.Signature(cameras), GoogleHomeSyncWorker.Signature(cameras));
    }

    /// <summary>Ordering is not a change — the registry's list order is not something Google sees.</summary>
    [Fact]
    public void Reordering_the_registry_is_not_a_change()
    {
        Camera[] a = [Camera("front-door"), Camera("garage")];
        Camera[] b = [Camera("garage"), Camera("front-door")];

        Assert.Equal(GoogleHomeSyncWorker.Signature(a), GoogleHomeSyncWorker.Signature(b));
    }

    /// <summary>Each of the three things SYNC actually carries moves the signature.</summary>
    [Fact]
    public void Anything_Google_was_told_moves_the_signature()
    {
        string baseline = GoogleHomeSyncWorker.Signature([Camera("front-door", "Front Door", "Driveway")]);

        Assert.NotEqual(baseline, GoogleHomeSyncWorker.Signature([Camera("front-door", "Side Door", "Driveway")]));
        Assert.NotEqual(baseline, GoogleHomeSyncWorker.Signature([Camera("front-door", "Front Door", "Porch")]));
        Assert.NotEqual(baseline, GoogleHomeSyncWorker.Signature([Camera("side-door", "Front Door", "Driveway")]));
        Assert.NotEqual(baseline, GoogleHomeSyncWorker.Signature(
            [Camera("front-door", "Front Door", "Driveway"), Camera("garage")]));
    }

    /// <summary>
    /// <b>And nothing else does.</b> This is the point of hashing the rendered device rather than
    /// the camera document: retention, ONVIF credentials and detection tuning all change without
    /// altering anything Google was ever told, and hashing the whole record would spend a call to
    /// Google on every edit anyone makes in the App.
    /// </summary>
    [Fact]
    public void Settings_Google_never_saw_do_not_move_the_signature()
    {
        Camera plain = Camera("front-door", "Front Door", "Driveway");

        Camera retuned = Camera("front-door", "Front Door", "Driveway");
        retuned.RetentionDays = 30;
        retuned.RecordAudio = true;
        retuned.AiVision = true;
        retuned.OnvifUrl = "http://192.0.2.10/onvif/device_service";
        retuned.OnvifPassword = "hunter2";

        Assert.Equal(
            GoogleHomeSyncWorker.Signature([plain]), GoogleHomeSyncWorker.Signature([retuned]));
    }

    /// <summary>
    /// A camera Google is not told about cannot move the signature, or every disabled test camera
    /// would cost a call to Google whenever somebody touched it.
    /// </summary>
    [Fact]
    public void An_ineligible_camera_does_not_move_the_signature()
    {
        Camera[] withFileCamera =
        [
            Camera("front-door"),
            new Camera
            {
                Id = "file-cam",
                Name = "File camera",
                Streams =
                [
                    new CameraStream
                    {
                        Name = "loop",
                        Url = "/media/samples/loop.mp4",
                        Roles = [StreamRole.Live],
                    },
                ],
            },
        ];

        Assert.Equal(
            GoogleHomeSyncWorker.Signature([Camera("front-door")]),
            GoogleHomeSyncWorker.Signature(withFileCamera));
    }

    /// <summary>
    /// Two different device sets must not flatten to the same string. A printable delimiter would
    /// let a camera named with it forge the boundary; the separators used are control characters
    /// that cannot appear in an id, a name, or a location.
    /// </summary>
    [Fact]
    public void Fields_cannot_be_confused_across_a_delimiter()
    {
        Assert.NotEqual(
            GoogleHomeSyncWorker.Signature([Camera("a", "b", "c")]),
            GoogleHomeSyncWorker.Signature([Camera("a", "b|c", null)]));

        Assert.NotEqual(
            GoogleHomeSyncWorker.Signature([Camera("front", "Front"), Camera("door", "Door")]),
            GoogleHomeSyncWorker.Signature([Camera("frontdoor", "FrontDoor")]));
    }

    [Fact]
    public void An_empty_registry_has_a_signature_of_its_own() =>
        Assert.NotEqual(
            GoogleHomeSyncWorker.Signature([]), GoogleHomeSyncWorker.Signature([Camera("front-door")]));

    // ----------------------------------------------------- reporting state

    /// <summary>
    /// <b>Google's field names, nested three deep.</b> None of this is derivable from anything on
    /// this side, and getting the nesting subtly wrong produces a 200 that reports nothing — so the
    /// only way to know it is right is to serialize the real thing and read it back.
    /// </summary>
    [Fact]
    public void The_report_state_body_uses_the_shape_HomeGraph_reads()
    {
        object body = HomeGraphClient.ReportStateBody(
            "agent-1",
            "req-1",
            new Dictionary<string, CameraState>(StringComparer.Ordinal)
            {
                ["front-door"] = new(Online: true, On: true),
                ["garage"] = new(Online: false, On: true),
            });

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(body));
        JsonElement root = json.RootElement;

        Assert.Equal("req-1", root.GetProperty("requestId").GetString());
        Assert.Equal("agent-1", root.GetProperty("agentUserId").GetString());

        JsonElement states = root
            .GetProperty("payload").GetProperty("devices").GetProperty("states");

        Assert.True(states.GetProperty("front-door").GetProperty("online").GetBoolean());
        Assert.False(states.GetProperty("garage").GetProperty("online").GetBoolean());

        // `on` rides alongside and is independent of it — a camera switched off in the Home app is
        // still reachable, which is what keeps its control usable.
        Assert.True(states.GetProperty("front-door").GetProperty("on").GetBoolean());
    }

    /// <summary>
    /// The pushed state must be decided by the same rule QUERY answers with. Google's Test Suite
    /// compares the two, and beyond that a device that reports one thing and answers another is a
    /// contradiction nobody can debug from either side alone.
    /// </summary>
    [Fact]
    public void Reported_state_agrees_with_what_query_would_answer()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch.AddHours(5);

        Snapshot? Latest(string id) => id switch
        {
            "fresh" => new Snapshot("fresh", [1], now.AddSeconds(-2)),
            "stale" => new Snapshot("stale", [1], now.AddMinutes(-5)),
            _ => null,
        };

        Dictionary<string, CameraState> reported = GoogleHomeStateWorker.States(
            [Camera("fresh"), Camera("stale"), Camera("never")], Latest, new HashSet<string>(), now);

        Assert.True(reported["fresh"].Online);
        Assert.False(reported["stale"].Online);

        // Never measured is reported online, for the reason CameraDeviceMapper.IsOnline gives.
        Assert.True(reported["never"].Online);

        foreach ((string id, CameraState state) in reported)
        {
            Assert.Equal(CameraDeviceMapper.IsOnline(Latest(id), now), state.Online);
        }
    }

    /// <summary>Only cameras Google was told about are reported — the same eligible set as SYNC.</summary>
    [Fact]
    public void An_ineligible_camera_is_not_reported()
    {
        Camera file = Camera("testcam");
        file.Streams[0].Url = "/media/loop.mp4";

        Assert.DoesNotContain(
            "testcam",
            GoogleHomeStateWorker.States(
                [file], _ => null, new HashSet<string>(), DateTimeOffset.UnixEpoch).Keys);
    }

    /// <summary>An unchanged tick spends nothing — the reason the worker keeps what it sent.</summary>
    [Fact]
    public void An_unchanged_state_set_is_not_reported_again()
    {
        var reported = new Dictionary<string, CameraState>(StringComparer.Ordinal)
        {
            ["a"] = new(Online: true, On: true),
        };

        Assert.False(GoogleHomeStateWorker.Changed(
            reported,
            new Dictionary<string, CameraState>(StringComparer.Ordinal)
            {
                ["a"] = new(Online: true, On: true),
            }));
    }

    /// <summary>
    /// A camera flipping, and equally one appearing or disappearing. A device newly in the set has
    /// never been reported at all, so "the values I already sent still match" is not the question.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_changed_state_set_is_reported(bool byMembership)
    {
        var reported = new Dictionary<string, CameraState>(StringComparer.Ordinal)
        {
            ["a"] = new(Online: true, On: true),
        };

        var current = byMembership
            ? new Dictionary<string, CameraState>(StringComparer.Ordinal)
            {
                ["a"] = new(Online: true, On: true),
                ["b"] = new(Online: true, On: true),
            }
            : new Dictionary<string, CameraState>(StringComparer.Ordinal)
            {
                ["a"] = new(Online: false, On: true),
            };

        Assert.True(GoogleHomeStateWorker.Changed(reported, current));
    }

    /// <summary>
    /// Nothing reported yet is a change, and it is the important one: until the first report lands,
    /// HomeGraph holds no state for these devices, which is what makes Google's Test Suite refuse
    /// to run against them at all.
    /// </summary>
    [Fact]
    public void A_first_report_counts_as_a_change() =>
        Assert.True(GoogleHomeStateWorker.Changed(
            new Dictionary<string, CameraState>(StringComparer.Ordinal),
            new Dictionary<string, CameraState>(StringComparer.Ordinal)
            {
                ["a"] = new(Online: true, On: true),
            }));
}
