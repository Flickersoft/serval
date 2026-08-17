using System.Globalization;

namespace Serval.Server.Vitals;

/// <summary>
/// GPU utilisation, where the hardware is willing to publish it.
///
/// Two sources, because the drivers disagree about what a usage figure even is. amdgpu exposes
/// <c>gpu_busy_percent</c> as a plain sysfs file that anything can read. i915 exposes nothing
/// comparable, but it does carry a perf PMU whose per-engine counters are the same numbers
/// <c>intel_gpu_top</c> shows — see <see cref="I915PerfCounters"/> for why reading them needs a
/// capability. NVIDIA wants nvidia-smi and its own container runtime, which is a different shape of
/// problem and is not attempted.
///
/// Anything that cannot be measured honestly reports "not available" with a sentence saying why,
/// rather than a zero — which is indistinguishable from an idle GPU and is exactly the kind of
/// invented number the App's stub table exists to forbid.
///
/// The node comes from <c>Serval:Ingest:HwAccelDevice</c> where one is configured, since that is the
/// GPU the operator has already named. Otherwise the render nodes are enumerated and the first that
/// answers wins: hardcoding <c>renderD128</c> is wrong on any box with two GPUs, which is not
/// exotic — it is commonly an NVIDIA card with no busy file while <c>renderD129</c> is the AMD one
/// that has it.
///
/// Pure: the path arithmetic and the parsing are here, the reads are in
/// <see cref="SystemStatsCollector"/> and <see cref="I915PerfCounters"/>.
/// </summary>
public static class GpuSysfs
{
    public const string DrmClassRoot = "/sys/class/drm";

    /// <summary>Where the kernel advertises the i915 PMU, when the driver is loaded.</summary>
    public const string I915PmuRoot = "/sys/devices/i915";

    /// <summary>The dynamic perf type id to put in <c>perf_event_attr.type</c>. Not a constant —
    /// it is assigned at driver registration and differs between boots.</summary>
    public static string I915PmuTypePath => $"{I915PmuRoot}/type";

    /// <summary>
    /// The CPUs the PMU may be opened against. i915 answers on exactly one, and opening against any
    /// other returns EINVAL — so this is read rather than assumed to be CPU 0.
    /// </summary>
    public static string I915PmuCpuMaskPath => $"{I915PmuRoot}/cpumask";

    /// <summary>The <c>config=</c> value for one named event.</summary>
    public static string I915PmuEventPath(string eventName) => $"{I915PmuRoot}/events/{eventName}";

    /// <summary>
    /// The engine busy counters worth opening, in the order the App lists them.
    ///
    /// Every one is optional: a part with no blitter or a second video engine simply has fewer or
    /// more of these files, so the set is intersected with what exists rather than required whole.
    /// Alder Lake-N publishes rcs0, vcs0, vecs0 and bcs0.
    ///
    /// The labels are what a person reads under the meter, so they are words rather than the
    /// kernel's ring names — "video" says more to an operator watching a recording server than
    /// "vcs0" does.
    /// </summary>
    public static readonly IReadOnlyList<GpuEngineEvent> I915EngineEvents =
    [
        new("rcs0-busy", "render"),
        new("vcs0-busy", "video"),
        new("vcs1-busy", "video 2"),
        new("vecs0-busy", "video enhance"),
        new("bcs0-busy", "blitter"),
    ];

    /// <summary>
    /// What the App shows when the i915 PMU is there and the container may not open it. Names the
    /// exact compose line, because "elevated privileges" sends an operator hunting and
    /// <c>cap_add</c> does not.
    /// </summary>
    public const string I915PerfDeniedReason =
        "Intel reports GPU usage through a system-wide performance counter this container is not "
        + "allowed to open. Add cap_add: [PERFMON] to the server in your compose file and restart.";

    /// <summary>
    /// The counters are monotonic totals, so a percentage is the difference between two readings.
    /// Until there are two, there is no figure — and saying so beats showing the first reading's
    /// lifetime average as though it were current load.
    /// </summary>
    public const string I915WarmingUpReason =
        "Warming up — a usage figure is the difference between two counter readings.";

    /// <summary>An open that failed for a reason that is not a permission. Rare enough to be worth
    /// the errno rather than a sentence that guesses.</summary>
    public static string I915PerfFailedReason(int errno) =>
        $"Intel's performance counter could not be opened (errno {errno}).";

    /// <summary>
    /// The sysfs device directory for a DRM node — <c>/dev/dri/renderD128</c> and a bare
    /// <c>renderD128</c> both give <c>/sys/class/drm/renderD128/device</c>.
    ///
    /// Anything that is not a bare <c>renderD*</c> or <c>card*</c> name returns null. This value
    /// comes from configuration rather than from a request, so it is not a security boundary — but
    /// a filesystem path assembled by concatenating an operator-supplied string should still
    /// refuse the obvious traversals rather than dutifully building them.
    /// </summary>
    public static string? DeviceDirFor(string? node)
    {
        if (string.IsNullOrWhiteSpace(node))
        {
            return null;
        }

        string name = node.Trim().TrimEnd('/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        return IsDrmNodeName(name) ? $"{DrmClassRoot}/{name}/device" : null;
    }

    /// <summary>
    /// The device directories worth probing, in order: whatever <paramref name="hwAccelDevice"/>
    /// names, then every enumerated render node that is not already it.
    ///
    /// The configured node goes first because it is the GPU actually doing the encoding, but the
    /// others still follow it — a box that transcodes on one card and has an idle iGPU beside it
    /// should report the one that answers rather than nothing at all.
    /// </summary>
    public static IReadOnlyList<string> CandidateDeviceDirs(string? hwAccelDevice, IEnumerable<string> renderNodes)
    {
        var dirs = new List<string>();

        if (DeviceDirFor(hwAccelDevice) is { } configured)
        {
            dirs.Add(configured);
        }

        foreach (string node in renderNodes)
        {
            if (DeviceDirFor(node) is { } dir && !dirs.Contains(dir, StringComparer.Ordinal))
            {
                dirs.Add(dir);
            }
        }

        return dirs;
    }

    /// <summary>The <c>DRIVER=</c> line out of a sysfs <c>uevent</c> body — "amdgpu", "nvidia", "i915".</summary>
    public static string? ParseDriver(string? uevent)
    {
        if (string.IsNullOrWhiteSpace(uevent))
        {
            return null;
        }

        foreach (string line in uevent.Split('\n'))
        {
            ReadOnlySpan<char> span = line.AsSpan().Trim();
            if (span.StartsWith("DRIVER=", StringComparison.Ordinal))
            {
                ReadOnlySpan<char> value = span["DRIVER=".Length..].Trim();
                return value.IsEmpty ? null : value.ToString();
            }
        }

        return null;
    }

    /// <summary>A whole-number sysfs value — <c>gpu_busy_percent</c>, <c>mem_info_vram_*</c>.</summary>
    public static long? ParseInteger(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : null;
    }

    /// <summary>The perf type id out of <c>/sys/devices/i915/type</c> — a plain decimal.</summary>
    public static uint? ParsePmuType(string? value) =>
        ParseInteger(value) is { } parsed && parsed is >= 0 and <= uint.MaxValue ? (uint)parsed : null;

    /// <summary>
    /// The event id out of an events file, whose body is <c>config=0x2000</c>.
    ///
    /// Only the <c>config=</c> term is taken. i915 currently writes nothing else, but the perf
    /// convention is a comma-separated list of terms and a kernel that adds one should not turn a
    /// working counter into a parse failure.
    /// </summary>
    public static ulong? ParseEventConfig(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        foreach (string term in value.Trim().Split(','))
        {
            ReadOnlySpan<char> span = term.AsSpan().Trim();
            if (!span.StartsWith("config=", StringComparison.Ordinal))
            {
                continue;
            }

            ReadOnlySpan<char> raw = span["config=".Length..].Trim();

            return raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ulong.TryParse(raw[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hex)
                    ? hex
                    : null
                : ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong dec)
                    ? dec
                    : null;
        }

        return null;
    }

    /// <summary>
    /// The first CPU in a perf cpumask — <c>0</c>, <c>0-3</c> and <c>0,4</c> all give 0.
    ///
    /// First rather than all of them because an i915 counter is one number for the whole device
    /// however many CPUs it may be read from, so opening it once is the whole job. Summing across a
    /// mask would double-count.
    /// </summary>
    public static int? ParseCpuMask(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        ReadOnlySpan<char> first = value.Trim().AsSpan();
        int end = first.IndexOfAny(',', '-');
        if (end >= 0)
        {
            first = first[..end];
        }

        return int.TryParse(first.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int cpu)
            && cpu >= 0
            ? cpu
            : null;
    }

    /// <summary>
    /// Busy nanoseconds over elapsed nanoseconds, as a percentage.
    ///
    /// Clamped, and deliberately: the counter and the clock are read a moment apart, so a fully
    /// saturated engine can produce a ratio a hair over 1.0 without anything being wrong. A
    /// backwards delta means the counter was reset underneath us — a driver reload — and is no
    /// figure at all rather than a zero.
    /// </summary>
    public static double? BusyPercent(long busyDeltaNs, long elapsedNs)
    {
        if (elapsedNs <= 0 || busyDeltaNs < 0)
        {
            return null;
        }

        return Math.Clamp(100.0 * busyDeltaNs / elapsedNs, 0, 100);
    }

    /// <summary>
    /// Why a driver publishes no usage figure, in the words the App will show. Null when the
    /// driver is one that does publish it, so the caller can tell "no reason needed" from
    /// "unrecognised".
    ///
    /// The i915 answer here is the one for a host whose kernel carries no PMU at all. Where the PMU
    /// exists, the more specific outcome — denied, or warming up — comes from
    /// <see cref="I915PerfCounters"/> and replaces this.
    /// </summary>
    public static string? UnavailableReasonFor(string? driver) => driver switch
    {
        "amdgpu" => null,
        null or "" => "No GPU render node was found on this host.",
        "i915" => "This kernel exposes no i915 performance counters, which is where Intel reports GPU usage.",
        "xe" => "The xe driver reports GPU usage through counters Serval does not read yet — it reads i915's.",
        "nvidia" => "The NVIDIA driver publishes no usage figure to sysfs — reading it needs nvidia-smi and "
            + "the NVIDIA container runtime.",
        _ => $"The {driver} driver publishes no usage figure to sysfs.",
    };

    /// <summary>Bare <c>renderD*</c> or <c>card*</c> names only — no separators, no dots, no traversal.</summary>
    private static bool IsDrmNodeName(string name)
    {
        if (name.Length == 0 || name.Contains('.') || name.Contains('/'))
        {
            return false;
        }

        return name.StartsWith("renderD", StringComparison.Ordinal)
            || name.StartsWith("card", StringComparison.Ordinal);
    }
}

/// <summary>One i915 PMU engine counter: the events-file name, and what to call it in the App.</summary>
public sealed record GpuEngineEvent(string EventName, string Label);
