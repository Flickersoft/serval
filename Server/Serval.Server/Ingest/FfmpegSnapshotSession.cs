using System.Globalization;
using Serval.Server.Cameras;
using Serval.Server.Configuration;
using Serval.Server.Recordings;
using Serval.Server.Snapshots;

namespace Serval.Server.Ingest;

/// <summary>
/// Produces a camera's frames for detection from a stream that isn't the one being recorded — the
/// point of giving a camera a separate detect stream. That means the ~1 fps JPEG the dashboard and
/// the vision model read, the raw frames object detection runs on, and a rolling buffer of the
/// stream itself so an alert can be given a clip.
///
/// It exists so the two jobs can be paid for separately. Both frame outputs require a full decode of
/// whatever they are taken from, so taking them off a 640x360 sub stream leaves the recorder as a
/// pure copy — the same frames still reach detection, the vision model, the dashboard wall
/// and <c>/snapshot.jpg</c>, for orders of magnitude less work than decoding a 4K main stream.
///
/// Writes only into the camera's snapshot directory, its own tmpfs frame directory, and the
/// <c>preview-</c> files of <see cref="PreviewRing"/>, and only ever runs when the recording session
/// has given up those outputs, so the two never race.
/// </summary>
public sealed class FfmpegSnapshotSession
{
    private readonly Camera _camera;
    private readonly CameraStream _stream;
    private readonly IngestOptions _options;
    private readonly string _cameraDir;
    private readonly string _snapshotDir;
    private DateTimeOffset _sessionStart;
    private readonly SnapshotBroadcaster _snapshots;
    private readonly DetectFrameBroadcaster _detectFrames;
    private readonly PreviewRingIndex _previewRing;
    private readonly ILogger _logger;

    /// <param name="stream">The camera's detect stream.</param>
    public FfmpegSnapshotSession(
        Camera camera,
        CameraStream stream,
        IngestOptions options,
        string mediaRoot,
        SnapshotBroadcaster snapshots,
        DetectFrameBroadcaster detectFrames,
        PreviewRingIndex previewRing,
        ILogger logger)
    {
        _camera = camera;
        _stream = stream;
        _options = options;
        _cameraDir = Path.Combine(mediaRoot, camera.Id);
        _snapshotDir = Path.Combine(_cameraDir, SnapshotWatcher.DirectoryName);
        _snapshots = snapshots;
        _detectFrames = detectFrames;
        _previewRing = previewRing;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cameraDir);
        SnapshotWatcher.Reset(_snapshotDir);
        PreviewRing.Reset(_cameraDir);

        // Asked for the frame size, which both frame outputs need, and — now that the ring copies
        // this stream to disk — for its codec, which decides whether the ring can exist at all.
        VideoProbe probe = await SourceProbe.VideoAsync(
            _stream.Url, _options.FfprobePath, _camera.Id, _logger, cancellationToken);

        // A source that will not give its dimensions gets no session at all, rather than one built
        // from the outputs that do not need them. The JPEG leg needs neither dimensions nor codec,
        // so a snapshot-only session starts cleanly and stays up: ffmpeg is running and producing
        // exactly what it was asked for, the wall and /snapshot.jpg are live, and every symptom of
        // detection having no frames — and no way to ever get them, because nothing here fails and
        // so nothing retries — surfaces somewhere else entirely, as an AI session restarting on its
        // idle timeout for as long as the process lives.
        //
        // Thrown rather than raised as IngestConfigurationException: a stream that did not answer
        // inside the probe timeout is a source problem that usually clears on its own, so this
        // wants the supervisor's exponential backoff, not the go-to-the-cap-and-wait-for-an-edit
        // path that exists for settings a human has to change. The recording half refuses an
        // unplannable session on the same principle — see IngestPlanner.ResolveVideo.
        if (_options.DetectFps > 0 && probe is not { Width: > 0, Height: > 0 })
        {
            throw new InvalidOperationException(
                $"Stream '{_stream.Name}' did not report its frame size, so this session could "
                + "produce snapshots but no frames for object detection. Retrying until the probe "
                + "answers.");
        }

        DetectFramePlan? detect = DetectFrameReader.Plan(_camera.Id, probe, _options, _logger);

        // What this session calls media offset zero, stamped immediately before ffmpeg starts for
        // the reason FfmpegStreamSession stamps its own here: whatever sits between the anchor and
        // the first frame is error, and it is error the recorder does not share. This process has
        // its own RTSP connection and so its own remaining startup delay, which is why a camera
        // that detects and records on one stream agrees exactly and one that splits them across
        // two does not quite.
        _sessionStart = DateTimeOffset.UtcNow;

        PreviewRingPlan? preview = PreviewRing.Plan(
            _camera.Id,
            _sessionStart.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
            probe.Codec,
            _options,
            _logger);

        List<Func<CancellationToken, Task>> sideTasks =
        [
            ct => SnapshotWatcher.WatchAsync(
                _snapshotDir, _camera.Id, _options.SnapshotFps, _sessionStart, _snapshots, ct),
        ];

        if (detect is not null)
        {
            DetectFrameReader.Reset(detect.Directory);
            sideTasks.Add(ct => DetectFrameReader.WatchAsync(
                detect.Directory, _camera.Id, detect.Width, detect.Height, detect.Fps,
                _options.DetectFrameBacklog, _sessionStart, _detectFrames, _logger, ct));
        }

        if (preview is not null)
        {
            sideTasks.Add(ct => PreviewRing.WatchAsync(
                _cameraDir, _camera.Id, preview, _sessionStart, _options.SegmentSeconds,
                _previewRing, _logger, ct));
        }

        // Watched on the same terms as the recorder. This session needs it for a reason of its own:
        // when it wedges nothing looks wrong at all, because recording carries on from a different
        // process while detection, the vision model and the wall stop together, quietly.
        await FfmpegRunner.RunAsync(
            _options.FfmpegPath,
            BuildArguments(detect, preview),
            _cameraDir,
            _camera.Id,
            $"detect, frames from '{_stream.Name}'",
            _logger,
            sideTasks,
            cancellationToken,
            TimeSpan.FromSeconds(_options.StallTimeoutSeconds));
    }

    /// <summary>
    /// One input and up to three outputs, with no encoder plan. The JPEG encoder is fixed, the raw
    /// frames are not encoded at all, and the ring is a copy — none of it is a rate worth hardware
    /// acceleration.
    /// </summary>
    private IReadOnlyList<string> BuildArguments(DetectFramePlan? detect, PreviewRingPlan? preview)
    {
        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "warning" };

        // Before -i, where it binds to the decoder rather than to an encoder. Everything this
        // process decodes is destined for a still, so the frame-threading delay described on
        // ServerOptions.DecodeThreads is pure latency here with nothing bought for it. The ring
        // does not change that: it copies, so it never reaches the decoder at all.
        args.AddRange(["-threads", _options.DecodeThreads.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(SourceArguments.InputArgs(_stream.Url));
        args.AddRange(SnapshotWatcher.OutputArgs(
            "0:v", _options.SnapshotFps, PixelBudget.Pixels(_options.SnapshotMaxMegapixels)));

        if (detect is not null)
        {
            args.AddRange(DetectFrameReader.OutputArgs(
                "0:v", detect.Fps, detect.Width, detect.Height, detect.Directory));
        }

        if (preview is not null)
        {
            args.AddRange(PreviewRing.OutputArgs("0:v", preview, _options.SegmentSeconds));
        }

        return args;
    }
}
