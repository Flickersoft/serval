using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
using Serval.Server.Clips;
using Serval.Server.Configuration;
using Serval.Server.Ingest;
using Serval.Server.Recordings;

namespace Serval.Server.Media;

/// <summary>A still from a camera's footage, and the instant it shows.</summary>
public sealed record RecordedFrame(byte[] Jpeg, DateTimeOffset At);

/// <summary>
/// One JPEG from a camera's footage at a past instant — what an assistant gets when it asks what a
/// camera saw at a given time.
///
/// <para><b>Recordings first, then the preview ring.</b> The recording is the full-quality stream
/// and reaches back as far as retention does. A camera that is not recording still keeps a ring of
/// its detect stream for alert clips, which covers the last minute or so — better than nothing for
/// "what just happened". The same two stores <c>AlertClipWorker</c> reads, both laid out under the
/// camera's own media directory.</para>
///
/// <para><b>The segment goes down ffmpeg's stdin, with its init in front</b> — the arrangement
/// <see cref="ClipExporter"/> uses — because an fMP4 media segment is unplayable on its own. That
/// makes the input unseekable, so the seek is an output seek: ffmpeg decodes from the segment's
/// start and keeps the first frame at or after the offset. A segment is a few seconds, so that is a
/// few seconds of decode at most.</para>
///
/// <para>Scaled to <c>Serval:Ingest:SnapshotMaxMegapixels</c>, the budget live snapshots are held
/// to, since the consumer here is a vision model that bills by pixel count too.</para>
/// </summary>
public sealed class RecordedFrameReader
{
    /// <summary>
    /// Decodes allowed at once. An assistant may ask for several frames in one turn, and each is a
    /// full-resolution decode of the main stream; on the small boxes Serval targets, two at a time
    /// keeps that from competing with ingest. The rest wait their turn rather than fail.
    /// </summary>
    private readonly SemaphoreSlim _decodes = new(2, 2);

    private readonly RecordingIndex _recordings;
    private readonly PreviewRingIndex _previewRing;
    private readonly ClipMedia _clipMedia;
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly ILogger<RecordedFrameReader> _logger;

    public RecordedFrameReader(
        RecordingIndex recordings,
        PreviewRingIndex previewRing,
        ClipMedia clipMedia,
        IOptionsMonitor<ServerOptions> options,
        ILogger<RecordedFrameReader> logger)
    {
        _recordings = recordings;
        _previewRing = previewRing;
        _clipMedia = clipMedia;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// One frame from a finished video file — a saved clip — under the same concurrency limit.
    /// </summary>
    public async Task<byte[]?> ReadFileAsync(string path, double offset, CancellationToken cancellationToken)
    {
        await _decodes.WaitAsync(cancellationToken);
        try
        {
            return await _clipMedia.ReadFrameAsync(path, offset, cancellationToken) is { Length: > 0 } jpeg ? jpeg : null;
        }
        finally
        {
            _decodes.Release();
        }
    }

    /// <summary>The frame at <paramref name="at"/>, or null when no footage covers it or it could not be decoded.</summary>
    public async Task<RecordedFrame?> ReadAsync(string cameraId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await _decodes.WaitAsync(cancellationToken);
        try
        {
            return await ReadUngatedAsync(cameraId, at, cancellationToken);
        }
        finally
        {
            _decodes.Release();
        }
    }

    private async Task<RecordedFrame?> ReadUngatedAsync(string cameraId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        // A one-second window rather than a point: the index's overlap rule excludes a segment that
        // merely touches an end, and a zero-width window touches everything.
        List<RecordingSegment> recorded = await _recordings.InRangeAsync(cameraId, at, at.AddSeconds(1), cancellationToken);
        RecordingSegment? segment = recorded.FirstOrDefault()
            ?? _previewRing.InRange(cameraId, at, at.AddSeconds(1)).FirstOrDefault();

        if (segment is null)
        {
            return null;
        }

        string cameraDir = Path.Combine(_options.CurrentValue.Media.Root, cameraId);
        string init = Path.Combine(cameraDir, segment.InitFileName);
        string media = Path.Combine(cameraDir, segment.FileName);

        if (!File.Exists(init) || !File.Exists(media))
        {
            return null;
        }

        // Clamped short of the end, where a seek can land past the last frame and yield nothing.
        double offset = Math.Clamp((at - segment.StartedAt).TotalSeconds, 0, Math.Max(segment.DurationSeconds - 0.1, 0));
        byte[]? jpeg = await DecodeAsync(init, media, offset, cancellationToken);

        return jpeg is { Length: > 0 } ? new RecordedFrame(jpeg, segment.StartedAt.AddSeconds(offset)) : null;
    }

    private async Task<byte[]?> DecodeAsync(string init, string media, double offset, CancellationToken cancellationToken)
    {
        IngestOptions ingest = _options.CurrentValue.Ingest;

        var startInfo = new ProcessStartInfo(ingest.FfmpegPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        List<string> args =
        [
            "-hide_banner", "-loglevel", "error",
            "-i", "pipe:0",
            "-ss", offset.ToString("0.###", CultureInfo.InvariantCulture),
            "-frames:v", "1",
        ];

        if (PixelBudget.ScaleFilter(ingest.SnapshotMaxMegapixels) is { } scale)
        {
            args.AddRange(["-vf", scale]);
        }

        args.AddRange(["-f", "mjpeg", "pipe:1"]);

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process? process = null;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start ffmpeg to read a recorded frame.");

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ChildProcess.HelperTimeout);

            using var buffer = new MemoryStream();
            Task copy = process.StandardOutput.BaseStream.CopyToAsync(buffer, deadline.Token);
            Task<string> errors = process.StandardError.ReadToEndAsync(deadline.Token);
            Task feed = FeedAsync(process.StandardInput.BaseStream, init, media, deadline.Token);

            await copy;
            await feed;
            string stderr = await errors;
            await process.WaitForExitAsync(deadline.Token);

            if (process.ExitCode != 0)
            {
                _logger.LogDebug("ffmpeg could not read a frame at {Offset}s of {Segment}: {Errors}", offset, media, stderr);
                return null;
            }

            return buffer.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Gave up reading a frame at {Offset}s of {Segment}.", offset, media);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read a frame at {Offset}s of {Segment}.", offset, media);
            return null;
        }
        finally
        {
            if (process is not null)
            {
                ChildProcess.Kill(process, _logger);
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Writes the init and then the segment, and closes the pipe. ffmpeg stops reading once it has
    /// its one frame, so a broken pipe partway through is the expected ending, not a failure.
    /// </summary>
    private static async Task FeedAsync(Stream stdin, string init, string media, CancellationToken cancellationToken)
    {
        try
        {
            foreach (string path in (string[])[init, media])
            {
                await using FileStream file = File.OpenRead(path);
                await file.CopyToAsync(stdin, cancellationToken);
            }
        }
        catch (IOException)
        {
        }
        finally
        {
            try
            {
                stdin.Close();
            }
            catch (IOException)
            {
            }
        }
    }
}
