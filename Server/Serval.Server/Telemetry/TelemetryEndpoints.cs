using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Serval.Contracts;
using Serval.Server.Alerts;
using Serval.Server.Cameras;
using Serval.Server.Configuration;
using Serval.Server.Events;

namespace Serval.Server.Telemetry;

/// <summary>
/// The seam the CameraModule's HttpTelemetrySink delivers to, plus the read APIs the App uses.
/// Ingest accepts the module's batch verbatim — a JSON array of records discriminated by
/// <c>type</c> — stamps each with the camera from the URL, stores it idempotently, and pushes it
/// live to the App.
///
/// The Server's own detection pipeline does not come through here; it writes to the same
/// repository directly, since there is no wire to cross.
/// </summary>
public static class TelemetryEndpoints
{
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static void MapTelemetryEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/cameras/{id}").WithTags("Telemetry")
            .RequireAuthorization(); // the GET reads below; /telemetry overrides this itself

        group.MapPost("/telemetry", async (
            string id,
            JsonElement body,
            HttpContext context,
            TelemetryRepository repository,
            EventBroadcaster events,
            AlertService alerts,
            CameraRepository cameras,
            IOptions<ServerOptions> options,
            CancellationToken ct) =>
        {
            if (!CameraRepository.IsSafeId(id))
            {
                return Results.NotFound();
            }

            if (!IsAuthorized(context, options.Value.ApiKey))
            {
                return Results.Unauthorized();
            }

            (int accepted, int rejected) = await IngestBatchAsync(
                id, body, doc => StoreAsync(id, doc, repository, events, alerts, cameras, ct));

            if (accepted == 0 && rejected > 0)
            {
                return Results.BadRequest(new { error = "No records could be parsed.", rejected });
            }

            return Results.Ok(new { accepted, rejected });
        })
            // Machine-to-machine (the CameraModule), guarded by the X-Api-Key check above rather
            // than by the user-login auth every other route in this group requires. Anonymous to
            // the authorization pipeline, never unauthenticated: with no key configured the check
            // refuses everything.
            .AllowAnonymous();

        group.MapGet("/utterances", async (
            string id, DateTimeOffset? from, DateTimeOffset? to, int? limit,
            TelemetryRepository repository, CancellationToken ct) =>
        {
            (DateTimeOffset f, DateTimeOffset t, int l) = Window(from, to, limit);
            return Results.Ok(await repository.QueryUtterancesAsync(id, f, t, l, ct));
        });

        // Scene descriptions stand alone: a motion-triggered one has no utterance to be attached
        // to, so it cannot be read back through /utterances.
        group.MapGet("/scenes", async (
            string id, DateTimeOffset? from, DateTimeOffset? to, int? limit,
            TelemetryRepository repository, CancellationToken ct) =>
        {
            (DateTimeOffset f, DateTimeOffset t, int l) = Window(from, to, limit);
            return Results.Ok(await repository.QueryScenesAsync(id, f, t, l, ct));
        });

        // Object-detection episodes. Returns anything *present* during the window rather than
        // anything that started in it — see QueryDetectionsAsync — so a car that has been parked
        // since before the window opened is in the answer, which is usually the point of asking.
        group.MapGet("/detections", async (
            string id, DateTimeOffset? from, DateTimeOffset? to, int? limit,
            TelemetryRepository repository, CancellationToken ct) =>
        {
            (DateTimeOffset f, DateTimeOffset t, int l) = Window(from, to, limit);
            return Results.Ok(await repository.QueryDetectionsAsync(id, f, t, l, ct));
        });

        // Non-speech sounds, for the same reason as scenes: the VAD that produces utterances
        // rejects everything here, so none of it can be read back through /utterances.
        group.MapGet("/sounds", async (
            string id, DateTimeOffset? from, DateTimeOffset? to, int? limit,
            TelemetryRepository repository, CancellationToken ct) =>
        {
            (DateTimeOffset f, DateTimeOffset t, int l) = Window(from, to, limit);
            return Results.Ok(await repository.QuerySoundsAsync(id, f, t, l, ct));
        });

        group.MapGet("/conversation-transcripts", async (
            string id, DateTimeOffset? from, DateTimeOffset? to, int? limit,
            TelemetryRepository repository, CancellationToken ct) =>
        {
            (DateTimeOffset f, DateTimeOffset t, int l) = Window(from, to, limit);
            return Results.Ok(await repository.QueryConversationTranscriptsAsync(id, f, t, l, ct));
        });
    }

    /// <summary>
    /// Puts a module-delivered alert in the queue, if that is what it is.
    ///
    /// <para>The camera is read here rather than passed in because this is the only thing on the
    /// ingest path that needs it, and an alert is a small fraction of a batch — a module delivering
    /// a hundred records has at most a few. A camera that has since been deleted takes its id as a
    /// name rather than dropping the alert: something happened, and saying where it happened
    /// imperfectly beats not saying it happened.</para>
    /// </summary>
    private static async Task RaiseIfAlertAsync(
        string cameraId,
        IOutputRecord document,
        AlertService alerts,
        CameraRepository cameras,
        CancellationToken ct)
    {
        bool isAlert = document switch
        {
            DetectionDocument detection => detection.IsAlert,
            SoundDocument sound => sound.IsAlert,
            _ => false,
        };

        if (!isAlert)
        {
            return;
        }

        Camera camera = await cameras.GetAsync(cameraId, ct)
            ?? new Camera { Id = cameraId, Name = cameraId, Streams = [] };

        switch (document)
        {
            case DetectionDocument detection:
                await alerts.RaiseObjectAsync(camera, detection, ct);
                break;
            case SoundDocument sound:
                await alerts.RaiseSoundAsync(camera, sound, ct);
                break;
        }
    }

    /// <summary>
    /// Walks the batch, splitting the two ways a record can fail: one that cannot be parsed is
    /// counted as rejected and skipped, while a failure to store — the database being down — fails
    /// the whole request so the module keeps the batch and redelivers it. Storage is an idempotent
    /// upsert, so redelivery of a partially-landed batch is safe.
    /// </summary>
    internal static async Task<(int Accepted, int Rejected)> IngestBatchAsync(
        string cameraId, JsonElement body, Func<IOutputRecord, Task> store)
    {
        // The module delivers a batch (array); tolerate a lone object too.
        IEnumerable<JsonElement> records = body.ValueKind == JsonValueKind.Array
            ? body.EnumerateArray()
            : [body];

        int accepted = 0, rejected = 0;
        DateTimeOffset receivedAt = DateTimeOffset.UtcNow;

        foreach (JsonElement element in records)
        {
            IOutputRecord document;
            try
            {
                document = ParseRecord(cameraId, element, receivedAt);
            }
            catch (JsonException)
            {
                rejected++;
                continue;
            }

            await store(document);
            accepted++;
        }

        return (accepted, rejected);
    }

    /// <summary>
    /// One entry per record type — the single list to extend when a new type joins the stream.
    /// The upsert is per-type because each document has its own natural key; everything else on
    /// the ingest path (stamping, publishing, alert raising) is common.
    /// </summary>
    private static readonly Dictionary<string, TelemetryKind> Kinds = new(StringComparer.Ordinal)
    {
        ["utterance"] = TelemetryKind.For<UtteranceDocument>((r, d, ct) => r.UpsertUtteranceAsync(d, ct)),
        ["diarization"] = TelemetryKind.For<DiarizationDocument>((r, d, ct) => r.UpsertDiarizationAsync(d, ct)),
        ["conversation_transcript"] = TelemetryKind.For<ConversationTranscriptDocument>(
            (r, d, ct) => r.UpsertConversationTranscriptAsync(d, ct)),
        ["scene"] = TelemetryKind.For<SceneDocument>((r, d, ct) => r.UpsertSceneAsync(d, ct)),

        // Only a finished episode is stored, matching what the Server's own detector does through
        // CameraAiCoordinator.StoreAsync. An open one arrives once a frame while something is
        // still there and is only ever a position to draw; storing it would leave a record
        // insisting someone is still standing there if whatever was watching goes away before it
        // can say otherwise.
        ["detection"] = TelemetryKind.For<DetectionDocument>(
            (r, d, ct) => d.EndedAt is null ? Task.CompletedTask : r.UpsertDetectionAsync(d, ct)),
        ["sound"] = TelemetryKind.For<SoundDocument>((r, d, ct) => r.UpsertSoundAsync(d, ct)),
    };

    private sealed record TelemetryKind(
        Func<JsonElement, IOutputRecord?> Parse,
        Func<TelemetryRepository, IOutputRecord, CancellationToken, Task> Upsert)
    {
        public static TelemetryKind For<T>(Func<TelemetryRepository, T, CancellationToken, Task> upsert)
            where T : class, IOutputRecord =>
            new(
                element => element.Deserialize<T>(ParseOptions),
                (repository, document, ct) => upsert(repository, (T)document, ct));
    }

    /// <summary>
    /// One record off the wire into its typed document, stamped with the camera from the URL.
    /// Anything wrong with the record itself — not an object, unknown type, missing required
    /// fields — is a <see cref="JsonException"/>, the one exception ingest treats as "this record
    /// is bad" rather than "this request failed".
    /// </summary>
    internal static IOutputRecord ParseRecord(string cameraId, JsonElement element, DateTimeOffset receivedAt)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Record is not a JSON object.");
        }

        string type = element.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? ""
            : "";

        if (!Kinds.TryGetValue(type, out TelemetryKind? kind))
        {
            throw new JsonException($"Unknown record type '{type}'.");
        }

        IOutputRecord document = kind.Parse(element)
            ?? throw new JsonException($"null {type}");
        document.CameraId = cameraId;
        document.ReceivedAt = receivedAt;
        return document;
    }

    private static async Task StoreAsync(
        string cameraId, IOutputRecord document, TelemetryRepository repository,
        EventBroadcaster events, AlertService alerts, CameraRepository cameras, CancellationToken ct)
    {
        await Kinds[document.Type].Upsert(repository, document, ct);

        // An open detection episode is a position the next frame supersedes; everything else is
        // the one notification its event ever gets. See EventBroadcaster for why the two cannot
        // share a queue.
        events.Publish(
            new LiveEvent(cameraId, document.Type, document),
            droppable: document is DetectionDocument { EndedAt: null });

        await RaiseIfAlertAsync(cameraId, document, alerts, cameras, ct);
    }

    /// <summary>
    /// The only machine-to-machine route on the server, and so the only one outside the login the
    /// rest of the API is behind. An unset key means no module has been granted access yet, which
    /// is refusal rather than permission — otherwise the default deployment would take
    /// utterances, transcripts and detections for any camera from anyone who can reach the port,
    /// and every client watching /api/events would be shown them as they arrived.
    /// </summary>
    internal static bool IsAuthorized(HttpContext context, string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return false;
        }

        if (!context.Request.Headers.TryGetValue("X-Api-Key", out StringValues provided)
            || provided.Count != 1)
        {
            return false;
        }

        // Fixed-time compare so a wrong key cannot be narrowed down a byte at a time. Lengths are
        // compared first because they are not secret and FixedTimeEquals needs equal spans.
        byte[] expected = Encoding.UTF8.GetBytes(apiKey);
        byte[] actual = Encoding.UTF8.GetBytes(provided[0]!);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>Sensible defaults: last 24h, newest 200 records, when the caller omits them.</summary>
    private static (DateTimeOffset From, DateTimeOffset To, int Limit) Window(
        DateTimeOffset? from, DateTimeOffset? to, int? limit)
    {
        DateTimeOffset t = to ?? DateTimeOffset.UtcNow;
        DateTimeOffset f = from ?? t.AddHours(-24);
        int l = Math.Clamp(limit ?? 200, 1, 1000);
        return (f, t, l);
    }
}
