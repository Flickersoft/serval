using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Serval.Ai;

/// <summary>
/// Object detection on Coral Edge TPUs, through the TFLite C API and libedgetpu.
///
/// <para>A sibling of <see cref="OnnxObjectDetector"/> and deliberately the same shape: a static
/// factory that validates the model and throws rather than degrading, a pool of lanes handed out and
/// returned, and a semaphore that bounds how many are out. What differs is what a lane <em>is</em> — a
/// physical device rather than a thread budget — and everything that follows from that.</para>
///
/// <para><b>One lane per device, and the device count is the concurrency.</b> An Edge TPU runs one
/// inference at a time and a TFLite interpreter is not thread-safe, so a second lane on the same device
/// would only queue. <see cref="DetectionOptions.MaxConcurrency"/> is therefore ignored here, and says
/// so at startup when it has been set.</para>
///
/// <para><b>Lanes need not be equal.</b> Two USB Corals split across USB generations measured 64.2 and
/// 29.7 inferences a second on the same model, and because lanes are rented on demand the faster device
/// returns to the bag sooner and simply serves more frames. No caller has to know. The part that needed
/// fixing for this was <see cref="InferenceBudget"/>, which used to credit an asymmetric pool with twice
/// its slowest lane rather than the sum.</para>
///
/// <para><b>Losing a device costs throughput, not function.</b> A lane that fails is marked unhealthy
/// and retried on a cooldown; work goes to whatever lanes remain. No lane is ever removed from the bag,
/// so the invariant that a rent can never fail while the gate admits is preserved.</para>
/// </summary>
public sealed class EdgeTpuObjectDetector : IObjectDetector
{
    /// <summary>How long a failed device is left alone before a rebuild is attempted.
    ///
    /// Every delegate creation resets the USB device — observable as
    /// <c>reset SuperSpeed USB device</c> in <c>dmesg</c>, once per open — so retrying per frame would
    /// reset the bus per frame and turn a sick device into a sick bus.</summary>
    private static readonly TimeSpan ReopenCooldown = TimeSpan.FromSeconds(5);

    /// <summary>Attempts when first opening a device.
    ///
    /// Not defensive padding: a Coral enumerates pre-firmware, and libedgetpu uploads its firmware on
    /// first open, after which the device re-enumerates and is briefly un-openable. Observed
    /// reproducibly — the first open of the second device failed while the bus resettled, and succeeded
    /// on retry. The error is an opaque delegate-creation failure whatever the real cause.</summary>
    private const int OpenAttempts = 6;

    private static int _resolverInstalled;

    /// <summary>One device, and everything bound to it.</summary>
    private sealed class Lane
    {
        public required string DevicePath { get; init; }

        public required int DeviceType { get; init; }

        public IntPtr Model { get; set; }

        public IntPtr Delegate { get; set; }

        public IntPtr Interpreter { get; set; }

        public IntPtr InputTensor { get; set; }

        public IntPtr[] OutputTensors { get; set; } = [];

        /// <summary>Per-output scratch, sized at load. Dequantising into a fresh array per output per
        /// frame would be megabytes of garbage a second on a path that runs tens of times a second.
        /// </summary>
        public float[][] Scratch { get; set; } = [];

        public int[][] OutputShapes { get; set; } = [];

        public bool Healthy { get; set; } = true;

        public DateTimeOffset NextReopenAt { get; set; } = DateTimeOffset.MinValue;

        /// <summary>Inferences completed, calls that returned an error, and <see cref="Stopwatch"/>
        /// ticks spent inside <c>TfLiteInterpreterInvoke</c>.
        ///
        /// <para>Fields rather than properties, and written through <see cref="Interlocked"/> where
        /// nothing else on this class is. A lane is rented exclusively so the writes cannot race each
        /// other — but the status page reads them from the vitals sampler's thread while a camera loop
        /// is writing, and a torn 64-bit read would surface as one nonsense busy percentage rather
        /// than as anything that fails.</para>
        /// </summary>
        public long Inferences;

        /// <inheritdoc cref="Inferences"/>
        public long Failures;

        /// <inheritdoc cref="Inferences"/>
        public long BusyTicks;
    }

    private readonly DetectionOptions _options;
    private readonly ILogger _logger;
    private readonly Lane[] _lanes;
    private readonly ConcurrentBag<Lane> _idle;
    private readonly SemaphoreSlim _gate;
    private readonly IDetectionPostprocessor _postprocessor;
    private readonly int _width;
    private readonly int _height;
    private readonly object _disposeGate = new();

    private bool _warnedUnknownClass;
    private bool _disposed;

    private EdgeTpuObjectDetector(
        DetectionOptions options,
        Lane[] lanes,
        IDetectionPostprocessor postprocessor,
        int width,
        int height,
        string description,
        ILogger logger)
    {
        _options = options;
        _lanes = lanes;
        _idle = new ConcurrentBag<Lane>(lanes);
        _gate = new SemaphoreSlim(lanes.Length, lanes.Length);
        _postprocessor = postprocessor;
        _width = width;
        _height = height;
        _logger = logger;
        Description = description;
    }

    public string Description { get; }

    public int Concurrency => _lanes.Length;

    /// <summary>Detections dropped because every lane was busy when the next frame arrived.</summary>
    public long DroppedWhileBusy { get; private set; }

    /// <summary>Lanes currently able to run. Below <see cref="Concurrency"/> means a device was lost and
    /// throughput has dropped with it.</summary>
    private int HealthyLanes => _lanes.Count(static lane => lane.Healthy);

    /// <inheritdoc/>
    public DetectorHealth Health
    {
        get
        {
            int healthy = HealthyLanes;

            string? degraded = healthy == _lanes.Length
                ? null
                : healthy == 0
                    ? "every Edge TPU stopped responding"
                    : $"{_lanes.Length - healthy} of {_lanes.Length} Edge TPU(s) stopped responding: "
                        + string.Join(
                            ", ",
                            _lanes.Where(static lane => !lane.Healthy)
                                .Select(static lane => Path.GetFileName(lane.DevicePath)));

            return new DetectorHealth(
                _lanes.Length, healthy, DroppedWhileBusy, degraded, AcceleratorLabel, Devices());
        }
    }

    /// <summary>What a person should call these devices. Fixed, because this backend drives exactly
    /// one kind of hardware — a PCIe Coral and a USB one are the same accelerator on different
    /// ends of a wire, and the per-device rows already carry which is which.</summary>
    private const string AcceleratorLabel = "Edge TPU";

    /// <summary>
    /// Every device and what it has done since startup, named by the same short form the startup line
    /// uses — <c>2-2</c>, not the whole sysfs path.
    ///
    /// <para><b>Sick lanes are included.</b> They are never removed from the pool, and the moment one
    /// drops out is exactly the moment somebody is looking for it. A list that shrank would report a
    /// halved accelerator as a smaller healthy one.</para>
    ///
    /// <para>The totals are cumulative and this reads them with no reset, so it is safe to call from
    /// anywhere and as often as anyone likes — unlike the server-side counters it feeds, which close
    /// their window when read.</para>
    /// </summary>
    private DetectorDevice[] Devices() =>
    [
        .. _lanes.Select(static lane => new DetectorDevice(
            Path.GetFileName(lane.DevicePath),
            lane.Healthy,
            Interlocked.Read(ref lane.Inferences),
            Interlocked.Read(ref lane.Failures),
            Interlocked.Read(ref lane.BusyTicks) / (double)Stopwatch.Frequency)),
    ];

    /// <summary>
    /// Higher than the CPU default, because the reason for that default does not apply here.
    ///
    /// <para>Half exists so detection does not starve ffmpeg, the vision model and the database of the
    /// cores they share. An Edge TPU shares none of them: what it competes for is USB bandwidth and its
    /// own thermal headroom. Spending only half of it would be waste, and on a four-core host it is the
    /// difference between covering ten cameras and covering five.</para>
    ///
    /// <para>0.85 rather than 1.0 because a USB device does have jitter — a bus reset, a thermal dip —
    /// and the scheduler should not be sized to a best case it cannot always deliver.</para>
    /// </summary>
    public double Utilisation => 0.85;

    /// <summary>
    /// Opens every attached Edge TPU and validates the model against it.
    ///
    /// <para>Order matters: files first, natives last. A missing model or labels file is a plain managed
    /// failure that needs no library loaded, which is also what lets the factory's rejection paths be
    /// tested on a machine with no Coral runtime at all.</para>
    /// </summary>
    /// <exception cref="FileNotFoundException">Model or labels missing.</exception>
    /// <exception cref="InvalidOperationException">No device, or a model this backend cannot run.</exception>
    public static EdgeTpuObjectDetector Create(
        DetectionOptions options,
        ILogger<EdgeTpuObjectDetector> logger)
    {
        if (!File.Exists(options.ModelPath))
        {
            throw new FileNotFoundException(
                $"EdgeTPU model not found: {options.ModelPath}. This device needs a model built by "
                + "edgetpu_compiler; Detection:ModelPath is shared with the ONNX runtime, so it has to "
                + "name the compiled .tflite when Detection:Device is tflite-edgetpu.",
                options.ModelPath);
        }

        LabelFile labelFile = LabelFile.Load(options.LabelsPath);
        string[] labels = [.. labelFile.Labels];

        InstallNativeResolver(options, logger);

        (string Path, int Type)[] devices = ListDevices();

        if (devices.Length == 0)
        {
            throw new InvalidOperationException(
                "No Edge TPU found. Check that the device is attached (`lsusb` shows 18d1:9302 once "
                + "its firmware is loaded, or 1a6e:089a before), that the container was given both "
                + "`--device /dev/bus/usb` and a device cgroup rule for major 189, and that the udev "
                + "rule granting access is in place. A per-device path such as /dev/bus/usb/001/004 "
                + "does not survive the re-enumeration that firmware upload causes.");
        }

        var lanes = new Lane[devices.Length];

        try
        {
            for (int i = 0; i < devices.Length; i++)
            {
                lanes[i] = OpenLane(options.ModelPath, devices[i].Path, devices[i].Type, logger);
            }

            (int width, int height) = ValidateInput(lanes[0]);
            IDetectionPostprocessor postprocessor = SelectPostprocessor(lanes[0], labels, logger);
            BindOutputs(lanes, postprocessor);

            string version = Marshal.PtrToStringUTF8(EdgeTpu.edgetpu_version()) ?? "unknown";
            string description =
                $"edgetpu/{Path.GetFileNameWithoutExtension(options.ModelPath)}";

            if (options.MaxConcurrency > 0)
            {
                logger.LogInformation(
                    "Detection:MaxConcurrency is {Configured}, and this backend ignores it: an Edge TPU "
                    + "runs one inference at a time, so concurrency is the device count ({Devices}).",
                    options.MaxConcurrency, devices.Length);
            }

            logger.LogInformation(
                "Object detector ready: {Description} — {Head} head, fixed {Width}x{Height} input, "
                + "{Classes} classes ({Format}), {Devices} device(s) at {Paths}. libedgetpu {Version}.",
                description,
                postprocessor.Description,
                width,
                height,
                labels.Length,
                labelFile.Format,
                devices.Length,
                string.Join(", ", devices.Select(static d => Path.GetFileName(d.Path))),
                version);

            return new EdgeTpuObjectDetector(
                options, lanes, postprocessor, width, height, description, logger);
        }
        catch
        {
            foreach (Lane? lane in lanes)
            {
                CloseLane(lane);
            }

            throw;
        }
    }

    /// <summary>
    /// Always the shape the model was compiled at.
    ///
    /// <para>Unlike the ONNX path, there is no per-camera choice to make: <c>edgetpu_compiler</c> builds
    /// one graph for one shape ahead of time. Every camera is fitted into it, and
    /// <see cref="DetectionOptions.InputPixels"/> is not consulted — which is why region cropping earns
    /// more here than it does on a host running a per-camera shape.</para>
    /// </summary>
    public DetectorInput InputFor(int frameWidth, int frameHeight) =>
        new(_width, _height, DetectorLayout.Uint8Nhwc, 1f);

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
                + $"{input.ByteLength}.", nameof(prepared));
        }

        // The compiled graph accepts exactly one shape. A buffer prepared at another is not an error
        // anything downstream could notice — the bytes are valid, the picture they describe is sheared.
        if (input.Width != _width || input.Height != _height)
        {
            throw new ArgumentException(
                $"This model has a fixed {_width}x{_height} input; a buffer prepared at "
                + $"{input.Width}x{input.Height} cannot be run against it.", nameof(input));
        }

        if (!await EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        Lane? lane = null;

        try
        {
            lane = Rent();
            return lane is null ? [] : Run(lane, prepared.Span, input, frame);
        }
        finally
        {
            Return(lane);
            _gate.Release();
        }
    }

    private unsafe IReadOnlyList<DetectedObject> Run(
        Lane lane,
        ReadOnlySpan<byte> pixels,
        DetectorInput input,
        PreparedFrame frame)
    {
        int wanted = input.ByteLength;

        fixed (byte* source = pixels)
        {
            int copied = EdgeTpu.TfLiteTensorCopyFromBuffer(lane.InputTensor, source, (nuint)wanted);

            if (copied != EdgeTpu.TfLiteOk)
            {
                MarkSick(lane, $"TfLiteTensorCopyFromBuffer returned {copied}");
                return [];
            }
        }

        // Only the invoke is timed. The copy above is a host memcpy into the input tensor and the
        // dequantise below is processor work on the way out — counting either would make a busy
        // processor read as a busy accelerator, which is the one thing a meter labelled Edge TPU
        // must not do.
        long startedAt = Stopwatch.GetTimestamp();
        int invoked = EdgeTpu.TfLiteInterpreterInvoke(lane.Interpreter);
        Interlocked.Add(ref lane.BusyTicks, Stopwatch.GetTimestamp() - startedAt);

        if (invoked != EdgeTpu.TfLiteOk)
        {
            // The usual cause is the device going away mid-run: a USB reset, a cable, autosuspend.
            MarkSick(lane, $"TfLiteInterpreterInvoke returned {invoked}");
            return [];
        }

        Interlocked.Increment(ref lane.Inferences);

        var outputs = new DetectorOutput[lane.OutputTensors.Length];

        for (int i = 0; i < lane.OutputTensors.Length; i++)
        {
            int read = ReadOutput(lane, i);
            outputs[i] = new DetectorOutput(
                Name(lane.OutputTensors[i]), lane.Scratch[i].AsMemory(0, read), lane.OutputShapes[i]);
        }

        IReadOnlyList<DetectedObject> found = _postprocessor.Decode(
            outputs, input, frame, _options.ScoreThreshold, out DecodeDiagnostics diagnostics);

        if (diagnostics.UnknownClassRows > 0 && !_warnedUnknownClass)
        {
            _warnedUnknownClass = true;
            _logger.LogWarning(
                "Detection dropped {Rows} row(s) whose class index was outside the labels in "
                + "'{LabelsPath}'. The labels file most likely does not match the weights, in which "
                + "case the classes that *are* in range are being given the wrong names. Note that a "
                + "COCO-90 model needs a COCO-90 labels file: the indices differ from COCO-80.",
                diagnostics.UnknownClassRows, _options.LabelsPath);
        }

        return found;
    }

    /// <summary>Copies one output tensor into the lane's scratch, dequantising if it is not float.
    ///
    /// <para>Dequantisation belongs here rather than in the postprocessor: only this side knows the
    /// tensor's quantisation parameters, and keeping the arithmetic native-side is what lets every
    /// postprocessor stay pure float and testable without a model.</para>
    /// </summary>
    private static unsafe int ReadOutput(Lane lane, int index)
    {
        IntPtr tensor = lane.OutputTensors[index];
        float[] scratch = lane.Scratch[index];
        int type = EdgeTpu.TfLiteTensorType(tensor);
        IntPtr data = EdgeTpu.TfLiteTensorData(tensor);
        nuint bytes = EdgeTpu.TfLiteTensorByteSize(tensor);

        if (data == IntPtr.Zero || bytes == 0)
        {
            return 0;
        }

        if (type == 1)
        {
            // kTfLiteFloat32. The SSD detection-postprocess head emits its four tensors already
            // dequantised, so this is the common path.
            int count = Math.Min((int)(bytes / sizeof(float)), scratch.Length);
            new ReadOnlySpan<float>((void*)data, count).CopyTo(scratch);
            return count;
        }

        EdgeTpu.TfLiteQuantizationParams quant = EdgeTpu.TfLiteTensorQuantizationParams(tensor);
        float scale = quant.Scale == 0f ? 1f : quant.Scale;

        if (type == EdgeTpu.TfLiteUInt8)
        {
            int count = Math.Min((int)bytes, scratch.Length);
            var source = new ReadOnlySpan<byte>((void*)data, count);

            for (int i = 0; i < count; i++)
            {
                scratch[i] = (source[i] - quant.ZeroPoint) * scale;
            }

            return count;
        }

        if (type == EdgeTpu.TfLiteInt8)
        {
            int count = Math.Min((int)bytes, scratch.Length);
            var source = new ReadOnlySpan<sbyte>((void*)data, count);

            for (int i = 0; i < count; i++)
            {
                scratch[i] = (source[i] - quant.ZeroPoint) * scale;
            }

            return count;
        }

        throw new InvalidOperationException(
            $"Output tensor '{Name(tensor)}' has TfLiteType {type}, which this backend does not read. "
            + "Supported: float32, uint8, int8.");
    }

    /// <summary>
    /// Takes a healthy lane, rebuilding a sick one if its cooldown has passed.
    ///
    /// <para>Sick lanes are held aside and returned in the <c>finally</c>, so the bag never shrinks and
    /// the gate's promise — that a rent cannot fail while it admits — is kept even with every device
    /// down. Null means every lane is sick, which is a dropped frame rather than an exception: a
    /// detector that throws takes the camera's whole pipeline with it.</para>
    /// </summary>
    private Lane? Rent()
    {
        List<Lane>? sick = null;

        try
        {
            for (int attempt = 0; attempt < _lanes.Length; attempt++)
            {
                if (!_idle.TryTake(out Lane? lane))
                {
                    return null;
                }

                if (lane.Healthy)
                {
                    return lane;
                }

                if (DateTimeOffset.UtcNow >= lane.NextReopenAt && TryReopen(lane))
                {
                    return lane;
                }

                (sick ??= []).Add(lane);
            }

            return null;
        }
        finally
        {
            if (sick is not null)
            {
                foreach (Lane held in sick)
                {
                    _idle.Add(held);
                }
            }
        }
    }

    private void Return(Lane? lane)
    {
        if (lane is not null)
        {
            _idle.Add(lane);
        }
    }

    private void MarkSick(Lane lane, string reason)
    {
        Interlocked.Increment(ref lane.Failures);
        lane.NextReopenAt = DateTimeOffset.UtcNow + ReopenCooldown;

        // Logged once per transition rather than per frame: a device that has gone away fails every
        // frame, and a log line per frame buries the one that mattered.
        if (lane.Healthy)
        {
            lane.Healthy = false;
            _logger.LogWarning(
                "Edge TPU {Device} stopped responding ({Reason}); {Remaining} of {Total} lane(s) left. "
                + "Throughput drops until it recovers. Check the cable, the port and dmesg for USB "
                + "resets.",
                Path.GetFileName(lane.DevicePath), reason, HealthyLanes, _lanes.Length);
        }
    }

    private bool TryReopen(Lane lane)
    {
        try
        {
            CloseHandles(lane);
            Lane rebuilt = OpenLane(_options.ModelPath, lane.DevicePath, lane.DeviceType, _logger, attempts: 1);

            lane.Model = rebuilt.Model;
            lane.Delegate = rebuilt.Delegate;
            lane.Interpreter = rebuilt.Interpreter;
            lane.InputTensor = rebuilt.InputTensor;
            lane.OutputTensors = rebuilt.OutputTensors;
            lane.OutputShapes = rebuilt.OutputShapes;
            lane.Scratch = rebuilt.Scratch;
            lane.Healthy = true;

            _logger.LogInformation(
                "Edge TPU {Device} is back; {Available} of {Total} lane(s) running.",
                Path.GetFileName(lane.DevicePath), HealthyLanes, _lanes.Length);

            return true;
        }
        catch (Exception ex)
        {
            lane.NextReopenAt = DateTimeOffset.UtcNow + ReopenCooldown;
            _logger.LogDebug(
                "Edge TPU {Device} did not reopen: {Message}",
                Path.GetFileName(lane.DevicePath), ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Waits for a lane, dropping the frame rather than queueing it.
    ///
    /// A backlog of stale pictures is worth less than the frame behind it, so the wait is one frame
    /// period and what follows is a drop.
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
            "Detection skipped: every Edge TPU still busy after {Wait:0.##}s ({Dropped} dropped so "
            + "far).", wait.TotalSeconds, DroppedWhileBusy);
        return false;
    }

    /// <summary>
    /// How many Edge TPUs are attached, or zero if the question cannot be asked.
    ///
    /// <para>For <c>Backend: auto</c> to decide with. <b>Never throws</b>, and that is the point: on a
    /// host with no libedgetpu the P/Invoke fails to bind, and a host without an accelerator asking
    /// "is there an accelerator" must get "no" rather than a startup crash. A CPU-only deployment has to
    /// stay a supported deployment.</para>
    ///
    /// <para><b>Takes the options because it has to install the resolver first.</b> The image ships the
    /// libraries in their own directory rather than on the default loader path — so without this,
    /// <c>auto</c> would answer "no devices" on a host that has two, and quietly run the CPU model.</para>
    /// </summary>
    public static int CountDevices(DetectionOptions options, ILogger? logger = null)
    {
        try
        {
            InstallNativeResolver(options, logger ?? NullLogger.Instance);
            return ListDevices().Length;
        }
        catch (DllNotFoundException)
        {
            return 0;
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
    }

    private static unsafe (string Path, int Type)[] ListDevices()
    {
        EdgeTpu.EdgeTpuDevice* devices = EdgeTpu.edgetpu_list_devices(out nuint count);

        if (devices is null || count == 0)
        {
            return [];
        }

        try
        {
            var found = new (string, int)[(int)count];

            for (int i = 0; i < (int)count; i++)
            {
                found[i] = (Marshal.PtrToStringUTF8(devices[i].Path) ?? $"device{i}", devices[i].Type);
            }

            return found;
        }
        finally
        {
            EdgeTpu.edgetpu_free_devices(devices);
        }
    }

    private static Lane OpenLane(
        string modelPath, string devicePath, int deviceType, ILogger logger, int attempts = OpenAttempts)
    {
        Exception? last = null;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                return OpenLaneOnce(modelPath, devicePath, deviceType);
            }
            catch (Exception ex)
            {
                last = ex;

                if (attempt + 1 < attempts)
                {
                    // A Coral re-enumerates after libedgetpu uploads its firmware and is briefly
                    // un-openable. Observed on a device's very first open.
                    Thread.Sleep(TimeSpan.FromMilliseconds(400 * (attempt + 1)));
                    logger.LogDebug(
                        "Edge TPU {Device} not ready (attempt {Attempt}); retrying.",
                        Path.GetFileName(devicePath), attempt + 1);
                }
            }
        }

        throw new InvalidOperationException(
            $"Could not open Edge TPU {devicePath} after {attempts} attempt(s): {last?.Message}", last);
    }

    private static unsafe Lane OpenLaneOnce(string modelPath, string devicePath, int deviceType)
    {
        var lane = new Lane { DevicePath = devicePath, DeviceType = deviceType };

        lane.Model = EdgeTpu.TfLiteModelCreateFromFile(modelPath);

        if (lane.Model == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"TfLiteModelCreateFromFile could not read '{modelPath}'. A model that is not a "
                + "flatbuffer, or was compiled for a different Edge TPU runtime, fails here.");
        }

        lane.Delegate = EdgeTpu.edgetpu_create_delegate(deviceType, devicePath, null, 0);

        if (lane.Delegate == IntPtr.Zero)
        {
            CloseHandles(lane);
            throw new InvalidOperationException(
                $"edgetpu_create_delegate failed for {devicePath}. The device is present but could not "
                + "be opened — most often it is already claimed by another process, or the bus is still "
                + "resettling after a firmware upload.");
        }

        IntPtr options = EdgeTpu.TfLiteInterpreterOptionsCreate();

        try
        {
            EdgeTpu.TfLiteInterpreterOptionsAddDelegate(options, lane.Delegate);

            // One thread: everything that matters runs on the device, and the CPU-side ops in a
            // detection-postprocess tail are trivial. More threads here would take cores from ffmpeg
            // and the vision model for nothing.
            EdgeTpu.TfLiteInterpreterOptionsSetNumThreads(options, 1);

            lane.Interpreter = EdgeTpu.TfLiteInterpreterCreate(lane.Model, options);
        }
        finally
        {
            EdgeTpu.TfLiteInterpreterOptionsDelete(options);
        }

        if (lane.Interpreter == IntPtr.Zero)
        {
            CloseHandles(lane);
            throw new InvalidOperationException(
                $"TfLiteInterpreterCreate failed for {devicePath}.");
        }

        int allocated = EdgeTpu.TfLiteInterpreterAllocateTensors(lane.Interpreter);

        if (allocated != EdgeTpu.TfLiteOk)
        {
            CloseHandles(lane);
            throw new InvalidOperationException(
                $"TfLiteInterpreterAllocateTensors returned {allocated} for {devicePath}.");
        }

        lane.InputTensor = EdgeTpu.TfLiteInterpreterGetInputTensor(lane.Interpreter, 0);

        int outputs = EdgeTpu.TfLiteInterpreterGetOutputTensorCount(lane.Interpreter);
        lane.OutputTensors = new IntPtr[outputs];
        lane.OutputShapes = new int[outputs][];
        lane.Scratch = new float[outputs][];

        for (int i = 0; i < outputs; i++)
        {
            IntPtr tensor = EdgeTpu.TfLiteInterpreterGetOutputTensor(lane.Interpreter, i);
            lane.OutputTensors[i] = tensor;

            int dims = EdgeTpu.TfLiteTensorNumDims(tensor);
            var shape = new int[dims];
            int elements = 1;

            for (int d = 0; d < dims; d++)
            {
                shape[d] = EdgeTpu.TfLiteTensorDim(tensor, d);
                elements *= Math.Max(shape[d], 1);
            }

            lane.OutputShapes[i] = shape;
            lane.Scratch[i] = new float[elements];
        }

        return lane;
    }

    /// <summary>
    /// Reads what the compiled graph accepts, and throws rather than warns.
    ///
    /// <para>A present-but-wrong model is a real fault. The graph itself is opaque — a compiled model's
    /// operations are replaced by a single <c>edgetpu-custom-op</c>, so there is nothing to inspect
    /// beyond the tensor contract at the boundaries, and that makes the boundaries worth checking
    /// properly.</para>
    /// </summary>
    private static (int Width, int Height) ValidateInput(Lane lane)
    {
        IntPtr tensor = lane.InputTensor;

        if (tensor == IntPtr.Zero)
        {
            throw new InvalidOperationException("The model declares no input tensor.");
        }

        int dims = EdgeTpu.TfLiteTensorNumDims(tensor);

        if (dims != 4)
        {
            throw new InvalidOperationException(
                $"Detection model input '{Name(tensor)}' has {dims} dimensions; expected 4 "
                + "([1, height, width, 3]).");
        }

        int channels = EdgeTpu.TfLiteTensorDim(tensor, 3);

        if (channels != 3)
        {
            throw new InvalidOperationException(
                $"Detection model input '{Name(tensor)}' has {channels} channels in its last dimension; "
                + "only 3-channel NHWC RGB is supported. A model with 3 in dimension 1 is NCHW, which "
                + "TFLite does not use and edgetpu_compiler will not have produced.");
        }

        int type = EdgeTpu.TfLiteTensorType(tensor);

        if (type != EdgeTpu.TfLiteUInt8)
        {
            throw new InvalidOperationException(
                $"Detection model input '{Name(tensor)}' has TfLiteType {type}; this backend writes "
                + "uint8. A float input means the export quantised its weights but left its boundaries "
                + "in float, which edgetpu_compiler will have left almost entirely on the CPU — check "
                + "the compiler's operation split, not its exit code.");
        }

        // Read and logged rather than enforced. Both conventions in circulation are satisfied by writing
        // the raw pixel byte: scale 1/255 zero 0 gives real = q/255, and scale 1/128 zero 128 gives
        // real = (q-128)/128, which is the [-1,1] these models want since q ~= pixel either way. Gating
        // on the identity would reject a working model, which was measured.
        EdgeTpu.TfLiteQuantizationParams quant = EdgeTpu.TfLiteTensorQuantizationParams(tensor);

        int height = EdgeTpu.TfLiteTensorDim(tensor, 1);
        int width = EdgeTpu.TfLiteTensorDim(tensor, 2);

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException(
                $"Detection model input '{Name(tensor)}' reports a {width}x{height} shape. An EdgeTPU "
                + "graph is compiled for one fixed shape, so a dynamic axis here means the file is not "
                + "an edgetpu_compiler output.");
        }

        _ = quant;
        return (width, height);
    }

    /// <summary>
    /// Picks the decode from the model's own output signature rather than from configuration.
    ///
    /// <para>Self-configuring on purpose: the head is a property of the file, and an operator asked to
    /// declare it would be asked for something they cannot see. Loud when it does not recognise the
    /// signature, because a four-tensor head read as a one-tensor head decodes into plausible
    /// nonsense.</para>
    /// </summary>
    private static IDetectionPostprocessor SelectPostprocessor(
        Lane lane, IReadOnlyList<string> labels, ILogger logger)
    {
        int outputs = lane.OutputTensors.Length;

        if (outputs == 4)
        {
            logger.LogDebug("Four output tensors: reading the SSD detection-postprocess head.");
            return new SsdPostprocessor(labels);
        }

        if (outputs == 3)
        {
            // Identified by shape rather than by count alone: three tensors is not distinctive on its
            // own, and the one that matters is the 64-wide box tensor — four distances of sixteen DFL
            // bins, which nothing else in this repo emits.
            if (Array.Exists(
                lane.OutputShapes,
                shape => shape.Length == 3 && shape[2] == YoloDflPostprocessor.BoxStride))
            {
                // The one head here that states how many classes it has — the class tensor's last
                // dimension is the count. Every other head declares nothing, which is why a labels file
                // in the wrong order is silently wrong forever on those; this one can refuse at load,
                // and a 90-entry COCO list against a 17-class model is exactly the mistake to expect
                // when the weights and their labels travel separately.
                int[]? classShape = Array.Find(
                    lane.OutputShapes,
                    shape => shape.Length == 3
                        && shape[2] != YoloDflPostprocessor.BoxStride
                        && shape[2] > 1);

                if (classShape is not null && classShape[2] != labels.Count)
                {
                    throw new InvalidOperationException(
                        $"Detection model has {classShape[2]} classes and the labels file has "
                        + $"{labels.Count} entries. This head states its own class count, so the two "
                        + "must agree — a mismatch renames every detection to its neighbour. Check "
                        + "Detection:LabelsPath is the list these weights were trained against.");
                }

                logger.LogDebug(
                    "A [1, N, {Stride}] box tensor: reading the raw YOLO detect head, decoding DFL "
                    + "distances and suppressing overlaps on the CPU.",
                    YoloDflPostprocessor.BoxStride);

                return new YoloDflPostprocessor(labels);
            }

            throw new InvalidOperationException(
                "Detection model has three outputs of shapes "
                + $"{Shapes(lane)}. Three tensors is read as the raw YOLO detect head, which needs one "
                + $"of them to be [1, anchors, {YoloDflPostprocessor.BoxStride}] — four distances of "
                + $"{YoloDflPostprocessor.RegMax} DFL bins. None of these is.");
        }

        if (outputs == 1)
        {
            int[] shape = lane.OutputShapes[0];

            if (shape.Length == 3 && shape[2] == YoloEndToEndPostprocessor.Stride)
            {
                logger.LogDebug("One [1, N, 6] output: reading the YOLO end-to-end head.");
                return new YoloEndToEndPostprocessor(labels);
            }

            throw new InvalidOperationException(
                $"Detection model has one output of shape [{string.Join(", ", shape)}]. A single-output "
                + $"head is only readable as the end-to-end [1, detections, {YoloEndToEndPostprocessor.Stride}] "
                + "layout, whose rows are x1, y1, x2, y2, score, class.");
        }

        throw new InvalidOperationException(
            $"Detection model has {outputs} output tensors of shapes {Shapes(lane)}; no head this "
            + "backend reads has that many. Expected 4 (SSD detection-postprocess), 3 (raw YOLO detect) "
            + "or 1 (YOLO end-to-end).");
    }

    private static string Shapes(Lane lane) =>
        string.Join(", ", lane.OutputShapes.Select(shape => $"[{string.Join(", ", shape)}]"));

    /// <summary>Checks every lane agrees with the head the first one chose. Two devices given different
    /// model files is an operator error that would otherwise decode silently.</summary>
    private static void BindOutputs(Lane[] lanes, IDetectionPostprocessor postprocessor)
    {
        foreach (Lane lane in lanes)
        {
            if (lane.OutputTensors.Length != postprocessor.ExpectedOutputs)
            {
                throw new InvalidOperationException(
                    $"Edge TPU {lane.DevicePath} loaded a model with {lane.OutputTensors.Length} "
                    + $"outputs where {postprocessor.Description} expects "
                    + $"{postprocessor.ExpectedOutputs}.");
            }
        }
    }

    private static string Name(IntPtr tensor) =>
        Marshal.PtrToStringUTF8(EdgeTpu.TfLiteTensorName(tensor)) ?? "(unnamed)";

    /// <summary>
    /// Pins the two native libraries when the image puts them somewhere other than the loader path.
    ///
    /// Installed once per process: a resolver cannot be replaced once set, and a second registration
    /// throws.
    /// </summary>
    private static void InstallNativeResolver(DetectionOptions options, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(options.EdgeTpuLibraryDirectory))
        {
            return;
        }

        if (Interlocked.Exchange(ref _resolverInstalled, 1) != 0)
        {
            return;
        }

        string directory = options.EdgeTpuLibraryDirectory;

        if (!Directory.Exists(directory))
        {
            logger.LogWarning(
                "Detection EdgeTpuLibraryDirectory '{Directory}' does not exist; falling back to "
                + "default probing.", directory);
            return;
        }

        NativeLibrary.SetDllImportResolver(
            typeof(EdgeTpuObjectDetector).Assembly,
            (name, assembly, search) =>
            {
                string[] candidates = name switch
                {
                    "edgetpu" => ["libedgetpu.so.1", "libedgetpu.so"],
                    "tensorflowlite_c" => ["libtensorflowlite_c.so"],
                    _ => [],
                };

                foreach (string candidate in candidates)
                {
                    string path = Path.Combine(directory, candidate);

                    if (File.Exists(path))
                    {
                        return NativeLibrary.Load(path);
                    }
                }

                return IntPtr.Zero;
            });

        logger.LogInformation("EdgeTPU native libraries pinned to {Directory}.", directory);
    }

    private static void CloseLane(Lane? lane)
    {
        if (lane is not null)
        {
            CloseHandles(lane);
        }
    }

    /// <summary>Interpreter, then delegate, then model — a delegate freed while an interpreter still
    /// holds it is a use-after-free in native code.</summary>
    private static void CloseHandles(Lane lane)
    {
        if (lane.Interpreter != IntPtr.Zero)
        {
            EdgeTpu.TfLiteInterpreterDelete(lane.Interpreter);
            lane.Interpreter = IntPtr.Zero;
        }

        if (lane.Delegate != IntPtr.Zero)
        {
            EdgeTpu.edgetpu_free_delegate(lane.Delegate);
            lane.Delegate = IntPtr.Zero;
        }

        if (lane.Model != IntPtr.Zero)
        {
            EdgeTpu.TfLiteModelDelete(lane.Model);
            lane.Model = IntPtr.Zero;
        }

        lane.InputTensor = IntPtr.Zero;
        lane.OutputTensors = [];
    }

    public void Dispose()
    {
        lock (_disposeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (Lane lane in _lanes)
            {
                _logger.LogDebug(
                    "Edge TPU {Device}: {Inferences} inference(s), {Failures} failure(s).",
                    Path.GetFileName(lane.DevicePath), lane.Inferences, lane.Failures);
                CloseHandles(lane);
            }

            _gate.Dispose();
        }
    }
}
