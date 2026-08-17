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
        // this stream to disk — for its codec, which decides whether the ring can exist at all. A
        // source that will not answer costs the raw detect frames and the ring; the JPEG path needs
        // neither.
        VideoProbe probe = await SourceProbe.VideoAsync(
            _stream.Url, _options.FfprobePath, _camera.Id, _logger, cancellationToken);

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
