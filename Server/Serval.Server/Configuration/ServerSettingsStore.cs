using MongoDB.Driver;

namespace Serval.Server.Configuration;

/// <summary>
/// Reads and writes the settings overlay document.
///
/// <para><b>Holds its own <see cref="MongoClient"/> rather than taking
/// <c>MongoContext</c>.</b> This runs before the service provider exists — the overlay has to be in
/// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> before <c>ServerOptions</c> is
/// bound, and <c>MongoContext</c> is constructed from that binding. Taking the connection details
/// directly from the bootstrap configuration is what breaks the cycle. The cost is one extra
/// connection pool that reads a single document at startup and writes one on each save.</para>
/// </summary>
public sealed class ServerSettingsStore
{
    private readonly IMongoCollection<ServerSettingsDocument> _settings;

    public ServerSettingsStore(MongoOptions mongo)
    {
        var client = new MongoClient(mongo.ConnectionString);
        _settings = client.GetDatabase(mongo.Database)
            .GetCollection<ServerSettingsDocument>("settings");
    }

    /// <summary>
    /// The stored overlay, or an empty one when nothing has ever been saved.
    /// </summary>
    public async Task<ServerSettingsDocument> LoadAsync(CancellationToken cancellationToken = default) =>
        await _settings
            .Find(d => d.Id == ServerSettingsDocument.DocumentId)
            .FirstOrDefaultAsync(cancellationToken)
        ?? new ServerSettingsDocument();

    /// <summary>
    /// Synchronous load, for the one caller that has no choice: <c>IConfigurationProvider.Load</c>
    /// is a void on a synchronous interface, and it runs once during host construction.
    /// </summary>
    public ServerSettingsDocument Load() => LoadAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Applies a set of changes to the stored overlay and returns what it now holds.
    ///
    /// <para>A null value <b>removes</b> the key, which is how "use the deployment default" is
    /// expressed — the overlay is sparse, and an absent key is the only way to say a setting is not
    /// overridden. Storing an empty string instead would be a value, and would pin the setting to
    /// "".</para>
    ///
    /// <para>Read-modify-write of the whole document rather than <c>$set</c>/<c>$unset</c> on
    /// individual fields. There is one writer — an admin submitting a form — so contention is not
    /// the problem being solved, and one replace keeps the document's shape in one place.</para>
    /// </summary>
    public async Task<ServerSettingsDocument> SaveAsync(
        IReadOnlyDictionary<string, string?> changes,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ServerSettingsDocument document = await LoadAsync(cancellationToken);

        foreach ((string key, string? value) in changes)
        {
            if (value is null)
            {
                document.Values.Remove(key);
            }
            else
            {
                document.Values[key] = value;
            }
        }

        document.UpdatedAt = DateTimeOffset.UtcNow;
        document.UpdatedBy = updatedBy;

        await _settings.ReplaceOneAsync(
            d => d.Id == ServerSettingsDocument.DocumentId,
            document,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);

        return document;
    }
}
