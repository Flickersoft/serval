namespace Serval.Ai;

/// <summary>
/// Where something sits in the frame, as a fraction of the frame's own width and height rather
/// than in pixels.
///
/// Normalised because the frame a box was found in and the frame it is drawn on are routinely not
/// the same picture — detection runs on the ~1 fps snapshot, a consumer may be drawing over a
/// full-resolution still or a scaled-down wall tile. Fractions make the box correct on any
/// rendition of the same moment without the consumer knowing the detector's input size.
///
/// <paramref name="X"/> and <paramref name="Y"/> are the top-left corner.
/// </summary>
public readonly record struct BoundingBox(float X, float Y, float Width, float Height);

/// <summary>
/// One object's box and how confident the detector was that it was there.
///
/// Separate from <see cref="BoundingBox"/> rather than a field on it, because the box is also what
/// masks, letterboxing and overlap suppression work in, and none of those have any business with a
/// score. This is the pair an episode carries once it has decided the detection is worth keeping.
///
/// The score is what this box in this frame was seen at, which is not the episode's
/// <see cref="ObjectEpisode.PeakConfidence"/> — that is the best the object ever reached.
/// </summary>
public readonly record struct ScoredBox(BoundingBox Box, float Score);

/// <summary>
/// Where one object was, from <paramref name="At"/> until the next sample.
///
/// Run-length rather than one entry per examined frame: a box is only recorded when it has actually
/// moved, so a car parked for ten minutes is one sample and not six hundred. A consumer reads a
/// sample as true until the following one, and the last one as true until the episode's end.
///
/// A null <paramref name="Box"/> is a gap — the object was looked for, not found, and past the
/// window in which <see cref="ObjectTracker"/> would still predict a position, while the episode
/// stayed open waiting out <see cref="DetectionOptions.AbsenceSeconds"/>. Without it the run-length
/// rule would hold a box on screen through half a minute in which nothing was there.
/// </summary>
public readonly record struct TrackSample(DateTimeOffset At, ScoredBox? Box);

/// <summary>One thing the detector found. <paramref name="Label"/> is the model's own class
/// string, verbatim and un-renamed — the same rule <see cref="ScoredSound"/> follows, and for the
/// same reason: grouping labels into categories is a presentation decision, and a consumer that
/// makes it locally can change its mind without a schema change.</summary>
public sealed record DetectedObject(string Label, float Score, BoundingBox Box);

/// <summary>
/// What a detector can say about its own condition, for the status page to report.
///
/// <para><b>Reported, never re-measured.</b> Re-running <see cref="InferenceBudget"/> against a live
/// detector would measure contention rather than capacity, so the scheduler scales its original
/// measurement by <paramref name="HealthyLanes"/> instead.</para>
///
/// <para>The failure this exists to make visible: a backend whose lanes are hardware can lose half its
/// capacity without anything else noticing. The budget was measured once at startup, so the scheduler
/// keeps admitting twice what the host can now do; the excess becomes dropped frames rather than shed
/// regions, and coverage — which counts only what the budget refused — stays clean. So the status page
/// reads healthy while half the detections silently stop happening.</para>
/// </summary>
/// <param name="Lanes">Lanes the backend was built with.</param>
/// <param name="HealthyLanes">Lanes able to run now. Fewer than <paramref name="Lanes"/> is degraded.</param>
/// <param name="DroppedWhileBusy">Frames skipped because every lane was occupied, cumulative.</param>
/// <param name="Degraded">A human-readable reason when something is wrong, else null.</param>
/// <param name="AcceleratorLabel">What to call these devices where a person will read it — "Edge
/// TPU". Null on a backend whose lanes are threads, which is what keeps the status page's
/// accelerator meter off a host that has no accelerator.</param>
/// <param name="Devices">One entry per physical device, or null where a lane is not one. See
/// <see cref="DetectorDevice"/> for why the figures on it are totals rather than rates.</param>
public readonly record struct DetectorHealth(
    int Lanes,
    int HealthyLanes,
    long DroppedWhileBusy,
    string? Degraded,
    string? AcceleratorLabel = null,
    IReadOnlyList<DetectorDevice>? Devices = null);

/// <summary>
/// One physical device a backend is running on, and what it has done since the process started.
///
/// <para><b>Cumulative totals, never rates.</b> A rate only exists between two readings, and the
/// detector does not know when it was last read — so it counts, and whoever is sampling divides by
/// the time between its own two samples. That is the same instrument the i915 perf counters use on
/// the same page, and it is what makes a busy percentage honest about the window it covers rather
/// than about however often the detector happened to be asked.</para>
///
/// <para><see cref="BusySeconds"/> is time inside the device call itself, not the whole rent: the
/// host-side copy in and the decode out are processor work, and counting them would make a busy
/// accelerator indistinguishable from a busy CPU with an idle one.</para>
///
/// <para>Startup measurement lands in these totals — <see cref="InferenceBudget"/> runs real
/// inferences to time the backend before any camera does. It perturbs the first window only, which
/// is the window a sampler has no previous reading for anyway.</para>
/// </summary>
/// <param name="Path">How the device names itself — a Coral's sysfs path, <c>2-2</c>. The only
/// stable identifier it has: <c>edgetpu_list_devices</c> guarantees no order and was observed
/// returning two devices in one order and then the other between runs.</param>
/// <param name="Healthy">Whether this device can run now. False is a device that stopped answering
/// and is waiting out its reopen cooldown; it stays in the list, because the moment it drops out is
/// the moment somebody wants to see it.</param>
/// <param name="Inferences">Inferences this device has completed.</param>
/// <param name="Failures">Calls that returned an error, most often the device going away mid-run.</param>
/// <param name="BusySeconds">Seconds spent inside the device call.</param>
public readonly record struct DetectorDevice(
    string Path, bool Healthy, long Inferences, long Failures, double BusySeconds);

/// <summary>
/// Finds objects in a single frame.
///
/// Deliberately narrower than <see cref="IVisionInferenceRunner"/>: one frame in, a list out, no
/// prompt and no free text. The two answer different questions — a detector reports what *is*
/// there, a vision-language model describes what is *happening* — and it is the detector's answer
/// that decides whether the expensive one runs at all.
///
/// One instance serves every camera, so implementations own their own concurrency and must be safe
/// to call from several camera loops at once. It holds no per-camera *settings*: the class allowlist
/// and the confidence floor a *particular* camera wants are applied downstream by
/// <see cref="ObjectEventPolicy"/>, because one shared detector cannot hold them. What an
/// implementation applies is its own configured floor, as a cheap way to keep obvious noise out of
/// the list it returns.
///
/// The one thing that does vary per camera is the input shape, and it is derived rather than held —
/// <see cref="InputFor"/> answers from a frame size, and the caller keeps the answer. That is what
/// lets one detector serve a 16:9 driveway and a 3:4 doorbell without either of them being fitted
/// into the other's shape.
/// </summary>
public interface IObjectDetector : IDisposable
{
    /// <summary>A short name for the backing model and execution path, for logs and diagnostics —
    /// e.g. <c>"onnx/cpu yolo26n"</c>. Never parsed.</summary>
    string Description { get; }

    /// <summary>
    /// The buffer shape this backend wants for frames of a given size. Callers that already hold
    /// pixels build it from this rather than assuming; see <see cref="DetectorInput"/> for why that
    /// is not optional.
    ///
    /// <para><b>Asked per camera, not once per process.</b> A backend free to choose returns a shape
    /// at the frame's own aspect, which is the difference between a 3:4 doorbell spending 55% of its
    /// input on grey padding and spending 4%. A backend with a shape baked into its weights returns
    /// that shape whatever it is handed.</para>
    ///
    /// <para>Resolve it once, when a camera's frame size is first known, and hold it: the answer
    /// cannot change while the stream's dimensions do not, and the buffer sized from it is reused
    /// for every frame and every crop.</para>
    /// </summary>
    /// <param name="frameWidth">The camera's frame width in pixels, not a crop's.</param>
    /// <param name="frameHeight">The camera's frame height in pixels.</param>
    DetectorInput InputFor(int frameWidth, int frameHeight);

    /// <summary>
    /// How many detections this backend will run at once. One unless it says otherwise.
    ///
    /// <para>Declared rather than inferred because <see cref="InferenceBudget"/> has to time the
    /// backend the way it will be used: timing a pool of four one call at a time reports a quarter
    /// of the host's capacity, and the scheduler then sheds work the machine could have done.</para>
    /// </summary>
    int Concurrency => 1;

    /// <summary>
    /// What this backend is able to report about its own condition.
    ///
    /// <para>Defaulted to "everything healthy, nothing dropped, no devices", which is the truthful
    /// answer for a backend that cannot lose capacity. Only a backend whose lanes are hardware has
    /// anything else to say — and the null device list is what keeps the status page's accelerator
    /// meter off a host running on its processor, rather than showing it an empty one.</para>
    /// </summary>
    DetectorHealth Health => new(Concurrency, Concurrency, 0, null);

    /// <summary>
    /// The share of measured capacity this backend's work should be scheduled to.
    ///
    /// <para><see cref="InferenceBudget.DefaultUtilisation"/> is half, and half is right for a CPU
    /// backend: detection shares those cores with an ffmpeg per camera, a vision model that wants
    /// seconds at a time, and a database being written to, so scheduling to the last inference the host
    /// can manage makes detection the thing that delays everything else.</para>
    ///
    /// <para><b>An accelerator shares none of that</b>, so spending only half of a device that nothing
    /// else is competing for is a straightforward waste — and on a small host it is the difference
    /// between covering the cameras and not. A backend that owns its silicon overrides this upward; the
    /// headroom it keeps is for its own jitter, not for anybody else's cores.</para>
    /// </summary>
    double Utilisation => InferenceBudget.DefaultUtilisation;

    /// <summary>
    /// Runs on pixels already in one of this detector's <see cref="InputFor"/> forms. Frames
    /// arrive from ffmpeg already scaled, so there is no decode, resize or letterbox to pay here —
    /// and no lossy JPEG round trip to cost a small distant object its detail.
    /// </summary>
    /// <param name="prepared">A buffer matching <paramref name="input"/> exactly. Valid only for the
    /// duration of the call; implementations must not retain it.</param>
    /// <param name="input">The shape <paramref name="prepared"/> was built to. Passed rather than
    /// assumed, because two cameras on one detector are routinely at different shapes and a buffer
    /// read at the wrong one is not an error anything can detect — the bytes are valid, the picture
    /// they describe is sheared.</param>
    /// <param name="frame">How to map a box found in <paramref name="prepared"/> back onto the
    /// picture it was cut from.</param>
    Task<IReadOnlyList<DetectedObject>> DetectPreparedAsync(
        ReadOnlyMemory<byte> prepared,
        DetectorInput input,
        PreparedFrame frame,
        CancellationToken cancellationToken);
}

/// <summary>How a detector's input buffer is laid out.</summary>
public enum DetectorLayout
{
    /// <summary>Interleaved 8-bit channels, one byte per sample — what an Edge TPU, an RKNN encoder
    /// and most quantised runtimes take.</summary>
    Uint8Nhwc,

    /// <summary>Planar 32-bit floats, one channel plane after another — what an ONNX detector
    /// exported from the usual toolchains takes.</summary>
    FloatNchw,
}

/// <summary>
/// The buffer one detector wants: how big, in what layout, and scaled how.
///
/// <para>Nothing outside a detector implementation has to know what its model eats. The sizes and
/// layouts genuinely differ — 640² planar float for the ONNX YOLO on CPU, 320² interleaved uint8
/// for an Edge TPU — and a caller hardcoding either silently produces garbage the day the backend
/// changes. Preparation is driven from these values, so adding a backend is a new implementation
/// rather than an edit to the frame path.</para>
///
/// <para><paramref name="Scale"/> is applied to each sample on the way in; the 1/255 an ONNX export
/// expects is a property of the model, not of the picture, so it belongs here rather than in the code
/// that cuts frames up.</para>
/// </summary>
public readonly record struct DetectorInput(
    int Width,
    int Height,
    DetectorLayout Layout,
    float Scale = 1f / 255f)
{
    /// <summary>Bytes one prepared buffer occupies.</summary>
    public int ByteLength => Layout switch
    {
        DetectorLayout.Uint8Nhwc => Width * Height * 3,
        DetectorLayout.FloatNchw => Width * Height * 3 * sizeof(float),
        _ => throw new NotSupportedException($"Unknown detector layout '{Layout}'."),
    };
}

/// <summary>
/// Everything needed to put a box found in a prepared buffer back where it belongs in the frame it
/// came from.
///
/// <para>Carried alongside the pixels rather than recomputed: the geometry is established where the
/// frame is cut and needed again at the far end of inference. Kept as loose locals, one eventually
/// goes missing, and the symptom is boxes a few percent off in a direction that depends on the
/// source aspect ratio, with nothing failing.</para>
///
/// <para><paramref name="CropX"/> and <paramref name="CropY"/> are where this buffer's picture starts
/// within the full frame, in full-frame pixels. They are zero for a whole frame and non-zero for a
/// region crop, which is the case that makes a small distant object large enough to detect.</para>
/// </summary>
/// <param name="Scale">Prepared pixels per source pixel.</param>
/// <param name="PadX">Padding on the left edge of the prepared buffer, in prepared pixels.</param>
/// <param name="PadY">Padding on the top edge, in prepared pixels.</param>
/// <param name="CropWidth">Width of the region cut from the frame, in frame pixels.</param>
/// <param name="CropHeight">Height of the region cut from the frame, in frame pixels.</param>
/// <param name="FrameWidth">Full frame width, which boxes are reported as a fraction of.</param>
/// <param name="FrameHeight">Full frame height.</param>
public readonly record struct PreparedFrame(
    float Scale,
    int PadX,
    int PadY,
    int CropX,
    int CropY,
    int CropWidth,
    int CropHeight,
    int FrameWidth,
    int FrameHeight)
{
    /// <summary>A whole frame with no crop and no padding — the case where the prepared buffer is the
    /// picture, stretched to the model's input.</summary>
    public static PreparedFrame Whole(int frameWidth, int frameHeight, float scale) =>
        new(scale, 0, 0, 0, 0, frameWidth, frameHeight, frameWidth, frameHeight);

    /// <summary>
    /// Maps a box in prepared-buffer pixels to a fraction of the whole frame.
    ///
    /// Clamped to the frame: an object at the edge routinely produces a box that runs a little off
    /// it, and a consumer drawing that literally would draw outside the picture.
    /// </summary>
    public BoundingBox ToFrame(float x, float y, float width, float height)
    {
        float left = CropX + ((x - PadX) / Scale);
        float top = CropY + ((y - PadY) / Scale);
        float right = CropX + ((x + width - PadX) / Scale);
        float bottom = CropY + ((y + height - PadY) / Scale);

        left = Math.Clamp(left, 0, FrameWidth);
        top = Math.Clamp(top, 0, FrameHeight);
        right = Math.Clamp(right, 0, FrameWidth);
        bottom = Math.Clamp(bottom, 0, FrameHeight);

        return new BoundingBox(
            left / FrameWidth,
            top / FrameHeight,
            (right - left) / FrameWidth,
            (bottom - top) / FrameHeight);
    }
}
