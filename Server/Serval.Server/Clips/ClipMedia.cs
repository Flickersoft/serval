using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
using Serval.Server.Configuration;
using Serval.Server.Ingest;

namespace Serval.Server.Clips;

/// <summary>
/// The two small ffmpeg jobs a saved clip needs once its video exists: a poster frame to show it
/// by, and the file's real duration.
///
/// Both run against the finished <c>clip.mp4</c> rather than against the segment pipe. That is
/// simpler and it is also more truthful — the summed <c>#EXTINF</c> of the segments says how long
/// the recording claimed to be, while the file says how long the file is, and it is the file that
/// gets played.
/// </summary>
public sealed class ClipMedia
{
    /// <summary>
    /// Poster area, as a 480x270 frame's worth. Sized for 13a's 124px card thumbnail on a 2×
    /// display with room to spare, rather than for the detail view, which plays the video itself.
    ///
    /// An area rather than a width so the card gets the same amount of picture from every camera —
    /// a 480px-wide poster off a 32:9 camera is 135px tall, which is a thumbnail of nothing. A
    /// constant rather than a setting: it is sized for a card whose dimensions are fixed in the
    /// app, so there is nothing here for an operator to weigh.
    /// </summary>
    private const long PosterPixels = 480 * 270;

    private readonly IngestOptions _options;
    private readonly ILogger<ClipMedia> _logger;

    public ClipMedia(IOptions<ServerOptions> options, ILogger<ClipMedia> logger)
    {
        _options = options.Value.Ingest;
        _logger = logger;
    }

    /// <summary>
    /// Grabs the frame at <paramref name="seekSeconds"/> into the clip, as the picture it is shown by.
    ///
    /// Which frame is the caller's decision, because the two callers want genuinely different
    /// answers: a saved clip has no privileged instant and takes its middle, while an alert has
    /// exactly one — the frame the detection fired on — and a poster of anything else would not
    /// match the box drawn over it.
    ///
    /// Best-effort. A clip with no poster still plays, so a failure here is logged and swallowed
    /// rather than failing the save.
    /// </summary>
    public async Task<bool> TryWritePosterAsync(
        string clipPath, string posterPath, double seekSeconds, CancellationToken cancellationToken)
    {
        string seek = Math.Max(seekSeconds, 0).ToString("0.###", CultureInfo.InvariantCulture);

        try
        {
            (int exitCode, string errors) = await RunAsync(
                _options.FfmpegPath,
                [
                    "-nostdin", "-hide_banner", "-loglevel", "warning",
                    "-ss", seek,
                    "-i", clipPath,
                    "-frames:v", "1",
                    "-vf", PixelBudget.ScaleFilter(PosterPixels)!,
                    "-f", "mjpeg",
                    "-y", posterPath,
                ],
                cancellationToken);

            if (exitCode == 0 && File.Exists(posterPath))
            {
                return true;
            }

            _logger.LogWarning("Could not write a poster for {Clip}: ffmpeg exited {Code}. {Errors}",
                clipPath, exitCode, errors);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not write a poster for {Clip}.", clipPath);
        }

        return false;
    }

    /// <summary>
    /// The clip's duration according to the file, or null if ffprobe could not say — in which case
    /// the caller keeps the figure it computed from the segment index.
    /// </summary>
    public async Task<double?> TryReadDurationAsync(string clipPath, CancellationToken cancellationToken)
    {
        try
        {
            (int exitCode, string output) = await RunAsync(
                _options.FfprobePath,
                [
                    "-v", "error",
                    "-show_entries", "format=duration",
                    "-of", "default=noprint_wrappers=1:nokey=1",
                    clipPath,
                ],
                cancellationToken,
                captureStandardOutput: true);

            if (exitCode == 0
                && double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                && seconds > 0)
            {
                return seconds;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not probe the duration of {Clip}.", clipPath);
        }

        return null;
    }

    /// <summary>
    /// Frames sampled evenly across the clip, as JPEG bytes, for the vision pass.
    ///
    /// Evenly rather than at the detections: the summary is meant to describe the arc of the clip,
    /// and sampling where the model already fired would describe its busiest second several times
    /// and the rest not at all.
    /// </summary>
    public async Task<IReadOnlyList<byte[]>> SampleFramesAsync(
        string clipPath, double durationSeconds, int count, CancellationToken cancellationToken)
    {
        var frames = new List<byte[]>(count);

        for (int i = 0; i < count; i++)
        {
            // Offsets at the midpoints of equal slices, so neither the first nor the last frame is
            // the one at the very edge of the file — where a seek can land on nothing.
            double offset = durationSeconds * (2.0 * i + 1) / (2.0 * count);
            byte[]? frame = await TryReadFrameAsync(clipPath, offset, cancellationToken);

            if (frame is { Length: > 0 })
            {
                frames.Add(frame);
            }
        }

        return frames;
    }

    private async Task<byte[]?> TryReadFrameAsync(
        string clipPath, double offset, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_options.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        List<string> args =
        [
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-ss", offset.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", clipPath,
            "-frames:v", "1",
        ];

        // The same budget the live snapshots are held to, for the same reason: this frame is going
        // to the vision model, which bills by pixel count. Sampled from the recorded stream rather
        // than the detect one, so without it a 4K camera sends several 8 MP frames per summary.
        if (PixelBudget.ScaleFilter(_options.SnapshotMaxMegapixels) is { } scale)
        {
            args.AddRange(["-vf", scale]);
        }

        args.AddRange(["-f", "mjpeg", "pipe:1"]);

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start ffmpeg to sample a frame.");

            using var buffer = new MemoryStream();
            Task copy = process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken);
            Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);

            await copy;
            await errors;
            await process.WaitForExitAsync(cancellationToken);

            return process.ExitCode == 0 ? buffer.ToArray() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not sample a frame at {Offset}s from {Clip}.", offset, clipPath);
            return null;
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool captureStandardOutput = false)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {executable}.");

        // Both pipes are read before waiting: a process that fills one while nobody drains it
        // blocks forever, and ffmpeg is chatty on stderr.
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, captureStandardOutput ? await output : await errors);
    }
}
