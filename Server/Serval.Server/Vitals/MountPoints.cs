namespace Serval.Server.Vitals;

/// <summary>
/// Which mounted volume a path actually sits on.
///
/// <see cref="DriveInfo"/> cannot be constructed from an arbitrary path and be trusted: the media
/// root is <c>/media</c> in Docker (a real mount point, fine) but a bare relative <c>media</c>
/// under <c>dotnet run</c>, which resolves under the content root and is not a mount point at all.
/// Enumerating the drives and matching the longest one that contains the path is the only form
/// that is right in both cases — and it is pure, so the one bug it exists to prevent is pinned by
/// a test rather than discovered on a deployment.
/// </summary>
public static class MountPoints
{
    /// <summary>
    /// The longest mount name that <paramref name="fullPath"/> sits under, or null when none does.
    ///
    /// Matching is on whole path components, not on the string: <c>/mediafoo</c> is under
    /// <c>/</c> and emphatically not under <c>/media</c>, and a plain <c>StartsWith</c> gets that
    /// backwards. Longest wins rather than first, since <c>/</c> contains everything and would
    /// otherwise always answer.
    /// </summary>
    public static string? Best(string fullPath, IReadOnlyList<string> mountNames)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return null;
        }

        string path = Normalise(fullPath);
        string? best = null;

        foreach (string mount in mountNames)
        {
            if (string.IsNullOrWhiteSpace(mount))
            {
                continue;
            }

            string candidate = Normalise(mount);

            if (!Contains(candidate, path))
            {
                continue;
            }

            if (best is null || candidate.Length > best.Length)
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>Trailing separators removed, so "/media/" and "/media" are one thing. "/" stays "/".</summary>
    private static string Normalise(string path)
    {
        string trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="mount"/> itself or lives beneath it.
    /// Both arguments are already normalised, so the only cases are equality and a
    /// separator-terminated prefix.
    /// </summary>
    private static bool Contains(string mount, string path)
    {
        if (path == mount)
        {
            return true;
        }

        // "/" is the one mount whose normalised form already ends in the separator.
        string prefix = mount == "/" ? "/" : mount + "/";
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }
}
