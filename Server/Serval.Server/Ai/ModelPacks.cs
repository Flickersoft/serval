using System.Diagnostics;
using Microsoft.Extensions.Options;
using Serval.Server.Configuration;

namespace Serval.Server.Ai;

/// <summary>
/// One AI capability as Settings → AI offers it: the files it needs, what fetching them costs, and
/// the switch that turns it on.
/// </summary>
/// <param name="Id">Stable identifier, the one the App sends back.</param>
/// <param name="DownloadBytes">Measured from the sources' Content-Length; 0 when it ships in the image.</param>
/// <param name="Memory">A measured working-set figure where one exists (Docs/memory.md), otherwise null.</param>
/// <param name="Switch">
/// The catalog setting that enables it, or null for speech, which loads whenever server AI is on and
/// its files exist.
/// </param>
/// <param name="SkipFlag">The fetch-models.sh variable that leaves this pack out.</param>
public sealed record ModelPack(
    string Id,
    string Name,
    string Description,
    long DownloadBytes,
    string? Memory,
    string? Switch,
    IReadOnlyList<string> Requires,
    string SkipFlag,
    Func<ServerOptions, IReadOnlyList<string>> Files)
{
    public static readonly IReadOnlyList<ModelPack> All =
    [
        new("detection", "Object detection",
            "People, vehicles and animals, tracked across frames — YOLO26n, the model Serval's "
            + "defaults are tuned against. Ships inside the server image.",
            DownloadBytes: 0, Memory: null, Switch: "Serval:Ai:Detection:Enabled",
            Requires: [], SkipFlag: "SKIP_DETECTION",
            Files: o => [o.Ai.Detection.ModelPath, o.Ai.Detection.LabelsPath]),

        new("speech", "Speech",
            "Transcripts of what is said, with emotion — SenseVoice, plus the voice-activity model "
            + "that decides when someone is talking. About 240 MB once unpacked.",
            DownloadBytes: 1_047_870_769, Memory: null, Switch: null,
            Requires: [], SkipFlag: "SKIP_SPEECH",
            Files: o => [o.Ai.Asr.ModelPath, o.Ai.Asr.TokensPath, o.Ai.Vad.ModelPath]),

        new("speaker", "Speaker labels",
            "Which voice is which within a conversation, and diarization after it ends.",
            DownloadBytes: 35_239_608, Memory: null, Switch: "Serval:Ai:Speaker:Enabled",
            Requires: ["speech"], SkipFlag: "SKIP_SPEAKER",
            Files: o => [o.Ai.Speaker.EmbeddingModelPath, o.Ai.Speaker.SegmentationModelPath]),

        new("sound", "Sound events",
            "527 kinds of sound — breaking glass, alarms, a barking dog.",
            DownloadBytes: 111_034_072, Memory: null, Switch: "Serval:Ai:Sound:Enabled",
            Requires: [], SkipFlag: "SKIP_SOUND",
            Files: o => [o.Ai.Sound.ModelPath, o.Ai.Sound.LabelsPath]),

        new("scene", "Scene descriptions",
            "A vision-language model (Qwen3-VL 2B) writes what is happening in a scene, and "
            + "summarises saved clips. The heaviest by far.",
            DownloadBytes: 2_279_480_352, Memory: "~2.9 GB of memory once loaded",
            Switch: "Serval:Ai:Vision:Enabled",
            Requires: [], SkipFlag: "SKIP_VISION",
            Files: o => [o.Ai.Vision.ModelPath, o.Ai.Vision.MmprojPath]),
    ];

    public static ModelPack? Find(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Downloads model packs into the models directory and turns them on — the server half of
/// Settings → AI.
///
/// <para><b>Runs the image's own <c>fetch-models.sh</c></b> rather than a second downloader, so the
/// URLs, the archive handling and the bundled-detector copy live in one place, the same one the
/// compose one-shot uses. Packs not asked for are passed as that script's <c>SKIP_*</c> flags.</para>
///
/// <para><b>One job at a time, held in memory.</b> A restart mid-download loses the job, not the
/// files: the script skips whatever is already complete, so asking again resumes rather than
/// starting over.</para>
///
/// <para><b>Switches are written only after the files are verified</b>, through the same overlay a
/// settings save uses, so a failed download never leaves a capability switched on with nothing to
/// load. They are all restart-gated, which is why the App offers a restart next.</para>
/// </summary>
public sealed class ModelInstaller
{
    private const int LogLines = 40;

    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly IConfiguration _configuration;
    private readonly SettingsConfigurationProvider _settings;
    private readonly ILogger<ModelInstaller> _logger;
    private readonly Lock _gate = new();

    private InstallJob? _job;

    public ModelInstaller(
        IOptionsMonitor<ServerOptions> options,
        IConfiguration configuration,
        SettingsConfigurationProvider settings,
        ILogger<ModelInstaller> logger)
    {
        _options = options;
        _configuration = configuration;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Where the weights live: <c>models/</c> under the content root, as every default path assumes.</summary>
    public static string ModelsDirectory => Path.GetFullPath("models");

    /// <summary>The image's copy of the fetcher, or a checkout's when running from source.</summary>
    public static string? ScriptPath
    {
        get
        {
            string bundled = Path.Combine(AppContext.BaseDirectory, "fetch-models.sh");
            if (File.Exists(bundled))
            {
                return bundled;
            }

            for (DirectoryInfo? dir = new(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "scripts", "fetch-models.sh");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    /// <summary>The detector bundled with the image, beside the script.</summary>
    private static bool DetectorBundled =>
        ScriptPath is { } script
        && File.Exists(Path.Combine(Path.GetDirectoryName(script)!, "models-seed", "detect", "model.onnx"));

    public ModelsStatus GetStatus()
    {
        ServerOptions options = _options.CurrentValue;
        bool aiOn = Bool("Serval:ServerAi:Enabled");

        List<ModelPackStatus> packs = [];
        foreach (ModelPack pack in ModelPack.All)
        {
            bool installed = pack.Files(options).All(File.Exists);
            string? unavailable = !installed && pack.Id == "detection" && !DetectorBundled
                ? "This server image has no bundled detector. Export one with scripts/export-detector.sh."
                : null;

            packs.Add(new ModelPackStatus(
                pack.Id, pack.Name, pack.Description, pack.DownloadBytes, pack.Memory, pack.Requires,
                Installed: installed,
                Enabled: aiOn && (pack.Switch is null || Bool(pack.Switch)),
                Unavailable: unavailable));
        }

        string? problem = ScriptPath is null
            ? "fetch-models.sh is not in this image, so models cannot be downloaded from here."
            : WritableProblem();

        InstallJob? job;
        lock (_gate)
        {
            job = _job;
        }

        return new ModelsStatus(packs, problem, job?.Snapshot(ModelsDirectory));
    }

    /// <summary>
    /// Starts downloading <paramref name="ids"/> (and what they require). Returns null when started,
    /// or why it could not start.
    /// </summary>
    public string? Start(IReadOnlyList<string> ids, string? user)
    {
        List<ModelPack> requested = [];
        foreach (string id in ids)
        {
            ModelPack pack = ModelPack.Find(id)
                ?? throw new SettingsValidationException($"'{id}' is not a model pack.");
            requested.Add(pack);
        }

        foreach (ModelPack pack in requested.ToList())
        {
            foreach (string dependency in pack.Requires)
            {
                if (!requested.Any(p => p.Id == dependency))
                {
                    requested.Add(ModelPack.Find(dependency)!);
                }
            }
        }

        if (requested.Count == 0)
        {
            throw new SettingsValidationException("Choose at least one model to install.");
        }

        if (ScriptPath is not { } script)
        {
            return "fetch-models.sh is not in this image, so models cannot be downloaded from here.";
        }

        if (WritableProblem() is { } readOnly)
        {
            return readOnly;
        }

        lock (_gate)
        {
            if (_job is { Finished: false })
            {
                return "A download is already running.";
            }

            _job = new InstallJob(requested.Select(p => p.Id).ToList());
        }

        InstallJob job = _job;
        _ = Task.Run(() => RunAsync(job, script, requested, user));
        return null;
    }

    private async Task RunAsync(InstallJob job, string script, List<ModelPack> requested, string? user)
    {
        try
        {
            var start = new ProcessStartInfo("bash", [script])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.Environment["MODEL_DIR"] = ModelsDirectory;
            foreach (ModelPack pack in ModelPack.All)
            {
                start.Environment[pack.SkipFlag] = requested.Contains(pack) ? "0" : "1";
            }

            _logger.LogInformation(
                "Installing AI models [{Packs}] into {Directory}, requested by '{User}'.",
                string.Join(", ", job.Packs), ModelsDirectory, user ?? "unknown");

            using Process process = Process.Start(start)
                ?? throw new InvalidOperationException("bash could not be started.");

            Task stdout = Pump(process.StandardOutput, job, isStderr: false);
            Task stderr = Pump(process.StandardError, job, isStderr: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);

            ServerOptions options = _options.CurrentValue;
            List<string> missing = requested
                .Where(p => !p.Files(options).All(File.Exists))
                .Select(p => p.Name)
                .ToList();

            if (process.ExitCode != 0 || missing.Count > 0)
            {
                string reason = process.ExitCode != 0
                    ? $"The download stopped with exit code {process.ExitCode}. Asking again resumes "
                      + "where it left off."
                    : $"Finished, but these are still missing: {string.Join(", ", missing)}.";
                job.Finish(reason);
                _logger.LogWarning("AI model install failed: {Reason}", reason);
                return;
            }

            // The master switch plus each pack's own. Speech has none: it loads whenever server AI
            // is on and its files exist.
            Dictionary<string, string?> writes = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Serval:ServerAi:Enabled"] = "true",
            };
            foreach (ModelPack pack in requested)
            {
                if (pack.Switch is { } key)
                {
                    writes[key] = "true";
                }
            }

            await _settings.SaveAsync(writes, user, CancellationToken.None);
            job.Finish(error: null);
            _logger.LogInformation("AI models installed and switched on; they load at the next restart.");
        }
        catch (Exception ex)
        {
            job.Finish($"The download could not run: {ex.Message}");
            _logger.LogError(ex, "AI model install failed.");
        }
    }

    /// <summary>
    /// Keeps what the script says it is doing (its stdout) and what failed (its stderr's error
    /// lines). The rest of stderr is curl's progress meter — a header row and a stream of numbers —
    /// which would otherwise become the "current step".
    /// </summary>
    private static async Task Pump(StreamReader reader, InstallJob job, bool isStderr)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!isStderr
                || trimmed.StartsWith("ERROR", StringComparison.Ordinal)
                || trimmed.StartsWith("WARNING", StringComparison.Ordinal)
                || trimmed.StartsWith("curl:", StringComparison.Ordinal)
                || trimmed.StartsWith("tar:", StringComparison.Ordinal))
            {
                job.Log(trimmed, LogLines);
            }
        }
    }

    private bool Bool(string key) =>
        bool.TryParse(_configuration[key], out bool value) && value;

    private static string? WritableProblem()
    {
        try
        {
            Directory.CreateDirectory(ModelsDirectory);
            string probe = Path.Combine(ModelsDirectory, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The models directory ({ModelsDirectory}) is read-only here — it is mounted "
                + "':ro' in this deployment's compose file. Run the fetch-models one-shot on the host "
                + "instead, or mount it writable.";
        }
    }

    /// <summary>
    /// The size of whichever file is being written right now — the download in progress — so the
    /// figure beside "Downloading SenseVoice…" is that download's, not the directory's. Summing the
    /// directory instead counts an archive twice while it is unpacked beside itself, and reads as
    /// twice the advertised size. Archives land in /tmp before they are unpacked; single files go
    /// straight into the models directory.
    /// </summary>
    private static long? CurrentFileBytes(string modelsDirectory)
    {
        IEnumerable<string> archives = Directory.Exists("/tmp")
            ? Directory.EnumerateFiles("/tmp", "*.tar.bz2")
            : [];
        IEnumerable<string> models = Directory.Exists(modelsDirectory)
            ? Directory.EnumerateFiles(modelsDirectory, "*", SearchOption.AllDirectories)
            : [];

        DateTime recent = DateTime.UtcNow.AddSeconds(-5);
        FileInfo? current = archives.Concat(models)
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists && file.LastWriteTimeUtc >= recent)
            .MaxBy(file => file.LastWriteTimeUtc);

        return current?.Length;
    }

    private sealed class InstallJob(IReadOnlyList<string> packs)
    {
        private readonly Lock _gate = new();
        private readonly Queue<string> _log = new();

        public IReadOnlyList<string> Packs { get; } = packs;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedAt { get; private set; }
        public string? Error { get; private set; }
        public bool Finished => FinishedAt is not null;

        public void Log(string line, int keep)
        {
            lock (_gate)
            {
                _log.Enqueue(line);
                while (_log.Count > keep)
                {
                    _log.Dequeue();
                }
            }
        }

        public void Finish(string? error)
        {
            Error = error;
            FinishedAt = DateTimeOffset.UtcNow;
        }

        public ModelInstallJob Snapshot(string modelsDirectory)
        {
            string[] log;
            lock (_gate)
            {
                log = [.. _log];
            }

            return new ModelInstallJob(
                Packs, StartedAt, FinishedAt, Succeeded: Finished && Error is null, Error,
                Step: log.LastOrDefault(),
                BytesWritten: Finished ? null : CurrentFileBytes(modelsDirectory),
                Log: log);
        }
    }
}

public sealed record ModelsStatus(
    IReadOnlyList<ModelPackStatus> Packs,
    string? CannotInstall,
    ModelInstallJob? Job);

public sealed record ModelPackStatus(
    string Id,
    string Name,
    string Description,
    long DownloadBytes,
    string? Memory,
    IReadOnlyList<string> Requires,
    bool Installed,
    bool Enabled,
    string? Unavailable);

public sealed record ModelInstallJob(
    IReadOnlyList<string> Packs,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    bool Succeeded,
    string? Error,
    string? Step,
    long? BytesWritten,
    IReadOnlyList<string> Log);

public sealed record InstallModelsRequest(IReadOnlyList<string> Packs);

public static class ModelEndpoints
{
    public static void MapModelEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/ai/models")
            .WithTags("AI models")
            .RequireAuthorization("Admin");

        group.MapGet("", (ModelInstaller installer) => Results.Ok(installer.GetStatus()))
            .WithSummary("Each AI model pack: installed or not, switched on or not, and any download in progress.")
            .Produces<ModelsStatus>();

        group.MapPost("/install", (InstallModelsRequest request, ModelInstaller installer, HttpContext http) =>
            {
                string? refused = installer.Start(request.Packs, Auth.TokenService.GetUserId(http.User));
                return refused is null
                    ? Results.Accepted("/api/ai/models", installer.GetStatus())
                    : Results.Json(new { error = refused }, statusCode: StatusCodes.Status409Conflict);
            })
            .WithSummary("Download model packs and switch them on; they load at the next restart.")
            .WithDescription(
                "Packs a requested pack needs are added (speaker labels need speech). Returns at once; "
                + "poll GET for progress. 409 while another download runs, or where the models "
                + "directory is read-only.")
            .Produces<ModelsStatus>(StatusCodes.Status202Accepted);
    }
}
