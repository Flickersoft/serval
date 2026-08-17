using System.Globalization;

namespace Serval.Server.Ingest;

/// <summary>
/// The conventions shared by every ffmpeg output that writes one file per frame: how a frame's
/// position is carried in its name, what order they come back in, and when they are removed.
///
/// Two outputs use this — the dashboard's JPEG snapshots and the raw frames detection runs on — and
/// they must agree, because both are dated on the same session's media clock and a consumer draws
/// one over the other. Anything either of them decided locally is a way for them to disagree.
/// </summary>
internal static class FrameFiles
{
    /// <summary>
    /// The frames waiting to be read, oldest first.
    ///
    /// Ordered numerically rather than by name, which is not the same thing: <c>10</c> sorts before
    /// <c>9</c> as text, and publishing frames out of order would hand the detection policy a clock
    /// that goes backwards.
    /// </summary>
    public static IEnumerable<(string Path, long Index)> Pending(
        string directory, string prefix, string extension)
    {
        List<(string Path, long Index)> found = [];

        foreach (string path in Directory.EnumerateFiles(directory, $"{prefix}*{extension}"))
        {
            if (IndexOf(Path.GetFileName(path), prefix, extension) is { } index)
            {
                found.Add((path, index));
            }
        }

        found.Sort((a, b) => a.Index.CompareTo(b.Index));
        return found;
    }

    /// <summary>The frame number in <c>{prefix}{index}{extension}</c>, or null if that is not what
    /// this is.</summary>
    public static long? IndexOf(string fileName, string prefix, string extension)
    {
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(extension, StringComparison.Ordinal))
        {
            return null;
        }

        string digits = fileName[prefix.Length..^extension.Length];

        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long index)
            ? index
            : null;
    }

    /// <summary>
    /// Empties a frame directory, for a session about to start writing into it.
    ///
    /// Frames left by a previous run are numbered from *its* timeline, so publishing them would
    /// date old pictures to this session's opening seconds.
    /// </summary>
    public static void Reset(string directory, string prefix, string extension)
    {
        Directory.CreateDirectory(directory);

        foreach (string path in Directory.EnumerateFiles(directory, $"{prefix}*{extension}"))
        {
            Delete(path);
        }
    }

    /// <summary>
    /// Removes a frame that has been read, or one being dropped to stay inside a backlog.
    ///
    /// Best-effort: a file that will not delete is a frame that will be published twice at worst,
    /// which the detection policy treats as a repeat sighting.
    /// </summary>
    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next Reset.
        }
    }

}

/// <summary>
/// Turns a frame's index into the instant it was captured, on the session's own media clock.
///
/// <para>Every detection episode is dated from a frame's timestamp, and reading it off the wall
/// clock times the moment *the file was got round to* — camera buffering, RTSP, ffmpeg's filter and
/// encode, the write, and a poll period all land inside the number. On a real camera that is ten
/// seconds, long enough for someone to walk out of shot before the box describing them appears over
/// the footage. Deriving it from the index makes it immune to how late the reader is.</para>
///
/// <para>The first frame's index anchors the run rather than zero. ffmpeg numbers from the start of
/// its own timeline, which is not always where the media begins — a source whose timestamps start
/// late has the <c>fps</c> filter counting from the gap. Anchoring on what actually arrived keeps the
/// spacing exact and puts the first frame at the session start, which is precisely the convention the
/// recorder's first segment follows.</para>
///
/// <para>Not absolutely true, and deliberately so: this takes media offset zero to be the session's
/// start, and is early by however long ffmpeg spent connecting. <see cref="Recordings.SessionSegments"/>
/// makes the same assumption from the same anchor. Sharing that error is the point — it cancels when
/// one ffmpeg serves both roles, and what a box drawn on footage needs is that the two agree, not
/// that either is right.</para>
/// </summary>
internal sealed class FrameClock(DateTimeOffset sessionStart, double fps)
{
    private readonly double _fps = Math.Max(fps, 0.01);
    private long? _firstIndex;

    public DateTimeOffset At(long index)
    {
        _firstIndex ??= index;
        return sessionStart.AddSeconds((index - _firstIndex.Value) / _fps);
    }
}
