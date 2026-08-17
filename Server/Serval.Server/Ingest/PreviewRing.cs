using System.Globalization;
using Serval.Server.Configuration;
using Serval.Server.Recordings;

namespace Serval.Server.Ingest;

/// <param name="SessionStamp">Ties every ring file to the session that wrote it, so a restarted
/// session can tell its own segments from the ones it inherited — the same job the stamp does for
/// recordings. See <see cref="SessionSegments"/>.</param>
/// <param name="SegmentCount">How many segments stay in the playlist, which is what makes the ring
/// a ring: ffmpeg deletes what falls off the end.</param>
/// <param name="CodecTag">A <c>-tag:v</c> the copy needs, or null. Same rule as the recorder's.</param>
internal sealed record PreviewRingPlan(
    string SessionStamp,
    string InitFileName,
    int SegmentCount,
    string? CodecTag);

/// <summary>
/// A rolling window of the detect stream on disk, so an alert can be given a clip that starts
/// before it fired.
///
/// <para><b>Why it has to exist.</b> Nothing can reconstruct the seconds before a detection after
/// the fact. Detect frames are deleted as they are read, the snapshot broadcaster keeps one JPEG
/// per camera, and a camera whose <c>Recording</c> switch is off has no footage anywhere — yet
/// detections fire on it regardless, so its alerts need a preview as much as anyone's. The only way
/// to have those seconds is to have been writing them already.</para>
///
/// <para><b>Why it is nearly free.</b> This is <c>-c:v copy</c> of a stream the session is already
/// receiving: no decode, no encode, just a mux. It rides on the detect session, which exists
/// precisely so that the frames it costs are taken off a 640x360 sub stream rather than a 4K main
/// one — so the ring is a few hundred kilobytes a second and a bounded amount of disk.</para>
///
/// <para><b>Why only the detect session writes it.</b> <see cref="StreamIngestManager"/> starts
/// <see cref="FfmpegSnapshotSession"/> exactly when the detect stream is not the one being
/// recorded. When it is, the recording is already that stream's bytes on disk and a preview is cut
/// from the recording index instead — writing a second identical copy would double the disk traffic
/// of a main stream to gain nothing.</para>
/// </summary>
internal static class PreviewRing
{
    public const string PlaylistName = "preview.m3u8";

    /// <summary>
    /// Shared by every ring file. It is what keeps them out of the recording's way: the retention
    /// sweep deletes only filenames <see cref="RecordingIndex"/> handed it, and no ring segment is
    /// ever indexed there, so nothing but ffmpeg and <see cref="Reset"/> touches these.
    /// </summary>
    public const string FilePrefix = "preview-";

    /// <summary>
    /// What the ring should be for this session, or null to write none.
    ///
    /// <para>Null rather than an exception when the source cannot be copied, which is the one real
    /// difference from <see cref="IngestPlanner.ResolveVideo"/>. That method refuses to guess and
    /// throws, because a recording that silently is not what the operator asked for is worse than
    /// no recording. Here the stakes are the opposite way round: this session's actual job is
    /// producing frames for detection, and failing it over a preview buffer would cost the camera
    /// its detection, its wall tile and its vision model to gain nothing. So an mjpeg sub stream
    /// gets no ring, says so once, and goes on detecting.</para>
    /// </summary>
    public static PreviewRingPlan? Plan(
        string cameraId,
        string sessionStamp,
        string? probedVideoCodec,
        IngestOptions options,
        ILogger logger)
    {
        if (options.PreviewBufferSeconds <= 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(probedVideoCodec))
        {
            logger.LogInformation(
                "Camera {CameraId}: no preview buffer — the detect stream would not say what codec "
                + "it is sending. Alerts on this camera will have no clip.",
                cameraId);
            return null;
        }

        string codec = probedVideoCodec.Trim();
        IReadOnlyList<string> copyable = options.ResolvedVideoPassthroughCodecs;

        if (!copyable.Any(c => string.Equals(c?.Trim(), codec, StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogWarning(
                "Camera {CameraId}: no preview buffer — its detect stream sends '{Codec}', which "
                + "fMP4 cannot carry untouched, and Serval will not re-encode a stream to buffer it. "
                + "Detection is unaffected; alerts on this camera will have no clip.",
                cameraId, codec);
            return null;
        }

        // Two segments minimum, because a ring of one holds less than the segment currently being
        // written and would never contain a complete window.
        int count = Math.Max(2, (int)Math.Ceiling(options.PreviewBufferSeconds / options.SegmentSeconds));

        return new PreviewRingPlan(
            SessionStamp: sessionStamp,
            InitFileName: $"{FilePrefix}init-{sessionStamp}.mp4",
            SegmentCount: count,
            CodecTag: string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase) ? "hvc1" : null);
    }

    /// <summary>
    /// The ring's output on an ffmpeg command line, written relative to the camera's media
    /// directory because that is the session's working directory.
    /// </summary>
    public static IReadOnlyList<string> OutputArgs(
        string inputSpecifier, PreviewRingPlan plan, double segmentSeconds)
    {
        var args = new List<string> { "-map", inputSpecifier, "-c:v", "copy" };

        if (plan.CodecTag is { } tag)
        {
            args.AddRange(["-tag:v", tag]);
        }

        // Audio is mapped optionally and always re-encoded, which is the opposite of the recorder's
        // rule and deliberate. A sound alert deserves a clip you can hear, so the track is worth
        // having; but copying it would mean probing the source for a codec this session otherwise
        // never asks about — a second ffprobe that can stall for fifteen seconds before detection
        // starts — and then handling the usual answer, G.711, which fMP4 cannot carry at all. One
        // mono AAC encode at 64 kbps costs a fraction of a percent of a core in a session that is
        // already decoding every frame for snapshots, and it always produces a file that plays.
        args.AddRange([
            "-map", "0:a?",
            "-c:a", "aac", "-b:a", "64k", "-ac", "1",
        ]);

        args.AddRange([
            "-f", "hls",
            "-hls_segment_type", "fmp4",
            "-hls_time", segmentSeconds.ToString(CultureInfo.InvariantCulture),

            // The exact inverse of the recording output, which sets hls_list_size 0 and omits
            // delete_segments so that nothing ffmpeg writes is ever lost. Here ffmpeg owning the
            // pruning is the whole point: it bounds the ring at its source, so there is no janitor
            // to write, nothing to fall behind, and no way for a camera to fill a disk with footage
            // nobody asked to keep.
            "-hls_list_size", plan.SegmentCount.ToString(CultureInfo.InvariantCulture),
            "-hls_flags", "independent_segments+delete_segments",
            "-hls_fmp4_init_filename", plan.InitFileName,
            "-hls_segment_filename", $"{FilePrefix}{plan.SessionStamp}-%05d.m4s",
            PlaylistName,
        ]);

        return args;
    }

    /// <summary>
    /// Clears the previous session's ring.
    ///
    /// <c>delete_segments</c> only prunes what the running process wrote, so without this every
    /// reconnect would leave a full buffer's worth of orphans behind — files no index names, that
    /// the retention sweep is built never to touch, on the volume holding the recordings. Best
    /// effort: a file that will not go is one the alert sweep picks up later.
    /// </summary>
    public static void Reset(string cameraDir)
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(cameraDir, $"{FilePrefix}*"))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left for the alert retention sweep.
                }
            }

            File.Delete(Path.Combine(cameraDir, PlaylistName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Nothing here is worth failing a session over.
        }
    }

    /// <summary>
    /// Keeps <see cref="PreviewRingIndex"/> matching what is on disk, by re-reading the playlist on
    /// a short timer.
    ///
    /// <para>The same shape as the recording indexer, and for the same reason: <c>#EXTINF</c> is the
    /// only honest duration under <c>-c:v copy</c>, where a segment is as long as the camera's GOP
    /// made it rather than as long as it was asked to be. Start times accumulate from the session
    /// anchor, so a ring segment and a detection made from a frame of the same stream are measured
    /// against the same clock — which is what lets a preview actually contain the moment its alert
    /// fired.</para>
    ///
    /// <para>Unlike a recording, the playlist here is a window rather than the whole session, so
    /// the accumulation has to be carried across passes: what has fallen off the front is gone from
    /// the file but its duration still counts towards where everything after it starts.</para>
    /// </summary>
    public static async Task WatchAsync(
        string cameraDir,
        string cameraId,
        PreviewRingPlan plan,
        DateTimeOffset sessionStart,
        double segmentSeconds,
        PreviewRingIndex index,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        index.Begin(cameraId, plan.InitFileName);

        string playlistPath = Path.Combine(cameraDir, PlaylistName);
        string prefix = $"{FilePrefix}{plan.SessionStamp}-";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset nextStart = sessionStart;
        int lastNumber = -1;

        var period = TimeSpan.FromSeconds(Math.Max(segmentSeconds / 2, 1));
        using var timer = new PeriodicTimer(period);

        try
        {
            do
            {
                try
                {
                    if (!File.Exists(playlistPath))
                    {
                        continue; // ffmpeg has not closed its first segment yet
                    }

                    string playlist = await File.ReadAllTextAsync(playlistPath, cancellationToken);
                    var living = new HashSet<string>(StringComparer.Ordinal);

                    foreach ((string fileName, double duration) in HlsPlaylist.ParseSegments(playlist))
                    {
                        // Anything not stamped with this session's id was left by a previous run,
                        // and counting it would push this session's own segments an entire
                        // recording into the future.
                        if (!fileName.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        living.Add(fileName);

                        if (!seen.Add(fileName))
                        {
                            continue;
                        }

                        int number = SegmentNumber(fileName, prefix);

                        // Segments are numbered from zero and seen long before they are pruned —
                        // the ring holds a minute and a half and this loop runs every couple of
                        // seconds — so a jump means this task was not running for longer than the
                        // whole buffer. Everything after the jump is offset by the missing
                        // durations, which cannot be recovered because those segments are gone, so
                        // re-anchor instead: a segment appears in the playlist when it closes, so
                        // it started about its own duration ago. That is accurate to one pass,
                        // where carrying on would be wrong by however long the stall was.
                        if (lastNumber >= 0 && number != lastNumber + 1)
                        {
                            logger.LogWarning(
                                "Camera {CameraId}: preview buffer skipped {Missing} segment(s); "
                                + "re-anchoring. Alert clips from around now may be a second out.",
                                cameraId, number - lastNumber - 1);

                            nextStart = DateTimeOffset.UtcNow.AddSeconds(-duration);
                        }

                        index.Add(cameraId, new RecordingSegment
                        {
                            CameraId = cameraId,
                            FileName = fileName,
                            InitFileName = plan.InitFileName,
                            StartedAt = nextStart,
                            DurationSeconds = duration,
                        });

                        nextStart = nextStart.AddSeconds(duration);
                        lastNumber = number;
                    }

                    index.Retain(cameraId, living);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // Caught ffmpeg mid-rewrite; the next tick will get it.
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex, "Camera {CameraId}: could not read the preview buffer this pass.", cameraId);
                }
            }
            while (await TaskWaits.SafeWaitAsync(timer, cancellationToken));
        }
        finally
        {
            // The session is over, so its ring is about to be deleted by the next Reset. Leaving
            // the index populated would offer the alert worker segment names that are on their way
            // out, and it has no way to tell those from live ones.
            index.Forget(cameraId);
        }
    }

    /// <summary>
    /// The <c>%05d</c> ffmpeg wrote into a segment's name, or -1 if it is not there. Only used to
    /// notice a gap, so an unparseable name is treated as "no opinion" rather than as an error.
    /// </summary>
    private static int SegmentNumber(string fileName, string prefix)
    {
        string tail = Path.GetFileNameWithoutExtension(fileName)[prefix.Length..];
        return int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            ? number
            : -1;
    }
}
