using Serval.Server.Vitals;

namespace Serval.Server.Tests;

/// <summary>
/// Finding the GPU, and knowing when its driver has nothing to say.
///
/// The multi-GPU case below is not hypothetical: on this project's own development machine
/// renderD128 is an NVIDIA card with no gpu_busy_percent and renderD129 is the AMD one that has
/// it. Hardcoding the first node would report "no GPU" with a perfectly readable one beside it.
/// </summary>
public class GpuSysfsTests
{
    [Theory]
    [InlineData("/dev/dri/renderD128")]
    [InlineData("renderD128")]
    [InlineData("/dev/dri/renderD128/")]
    public void A_render_node_resolves_to_its_sysfs_device_directory(string node)
    {
        Assert.Equal("/sys/class/drm/renderD128/device", GpuSysfs.DeviceDirFor(node));
    }

    [Fact]
    public void A_card_node_resolves_too()
    {
        Assert.Equal("/sys/class/drm/card0/device", GpuSysfs.DeviceDirFor("/dev/dri/card0"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_configured_is_null(string? node)
    {
        Assert.Null(GpuSysfs.DeviceDirFor(node));
    }

    /// <summary>
    /// Not a security boundary — this comes from configuration, not from a request — but a path
    /// assembled by concatenating an operator-supplied string should refuse the obvious traversals
    /// rather than dutifully building them.
    /// </summary>
    [Theory]
    [InlineData("/dev/dri/../../etc/passwd")]
    [InlineData("..")]
    [InlineData("nvidia0")]
    [InlineData("/dev/null")]
    public void Anything_that_is_not_a_drm_node_name_is_refused(string node)
    {
        Assert.Null(GpuSysfs.DeviceDirFor(node));
    }

    [Fact]
    public void The_configured_node_is_probed_first_and_the_others_still_follow()
    {
        IReadOnlyList<string> dirs = GpuSysfs.CandidateDeviceDirs(
            "/dev/dri/renderD128",
            ["renderD128", "renderD129"]);

        Assert.Equal(
            ["/sys/class/drm/renderD128/device", "/sys/class/drm/renderD129/device"],
            dirs);
    }

    /// <summary>The dev machine's layout: the configured node is the one with no usage figure, and
    /// the readable GPU is the second one.</summary>
    [Fact]
    public void Enumerated_nodes_are_offered_when_nothing_is_configured()
    {
        IReadOnlyList<string> dirs = GpuSysfs.CandidateDeviceDirs(null, ["renderD128", "renderD129"]);

        Assert.Equal(
            ["/sys/class/drm/renderD128/device", "/sys/class/drm/renderD129/device"],
            dirs);
    }

    [Fact]
    public void A_node_is_never_offered_twice()
    {
        IReadOnlyList<string> dirs = GpuSysfs.CandidateDeviceDirs("renderD129", ["renderD128", "renderD129"]);

        Assert.Equal(
            ["/sys/class/drm/renderD129/device", "/sys/class/drm/renderD128/device"],
            dirs);
    }

    [Fact]
    public void The_driver_is_read_out_of_a_uevent()
    {
        const string amd = """
            DRIVER=amdgpu
            PCI_CLASS=30000
            PCI_ID=1002:1638
            MODALIAS=pci:v00001002d00001638sv00001043sd000087C6bc03sc00i00
            """;

        Assert.Equal("amdgpu", GpuSysfs.ParseDriver(amd));
        Assert.Equal("nvidia", GpuSysfs.ParseDriver("DRIVER=nvidia\nPCI_CLASS=30000"));
    }

    [Fact]
    public void A_uevent_with_no_driver_is_null()
    {
        Assert.Null(GpuSysfs.ParseDriver("PCI_CLASS=30000\nPCI_ID=1002:1638"));
        Assert.Null(GpuSysfs.ParseDriver(""));
        Assert.Null(GpuSysfs.ParseDriver(null));
    }

    [Fact]
    public void Sysfs_integers_parse_and_junk_does_not()
    {
        Assert.Equal(0, GpuSysfs.ParseInteger("0\n"));
        Assert.Equal(97, GpuSysfs.ParseInteger("97"));
        Assert.Equal(2147483648, GpuSysfs.ParseInteger("2147483648\n"));
        Assert.Null(GpuSysfs.ParseInteger("N/A"));
        Assert.Null(GpuSysfs.ParseInteger(null));
    }

    /// <summary>
    /// amdgpu is the only driver that publishes a usage figure to sysfs, so it is the only one with
    /// nothing to explain. Everything else gets a sentence saying why, which is what the App renders
    /// in place of a meter.
    ///
    /// i915's answer here is for a kernel carrying no PMU at all. Where the PMU exists, the more
    /// specific outcome comes from <see cref="I915PerfCounters"/> and replaces this — which is why
    /// this sentence must not be the one about capabilities.
    /// </summary>
    [Fact]
    public void Only_amdgpu_has_no_reason_to_give()
    {
        Assert.Null(GpuSysfs.UnavailableReasonFor("amdgpu"));

        Assert.Contains("no i915 performance counters", GpuSysfs.UnavailableReasonFor("i915"));
        Assert.Contains("xe driver", GpuSysfs.UnavailableReasonFor("xe"));
        Assert.Contains("nvidia-smi", GpuSysfs.UnavailableReasonFor("nvidia"));
        Assert.Contains("render node", GpuSysfs.UnavailableReasonFor(null));
        Assert.Contains("virtio_gpu", GpuSysfs.UnavailableReasonFor("virtio_gpu"));
    }

    /// <summary>
    /// The denied case is the one most Intel hosts will sit on, so it has to name the line to add
    /// rather than describe the problem. "Elevated privileges" sends an operator hunting.
    /// </summary>
    [Fact]
    public void The_denied_reason_names_the_compose_line()
    {
        Assert.Contains("cap_add: [PERFMON]", GpuSysfs.I915PerfDeniedReason);
    }
}
