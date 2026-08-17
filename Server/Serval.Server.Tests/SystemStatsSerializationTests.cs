using System.Text.Json;
using System.Text.Json.Nodes;
using Serval.Server.Vitals;

namespace Serval.Server.Tests;

/// <summary>
/// The wire contract the App's <c>SystemStats.fromJson</c> is written against.
///
/// The one that matters is <see cref="A_null_figure_is_written_rather_than_dropped"/>. The whole
/// design of this payload is that the client can tell "this host cannot measure it" from "it
/// measured zero"; turning on <c>DefaultIgnoreCondition.WhenWritingNull</c> anywhere upstream
/// silently collapses the first into the second, and the App would start painting a full-looking
/// meter at 0% on hardware that publishes nothing.
/// </summary>
public class SystemStatsSerializationTests
{
    /// <summary>The options ASP.NET minimal APIs serialise with.</summary>
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonObject Serialise(SystemStats stats) =>
        JsonNode.Parse(JsonSerializer.Serialize(stats, Web))!.AsObject();

    [Fact]
    public void A_null_figure_is_written_rather_than_dropped()
    {
        JsonObject json = Serialise(SystemStats.NotSampled("Not sampled yet."));

        JsonObject gpu = json["gpu"]!.AsObject();
        Assert.True(gpu.ContainsKey("busyPercent"));
        Assert.Null(gpu["busyPercent"]);
        Assert.Equal("Not sampled yet.", (string?)gpu["unavailableReason"]);

        JsonObject cpu = json["cpu"]!.AsObject();
        Assert.True(cpu.ContainsKey("containerPercent"));
        Assert.Null(cpu["containerPercent"]);
    }

    /// <summary>
    /// A processor-only host sends the accelerator group with an explicit null device list, the same
    /// way an amdgpu host sends a null <c>engines</c>.
    ///
    /// <para>The App hides the whole meter on this, so the key going missing rather than arriving
    /// null is the difference between a hidden meter and one drawn empty — and the decoder would
    /// reach the same answer either way only by accident. Pinned because it is the one group the
    /// client is expected to hide outright.</para>
    /// </summary>
    [Fact]
    public void A_host_with_no_accelerator_sends_a_null_device_list()
    {
        JsonObject json = Serialise(new SystemStats
        {
            Accelerator = AcceleratorStats.Unavailable(
                SystemStatsCollector.NoAcceleratorReason),
        });

        JsonObject accelerator = json["accelerator"]!.AsObject();

        Assert.True(accelerator.ContainsKey("devices"));
        Assert.Null(accelerator["devices"]);
        Assert.Null(accelerator["busyPercent"]);
        Assert.Equal(
            SystemStatsCollector.NoAcceleratorReason,
            (string?)accelerator["unavailableReason"]);
    }

    [Fact]
    public void The_keys_are_the_ones_the_app_reads()
    {
        var stats = new SystemStats
        {
            SampledAt = DateTimeOffset.UnixEpoch,
            ProcessUptimeSeconds = 934812,
            Cpu = new CpuStats
            {
                ContainerPercent = 41.2,
                HostPercent = 55.8,
                Cores = 16,
                QuotaCores = null,
                LoadAverage = [3.4, 2.9, 2.1],
            },
            Memory = new MemoryStats
            {
                UsedBytes = 2_617_245_696,
                CacheBytes = 4_262_461_440,
                LimitBytes = 8_589_934_592,
                Percent = 30.5,
            },
            Gpu = new GpuStats
            {
                BusyPercent = 6,
                Driver = "amdgpu",
                RenderNode = "renderD129",
                VramUsedBytes = 268_435_456,
                VramTotalBytes = 2_147_483_648,
                HostWide = true,
            },
            Accelerator = new AcceleratorStats
            {
                Label = "Edge TPU",
                BusyPercent = 61,
                InferencesPerSecond = 92.5,
                DeclinedPerSecond = 0,
                Devices =
                [
                    new AcceleratorDeviceStats
                    {
                        Name = "2-2",
                        Healthy = true,
                        Link = "USB 3",
                        BusyPercent = 78,
                        InferencesPerSecond = 63.1,
                        MeanLatencyMs = 15.8,
                    },
                ],
            },
            Disk = new DiskStats
            {
                MountPoint = "/media",
                TotalBytes = 4_000_787_030_016,
                FreeBytes = 2_200_000_000_000,
                UsedBytes = 1_800_787_030_016,
                MediaBytes = 1_743_000_000_000,
                ScannedAt = DateTimeOffset.UnixEpoch,
                ScanSeconds = 4.8,
                Cameras =
                [
                    new CameraDiskUsage
                    {
                        CameraId = "front-door",
                        Label = "Front door",
                        Bytes = 412_000_000_000,
                        FileCount = 148_231,
                        OldestSegmentAt = DateTimeOffset.UnixEpoch,
                        RetentionDays = 7,
                        BytesPerDay = 58_857_142_857,
                    },
                ],
            },
            Detection = new DetectionStats
            {
                BudgetPerSecond = 21.5,
                Cameras = 10,
                ExaminedPerSecond = 18.2,
                ShedPerSecond = 3.3,
                DroppedFramesPerSecond = 0.4,
                Coverage = 0.846,
            },
            Alerts = [new VitalsAlert { Kind = VitalsAlertKinds.DiskLow, Severity = "warning", Message = "…" }],
        };

        JsonObject json = Serialise(stats);

        Assert.Equal(
            ["sampledAt", "processUptimeSeconds", "cpu", "memory", "gpu", "accelerator", "disk",
                "detection", "alerts"],
            json.Select(p => p.Key));

        Assert.Equal(
            ["containerPercent", "hostPercent", "cores", "quotaCores", "loadAverage", "unavailableReason"],
            json["cpu"]!.AsObject().Select(p => p.Key));

        Assert.Equal(
            ["usedBytes", "cacheBytes", "limitBytes", "percent", "unavailableReason"],
            json["memory"]!.AsObject().Select(p => p.Key));

        Assert.Equal(
            ["busyPercent", "driver", "renderNode", "vramUsedBytes", "vramTotalBytes", "engines", "hostWide",
                "unavailableReason"],
            json["gpu"]!.AsObject().Select(p => p.Key));

        // Null on this fixture because amdgpu publishes one number and has no split to report. The
        // key is still emitted, so an Intel payload adds values rather than a shape.
        Assert.True(json["gpu"]!.AsObject().ContainsKey("engines"));
        Assert.Null(json["gpu"]!["engines"]);

        Assert.Equal(
            ["label", "busyPercent", "inferencesPerSecond", "declinedPerSecond", "devices",
                "unavailableReason"],
            json["accelerator"]!.AsObject().Select(p => p.Key));

        Assert.Equal(
            ["name", "healthy", "link", "busyPercent", "inferencesPerSecond", "meanLatencyMs",
                "failures"],
            json["accelerator"]!["devices"]![0]!.AsObject().Select(p => p.Key));

        Assert.Equal(
            ["mountPoint", "totalBytes", "freeBytes", "usedBytes", "mediaBytes", "scannedAt", "scanSeconds",
                "cameras", "unavailableReason"],
            json["disk"]!.AsObject().Select(p => p.Key));

        Assert.Equal(
            ["cameraId", "label", "bytes", "fileCount", "oldestSegmentAt", "retentionDays", "bytesPerDay",
                "note"],
            json["disk"]!["cameras"]![0]!.AsObject().Select(p => p.Key));

        // backend/lanes/healthyLanes/detectorDegraded are appended rather than inserted, and every one is
        // nullable, so an App built before they existed keeps deserialising this payload unchanged.
        Assert.Equal(
            ["budgetPerSecond", "cameras", "backend", "lanes", "healthyLanes", "detectorDegraded",
                "examinedPerSecond", "shedPerSecond",
                "droppedFramesPerSecond", "coverage", "unavailableReason"],
            json["detection"]!.AsObject().Select(p => p.Key));

        Assert.Equal(
            ["kind", "severity", "message"],
            json["alerts"]![0]!.AsObject().Select(p => p.Key));
    }

    /// <summary>The conversations directory belongs to no camera, and says so with a null id
    /// rather than being left out and making the per-camera figures fail to add up.</summary>
    [Fact]
    public void The_conversations_entry_carries_a_null_camera_id()
    {
        var stats = new SystemStats
        {
            Disk = new DiskStats
            {
                Cameras = [new CameraDiskUsage { CameraId = null, Label = "conversations", Bytes = 12, FileCount = 3 }],
            },
        };

        JsonObject entry = Serialise(stats)["disk"]!["cameras"]![0]!.AsObject();

        Assert.True(entry.ContainsKey("cameraId"));
        Assert.Null(entry["cameraId"]);
        Assert.Equal("conversations", (string?)entry["label"]);
    }

    [Fact]
    public void Alert_kinds_are_the_strings_the_app_switches_on()
    {
        Assert.Equal("diskCritical", VitalsAlertKinds.DiskCritical);
        Assert.Equal("diskLow", VitalsAlertKinds.DiskLow);
        Assert.Equal("memoryHigh", VitalsAlertKinds.MemoryHigh);
    }
}
