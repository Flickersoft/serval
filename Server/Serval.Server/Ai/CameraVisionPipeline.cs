using System.Buffers;
using Serval.Ai;
using Serval.Contracts;
using Serval.Server.Cameras;
using Serval.Server.Snapshots;

namespace Serval.Server.Ai;

/// <summary>
/// One camera's vision path: decide, per snapshot, whether the scene is worth describing, and ask
/// for a description when it is.
///
/// Separate from <see cref="CameraAiCoordinator"/>, which is in the reconcile-and-supervise
/// business, and holding per-camera state of its own — the same split the audio half has in
/// <see cref="CameraAudioDetector"/>.
///
/// Two gates can stand in front of the vision model and only one runs:
///
/// <list type="bullet">
/// <item><b>Object detection</b>, when a detector is loaded and enabled. It reports what is present,
/// which is what makes "a person arrived" and "the car parked here has left" expressible.</item>
/// <item><b>Frame differencing</b> otherwise — the path for a host with no model on disk.</item>
/// </list>
///
/// **Alternatives rather than a chain.** Running motion in front of the detector buys a little
/// compute and inherits both of its blind spots: a whole-frame lighting change is discarded as "not
/// motion" exactly when a floodlight has come on because someone is there, and slow or distant
/// movement never reaches the threshold at all.
///
/// <para><b>Two inputs, and the split is deliberate.</b> Detection runs on raw frames from
/// <see cref="Ingest.DetectFrameReader"/>, already scaled and never through a JPEG — re-encoding a
/// picture only to decode it again costs time and the detail a small distant object can least
/// afford. Descriptions are built from the ~1 fps JPEG snapshots, which is what the vision model
/// wants and what it can keep up with. The motion gate stays on the snapshots too: moving it would
/// change what every existing per-camera threshold means.</para>
/// </summary>
public sealed class CameraVisionPipeline : IDisposable
{
    private readonly Camera _camera;
    private readonly AiOptions _ai;
    private readonly SceneDescriptionWorker? _vision;
    private readonly IObjectDetector? _detector;
    private readonly SceneDescriptionService? _scenes;
    private readonly InferenceScheduler? _scheduler;
    private readonly DetectionLoad? _load;
    private readonly ILogger _logger;

    private readonly JpegMotionDetector? _motion;
    private readonly FrameMotionDetector? _frameMotion;
    private readonly RegionPlanner? _regions;
    private readonly ObjectTracker? _tracker;
    private readonly ObjectEventPolicy? _policy;
    private readonly FrameRing _frames;

    /// <summary>
    /// Scratch for one prepared frame, rented on the first examined frame and held for the life of
    /// the camera's session.
    ///
    /// One buffer is enough because a camera examines one frame at a time, and it is worth holding:
    /// the detector's input runs to a megabyte or two, and allocating that per frame at several
    /// frames a second per camera is an allocation rate nothing else here comes close to.
    ///
    /// Not rented in the constructor, because its size depends on the shape this camera resolves to
    /// and that needs a frame size ffmpeg has not reported yet. Every crop reuses it: crops go into
    /// this camera's own shape rather than one of their own, so the size never changes after the
    /// first frame.
    /// </summary>
    private byte[]? _prepared;

    /// <summary>Tiles covering this camera's frame at the detector's native scale, or null when the whole
    /// frame is examined in one shrunken look. Resolved once on the first frame.</summary>
    private IReadOnlyList<FrameRegion>? _floorTiles;

    /// <summary>The largest crop this camera may examine, or null when nothing bounds them. Resolved
    /// once alongside <see cref="_floorTiles"/> and constant for the same reason.</summary>
    private (int Width, int Height)? _maxRegion;

    /// <summary>The shape this camera's frames and crops are prepared to, resolved once
    /// <see cref="_prepared"/> exists and constant for as long as it does.</summary>
    private DetectorInput _input;

    private readonly ExamineRateGate _examineRate = new(0);

    private bool _primed;

    public CameraVisionPipeline(
        Camera camera,
        AiOptions ai,
        SceneDescriptionWorker? vision,
        IObjectDetector? detector,
        ILogger logger,
        InferenceScheduler? scheduler = null,
        DetectionLoad? load = null)
    {
        _camera = camera;
        _ai = ai;
        _vision = vision;
        _detector = detector;
        _scheduler = scheduler;
        _load = load;
        _logger = logger;

        _scenes = vision?.Register(camera.Id);
        _frames = new FrameRing(Math.Max(1, vision?.FramesPerRequest ?? 1));

        if (UsesDetection)
        {
            _policy = new ObjectEventPolicy(ai.Detection);
            _tracker = new ObjectTracker(ai.Detection.Tracking);
            _frameMotion = new FrameMotionDetector(ai.Motion);
            _regions = new RegionPlanner(ai.Detection.Regions, ai.Detection.Masks);
            _examineRate = new ExamineRateGate(ai.Detection.MaxFps);
        }
        else
        {
            _motion = new JpegMotionDetector(ai.Motion);
        }
    }

    /// <summary>Whether the object gate is the one running. False means frame differencing.</summary>
    public bool UsesDetection => _detector is not null && _ai.Detection.Enabled;

    /// <summary>
    /// Folds one snapshot in: it is always a candidate frame for a description, and on a host with no
    /// detection model it is also what the motion gate compares.
    /// </summary>
    /// <returns>Always empty. The motion path produces no records of its own — a motion score is
    /// only ever an attribute of a description it caused — and detection reports through
    /// <see cref="ProcessDetectFrameAsync"/> instead.</returns>
    public IReadOnlyList<ObjectEpisode> ProcessSnapshot(Snapshot snapshot)
    {
        _frames.Add(new VisionFrame(snapshot.Jpeg, snapshot.CapturedAt));

        if (!_primed)
        {
            // Describe once as soon as we can see, so the camera has a current description before
            // anything has happened. Both gates want this; neither can produce it on its own.
            _primed = true;
            Request(SceneTrigger.Periodic, motionScore: null);

            // The motion gate needs this frame as its reference; with nothing to compare against
            // there is no evidence of change.
            if (!UsesDetection)
            {
                _motion!.Accept(snapshot.Jpeg);
            }

            return [];
        }

        return UsesDetection ? [] : Compare(snapshot);
    }

    /// <summary>
    /// Folds one raw frame into the object gate.
    /// </summary>
    /// <returns>Detection episodes that opened or closed on this frame, for a caller that stores
    /// them.</returns>
    public async Task<IReadOnlyList<ObjectEpisode>> ProcessDetectFrameAsync(
        DetectFrame frame,
        CancellationToken cancellationToken)
    {
        if (!UsesDetection)
        {
            return [];
        }

        // A ceiling on how often this camera is examined at all, independent of how fast frames
        // arrive. It bounds the work *before* a frame is downsampled and planned, which the
        // scheduler cannot: the scheduler divides up inference, and this decides whether the frame
        // is considered in the first place. See ExamineRateGate for why it is not a subtraction.
        if (!_examineRate.Admit(frame.CapturedAt))
        {
            return [];
        }

        IReadOnlyList<DetectedObject>? found;
        try
        {
            found = await ExamineAsync(frame, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One bad frame must not take the camera's session down with it.
            _logger.LogWarning(
                ex, "Object detection failed for one frame of camera {CameraId}.", _camera.Id);
            return [];
        }

        if (found is null)
        {
            // Nothing was examined this frame: not "nothing is there". The policy is deliberately
            // not told, because an observation it never made must not close an episode.
            return [];
        }

        // Between the detector and the policy rather than beside them, so what the policy sees is
        // objects that have been followed rather than boxes that happened to be in one frame. Two
        // things change as a result, and both are what the frame rate was raised for: a subject the
        // detector missed for a frame or two is still reported, because its track coasts, and a
        // confident single-frame ghost never arrives at all, because a track is not believed until
        // it has been seen for TrackingOptions.ConfirmSeconds.
        //
        // Fed the raw detections, including masked and out-of-allowlist ones. Filtering here would
        // buy nothing: the inference these came from has already been paid for, and the tracker's
        // output is what RegionPlanner reads to decide where to look next — where the masked ones are
        // dropped, before they can cost anything. What an operator tunes masks against is the
        // complete count from --replay-gates, which runs the policy with no planner in front of it.
        IReadOnlyList<TrackedObject> tracked = _tracker!.Update(found, frame.CapturedAt);

        ObjectObservation observed = _policy!.Observe(tracked, frame.CapturedAt);

        foreach (DescriptionTrigger trigger in observed.Triggers)
        {
            _logger.LogDebug(
                "Camera {CameraId}: {Label} on {Reason} ({Confidence:0.00}) — requesting a description.",
                _camera.Id, trigger.Label, trigger.Reason, trigger.Confidence);

            Request(SceneTrigger.Object, motionScore: null);
        }

        // Closed episodes and open ones together, in that order. The caller tells them apart by
        // whether they have ended, and does different things with them — the closed ones are the
        // record, the open ones are only where things are right now.
        return [.. observed.Published, .. observed.Live];
    }

    /// <summary>
    /// Runs the detector over wherever the planner says to look, and returns everything found in
    /// frame coordinates.
    ///
    /// <para>Null rather than an empty list when the planner asked for nothing at all. The two mean
    /// opposite things to <see cref="ObjectEventPolicy"/> — empty is "looked, saw nothing", which
    /// starts the clock on closing every open episode — and conflating them is how a subtractive
    /// motion gate ends up reporting that a parked car left.</para>
    ///
    /// <para>Detections from every region are pooled into one observation. Each region's own
    /// geometry has already mapped its boxes back onto the whole frame, so by this point where a box
    /// came from no longer matters — which is what lets the policy stay unaware that regions
    /// exist.</para>
    /// </summary>
    private async Task<IReadOnlyList<DetectedObject>?> ExamineAsync(
        DetectFrame frame,
        CancellationToken cancellationToken)
    {
        // Once, on the first frame, because the shape depends on a frame size only ffmpeg can supply.
        // It cannot change afterwards without the stream's dimensions changing, which restarts the
        // camera and rebuilds this.
        // Non-null for as long as this runs: the only route here is the detection path, which
        // UsesDetection already gates on the detector existing.
        IObjectDetector detector = _detector!;

        byte[] prepared;
        bool first = _prepared is null;

        if (_prepared is null)
        {
            _input = detector.InputFor(frame.Width, frame.Height);
            prepared = _prepared = ArrayPool<byte>.Shared.Rent(_input.ByteLength);
        }
        else
        {
            prepared = _prepared;
        }

        bool cropping = _ai.Detection.Regions.ShouldCrop(frame.Width, frame.Height, _input);

        if (first)
        {
            // Computed once per camera and held, because these are pure functions of the frame size
            // and the detector's input — both fixed for the life of the session. The tiles are null
            // when this camera is not being squeezed enough to be worth tiling, or when the
            // operator has not asked for it at all.
            if (_ai.Detection.Regions.ShouldTileFloor(frame.Width, frame.Height, _input))
            {
                _floorTiles = DetectorShapes.Tiles(
                    frame.Width,
                    frame.Height,
                    _input.Width,
                    _input.Height,
                    _ai.Detection.Regions.TileOverlapFraction);
            }

            _maxRegion = _ai.Detection.Regions.MaxRegion(_input);

            // Said out loud because every part of it is a silent decision otherwise: `auto` resolving
            // crops to off leaves no trace, and a camera whose aspect found no good shape looks
            // exactly like one that did. The picture figure is what shows a mismatch at a glance —
            // much under 90% means this camera is paying to convolve mid-grey.
            double fit = Math.Min(
                (double)_input.Width / frame.Width, (double)_input.Height / frame.Height);
            double picture =
                Math.Round(frame.Width * fit) * Math.Round(frame.Height * fit)
                / ((double)_input.Width * _input.Height);

            // The floor's own form is said out loud for the same reason the crop decision is: whether the
            // whole-frame pass is one shrunken look or a sweep of native-scale tiles is invisible
            // otherwise, and it is the difference between covering the frame and examining it.
            string floor = _floorTiles is { Count: > 1 } tiles
                ? $"tiled into {tiles.Count}"
                : "one whole-frame pass";

            // The bound is reported as what it does to this camera rather than as the setting, because
            // the setting is a scale and the consequence is a size: on a frame that already fits inside
            // it nothing is ever cut, and saying "0.5" would leave an operator unable to tell which of
            // their cameras it touches.
            string bound = _maxRegion is { } max && (frame.Width > max.Width || frame.Height > max.Height)
                ? $"crops bounded to {max.Width}x{max.Height}"
                : "no crop is large enough to bound";

            _logger.LogInformation(
                "Camera {CameraId}: {Frame}x{FrameHeight} frames into a {Input}x{InputHeight} input "
                + "— {Picture:P0} picture at {Fit:0.00}x scale. Region crops {State}, a "
                + "{Gain:0.0}x gain on a distant subject (mode {Mode}). Floor: {Floor}. {Bound}.",
                _camera.Id,
                frame.Width,
                frame.Height,
                _input.Width,
                _input.Height,
                picture,
                fit,
                cropping ? "on" : "off",
                RegionOptions.Gain(frame.Width, frame.Height, _input),
                _ai.Detection.Regions.Mode,
                floor,
                bound);
        }

        // Motion is scored only when crops are being cut, because proposing where to cut them is the
        // only thing anything downstream does with it: the planner returns the whole frame without
        // reading a cell when cropping is off, and an object trigger carries no motion score. Scoring
        // it anyway box-averages the entire luma plane, per frame, per camera, for nothing.
        ReadOnlySpan<byte> changedCells = default;
        int gridWidth = 0;
        int gridHeight = 0;

        if (cropping)
        {
            FrameMotion motion = _frameMotion!.Accept(frame.Luma, frame.Width, frame.Height);
            changedCells = motion.Result.Rejected ? default : motion.ChangedCells;
            gridWidth = _frameMotion.GridWidth;
            gridHeight = _frameMotion.GridHeight;
        }

        // The tracker's view as of the previous frame — it cannot be updated until the detector has
        // run, which is what this is planning. That staleness is the point rather than a compromise:
        // asking where a subject *was* is how you decide where to look for them now.
        IReadOnlyList<PlannedRegion> planned = _regions!.Plan(
            frame.CapturedAt,
            frame.Width,
            frame.Height,
            cropping,
            changedCells,
            gridWidth,
            gridHeight,
            [.. _tracker!.Live],
            _floorTiles,
            _maxRegion,
            (double)_input.Width / _input.Height);

        if (planned.Count == 0)
        {
            return null;
        }

        // What the host can afford, which is not the same question as what this camera wants. The
        // detector is shared and so is the budget; a driveway onto a main road must not spend the
        // back door's inference.
        IReadOnlyList<PlannedRegion> admitted =
            _scheduler?.Admit(_camera.Id, planned, frame.CapturedAt) ?? planned;

        // Reported rather than only logged. A host quietly examining half of what it wanted to looks
        // exactly like one that is keeping up, and the difference is detections that never happened.
        _load?.Examined(admitted.Count);
        if (admitted.Count < planned.Count)
        {
            _load?.ShedRegions(planned.Count - admitted.Count);
        }

        if (admitted.Count == 0)
        {
            return null;
        }

        List<DetectedObject> found = [];

        foreach (PlannedRegion region in admitted)
        {
            PreparedFrame geometry = FramePreparer.Prepare(
                frame.Pixels,
                frame.Width,
                frame.Height,
                region.Region,
                _input,
                prepared);

            IReadOnlyList<DetectedObject> inRegion =
                await detector.DetectPreparedAsync(prepared, _input, geometry, cancellationToken);

            found.AddRange(inRegion);

            if (inRegion.Count > 0)
            {
                _logger.LogTrace(
                    "Camera {CameraId}: {Count} object(s) in a {Reason} region "
                    + "{Width}x{Height} at {X},{Y}.",
                    _camera.Id, inRegion.Count, region.Reason, region.Region.Width,
                    region.Region.Height, region.Region.X, region.Region.Y);
            }
        }

        return found;
    }

    private IReadOnlyList<ObjectEpisode> Compare(Snapshot snapshot)
    {
        if (!_ai.Motion.Enabled)
        {
            return [];
        }

        MotionResult result;
        try
        {
            result = _motion!.Accept(snapshot.Jpeg);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Motion detection failed for one frame of camera {CameraId}.", _camera.Id);
            return [];
        }

        if (result.Rejected)
        {
            _logger.LogDebug(
                "Camera {CameraId}: ignoring a whole-frame change ({Fraction:P1}) — lighting, not motion.",
                _camera.Id, result.ChangedFraction);
            return [];
        }

        if (result.Moved)
        {
            _logger.LogDebug(
                "Camera {CameraId}: motion, {Fraction:P2} of pixels changed.",
                _camera.Id, result.ChangedFraction);
            Request(SceneTrigger.Motion, result.ChangedFraction);
        }

        return [];
    }

    /// <summary>
    /// Closes every episode still open, for when this camera's session is stopping.
    ///
    /// Without it a restart strands its open episodes as records claiming, forever, that someone is
    /// still standing there.
    /// </summary>
    public IReadOnlyList<ObjectEpisode> Finalise(DateTimeOffset now) =>
        _policy?.Finalise(now) ?? [];

    private void Request(string trigger, double? motionScore)
    {
        if (_vision is null || _scenes is null)
        {
            // Detection without a vision model is a supported deployment: the 12 MB detector runs,
            // episodes are recorded, and there is simply nothing to describe them with.
            return;
        }

        IReadOnlyList<VisionFrame> recent = _frames.Recent(_vision.FramesPerRequest);
        if (recent.Count == 0)
        {
            return;
        }

        if (_scenes.RequestDescription(new SceneRequest(recent, trigger, motionScore)))
        {
            _vision.Notify();
        }
    }

    public void Dispose()
    {
        _vision?.Forget(_camera.Id);

        // So the cameras still running get this one's share back. Without it a server that has had
        // twenty cameras over its life keeps dividing the budget twenty ways.
        _scheduler?.Forget(_camera.Id);

        if (_prepared is not null)
        {
            ArrayPool<byte>.Shared.Return(_prepared);
        }
    }
}
