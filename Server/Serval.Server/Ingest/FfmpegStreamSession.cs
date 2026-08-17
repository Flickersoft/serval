using System.Diagnostics;
using System.Globalization;
using Serval.Server.Cameras;
using Serval.Server.Configuration;
using Serval.Server.Recordings;
using Serval.Server.Snapshots;

namespace Serval.Server.Ingest;

/// <summary>
/// A camera's recording process: an HLS fMP4 stream on disk (init + numbered segments, serving as
/// both the recording and the live stream), and — when this same stream also carries the detect
/// role — a ~1 fps JPEG snapshot. <see cref="RunAsync"/> returns when ffmpeg exits or the token
/// cancels; restart and backoff belong to <see cref="StreamIngestManager"/>.
///
/// Video is recorded untouched unless the stream explicitly asks to be transcoded
/// (<see cref="CameraStream.Transcode"/>); a source Serval cannot record as-is
/// (<see cref="IngestOptions.VideoPassthroughCodecs"/>) is an error naming the codec, never a
/// silent re-encode. When a camera has a separate detect stream this session emits no snapshot
/// output at all, which is what makes a copy genuinely free — with no JPEG to produce, ffmpeg never
/// decodes a frame.
///
/// Each session stamps its files with its UTC start time (<c>seg-&lt;stamp&gt;-NNNNN.m4s</c>,
/// <c>init-&lt;stamp&gt;.mp4</c>), which ties every segment to its init and — because
/// <c>live.m3u8</c> outlives the run that wrote it — is how a session tells its own segments from
/// the ones it inherited. See <see cref="SessionSegments"/>.
///
/// HLS with <c>fmp4</c> segments rather than DASH, so a single <c>.m4s</c> carries both tracks —
/// one file you can concatenate onto its init and play anywhere, with sound. See
/// <c>Docs/recording.md</c> for why the DASH muxer cannot do that.
/// </summary>
public sealed class FfmpegStreamSession
{
    private readonly Camera _camera;
    private readonly CameraStream _stream;
    private readonly bool _ownsSnapshots;
    private readonly IngestOptions _options;
    private readonly FfmpegCapabilities _capabilities;
    private readonly string _cameraDir;
    private readonly SnapshotBroadcaster _snapshots;
    private readonly DetectFrameBroadcaster _detectFrames;
    private readonly RecordingIndex _recordings;
    private readonly ILogger _logger;

    /// <summary>
    /// What this session calls media offset zero — every segment's start is measured from it, and
    /// so is every detection made from the frames of the same stream.
    ///
    /// Stamped immediately before ffmpeg is launched rather than when the session is constructed,
    /// because <see cref="ProbeAsync"/> sits between the two and takes as long as the source makes
    /// it: two ffprobe calls, each capped at fifteen seconds. Measured on a real camera that gap
    /// was 5.2 s, and every second of it dated the recording earlier than the footage it actually
    /// holds — which is a box drawn seconds behind the person it belongs to.
    /// </summary>
    private DateTimeOffset _sessionStart;
    private string _sessionStamp = string.Empty;
    private string _initFileName = string.Empty;
    private readonly string _snapshotDir;

    /// <param name="stream">The camera's record stream — the one written to disk.</param>
    public FfmpegStreamSession(
        Camera camera,
        CameraStream stream,
        IngestOptions options,
        FfmpegCapabilities capabilities,
        string mediaRoot,
        SnapshotBroadcaster snapshots,
        DetectFrameBroadcaster detectFrames,
        RecordingIndex recordings,
        ILogger logger)
    {
        _camera = camera;
        _stream = stream;
        // When this stream also carries the detect role it rides along here, and we produce the
        // snapshot rather than a second process doing it.
        _ownsSnapshots = stream.Roles.Contains(StreamRole.Detect);
        _options = options;
        _capabilities = capabilities;
        _cameraDir = Path.Combine(mediaRoot, camera.Id);
        _snapshots = snapshots;
        _detectFrames = detectFrames;
        _recordings = recordings;
        _logger = logger;

        _snapshotDir = Path.Combine(_cameraDir, SnapshotWatcher.DirectoryName);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cameraDir);

        // The previous run's playlist, before anything can read it as ours. ffmpeg overwrites it
        // eventually, but not until its first segment closes — under `-c:v copy` that waits for a
        // keyframe the camera has yet to send — and the indexer polls every couple of seconds from
        // the moment ffmpeg starts. It also stops /live.m3u8 serving a playlist of dead segments
        // for the length of a reconnect.
        DeleteStalePlaylist();

        (VideoProbe probe, string? audioCodec) = await ProbeAsync(cancellationToken);

        // The codec answer is withheld from the planner for a stream that declares a transcode, which
        // is re-encoded whatever it sends. The dimensions are kept either way — the detect output
        // needs them regardless of how the recording is being written.
        VideoPlan video = IngestPlanner.ResolveVideo(
            _stream, _stream.Transcode is null ? probe.Codec : null, _options, _capabilities);
        AudioPlan audio = IngestPlanner.ResolveAudio(_camera.RecordAudio, audioCodec, _options);

        if (video.Mode == VideoMode.Copy)
        {
            // Said out loud because copy-by-default silently decides what the archive *is*. A
            // deployment of HEVC cameras now archives HEVC, which needs Safari, Edge or a Chrome
            // with hardware decode — a fact that otherwise surfaces as a black frame months later.
            _logger.LogInformation(
                "Camera {CameraId}: recording {Codec} untouched (no transcode configured).",
                _camera.Id, video.Codec);
        }

        if (_camera.RecordAudio && audioCodec is null)
        {
            _logger.LogWarning(
                "Camera {CameraId} has RecordAudio set but its source has no audio track; recording video only.",
                _camera.Id);
        }
        else if (audio is { Include: true, Copy: false })
        {
            _logger.LogInformation(
                "Camera {CameraId}: source audio is {SourceCodec}, which fMP4 cannot carry; "
                + "transcoding to AAC 64k mono. This is a constraint of the recording container, "
                + "not a codec policy — unlike video there is exactly one legal target.",
                _camera.Id, audio.SourceCodec);
        }

        // Probing is done; from here it is only argument building, so this is as close to the first
        // frame as the anchor can get without asking the camera what time it thinks it is.
        _sessionStart = DateTimeOffset.UtcNow;
        _sessionStamp = _sessionStart.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        _initFileName = $"init-{_sessionStamp}.mp4";

        List<Func<CancellationToken, Task>> sideTasks = [IndexSegmentsAsync];
        DetectFramePlan? detect = null;

        if (_ownsSnapshots)
        {
            SnapshotWatcher.Reset(_snapshotDir);
            sideTasks.Add(ct => SnapshotWatcher.WatchAsync(
                _snapshotDir, _camera.Id, _options.SnapshotFps, _sessionStart, _snapshots, ct));

            detect = DetectFrameReader.Plan(_camera.Id, probe, _options, _logger);
            if (detect is not null)
            {
                DetectFrameReader.Reset(detect.Directory);
                sideTasks.Add(ct => DetectFrameReader.WatchAsync(
                    detect.Directory, _camera.Id, detect.Width, detect.Height, detect.Fps,
                    _options.DetectFrameBacklog, _sessionStart, _detectFrames, _logger, ct));
            }
        }

        var spec = new RecordSpec(
            Url: _stream.Url,
            Video: video,
            Audio: audio,
            SegmentSeconds: _options.SegmentSeconds,
            SessionStamp: _sessionStamp,
            InitFileName: _initFileName,
            WithSnapshot: _ownsSnapshots,
            SnapshotFps: _options.SnapshotFps,
            SnapshotMaxPixels: PixelBudget.Pixels(_options.SnapshotMaxMegapixels),
            Detect: detect,
            DecodeThreads: _options.DecodeThreads);

        await FfmpegRunner.RunAsync(
            _options.FfmpegPath,
            RecordArguments.Build(spec),
            _cameraDir,
            _camera.Id,
            video.Mode == VideoMode.Copy
                ? $"record, copy ({video.Codec})"
                : $"record, transcode→{video.Codec} ({video.Encoder!.EncoderName})",
            _logger,
            sideTasks,
            cancellationToken,
            TimeSpan.FromSeconds(_options.StallTimeoutSeconds));
    }

    /// <summary>
    /// Asks the source what it is sending, for the questions that shape the command line: which
    /// video codec (so <see cref="IngestPlanner"/> can decide whether it is recordable as-is), how
    /// large its frames are (so the detect output can be written at an exact size), and whether there
    /// is an audio track at all.
    ///
    /// The video probe runs even for a stream that declares a transcode, whose codec cannot change
    /// anything — its *dimensions* still can. The audio probe is skipped unless a camera actually
    /// opted in to recording sound, which also skips a 15-second stall on a wedged source. A null
    /// audio codec means "no audio track", which is a normal answer, not a fault. A null
    /// <em>video</em> codec is a fault, and one this refuses to paper over — see
    /// <see cref="IngestPlanner.ResolveVideo"/> for why guessing is worse than retrying.
    /// </summary>
    private async Task<(VideoProbe Video, string? AudioCodec)> ProbeAsync(
        CancellationToken cancellationToken)
    {
        VideoProbe video = await SourceProbe.VideoAsync(
            _stream.Url, _options.FfprobePath, _camera.Id, _logger, cancellationToken);

        string? audioCodec = _camera.RecordAudio
            ? await SourceProbe.AudioCodecAsync(
                _stream.Url, _options.FfprobePath, _camera.Id, _logger, cancellationToken)
            : null;

        return (video, audioCodec);
    }

    /// <summary>
    /// Indexes HLS media segments as they appear, by re-reading the live playlist on a short timer.
    ///
    /// The playlist is the source of truth rather than a directory scan, and specifically rather
    /// than arithmetic on the segment number. Durations are only nominally
    /// <see cref="IngestOptions.SegmentSeconds"/>: under <c>-c:v copy</c> ffmpeg can cut only at a
    /// keyframe the camera already sent, so a 10-second GOP against a 4-second target yields
    /// 10-second segments. An index assuming the target drifts six seconds per segment — half an
    /// hour of error within an hour of recording — putting every seek in the wrong place and pruning
    /// by a timestamp that is not the segment's. <c>hls_list_size 0</c> keeps every segment of the
    /// session in the playlist with its real <c>#EXTINF</c>, so start times accumulate exactly.
    ///
    /// A FileSystemWatcher is tempting and wrong: ffmpeg writes each segment as a <c>.tmp</c> then
    /// renames it into place, so segments arrive as Renamed rather than Created events. The
    /// in-memory <c>seen</c> set keeps the pass from re-hitting Mongo for what is already indexed.
    ///
    /// Which segments are *ours* is <see cref="SessionSegments.Resolve"/>'s business, and it is not
    /// a formality: the playlist on disk when this starts is the previous run's.
    /// </summary>
    private async Task IndexSegmentsAsync(CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string playlistPath = Path.Combine(_cameraDir, "live.m3u8");
        var period = TimeSpan.FromSeconds(Math.Max(_options.SegmentSeconds / 2, 1));
        using var timer = new PeriodicTimer(period);

        do
        {
            // Per pass, so one bad pass costs one pass. A throw escaping here leaves the session
            // recording with nothing indexing it — silently, because FfmpegRunner discards what a
            // side task throws — and those unindexed segments are what the *next* session would
            // then adopt at the wrong times.
            try
            {
                if (!File.Exists(playlistPath))
                {
                    continue; // ffmpeg has not written it yet
                }

                string playlist = await File.ReadAllTextAsync(playlistPath, cancellationToken);

                foreach ((string fileName, DateTimeOffset startsAt, double duration) in
                    SessionSegments.Resolve(playlist, _sessionStamp, _sessionStart))
                {
                    if (!seen.Add(fileName))
                    {
                        continue;
                    }

                    await _recordings.AddIfNewAsync(
                        new RecordingSegment
                        {
                            CameraId = _camera.Id,
                            FileName = fileName,
                            InitFileName = _initFileName,
                            StartedAt = startsAt,
                            DurationSeconds = duration,
                        },
                        cancellationToken);
                }
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
                _logger.LogWarning(
                    ex, "Camera {CameraId}: could not index recorded segments this pass.",
                    _camera.Id);
            }
        }
        while (await TaskWaits.SafeWaitAsync(timer, cancellationToken));
    }

    /// <summary>
    /// Removes the playlist left behind by whatever ran before this session.
    ///
    /// Best-effort: a playlist that cannot be deleted is not worth refusing to record over, because
    /// <see cref="SessionSegments.Resolve"/> already declines to adopt segments that are not this
    /// session's. This narrows the window rather than being the guard.
    /// </summary>
    private void DeleteStalePlaylist()
    {
        try
        {
            File.Delete(Path.Combine(_cameraDir, "live.m3u8"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex, "Camera {CameraId}: could not remove the previous session's playlist.",
                _camera.Id);
        }
    }

}
