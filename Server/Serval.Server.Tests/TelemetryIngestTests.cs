using System.Text.Json;
using Serval.Contracts;
using Serval.Server.Telemetry;

namespace Serval.Server.Tests;

/// <summary>
/// The batch-splitting decision on telemetry ingest: a record that cannot be parsed is rejected
/// and counted, while a storage failure fails the whole request — the module must keep the batch
/// and redeliver, or the records are silently lost.
/// </summary>
public class TelemetryIngestTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly DateTimeOffset ReceivedAt =
        new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_storage_failure_fails_the_batch_so_the_module_redelivers()
    {
        JsonElement body = Json("""[{"type":"utterance","id":"u1","text":"hi"}]""");

        await Assert.ThrowsAsync<TimeoutException>(() => TelemetryEndpoints.IngestBatchAsync(
            "cam1", body, _ => throw new TimeoutException("database down")));
    }

    [Fact]
    public async Task A_malformed_record_is_rejected_without_sinking_the_batch()
    {
        JsonElement body = Json("""
            [
                {"type":"utterance","id":"u1","text":"hi"},
                {"type":"nonsense"},
                "not-even-an-object",
                {"type":"utterance"},
                {"noType":true}
            ]
            """);

        List<object> stored = [];
        (int accepted, int rejected) = await TelemetryEndpoints.IngestBatchAsync(
            "cam1", body, doc => { stored.Add(doc); return Task.CompletedTask; });

        Assert.Equal(1, accepted);
        Assert.Equal(4, rejected);
        UtteranceDocument utterance = Assert.IsType<UtteranceDocument>(Assert.Single(stored));
        Assert.Equal("u1", utterance.Id);
    }

    [Fact]
    public async Task A_lone_object_is_taken_as_a_batch_of_one()
    {
        JsonElement body = Json("""{"type":"sound","id":"s1"}""");

        List<object> stored = [];
        (int accepted, int rejected) = await TelemetryEndpoints.IngestBatchAsync(
            "cam1", body, doc => { stored.Add(doc); return Task.CompletedTask; });

        Assert.Equal((1, 0), (accepted, rejected));
        Assert.IsType<SoundDocument>(Assert.Single(stored));
    }

    [Theory]
    [InlineData("""{"type":"utterance","id":"r1"}""", typeof(UtteranceDocument))]
    [InlineData("""{"type":"diarization","conversation_id":"r1"}""", typeof(DiarizationDocument))]
    [InlineData("""{"type":"conversation_transcript","conversation_id":"r1"}""", typeof(ConversationTranscriptDocument))]
    [InlineData("""{"type":"scene","id":"r1"}""", typeof(SceneDocument))]
    [InlineData("""{"type":"detection","id":"r1"}""", typeof(DetectionDocument))]
    [InlineData("""{"type":"sound","id":"r1"}""", typeof(SoundDocument))]
    public void Every_record_type_parses_and_is_stamped(string json, Type expected)
    {
        IOutputRecord document = TelemetryEndpoints.ParseRecord("cam1", Json(json), ReceivedAt);

        Assert.IsType(expected, document);
        Assert.Equal("cam1", document.CameraId);
        Assert.Equal(ReceivedAt, document.ReceivedAt);
    }

    [Theory]
    [InlineData(""" "a-string" """)]
    [InlineData("""{"type":"unknown","id":"x"}""")]
    [InlineData("""{"id":"x"}""")]
    [InlineData("""{"type":7,"id":"x"}""")]
    [InlineData("""{"type":"utterance"}""")]
    public void Anything_else_is_a_JsonException(string json) =>
        Assert.Throws<JsonException>(() => TelemetryEndpoints.ParseRecord("cam1", Json(json), ReceivedAt));
}
