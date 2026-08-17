using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Serval.Server.Vitals;

/// <summary>
/// Intel GPU utilisation, read from the i915 perf PMU.
///
/// i915 publishes no <c>gpu_busy_percent</c> the way amdgpu does. What it publishes instead is a
/// set of perf counters — one per engine — each a monotonic total of nanoseconds that engine has
/// been busy. A percentage is the difference between two readings over the wall time between them,
/// which is the same arithmetic <c>intel_gpu_top</c> does and the same numbers it shows.
///
/// Everything needed to open one is a string in sysfs and readable by anyone: the PMU's dynamically
/// assigned perf type, the CPU it answers on, and each event's config value. The container reads all
/// three today. What it cannot do without <c>CAP_PERFMON</c> is the <c>perf_event_open</c> itself,
/// because these are system-wide counters — opened with <c>pid = -1</c> — and a kernel with
/// <c>perf_event_paranoid</c> at its default of 2 refuses those to an unprivileged caller. Docker's
/// default seccomp profile independently gates the syscall behind the same capability.
///
/// That is the whole gate, and it is one line of compose. A host that has not granted it gets
/// <see cref="GpuSysfs.I915PerfDeniedReason"/> under the meter, naming the line — not a zero, and
/// not a proxy figure derived from something else that happens to be readable.
///
/// System-wide is also why <see cref="GpuStats.HostWide"/> stays true here: this counts every
/// process on the box, not Serval's share.
/// </summary>
public sealed class I915PerfCounters : IDisposable
{
    /// <summary>x86_64's <c>__NR_perf_event_open</c>. The guard in <see cref="TryOpen"/> is what
    /// keeps this from being wrong somewhere else.</summary>
    private const long SysPerfEventOpen = 298;

    /// <summary><c>PERF_FLAG_FD_CLOEXEC</c> — an ffmpeg child has no business inheriting these.</summary>
    private const ulong PerfFlagFdCloexec = 8;

    /// <summary>
    /// <c>PERF_ATTR_SIZE_VER7</c>. The kernel reads the caller's <c>size</c> and zero-fills any
    /// field a newer version added, so naming a version it certainly knows is more durable than
    /// tracking the current one. Every field past <c>config</c> stays zero: no sampling, no
    /// exclusions — i915 declares <c>PERF_PMU_CAP_NO_EXCLUDE</c> and rejects the open outright if
    /// an exclude bit is set.
    /// </summary>
    private const int AttrSize = 128;

    private readonly List<Counter> _counters;
    private long _timestamp;

    /// <summary>
    /// False once the counters have been closed — either by shutdown or by a read that stopped
    /// answering. The caller uses it to tell "no figure this tick" from "no figure ever again", and
    /// to stop holding a session that has nothing left to sample.
    /// </summary>
    public bool IsOpen => _counters.Count > 0;

    private I915PerfCounters(List<Counter> counters, long timestamp)
    {
        _counters = counters;
        _timestamp = timestamp;
    }

    /// <summary>
    /// Opens one counter per engine the host has, or explains why it could not.
    ///
    /// A baseline is read from every counter before returning, so the first
    /// <see cref="Sample"/> a tick later already has two readings to subtract.
    /// </summary>
    public static I915PerfCounters? TryOpen(out string reason)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            reason = "Intel's performance counters are read only on x86-64.";
            return null;
        }

        if (!Directory.Exists(GpuSysfs.I915PmuRoot))
        {
            reason = GpuSysfs.UnavailableReasonFor("i915")!;
            return null;
        }

        if (GpuSysfs.ParsePmuType(TryRead(GpuSysfs.I915PmuTypePath)) is not { } type)
        {
            reason = GpuSysfs.UnavailableReasonFor("i915")!;
            return null;
        }

        // Missing cpumask means the PMU is there but not telling us where to open it. Zero is the
        // answer on every part that has ever shipped, so it is a better guess than giving up.
        int cpu = GpuSysfs.ParseCpuMask(TryRead(GpuSysfs.I915PmuCpuMaskPath)) ?? 0;

        var counters = new List<Counter>();
        int lastErrno = 0;

        foreach (GpuEngineEvent engine in GpuSysfs.I915EngineEvents)
        {
            if (GpuSysfs.ParseEventConfig(TryRead(GpuSysfs.I915PmuEventPath(engine.EventName))) is not { } config)
            {
                continue;
            }

            int fd = Open(type, config, cpu, out int errno);
            if (fd < 0)
            {
                lastErrno = errno;
                continue;
            }

            var handle = new SafeFileHandle(fd, ownsHandle: true);

            if (TryReadCounter(fd, out ulong baseline))
            {
                counters.Add(new Counter(engine.Label, fd, handle, baseline));
            }
            else
            {
                lastErrno = Marshal.GetLastPInvokeError();
                handle.Dispose();
            }
        }

        if (counters.Count == 0)
        {
            reason = lastErrno switch
            {
                // EACCES and EPERM are the same story to an operator: the capability is missing.
                1 or 13 => GpuSysfs.I915PerfDeniedReason,
                0 => GpuSysfs.UnavailableReasonFor("i915")!,
                _ => GpuSysfs.I915PerfFailedReason(lastErrno),
            };
            return null;
        }

        reason = GpuSysfs.I915WarmingUpReason;
        return new I915PerfCounters(counters, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Each engine's busy percentage since the previous call, or null with a reason.
    ///
    /// Null on the tick the counters were opened, because a difference needs two readings, and null
    /// if a counter stops answering — which disposes the session rather than leaving a stale figure
    /// standing.
    /// </summary>
    public IReadOnlyList<GpuEngineSample>? Sample(out string reason)
    {
        long now = Stopwatch.GetTimestamp();
        long elapsedNs = (long)Stopwatch.GetElapsedTime(_timestamp, now).TotalNanoseconds;

        // A tick that lands on top of the open has nothing to divide by. Anything under a
        // millisecond is that case rather than a real window.
        if (elapsedNs < 1_000_000)
        {
            reason = GpuSysfs.I915WarmingUpReason;
            return null;
        }

        var samples = new List<GpuEngineSample>(_counters.Count);

        foreach (Counter counter in _counters)
        {
            if (!TryReadCounter(counter.Fd, out ulong busy))
            {
                int errno = Marshal.GetLastPInvokeError();
                Dispose();
                reason = GpuSysfs.I915PerfFailedReason(errno);
                return null;
            }

            // A counter that went backwards means the driver reloaded underneath us. Re-baseline
            // and say nothing this tick rather than report a negative window as zero.
            if (busy < counter.Previous)
            {
                counter.Previous = busy;
                continue;
            }

            long delta = (long)(busy - counter.Previous);
            counter.Previous = busy;

            if (GpuSysfs.BusyPercent(delta, elapsedNs) is { } percent)
            {
                samples.Add(new GpuEngineSample(counter.Label, percent));
            }
        }

        _timestamp = now;

        if (samples.Count == 0)
        {
            reason = GpuSysfs.I915WarmingUpReason;
            return null;
        }

        reason = string.Empty;
        return samples;
    }

    public void Dispose()
    {
        foreach (Counter counter in _counters)
        {
            counter.Handle.Dispose();
        }

        _counters.Clear();
    }

    private static int Open(uint type, ulong config, int cpu, out int errno)
    {
        var attr = new PerfEventAttr
        {
            Type = type,
            Size = AttrSize,
            Config = config,
        };

        // pid -1, cpu n: the counter for the whole machine. This is the argument pair that needs
        // the capability, and also the one that makes the answer host-wide.
        long fd = Syscall(SysPerfEventOpen, ref attr, -1, cpu, -1, PerfFlagFdCloexec);

        if (fd < 0)
        {
            errno = Marshal.GetLastPInvokeError();
            return -1;
        }

        errno = 0;
        return (int)fd;
    }

    /// <summary>
    /// One counter's current value. With <c>read_format</c> left at zero a perf fd reads as a
    /// single unsigned 64-bit count and nothing else, so a short read is a failure rather than a
    /// partial value to reassemble.
    /// </summary>
    private static bool TryReadCounter(int fd, out ulong value)
    {
        nint read = Read(fd, out value, 8);

        if (read == 8)
        {
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Optional by design, exactly as in <see cref="SystemStatsCollector"/>: a host without these
    /// files is answering the question, not failing.
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

    /// <summary>
    /// glibc's raw syscall entry. Declared with fixed arguments rather than as a variadic: on the
    /// SysV x86-64 ABI these land in the same registers either way, and glibc's <c>syscall</c> is
    /// hand-written assembly that never reads the vararg register count.
    /// </summary>
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long Syscall(long number, ref PerfEventAttr attr, int pid, int cpu, int groupFd, ulong flags);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint Read(int fd, out ulong buffer, nuint count);

    /// <summary>
    /// The head of <c>struct perf_event_attr</c>. Only the three fields an i915 counter needs are
    /// named; <see cref="AttrSize"/> pads the rest, and a struct is zeroed whole on construction, so
    /// every field this does not name is the zero the kernel expects.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = AttrSize)]
    private struct PerfEventAttr
    {
        public uint Type;
        public uint Size;
        public ulong Config;
    }

    /// <summary>
    /// The fd is held twice on purpose: as an int to read from, and as a handle that closes it. The
    /// alternative is reaching into the handle on every sample, which is a use-after-free the day
    /// something disposes the session from another thread.
    /// </summary>
    private sealed class Counter(string label, int fd, SafeFileHandle handle, ulong previous)
    {
        public string Label { get; } = label;
        public int Fd { get; } = fd;
        public SafeFileHandle Handle { get; } = handle;
        public ulong Previous { get; set; } = previous;
    }
}

/// <summary>One engine's share of the GPU over the last sampling window.</summary>
public sealed record GpuEngineSample(string Label, double BusyPercent);
