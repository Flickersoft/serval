namespace Serval.Server.Vitals;

/// <summary>
/// What the server can say about itself, as of the last background sample. This file is the JSON
/// contract that <c>GET /api/system/stats</c> serves and the App's <c>SystemStats.fromJson</c> is
/// written against.
///
/// **Every figure is nullable and every group carries an <c>UnavailableReason</c>.** That is the
/// whole design. A host whose GPU driver publishes no utilisation, a kernel with no cgroup v2, a
/// volume whose statvfs failed — each says so in words, rather than reporting a zero that is
/// indistinguishable from a real measurement of nothing. The App renders "not reported" for the
/// first and a full-looking meter at 0% for the second, and only one of those is honest.
///
/// Serialisation must leave <c>DefaultIgnoreCondition</c> at <c>Never</c> (the ASP.NET web default,
/// pinned by <c>SystemStatsSerializationTests</c>): the client distinguishes an absent key from a
/// zero, so a null silently dropped on the wire becomes a <c>0</c> at the far end and undoes all
/// of the above.
/// </summary>
public sealed record SystemStats
{
    /// <summary>When the fast sample was taken. Null before the first tick.</summary>
    public DateTimeOffset? SampledAt { get; init; }

    /// <summary>
    /// How long this server process has been running — deliberately the process, not the host.
    /// <c>/proc/uptime</c> inside a container is the host's, which answers a question nobody on
    /// this page is asking.
    /// </summary>
    public double? ProcessUptimeSeconds { get; init; }

    public CpuStats Cpu { get; init; } = CpuStats.NotSampled;
    public MemoryStats Memory { get; init; } = MemoryStats.NotSampled;
    public GpuStats Gpu { get; init; } = GpuStats.NotSampled;
    public AcceleratorStats Accelerator { get; init; } = AcceleratorStats.NotSampled;
    public DiskStats Disk { get; init; } = DiskStats.NotSampled;
    public DetectionStats Detection { get; init; } = DetectionStats.NotSampled;

    public IReadOnlyList<VitalsAlert> Alerts { get; init; } = [];

    /// <summary>
    /// What the endpoint serves before the worker's first tick, and when vitals are switched off.
    /// A shape rather than a 503: the page renders its own "not reported" states perfectly well,
    /// and a settings screen that fails to load is a worse answer than one that says so in place.
    /// </summary>
    public static SystemStats NotSampled(string reason) => new()
    {
        Cpu = CpuStats.Unavailable(reason),
        Memory = MemoryStats.Unavailable(reason),
        Gpu = GpuStats.Unavailable(reason),
        Accelerator = AcceleratorStats.Unavailable(reason),
        Disk = DiskStats.Unavailable(reason),
        Detection = DetectionStats.Unavailable(reason),
    };
}

/// <summary>
/// Whether object detection is keeping up, and what it is skipping when it is not.
///
/// <para><b>Why this is on the status page rather than only in the log.</b> Every part of the
/// detection path degrades by dropping work — a backlog of frames, a crop the budget could not
/// afford, a frame arriving while the detector was still busy. That is deliberate: a recorder must
/// not stall its recording because inference is slow. But each drop is a small loss of accuracy, and
/// a system that is silently examining half of what it could looks exactly like one that is keeping
/// up. Somebody comparing this week's events with last week's would have no way to tell the
/// difference was the host rather than the driveway. This is the number that says so.</para>
///
/// <para><see cref="Coverage"/> is the headline: of everything detection wanted to look at, the
/// fraction it actually did. One means nothing is being skipped.</para>
/// </summary>
public sealed record DetectionStats
{
    /// <summary>Inferences a second this host was measured at and is willing to spend. Null when the
    /// detector could not be timed, in which case nothing is throttled.</summary>
    public double? BudgetPerSecond { get; init; }

    /// <summary>Cameras sharing that budget.</summary>
    public int? Cameras { get; init; }

    /// <summary>Which detector is running, e.g. <c>onnx/cpu model</c> or
    /// <c>edgetpu/ssdlite_mobiledet…</c>. With more than one backend buildable, "which one is actually
    /// running" is a question the status page should answer rather than the startup log.</summary>
    public string? Backend { get; init; }

    /// <summary>Lanes the detector holds, and how many can run now.
    ///
    /// <para>Equal on a CPU backend, always — a thread pool does not lose lanes. Unequal means a device
    /// was unplugged or stopped answering, and real capacity fell with it. Nullable so an older App that
    /// has never heard of a lane keeps deserialising this payload.</para></summary>
    public int? Lanes { get; init; }

    /// <inheritdoc cref="Lanes"/>
    public int? HealthyLanes { get; init; }

    /// <summary>Why the detector says it is degraded, or null when it is not.</summary>
    public string? DetectorDegraded { get; init; }

    /// <summary>Regions actually examined, per second, over the last sampling window.</summary>
    public double? ExaminedPerSecond { get; init; }

    /// <summary>Regions the planner wanted and the budget could not afford, per second. A place in a
    /// frame nothing looked at.</summary>
    public double? ShedPerSecond { get; init; }

    /// <summary>Whole frames that never reached detection, per second. A moment nothing looked at,
    /// which is the more expensive of the two losses because it loses time rather than area.</summary>
    public double? DroppedFramesPerSecond { get; init; }

    /// <summary>
    /// Examined ÷ (examined + shed), over the last window. Null when nothing was asked for, which is
    /// an idle server rather than a failing one — a scene where nothing is happening proposes no
    /// crops, and reporting that as 0% coverage would be exactly backwards.
    /// </summary>
    public double? Coverage { get; init; }

    public string? UnavailableReason { get; init; }

    public static readonly DetectionStats NotSampled =
        new() { UnavailableReason = "Not sampled yet." };

    public static DetectionStats Unavailable(string reason) =>
        new() { UnavailableReason = reason };
}

/// <summary>
/// Processor load, from two sources that answer different questions.
///
/// <see cref="ContainerPercent"/> is this container — the server process *and* the one ffmpeg per
/// camera it spawns, which is why it comes from cgroup accounting rather than from the .NET
/// process counter, which cannot see the children at all. <see cref="HostPercent"/> is the whole
/// machine, Mongo and go2rtc and anything else on the box included, because procfs is not
/// namespaced for <c>/proc/stat</c>.
/// </summary>
public sealed record CpuStats
{
    public double? ContainerPercent { get; init; }
    public double? HostPercent { get; init; }

    /// <summary>The denominator actually used for <see cref="ContainerPercent"/>.</summary>
    public double? Cores { get; init; }

    /// <summary>
    /// The CPU ceiling from <c>cpu.max</c>, in cores, or null for no limit. Null is the normal
    /// answer here: neither compose file sets one.
    /// </summary>
    public double? QuotaCores { get; init; }

    /// <summary>One, five and fifteen minute host load averages. The number that actually
    /// distinguishes "busy" from "oversubscribed", which a percentage cannot.</summary>
    public IReadOnlyList<double>? LoadAverage { get; init; }

    public string? UnavailableReason { get; init; }

    public static readonly CpuStats NotSampled = new() { UnavailableReason = "Not sampled yet." };
    public static CpuStats Unavailable(string reason) => new() { UnavailableReason = reason };
}

/// <summary>
/// Memory against the container's cgroup limit — the ceiling that actually bites on this
/// deployment, where <c>mem_limit</c> was raised from 2g to 8g after the vision model, the KV
/// cache, SenseVoice and one ffmpeg per camera stopped fitting. The OOM killer gives no notice and
/// leaves no trace in the app, so this is the one figure here worth watching without being asked.
/// </summary>
public sealed record MemoryStats
{
    /// <summary>
    /// The working set — what the container is holding and cannot simply hand back. This is
    /// <c>memory.current</c> less reclaimable page cache, not <c>memory.current</c> itself; see
    /// <see cref="CgroupV2.WorkingSet"/> for why reporting the raw figure read as 100% forever on
    /// any host whose media volume is not ZFS.
    /// </summary>
    public long? UsedBytes { get; init; }

    /// <summary>
    /// Reclaimable page cache the cgroup is charged for, excluded from <see cref="UsedBytes"/> and
    /// reported so that it is visible rather than merely subtracted.
    ///
    /// On an NVR this is large, healthy and mostly recordings on their way to disk — the kernel
    /// grows it into whatever the limit leaves spare. Null on a kernel that publishes no
    /// <c>inactive_file</c> key, which is the same case where <see cref="UsedBytes"/> falls back to
    /// the raw figure.
    /// </summary>
    public long? CacheBytes { get; init; }

    /// <summary>Null means no limit is set, so there is nothing to be near.</summary>
    public long? LimitBytes { get; init; }

    /// <summary><see cref="UsedBytes"/> as a percentage of <see cref="LimitBytes"/>, so cache is excluded here too.</summary>
    public double? Percent { get; init; }
    public string? UnavailableReason { get; init; }

    public static readonly MemoryStats NotSampled = new() { UnavailableReason = "Not sampled yet." };
    public static MemoryStats Unavailable(string reason) => new() { UnavailableReason = reason };
}

/// <summary>
/// GPU utilisation, where the hardware publishes one. See <see cref="GpuSysfs"/> for the two ways
/// it can arrive and <see cref="I915PerfCounters"/> for what Intel asks in exchange.
/// </summary>
public sealed record GpuStats
{
    public long? BusyPercent { get; init; }
    public string? Driver { get; init; }
    public string? RenderNode { get; init; }
    public long? VramUsedBytes { get; init; }
    public long? VramTotalBytes { get; init; }

    /// <summary>
    /// The per-engine split, where the source has one. amdgpu's single busy file does not, so this
    /// is null there; i915 counts render, video, video-enhance and blitter separately, and on a
    /// recording server that split is the interesting part — a box at 40% because ffmpeg is
    /// encoding and a box at 40% because the vision model is running are different boxes.
    ///
    /// <see cref="BusyPercent"/> is the busiest of these, because one meter can only mean one
    /// thing and "how close is this GPU to saturated" is the useful one.
    /// </summary>
    public IReadOnlyList<GpuEngineStats>? Engines { get; init; }

    /// <summary>
    /// Always true when a figure is present, and stated on the wire rather than assumed: unlike
    /// the CPU and memory numbers beside it, this is the whole GPU and not Serval's share of it.
    /// The App says so under the meter.
    /// </summary>
    public bool HostWide { get; init; }

    public string? UnavailableReason { get; init; }

    public static readonly GpuStats NotSampled = new() { UnavailableReason = "Not sampled yet." };
    public static GpuStats Unavailable(string reason) => new() { UnavailableReason = reason };
}

/// <summary>One engine of a GPU that reports them separately, as a percentage of the last window.</summary>
public sealed record GpuEngineStats
{
    public required string Name { get; init; }
    public required long BusyPercent { get; init; }
}

/// <summary>
/// How hard the object detector's accelerators are working, where the detector runs on any — a pair
/// of Coral Edge TPUs, today.
///
/// <para><b>The meter is the pool, where the GPU's beside it is the busiest engine.</b> That
/// difference is deliberate and is the whole reason this is a separate group rather than more
/// <see cref="GpuEngineStats"/>. GPU engines are not interchangeable, so one number has to pick one
/// of them and "how close is the busiest to saturated" is the useful pick. Accelerator devices *are*
/// interchangeable: a frame goes to whichever is idle and the detection budget is their sum, so the
/// question worth answering is how close the pool is to saturated. The asymmetry between devices —
/// which is real, and on this deployment is a factor of two — lives in <see cref="Devices"/>, where
/// it is a fact about the hardware rather than a meter jumping between two of them.</para>
///
/// <para><b>Absent on a processor deployment, and that is the point.</b> A backend whose lanes are
/// threads reports no devices, <see cref="Devices"/> is null, and the App draws no meter at all
/// rather than an empty one. This is the one group on the page the client is expected to hide
/// outright: the others describe hardware every host has, and this describes hardware most do not.
/// A host that *has* accelerators and has lost them keeps its devices listed and unhealthy, so the
/// meter never disappears at the moment it starts mattering.</para>
/// </summary>
public sealed record AcceleratorStats
{
    /// <summary>What to call these devices where a person will read it — <c>Edge TPU</c>. From the
    /// detector, because only it knows what it opened.</summary>
    public string? Label { get; init; }

    /// <summary>Time the devices spent inferring over the last window, as a percentage of the time
    /// they were all available to. Null before there are two readings to subtract.</summary>
    public long? BusyPercent { get; init; }

    /// <summary>Inferences a second across every device, over the last window. The figure to compare
    /// against <see cref="DetectionStats.BudgetPerSecond"/>.</summary>
    public double? InferencesPerSecond { get; init; }

    /// <summary>
    /// Frames a second the detector refused because every device was still busy.
    ///
    /// <para>The most direct statement there is that the accelerator is the bottleneck, and it lives
    /// here rather than on <see cref="DetectionStats"/> because it is a property of the devices
    /// rather than of coverage — the budget did not shed these, the hardware declined them.</para>
    /// </summary>
    public double? DeclinedPerSecond { get; init; }

    /// <summary>One entry per device, in the order the detector holds them. Null on a host whose
    /// detector has none, which is what the App hides the whole meter on.</summary>
    public IReadOnlyList<AcceleratorDeviceStats>? Devices { get; init; }

    public string? UnavailableReason { get; init; }

    public static readonly AcceleratorStats NotSampled =
        new() { UnavailableReason = "Not sampled yet." };

    public static AcceleratorStats Unavailable(string reason) =>
        new() { UnavailableReason = reason };
}

/// <summary>
/// One accelerator device over the last window.
///
/// <para>Per device rather than pooled only, because on this deployment the two are not alike: a
/// Coral on a USB 2 path measured 29.7 inferences a second against its twin's 64.2 on USB 3, and
/// nothing else in the product would ever say so. The pooled meter is right about capacity and
/// silent about that, and it is the kind of fault that looks like a slow model.</para>
/// </summary>
public sealed record AcceleratorDeviceStats
{
    /// <summary>How the device names itself — a Coral's sysfs path, <c>2-2</c>. The same short form
    /// the startup line prints, so the two can be read against each other.</summary>
    public required string Name { get; init; }

    /// <summary>False for a device that stopped answering. It stays in the list.</summary>
    public required bool Healthy { get; init; }

    /// <summary>The link it is attached over — <c>USB 3</c>, <c>USB 2</c>. Null where the host does
    /// not publish one, which costs nothing: it explains a slow device rather than reporting it.</summary>
    public string? Link { get; init; }

    public long? BusyPercent { get; init; }

    public double? InferencesPerSecond { get; init; }

    /// <summary>Mean time inside one inference over the window — busy time divided by the count, not
    /// a percentile. Null over a window in which this device ran nothing.</summary>
    public double? MeanLatencyMs { get; init; }

    /// <summary>Calls that returned an error, cumulative since startup rather than over the window.
    /// A rate would round to zero for the failure that matters, which happens once.</summary>
    public long Failures { get; init; }
}

/// <summary>
/// The media volume, and what each camera is using of it.
///
/// <see cref="UsedBytes"/> is everything on the volume (total minus free) while
/// <see cref="MediaBytes"/> is only what Serval wrote — on a shared pool those are very different
/// numbers, and conflating them would explain a full disk as Serval's fault when it isn't.
/// </summary>
public sealed record DiskStats
{
    public string? MountPoint { get; init; }
    public long? TotalBytes { get; init; }
    public long? FreeBytes { get; init; }
    public long? UsedBytes { get; init; }

    /// <summary>Sum of the per-camera walk plus the conversations directory. Null when the walk
    /// is switched off (<c>Vitals:DiskScanMinutes</c> of 0) or has not run yet.</summary>
    public long? MediaBytes { get; init; }

    public DateTimeOffset? ScannedAt { get; init; }

    /// <summary>How long the last walk took. In the payload because it is the number that tells an
    /// operator to turn the walk off, and there is no other way for them to find it out.</summary>
    public double? ScanSeconds { get; init; }

    public IReadOnlyList<CameraDiskUsage> Cameras { get; init; } = [];

    public string? UnavailableReason { get; init; }

    public static readonly DiskStats NotSampled = new() { UnavailableReason = "Not sampled yet." };
    public static DiskStats Unavailable(string reason) => new() { UnavailableReason = reason };
}

/// <summary>
/// One camera's footprint under the media root, measured from the filesystem rather than summed
/// from the recording index — see <see cref="DiskUsageScanner"/> for why that distinction matters.
///
/// <see cref="CameraId"/> is null for the conversations directory, which lives under the same root
/// and belongs to no camera.
/// </summary>
public sealed record CameraDiskUsage
{
    public string? CameraId { get; init; }
    public required string Label { get; init; }
    public long Bytes { get; init; }
    public int FileCount { get; init; }

    /// <summary>Start of the oldest indexed segment, so the App can say "7 days · 412 GB" rather
    /// than a byte count with no span attached to it.</summary>
    public DateTimeOffset? OldestSegmentAt { get; init; }

    /// <summary>What this camera is set to keep — its own override, or the server default.</summary>
    public int? RetentionDays { get; init; }

    /// <summary>
    /// Measured write rate: <see cref="Bytes"/> over the span back to
    /// <see cref="OldestSegmentAt"/>. Derived here rather than in the App so the client does no
    /// arithmetic across two nullable fields. Measured rather than multiplied out from a nominal
    /// bitrate, so it accounts for audio and keyframes as actually written.
    /// </summary>
    public double? BytesPerDay { get; init; }

    /// <summary>
    /// What this directory is, where that is not obvious from a byte count.
    ///
    /// Null for a camera, whose row already says how far back it goes and what it keeps. Set for
    /// the directories no retention sweep touches, because "539 KB" beside a camera that is being
    /// pruned invites the assumption that this is pruned too — and saved clips are the one thing
    /// on the volume that only ever grows.
    /// </summary>
    public string? Note { get; init; }
}

/// <summary>
/// Something the operator should know, evaluated server-side so the threshold stays a deployment
/// property and the wording has one spelling — and so a later notification path can reuse it with
/// no Flutter client running.
/// </summary>
public sealed record VitalsAlert
{
    /// <summary>Wire values are the <see cref="VitalsAlertKinds"/> constants.</summary>
    public required string Kind { get; init; }

    /// <summary>"warning" or "critical".</summary>
    public required string Severity { get; init; }

    /// <summary>Rendered here, shown verbatim by the App.</summary>
    public required string Message { get; init; }
}

/// <summary>
/// The alert kinds, as they appear on the wire. Deliberately a small closed set: an App that does
/// not recognise a kind drops it, so adding one here is safe for older clients.
/// </summary>
public static class VitalsAlertKinds
{
    public const string DiskCritical = "diskCritical";
    public const string DiskLow = "diskLow";
    public const string MemoryHigh = "memoryHigh";
    public const string DetectionDegraded = "detectionDegraded";
}
