using Serval.CameraModule;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Serval.Contracts;

namespace Serval.CameraModule.Tests;

public class TelemetryRepositoryTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public TelemetryRepositoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "camera-module-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "telemetry.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private TelemetryRepository NewRepository()
    {
        var options = new CameraModuleOptions();
        options.Output.DatabasePath = _dbPath;
        return new TelemetryRepository(Options.Create(options), NullLogger<TelemetryRepository>.Instance);
    }

    private static TelemetryRecord Record(string id, string? speaker = null, string? conv = null) => new()
    {
        Id = id,
        Timestamp = DateTimeOffset.Parse("2026-07-15T20:33:08+00:00"),
        Transcript = "hello world",
        Language = "en",
        Emotion = "neutral",
        DurationSeconds = 5.5,
        ConversationId = conv,
        Speaker = speaker,
    };

    [Fact]
    public void InitializeDatabase_is_idempotent()
    {
        var repo = NewRepository();
        repo.InitializeDatabase();
        repo.InitializeDatabase(); // must not throw on the second run
        Assert.Contains("Speaker", TableColumns("Telemetry"));
    }

    [Fact]
    public async Task Save_and_read_round_trip_preserves_values_and_nulls()
    {
        var repo = NewRepository();
        repo.InitializeDatabase();

        await repo.SaveAsync(Record("r1", speaker: "speaker_1", conv: "conv-a"));

        List<TelemetryRecord> read = await repo.GetUnsyncedAsync(10);
        TelemetryRecord got = Assert.Single(read);

        Assert.Equal("r1", got.Id);
        Assert.Equal("hello world", got.Transcript);
        Assert.Equal("en", got.Language);
        Assert.Equal("speaker_1", got.Speaker);
        Assert.Equal("conv-a", got.ConversationId);
        Assert.Null(got.AudioEvent); // never set → stays null through the round trip
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-15T20:33:08+00:00").ToUniversalTime(),
            got.Timestamp.ToUniversalTime());
    }

    [Fact]
    public async Task MarkSynced_without_delete_clears_the_row_from_the_unsynced_query()
    {
        var repo = NewRepository();
        repo.InitializeDatabase();
        await repo.SaveAsync(Record("r1"));

        await repo.MarkSyncedAsync(["r1"], delete: false);

        Assert.Empty(await repo.GetUnsyncedAsync(10)); // no longer pending
        Assert.Equal(1, RowCount("Telemetry"));        // but still present
    }

    [Fact]
    public async Task MarkSynced_with_delete_removes_the_row()
    {
        var repo = NewRepository();
        repo.InitializeDatabase();
        await repo.SaveAsync(Record("r1"));

        await repo.MarkSyncedAsync(["r1"], delete: true);

        Assert.Empty(await repo.GetUnsyncedAsync(10));
        Assert.Equal(0, RowCount("Telemetry"));
    }

    [Fact]
    public async Task Saving_a_record_twice_under_one_id_keeps_a_single_row()
    {
        // Orphan recovery may re-diarize a conversation already stored; INSERT OR REPLACE keeps
        // that idempotent rather than crashing on the primary key.
        var repo = NewRepository();
        repo.InitializeDatabase();

        DiarizationDocument Make(int speakers) => new()
        {
            ConversationId = "conv-x",
            StartedAt = DateTimeOffset.Parse("2026-07-15T20:00:00Z"),
            AudioSeconds = 12.0,
            SpeakerCount = speakers,
            Segments = [new DiarizationSegment { Start = 0, End = 1, Speaker = 0 }],
        };

        await repo.SaveRecordAsync("diarization-conv-x", Make(1));
        await repo.SaveRecordAsync("diarization-conv-x", Make(2)); // same id: replaces, not a second row

        (string id, string json) = Assert.Single(await repo.GetUnsyncedRecordsAsync(10));
        Assert.Equal("diarization-conv-x", id);
        Assert.Contains("\"speaker_count\":2", json, StringComparison.Ordinal);
    }

    private List<string> TableColumns(string table)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
        using var reader = cmd.ExecuteReader();

        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private int RowCount(string table)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task A_record_type_the_schema_never_heard_of_needs_no_migration()
    {
        // The Records table's claim, tested rather than asserted in a comment. `sound` arrived in
        // schema 5 and this file was not touched to accommodate it: the whole document is stored
        // as JSON, forwarded verbatim, and never queried into.
        var repo = NewRepository();
        repo.InitializeDatabase();

        var sound = new SoundDocument
        {
            Id = "sound-1",
            Timestamp = DateTimeOffset.Parse("2026-07-15T20:33:08+00:00"),
            Label = "Vehicle horn, car horn, honking",
            Confidence = 0.812,
            IsAlert = false,
            DurationSeconds = 2.4,
            Alternates =
            [
                new SoundAlternate { Label = "Vehicle horn, car horn, honking", Confidence = 0.812 },
                new SoundAlternate { Label = "Vehicle", Confidence = 0.441 },
            ],
        };

        await repo.SaveRecordAsync(sound.Id, sound);

        List<(string Id, string Json)> pending = await repo.GetUnsyncedRecordsAsync(10);
        (string id, string json) = Assert.Single(pending);

        Assert.Equal("sound-1", id);

        // Forwarded exactly as serialized — including the label's commas, which a round trip
        // through columns would have been an opportunity to mangle.
        Assert.Contains("\"type\":\"sound\"", json);
        Assert.Contains("\"label\":\"Vehicle horn, car horn, honking\"", json);
        Assert.Contains("\"is_alert\":false", json);

        await repo.MarkRecordsSyncedAsync([id], delete: false);
        Assert.Empty(await repo.GetUnsyncedRecordsAsync(10));
    }
}
