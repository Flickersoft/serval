using System.Buffers.Text;
using System.Security.Cryptography;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Serval.Server.Storage;

namespace Serval.Server.Push;

/// <summary>
/// This deployment's VAPID identity, generated once and kept forever.
///
/// <para><b>Not a setting.</b> It is a private key, so the settings catalogue — which is readable
/// by any admin and round-trips through a JSON API — is the wrong place for it, and there is
/// nothing here a person would want to choose. Generating it on first use is also the only way this
/// feature works out of the box, and an NVR that requires a key-generation step before it can
/// notify anybody is one that never gets configured.</para>
///
/// <para><b>Changing it invalidates every subscription.</b> A subscription is bound to the public
/// key it was created with, so a new key means every existing endpoint rejects every message — with
/// no error anybody sees, because a notification that does not arrive looks exactly like a quiet
/// night. That is why this is stored rather than derived per boot.</para>
///
/// <para><b>It is deliberately not in the configuration backup.</b> Carrying a private key into a
/// file that gets copied around is a poor trade for what it buys, because the App recovers from a
/// changed key on its own: it remembers which key it subscribed with and re-subscribes when the
/// server offers a different one. That path has to exist regardless — a browser profile restored
/// from a backup, or a deployment moved between machines, produces the same mismatch — so making
/// the key portable would be a second mechanism for a problem already solved.</para>
/// </summary>
public sealed class VapidKeyStore
{
    /// <summary>The single document's id. There is one identity per deployment, not one per user.</summary>
    private const string DocumentId = "vapid";

    private readonly MongoContext _context;
    private readonly ILogger<VapidKeyStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private VapidKeyPair? _cached;

    public VapidKeyStore(MongoContext context, ILogger<VapidKeyStore> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// The deployment's key pair, generating and storing one on first call.
    ///
    /// Cached after the first read because it cannot change while the process runs — the only way
    /// to replace it is to delete the document, which is a deliberate act with consequences the
    /// class comment spells out.
    /// </summary>
    public async Task<VapidKeyPair> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is { } already)
        {
            return already;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _cached ??= await LoadOrCreateAsync(cancellationToken);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<VapidKeyPair> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        IMongoCollection<VapidKeyDocument> keys = _context.PushKeys;
        FilterDefinition<VapidKeyDocument> byId =
            Builders<VapidKeyDocument>.Filter.Eq(k => k.Id, DocumentId);

        if (await keys.Find(byId).FirstOrDefaultAsync(cancellationToken) is { } stored)
        {
            return VapidKeyPair.FromPkcs8(stored.PrivateKey);
        }

        using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var document = new VapidKeyDocument
        {
            Id = DocumentId,
            PrivateKey = Convert.ToBase64String(generated.ExportPkcs8PrivateKey()),
            PublicKey = Base64Url.EncodeToString(WebPushCrypto.UncompressedPublicKey(generated)),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            await keys.InsertOneAsync(document, options: null, cancellationToken);
            _logger.LogInformation(
                "Generated this deployment's VAPID key pair. Browsers subscribe against it, so "
                + "replacing it would silently unsubscribe every device.");
            return VapidKeyPair.FromPkcs8(document.PrivateKey);
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Another instance generated one between the read and the insert. Theirs wins — either
            // is fine, but they must not disagree, so the stored one is always the answer.
            VapidKeyDocument winner = await keys.Find(byId).FirstAsync(cancellationToken);
            return VapidKeyPair.FromPkcs8(winner.PrivateKey);
        }
    }
}

/// <summary>The stored form: a PKCS#8 private key, and the public half in the shape the wire wants.</summary>
[BsonIgnoreExtraElements]
public sealed class VapidKeyDocument
{
    [BsonId]
    public required string Id { get; set; }

    /// <summary>PKCS#8, base64. The private half — never leaves the server.</summary>
    public required string PrivateKey { get; set; }

    /// <summary>
    /// The uncompressed P-256 point, base64url. Stored alongside the private key though it could be
    /// derived from it, because this is the value the App asks for on every subscribe and deriving
    /// it per request is work with no purpose.
    /// </summary>
    public required string PublicKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A VAPID identity in usable form: the public key as the browser and the <c>k=</c> parameter want
/// it, and the means to sign with the private half.
/// </summary>
public sealed class VapidKeyPair
{
    private readonly byte[] _pkcs8;

    private VapidKeyPair(byte[] pkcs8, string publicKey)
    {
        _pkcs8 = pkcs8;
        PublicKey = publicKey;
    }

    /// <summary>Base64url, uncompressed. What the App passes as <c>applicationServerKey</c>.</summary>
    public string PublicKey { get; }

    public static VapidKeyPair FromPkcs8(string base64Pkcs8)
    {
        byte[] pkcs8 = Convert.FromBase64String(base64Pkcs8);

        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);

        return new VapidKeyPair(pkcs8, Base64Url.EncodeToString(WebPushCrypto.UncompressedPublicKey(key)));
    }

    /// <summary>
    /// A signing key for one use. <see cref="ECDsa"/> holds unmanaged state and is not safe to
    /// share across the threads sending to several subscriptions at once, so the key material is
    /// what is kept and the object is made per signature.
    /// </summary>
    public ECDsa CreateSigningKey()
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(_pkcs8, out _);
        return key;
    }
}
