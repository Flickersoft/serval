using System.Diagnostics;
using Microsoft.Extensions.Options;
using Serval.Server.Configuration;
using Serval.Server.Recordings;

namespace Serval.Server.Media;

/// <summary>
/// Turns a run of recorded segments into one standalone MP4 that plays anywhere.
///
/// The stored segments are fMP4: each is <c>moof</c>+<c>mdat</c> and is undecodable without the
/// <c>init</c> it was written with. Concatenating the init and its segments produces a valid
/// stream — the same thing <c>cat init-*.mp4 seg-*.m4s &gt; out.mp4</c> does by hand — which is
/// piped through ffmpeg to rewrite it as an MP4. Video and audio are already muxed together in
/// those segments, so this is a container rewrite with no re-encoding: <c>-c copy</c> throughout.
///
/// Two destinations, and they get different containers because a pipe cannot seek.
/// <see cref="WriteAsync"/> streams down a response and must fragment its output;
/// <see cref="WriteFileAsync"/> writes a saved clip and produces an ordinary faststart MP4 that
/// scrubs, resumes and opens anywhere.
///
/// Only segments sharing one init can go in a single file. A session restart changes the init and
/// makes the streams undecodable across the boundary, so the export stops there and returns the
/// first run rather than emitting a file that plays and then breaks partway through.
/// </summary>
public sealed class ClipExporter
{
    private readonly IngestOptions _options;
    private readonly string _mediaRoot;
    private readonly ILogger<ClipExporter> _logger;

    public ClipExporter(IOptions<ServerOptions> options, ILogger<ClipExporter> logger)
    {
        _options = options.Value.Ingest;
        _mediaRoot = options.Value.Media.Root;
        _logger = logger;
    }

    /// <summary>
    /// The leading segments that share one init — everything that can go in a single playable
    /// file. A session restart changes the init and makes the streams undecodable across the
    /// boundary, so the export stops there.
    ///
    /// Separate from <see cref="WriteAsync"/> because the caller needs it *before* the body is
    /// written: the response headers say how much of the requested window the clip actually
    /// covers, and headers cannot be set once bytes are flowing. Returns empty for an empty
    /// input rather than throwing, so the caller decides what a range with no footage means.
    /// </summary>
    internal static IReadOnlyList<RecordingSegment> LeadingRun(IReadOnlyList<RecordingSegment> segments)
    {
        if (segments.Count == 0)
        {
            return [];
        }

        string init = segments[0].InitFileName;
        return [.. segments.TakeWhile(s => s.InitFileName == init)];
    }

    public async Task WriteAsync(
        string cameraId,
        IReadOnlyList<RecordingSegment> segments,
        Stream destination,
        CancellationToken cancellationToken)
    {
        (Process process, Task feed, Task drainErrors) = Start(
            cameraId,
            segments,
            [
                "-c", "copy",

                // Fragmented output, because a plain MP4 needs to seek back to write its
                // index and a pipe cannot seek. The result is still one self-contained file
                // that any player opens; it simply keeps the fMP4 box layout.
                "-movflags", "frag_keyframe+empty_moov+default_base_moof",
                "-f", "mp4",
                "pipe:1",
            ],
            redirectStandardOutput: true,
            cancellationToken);

        using (process)
        {
            try
            {
                await process.StandardOutput.BaseStream.CopyToAsync(destination, cancellationToken);
                await feed;
                await process.WaitForExitAsync(cancellationToken);
            }
            finally
            {
                await CleanUpAsync(process, feed, drainErrors);
            }
        }
    }

    /// <summary>
    /// The same export, into a file rather than down a response.
    ///
    /// The difference in the arguments is the point of having both. A pipe cannot seek, so the
    /// streaming export has to fragment its output and can state no length; a file can, so this one
    /// writes an ordinary MP4 with a real <c>moov</c> at the front. That is what lets a saved clip
    /// be scrubbed in a browser, resumed as a download, and opened by things that will not touch
    /// fMP4 — none of which a kept clip can do without.
    /// </summary>
    public async Task WriteFileAsync(
        string cameraId,
        IReadOnlyList<RecordingSegment> segments,
        string path,
        CancellationToken cancellationToken)
    {
        (Process process, Task feed, Task drainErrors) = Start(
            cameraId,
            segments,
            [
                "-c", "copy",

                // Writes the index up front by writing the file, then rewriting it with the moov
                // moved to the start. Costs a second pass over the bytes and is worth it once:
                // without it a player must read to the end of a multi-gigabyte file before it can
                // show the first frame.
                "-movflags", "+faststart",
                "-f", "mp4",
                "-y", path,
            ],
            redirectStandardOutput: false,
            cancellationToken);

        using (process)
        {
            try
            {
                await feed;
                await process.WaitForExitAsync(cancellationToken);

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"ffmpeg exited {process.ExitCode} writing a clip for camera {cameraId}.");
                }
            }
            finally
            {
                await CleanUpAsync(process, feed, drainErrors);
            }
        }
    }

    /// <summary>
    /// Starts ffmpeg reading the concatenated init and segments on stdin, with
    /// <paramref name="outputArguments"/> deciding where the result goes.
    /// </summary>
    private (Process Process, Task Feed, Task DrainErrors) Start(
        string cameraId,
        IReadOnlyList<RecordingSegment> segments,
        IReadOnlyList<string> outputArguments,
        bool redirectStandardOutput,
        CancellationToken cancellationToken)
    {
        string cameraDir = Path.Combine(_mediaRoot, cameraId);

        IReadOnlyList<RecordingSegment> run = LeadingRun(segments);
        if (run.Count == 0)
        {
            throw new InvalidOperationException("Cannot export a clip from no segments.");
        }

        string init = run[0].InitFileName;

        if (run.Count < segments.Count)
        {
            _logger.LogInformation(
                "Clip for camera {CameraId} stops at a session boundary: {Taken} of {Total} segments.",
                cameraId, run.Count, segments.Count);
        }

        var startInfo = new ProcessStartInfo(_options.FfmpegPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = redirectStandardOutput,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "warning", "-i", "pipe:0" })
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (string arg in outputArguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start ffmpeg to export a clip.");

        return (
            process,
            FeedAsync(process, cameraDir, init, run, cancellationToken),
            DrainStderrAsync(process, cameraId, cancellationToken));
    }

    private static async Task CleanUpAsync(Process process, Task feed, Task drainErrors)
    {
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }

        // Both are already awaited on the happy path; awaiting again is free. This is for the path
        // where the destination went away: the feed is writing into a pipe whose process we just
        // killed, and its IOException has to be observed by someone.
        try { await feed; } catch { /* the export was already aborted */ }
        try { await drainErrors; } catch { /* not interesting */ }
    }

    /// <summary>Writes init + segments into ffmpeg's stdin, in order.</summary>
    private async Task FeedAsync(
        Process process,
        string cameraDir,
        string init,
        IReadOnlyList<RecordingSegment> segments,
        CancellationToken cancellationToken)
    {
        try
        {
            await CopyFileAsync(Path.Combine(cameraDir, init), process.StandardInput.BaseStream, cancellationToken);

            foreach (RecordingSegment segment in segments)
            {
                await CopyFileAsync(
                    Path.Combine(cameraDir, segment.FileName),
                    process.StandardInput.BaseStream,
                    cancellationToken);
            }
        }
        finally
        {
            // ffmpeg needs to see the end of input before it will finish the output.
            process.StandardInput.BaseStream.Close();
        }
    }

    private static async Task CopyFileAsync(string path, Stream destination, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            // Retention may have pruned a segment between the index query and this read. Skipping
            // is better than failing the whole export: the clip is short by a few seconds rather
            // than absent.
            return;
        }

        await using FileStream source = File.OpenRead(path);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private async Task DrainStderrAsync(Process process, string cameraId, CancellationToken cancellationToken)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                _logger.LogWarning("[ffmpeg-clip {CameraId}] {Line}", cameraId, line);
            }
        }
        catch (OperationCanceledException) { }
    }
}
