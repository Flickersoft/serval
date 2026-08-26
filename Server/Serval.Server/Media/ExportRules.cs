namespace Serval.Server.Media;

/// <summary>
/// The decisions the export route makes, out here so they are decidable in a test with no Mongo,
/// no ffmpeg and no HTTP — the same reason <c>ClipRules</c> exists for saved clips.
///
/// Separate from those rules rather than shared with them because the two limits are not the same
/// kind of limit. A saved clip is a file kept until somebody deletes it, so its ceiling is disk. An
/// export is built as it is sent and kept by nobody, so its ceiling is patience.
/// </summary>
public static class ExportRules
{
    /// <summary>Why this range cannot be exported, or null if it can.</summary>
    public static string? RejectExport(DateTimeOffset from, DateTimeOffset to, int maxMinutes)
    {
        if (to <= from)
        {
            return "An export has to end after it starts.";
        }

        TimeSpan span = to - from;
        if (span > TimeSpan.FromMinutes(maxMinutes))
        {
            return $"That is {Describe(span)}. An export can cover at most {Describe(TimeSpan.FromMinutes(maxMinutes))}.";
        }

        return null;
    }

    /// <summary>
    /// A span in the units a person would say it in. Twelve hours reported as 720 minutes reads
    /// like a different limit than the one the settings screen offered.
    /// </summary>
    private static string Describe(TimeSpan span) =>
        span.TotalMinutes >= 60
            ? $"{span.TotalHours:0.#} hours"
            : $"{span.TotalMinutes:0.#} minutes";
}
