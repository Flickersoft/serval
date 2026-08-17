using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Serval.Server.Push;

/// <summary>
/// Encrypts a push payload the way a browser expects to receive it — RFC 8291 Message Encryption
/// for Web Push, which is RFC 8188's <c>aes128gcm</c> content coding with the key agreement RFC
/// 8291 layers on top.
///
/// <para><b>The push service cannot read any of this.</b> The keys come from the subscription the
/// browser handed us, so the payload is encrypted to that browser and Google or Mozilla relay
/// ciphertext they have no way to open. That is what makes it acceptable to put a camera's alert
/// text, and a token that can fetch its snapshot, through somebody else's infrastructure.</para>
///
/// <para>Hand-written against the RFC rather than taken from a package. Every primitive is in the
/// BCL — <see cref="ECDiffieHellman"/>, <see cref="HKDF"/>, <see cref="AesGcm"/> — and the
/// alternative was a dependency that has to be trusted with exactly the material this exists to
/// protect. <c>WebPushEncryptionTests</c> pins RFC 8291's own worked example, which is the only
/// reason to believe any of this: the failure mode of getting it subtly wrong is a push service
/// accepting the request and the browser silently discarding it.</para>
/// </summary>
public static class WebPushCrypto
{
    /// <summary>
    /// The header RFC 8188 puts in front of the ciphertext: 16-byte salt, 4-byte record size,
    /// 1-byte key id length, then the sender's 65-byte public key as the key id.
    /// </summary>
    private const int HeaderLength = 16 + 4 + 1 + 65;

    /// <summary>Tag length AES-GCM appends, and the one RFC 8188 mandates.</summary>
    private const int TagLength = 16;

    /// <summary>
    /// The record size every implementation uses, and the ceiling push services enforce on the
    /// whole body. It is written into the header rather than assumed by the reader.
    /// </summary>
    private const int RecordSize = 4096;

    /// <summary>
    /// The most plaintext that fits: a record, less the header that precedes it, the delimiter byte
    /// RFC 8188 requires on the last record, and the GCM tag. Callers are expected to stay well
    /// under it — see <see cref="AlertNotifier"/>, whose payload is a few hundred bytes — and to
    /// treat exceeding it as a bug rather than a runtime condition to handle.
    /// </summary>
    public const int MaxPayloadLength = RecordSize - HeaderLength - 1 - TagLength;

    private static readonly byte[] KeyInfoPrefix = Encoding.ASCII.GetBytes("WebPush: info\0");
    private static readonly byte[] ContentEncryptionKeyInfo =
        Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0");
    private static readonly byte[] NonceInfo =
        Encoding.ASCII.GetBytes("Content-Encoding: nonce\0");

    /// <summary>
    /// Encrypts <paramref name="payload"/> for one subscription, producing the exact bytes to put
    /// in the request body under <c>Content-Encoding: aes128gcm</c>.
    ///
    /// <paramref name="p256dh"/> and <paramref name="auth"/> are the two values the browser's
    /// <c>PushSubscription</c> exposes, base64url as it encodes them.
    /// </summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> payload, string p256dh, string auth)
    {
        byte[] userAgentPublicKey = Base64Url.DecodeFromChars(p256dh);
        byte[] authSecret = Base64Url.DecodeFromChars(auth);

        // A fresh key pair per message, which is what makes the salt and the derived key unique
        // per message without any state to keep. RFC 8291 calls this the application server key
        // pair; it is unrelated to the VAPID identity key, which signs rather than encrypts.
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        return Encrypt(
            payload, userAgentPublicKey, authSecret, ephemeral, RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>
    /// The deterministic form, for the RFC's test vector. Everything the caller would otherwise
    /// have to trust to a random number generator is supplied.
    ///
    /// Internal because a caller who reuses a salt and key pair across two messages has broken the
    /// encryption, and the only caller with a reason to want that is a test comparing against a
    /// published answer.
    /// </summary>
    internal static byte[] Encrypt(
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> userAgentPublicKey,
        ReadOnlySpan<byte> authSecret,
        ECDiffieHellman ephemeral,
        ReadOnlySpan<byte> salt)
    {
        if (payload.Length > MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                $"A push payload is at most {MaxPayloadLength} bytes; this one is {payload.Length}.");
        }

        if (userAgentPublicKey.Length != 65 || userAgentPublicKey[0] != 0x04)
        {
            throw new ArgumentException(
                "A subscription's p256dh must be a 65-byte uncompressed P-256 point.",
                nameof(userAgentPublicKey));
        }

        byte[] senderPublicKey = UncompressedPublicKey(ephemeral);

        // The shared secret is the raw x-coordinate of the agreed point. Not one of the Derive*
        // overloads that hash it — RFC 8291 feeds the coordinate itself into the first extract,
        // and a hashed agreement produces a plausible-looking key that no browser can match.
        using ECDiffieHellman recipient = ImportPublicKey(userAgentPublicKey);
        byte[] sharedSecret = ephemeral.DeriveRawSecretAgreement(recipient.PublicKey);

        // RFC 8291 §3.4. The auth secret salts the first extract, and the info string binds the
        // derived key to both parties' public keys so a swapped key yields a different key rather
        // than a working one.
        var pseudoRandomKey = new byte[32];
        HKDF.Extract(HashAlgorithmName.SHA256, sharedSecret, authSecret, pseudoRandomKey);
        byte[] keyInfo = [.. KeyInfoPrefix, .. userAgentPublicKey, .. senderPublicKey];
        byte[] inputKeyingMaterial =
            HKDF.Expand(HashAlgorithmName.SHA256, pseudoRandomKey, 32, keyInfo);

        // RFC 8188 §2.2, now with the per-message salt.
        var contentPseudoRandomKey = new byte[32];
        HKDF.Extract(
            HashAlgorithmName.SHA256, inputKeyingMaterial, salt, contentPseudoRandomKey);
        byte[] contentEncryptionKey =
            HKDF.Expand(HashAlgorithmName.SHA256, contentPseudoRandomKey, 16, ContentEncryptionKeyInfo);
        byte[] nonce =
            HKDF.Expand(HashAlgorithmName.SHA256, contentPseudoRandomKey, 12, NonceInfo);

        // 0x02 marks the last record. A single record always is the last one, and 0x01 here would
        // leave the browser waiting for a continuation that never comes.
        byte[] record = [.. payload, 0x02];

        var ciphertext = new byte[record.Length];
        var tag = new byte[TagLength];
        using (var aes = new AesGcm(contentEncryptionKey, TagLength))
        {
            aes.Encrypt(nonce, record, ciphertext, tag);
        }

        var body = new byte[HeaderLength + ciphertext.Length + tag.Length];
        Span<byte> cursor = body;

        salt.CopyTo(cursor[..16]);
        BinaryPrimitives.WriteUInt32BigEndian(cursor.Slice(16, 4), RecordSize);
        cursor[20] = (byte)senderPublicKey.Length;
        senderPublicKey.CopyTo(cursor[21..]);
        ciphertext.CopyTo(cursor[HeaderLength..]);
        tag.CopyTo(cursor[(HeaderLength + ciphertext.Length)..]);

        return body;
    }

    /// <summary>
    /// A P-256 public key as the 65-byte uncompressed point every Web Push field carries it as:
    /// <c>0x04</c>, then x, then y, each padded to the curve's 32 bytes.
    /// </summary>
    internal static byte[] UncompressedPublicKey(ECDiffieHellman key) =>
        Uncompressed(key.ExportParameters(includePrivateParameters: false).Q);

    /// <inheritdoc cref="UncompressedPublicKey(ECDiffieHellman)"/>
    internal static byte[] UncompressedPublicKey(ECDsa key) =>
        Uncompressed(key.ExportParameters(includePrivateParameters: false).Q);

    private static byte[] Uncompressed(ECPoint point) => [0x04, .. point.X!, .. point.Y!];

    private static ECDiffieHellman ImportPublicKey(ReadOnlySpan<byte> uncompressed) =>
        ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = uncompressed[1..33].ToArray(),
                Y = uncompressed[33..65].ToArray(),
            },
        });
}
