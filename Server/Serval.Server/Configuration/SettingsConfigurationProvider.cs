namespace Serval.Server.Configuration;

/// <summary>
/// The stored settings overlay, presented to the configuration system as a provider.
///
/// <para><b>Why this goes through <see cref="IConfigurationProvider"/> at all.</b> The alternative —
/// a settings singleton read directly by each service — would be a second configuration system
/// running beside the first, with its own precedence rules, its own binding, and no relationship to
/// the <c>Serval__Foo__Bar</c> environment syntax the deployment already uses. As a provider it is
/// just one more source in the existing chain: every option class, the binder, the env override
/// convention and <c>ConfigurationBindingTests</c> keep working untouched, and the only thing that
/// changes is which value wins.</para>
///
/// <para><b>Added last, so it wins.</b> appsettings seeds a fresh install, the environment carries
/// the deployment's choices, and this carries what a user has since changed in the UI. A key absent
/// here is not "empty" — it is "not overridden", and the value falls through to whatever the
/// deployment said.</para>
///
/// <para><b>There is no Mongo change stream, no watch, and no polling.</b> This is a single server
/// and it is the process that writes the value, so <see cref="SaveAsync"/> writes the document and
/// then reloads in the same call. Mongo is durable storage for the next process start, not a
/// message bus between two halves of this one.</para>
/// </summary>
public sealed class SettingsConfigurationProvider : ConfigurationProvider
{
    private readonly ServerSettingsStore? _store;

    /// <summary>
    /// Set when the overlay could not be read at startup, so <c>Program.cs</c> can log it once it
    /// has a logger to log with.
    /// </summary>
    public string? LoadError { get; private set; }

    public SettingsConfigurationProvider(ServerSettingsStore store) => _store = store;

    /// <summary>
    /// A provider whose overlay is handed to it rather than read from anywhere.
    ///
    /// <para>For tests about what an overlay <em>means</em> — which settings report themselves as
    /// changed, what a reset would restore — as against how one is stored. Without it those tests
    /// would open a MongoDB connection to assert on a classification, and would then either depend
    /// on a database being up or spend the driver's server-selection timeout discovering it is
    /// not.</para>
    /// </summary>
    internal SettingsConfigurationProvider(IReadOnlyDictionary<string, string> overlay)
    {
        _store = null;
        Replace(overlay);
    }

    /// <summary>
    /// Reads the overlay into <see cref="ConfigurationProvider.Data"/>.
    ///
    /// <para><b>A database this cannot reach must not stop the server booting.</b> Losing the
    /// overlay degrades the server to its deployment configuration, which is a working appliance.
    /// Failing to start because a tuning value could not be read would trade that for a settings
    /// page. Matches the warn-and-run-degraded posture everywhere except the signing key, which has
    /// no degraded mode.</para>
    /// </summary>
    public override void Load()
    {
        // A provider handed its overlay directly has nothing to read, and re-reading would discard
        // what it was given — ConfigurationBuilder.Build() calls this.
        if (_store is null)
        {
            return;
        }

        try
        {
            Replace(_store.Load().Values);
            LoadError = null;
        }
        catch (Exception ex)
        {
            Replace(new Dictionary<string, string>());
            LoadError = ex.Message;
        }
    }

    /// <summary>
    /// Writes changes and republishes them to everything holding an
    /// <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}"/>.
    ///
    /// <para>The write and the reload are one operation on purpose. Two callers — "store it" and
    /// "apply it" — is a pair that can be got wrong in one direction only, and the failure is
    /// silent: the setting is saved, the UI shows it, and nothing in the running server is using
    /// it until the next restart.</para>
    /// </summary>
    public async Task<ServerSettingsDocument> SaveAsync(
        IReadOnlyDictionary<string, string?> changes,
        string? updatedBy,
        CancellationToken cancellationToken = default)
    {
        ServerSettingsDocument document =
            await _store!.SaveAsync(changes, updatedBy, cancellationToken);

        Replace(document.Values);

        // Raises the change token. IOptionsMonitor re-binds off it, so every .CurrentValue reader
        // sees the new value on its next read — no restart, no notification of our own to deliver.
        OnReload();

        return document;
    }

    /// <summary>The keys the overlay currently sets, for reporting which settings a user has changed.</summary>
    public IReadOnlySet<string> OverriddenKeys =>
        Data.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Swaps the published overlay wholesale. Does not raise the change token.</summary>
    private void Replace(IReadOnlyDictionary<string, string> overlay)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in overlay)
        {
            data[key] = value;
        }

        Data = data;
    }
}

/// <summary>
/// Registers <see cref="SettingsConfigurationProvider"/> with a configuration builder. Holds the
/// provider instance so the same object can be handed to DI — the endpoints write through the very
/// provider the configuration root is reading from, which is what makes a save take effect.
/// </summary>
public sealed class SettingsConfigurationSource : IConfigurationSource
{
    public SettingsConfigurationSource(ServerSettingsStore store) =>
        Provider = new SettingsConfigurationProvider(store);

    /// <summary>A source over a fixed overlay. See the matching provider constructor.</summary>
    internal SettingsConfigurationSource(IReadOnlyDictionary<string, string> overlay) =>
        Provider = new SettingsConfigurationProvider(overlay);

    public SettingsConfigurationProvider Provider { get; }

    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}
