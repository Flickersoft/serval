using Serval.Contracts;

namespace Serval.Server.Clips;

/// <summary>
/// What a clip looks like on the wire.
///
/// Two shapes rather than one. The list draws cards — a picture, a name, a time, a size — and
/// fourteen of those must not carry fourteen frozen transcripts with them. The detail carries
/// everything, because by then it is one clip and the panel shows all of it.
/// </summary>
public static class ClipResponse
{
    public static object Summary(SavedClip clip) => new
    {
        id = clip.Id.ToString(),
        cameraId = clip.CameraId,
        cameraName = clip.CameraName,
        name = clip.Name,
        savedBy = clip.SavedBy,
        from = clip.From,
        to = clip.To,
        savedAt = clip.SavedAt,
        durationSeconds = clip.DurationSeconds,
        sizeBytes = clip.SizeBytes,
        state = clip.State.ToString().ToLowerInvariant(),
        summary = clip.Summary,
    };

    public static object Detail(SavedClip clip) => new
    {
        id = clip.Id.ToString(),
        cameraId = clip.CameraId,
        cameraName = clip.CameraName,
        name = clip.Name,
        savedBy = clip.SavedBy,
        from = clip.From,
        to = clip.To,
        savedAt = clip.SavedAt,
        durationSeconds = clip.DurationSeconds,
        sizeBytes = clip.SizeBytes,
        state = clip.State.ToString().ToLowerInvariant(),
        summary = clip.Summary,
        speech = ClipSpeech.Of(clip).Select(line => new
        {
            timestamp = line.Timestamp,
            offsetSeconds = line.OffsetSeconds,
            speaker = line.Speaker,
            text = line.Text,
        }),
        detections = clip.Documents.Detections.Select(d => new
        {
            id = d.Id,
            label = d.Label,
            timestamp = d.Timestamp,
            endedAt = d.EndedAt,
            offsetSeconds = Offset(clip, d.Timestamp),
            isAlert = d.IsAlert,
            peakConfidence = d.PeakConfidence,
        }),
        sounds = clip.Documents.Sounds.Select(s => new
        {
            id = s.Id,
            label = s.Label,
            timestamp = s.Timestamp,
            offsetSeconds = Offset(clip, s.Timestamp),
            isAlert = s.IsAlert,
        }),
        scenes = clip.Documents.Scenes.Select(s => new
        {
            id = s.Id,
            description = s.Description,
            timestamp = s.Timestamp,
            offsetSeconds = Offset(clip, s.Timestamp),
        }),
    };

    /// <summary>
    /// Seconds from the start of the clip — the only clock a person watching it has.
    ///
    /// Clamped at zero: a detection episode that opened before the clip is included because it was
    /// present during it, and a negative position would put it before the first frame.
    /// </summary>
    private static double Offset(SavedClip clip, DateTimeOffset at) =>
        Math.Max(0, (at - clip.From).TotalSeconds);
}
