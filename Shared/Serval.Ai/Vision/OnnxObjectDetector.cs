using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;

namespace Serval.Ai;

/// <summary>
/// Object detection through ONNX Runtime, over a YOLO-style detection head.
///
/// Only session management lives here. Everything that decides what a box *means* —
/// <see cref="FramePreparer"/> for the geometry, <see cref="YoloEndToEndPostprocessor"/> for the
/// decode — is pure and tested without weights, because that is where the errors are. What is left
/// is the part that genuinely cannot be tested without a model on disk.
///
/// One instance serves every camera, with inference bounded by a semaphore. Not for thread safety
/// — <c>InferenceSession.Run</c> is thread-safe — but for memory: ONNX Runtime's arena allocator
/// grows to the high-water mark of *concurrent* allocations and never gives it back, so eight
/// cameras firing together would pin that peak for the life of the process. It also makes the CPU
/// cost statable as a flat <see cref="DetectionOptions.NumThreads"/> per lane rather than a product
/// of it and how many cameras happened to overlap.
/// </summary>
public sealed class OnnxObjectDetector : IObjectDetector
{
    private static int _resolverInstalled;

    private readonly DetectionOptions _options;
    private readonly ILogger _logger;
    // One session per concurrent inference, handed out and returned rather than shared. The gate
    // bounds how many are out at once; the bag is which. A rent can never fail, because the gate is
    // sized to the bag.
    private readonly InferenceSession[] _sessions;
    private readonly ConcurrentBag<InferenceSession> _idle;
    private readonly SemaphoreSlim _gate;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly string[] _labels;
    private readonly IDetectionPostprocessor _postprocessor;

    // The shape the export baked in, or null when it left the spatial axes dynamic and every camera
    // gets one cut to its own aspect. Null is the interesting case; a value here pins the whole host
    // to one shape, which is what an accelerator's ahead-of-time compilation requires.
    private readonly (int Width, int Height)? _fixedShape;
    private readonly int _pixelBudget;
    private readonly int _maxDetections;

    private bool _warnedUnknownClass;

    private OnnxObjectDetector(
        DetectionOptions options,
        InferenceSession[] sessions,
        string inputName,
        string outputName,
        string[] labels,
        (int Width, int Height)? fixedShape,
        int pixelBudget,
        int maxDetections,
        string description,
        ILogger logger)
    {
        _options = options;
        _sessions = sessions;
        _idle = new ConcurrentBag<InferenceSession>(sessions);
        _gate = new SemaphoreSlim(sessions.Length, sessions.Length);
        _inputName = inputName;
        _outputName = outputName;
        _labels = labels;
        _postprocessor = new YoloEndToEndPostprocessor(labels);
        _fixedShape = fixedShape;
        _pixelBudget = pixelBudget;
        _maxDetections = maxDetections;
        _logger = logger;
        Description = description;
    }

    public string Description { get; }

    /// <summary>Detections dropped because the semaphore was still held when the next frame
    /// arrived. Non-zero means the host cannot keep up with
    /// <see cref="DetectionOptions.MaxFps"/> and that setting should come down.</summary>
    public long DroppedWhileBusy { get; private set; }

    /// <param name="provider">The execution provider to request, from the second half of
    /// <see cref="DetectionOptions.Device"/>. Passed in rather than read here so that
    /// <see cref="ObjectDetectorFactory"/> stays the only thing that interprets that setting, and no
    /// two readers can disagree about what was selected.</param>
    public static OnnxObjectDetector Create(
        DetectionOptions options,
        string provider,
        ILogger<OnnxObjectDetector> logger)
    {
        if (!File.Exists(options.ModelPath))
        {
            throw new FileNotFoundException($"Detection model not found: {options.ModelPath}", options.ModelPath);
        }

        // Read before any native library is touched, so a bad labels file is a plain managed failure
        // rather than one that lands after a P/Invoke resolver has been installed for the process.
        //
        // Via LabelFile rather than by filtering blank lines away: dropping a gap shifts every index
        // after it, which renames most of the vocabulary and cannot be detected downstream.
        LabelFile labelFile = LabelFile.Load(options.LabelsPath);
        string[] labels = [.. labelFile.Labels];

        logger.LogInformation(
            "Detection labels: {Count} classes from '{Path}', {Format} format, {Placeholders} gap(s) "
            + "filled.",
            labels.Length, options.LabelsPath, labelFile.Format, labelFile.PlaceholderCount);

        InstallNativeResolver(options, logger);

        SessionOptions sessionOptions = BuildSessionOptions(options, provider, logger, out string running);

        // One session per concurrent inference. Each carries its own copy of the weights and its own
        // arena, which is the cost of the throughput — and is also what keeps the peak memory a
        // number that can be stated in advance rather than whatever concurrency happened to peak at.
        int concurrency = Math.Max(options.EffectiveConcurrency, 1);
        var sessions = new InferenceSession[concurrency];

        try
        {
            for (int i = 0; i < concurrency; i++)
            {
                sessions[i] = new InferenceSession(options.ModelPath, sessionOptions);
            }

            InferenceSession session = sessions[0];
            (string inputName, (int Width, int Height)? fixedShape) = ValidateInput(session);
            (string outputName, int maxDetections) = ValidateOutput(session);

            string description = $"onnx/{running} {Path.GetFileNameWithoutExtension(options.ModelPath)}";

            // The resolved shape rather than the configured budget, and the reason it resolved that
            // way, because a fixed-shape export silently ignores the budget and an operator who has
            // just set one deserves to see that it did nothing.
            string shape = fixedShape is (int width, int height)
                ? $"fixed {width}x{height}"
                : $"per camera, about {options.InputPixels:N0} px "
                    + $"({DetectorShapes.Ladder(options.InputPixels).Count} shapes)";

            logger.LogInformation(
                "Object detector ready: {Description} — end-to-end head, input {Shape}, "
                + "{Classes} classes, up to {MaxDetections} detections, {Concurrency} at a time "
                + "x {Threads} thread(s). ONNX Runtime {Runtime}.",
                description, shape, labels.Length,
                maxDetections > 0 ? maxDetections.ToString() : "dynamic",
                concurrency,
                options.NumThreads,
                OrtEnv.Instance().GetVersionString());

            return new OnnxObjectDetector(
                options, sessions, inputName, outputName, labels, fixedShape, options.InputPixels,
                maxDetections, description, logger);
        }
        catch
        {
            foreach (InferenceSession? session in sessions)
            {
                session?.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Points <c>DllImport("onnxruntime")</c> at an explicit library when one is configured.
    ///
    /// Default probing finds the runtime that ships beside the app, which carries the CPU provider
    /// only. Anything else — a GPU build, a vendor build — is a different <c>.so</c> at the same
    /// managed API, so it belongs to the image rather than to the package graph. Installed once per
    /// process: a resolver cannot be replaced once set, and the second registration throws.
    /// </summary>
    private static void InstallNativeResolver(DetectionOptions options, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(options.NativeLibraryPath))
        {
            return;
        }

        if (Interlocked.Exchange(ref _resolverInstalled, 1) != 0)
        {
            return;
        }

        string path = options.NativeLibraryPath;

        if (!File.Exists(path))
        {
            logger.LogWarning(
                "Detection NativeLibraryPath '{Path}' does not exist; falling back to default probing.",
                path);
            return;
        }

        NativeLibrary.SetDllImportResolver(
            typeof(InferenceSession).Assembly,
            (name, assembly, search) => name == "onnxruntime"
                ? NativeLibrary.Load(path)
                : IntPtr.Zero);

        logger.LogInformation("ONNX Runtime native library pinned to {Path}.", path);
    }

    /// <param name="requested">What <see cref="DetectionOptions.Device"/> asked for.</param>
    /// <param name="running">What was actually installed, which is <c>cpu</c> whenever the requested
    /// provider is not in the loaded runtime. The two are separate so the startup line and
    /// <see cref="Description"/> report the provider in force rather than the one hoped for.</param>
    private static SessionOptions BuildSessionOptions(
        DetectionOptions options,
        string requested,
        ILogger logger,
        out string running)
    {
        var sessionOptions = new SessionOptions
        {
            // A per-session pool. Since inference is serialised, this is the whole CPU budget for
            // detection rather than a per-camera one.
            IntraOpNumThreads = Math.Max(1, options.NumThreads),
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        running = "cpu";

        if (requested is "cpu" or "")
        {
            return sessionOptions;
        }

        // A provider that was not compiled into the loaded runtime throws rather than degrading,
        // and the runtime the build ships carries none of them. Warn and stay on CPU: an operator
        // who asked for a GPU and silently got one core deserves to be told, but a detector that
        // refuses to start is worse than a slow one.
        try
        {
            switch (requested)
            {
                case "cuda":
                    sessionOptions.AppendExecutionProvider_CUDA();
                    break;
                case "openvino":
                    sessionOptions.AppendExecutionProvider_OpenVINO();
                    break;
                case "tensorrt":
                    sessionOptions.AppendExecutionProvider_Tensorrt();
                    break;
                default:
                    // Unreachable through configuration — ObjectDetectorFactory rejects a device it
                    // does not know before this is called. Kept so a new device name added there
                    // without an arm here degrades rather than silently running on the CPU while
                    // claiming otherwise.
                    logger.LogWarning(
                        "Unknown detection execution provider '{Provider}'; using CPU.", requested);
                    return sessionOptions;
            }

            running = requested;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Detection:Device asked for the '{Provider}' execution provider, which is not in this "
                + "ONNX Runtime build ({Message}); using CPU. A non-CPU provider needs a runtime built "
                + "with it, selected via Detection:NativeLibraryPath.",
                requested, ex.Message);
        }

        return sessionOptions;
    }

    /// <summary>
    /// Reads what the export will accept: a shape it baked in, or nothing when it left the spatial
    /// axes dynamic.
    ///
    /// <para>Throws rather than warns: a present-but-wrong model is a real fault, unlike an absent
    /// one, and everything checked here produces silent nonsense when it is wrong — a non-RGB input
    /// means the letterbox packs a buffer the graph will misread.</para>
    ///
    /// <para><b>A dynamic export is the one worth having on a general-purpose host.</b> Its axes come
    /// through as -1, and every camera is then given a shape at its own aspect by
    /// <see cref="DetectorShapes"/> — which is free, measured: against a fixed export of the same
    /// weights at the same shape, a dynamic one runs within noise and returns bit-identical boxes.
    /// What it buys is that a 3:4 doorbell and a 32:9 panoramic stop being letterboxed into a shape
    /// chosen for something else.</para>
    ///
    /// <para>A fixed export still loads, and pins every camera to its one shape. That is not a
    /// courtesy: RKNN and Edge TPU compile ahead of time against a fixed shape, and the TensorRT and
    /// OpenVINO providers build an engine per shape, so a single-camera SBC or an accelerator wants
    /// exactly this.</para>
    /// </summary>
    private static (string Name, (int Width, int Height)? Fixed) ValidateInput(
        InferenceSession session)
    {
        KeyValuePair<string, NodeMetadata> input = session.InputMetadata.First();
        int[] dims = input.Value.Dimensions;

        if (dims.Length != 4)
        {
            throw new InvalidOperationException(
                $"Detection model input '{input.Key}' has {dims.Length} dimensions; expected 4 "
                + "([1, 3, height, width]).");
        }

        if (dims[1] != 3)
        {
            throw new InvalidOperationException(
                $"Detection model input '{input.Key}' expects {dims[1]} channels; only 3-channel RGB "
                + "is supported.");
        }

        // One axis fixed and the other dynamic is rejected rather than half-honoured. It is not a
        // shape anything here can satisfy sensibly — the free axis would have to be chosen against a
        // budget the fixed one already spent — and it is far more likely to be a mis-specified
        // export than a deliberate one.
        if (dims[2] <= 0 != (dims[3] <= 0))
        {
            throw new InvalidOperationException(
                $"Detection model input '{input.Key}' has one spatial axis fixed and one dynamic "
                + $"([{string.Join(", ", dims)}]). Export with both dynamic, or neither.");
        }

        if (dims[2] <= 0)
        {
            return (input.Key, null);
        }

        int height = dims[2];
        int width = dims[3];

        // A detection backbone downsamples by its stride, so an axis that is not a multiple of it
        // either fails to build the head's grid or silently loses the remainder. Caught here because
        // the alternative symptom is boxes that are subtly short along one axis with nothing
        // reporting a fault.
        if (width % DetectorShapes.Stride != 0 || height % DetectorShapes.Stride != 0)
        {
            throw new InvalidOperationException(
                $"Detection model input '{input.Key}' is {width}x{height}; both axes must be "
                + $"multiples of {DetectorShapes.Stride}.");
        }

        return (input.Key, (width, height));
    }

    /// <summary>
    /// Checks the output is the end-to-end head, and says so in terms of the mistake that actually
    /// gets made: exporting the one-to-many head instead, which is what every pre-YOLO26 model is
    /// and what <c>end2end=False</c> asks for. Both are rank 3 and differ only in the last axis, so
    /// without naming it the error reads as an unrelated shape problem.
    ///
    /// <para>A labels file that disagrees with the weights is the fault worth catching here, because
    /// it produces confidently wrong names forever with no other symptom. It cannot be: this layout
    /// carries no class count, so there is nothing to compare the file's length against. The only
    /// available signal is a class index that decodes past the end of the file, counted by
    /// <see cref="YoloEndToEndPostprocessor.Decode"/> and warned about in <see cref="Detect"/> —
    /// which a file of the right length in the wrong order never trips.</para>
    /// </summary>
    private static (string Name, int MaxDetections) ValidateOutput(InferenceSession session)
    {
        KeyValuePair<string, NodeMetadata> output = session.OutputMetadata.First();
        int[] dims = output.Value.Dimensions;

        // The last axis is required to be exactly the stride, never merely non-contradictory. A
        // one-to-many export routinely leaves its anchor count dynamic, which arrives here as -1,
        // and treating that as "cannot tell, allow it" lets the wrong head load and postpones the
        // complaint to the first frame — where it arrives as a bare shape mismatch, with none of
        // the diagnosis below, from a detector that has already announced itself as end-to-end.
        if (dims.Length != 3 || dims[2] != YoloEndToEndPostprocessor.Stride)
        {
            throw new InvalidOperationException(
                $"Detection model output '{output.Key}' has shape [{string.Join(", ", dims)}]; "
                + $"expected [1, detections, {YoloEndToEndPostprocessor.Stride}] — the end-to-end head, "
                + "whose rows are x1, y1, x2, y2, score, class. A rank-3 output with some other "
                + "last axis is the one-to-many head, which this does not decode: re-export "
                + "without end2end=False, and note that no pre-YOLO26 model has the end-to-end "
                + "head at all.");
        }

        // A dimension comes through as -1 when the export left that axis dynamic. The real value is
        // read back off the output tensor on every run; this one is for the startup log, and for
        // rejecting the wrong head above while the model is still being loaded rather than on the
        // first frame.
        return (output.Key, dims[1]);
    }

    /// <summary>
    /// The buffer this model takes for a camera of the given frame size: planar floats, scaled the
    /// way the export expects, at the shape nearest that camera's aspect within the configured pixel
    /// budget — or at the export's own shape when it fixed one.
    ///
    /// Read by whoever prepares frames, so the shape reaches the crop step without anybody restating
    /// it. That indirection is what lets the aspect vary per camera without a single edit on the
    /// preparing side.
    /// </summary>
    public DetectorInput InputFor(int frameWidth, int frameHeight)
    {
        (int width, int height) = _fixedShape
            ?? DetectorShapes.Fit(frameWidth, frameHeight, _pixelBudget);

        return new DetectorInput(width, height, DetectorLayout.FloatNchw, 1f / 255f);
    }

    /// <inheritdoc />
    public int Concurrency => _sessions.Length;

    public async Task<IReadOnlyList<DetectedObject>> DetectPreparedAsync(
        ReadOnlyMemory<byte> prepared,
        DetectorInput input,
        PreparedFrame frame,
        CancellationToken cancellationToken)
    {
        if (prepared.Length < input.ByteLength)
        {
            throw new ArgumentException(
                $"Prepared buffer is {prepared.Length} bytes; {input.Width}x{input.Height} needs "
                + $"{input.ByteLength}.",
                nameof(prepared));
        }

        // A fixed-shape export cannot take anything else, and the failure it would otherwise give is
        // an ONNX Runtime shape error from inside Run with nothing naming the camera that caused it.
        if (_fixedShape is (int fixedWidth, int fixedHeight)
            && (input.Width != fixedWidth || input.Height != fixedHeight))
        {
            throw new ArgumentException(
                $"This model has a fixed {fixedWidth}x{fixedHeight} input; a buffer prepared at "
                + $"{input.Width}x{input.Height} cannot be run against it.",
                nameof(input));
        }

        if (!await EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        _idle.TryTake(out InferenceSession? session);

        try
        {
            // The caller already produced planar floats at the model's own input size, so there is
            // nothing to decode and nothing to resize. Reinterpreting the bytes is the whole of the
            // preparation left, which is the reason this path exists.
            ReadOnlySpan<float> floats = MemoryMarshal.Cast<byte, float>(prepared.Span);

            return Detect(session!, floats[..(input.Width * input.Height * 3)], input, frame);
        }
        finally
        {
            _idle.Add(session!);
            _gate.Release();
        }
    }

    /// <summary>
    /// Takes the inference slot, or reports that this frame is being dropped.
    ///
    /// Bounded rather than indefinite: a frame that cannot be started before the next one arrives is
    /// stale, and queueing it would build exactly the backlog of old pictures that the single-slot
    /// frame handoff exists to avoid.
    /// </summary>
    private async Task<bool> EnterAsync(CancellationToken cancellationToken)
    {
        TimeSpan wait = _options.MaxFps > 0
            ? TimeSpan.FromSeconds(1.0 / _options.MaxFps)
            : TimeSpan.FromSeconds(1);

        if (await _gate.WaitAsync(wait, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        DroppedWhileBusy++;
        _logger.LogDebug(
            "Detection skipped: still busy after {Wait:0.##}s ({Dropped} dropped so far).",
            wait.TotalSeconds, DroppedWhileBusy);
        return false;
    }

    private IReadOnlyList<DetectedObject> Detect(
        InferenceSession session,
        ReadOnlySpan<float> pixels,
        DetectorInput input,
        PreparedFrame frame)
    {
        long started = Stopwatch.GetTimestamp();

        var pixelTensor = new DenseTensor<float>(
            pixels.ToArray(),
            [1, 3, input.Height, input.Width]);

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = session.Run(
            [NamedOnnxValue.CreateFromTensor(_inputName, pixelTensor)],
            [_outputName]);

        Tensor<float> tensor = results.First().AsTensor<float>();

        // From the tensor rather than from the metadata: an export routinely leaves the detection
        // count dynamic, in which case the declared shape carries -1 and only the actual result
        // knows how many rows came back.
        ReadOnlySpan<int> shape = tensor.Dimensions;
        int detections = shape.Length == 3 ? shape[1] : _maxDetections;

        // Cheap, and it catches a graph that disagrees with the metadata the load-time check read.
        if (shape.Length != 3 || shape[2] != YoloEndToEndPostprocessor.Stride)
        {
            throw new InvalidOperationException(
                $"Detection model returned shape [{string.Join(", ", shape.ToArray())}]; expected "
                + $"[1, detections, {YoloEndToEndPostprocessor.Stride}].");
        }

        IReadOnlyList<DetectedObject> found = _postprocessor.Decode(
            [new DetectorOutput(_outputName, tensor.ToArray(), [1, detections, YoloEndToEndPostprocessor.Stride])],
            input,
            frame,
            _options.ScoreThreshold,
            out DecodeDiagnostics diagnostics);

        // Once, not per frame: this is a property of the model-and-labels pair, so it will be true
        // of every frame from here on and repeating it would only bury the first one.
        if (diagnostics.UnknownClassRows > 0 && !_warnedUnknownClass)
        {
            _warnedUnknownClass = true;
            _logger.LogWarning(
                "Detection dropped {Rows} row(s) whose class index was outside the {Count} label(s) "
                + "in '{LabelsPath}'. The labels file most likely does not match the weights, in "
                + "which case the classes that *are* in range are being given the wrong names.",
                diagnostics.UnknownClassRows, _labels.Length, _options.LabelsPath);
        }

        _logger.LogDebug(
            "Detected {Count} object(s) in {Elapsed:0.#}ms.",
            found.Count,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        return found;
    }

    public void Dispose()
    {
        foreach (InferenceSession session in _sessions)
        {
            session.Dispose();
        }

        _gate.Dispose();
    }
}
