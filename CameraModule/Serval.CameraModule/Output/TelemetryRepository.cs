using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serval.Contracts;

using Serval.Ai;

namespace Serval.CameraModule;

/// <summary>
/// An analysed utterance as stored locally. Internal only — see <see cref="UtteranceDocument"/>
/// for the shape that leaves this process.
/// </summary>
public sealed class TelemetryRecord
{
    public required string Id { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string Transcript { get; init; }
    public string? Language { get; init; }
    public string? Emotion { get; init; }
    public string? AudioEvent { get; init; }
    public double DurationSeconds { get; init; }

    /// <summary>Joins this utterance to its conversation's diarization record.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Live, best-effort speaker label. Null when none could be justified.</summary>
    public string? Speaker { get; init; }

    // No scene description: each is published as its own scene record, so a consumer correlates
    // the two on timestamp and picks its own idea of how near is near enough.
}

/// <summary>Maps the locally-stored shape onto the wire contract shared with the Server.</summary>
public static class TelemetryDocuments
{
    public static UtteranceDocument ToDocument(this TelemetryRecord record) => new()
    {
        Id = record.Id,
        ConversationId = record.ConversationId,
        Timestamp = record.Timestamp,
        Transcript = record.Transcript,
        Language = record.Language,
        Emotion = record.Emotion,
        AudioEvent = record.AudioEvent,
        DurationSeconds = Math.Round(record.DurationSeconds, 3),
        Speaker = record.Speaker,

        // Only ever "live" here, and only when a speaker was actually determined. Stated
        // explicitly so a consumer cannot mistake this best-effort guess for the considered
        // answer in the conversation's diarization record.
        SpeakerSource = record.Speaker is null ? null : "live",
        Source = TelemetrySource.Module,
    };

}

/// <summary>
/// Durable outbox. Records survive restarts and network gaps until a sink confirms
/// delivery, which is what makes the eventual HTTP sync safe to retry.
/// </summary>
public sealed class TelemetryRepository
{
    /// <summary>The wire's serialization settings, shared with the Server via Serval.Contracts.</summary>
    public static JsonSerializerOptions JsonOptions => TelemetryJson.Options;

    private readonly string _connectionString;
    private readonly ILogger<TelemetryRepository> _logger;

    public TelemetryRepository(IOptions<CameraModuleOptions> options, ILogger<TelemetryRepository> logger)
    {
        _logger = logger;
        string path = options.Value.Output.DatabasePath;

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    public void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS Telemetry (
                Id               TEXT PRIMARY KEY,
                Timestamp        TEXT NOT NULL,
                Transcript       TEXT NOT NULL,
                Language         TEXT NULL,
                Emotion          TEXT NULL,
                AudioEvent       TEXT NULL,
                DurationSeconds  REAL NOT NULL,
                ConversationId   TEXT NULL,
                Speaker          TEXT NULL,
                IsSynced         INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS IX_Telemetry_Unsynced
                ON Telemetry (IsSynced) WHERE IsSynced = 0;

            -- Everything that is not an utterance: diarization, scene, conversation_transcript,
            -- sound. Stored as whole JSON documents rather than columns — nothing here queries
            -- into them, they are written once and forwarded verbatim, and a shape kept in one
            -- place cannot drift from the contract the way parallel columns do. A new record type
            -- therefore needs no change to this file at all. Utterances alone earn real columns,
            -- because GetConversationUtterancesAsync queries into them.
            CREATE TABLE IF NOT EXISTS Records (
                Id        TEXT PRIMARY KEY,
                Type      TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                Json      TEXT NOT NULL,
                IsSynced  INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS IX_Records_Unsynced
                ON Records (IsSynced) WHERE IsSynced = 0;
            """;
        command.ExecuteNonQuery();

        _logger.LogInformation("Telemetry store ready at {Path}", _connectionString);
    }

    public async Task SaveAsync(TelemetryRecord record, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Telemetry
                (Id, Timestamp, Transcript, Language, Emotion, AudioEvent, DurationSeconds,
                 ConversationId, Speaker, IsSynced)
            VALUES
                ($id, $timestamp, $transcript, $language, $emotion, $audioEvent, $duration,
                 $conversationId, $speaker, 0)
            """;

        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$timestamp", record.Timestamp.ToUniversalTime().ToString("o"));
        command.Parameters.AddWithValue("$transcript", record.Transcript);
        command.Parameters.AddWithValue("$language", (object?)record.Language ?? DBNull.Value);
        command.Parameters.AddWithValue("$emotion", (object?)record.Emotion ?? DBNull.Value);
        command.Parameters.AddWithValue("$audioEvent", (object?)record.AudioEvent ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", record.DurationSeconds);
        command.Parameters.AddWithValue("$conversationId", (object?)record.ConversationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$speaker", (object?)record.Speaker ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<TelemetryRecord>> GetUnsyncedAsync(int limit, CancellationToken cancellationToken = default)
    {
        var results = new List<TelemetryRecord>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        // Named columns, not SELECT *: positional reads break silently on any schema change.
        command.CommandText = """
            SELECT Id, Timestamp, Transcript, Language, Emotion, AudioEvent, DurationSeconds,
                   ConversationId, Speaker
            FROM Telemetry
            WHERE IsSynced = 0
            ORDER BY Timestamp
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new TelemetryRecord
            {
                Id = reader.GetString(0),
                Timestamp = DateTimeOffset.Parse(reader.GetString(1)),
                Transcript = reader.GetString(2),
                Language = reader.IsDBNull(3) ? null : reader.GetString(3),
                Emotion = reader.IsDBNull(4) ? null : reader.GetString(4),
                AudioEvent = reader.IsDBNull(5) ? null : reader.GetString(5),
                DurationSeconds = reader.GetDouble(6),
                ConversationId = reader.IsDBNull(7) ? null : reader.GetString(7),
                Speaker = reader.IsDBNull(8) ? null : reader.GetString(8),
            });
        }

        return results;
    }

    /// <summary>
    /// The live utterances belonging to one conversation, for the offline pass to re-attribute.
    ///
    /// Read back out of the outbox rather than held in memory: a conversation can run for half an
    /// hour, and its utterances were durably written the moment they were transcribed. This also
    /// means a conversation recovered after a crash still has its transcripts to work with — they
    /// outlived the process that produced them.
    ///
    /// Rows already delivered and deleted (<c>DeleteAfterSync</c>) are simply absent, which
    /// degrades to a diarization record with fewer attributed turns rather than a wrong one.
    /// </summary>
    public async Task<IReadOnlyList<LiveUtterance>> GetConversationUtterancesAsync(
        string conversationId, CancellationToken cancellationToken = default)
    {
        var results = new List<LiveUtterance>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Timestamp, DurationSeconds, Transcript, Emotion
            FROM Telemetry
            WHERE ConversationId = $id
            ORDER BY Timestamp
            """;
        command.Parameters.AddWithValue("$id", conversationId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new LiveUtterance(
                DateTimeOffset.Parse(reader.GetString(0)),
                reader.GetDouble(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return results;
    }

    /// <summary>
    /// Queues a record whose only local representation is its published JSON. Idempotent by id,
    /// so re-processing a crash-recovered conversation replaces rather than duplicates.
    /// </summary>
    public async Task SaveRecordAsync(
        string id, IOutputRecord record, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO Records (Id, Type, CreatedAt, Json, IsSynced)
            VALUES ($id, $type, $createdAt, $json, 0)
            """;

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$type", record.Type);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("o"));
        command.Parameters.AddWithValue(
            "$json", JsonSerializer.Serialize(record, record.GetType(), JsonOptions));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Undelivered JSON-only records, as (id, json) pairs. The JSON is forwarded exactly as
    /// stored — round-tripping it through a type here would be an opportunity to change it.
    /// </summary>
    public async Task<List<(string Id, string Json)>> GetUnsyncedRecordsAsync(
        int limit, CancellationToken cancellationToken = default)
    {
        var results = new List<(string, string)>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Json FROM Records
            WHERE IsSynced = 0
            ORDER BY CreatedAt
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add((reader.GetString(0), reader.GetString(1)));
        }

        return results;
    }

    public Task MarkRecordsSyncedAsync(
        IReadOnlyCollection<string> ids, bool delete, CancellationToken cancellationToken = default) =>
        MarkAsync("Records", "Id", ids, delete, cancellationToken);

    public async Task MarkSyncedAsync(
        IReadOnlyCollection<string> ids,
        bool delete,
        CancellationToken cancellationToken = default)
    {
        await MarkAsync("Telemetry", "Id", ids, delete, cancellationToken);
    }

    /// <summary>
    /// Shared body of the three mark-delivered paths. The table and key column are internal
    /// constants, never user input, so interpolating them cannot be an injection route.
    /// </summary>
    private async Task MarkAsync(
        string table,
        string keyColumn,
        IReadOnlyCollection<string> ids,
        bool delete,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = delete
            ? $"DELETE FROM {table} WHERE {keyColumn} = $id"
            : $"UPDATE {table} SET IsSynced = 1 WHERE {keyColumn} = $id";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "$id";
        command.Parameters.Add(parameter);

        foreach (string id in ids)
        {
            parameter.Value = id;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
