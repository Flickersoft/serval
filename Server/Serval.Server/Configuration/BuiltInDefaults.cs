using System.Collections;
using System.Globalization;
using System.Reflection;

namespace Serval.Server.Configuration;

/// <summary>
/// The values compiled into the option classes, addressed by configuration path.
///
/// <para>A property initialiser is never in <see cref="IConfiguration"/>, and binding does not put
/// it there: <c>Get&lt;ServerOptions&gt;()</c> constructs the object, runs the initialisers, and then
/// overwrites only the properties configuration has a key for. So a setting nothing configures has a
/// real value that no amount of asking configuration will produce, and the settings page — which
/// addresses settings by path — needs the class walked to reach it.</para>
///
/// <para>Deliberately not a configuration source. Layered under the real ones it would land in
/// <see cref="DeploymentConfiguration"/> too, and every setting would report itself as
/// <see cref="SettingSource.Deployment"/> — <em>set by this deployment</em> — for a value nobody
/// chose. Answering "what is the built-in" and answering "did anyone choose this" are different
/// questions, and merging the sources loses the second one.</para>
/// </summary>
public static class BuiltInDefaults
{
    /// <summary>
    /// Every scalar leaf of a freshly constructed <see cref="ServerOptions"/>, in the string form
    /// configuration would have carried it in, so it re-parses through the same path a configured
    /// value does.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> All = Walk();

    /// <summary>The built-in value for a configuration path, or null if nothing there sets one.</summary>
    public static string? For(string key) =>
        All.TryGetValue(key, out string? value) ? value : null;

    private static Dictionary<string, string> Walk()
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        Walk(new ServerOptions(), ServerOptions.SectionName, values);
        return values;
    }

    private static void Walk(object instance, string prefix, Dictionary<string, string> values)
    {
        foreach (PropertyInfo property in
            instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // What the binder writes to is what configuration can address. A computed property —
            // ResolvedVideoPassthroughCodecs, EffectiveClasses — is a reading of other settings
            // rather than a setting, and has no path of its own.
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            object? value = property.GetValue(instance);
            if (value is null)
            {
                continue;
            }

            string key = $"{prefix}:{property.Name}";

            if (Scalar(value) is { } text)
            {
                values[key] = text;
            }
            else if (value is not IEnumerable)
            {
                Walk(value, key, values);
            }

            // A list falls through both, on purpose. An empty writable list means "using the
            // built-in set" and is drawn from SettingDescriptor.Fallback as placeholder text —
            // filling a value in here would make it indistinguishable from someone having chosen
            // exactly those entries, and saving would pin it.
        }
    }

    private static string? Scalar(object value) => value switch
    {
        bool flag => flag ? "true" : "false",
        string text => text,
        Enum name => name.ToString(),
        IFormattable number when value.GetType().IsPrimitive || value is decimal =>
            number.ToString(null, CultureInfo.InvariantCulture),
        _ => null,
    };
}
