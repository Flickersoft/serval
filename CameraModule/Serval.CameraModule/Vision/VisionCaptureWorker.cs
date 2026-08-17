using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serval.Ai;
using Serval.Contracts;

namespace Serval.CameraModule;

/// <summary>
/// Keeps the <see cref="FrameRing"/> stocked with recent JPEG frames, and asks for a description
/// when the scene changes.
///
/// Capture is cheap (the camera emits JPEG; we just copy it), so it runs on a simple interval
/// independent of whether anything describes the frames. Incomplete frames are discarded rather
/// than published — a truncated JPEG would waste a very expensive VLM inference and produce a
/// confident description of a smeared image.
///
/// Motion gating lives here rather than in the description worker because this is where frames
/// arrive: comparing them costs a downscale and a subtraction, where the model behind the gate costs
/// seconds of CPU. It is also what lets vision run on an empty room in silence, rather than only
/// when somebody speaks.
///
/// There is deliberately no synthetic-frame fallback: no camera means no frames, not
/// fabricated ones. Audio is unaffected either way.
/// </summary>
public sealed class VisionCaptureWorker : BackgroundService
{
    private readonly FrameRing _frames;
    private readonly SceneDescriptionService _scenes;
    private readonly CaptureOptions _capture;
    private readonly VisionOptions _vision;
    private readonly MotionOptions _motion;
    private readonly ILogger<VisionCaptureWorker> _logger;

    public VisionCaptureWorker(
        FrameRing frames,
        SceneDescriptionService scenes,
        IOptions<CameraModuleOptions> options,
        ILogger<VisionCaptureWorker> logger)
    {
        _frames = frames;
        _scenes = scenes;
        _capture = options.Value.Capture;
        _vision = options.Value.Vision;
        _motion = options.Value.Motion;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        V4l2MjpegCamera camera;
        try
        {
            camera = new V4l2MjpegCamera(_capture.DevicePath, _capture.Width, _capture.Height, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Camera unavailable at {Device}. Vision will produce nothing; audio is unaffected.",
                _capture.DevicePath);
            return;
        }

        using (camera)
        {
            var interval = TimeSpan.FromSeconds(_capture.CaptureIntervalSeconds);
            var motion = new JpegMotionDetector(_motion);
            bool warnedTruncated = false;
            bool primed = false;

            _logger.LogInformation(
                "Vision capture running every {Interval}s; motion gate {State} (>= {Min:P1} of pixels changed).",
                _capture.CaptureIntervalSeconds,
                _motion.Enabled ? "enabled" : "disabled",
                _motion.MinChangedFraction);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    byte[]? frame = camera.ReadFrame(timeoutMs: 2000);

                    if (frame is null)
                    {
                        _logger.LogWarning("No frame within 2s. Is the camera still connected?");
                    }
                    else if (!V4l2MjpegCamera.IsCompleteJpeg(frame))
                    {
                        // Routine for the first frames after streaming starts, so only say so once.
                        if (!warnedTruncated)
                        {
                            _logger.LogDebug("Discarding truncated frame(s); this is normal during warmup.");
                            warnedTruncated = true;
                        }
                    }
                    else
                    {
                        _frames.Add(frame);

                        if (!primed)
                        {
                            // Describe the scene once as soon as we can see, so a description is
                            // already waiting when the first utterance arrives. Otherwise the
                            // first speaker always gets no vision: the audio path never waits for
                            // a description, it only attaches one that already exists.
                            primed = true;
                            Request(SceneTrigger.Periodic, motionScore: null);
                        }
                        else if (_motion.Enabled)
                        {
                            CheckForMotion(motion, frame);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Frame capture failed; continuing.");
                }

                try
                {
                    await Task.Delay(interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void CheckForMotion(JpegMotionDetector motion, byte[] frame)
    {
        MotionResult result;
        try
        {
            result = motion.Accept(frame);
        }
        catch (Exception ex)
        {
            // A frame we cannot decode is not a reason to stop watching for movement.
            _logger.LogWarning(ex, "Motion detection failed for one frame; continuing.");
            return;
        }

        if (result.Rejected)
        {
            _logger.LogDebug(
                "Ignoring a whole-frame change ({Fraction:P1}); this is lighting or IR-cut, not motion.",
                result.ChangedFraction);
            return;
        }

        if (!result.Moved)
        {
            return;
        }

        _logger.LogDebug("Motion: {Fraction:P2} of pixels changed.", result.ChangedFraction);
        Request(SceneTrigger.Motion, result.ChangedFraction);
    }

    private void Request(string trigger, double? motionScore)
    {
        IReadOnlyList<VisionFrame> frames = _frames.Recent(Math.Max(1, _vision.MaxFrames));
        if (frames.Count == 0)
        {
            return;
        }

        _scenes.RequestDescription(new SceneRequest(frames, trigger, motionScore));
    }
}
