using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using Serval.Ai;
using Serval.Server.Configuration;

namespace Serval.Server.Clips;

/// <summary>
/// Describes a saved clip in one sentence, from frames sampled along it.
///
/// This is the only place in Serval that describes a <em>span</em>. Every other description is one
/// frame's — a scene record is what the camera could see at an instant — so a clip's "what's in it"
/// could not be assembled from them without claiming a still was a summary.
///
/// Entirely best-effort, and separate from <see cref="ClipWriteWorker"/> for that reason. A server
/// with no vision model, or one whose inference fails, still saves clips; they simply have no
/// summary and the App omits the block rather than showing an empty one. Nothing waits on this.
/// </summary>
public sealed class ClipSummaryWorker : BackgroundService
{
    private readonly Channel<ObjectId> _queue = Channel.CreateUnbounded<ObjectId>();

    private readonly IServiceProvider _services;
    private readonly ClipRepository _clips;
    private readonly ClipStorage _storage;
    private readonly ClipMedia _media;
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly ILogger<ClipSummaryWorker> _logger;

    public ClipSummaryWorker(
        IServiceProvider services,
        ClipRepository clips,
        ClipStorage storage,
        ClipMedia media,
        IOptionsMonitor<ServerOptions> options,
        ILogger<ClipSummaryWorker> logger)
    {
        _services = services;
        _clips = clips;
        _storage = storage;
        _media = media;
        _options = options;
        _logger = logger;
    }

    private VisionOptions Vision => _options.CurrentValue.Ai.Vision;

    /// <summary>
    /// The vision runner, or null on a server that has none.
    ///
    /// Resolved through the provider rather than injected because it is only registered when server
    /// AI is on, its vision half is enabled, and the weights are actually present — three
    /// conditions this worker must not inherit, since it has to exist either way for
    /// <see cref="ClipWriteWorker"/> to hand work to.
    /// </summary>
    private IVisionInferenceRunner? Runner => _services.GetService<IVisionInferenceRunner>();

    public void Enqueue(ObjectId id) => _queue.Writer.TryWrite(id);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (ObjectId id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await DescribeAsync(id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A clip without a summary is a clip. Log and move on.
                _logger.LogWarning(ex, "Could not summarise clip {ClipId}.", id);
            }
        }
    }

    private async Task DescribeAsync(ObjectId id, CancellationToken cancellationToken)
    {
        if (Runner is not { } runner)
        {
            _logger.LogDebug("No vision model in this process; clip {ClipId} keeps no summary.", id);
            return;
        }

        SavedClip? clip = await _clips.GetAsync(id, cancellationToken);
        if (clip is not { State: ClipState.Ready })
        {
            return;
        }

        string video = _storage.VideoFor(id);
        if (!File.Exists(video))
        {
            return;
        }

        int wanted = Math.Clamp(Vision.ClipFrames, 1, runner.MaxFrames);
        IReadOnlyList<byte[]> jpegs =
            await _media.SampleFramesAsync(video, clip.DurationSeconds, wanted, cancellationToken);

        if (jpegs.Count == 0)
        {
            _logger.LogWarning("No frames could be sampled from clip {ClipId}; it keeps no summary.", id);
            return;
        }

        // Stamped at their true position in the clip so a backend that tells the model how far
        // apart its frames are gets the real spacing, not the wall-clock instant they were decoded.
        var frames = new List<VisionFrame>(jpegs.Count);
        for (int i = 0; i < jpegs.Count; i++)
        {
            double offset = clip.DurationSeconds * (2.0 * i + 1) / (2.0 * jpegs.Count);
            frames.Add(new VisionFrame(jpegs[i], clip.From.AddSeconds(offset)));
        }

        string prompt = Vision.ClipPrompt
            .Replace("{count}", frames.Count.ToString(CultureInfo.InvariantCulture))
            .Replace("{seconds}", clip.DurationSeconds.ToString("0", CultureInfo.InvariantCulture));

        string text = (await runner.RunInferenceAsync(frames, prompt, cancellationToken)).Trim();

        if (text.Length == 0)
        {
            return;
        }

        await _clips.SetSummaryAsync(id, text, cancellationToken);

        _logger.LogInformation("Summarised clip {ClipId} from {Frames} frames.", id, frames.Count);
    }
}
