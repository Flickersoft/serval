using System.Globalization;
using Serval.Server.Configuration;
using Serval.Server.Snapshots;

namespace Serval.Server.Ingest;

/// <summary>
/// Reads the raw frames ffmpeg writes for object detection and publishes each new one.
///
/// The raw-frame sibling of <see cref="SnapshotWatcher"/>, deliberately built on the same
/// <see cref="FrameFiles"/> conventions: both are dated from the same session anchor, and a box found
/// in one is drawn over footage timed by the other. Where they differ is what the frame is for. A
/// snapshot is an encoded picture for the dashboard and the vision model, at one a second. These are
/// unencoded planes for a detector, as fast as <c>Ingest:DetectFps</c> asks.
///
/// <para><b>Files rather than a pipe.</b> <c>-f rawvideo pipe:1</c> is a trap: ffmpeg's output loop
/// is effectively single-threaded across outputs and a Linux pipe holds 64 KiB against a frame of
/// well over a megabyte, so a reader stalling for even one frame time blocks ffmpeg's write — and
/// this output rides on the process *recording* the camera. A burst of database writes delaying this
/// loop would be enough. Writing to a file never blocks on a slow consumer; it costs a poll period
/// instead, which <see cref="PollPeriod"/> keeps small.</para>
///
/// <para><b>Why yuv420p.</b> The Y plane is exactly the 8-bit grayscale buffer motion detection
/// wants, so that half of the pipeline reads it with no conversion at all, and it is two thirds the
/// size of RGB on the way through. Only the crops that actually reach a detector are converted.</para>
/// </summary>
internal static class DetectFrameReader
{
    private const string Prefix = "frame-";
    private const string Extension = ".yuv";

    /// <summary>This camera's staging directory under the configured root. Per camera so one
    /// reader's <see cref="FrameFiles.Reset"/> cannot discard another's frames.</summary>
    public static string DirectoryFor(string root, string cameraId) => Path.Combine(root, cameraId);

    /// <summary>
    /// Says so, loudly and once, if the frame directory is not in memory.
    ///
    /// Worth checking at startup rather than leaving to be discovered: nothing about a disk-backed
    /// path fails, it simply writes tens of megabytes a second of frames that are deleted moments
    /// later to whatever device it landed on — most likely the one holding the recordings. The
    /// symptom is drive wear and a puzzling I/O load, months later, with nothing in the log.
    ///
    /// A warning rather than a refusal to start, because this is the sort of thing that is wrong on
    /// a development box and fine in production, and losing recording over it would be the worse
    /// failure.
    /// </summary>
    public static void WarnIfNotInMemory(IngestOptions options, ILogger logger)
    {
        if (options.DetectFps <= 0)
        {
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(options.DetectFrameDir);

            if (MountFor(options.DetectFrameDir) is not { } mount)
            {
                return;
            }

            if (!string.Equals(mount.DriveFormat, "tmpfs", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mount.DriveFormat, "ramfs", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Serval:Ingest:DetectFrameDir is '{Path}', which is on {Mount} — a {Format} "
                    + "filesystem rather than tmpfs. Detection frames are written and deleted within "
                    + "milliseconds, so this puts continuous churn on a real device for no benefit. "
                    + "Point it at /dev/shm, or add a tmpfs mount for it.",
                    options.DetectFrameDir, mount.Name, mount.DriveFormat);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Could not prepare the detect frame directory '{Path}'.", options.DetectFrameDir);
        }
    }

    /// <summary>
    /// The mount the given path actually sits on: the deepest mount point that is a prefix of it.
    ///
    /// Not <see cref="Directory.GetDirectoryRoot"/>, which on Linux answers <c>/</c> for every
    /// absolute path — so a directory on a perfectly good tmpfs is reported as whatever the root
    /// filesystem happens to be. That produced a warning telling operators to move
    /// <c>/dev/shm/serval</c> off btrfs, which is advice about the wrong filesystem.
    /// </summary>
    private static DriveInfo? MountFor(string path)
    {
        string full = Path.GetFullPath(path);

        return DriveInfo.GetDrives()
            .Where(drive => IsUnder(full, drive.Name))
            .OrderByDescending(drive => drive.Name.Length)
            .FirstOrDefault();

        static bool IsUnder(string path, string mount)
        {
            if (!path.StartsWith(mount, StringComparison.Ordinal))
            {
                return false;
            }

            // "/dev/shmmm" is not under "/dev/shm", but "/dev/shm/x" and "/dev/shm" both are.
            return mount.EndsWith(Path.DirectorySeparatorChar)
                || path.Length == mount.Length
                || path[mount.Length] == Path.DirectorySeparatorChar;
        }
    }

    /// <summary>
    /// How often the directory is swept, at a given frame rate.
    ///
    /// A quarter of the frame period rather than the whole of it, because the poll is the only
    /// latency this transport adds and a sweep of a small tmpfs directory costs microseconds — at ten
    /// cameras and 20 Hz the whole thing is a millisecond or two a second. The floor keeps a
    /// misconfigured high frame rate from turning this into a spin.
    /// </summary>
    public static TimeSpan PollPeriod(double fps) =>
        TimeSpan.FromSeconds(Math.Clamp(1.0 / Math.Max(fps, 0.1) / 4.0, 0.025, 10.0));

    /// <summary>
    /// Polls <paramref name="frameDir"/> and publishes every complete frame it finds, oldest first,
    /// deleting each one as it goes.
    ///
    /// A frame is complete when it is exactly <paramref name="width"/> × <paramref name="height"/> ×
    /// 3/2 bytes, which raw planar video always is — a shorter file is one ffmpeg is still writing.
    /// That is a cheaper and stricter check than the marker scan an encoded format needs.
    /// </summary>
    /// <param name="sessionStart">What this session calls media offset zero.</param>
    /// <param name="backlog">Frames allowed to pile up before the oldest are dropped unread.</param>
    public static async Task WatchAsync(
        string frameDir,
        string cameraId,
        int width,
        int height,
        double fps,
        int backlog,
        DateTimeOffset sessionStart,
        DetectFrameBroadcaster frames,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        int frameBytes = FrameBytes(width, height);
        var clock = new FrameClock(sessionStart, fps);
        using var timer = new PeriodicTimer(PollPeriod(fps));

        do
        {
            try
            {
                var pending = FrameFiles.Pending(frameDir, Prefix, Extension).ToList();

                // Anything past the backlog is dropped where it lies. Nothing upstream can be made
                // to wait — that is the point of writing files — so falling behind has to cost the
                // oldest frames rather than memory. Dropping before reading also means a reader that
                // is behind does not first pay to read what it is about to discard.
                int excess = pending.Count - Math.Max(backlog, 1);
                if (excess > 0)
                {
                    // Counted as well as logged: these are moments nothing looked at, and a server
                    // dropping them steadily is indistinguishable from one keeping up unless the
                    // number reaches the status page.
                    frames.Dropped(excess);

                    logger.LogDebug(
                        "Camera {CameraId}: dropping {Count} detect frame(s); the reader is behind.",
                        cameraId, excess);

                    foreach ((string stale, long staleIndex) in pending.Take(excess))
                    {
                        // Shown to the clock on the way out for the reason skipped frames are: the
                        // anchor is the first index it ever sees, and a burst dropped at startup
                        // would otherwise move it.
                        clock.At(staleIndex);
                        FrameFiles.Delete(stale);
                    }

                    pending.RemoveRange(0, excess);
                }

                // Nothing is looking at this camera right now — its AI session is stopped or
                // restarting, or the host has no detection model at all. The frames still have to be
                // cleared, but reading a megabyte apiece to throw them away would be the most
                // expensive way to do nothing.
                bool wanted = frames.HasSubscriber(cameraId);

                foreach ((string path, long index) in pending)
                {
                    // Dated even when discarded, which is not busywork: the clock anchors on the
                    // first index it is shown, so letting a skipped stretch go past unseen would
                    // move the anchor to whenever a subscriber happened to appear and date every
                    // frame after it early by however long detection took to start.
                    DateTimeOffset capturedAt = clock.At(index);

                    if (!wanted)
                    {
                        FrameFiles.Delete(path);
                        continue;
                    }

                    // Renting is not an optimisation here: a frame is well over a megabyte and these
                    // arrive several times a second per camera, which is a gen-2 allocation rate
                    // nothing else in the Server comes close to.
                    DetectFrame frame = DetectFrame.Rent(cameraId, width, height, capturedAt);
                    bool published = false;

                    try
                    {
                        if (!await TryReadAsync(path, frame, cancellationToken))
                        {
                            // Still being written; it and everything after it can wait a tick.
                            break;
                        }

                        // Ownership passes to the broadcaster, and from there to whoever reads it.
                        frames.Publish(frame);
                        published = true;
                    }
                    finally
                    {
                        if (!published)
                        {
                            frame.Return();
                        }
                    }

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
            catch (Exception ex)
            {
                // Per pass, so one bad pass costs one pass. This loop is the only thing deleting
                // what ffmpeg writes, and it runs as a side task whose exceptions FfmpegRunner
                // discards — a throw escaping here would leave the session writing frames into
                // tmpfs at DetectFps with nothing removing them and nothing in the log, until the
                // mount fills. Bounded by DetectFrameBacklog only while this keeps running.
                logger.LogWarning(
                    ex, "Camera {CameraId}: could not sweep detect frames this pass.", cameraId);
            }
        }
        while (await TaskWaits.SafeWaitAsync(timer, cancellationToken));
    }

    /// <summary>Bytes in one yuv420p frame: a full-resolution luma plane plus two half-resolution
    /// chroma planes.</summary>
    public static int FrameBytes(int width, int height) => width * height * 3 / 2;

    /// <summary>
    /// The exact pixel size detect frames are written at, from the source's own dimensions.
    ///
    /// Computed here rather than delegated to ffmpeg's <c>-2</c> aspect keeper, and the difference
    /// matters: this is the number the reader uses to tell a complete frame from one still being
    /// written, so ffmpeg is given explicit dimensions and cannot round differently. Both axes are
    /// forced even because yuv420p halves chroma in both, leaving an odd dimension with no legal
    /// representation.
    ///
    /// <paramref name="maxWidth"/> is a ceiling, not a target — a source already narrower than it is
    /// left alone rather than upscaled, which would cost bandwidth for detail that is not there.
    /// </summary>
    public static (int Width, int Height) FrameSize(int sourceWidth, int sourceHeight, int maxWidth)
    {
        int width = maxWidth > 0 ? Math.Min(maxWidth, sourceWidth) : sourceWidth;
        int height = (int)Math.Round(sourceHeight * (double)width / sourceWidth);

        return (Even(width), Even(height));

        static int Even(int value) => Math.Max(2, value - (value % 2));
    }

    /// <summary>Empties the camera's frame directory, for a session about to start writing into
    /// it.</summary>
    public static void Reset(string frameDir) => FrameFiles.Reset(frameDir, Prefix, Extension);

    /// <summary>
    /// What this camera's detect output should be, or null for no detect output at all.
    ///
    /// Null on two counts, and both are ordinary rather than faults. A non-positive
    /// <see cref="IngestOptions.DetectFps"/> turns the output off. A source that would not report its
    /// dimensions turns it off too: frames of unknown size cannot be told apart from frames still
    /// being written, and silently reading torn ones would be worse than not detecting. Recording is
    /// unaffected either way — a probe failure must never cost footage.
    /// </summary>
    public static DetectFramePlan? Plan(
        string cameraId, VideoProbe probe, IngestOptions options, ILogger logger)
    {
        if (options.DetectFps <= 0)
        {
            return null;
        }

        if (probe is not { Width: { } sourceWidth, Height: { } sourceHeight })
        {
            logger.LogWarning(
                "Camera {CameraId}: the source did not report its frame size, so object detection "
                + "has no frames to run on. Recording is unaffected.",
                cameraId);
            return null;
        }

        (int width, int height) = FrameSize(sourceWidth, sourceHeight, options.DetectFrameWidth);

        return new DetectFramePlan(
            DirectoryFor(options.DetectFrameDir, cameraId), options.DetectFps, width, height);
    }

    /// <summary>
    /// The ffmpeg output arguments that produce the raw frames.
    ///
    /// Dimensions are explicit rather than <c>-2</c>-derived, because <see cref="WatchAsync"/> tells a
    /// finished frame from a partial one by its exact length: ffmpeg rounding an aspect-derived
    /// height differently from <see cref="FrameSize"/> would mean every frame looked incomplete
    /// forever. <c>flags=area</c> averages the pixels it discards instead of point-sampling them,
    /// which is what stops a large downscale aliasing a distant subject out of existence before the
    /// detector ever sees it.
    ///
    /// <c>-frame_pts 1</c> puts the frame's own position in its name, which is what
    /// <see cref="FrameClock"/> dates it from. The path is absolute because
    /// <see cref="FfmpegRunner"/> runs each session in the camera's media directory, and these frames
    /// deliberately do not live there.
    /// </summary>
    public static IReadOnlyList<string> OutputArgs(
        string inputSpecifier, double fps, int width, int height, string frameDir)
    {
        string rate = fps.ToString(CultureInfo.InvariantCulture);
        string w = width.ToString(CultureInfo.InvariantCulture);
        string h = height.ToString(CultureInfo.InvariantCulture);

        return
        [
            "-map", inputSpecifier,
            "-vf", $"fps={rate},scale={w}:{h}:flags=area",
            "-pix_fmt", "yuv420p",
            "-c:v", "rawvideo",
            "-f", "image2",
            "-frame_pts", "1",
            "-y", Path.Combine(frameDir, $"{Prefix}%d{Extension}"),
        ];
    }

    /// <summary>
    /// Fills <paramref name="buffer"/> from a frame that is fully written, or reports that it is not
    /// yet.
    ///
    /// The length check happens twice deliberately — once on the directory entry to avoid opening a
    /// file that is obviously short, and once on the opened stream, because the first answer can be
    /// stale by the time the handle exists.
    /// </summary>
    private static async Task<bool> TryReadAsync(
        string path, DetectFrame frame, CancellationToken cancellationToken)
    {
        try
        {
            if (new FileInfo(path).Length != frame.Length)
            {
                return false;
            }

            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (stream.Length != frame.Length)
            {
                return false;
            }

            await stream.ReadExactlyAsync(
                frame.Buffer.AsMemory(0, frame.Length), cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException)
        {
            return false;
        }
    }
}
