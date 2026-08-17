using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serval.Server.Push;

namespace Serval.Server.Tests;

/// <summary>
/// What a push service checks before it accepts a message from this server, checked here so the
/// answer is not "it returns 401 and nobody knows why".
///
/// <para>There is no published vector for VAPID the way there is for the payload encryption, since
/// every token embeds a timestamp. So these verify the signature against the public key the same
/// way a push service does, and pin the three claim details that are wrong most often.</para>
/// </summary>
public class VapidSignerTests
{
    private static readonly Uri Endpoint =
        new("https://fcm.googleapis.com/fcm/send/abcdef:0123456789");

    private const string Contact = "mailto:serval@example.com";

    [Fact]
    public void SignsATokenThePublicKeyVerifies()
    {
        (VapidKeyPair keys, ECDsa verifier) = NewIdentity();
        using (verifier)
        {
            string header = new VapidSigner().AuthorizationHeader(keys, Endpoint, Contact);
            string token = TokenFrom(header);

            string[] parts = token.Split('.');
            Assert.Equal(3, parts.Length);

            byte[] signature = Base64Url.DecodeFromChars(parts[2]);

            // 64 bytes, not 70-something: a DER signature is the classic way to produce a token
            // that looks right, verifies with a permissive library, and is rejected by every push
            // service. Its length is the tell.
            Assert.Equal(64, signature.Length);

            Assert.True(verifier.VerifyData(
                Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
    }

    [Fact]
    public void DeclaresEs256()
    {
        (VapidKeyPair keys, ECDsa verifier) = NewIdentity();
        using (verifier)
        {
            string token = TokenFrom(new VapidSigner().AuthorizationHeader(keys, Endpoint, Contact));
            JsonElement header = Decode(token.Split('.')[0]);

            Assert.Equal("JWT", header.GetProperty("typ").GetString());
            Assert.Equal("ES256", header.GetProperty("alg").GetString());
        }
    }

    /// <summary>
    /// The audience is the push service's origin and nothing more. Including the path — which is
    /// the subscription-specific part — is the other common way to build a token every service
    /// rejects.
    /// </summary>
    [Fact]
    public void AddressesTheOriginRatherThanTheSubscription()
    {
        (VapidKeyPair keys, ECDsa verifier) = NewIdentity();
        using (verifier)
        {
            string token = TokenFrom(new VapidSigner().AuthorizationHeader(keys, Endpoint, Contact));
            JsonElement claims = Decode(token.Split('.')[1]);

            Assert.Equal("https://fcm.googleapis.com", claims.GetProperty("aud").GetString());
            Assert.Equal(Contact, claims.GetProperty("sub").GetString());
        }
    }

    [Fact]
    public void ExpiresWithinTheDayTheRfcAllows()
    {
        (VapidKeyPair keys, ECDsa verifier) = NewIdentity();
        using (verifier)
        {
            string token = TokenFrom(new VapidSigner().AuthorizationHeader(keys, Endpoint, Contact));
            JsonElement claims = Decode(token.Split('.')[1]);

            var expires = DateTimeOffset.FromUnixTimeSeconds(claims.GetProperty("exp").GetInt64());

            Assert.True(expires > DateTimeOffset.UtcNow);
            Assert.True(expires < DateTimeOffset.UtcNow.AddHours(24));
        }
    }

    /// <summary>
    /// The header carries the public key alongside the token, and it must be the same key that
    /// signed it — a browser matches it against what it subscribed with.
    /// </summary>
    [Fact]
    public void CarriesThePublicKeyThatSigned()
    {
        (VapidKeyPair keys, ECDsa verifier) = NewIdentity();
        using (verifier)
        {
            string header = new VapidSigner().AuthorizationHeader(keys, Endpoint, Contact);

            Assert.StartsWith("vapid t=", header, StringComparison.Ordinal);
            Assert.Contains($", k={keys.PublicKey}", header, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Two endpoints on the same service share a token; a different service gets its own, because
    /// the audience is part of what is signed.
    /// </summary>
    [Fact]
    public void ReusesATokenPerPushServiceAndNotAcrossThem()
    {
        (VapidKeyPair keys, ECDsa verifier) = NewIdentity();
        using (verifier)
        {
            var signer = new VapidSigner();

            string first = signer.AuthorizationHeader(keys, Endpoint, Contact);
            string same = signer.AuthorizationHeader(
                keys, new Uri("https://fcm.googleapis.com/fcm/send/zzzz:9876"), Contact);
            string other = signer.AuthorizationHeader(
                keys, new Uri("https://updates.push.services.mozilla.com/wpush/v2/abc"), Contact);

            Assert.Equal(first, same);
            Assert.NotEqual(first, other);
        }
    }

    private static string TokenFrom(string header) =>
        header["vapid t=".Length..].Split(',')[0];

    private static JsonElement Decode(string segment) =>
        JsonDocument.Parse(Base64Url.DecodeFromChars(segment)).RootElement.Clone();

    /// <summary>
    /// A fresh identity plus a verifier holding only its public half — the position a push service
    /// is in, which is what makes verifying with it meaningful.
    /// </summary>
    private static (VapidKeyPair Keys, ECDsa Verifier) NewIdentity()
    {
        using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        VapidKeyPair keys =
            VapidKeyPair.FromPkcs8(Convert.ToBase64String(generated.ExportPkcs8PrivateKey()));

        byte[] point = Base64Url.DecodeFromChars(keys.PublicKey);
        var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..65] },
        });

        return (keys, verifier);
    }
}
