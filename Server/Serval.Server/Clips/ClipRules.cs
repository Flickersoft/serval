using Serval.Server.Auth;
using Serval.Server.Cameras;
using Serval.Server.Media;
using Serval.Server.Recordings;

namespace Serval.Server.Clips;

/// <summary>
/// The decisions the clip routes make, separated from the plumbing that makes them.
///
/// Out here rather than inline in the endpoint lambdas because these are the parts that can be
/// wrong in ways a person would only find in production — a range one segment too long, a
/// permission check that reads the wrong claim — and out here they are decidable in a test with no
/// Mongo, no ffmpeg and no HTTP.
/// </summary>
public static class ClipRules
{
    /// <summary>Longest a clip name may be. Long enough for a sentence, which is what these are.</summary>
    public const int MaxNameLength = 200;

    /// <summary>
    /// Why this save cannot be accepted, or null if it can.
    ///
    /// Every reason is phrased for the person who pressed the button, because every one of them is
    /// something they can act on: shorten the range, pick a different one, name it.
    /// </summary>
    public static string? RejectSave(
        string? cameraId,
        string? name,
        DateTimeOffset from,
        DateTimeOffset to,
        int maxMinutes)
    {
        if (string.IsNullOrWhiteSpace(cameraId) || !CameraRepository.IsSafeId(cameraId))
        {
            return "A clip needs a camera.";
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return "A clip needs a name.";
        }

        if (name.Trim().Length > MaxNameLength)
        {
            return $"That name is too long — {MaxNameLength} characters at most.";
        }

        if (to <= from)
        {
            return "A clip has to end after it starts.";
        }

        var span = to - from;
        if (span > TimeSpan.FromMinutes(maxMinutes))
        {
            return $"That is {span.TotalMinutes:0.#} minutes. A clip can be at most {maxMinutes} minutes long.";
        }

        return null;
    }

    /// <summary>
    /// Why this range cannot become a clip, given what is actually on disk for it, or null if it can.
    ///
    /// Separate from <see cref="RejectSave"/> because it needs the segment index, and because these
    /// two failures are about the footage rather than about the request: nothing the caller types
    /// differently fixes them, only a different range does.
    /// </summary>
    public static string? RejectSegments(IReadOnlyList<RecordingSegment> segments)
    {
        if (segments.Count == 0)
        {
            return "Nothing was recorded in that range.";
        }

        // A run of segments sharing one fMP4 init is the most that can go in one playable file.
        // Refused here rather than discovered halfway through writing it, because a clip that plays
        // and then stops is worse than one that was never made.
        if (ClipExporter.LeadingRun(segments).Count < segments.Count)
        {
            return "Recording restarted partway through that range, so it cannot be saved as one clip. "
                + "Choose a range on one side of the gap.";
        }

        return null;
    }

    /// <summary>
    /// Whether this caller may rename or delete this clip.
    ///
    /// A shared library with owned edits: everyone sees every clip because the footage is the
    /// household's, but renaming and deleting are the two things nobody else can undo, so they stay
    /// with the person who saved it — and with an Admin, who has to be able to clear up after an
    /// account that is gone.
    /// </summary>
    public static bool MayEdit(string? userId, Role role, SavedClip clip) =>
        role == Role.Admin || string.Equals(userId, clip.SavedBy, StringComparison.Ordinal);

    /// <summary>
    /// What the file is called once it leaves Serval.
    ///
    /// The clip's own name, because that is what the person called it and what they will look for
    /// in a downloads folder. Falls back to camera and time when a name is nothing but characters a
    /// filesystem refuses.
    /// </summary>
    public static string FileNameFor(SavedClip clip)
    {
        char[] illegal = Path.GetInvalidFileNameChars();
        var safe = new string([.. clip.Name.Select(c => illegal.Contains(c) ? '-' : c)]).Trim(' ', '.', '-');

        return safe.Length == 0
            ? $"{clip.CameraId}-{clip.From:yyyyMMdd-HHmmss}.mp4"
            : $"{safe}.mp4";
    }
}
