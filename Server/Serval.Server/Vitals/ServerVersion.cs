using System.Reflection;

namespace Serval.Server.Vitals;

/// <summary>
/// Which build of Serval is running. This file is the JSON contract that
/// <c>GET /api/system/version</c> serves.
///
/// Both fields are read once, from the entry assembly's <c>AssemblyInformationalVersion</c>, which
/// the SDK writes as <c>0.1.7+abc1234…</c> — the version before the <c>+</c>, the commit after it.
/// Nothing is measured and nothing is configured: a server that could be told its own version by a
/// setting could be told the wrong one, and the point of this route is to be the answer that cannot
/// disagree with the binary serving it.
///
/// <see cref="Revision"/> is null on any build made outside the image workflow, because such a
/// build has no commit to name and <see cref="Version"/> then carries a <c>-dev</c> suffix instead.
/// The two go together by construction — see the version block in <c>Directory.Build.props</c>.
/// </summary>
public sealed record ServerVersion
{
    /// <summary>The release, as <c>major.minor.patch</c>, or <c>major.minor.0-dev</c> off-workflow.</summary>
    public required string Version { get; init; }

    /// <summary>The full commit this build was made from, or null when it was not built by the workflow.</summary>
    public string? Revision { get; init; }

    /// <summary>
    /// Reads the running assembly. Cached in a static because the answer cannot change while the
    /// process lives, and this route is polled.
    /// </summary>
    public static ServerVersion Current { get; } = Read();

    private static ServerVersion Read()
    {
        string informational = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

        // Split rather than parse: the value is whatever MSBuild wrote, and a build configured in
        // some way not anticipated here should still report *something* rather than throw on the
        // route that exists to say what it is.
        int plus = informational.IndexOf('+');

        return plus < 0
            ? new ServerVersion { Version = informational }
            : new ServerVersion
            {
                Version = informational[..plus],
                Revision = informational[(plus + 1)..],
            };
    }
}
