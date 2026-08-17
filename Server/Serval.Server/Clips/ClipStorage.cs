using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Serval.Server.Configuration;

namespace Serval.Server.Clips;

/// <summary>
/// Where a clip's files live, and the only place that decides.
///
/// Flat — <c>{id}.mp4</c> and <c>{id}.jpg</c> side by side — rather than a directory per clip, for
/// the same reason a camera's directory is flat: <see cref="Vitals.DiskUsageScanner"/> refuses to
/// recurse, so that a symlink or a stray mount underneath cannot turn a bounded walk into an
/// unbounded one. A clip stored one level down would measure as zero bytes, and the one kind of
/// footage that never rolls off would be the one kind missing from the storage figures.
/// </summary>
public sealed class ClipStorage
{
    private readonly string _root;

    public ClipStorage(IOptions<ServerOptions> options)
    {
        MediaOptions media = options.Value.Media;

        // Combined here rather than mutated into the options, so the configured value stays the
        // relative name it was written as. Path.Combine yields the absolute path unchanged if
        // ClipsRoot is one, which is how clips get pointed at a different volume.
        _root = Path.Combine(media.Root, media.ClipsRoot);
    }

    /// <summary>The directory every clip file sits in — what the disk scan measures.</summary>
    public string Root => _root;

    public string VideoFor(ObjectId id) => Path.Combine(_root, $"{id}.mp4");

    public string PosterFor(ObjectId id) => Path.Combine(_root, $"{id}.jpg");

    public void EnsureRoot() => Directory.CreateDirectory(_root);

    /// <summary>
    /// Removes a clip's files. Silent when they are already gone — deleting a clip whose files
    /// somebody removed by hand should still remove the row.
    /// </summary>
    public void Remove(ObjectId id)
    {
        foreach (string path in new[] { VideoFor(id), PosterFor(id) })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Bytes written to the clip's video so far, for the save dialog's progress.
    ///
    /// A live read of the file rather than a counter the writer updates: ffmpeg owns the write, so
    /// the file length is the only figure that is actually true. Zero before it starts.
    /// </summary>
    public long BytesWritten(ObjectId id)
    {
        var file = new FileInfo(VideoFor(id));
        return file.Exists ? file.Length : 0;
    }
}
