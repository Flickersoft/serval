using System.Globalization;

namespace Serval.Server.Configuration;

/// <summary>Where a setting's current value came from.</summary>
public static class SettingSource
{
    /// <summary>Nothing sets it; this is the value built into the software.</summary>
    public const string Default = "default";

    /// <summary>An appsettings file or an environment variable sets it.</summary>
    public const string Deployment = "deployment";

    /// <summary>Someone changed it here, in the App. Only these can be reset.</summary>
    public const string User = "user";
}

/// <summary>One setting as the App receives it: what it is, what it holds, and where that came from.</summary>
/// <param name="Value">
/// The value in force. Typed to its <see cref="SettingKind"/> — a bool, a number, a string or an
/// array of strings — rather than stringly, so the App does not have to re-parse what the catalog
/// already knows.
/// </param>
/// <param name="Default">
/// What this would revert to if reset: the deployment's value with the user overlay taken out.
/// </param>
/// <param name="UnavailableChoices">
/// Which of <paramref name="Choices"/> this host and image cannot deliver, and why — see
/// <see cref="ISettingChoiceProbe"/>. Null for every setting nothing probes, so the App's default of
/// "all selectable" needs no special case.
/// </param>
/// <param name="Advanced">
/// Whether this belongs below the <c>ADVANCED</c> rule in its group rather than among the settings
/// somebody running a house actually reaches for. See <see cref="SettingDescriptor.Advanced"/> — it
/// is a statement about audience, not about risk, and it never hides anything.
/// </param>
/// <param name="AppliesWhen">
/// The dependency from the catalogue, passed through unevaluated: it is resolved against the value
/// being edited, which only the App knows. See <see cref="SettingDescriptor.AppliesWhen"/>.
/// </param>
public sealed record SettingView(
    string Key,
    string Group,
    string Label,
    string Help,
    string Kind,
    bool RestartRequired,
    object? Value,
    object? Default,
    string Source,
    double? Min = null,
    double? Max = null,
    string? Unit = null,
    IReadOnlyList<string>? Choices = null,
    IReadOnlyList<string>? Fallback = null,
    IReadOnlyDictionary<string, string>? UnavailableChoices = null,
    bool Advanced = false,
    SettingDependency? AppliesWhen = null);

/// <param name="RestartRequired">
/// True when a restart-gated setting has been changed since this process started. Not "this setting
/// is restart-gated" — that is per-setting — but "there is a change sitting here that the running
/// server is not using".
/// </param>
public sealed record SettingsResponse(
    IReadOnlyList<string> Groups,
    IReadOnlyList<SettingView> Settings,
    bool RestartRequired,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy);

/// <summary>
/// Turns the catalog plus the live configuration into what the settings page draws.
///
/// <para>Holds two configuration roots. The first is the real one, overlay included, and answers
/// "what is this server using". The second is built from the same sources <em>minus</em> the
/// overlay, and answers "what would this be if the user reset it" — which is the question a Reset
/// button has to be able to answer before it is pressed, and the reason the overlay is a separate
/// provider rather than something merged into the files on disk.</para>
/// </summary>
public sealed class SettingsReader
{
    private readonly IConfiguration _effective;
    private readonly IConfiguration _deployment;
    private readonly SettingsConfigurationProvider _overlay;
    private readonly IReadOnlyDictionary<string, ISettingChoiceProbe> _probes;

    /// <summary>
    /// What the restart-gated settings said when this process was composed. Captured at
    /// construction — this is a singleton built during startup — because "has anything changed
    /// since we booted" cannot be reconstructed later from a configuration that has already
    /// changed.
    /// </summary>
    private readonly Dictionary<string, string?> _atBoot;

    public SettingsReader(
        IConfiguration effective,
        DeploymentConfiguration deployment,
        SettingsConfigurationProvider overlay,
        IEnumerable<ISettingChoiceProbe>? probes = null)
    {
        _effective = effective;
        _deployment = deployment.Configuration;
        _overlay = overlay;
        _probes = (probes ?? []).ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);

        _atBoot = SettingsCatalog.All
            .Where(d => d.RestartRequired)
            .ToDictionary(d => d.Key, d => Raw(_effective, d), StringComparer.OrdinalIgnoreCase);
    }

    public SettingsResponse Read(ServerSettingsDocument document)
    {
        List<SettingView> settings = [.. SettingsCatalog.All.Select(View)];

        return new SettingsResponse(
            SettingsCatalog.Groups,
            settings,
            RestartRequired: _atBoot.Any(pair => Raw(_effective, SettingsCatalog.Find(pair.Key)!) != pair.Value),
            document.UpdatedAt == default ? null : document.UpdatedAt,
            document.UpdatedBy);
    }

    private SettingView View(SettingDescriptor descriptor) => new(
        descriptor.Key,
        descriptor.Group,
        descriptor.Label,
        descriptor.Help,
        descriptor.Kind.ToString().ToLowerInvariant(),
        descriptor.RestartRequired,
        Effective(_effective, descriptor),
        Effective(_deployment, descriptor),
        Source(descriptor),
        descriptor.Min,
        descriptor.Max,
        descriptor.Unit,
        descriptor.Choices,
        descriptor.Fallback,
        Unavailable(descriptor),
        descriptor.Advanced,
        descriptor.AppliesWhen);

    /// <summary>
    /// What the probe for this setting says it cannot do, or null when nothing probes it.
    ///
    /// <para>Null rather than an empty map so that "nobody asked" and "everything is available" stay
    /// distinguishable on the wire — the App draws them the same, but a probe that started failing
    /// should not be indistinguishable from a setting that never had one.</para>
    /// </summary>
    private IReadOnlyDictionary<string, string>? Unavailable(SettingDescriptor descriptor) =>
        _probes.TryGetValue(descriptor.Key, out ISettingChoiceProbe? probe)
            ? probe.Unavailable()
            : null;

    private string Source(SettingDescriptor descriptor)
    {
        if (IsOverridden(descriptor))
        {
            return SettingSource.User;
        }

        return Raw(_deployment, descriptor) is null
            ? SettingSource.Default
            : SettingSource.Deployment;
    }

    /// <summary>
    /// Whether the user overlay sets this. A list is stored as indexed children, so any one of them
    /// means the whole list is the user's — which is what makes Reset restore the entire list rather
    /// than a suffix of it.
    /// </summary>
    private bool IsOverridden(SettingDescriptor descriptor)
    {
        IReadOnlySet<string> keys = _overlay.OverriddenKeys;

        return descriptor.Kind == SettingKind.TextList
            ? keys.Any(k => k.StartsWith(descriptor.Key + ":", StringComparison.OrdinalIgnoreCase))
            : keys.Contains(descriptor.Key);
    }

    /// <summary>
    /// The value in force, which is the configured one where there is one and the value built into
    /// the option class where there is not.
    ///
    /// <para>The fallback is the whole point: a default written as a property initialiser is not
    /// configuration, so a setting nothing mentions has no value in any source to read. Without it
    /// the App draws an empty box for a setting the Server is using perfectly well, inviting someone
    /// to correct a blank that is really a working value they have never been shown.</para>
    ///
    /// <para>Lists never reach it. <see cref="Value"/> returns an empty list rather than null for
    /// one, and that emptiness carries meaning the App draws as
    /// <see cref="SettingDescriptor.Fallback"/> placeholder text.</para>
    /// </summary>
    private static object? Effective(IConfiguration configuration, SettingDescriptor descriptor) =>
        Value(configuration, descriptor) ?? Typed(BuiltInDefaults.For(descriptor.Key), descriptor);

    /// <summary>
    /// The value as the App should receive it, typed to the setting's kind. Null means the source
    /// says nothing about it, which for a list is not the same as an empty list — see
    /// <see cref="SettingDescriptor.Fallback"/>.
    /// </summary>
    private static object? Value(IConfiguration configuration, SettingDescriptor descriptor)
    {
        if (descriptor.Kind == SettingKind.TextList)
        {
            List<string> items =
            [
                .. configuration.GetSection(descriptor.Key).GetChildren()
                    .OrderBy(c => int.TryParse(c.Key, out int index) ? index : int.MaxValue)
                    .Select(c => c.Value)
                    .OfType<string>(),
            ];

            return items;
        }

        return Typed(configuration[descriptor.Key], descriptor);
    }

    /// <summary>
    /// One raw string as the setting's kind, so a value read from configuration and one read from
    /// the option classes arrive at the App in the same shape.
    /// </summary>
    private static object? Typed(string? raw, SettingDescriptor descriptor)
    {
        if (raw is null)
        {
            return null;
        }

        return descriptor.Kind switch
        {
            SettingKind.Bool => bool.TryParse(raw, out bool flag) ? flag : null,
            SettingKind.Int => long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole)
                ? whole
                : null,
            SettingKind.Number => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                ? number
                : null,
            _ => raw,
        };
    }

    /// <summary>
    /// A comparable string form, and null when the source says nothing about this setting at all.
    ///
    /// <para>The null matters as much as the comparison: <see cref="Source"/> reads it as "nothing
    /// sets this, so the value is the one built in". An empty list has to come back null rather
    /// than as the empty string a join produces, or every list nobody has touched would report
    /// itself as set by the deployment — which reads as "someone chose this" for a setting quietly
    /// running on its built-in set.</para>
    /// </summary>
    private static string? Raw(IConfiguration configuration, SettingDescriptor descriptor)
    {
        if (descriptor.Kind != SettingKind.TextList)
        {
            return configuration[descriptor.Key];
        }

        var items = (List<string>)Value(configuration, descriptor)!;
        return items.Count == 0 ? null : string.Join('\u001f', items);
    }
}

/// <summary>
/// The configuration this server would have without the user's overlay — the same sources in the
/// same order, minus the one the settings page writes to.
///
/// <para>A wrapper type rather than a bare <see cref="IConfiguration"/> so it can be injected
/// without colliding with the real one, which is already registered by the host.</para>
/// </summary>
public sealed class DeploymentConfiguration
{
    public DeploymentConfiguration(IConfiguration configuration) => Configuration = configuration;

    public IConfiguration Configuration { get; }
}
