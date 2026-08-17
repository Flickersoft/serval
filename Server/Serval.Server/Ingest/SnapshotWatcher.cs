using System.Globalization;
using Serval.Server.Snapshots;

namespace Serval.Server.Ingest;

/// <summary>
/// Reads the JPEGs ffmpeg writes for the detect stream and publishes each new frame.
///
/// Shared by whichever session owns the camera's detect stream — the recording session when one
/// stream does everything, or the standalone snapshot session when detection has a stream of its
/// own. Exactly one of them writes into <see cref="DirectoryName"/>, so there is never a second
/// writer to race.
///
/// <para>Frames are named for their position in the stream rather than overwritten in place, and the
/// timestamp that comes off that name is <see cref="FrameClock"/>'s business — see that type for why
/// the wall clock is the wrong answer.</para>
///
/// <para>These are the frames the dashboard wall, <c>/snapshot.jpg</c> and the vision model consume.
/// Object detection reads <see cref="DetectFrameReader"/>'s raw frames instead, which is why one
/// frame a second is still the right rate here however fast detection runs.</para>
/// </summary>
internal static class SnapshotWatcher
{
    /// <summary>Where the frames land, under the camera's own directory. A subdirectory so the
    /// camera directory stays flat — <c>DiskUsageScanner</c> enumerates it top-level only — and so
    /// no per-frame name has to be made safe for the media routes.</summary>
    public const string DirectoryName = "snapshots";

    private const string Prefix = "snap-";
    private const string Extension = ".jpg";

    /// <summary>
    /// Polls <paramref name="snapshotDir"/> and publishes every complete frame it finds, oldest
    /// first, deleting each one as it goes.
    ///
    /// Consume-and-delete is what bounds this: at one frame a second nothing else would ever prune
    /// them, and the retention sweep only knows about recorded segments. A frame that cannot be
    /// read is skipped and retried next tick; ffmpeg writes each file whole and then moves on, so a
    /// torn read is a file still being written and never one already published.
    /// </summary>
    /// <param name="sessionStart">What this session calls media offset zero.</param>
    public static async Task WatchAsync(
        string snapshotDir,
        string cameraId,
        double snapshotFps,
        DateTimeOffset sessionStart,
        SnapshotBroadcaster snapshots,
        CancellationToken cancellationToken)
    {
        double fps = Math.Max(snapshotFps, 0.1);
        var clock = new FrameClock(sessionStart, fps);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / fps));

        do
        {
            try
            {
                foreach ((string path, long index) in FrameFiles.Pending(snapshotDir, Prefix, Extension))
                {
                    byte[] bytes;
                    try
                    {
                        bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                    }
                    catch (IOException)
                    {
                        break; // still being written; it and everything after it can wait a tick
                    }

                    if (!IsCompleteJpeg(bytes))
                    {
                        break;
                    }

                    snapshots.Publish(new Snapshot(cameraId, bytes, clock.At(index)));
                    FrameFiles.Delete(path);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (DirectoryNotFoundException)
            {
                // ffmpeg has not written anything yet.
            }
        }
        while (await TaskWaits.SafeWaitAsync(timer, cancellationToken));
    }

    /// <summary>Empties the snapshot directory, for a session about to start writing into it.</summary>
    public static void Reset(string snapshotDir) =>
        FrameFiles.Reset(snapshotDir, Prefix, Extension);

    /// <summary>
    /// The ffmpeg output arguments that produce the snapshots, mapped from the given input.
    ///
    /// <paramref name="maxPixels"/> is a ceiling rather than a target, and it bounds area rather
    /// than a single axis — see <see cref="PixelBudget"/> for why the shape of the camera should
    /// not decide how much picture it gets. A source already inside the budget is left completely
    /// untouched, so a camera with a proper sub stream pays nothing for the filter while a 4K-only
    /// camera stops feeding 8 MP frames to the vision model and every dashboard tile.
    ///
    /// <c>-frame_pts 1</c> is what puts the frame's own position in its name. It rules out
    /// <c>-update 1</c>, which needs a fixed filename, and that is the trade: a file per frame,
    /// deleted as it is read, in exchange for a timestamp that means the frame rather than the
    /// read.
    /// </summary>
    public static IReadOnlyList<string> OutputArgs(string inputSpecifier, double fps, long maxPixels)
    {
        string rate = fps.ToString(CultureInfo.InvariantCulture);
        string filter = PixelBudget.ScaleFilter(maxPixels) is { } scale
            ? $"fps={rate},{scale}"
            : $"fps={rate}";

        return
        [
            "-map", inputSpecifier,
            "-vf", filter,
            "-frame_pts", "1",
            "-q:v", "5",
            "-y", Path.Combine(DirectoryName, $"{Prefix}%d{Extension}"),
        ];
    }

    private static bool IsCompleteJpeg(byte[] bytes) =>
        bytes.Length > 4
        && bytes[0] == 0xFF && bytes[1] == 0xD8       // SOI
        && bytes[^2] == 0xFF && bytes[^1] == 0xD9;    // EOI
}
