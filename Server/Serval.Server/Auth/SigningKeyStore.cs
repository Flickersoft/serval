using System.Security.Cryptography;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Serval.Server.Configuration;

namespace Serval.Server.Auth;

/// <summary>
/// The token signing key for a deployment that was not given one: generated once, stored in
/// MongoDB, and the same on every boot after.
///
/// <para><b>Only consulted when <c>Serval:Auth:SigningKey</c> is unset.</b> A configured key always
/// wins and is validated exactly as before; this exists so a compose file with no <c>.env</c> at all
/// is a working deployment rather than one that refuses to start.</para>
///
/// <para><b>Holds its own <see cref="MongoClient"/>, like <see cref="ServerSettingsStore"/>.</b> The key
/// has to be in configuration before the JWT bearer options take their startup snapshot, which is
/// before the service provider — and so <c>MongoContext</c> — exists.</para>
///
/// <para><b>Stored in the database rather than in a file</b> because the database is the one
/// persistent, writable place every deployment has: the models mount is read-only on some hosts and
/// the media volume is swept by retention. Anyone who can read this document can already read every
/// account and camera credential in the same database, so it is no weaker than an environment
/// variable on the same host. It is deliberately left out of the configuration backup: a restore onto
/// a new machine signs everyone out once and keeps going, and a backup file holding a signing key is a
/// file that can mint Admin tokens.</para>
///
/// <para>Replacing it means deleting the document and restarting, which signs every device out.</para>
/// </summary>
public sealed class SigningKeyStore
{
    /// <summary>The document holding the generated key.</summary>
    public const string SigningKeyId = "jwt-signing-key";

    private readonly IMongoCollection<ServerSecretDocument> _secrets;

    public SigningKeyStore(MongoOptions mongo)
    {
        var client = new MongoClient(mongo.ConnectionString);
        _secrets = client.GetDatabase(mongo.Database)
            .GetCollection<ServerSecretDocument>(ServerSecretDocument.CollectionName);
    }

    /// <summary>
    /// The stored key, generating and storing one on first call. Synchronous because its one caller
    /// is top-level startup code that runs before the host exists. Throws when the database cannot be
    /// reached — with no key there is no server to run, and the container's restart policy retries.
    /// </summary>
    public string LoadOrCreate() => LoadOrCreateAsync().GetAwaiter().GetResult();

    public async Task<string> LoadOrCreateAsync(CancellationToken cancellationToken = default)
    {
        FilterDefinition<ServerSecretDocument> byId =
            Builders<ServerSecretDocument>.Filter.Eq(s => s.Id, SigningKeyId);

        if (await _secrets.Find(byId).FirstOrDefaultAsync(cancellationToken) is { } stored)
        {
            return stored.Value;
        }

        var document = new ServerSecretDocument
        {
            Id = SigningKeyId,
            Value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            await _secrets.InsertOneAsync(document, options: null, cancellationToken);
            return document.Value;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // Another instance generated one between the read and the insert. The stored one is the
            // answer, so two instances on one database never sign with different keys.
            ServerSecretDocument winner = await _secrets.Find(byId).FirstAsync(cancellationToken);
            return winner.Value;
        }
    }
}

/// <summary>
/// A secret the server generated for itself. One document per secret, keyed by what it is for.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class ServerSecretDocument
{
    public const string CollectionName = "server_secrets";

    [BsonId]
    public required string Id { get; set; }

    public required string Value { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
