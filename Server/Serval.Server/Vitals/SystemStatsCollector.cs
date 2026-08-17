using Microsoft.Extensions.Options;
using Serval.Ai;
using Serval.Server.Ai;
using Serval.Server.Alerts;
using Serval.Server.Cameras;
using Serval.Server.Clips;
using Serval.Server.Configuration;
using Serval.Server.Recordings;
using Serval.Server.Snapshots;

namespace Serval.Server.Vitals;

/// <summary>
/// Holds the current sample and knows how to take a new one.
///
/// Split from <see cref="SystemStatsWorker"/> so the endpoint depends on the thing that *has* the
/// answer rather than on the thing that runs a timer, and split from the parsers in
/// <see cref="CgroupV2"/>, <see cref="ProcStat"/>, <see cref="GpuSysfs"/> and
/// <see cref="MountPoints"/> so that everything except the file reads themselves is pure and
/// unit-tested. What is left here is genuinely only I/O and assembly.
///
/// Nothing in the request path measures anything: <c>GET /api/system/stats</c> reads
/// <see cref="Current"/> and returns. A CPU percentage is the difference between two samples over
/// the time between them, so a sampler has to exist whatever the transport is — and once it does,
/// serving the last one is free.
/// </summary>
public sealed class SystemStatsCollector : IDisposable
{
    private readonly MediaOptions _media;
    private readonly IngestOptions _ingest;
    private readonly ServerAiOptions _serverAi;
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly RecordingIndex _recordings;
    private readonly ClipStorage _clips;
    private readonly AlertStorage _alerts;
    private readonly ILogger<SystemStatsCollector> _logger;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    private CpuSample? _previousContainerCpu;
    private HostCpuSample? _previousHostCpu;

    /// <summary>The last disk figures, kept across fast samples — the walk runs far less often.</summary>
    private DiskStats _disk = DiskStats.NotSampled;

    /// <summary>Per-camera results accumulated across sweeps, keyed by directory name.</summary>
    private readonly Dictionary<string, CameraDiskUsage> _cameraUsage = new(StringComparer.Ordinal);

    private volatile SystemStats _current = SystemStats.NotSampled("Not sampled yet.");

    /// <summary>Retained fast samples, for the App's sparklines. See <see cref="VitalsRing"/>.</summary>
    private readonly VitalsRing _history;

    private readonly DetectionLoad _detectionLoad;
    private readonly DetectFrameBroadcaster _detectFrames;

    /// <summary>
    /// The running detector, or null where this deployment has none.
    ///
    /// <para><b>Read here rather than through <see cref="DetectionLoad"/></b>, unlike every other
    /// detection figure on this page. The counters behind the accelerator meter are cumulative, so a
    /// percentage is a difference between two of them divided by the time between the two — and
    /// <see cref="DetectionLoad"/>'s detector fields are published on the AI coordinator's reconcile
    /// tick, which is a separate timer from this one. Dividing counters captured on one clock by an
    /// interval measured on another gives a figure that drifts and occasionally exceeds 100%.</para>
    /// </summary>
    private readonly IObjectDetector? _detector;

    /// <summary>
    /// The previous reading of each device's cumulative totals, keyed by device name, and when it was
    /// taken. Held across samples for the same reason <see cref="_i915"/> is: the totals are
    /// monotonic, and a rate only exists between two of them.
    /// </summary>
    private IReadOnlyDictionary<string, DetectorDevice> _lastDevices =
        new Dictionary<string, DetectorDevice>(StringComparer.Ordinal);

    private DateTimeOffset? _lastAcceleratorAt;

    private long _lastDeclined;

    /// <summary>
    /// Link speeds, resolved once per device rather than per sample.
    ///
    /// <para>The value cannot change while the device is the one the detector is holding open —
    /// re-training the link means re-enumerating, which means a new lane. So this is two file reads
    /// per accelerator for the life of the process, against one per accelerator every five seconds.
    /// A null answer is cached too: a PCIe part has no such file and will not grow one.</para>
    /// </summary>
    private readonly Dictionary<string, string?> _deviceLinks = new(StringComparer.Ordinal);

    /// <summary>
    /// Frames the handoff had already discarded when detection was last sampled.
    ///
    /// Kept here rather than reset on the broadcaster because that counter has no single owner —
    /// every camera's ingest writes to it — and a reader that reset it would race every one of them.
    /// A high-water mark turns a shared cumulative counter into a rate safely.
    /// </summary>
    private long _lastSupersededFrames;

    /// <summary>
    /// The open i915 counters, where this host has them and let us have them. Held across samples
    /// because the counters are monotonic totals — a percentage only exists between two readings,
    /// so closing and reopening every tick would report nothing forever.
    /// </summary>
    private I915PerfCounters? _i915;

    /// <summary>Attempted once. Granting CAP_PERFMON means recreating the container, so a host that
    /// said no this boot will say no every tick of it, and retrying is a syscall spent to be told
    /// the same thing.</summary>
    private bool _i915Attempted;

    private string? _i915Reason;

    public SystemStatsCollector(
        IOptionsMonitor<ServerOptions> options,
        RecordingIndex recordings,
        DetectionLoad detectionLoad,
        DetectFrameBroadcaster detectFrames,
        ClipStorage clips,
        AlertStorage alerts,
        ILogger<SystemStatsCollector> logger,
        IObjectDetector? detector = null)
    {
        ServerOptions server = options.CurrentValue;
        _media = server.Media;
        _ingest = server.Ingest;
        _serverAi = server.ServerAi;
        _options = options;
        _recordings = recordings;
        _clips = clips;
        _alerts = alerts;
        _detectionLoad = detectionLoad;
        _detectFrames = detectFrames;
        _logger = logger;
        _detector = detector;

        // Derived from both dials rather than hardcoded to 720: the window is a duration, so a
        // deployment that slows the sample cadence should keep the same hour, not the same count.
        _history = new VitalsRing(VitalsRing.CapacityFor(Vitals.HistoryMinutes, Vitals.SampleSeconds));

        if (!Vitals.Enabled)
        {
            _current = SystemStats.NotSampled("Vitals are switched off on this server (Serval:Vitals:Enabled).");
        }
    }

    /// <summary>
    /// Read fresh, so a changed threshold applies to the next sample rather than the next restart.
    /// The three sections above it are not re-read: the media root and the conversation directory
    /// are where files already are, and moving them is a restart either way.
    /// </summary>
    private VitalsOptions Vitals => _options.CurrentValue.Vitals;

    /// <summary>
    /// The last completed sample. Never null: before the first tick it is a shape whose reason
    /// strings say so, which the App renders in place rather than failing to load a page over.
    /// </summary>
    public SystemStats Current => _current;

    /// <summary>
    /// The directories under the media root that belong to no camera, as scan targets.
    ///
    /// Exposed rather than hardcoded in the worker because each is configurable
    /// (<c>ServerAi:ConversationAudioRoot</c>, <c>Media:ClipsRoot</c>, <c>Media:AlertsRoot</c>), and
    /// a copy of any of those names elsewhere would silently drop the entry from every sweep the
    /// moment somebody changed it.
    ///
    /// They are in the sweep at all so the per-directory figures add up to the volume total. A
    /// gigabyte of saved clips that appears in the free-space number and nowhere in the breakdown
    /// is exactly the kind of missing space nobody can account for. **Anything placed outside the
    /// camera directories has to be added here** — that placement is what exempts a directory from
    /// the recording sweep, and it is equally what hides it from this one.
    /// </summary>
    public IReadOnlyList<DiskScanTarget> NonCameraTargets =>
    [
        new(
            _serverAi.ConversationAudioRoot,
            "Conversation audio",
            Path.Combine(Path.GetFullPath(_media.Root), _serverAi.ConversationAudioRoot),
            Note: "buffered while a conversation is open"),
        new(
            _media.ClipsRoot,
            "Saved clips",
            _clips.Root,
            Note: "kept until you delete them"),
        new(
            _media.AlertsRoot,
            "Alert previews",
            _alerts.Root,
            Note: _media.AlertRetentionDays == 1
                ? "kept for a day"
                : $"kept for {_media.AlertRetentionDays} days"),
    ];

    /// <summary>A camera's own directory under the media root.</summary>
    public DiskScanTarget TargetFor(Camera camera) => new(
        camera.Id,
        camera.Name is { Length: > 0 } cameraName ? cameraName : camera.Id,
        Path.Combine(Path.GetFullPath(_media.Root), camera.Id),
        camera);

    /// <summary>
    /// Processor, memory and GPU. Cheap — four small file reads — so it runs on the short cadence.
    /// </summary>
    public void SampleFast()
    {
        CpuStats cpu = SampleCpu();
        MemoryStats memory = SampleMemory();
        GpuStats gpu = SampleGpu();
        AcceleratorStats accelerator = SampleAccelerator();

        var stats = new SystemStats
        {
            SampledAt = DateTimeOffset.UtcNow,
            ProcessUptimeSeconds = (DateTimeOffset.UtcNow - _startedAt).TotalSeconds,
            Cpu = cpu,
            Memory = memory,
            Gpu = gpu,
            Accelerator = accelerator,
            Disk = _disk,
            Detection = SampleDetection(),
        };

        _current = stats with { Alerts = VitalsAlerts.Evaluate(stats, Vitals) };

        Retain(stats);
    }

    /// <summary>
    /// What detection is skipping.
    ///
    /// <para>This is the only reader of <see cref="DetectionLoad.Read"/>, and it has to be: reading
    /// closes the rate window, so a second caller would silently halve this one's numbers.</para>
    ///
    /// <para>A server with no detector reports a reason rather than zeroes. Zero shed of zero
    /// examined is indistinguishable from perfect coverage, and claiming detection is keeping up on
    /// a host that is not running any is the kind of confident wrong answer this whole file is
    /// written to avoid.</para>
    /// </summary>
    private DetectionStats SampleDetection()
    {
        if (!_serverAi.Enabled)
        {
            return DetectionStats.Unavailable(
                "Server-side AI is switched off on this server (Serval:ServerAi:Enabled).");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DetectionLoadSample load = _detectionLoad.Read(now);

        if (load.ActiveCameras == 0)
        {
            return DetectionStats.Unavailable("No camera has object detection running.");
        }

        long superseded = _detectFrames.Superseded;
        double? droppedPerSecond = Rate(superseded - _lastSupersededFrames, now);
        _lastSupersededFrames = superseded;

        // Of everything detection wanted to look at, what it actually did. Null on an idle window
        // rather than zero: a still scene proposes nothing, and reporting that as no coverage would
        // raise an alert about a server doing exactly the right thing.
        double? coverage = load.ExaminedPerSecond is { } examined
            && load.ShedRegionsPerSecond is { } shed
            && examined + shed > 0
            ? examined / (examined + shed)
            : null;

        return new DetectionStats
        {
            BudgetPerSecond = load.BudgetPerSecond,
            Cameras = load.ActiveCameras,
            ExaminedPerSecond = load.ExaminedPerSecond,
            ShedPerSecond = load.ShedRegionsPerSecond,
            DroppedFramesPerSecond = droppedPerSecond,
            Coverage = coverage,
            Backend = load.Backend,
            Lanes = load.Lanes,
            HealthyLanes = load.HealthyLanes,
            DetectorDegraded = load.DetectorDegraded,
        };

        double? Rate(long delta, DateTimeOffset at)
        {
            double seconds = (at - _lastDetectionSampledAt).TotalSeconds;
            _lastDetectionSampledAt = at;
            return seconds > 0 ? delta / seconds : null;
        }
    }

    private DateTimeOffset _lastDetectionSampledAt = DateTimeOffset.UtcNow;

    /// <summary>Said on a host whose detector runs on the processor. The App draws no meter at all for
    /// this case, so the sentence is for whoever reads the payload rather than for the page.</summary>
    public const string NoAcceleratorReason =
        "This server's object detector runs on the processor, so there is no accelerator to report.";

    /// <summary>
    /// How hard the object detector's accelerators are working.
    ///
    /// <para>Holds the previous reading and reads one file per device; the arithmetic is in
    /// <see cref="AcceleratorLoad"/>, where it can be tested with no accelerator attached. The
    /// detector is read directly here rather than through <see cref="DetectionLoad"/> — see
    /// <see cref="_detector"/> for why that matters to a figure derived from two readings.</para>
    /// </summary>
    private AcceleratorStats SampleAccelerator()
    {
        if (_detector?.Health is not { Devices: { Count: > 0 } devices } health)
        {
            return AcceleratorStats.Unavailable(NoAcceleratorReason);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        double elapsed = _lastAcceleratorAt is { } previousAt
            ? (now - previousAt).TotalSeconds
            : 0;

        IReadOnlyDictionary<string, DetectorDevice> last = _lastDevices;

        _lastDevices = devices.ToDictionary(device => device.Path, StringComparer.Ordinal);
        _lastAcceleratorAt = now;

        long declined = health.DroppedWhileBusy;
        double? declinedPerSecond = elapsed > 0 ? (declined - _lastDeclined) / elapsed : null;
        _lastDeclined = declined;

        return AcceleratorLoad.Measure(
            devices, last, elapsed, health.AcceleratorLabel, declinedPerSecond, LinkFor);
    }

    /// <summary>
    /// The link a device is attached over, read once and remembered. See <see cref="UsbSysfs"/> for
    /// why it is worth reporting and <see cref="_deviceLinks"/> for why it is cached.
    /// </summary>
    private string? LinkFor(string device)
    {
        if (_deviceLinks.TryGetValue(device, out string? cached))
        {
            return cached;
        }

        string? link = null;

        try
        {
            string path = UsbSysfs.SpeedPath(UsbSysfs.DeviceRoot, device);

            if (File.Exists(path))
            {
                link = UsbSysfs.Generation(UsbSysfs.ParseSpeed(File.ReadAllText(path)));
            }
        }
        catch (IOException)
        {
            // Best-effort: a link speed explains a slow device, it does not report one.
        }
        catch (UnauthorizedAccessException)
        {
        }

        _deviceLinks[device] = link;
        return link;
    }

    /// <summary>Files one sample in the ring. Nulls ride through untouched — see <see cref="VitalsRing.Add"/>.</summary>
    private void Retain(SystemStats stats)
    {
        // Resized here rather than only at construction, because both dials it is derived from can
        // change while the server runs. Widening the window keeps what is already retained;
        // narrowing it drops the oldest immediately, which is what the App then draws.
        _history.Capacity = VitalsRing.CapacityFor(Vitals.HistoryMinutes, Vitals.SampleSeconds);

        _history.Add(new VitalsSample(
            stats.SampledAt ?? DateTimeOffset.UtcNow,
            stats.Cpu.ContainerPercent,
            stats.Memory.Percent,
            stats.Gpu.BusyPercent,
            stats.Accelerator.BusyPercent));
    }

    /// <summary>The retained samples as the wire contract. Served straight from memory, like <see cref="Current"/>.</summary>
    public VitalsHistory History()
    {
        if (!Vitals.Enabled)
        {
            return VitalsHistory.Unavailable(
                "Vitals are switched off on this server (Serval:Vitals:Enabled).");
        }

        if (_history.Capacity == 0)
        {
            return VitalsHistory.Unavailable(
                "History retention is switched off on this server (Serval:Vitals:HistoryMinutes).");
        }

        return VitalsHistory.From(_history.Snapshot(), Vitals.HistoryMinutes);
    }

    /// <summary>
    /// Free space on the media volume. One statvfs, so this rides the fast cadence too — it is the
    /// figure the disk alerts are built on and it should not wait on the per-camera walk.
    /// </summary>
    public void SampleVolume()
    {
        string root = Path.GetFullPath(_media.Root);

        try
        {
            DriveInfo[] drives = DriveInfo.GetDrives();
            string? mount = MountPoints.Best(root, [.. drives.Select(d => d.Name)]);

            DriveInfo? drive = mount is null
                ? null
                : drives.FirstOrDefault(d => MountPoints.Best(root, [d.Name]) == mount);

            if (drive is null || !drive.IsReady)
            {
                _disk = _disk with
                {
                    UnavailableReason = $"No mounted volume was found for the media root ({root}).",
                };
                return;
            }

            long total = drive.TotalSize;
            long free = drive.AvailableFreeSpace;

            _disk = _disk with
            {
                MountPoint = mount,
                TotalBytes = total,
                FreeBytes = free,
                UsedBytes = total - free,
                UnavailableReason = null,
            };
        }
        catch (IOException ex)
        {
            _disk = _disk with { UnavailableReason = $"The media volume could not be read: {ex.Message}" };
        }
        catch (UnauthorizedAccessException ex)
        {
            _disk = _disk with { UnavailableReason = $"The media volume could not be read: {ex.Message}" };
        }
    }

    /// <summary>
    /// Walk one directory and fold the result into the cached set. One per tick rather than all of
    /// them in a burst — see <see cref="VitalsOptions.DiskScanMinutes"/> for the cost this is
    /// spreading.
    /// </summary>
    public async Task ScanOneAsync(DiskScanTarget target, CancellationToken cancellationToken)
    {
        Camera? camera = target.Camera;
        string key = target.Key;
        string directory = target.Directory;

        var started = System.Diagnostics.Stopwatch.StartNew();
        DirectoryUsage usage;

        try
        {
            usage = await Task.Run(() => DiskUsageScanner.Scan(directory, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not measure {Directory}.", directory);
            return;
        }

        started.Stop();

        DateTimeOffset? oldest = camera is null
            ? null
            : await _recordings.OldestSegmentAtAsync(camera.Id, cancellationToken);

        _cameraUsage[key] = new CameraDiskUsage
        {
            CameraId = camera?.Id,
            Label = target.Label,
            Bytes = usage.Bytes,
            FileCount = usage.FileCount,
            OldestSegmentAt = oldest,
            RetentionDays = camera is null ? null : camera.RetentionDays ?? _media.RetentionDays,
            BytesPerDay = BytesPerDay(usage.Bytes, oldest),
            Note = target.Note,
        };

        if (started.Elapsed.TotalSeconds > 30)
        {
            _logger.LogWarning(
                "Measuring {Directory} took {Seconds:0.0}s ({Files} files). Raise Serval:Vitals:DiskScanMinutes, "
                + "or set it to 0 to report volume free space only.",
                directory, started.Elapsed.TotalSeconds, usage.FileCount);
        }
    }

    /// <summary>
    /// Publish the accumulated per-camera figures, dropping any camera that has since been deleted
    /// from the registry.
    /// </summary>
    public void PublishDisk(IReadOnlyCollection<string> liveKeys, double scanSeconds)
    {
        foreach (string stale in _cameraUsage.Keys.Where(k => !liveKeys.Contains(k)).ToList())
        {
            _cameraUsage.Remove(stale);
        }

        List<CameraDiskUsage> cameras = [.. _cameraUsage.Values.OrderByDescending(c => c.Bytes)];

        _disk = _disk with
        {
            MediaBytes = cameras.Sum(c => c.Bytes),
            ScannedAt = DateTimeOffset.UtcNow,
            ScanSeconds = scanSeconds,
            Cameras = cameras,
        };
    }

    /// <summary>Blank the per-camera figures when the walk is switched off, so a stale set from a
    /// previous configuration cannot linger in the payload.</summary>
    public void ClearDiskDetail()
    {
        _cameraUsage.Clear();
        _disk = _disk with { MediaBytes = null, ScannedAt = null, ScanSeconds = null, Cameras = [] };
    }

    private CpuStats SampleCpu()
    {
        double cores = Environment.ProcessorCount;
        double? quotaCores = CgroupV2.ParseQuotaCores(TryRead(CgroupV2.CpuMaxPath));

        // A quota is the honest denominator when there is one: 50% of two allotted cores is a very
        // different statement from 50% of the machine. Neither compose file sets one today.
        if (quotaCores is { } quota && quota > 0)
        {
            cores = quota;
        }

        double? hostPercent = null;
        if (ProcStat.ParseAggregate(TryRead(ProcStat.StatPath)) is { } hostSample)
        {
            if (_previousHostCpu is { } previousHost)
            {
                hostPercent = ProcStat.Percent(previousHost, hostSample);
            }

            _previousHostCpu = hostSample;
        }

        IReadOnlyList<double>? load = ProcStat.ParseLoadAverage(TryRead(ProcStat.LoadAveragePath)) is { } avg
            ? [avg.One, avg.Five, avg.Fifteen]
            : null;

        string? cpuStat = TryRead(CgroupV2.CpuStatPath);
        if (CgroupV2.ParseUsageUsec(cpuStat) is not { } usageUsec)
        {
            return new CpuStats
            {
                HostPercent = hostPercent,
                Cores = cores,
                QuotaCores = quotaCores,
                LoadAverage = load,
                UnavailableReason =
                    "This host publishes no cgroup v2 CPU accounting, so Serval's own share cannot be "
                    + "separated from the rest of the machine.",
            };
        }

        var sample = new CpuSample(usageUsec, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
        double? containerPercent = _previousContainerCpu is { } previous
            ? CgroupV2.Percent(previous, sample, cores)
            : null;

        _previousContainerCpu = sample;

        return new CpuStats
        {
            ContainerPercent = containerPercent,
            HostPercent = hostPercent,
            Cores = cores,
            QuotaCores = quotaCores,
            LoadAverage = load,
            // Not an error: the first sample after start has nothing to subtract from. Saying so
            // beats a null with no explanation on a page somebody has just opened.
            UnavailableReason = containerPercent is null ? "Waiting for a second sample." : null,
        };
    }

    private MemoryStats SampleMemory()
    {
        long? current = CgroupV2.ParseBytes(TryRead(CgroupV2.MemoryCurrentPath));
        if (current is null)
        {
            return MemoryStats.Unavailable("This host publishes no cgroup v2 memory accounting.");
        }

        long? limit = CgroupV2.ParseBytes(TryRead(CgroupV2.MemoryMaxPath));
        long? cache = CgroupV2.ParseMemoryStat(TryRead(CgroupV2.MemoryStatPath), "inactive_file");
        long used = CgroupV2.WorkingSet(current.Value, cache);

        return new MemoryStats
        {
            UsedBytes = used,
            CacheBytes = cache,
            LimitBytes = limit,
            Percent = limit is { } max && max > 0 ? Math.Clamp(100.0 * used / max, 0, 100) : null,
        };
    }

    private GpuStats SampleGpu()
    {
        IReadOnlyList<string> candidates = GpuSysfs.CandidateDeviceDirs(_ingest.HwAccelDevice, RenderNodes());
        if (candidates.Count == 0)
        {
            return GpuStats.Unavailable("No GPU render node was found on this host.");
        }

        // The first candidate's driver is what an unusable result gets explained by — it is the
        // one the operator configured, so "the NVIDIA driver publishes nothing" is a more useful
        // sentence than a generic miss.
        string? firstDriver = null;

        // Remembered rather than acted on inside the loop: the plain sysfs file is cheaper than a
        // syscall, so every candidate gets asked for one before Intel's counters are considered.
        string? intelDir = null;

        foreach (string dir in candidates)
        {
            string? driver = GpuSysfs.ParseDriver(TryRead(Path.Combine(dir, "uevent")));
            firstDriver ??= driver;

            if (driver == "i915")
            {
                intelDir ??= dir;
            }

            if (GpuSysfs.ParseInteger(TryRead(Path.Combine(dir, "gpu_busy_percent"))) is not { } busy)
            {
                continue;
            }

            return new GpuStats
            {
                BusyPercent = Math.Clamp(busy, 0, 100),
                Driver = driver,
                RenderNode = NodeNameOf(dir),
                VramUsedBytes = GpuSysfs.ParseInteger(TryRead(Path.Combine(dir, "mem_info_vram_used"))),
                VramTotalBytes = GpuSysfs.ParseInteger(TryRead(Path.Combine(dir, "mem_info_vram_total"))),
                HostWide = true,
            };
        }

        if (intelDir is not null)
        {
            return SampleI915(intelDir);
        }

        return new GpuStats
        {
            Driver = firstDriver,
            RenderNode = NodeNameOf(candidates[0]),
            UnavailableReason = GpuSysfs.UnavailableReasonFor(firstDriver)
                ?? "This GPU publishes no usage figure to sysfs.",
        };
    }

    /// <summary>
    /// Intel's per-engine counters, or the sentence explaining what this host would have to grant.
    ///
    /// Every branch returns a <see cref="GpuStats"/> naming the driver and node, so the meter's
    /// label stays "Graphics · i915" whether or not there is a figure under it — the App renders a
    /// reason in place of the bar, not in place of the card.
    /// </summary>
    private GpuStats SampleI915(string deviceDir)
    {
        GpuStats Blank(string reason) => new()
        {
            Driver = "i915",
            RenderNode = NodeNameOf(deviceDir),
            UnavailableReason = reason,
        };

        if (_i915 is null)
        {
            if (_i915Attempted)
            {
                return Blank(_i915Reason ?? GpuSysfs.UnavailableReasonFor("i915")!);
            }

            _i915Attempted = true;
            _i915 = I915PerfCounters.TryOpen(out string openReason);
            _i915Reason = openReason;

            if (_i915 is null)
            {
                // Once, at Information: an operator wondering why the meter is blank should find
                // the answer in the log as well as on the page.
                _logger.LogInformation("Intel GPU utilisation is not available: {Reason}", openReason);
                return Blank(openReason);
            }

            _logger.LogInformation("Reading Intel GPU utilisation from the i915 performance counters.");
        }

        IReadOnlyList<GpuEngineSample>? engines = _i915.Sample(out string reason);

        if (engines is null)
        {
            if (!_i915.IsOpen)
            {
                _i915 = null;
                _i915Reason = reason;
                _logger.LogWarning("Intel GPU counters stopped answering: {Reason}", reason);
            }

            return Blank(reason);
        }

        return new GpuStats
        {
            // The busiest engine. A GPU is saturated when any one of its engines is, and averaging
            // them would report a box whose video engine is pinned as comfortably idle.
            BusyPercent = (long)Math.Round(engines.Max(engine => engine.BusyPercent)),
            Driver = "i915",
            RenderNode = NodeNameOf(deviceDir),
            Engines = [.. engines.Select(engine => new GpuEngineStats
            {
                Name = engine.Label,
                BusyPercent = (long)Math.Round(engine.BusyPercent),
            })],
            // No VRAM figures: an integrated part has no pool of its own to report, and its share
            // of system memory is already in the memory meter beside this one.
            HostWide = true,
        };
    }

    public void Dispose()
    {
        _i915?.Dispose();
        _i915 = null;
    }

    private IEnumerable<string> RenderNodes()
    {
        try
        {
            return Directory.Exists(GpuSysfs.DrmClassRoot)
                ? Directory.EnumerateDirectories(GpuSysfs.DrmClassRoot)
                    .Select(Path.GetFileName)
                    .Where(n => n is not null && n.StartsWith("renderD", StringComparison.Ordinal))
                    .Select(n => n!)
                    .Order(StringComparer.Ordinal)
                    .ToList()
                : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>"/sys/class/drm/renderD129/device" -> "renderD129".</summary>
    private static string? NodeNameOf(string deviceDir) =>
        Path.GetFileName(Path.GetDirectoryName(deviceDir.TrimEnd('/')) ?? string.Empty) is { Length: > 0 } name
            ? name
            : null;

    private static double? BytesPerDay(long bytes, DateTimeOffset? oldest)
    {
        if (oldest is not { } from)
        {
            return null;
        }

        double days = (DateTimeOffset.UtcNow - from).TotalDays;

        // Under an hour of footage extrapolates to a nonsense daily figure — a camera added five
        // minutes ago would read as terabytes a day. Better to say nothing until there is a span.
        return days >= 1.0 / 24.0 ? bytes / days : null;
    }

    /// <summary>
    /// Reads a sysfs or procfs file, or null. Every one of these is optional by design — that is
    /// the difference between "this host cannot tell us" and a startup failure — so a missing file
    /// or a denied read is an expected answer rather than an exception worth logging every sweep.
    /// </summary>
    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
