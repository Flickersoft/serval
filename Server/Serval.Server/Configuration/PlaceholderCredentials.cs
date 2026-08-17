namespace Serval.Server.Configuration;

/// <summary>
/// The credential values that ship in this repository's compose files as things to replace.
///
/// They exist so an operator can grep <c>CHANGE-ME</c> and find every secret that needs one. The
/// cost of that convenience is that a published repository publishes them: a signing key anyone can
/// read is a signing key anyone can mint an Admin token with, without ever reaching the login route
/// the rate limiter and the lockout guard. So the values are refused at startup rather than
/// documented as "for dev only" — a comment cannot stop a compose file being copied to a NAS.
/// </summary>
public static class PlaceholderCredentials
{
    /// <summary>
    /// Matched case-insensitively and after trimming, since a value pasted from documentation
    /// arrives with whatever whitespace and casing came with it.
    /// </summary>
    private static readonly string[] Values =
    [
        "CHANGE-ME",
        "CHANGEME",
        "CHANGE_ME",
        "password",
        "admin",
    ];

    /// <summary>True when <paramref name="value"/> is a placeholder that was never replaced.</summary>
    public static bool IsPlaceholder(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Values.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Throws when <paramref name="value"/> is still a placeholder. <paramref name="key"/> is the
    /// full configuration key so the message names the line to edit, and
    /// <paramref name="howToGenerate"/> is the command that produces an acceptable one — an error
    /// that only says "no" leaves the operator to search for how.
    /// </summary>
    public static void ThrowIfPlaceholder(string? value, string key, string howToGenerate)
    {
        if (IsPlaceholder(value))
        {
            throw new InvalidOperationException(
                $"{key} is still the placeholder '{value!.Trim()}'. This repository is public, so "
                + $"that value is public too — anyone could use it against this server. {howToGenerate}");
        }
    }
}
