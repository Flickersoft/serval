using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Serval.Contracts;

namespace Serval.CameraModule.Tests;

/// <summary>
/// The module's side of delivery: that records reach a sink whole. The shape of the records
/// themselves is pinned once, in the shared contract suite.
/// </summary>
public class OutputSinkTests
{
    private static TelemetryRecord MinimalRecord() => new()
    {
        Id = "id-1",
        Timestamp = DateTimeOffset.Parse("2026-07-15T20:33:08Z"),
        Transcript = "hello",
    };

    [Fact]
    public async Task FileTelemetrySink_writes_one_full_record_per_line()
    {
        // The end-to-end guard against interface-typed serialization: if a record is ever
        // serialized as IOutputRecord rather than its runtime type, the written line collapses to
        // {"type":"utterance"} and everything downstream sees an empty transcript.
        string dir = Path.Combine(Path.GetTempPath(), "camera-module-sink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var options = new CameraModuleOptions();
            options.Output.JsonlPath = Path.Combine(dir, "telemetry.jsonl");
            var sink = new FileTelemetrySink(Options.Create(options), NullLogger<FileTelemetrySink>.Instance);

            var diarization = new DiarizationDocument
            {
                ConversationId = "conv-9",
                StartedAt = DateTimeOffset.Parse("2026-07-15T20:31:00Z"),
                AudioSeconds = 12.3,
                SpeakerCount = 2,
                Segments = [new DiarizationSegment { Start = 0.0, End = 4.2, Speaker = 0 }],
                Source = TelemetrySource.Module,
            };

            await sink.DeliverAsync(
                [
                    TelemetryJson.Serialize(MinimalRecord().ToDocument()),
                    TelemetryJson.Serialize(diarization),
                ],
                TestContext.Current.CancellationToken);

            string[] lines = await File.ReadAllLinesAsync(
                options.Output.JsonlPath, TestContext.Current.CancellationToken);
            Assert.Equal(2, lines.Length);

            using var utterance = JsonDocument.Parse(lines[0]);
            Assert.Equal("utterance", utterance.RootElement.GetProperty("type").GetString());
            Assert.Equal("hello", utterance.RootElement.GetProperty("transcript").GetString());

            using var parsedDiarization = JsonDocument.Parse(lines[1]);
            Assert.Equal("diarization", parsedDiarization.RootElement.GetProperty("type").GetString());
            Assert.Equal(2, parsedDiarization.RootElement.GetProperty("speaker_count").GetInt32());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Live_speaker_labels_are_marked_as_such_on_the_way_out()
    {
        // speaker_source exists so a consumer cannot mistake the live best-effort guess for the
        // considered answer in the conversation's diarization record. It must appear exactly when
        // a speaker does.
        Assert.Null(MinimalRecord().ToDocument().SpeakerSource);

        TelemetryRecord labelled = new()
        {
            Id = "id-2",
            Timestamp = DateTimeOffset.UtcNow,
            Transcript = "hi",
            ConversationId = "conv-1",
            Speaker = "speaker_0",
        };

        Assert.Equal("live", labelled.ToDocument().SpeakerSource);
    }

    [Fact]
    public void Module_produced_records_are_attributed_to_the_module()
    {
        Assert.Equal(TelemetrySource.Module, MinimalRecord().ToDocument().Source);
    }

    [Fact]
    public void Numeric_fields_are_rounded_to_the_documented_precision()
    {
        TelemetryRecord record = new()
        {
            Id = "id-4",
            Timestamp = DateTimeOffset.UtcNow,
            Transcript = "hi",
            DurationSeconds = 5.512345,
        };

        UtteranceDocument document = record.ToDocument();
        Assert.Equal(5.512, document.DurationSeconds, 6);
    }
}
