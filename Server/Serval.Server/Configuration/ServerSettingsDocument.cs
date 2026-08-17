using MongoDB.Bson.Serialization.Attributes;

namespace Serval.Server.Configuration;

/// <summary>
/// The stored settings overlay — the keys a user has changed through the UI, on top of whatever
/// appsettings and the environment already say.
///
/// <para>One document, always <see cref="DocumentId"/>. There is exactly one server's worth of
/// settings and no notion of a profile or a scope, so a collection with a variable number of rows
/// would be inventing a dimension nothing uses.</para>
/// </summary>
[BsonIgnoreExtraElements]
public sealed class ServerSettingsDocument
{
    /// <summary>The only id this collection ever holds.</summary>
    public const string DocumentId = "server";

    [BsonId]
    public string Id { get; set; } = DocumentId;

    /// <summary>
    /// Configuration paths to their values, in the colon form the binder uses
    /// (<c>Serval:Media:RetentionDays</c>), values as strings.
    ///
    /// <para><b>A flat map rather than a nested typed document, deliberately.</b> It is exactly what
    /// an <see cref="Microsoft.Extensions.Configuration.IConfigurationProvider"/> consumes, so no
    /// translation layer exists to drift; it is sparse by construction, which is what makes "reset
    /// this one field to the deployment default" a delete rather than a sentinel value that every
    /// reader would have to know about; and it needs no class map and no schema evolution as
    /// <see cref="ServerOptions"/> grows. Type safety comes from validating against
    /// <see cref="SettingsCatalog"/> on the way in, not from the shape of this document.</para>
    ///
    /// <para>The colon separator is also what keeps these legal BSON field names — a dotted form
    /// would collide with Mongo's own path syntax on every update.</para>
    /// </summary>
    public Dictionary<string, string> Values { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Who last wrote this, for the audit question "who turned detection off".</summary>
    public string? UpdatedBy { get; set; }
}
