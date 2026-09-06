using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using Serval.Server.Configuration;
using Serval.Server.Recordings;

namespace Serval.Server.Media;

/// <summary>
/// Turns recorded segments into one standalone MP4 that plays anywhere.
///
/// The stored segments are fMP4: each is <c>moof</c>+<c>mdat</c> and is undecodable without the
/// <c>init</c> it was written with. Concatenating the init and its segments produces a valid
/// stream — the same thing <c>cat init-*.mp4 seg-*.m4s &gt; out.mp4</c> does by hand — which is
/// piped through ffmpeg to rewrite it as an MP4. Video and audio are already muxed together in
/// those segments, so this is a container rewrite with no re-encoding: <c>-c copy</c> throughout.
///
/// A recorder restart writes a new init, and everything after it belongs to that one instead. So a
/// long range is not one pile of segments but several batches, and joining them is what lets an
/// export outlive a reconnect. One batch is fed to ffmpeg on stdin exactly as it always was. Several
/// are handed over as a concat playlist, one named pipe per batch, each entry carrying the batch's
/// exact recorded length so ffmpeg lays them end to end instead of restarting the clock at every
/// join — see <see cref="StartConcat"/>, where that decision is spelled out in full.
///
/// Two destinations, and they get different containers because a pipe cannot seek.
/// <see cref="WriteAsync"/> streams down a response and must fragment its output;
/// <see cref="WriteFileAsync"/> writes a saved clip and produces an ordinary faststart MP4 that
/// scrubs, resumes and opens anywhere.
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
        SweepAbandonedPipes();
    }

    /// <summary>
    /// Clears out the pipes of exports that were running when the Server last stopped.
    ///
    /// A named pipe only means anything while both ends are open, so every one of these belongs to
    /// a process that no longer exists — there is nothing here to resume and nothing to lose. The
    /// per-export cleanup handles the ordinary case; this is for the kill that never reached it,
    /// and it runs once at startup because that is the only moment nothing can be in flight.
    /// </summary>
    private void SweepAbandonedPipes()
    {
        try
        {
            if (Directory.Exists(_options.ExportPipeDir))
            {
                Directory.Delete(_options.ExportPipeDir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // Never fatal: this runs while the container is coming up, and an export's leftovers
            // are not worth refusing to start over.
            _logger.LogWarning(ex, "Could not clear abandoned export pipes from {Directory}.",
                _options.ExportPipeDir);
        }
    }

    /// <summary>
    /// The segments grouped into batches, each sharing one init and so playable with it alone.
    ///
    /// Consecutive grouping rather than a lookup by name: the segments arrive in time order and a
    /// batch is a stretch of that order, so a restart is a change between neighbours. Grouping by
    /// key would silently reorder footage if an init name ever repeated.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<RecordingSegment>> Runs(
        IReadOnlyList<RecordingSegment> segments)
    {
        var batches = new List<IReadOnlyList<RecordingSegment>>();
        var current = new List<RecordingSegment>();

        foreach (RecordingSegment segment in segments)
        {
            if (current.Count > 0 && current[0].InitFileName != segment.InitFileName)
            {
                batches.Add(current);
                current = [];
            }

            current.Add(segment);
        }

        if (current.Count > 0)
        {
            batches.Add(current);
        }

        return batches;
    }

    /// <summary>
    /// The first batch on its own.
    ///
    /// Kept for the callers that only ever want one and would gain nothing from joining: an alert's
    /// preview clip is twenty seconds cut from the ring, so a restart inside it means the moment was
    /// missed, not that a longer file should be assembled.
    /// </summary>
    internal static IReadOnlyList<RecordingSegment> LeadingRun(IReadOnlyList<RecordingSegment> segments) =>
        Runs(segments) is [var first, ..] ? first : [];

    /// <summary>
    /// Works out what an export of these segments will contain, before anything is written.
    ///
    /// The batches are checked against each other here rather than discovered to be incompatible
    /// halfway through writing, because by then the response is a 200 with bytes already in it. A
    /// camera that changed resolution or codec across a restart cannot have both sides in one file,
    /// so the export stops at that point and says so — short, and honest about being short, beats a
    /// file that plays and then falls apart.
    /// </summary>
    public async Task<ExportPlan> PlanAsync(
        string cameraId,
        IReadOnlyList<RecordingSegment> segments,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyList<RecordingSegment>> batches = Runs(segments);
        if (batches.Count == 0)
        {
            return new ExportPlan([], Truncated: false, default, default, 0);
        }

        string cameraDir = Path.Combine(_mediaRoot, cameraId);
        var kept = new List<IReadOnlyList<RecordingSegment>>();
        string? profile = null;

        foreach (IReadOnlyList<RecordingSegment> batch in batches)
        {
            // The init alone describes the tracks and contains no media, so this reads a kilobyte
            // rather than the hours of footage behind it.
            string? current = await ProfileAsync(
                Path.Combine(cameraDir, batch[0].InitFileName), cancellationToken);

            // A profile that could not be read is not treated as a mismatch. ffprobe failing on the
            // first batch would otherwise refuse an export that would have worked, and the first
            // batch is the one that is always kept.
            if (profile is not null && current is not null && current != profile)
            {
                _logger.LogInformation(
                    "Export for camera {CameraId} stops at {Init}: the stream changed across a "
                    + "recording restart ({Before} then {After}).",
                    cameraId, batch[0].InitFileName, profile, current);
                break;
            }

            profile ??= current;
            kept.Add(batch);
        }

        RecordingSegment last = kept[^1][^1];

        return new ExportPlan(
            kept,
            Truncated: kept.Count < batches.Count,
            From: kept[0][0].StartedAt,
            To: last.StartedAt.AddSeconds(last.DurationSeconds),
            DurationSeconds: kept.Sum(b => b.Sum(s => s.DurationSeconds)));
    }

    /// <summary>Streams the export down a response.</summary>
    public async Task WriteAsync(
        string cameraId,
        ExportPlan plan,
        Stream destination,
        CancellationToken cancellationToken)
    {
        Export export = Start(
            cameraId,
            plan,
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

        using (export.Process)
        {
            try
            {
                await export.Process.StandardOutput.BaseStream.CopyToAsync(destination, cancellationToken);
                await export.Feed;
                await export.Process.WaitForExitAsync(cancellationToken);
            }
            finally
            {
                await CleanUpAsync(export);
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
        ExportPlan plan,
        string path,
        CancellationToken cancellationToken)
    {
        Export export = Start(
            cameraId,
            plan,
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

        using (export.Process)
        {
            try
            {
                await export.Feed;
                await export.Process.WaitForExitAsync(cancellationToken);

                if (export.Process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"ffmpeg exited {export.Process.ExitCode} writing a clip for camera {cameraId}.");
                }
            }
            finally
            {
                await CleanUpAsync(export);
            }
        }
    }

    /// <summary>
    /// Exports a single batch, for callers that already hold one and have no use for joining.
    /// </summary>
    public Task WriteFileAsync(
        string cameraId,
        IReadOnlyList<RecordingSegment> segments,
        string path,
        CancellationToken cancellationToken)
    {
        if (segments.Count == 0)
        {
            throw new InvalidOperationException("Cannot export a clip from no segments.");
        }

        RecordingSegment last = segments[^1];

        return WriteFileAsync(
            cameraId,
            new ExportPlan(
                [segments],
                Truncated: false,
                segments[0].StartedAt,
                last.StartedAt.AddSeconds(last.DurationSeconds),
                segments.Sum(s => s.DurationSeconds)),
            path,
            cancellationToken);
    }

    /// <summary>Everything one running export owns, so cleanup has one thing to take apart.</summary>
    private sealed record Export(Process Process, Task Feed, Task DrainErrors, string? PipeDirectory);

    private Export Start(
        string cameraId,
        ExportPlan plan,
        IReadOnlyList<string> outputArguments,
        bool redirectStandardOutput,
        CancellationToken cancellationToken)
    {
        if (plan.IsEmpty)
        {
            throw new InvalidOperationException("Cannot export a clip from no segments.");
        }

        return plan.Batches.Count == 1
            ? StartSingle(cameraId, plan.Batches[0], outputArguments, redirectStandardOutput, cancellationToken)
            : StartConcat(cameraId, plan, outputArguments, redirectStandardOutput, cancellationToken);
    }

    /// <summary>
    /// One batch: the init and its segments straight down ffmpeg's stdin.
    ///
    /// This is the overwhelming majority of exports and it stays as simple as it has always been —
    /// no playlist, no pipes on disk, nothing to clean up.
    /// </summary>
    private Export StartSingle(
        string cameraId,
        IReadOnlyList<RecordingSegment> batch,
        IReadOnlyList<string> outputArguments,
        bool redirectStandardOutput,
        CancellationToken cancellationToken)
    {
        string cameraDir = Path.Combine(_mediaRoot, cameraId);

        Process process = StartFfmpeg(
            ["-i", "pipe:0", .. outputArguments],
            redirectStandardInput: true,
            redirectStandardOutput);

        Task feed = FeedStdinAsync(process, cameraDir, batch, cancellationToken);

        return new Export(process, feed, DrainStderrAsync(process, cameraId, cancellationToken), null);
    }

    /// <summary>
    /// Several batches joined into one file, through ffmpeg's concat demuxer.
    ///
    /// A batch is not a file — it is an init plus dozens of segments — and the demuxer opens each
    /// entry by name. So each batch gets a name to open: a named pipe, which is a filesystem entry
    /// holding nothing at all. The segments are poured in one end while ffmpeg reads the other, and
    /// no joined copy is ever assembled anywhere.
    ///
    /// Every entry carries its batch's <c>duration</c>, and that is not decoration. The demuxer
    /// offsets each input by the length of the ones before it, and it cannot measure a pipe: with
    /// the durations left out, the second batch restarts the clock, ffmpeg forces the timestamps
    /// forward a tick at a time and the result is a file that reports success and plays wrongly.
    /// The lengths come from the segment index, which knows each one exactly, so they are stated
    /// rather than measured — and stated in full precision, since a rounded batch drags every
    /// later one out of step.
    ///
    /// Lining batches up by recorded length also closes the gaps. The minutes a camera spent
    /// reconnecting are in no segment, so they are in no batch, and the export runs straight from
    /// one side of the outage to the other rather than holding on a frozen frame.
    ///
    /// ffmpeg opens every pipe at the start but reads them one at a time, so all but the first fill
    /// their kernel buffer and their writers wait there. That is the backpressure that keeps this
    /// flat: a few dozen kilobytes per batch, none of it on the heap, however long the export runs.
    /// </summary>
    private Export StartConcat(
        string cameraId,
        ExportPlan plan,
        IReadOnlyList<string> outputArguments,
        bool redirectStandardOutput,
        CancellationToken cancellationToken)
    {
        string cameraDir = Path.Combine(_mediaRoot, cameraId);
        string pipeDirectory = Path.Combine(_options.ExportPipeDir, Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(pipeDirectory);

        var playlist = new List<string>();
        var pipes = new List<string>();

        for (int i = 0; i < plan.Batches.Count; i++)
        {
            string pipe = Path.Combine(pipeDirectory, $"batch-{i:D4}");
            CreateNamedPipe(pipe);
            pipes.Add(pipe);

            double seconds = plan.Batches[i].Sum(s => s.DurationSeconds);

            // Single quotes are the demuxer's own escaping, and these paths are ours — a directory
            // we just made from a GUID under a configured root, so there is nothing in them to
            // escape.
            playlist.Add($"file '{pipe}'");
            playlist.Add($"duration {seconds.ToString("0.######", CultureInfo.InvariantCulture)}");
        }

        string listPath = Path.Combine(pipeDirectory, "batches.txt");
        File.WriteAllLines(listPath, playlist);

        _logger.LogInformation(
            "Export for camera {CameraId} joins {Batches} recording sessions covering {Seconds:0.#}s.",
            cameraId, plan.Batches.Count, plan.DurationSeconds);

        Process process = StartFfmpeg(
            [
                "-f", "concat",

                // The playlist points at absolute paths, which the demuxer refuses without this,
                // and at named pipes, which are not in its default protocol list.
                "-safe", "0",
                "-protocol_whitelist", "file,pipe,fd",
                "-i", listPath,
                .. outputArguments,
            ],
            redirectStandardInput: false,
            redirectStandardOutput);

        // Every writer starts now, because ffmpeg opens every pipe before it reads any of them and
        // a batch with nobody on the far end would hold the whole export. Opening a pipe for
        // writing blocks until the reader arrives, which is why these are not run inline.
        Task feed = Task.WhenAll(pipes.Select((pipe, i) =>
            Task.Run(() => FeedPipeAsync(pipe, cameraDir, plan.Batches[i], cancellationToken), cancellationToken)));

        return new Export(
            process, feed, DrainStderrAsync(process, cameraId, cancellationToken), pipeDirectory);
    }

    private Process StartFfmpeg(
        IReadOnlyList<string> arguments,
        bool redirectStandardInput,
        bool redirectStandardOutput)
    {
        var startInfo = new ProcessStartInfo(_options.FfmpegPath)
        {
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = redirectStandardOutput,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "warning" })
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (string arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start ffmpeg to export a clip.");
    }

    private async Task CleanUpAsync(Export export)
    {
        ChildProcess.Kill(export.Process, _logger);

        // Both are already awaited on the happy path; awaiting again is free. This is for the path
        // where the destination went away: the feed is writing into a pipe whose process we just
        // killed, and its IOException has to be observed by someone.
        try { await export.Feed; } catch { /* the export was already aborted */ }
        try { await export.DrainErrors; } catch { /* not interesting */ }

        if (export.PipeDirectory is not null)
        {
            try
            {
                Directory.Delete(export.PipeDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The sweep will get it. Failing an export that has already been written because
                // its scratch directory would not go away helps nobody.
                _logger.LogWarning(ex, "Could not remove the export pipes at {Directory}.", export.PipeDirectory);
            }
        }
    }

    /// <summary>Writes init + segments into ffmpeg's stdin, in order.</summary>
    private async Task FeedStdinAsync(
        Process process,
        string cameraDir,
        IReadOnlyList<RecordingSegment> batch,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteBatchAsync(process.StandardInput.BaseStream, cameraDir, batch, cancellationToken);
        }
        finally
        {
            // ffmpeg needs to see the end of input before it will finish the output.
            process.StandardInput.BaseStream.Close();
        }
    }

    /// <summary>Writes one batch into its named pipe, then closes it so ffmpeg moves to the next.</summary>
    private async Task FeedPipeAsync(
        string pipePath,
        string cameraDir,
        IReadOnlyList<RecordingSegment> batch,
        CancellationToken cancellationToken)
    {
        // Blocks until ffmpeg opens the read end, which for every batch after the first is only
        // once it has finished the one before.
        await using var pipe = new FileStream(pipePath, FileMode.Open, FileAccess.Write);
        await WriteBatchAsync(pipe, cameraDir, batch, cancellationToken);
    }

    private static async Task WriteBatchAsync(
        Stream destination,
        string cameraDir,
        IReadOnlyList<RecordingSegment> batch,
        CancellationToken cancellationToken)
    {
        await CopyFileAsync(Path.Combine(cameraDir, batch[0].InitFileName), destination, cancellationToken);

        foreach (RecordingSegment segment in batch)
        {
            await CopyFileAsync(Path.Combine(cameraDir, segment.FileName), destination, cancellationToken);
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

    /// <summary>
    /// What the tracks in an init are, as one comparable string, or null if ffprobe would not say.
    /// </summary>
    private async Task<string?> ProfileAsync(string initPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(initPath))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo(_options.FfprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string arg in new[]
                 {
                     "-v", "error",
                     "-show_entries", "stream=codec_name,width,height,sample_rate,channels",
                     "-of", "csv=p=0",
                     initPath,
                 })
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process? probe = null;
        try
        {
            probe = Process.Start(startInfo);
            if (probe is null)
            {
                return null;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(ChildProcess.HelperTimeout);

            // Both pipes are read before waiting: a process that fills one while nobody drains it
            // blocks forever.
            Task<string> output = probe.StandardOutput.ReadToEndAsync(deadline.Token);
            Task<string> errors = probe.StandardError.ReadToEndAsync(deadline.Token);
            await probe.WaitForExitAsync(deadline.Token);

            string streams = (await output).Trim();
            await errors;

            return probe.ExitCode == 0 && streams.Length > 0
                ? string.Join(" | ", streams.Split('\n').Select(l => l.Trim()))
                : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Gave up reading the stream layout of {Init}.", initPath);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the stream layout of {Init}.", initPath);
            return null;
        }
        finally
        {
            if (probe is not null)
            {
                ChildProcess.Kill(probe, _logger);
                probe.Dispose();
            }
        }
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

    /// <summary>
    /// Creates a named pipe. There is no call for this in .NET — <c>NamedPipeServerStream</c> is a
    /// Windows concept that maps to a unix socket here, which ffmpeg cannot open as a file — so it
    /// goes to libc directly.
    /// </summary>
    private static void CreateNamedPipe(string path)
    {
        // 0600: nothing outside this process has any business reading an export in flight.
        if (Mkfifo(path, 0b110_000_000) != 0)
        {
            throw new IOException(
                $"Could not create the export pipe at {path}.",
                Marshal.GetLastPInvokeError());
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int Mkfifo(string path, uint mode);
}
