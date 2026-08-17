using Microsoft.Extensions.Options;
using Serval.Server.Configuration;

namespace Serval.Server.Alerts;

/// <summary>
/// Where an alert's preview clip and poster live, and the only place that decides.
///
/// Flat, and outside the per-camera directories, for the two reasons <see cref="Clips.ClipStorage"/>
/// is: <see cref="Vitals.DiskUsageScanner"/> refuses to recurse, so anything a level down measures
/// as zero bytes; and the recording sweep only ever deletes inside <c>Root/{cameraId}</c>, so
/// placement is what exempts these from rolling off with the footage they were cut from.
///
/// Unlike clips, that exemption is not the end of the story — <see cref="AlertRetentionWorker"/>
/// prunes here on its own schedule. Being outside the camera directory buys an alert a lifetime of
/// its own, not an unlimited one.
/// </summary>
public sealed class AlertStorage
{
    private readonly string _root;

    public AlertStorage(IOptions<ServerOptions> options)
    {
        MediaOptions media = options.Value.Media;
        _root = Path.Combine(media.Root, media.AlertsRoot);
    }

    /// <summary>The directory every alert file sits in — what the disk scan measures.</summary>
    public string Root => _root;

    public string VideoFor(string alertId) => Path.Combine(_root, $"{Safe(alertId)}.mp4");

    public string PosterFor(string alertId) => Path.Combine(_root, $"{Safe(alertId)}.jpg");

    public void EnsureRoot() => Directory.CreateDirectory(_root);

    /// <summary>
    /// Removes an alert's files. Silent when they are already gone — an alert whose clip was never
    /// cut is the ordinary case, not an error.
    /// </summary>
    public void Remove(string alertId)
    {
        foreach (string path in new[] { VideoFor(alertId), PosterFor(alertId) })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The row goes either way; the next sweep will find the file again.
            }
        }
    }

    /// <summary>
    /// An alert id reduced to what may appear in a filename.
    ///
    /// Alert ids are the detection's own GUID, so in practice this changes nothing. It is here
    /// because that id reaches this method from a URL route as well as from the database, and a
    /// path built from a route parameter is exactly the shape a traversal takes — the check belongs
    /// where the path is built rather than at each of the places that ask for one.
    /// </summary>
    private static string Safe(string alertId) =>
        string.Concat(alertId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
}
