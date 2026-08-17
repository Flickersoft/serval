using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serval.Server.Push;

namespace Serval.Server.Tests;

/// <summary>
/// Pins <see cref="WebPushCrypto"/> against the worked example in RFC 8291 §5.
///
/// <para>This is the test the whole notification feature rests on, because the failure it catches
/// is otherwise invisible. Web Push encryption has no negotiation and no error channel: a payload
/// encrypted even slightly wrong is accepted by the push service, relayed, and then discarded by
/// the browser without a word to anyone. There is no log, no status code and nothing on screen —
/// the notification simply never appears. Every plausible mistake in that code (a hashed ECDH
/// agreement instead of the raw one, the two HKDF stages transposed, a DER signature, the wrong
/// last-record delimiter) produces exactly that same silence.</para>
///
/// <para>So the only way to know it works is to reproduce an answer somebody else published. The
/// keys and salt below are the RFC's, which is why they are hard-coded rather than generated —
/// determinism is the entire point, and <see cref="WebPushCrypto"/> exposes an internal overload
/// taking both purely so this test can supply them.</para>
/// </summary>
public class WebPushEncryptionTests
{
    // RFC 8291 §5, verbatim.
    private const string Plaintext = "When I grow up, I want to be a watermelon";
    private const string UserAgentPublicKey =
        "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string AuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";
    private const string SenderPrivateKey = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string SenderPublicKey =
        "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string ExpectedBody =
        "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYW"
        + "AmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSg"
        + "Sxsj_Qulcy4a-fN";

    [Fact]
    public void MatchesTheRfc8291Example()
    {
        using ECDiffieHellman sender = SenderKeyPair();

        byte[] body = WebPushCrypto.Encrypt(
            Encoding.UTF8.GetBytes(Plaintext),
            Base64Url.DecodeFromChars(UserAgentPublicKey),
            Base64Url.DecodeFromChars(AuthSecret),
            sender,
            Base64Url.DecodeFromChars(Salt));

        Assert.Equal(ExpectedBody, Base64Url.EncodeToString(body));
    }

    /// <summary>
    /// The header RFC 8188 puts in front of the ciphertext, checked field by field, so a failure
    /// says which part is wrong instead of only that a long string differs.
    /// </summary>
    [Fact]
    public void WritesTheContentCodingHeader()
    {
        using ECDiffieHellman sender = SenderKeyPair();

        byte[] body = WebPushCrypto.Encrypt(
            Encoding.UTF8.GetBytes(Plaintext),
            Base64Url.DecodeFromChars(UserAgentPublicKey),
            Base64Url.DecodeFromChars(AuthSecret),
            sender,
            Base64Url.DecodeFromChars(Salt));

        Assert.Equal(Base64Url.DecodeFromChars(Salt), body[..16]);

        // Record size, big-endian. 4096 is what the RFC's example uses.
        Assert.Equal(4096u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4)));

        // The key id is the sender's public key, and its length byte says so.
        Assert.Equal(65, body[20]);
        Assert.Equal(Base64Url.DecodeFromChars(SenderPublicKey), body[21..86]);
    }

    /// <summary>
    /// Two sends of the same text must not produce the same bytes. This is what the per-message key
    /// pair and salt are for, and losing it would be a real weakness that the vector above cannot
    /// see — the vector fixes both, precisely so it can be deterministic.
    /// </summary>
    [Fact]
    public void UsesFreshRandomnessPerMessage()
    {
        byte[] payload = Encoding.UTF8.GetBytes(Plaintext);

        byte[] first = WebPushCrypto.Encrypt(payload, UserAgentPublicKey, AuthSecret);
        byte[] second = WebPushCrypto.Encrypt(payload, UserAgentPublicKey, AuthSecret);

        Assert.NotEqual(first, second);

        // Same length though: identical plaintext, and the header is fixed width.
        Assert.Equal(first.Length, second.Length);
    }

    [Fact]
    public void RejectsAPayloadTooLargeToEncrypt()
    {
        var oversized = new byte[WebPushCrypto.MaxPayloadLength + 1];

        Assert.Throws<ArgumentOutOfRangeException>(
            () => WebPushCrypto.Encrypt(oversized, UserAgentPublicKey, AuthSecret));
    }

    [Fact]
    public void RejectsASubscriptionKeyThatIsNotAPoint()
    {
        // 65 bytes but not starting 0x04, which is the shape check that catches a client sending a
        // compressed point or a base64 mix-up rather than an uncompressed one.
        var wrong = new byte[65];
        wrong[0] = 0x02;

        using ECDiffieHellman sender = SenderKeyPair();

        Assert.Throws<ArgumentException>(() => WebPushCrypto.Encrypt(
            [1, 2, 3], wrong, Base64Url.DecodeFromChars(AuthSecret), sender, new byte[16]));
    }

    /// <summary>
    /// The notifier's real payload has to fit, with room to spare. Alert ids and camera ids are
    /// bounded in practice but not by anything structural, so this is the check that a title and a
    /// stream token together stay far from the ceiling.
    /// </summary>
    [Fact]
    public void TheNotificationPayloadFitsComfortably()
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = new string('a', 64),
            camera_id = new string('c', 64),
            title = "Person at Front door",
            body = "18:42:07",
            image = "/api/cameras/front-door/snapshot.jpg?stream_token=" + new string('t', 800),
            url = "/alerts/" + new string('a', 64),
            at = DateTimeOffset.UtcNow,
        });

        Assert.True(
            payload.Length < WebPushCrypto.MaxPayloadLength / 2,
            $"A notification payload of {payload.Length} bytes is more than half the "
            + $"{WebPushCrypto.MaxPayloadLength}-byte ceiling.");
    }

    private static ECDiffieHellman SenderKeyPair()
    {
        byte[] publicKey = Base64Url.DecodeFromChars(SenderPublicKey);

        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Base64Url.DecodeFromChars(SenderPrivateKey),
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] },
        });
    }
}
