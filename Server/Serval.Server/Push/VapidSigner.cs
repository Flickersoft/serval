using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Serval.Server.Push;

/// <summary>
/// Signs the request that identifies this server to a push service — RFC 8292, "Voluntary
/// Application Server Identification for Web Push".
///
/// <para>The push service is an open relay to anyone holding a subscription's endpoint URL. VAPID
/// is what stops that being a way to push into somebody's browser from anywhere: the browser
/// records the public key it subscribed with, and the service rejects a message signed by any
/// other. It also gives the operators of those services a contact address when a deployment
/// misbehaves, which is what <c>sub</c> is for.</para>
///
/// <para>Signing is a JWS by hand rather than through the JWT library the auth path uses. The
/// signature ES256 requires is the raw r‖s pair, and the default output of most signing helpers is
/// DER — a difference that produces a well-formed token every push service rejects, for a reason
/// nothing in the error says. <see cref="DSASignatureFormat.IeeeP1363FixedFieldConcatenation"/> is
/// the whole of the fix, and it is clearer stated once here than configured somewhere else.</para>
/// </summary>
public sealed class VapidSigner
{
    /// <summary>
    /// How long a signed header stays valid. RFC 8292 caps this at 24 hours; half that leaves room
    /// for a clock that disagrees with the push service's without approaching the ceiling.
    /// </summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    /// <summary>
    /// Re-sign this far before expiry, so a header is never handed out with almost no life left in
    /// it — the request it is attached to still has to travel.
    /// </summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(30);

    private static readonly byte[] Header =
        JsonSerializer.SerializeToUtf8Bytes(new { typ = "JWT", alg = "ES256" });

    private readonly ConcurrentDictionary<string, (string Header, DateTimeOffset Expires)> _cache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// The <c>Authorization</c> header value for one push endpoint.
    ///
    /// Cached per audience because a token is valid for hours and identical for every subscription
    /// on the same push service — one alert going to a household's five devices should not be five
    /// ECDSA signatures over the same bytes.
    /// </summary>
    public string AuthorizationHeader(VapidKeyPair keys, Uri endpoint, string contactUri)
    {
        string audience = endpoint.GetLeftPart(UriPartial.Authority);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (_cache.TryGetValue(audience, out var cached) && cached.Expires - RefreshMargin > now)
        {
            return cached.Header;
        }

        DateTimeOffset expires = now.Add(Lifetime);
        string token = Sign(keys, audience, contactUri, expires);
        string header = $"vapid t={token}, k={keys.PublicKey}";

        _cache[audience] = (header, expires);
        return header;
    }

    private static string Sign(
        VapidKeyPair keys, string audience, string contactUri, DateTimeOffset expires)
    {
        byte[] claims = JsonSerializer.SerializeToUtf8Bytes(new
        {
            aud = audience,
            exp = expires.ToUnixTimeSeconds(),
            sub = contactUri,
        });

        string signingInput =
            $"{Base64Url.EncodeToString(Header)}.{Base64Url.EncodeToString(claims)}";

        using ECDsa key = keys.CreateSigningKey();
        byte[] signature = key.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }
}
