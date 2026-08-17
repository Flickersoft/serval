using System.Text.Json;
using System.Text.Json.Nodes;
using Serval.Server.Vitals;

namespace Serval.Server.Tests;

/// <summary>
/// The retained-sample buffer behind the App's sparklines, and the wire contract it is served as.
///
/// The ones that matter are the null tests. A sparkline is where "this host cannot measure it"
/// most easily degrades into "it measured zero", because a chart has to put the line *somewhere* —
/// and a line resting on the axis is a claim that the GPU was idle. The buffer must carry nulls
/// through and the serialiser must write them, so the App can break the line instead.
/// </summary>
public class VitalsHistoryTests
{
    /// <summary>The options ASP.NET minimal APIs serialise with.</summary>
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static VitalsSample Sample(int second, double? cpu = 10, double? memory = 20, double? gpu = 30) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(second), cpu, memory, gpu);

    [Fact]
    public void The_oldest_sample_is_evicted_once_full()
    {
        var ring = new VitalsRing(capacity: 3);

        for (int i = 0; i < 5; i++)
        {
            ring.Add(Sample(i, cpu: i));
        }

        VitalsSample[] kept = ring.Snapshot();

        Assert.Equal(3, kept.Length);
        Assert.Equal([2d, 3d, 4d], kept.Select(s => s.CpuPercent));
    }

    [Fact]
    public void Samples_come_back_oldest_first()
    {
        var ring = new VitalsRing(capacity: 10);

        ring.Add(Sample(1));
        ring.Add(Sample(2));
        ring.Add(Sample(3));

        Assert.Equal(
            [DateTimeOffset.UnixEpoch.AddSeconds(1), DateTimeOffset.UnixEpoch.AddSeconds(2), DateTimeOffset.UnixEpoch.AddSeconds(3)],
            ring.Snapshot().Select(s => s.SampledAt));
    }

    [Fact]
    public void A_capacity_of_zero_retains_nothing()
    {
        var ring = new VitalsRing(capacity: 0);

        ring.Add(Sample(1));

        Assert.Empty(ring.Snapshot());
    }

    [Fact]
    public void An_unavailable_figure_is_kept_as_null_not_as_zero()
    {
        var ring = new VitalsRing(capacity: 10);

        // The shape a host with no amdgpu produces: processor and memory measured, GPU not.
        ring.Add(Sample(1, gpu: null));

        Assert.Null(ring.Snapshot()[0].GpuPercent);
    }

    [Fact]
    public void The_window_is_a_duration_so_capacity_follows_the_cadence()
    {
        // The defaults.
        Assert.Equal(720, VitalsRing.CapacityFor(historyMinutes: 60, sampleSeconds: 5));

        // Sampling half as often keeps the same hour, not the same count.
        Assert.Equal(360, VitalsRing.CapacityFor(historyMinutes: 60, sampleSeconds: 10));

        // Zero minutes is retention switched off.
        Assert.Equal(0, VitalsRing.CapacityFor(historyMinutes: 0, sampleSeconds: 5));
    }

    [Fact]
    public void The_series_stay_aligned_with_their_timestamps()
    {
        VitalsHistory history = VitalsHistory.From(
            [Sample(1, cpu: 1, memory: 2, gpu: 3), Sample(2, cpu: 4, memory: 5, gpu: 6)],
            windowMinutes: 60);

        Assert.Equal(2, history.SampledAt.Count);
        Assert.Equal([1d, 4d], history.Cpu);
        Assert.Equal([2d, 5d], history.Memory);
        Assert.Equal([3d, 6d], history.Gpu);

        // Every series is the same length as the timestamps, which is what lets the App read index
        // i of each as one instant.
        Assert.All(
            new[] { history.Cpu.Count, history.Memory.Count, history.Gpu.Count },
            count => Assert.Equal(history.SampledAt.Count, count));
    }

    [Fact]
    public void A_null_in_a_series_is_written_rather_than_dropped()
    {
        VitalsHistory history = VitalsHistory.From([Sample(1, gpu: null)], windowMinutes: 60);

        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(history, Web))!.AsObject();
        JsonArray gpu = json["gpu"]!.AsArray();

        // The array keeps its slot. Dropping it would shorten the series against its timestamps and
        // shift every later reading onto the wrong instant — worse than the zero this guards.
        Assert.Single(gpu);
        Assert.Null(gpu[0]);
    }

    [Fact]
    public void The_keys_are_the_ones_the_app_reads()
    {
        VitalsHistory history = VitalsHistory.From([Sample(1)], windowMinutes: 60);

        JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(history, Web))!.AsObject();

        Assert.True(json.ContainsKey("sampledAt"));
        Assert.True(json.ContainsKey("cpu"));
        Assert.True(json.ContainsKey("memory"));
        Assert.True(json.ContainsKey("gpu"));
        Assert.Equal(60, (double?)json["windowMinutes"]);
        Assert.True(json.ContainsKey("unavailableReason"));
        Assert.Null(json["unavailableReason"]);
    }

    [Fact]
    public void Retention_switched_off_says_so_rather_than_serving_empty_arrays()
    {
        // An empty history and a disabled one are different answers: the first fills in a few
        // seconds after a restart, the second never will, and only one of them is worth a sentence
        // on the page.
        VitalsHistory off = VitalsHistory.Unavailable("History retention is switched off.");

        Assert.Equal("History retention is switched off.", off.UnavailableReason);
        Assert.Empty(off.SampledAt);
    }
}
