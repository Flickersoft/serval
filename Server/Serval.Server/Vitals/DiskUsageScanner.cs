using Serval.Server.Cameras;

namespace Serval.Server.Vitals;

/// <summary>
/// One directory the sweep measures.
///
/// Cameras are most of them, but not all: conversation audio and saved clips sit under the same
/// root and belong to no camera, and leaving either out would make the per-directory figures fail
/// to add up to the volume total for no visible reason. <see cref="Camera"/> is null for those, and
/// that is the only thing that distinguishes them — a target is a key, a label and a path.
/// </summary>
/// <param name="Key">What the entry is filed under. A camera id, or the directory's own name.</param>
/// <param name="Label">What a person reads. The camera's name, or what the directory holds.</param>
/// <param name="Directory">The absolute path to walk.</param>
/// <param name="Camera">The camera this belongs to, if any. Drives the retention and age columns,
/// which mean nothing for a directory no retention sweep touches.</param>
/// <param name="Note">What the directory is, for the rows where a byte count alone would mislead.</param>
public sealed record DiskScanTarget(
    string Key,
    string Label,
    string Directory,
    Camera? Camera = null,
    string? Note = null);

/// <summary>
/// How much of the media volume a directory is actually using, measured by walking it.
///
/// The cheaper alternative — a byte count on <c>RecordingSegment</c>, summed in Mongo — was
/// rejected, and the reason is the whole point of this feature. It would sum only what is
/// *indexed*, and <see cref="Recordings.RetentionWorker"/> deliberately leaves un-indexed files
/// alone ("files with no index entry are left for a later sweep rather than risk deleting a live
/// one"). Those orphans are exactly the files most likely to be quietly eating the pool, so a
/// measurement blind to them is blind to the failure it exists to catch. It would also miss every
/// <c>init-*.mp4</c>, the live playlist, and <c>snapshot.jpg</c>.
///
/// So this reports what the filesystem holds, not what the database believes. The cost is real —
/// a week of four-second segments is ~150,000 files per camera — which is why the caller walks one
/// camera per tick on its own slow cadence and never does this inside a request.
/// </summary>
public static class DiskUsageScanner
{
    /// <summary>
    /// Bytes and file count directly under <paramref name="directory"/>, not recursing.
    ///
    /// Flat on purpose: a camera's directory is flat by construction (ffmpeg writes segments,
    /// init files and a snapshot side by side), and refusing to recurse means a symlink or a
    /// stray mount underneath cannot turn a bounded walk into an unbounded one.
    ///
    /// A directory that does not exist is zero rather than an error — a camera registered a minute
    /// ago has not been written to yet, and that is not a fault worth a log line every sweep.
    /// </summary>
    public static DirectoryUsage Scan(string directory, CancellationToken cancellationToken)
    {
        long bytes = 0;
        int files = 0;

        var info = new DirectoryInfo(directory);
        if (!info.Exists)
        {
            return new DirectoryUsage(0, 0);
        }

        // Enumerate rather than materialise: GetFiles() on 150,000 entries allocates the whole
        // array before the first byte is counted.
        foreach (FileInfo file in info.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                bytes += file.Length;
                files++;
            }
            catch (FileNotFoundException)
            {
                // Retention deleted it between the enumeration and the stat. Nothing to report:
                // the file is gone, which is the state we were trying to measure anyway.
            }
            catch (IOException)
            {
                // Same race, different filesystem, different exception.
            }
        }

        return new DirectoryUsage(bytes, files);
    }
}

/// <summary>What one directory holds.</summary>
public readonly record struct DirectoryUsage(long Bytes, int FileCount);
