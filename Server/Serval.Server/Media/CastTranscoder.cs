using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
using Serval.Server.Configuration;
using Serval.Server.Recordings;

namespace Serval.Server.Media;

/// <summary>
/// Turns one recorded segment into something a television can decode, on demand.
///
/// <para><b>Why this exists.</b> A Cast device decodes H.264 to Level 4.2 — 1080p — and records
/// here are the camera's main stream copied untouched: 4K HEVC on one, 2560x1440 on another,
/// 1920x2560 on a doorbell. Measured on the real hardware, a Cast device fetches such a playlist
/// and every segment in it, all 200, and renders nothing at all. Casting a recording therefore
/// means re-encoding it, and the only question was where.</para>
///
/// <para><b>Per request, not per session.</b> Each request transcodes one batch of recorded
/// segments, so the playlist stays an ordinary VOD playlist with real durations: seeking and
/// scrubbing work without any of this knowing they happened, there is no ffmpeg lifecycle tied to a
/// viewer who may have walked away, and nothing is encoded that nobody watches.</para>
///
/// <para><b>A batch rather than a segment, because a run costs more than the work.</b> Launching
/// ffmpeg and initialising VAAPI is most of what a four-second segment took. Batching
/// <see cref="HlsPlaylist.CastBatchSegments"/> of them pays that once instead of once each, and
/// everything inside a batch is a single continuous encode rather than several independent ones —
/// fewer joins, and fewer places for a decoder to object.</para>
///
/// <para><b>MPEG-TS out, not fMP4.</b> A TS segment carries its own parameter sets, so independent
/// ffmpeg runs need not agree on an initialisation segment — which they cannot be relied on to do.
/// It is also Cast's native HLS format, and fMP4 is the one this deployment has twice watched a
/// television fetch in full and not draw.</para>
/// </summary>
public sealed class CastTranscoder
{
    /// <summary>
    /// The height to encode to when the receiver has not said what its screen will take. 1080p is
    /// the floor every Cast device decodes, and the safe answer when nothing better is known.
    /// </summary>
    public const int DefaultHeight = 1080;

    /// <summary>
    /// The ceiling, whatever a receiver claims. A device reporting something absurd would otherwise
    /// have this encoding at that size, which is expensive and pointless in equal measure.
    /// </summary>
    private const int MaxHeight = 2160;

    private const int MinHeight = 360;

    /// <summary>
    /// How many segments may be transcoded at once.
    ///
    /// <para>A bound rather than a queue depth: seeking makes a player abandon what it asked for
    /// and ask for somewhere else, and without a ceiling a few scrubs would leave a handful of
    /// ffmpeg processes competing for one GPU and finishing none of them in time. Two, because this
    /// server's first job is recording and the GPU is shared with it.</para>
    /// </summary>
    private static readonly SemaphoreSlim Slots = new(2, 2);

    private readonly IngestOptions _options;
    private readonly string _mediaRoot;
    private readonly ILogger<CastTranscoder> _logger;

    public CastTranscoder(IOptions<ServerOptions> options, ILogger<CastTranscoder> logger)
    {
        _options = options.Value.Ingest;
        _mediaRoot = options.Value.Media.Root;
        _logger = logger;
    }

    /// <summary>
    /// The init a segment cannot be decoded without.
    ///
    /// <para>Derived from the name rather than looked up, because the naming is the mechanism: a
    /// session stamps every file it writes with its own start time, which is what ties
    /// <c>seg-&lt;stamp&gt;-NNNNN.m4s</c> to <c>init-&lt;stamp&gt;.mp4</c>. See
    /// <c>FfmpegStreamSession</c>. Null for anything not shaped like a segment, which the caller
    /// treats as not found.</para>
    /// </summary>
    internal static string? InitFor(string segmentName)
    {
        if (!segmentName.StartsWith("seg-", StringComparison.Ordinal))
        {
            return null;
        }

        int lastDash = segmentName.LastIndexOf('-');
        return lastDash <= 4 ? null : $"init-{segmentName[4..lastDash]}.mp4";
    }

    /// <summary>
    /// Writes one batch to <paramref name="destination"/> as MPEG-TS, re-encoded to fit.
    ///
    /// <paramref name="count"/> is how many consecutive recorded segments the batch covers, as the
    /// playlist worked out — they share an init and are fed to one ffmpeg in order.
    /// <paramref name="offsetSeconds"/> is where the batch sits in that playlist, which is what
    /// pins its timestamps so that a seek means the same thing to both ends.
    /// <paramref name="durationSeconds"/> is how long the playlist said that slot is, which the
    /// encode is trimmed to so that it cannot run into the next one.
    /// </summary>
    public async Task WriteSegmentAsync(
        string cameraId,
        string segmentName,
        int count,
        double offsetSeconds,
        double? durationSeconds,
        int? maxHeight,
        Stream destination,
        CancellationToken cancellationToken)
    {
        string? init = InitFor(segmentName);
        if (init is null)
        {
            throw new InvalidOperationException($"'{segmentName}' is not a recorded segment name.");
        }

        string cameraDir = Path.Combine(_mediaRoot, cameraId);
        string initPath = Path.Combine(cameraDir, init);

        IReadOnlyList<string> paths = BatchPaths(cameraDir, segmentName, count);

        if (paths.Count == 0 || !File.Exists(initPath))
        {
            throw new FileNotFoundException($"No recorded segment {segmentName} for camera {cameraId}.");
        }

        await Slots.WaitAsync(cancellationToken);

        try
        {
            await RunAsync(
                cameraId, initPath, paths, offsetSeconds, durationSeconds, Clamp(maxHeight),
                destination, cancellationToken);
        }
        finally
        {
            Slots.Release();
        }
    }

    /// <summary>
    /// The files in a batch, in order, stopping at the first one that is not there.
    ///
    /// <para>Stopping rather than failing: retention runs while somebody is watching, and a batch
    /// whose tail has been deleted is still playable up to the gap. The alternative is a segment
    /// that 404s in the middle of a recording somebody is part-way through.</para>
    /// </summary>
    internal static IReadOnlyList<string> BatchPaths(string cameraDir, string firstSegment, int count)
    {
        int lastDash = firstSegment.LastIndexOf('-');
        if (lastDash < 0 || !int.TryParse(firstSegment[(lastDash + 1)..], out int first))
        {
            return [];
        }

        string prefix = firstSegment[..(lastDash + 1)];
        int width = firstSegment.Length - lastDash - 1;

        var paths = new List<string>();

        for (int i = 0; i < Math.Max(1, count); i++)
        {
            string name = prefix + (first + i).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
            string path = Path.Combine(cameraDir, $"{name}.m4s");

            if (!File.Exists(path))
            {
                break;
            }

            paths.Add(path);
        }

        return paths;
    }

    /// <summary>
    /// The height to actually encode to: what the receiver asked for, inside what is sensible.
    /// Absent means the receiver could not say, which is the 1080p floor rather than a failure.
    /// </summary>
    internal static int Clamp(int? maxHeight) =>
        maxHeight is not int h ? DefaultHeight : Math.Clamp(h, MinHeight, MaxHeight);

    /// <summary>
    /// Bitrate for a height, in the shape a Cast device is happy with.
    ///
    /// <para>Scaled with the pixels rather than fixed, because the ceiling is now the receiver's to
    /// choose: 4M is generous at 1080p and visibly poor at 2160p, and encoding a 4K screen's worth
    /// of detail at a 1080p bitrate throws away most of what asking the device bought.</para>
    /// </summary>
    internal static string Bitrate(int height) => height switch
    {
        >= 2160 => "16M",
        >= 1440 => "8M",
        >= 1080 => "5M",
        >= 720 => "3M",
        _ => "1500k",
    };

    private async Task RunAsync(
        string cameraId,
        string initPath,
        IReadOnlyList<string> segmentPaths,
        double offsetSeconds,
        double? durationSeconds,
        int height,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_options.FfmpegPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string arg in Arguments(offsetSeconds, durationSeconds, height))
        {
            startInfo.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start ffmpeg to transcode a segment.");

        Task feed = FeedAsync(process, initPath, segmentPaths, cancellationToken);
        Task drain = DrainStderrAsync(process, cameraId, cancellationToken);

        try
        {
            long written = await CopyCountingAsync(
                process.StandardOutput.BaseStream, destination, cancellationToken);
            await feed;
            await process.WaitForExitAsync(cancellationToken);

            // The response has already been sent with a 200 in front of it — it is streamed, so
            // there is no moment at which a failed run could still be answered with an error. What
            // the player receives instead is a short or empty segment, which it reports as a decode
            // failure and stops on. That failure is only diagnosable from this side, and only if
            // somebody wrote it down.
            if (process.ExitCode != 0 || written == 0)
            {
                _logger.LogWarning(
                    "ffmpeg produced {Bytes} bytes and exited {ExitCode} transcoding {Segment} "
                    + "(+{Count}) for camera {CameraId}; the player has already been sent them.",
                    written, process.ExitCode, Path.GetFileName(segmentPaths[0]),
                    segmentPaths.Count - 1, cameraId);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }

            // The viewer seeking away closes the response mid-copy, which leaves the feed writing
            // into a pipe whose process has just been killed. Somebody has to observe that.
            try { await feed; } catch { /* the request was abandoned */ }
            try { await drain; } catch { /* not interesting */ }
        }
    }

    /// <summary>
    /// The whole command line, as one list so the hardware and software paths are read side by
    /// side rather than assembled from fragments.
    ///
    /// <para>With a render node, decode, scale and encode all stay on the GPU — the frames never
    /// come back to the CPU, which is what makes 4K HEVC affordable on an N100 that is also running
    /// detection. Without one it is the same shape in software, which will not keep up with 4K but
    /// is the honest fallback for a deployment that has no GPU to give.</para>
    /// </summary>
    internal IReadOnlyList<string> Arguments(double offsetSeconds, double? durationSeconds, int height)
    {
        string offset = offsetSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        string device = _options.HwAccelDevice ?? "";

        // Width is the 16:9 partner of the height, and only a bound: force_original_aspect_ratio
        // fits the source inside the box rather than filling it, which is what keeps a portrait
        // doorbell portrait — 1920x2560 becomes 810x1080 instead of a squeezed landscape.
        int width = height * 16 / 9;
        string scale = $"w={width}:h={height}:force_original_aspect_ratio=decrease";

        List<string> args = ["-nostdin", "-hide_banner", "-loglevel", "warning"];

        if (!string.IsNullOrWhiteSpace(device))
        {
            args.AddRange([
                "-hwaccel", "vaapi",
                "-hwaccel_device", device.Trim(),
                "-hwaccel_output_format", "vaapi",
                "-i", "pipe:0",
                "-vf", $"scale_vaapi={scale}",
                "-c:v", "h264_vaapi",
            ]);
        }
        else
        {
            args.AddRange([
                "-i", "pipe:0",

                // force_divisible_by, because an odd dimension is not encodable as yuv420p and a
                // portrait source scaled to fit will land on one.
                "-vf", $"scale={scale}:force_divisible_by=2",
                "-c:v", "libx264",
                "-preset", "veryfast",
            ]);
        }

        args.AddRange([
            "-b:v", Bitrate(height),

            // Audio is normalised, not passed through at the camera's own shape. These cameras
            // record AAC at 16 kHz mono, and asking the encoder for a television's bitrate at that
            // rate overruns what a frame can hold — ffmpeg says so on every segment and clamps.
            // Resampling to 48 kHz stereo first is what makes the bitrate valid, and 16 kHz mono is
            // an odd thing to hand a Cast device besides: audio is what has broken every playback
            // path in this integration so far, and none of them said so.
            "-af", "aresample=async=1",
            "-ar", "48000",
            "-ac", "2",
            "-c:a", "aac",
            "-b:a", "128k",

            // Where this batch sits in its playlist. ffmpeg normalises timestamps per invocation,
            // so without it every batch claims to begin at the same instant; with it, playlist time
            // and media time are the same and a seek lands where it was aimed.
            //
            // The alternative — keeping the recording's own timestamps — reads better and does not
            // work: the recorder restarts every few minutes, and each restart begins afresh, so any
            // window of length spans several and its timeline jumps backwards partway through.
            "-output_ts_offset", offset,

            // The muxer's own head start, removed. Left at its default the stream begins 1.4
            // seconds after the offset it was given, which is 1.4 seconds this batch is not where
            // the playlist says it is.
            "-muxdelay", "0",
            "-muxpreload", "0",
        ]);

        // Trimmed to the slot the playlist declared for it.
        //
        // **This is what a Cast device was stopping on.** Each batch is an independent encode
        // positioned absolutely, and its natural length is a frame or two more than the wall-clock
        // spacing the playlist measured — so the next batch's first packet carried a timestamp
        // *earlier* than this one's last, at every single join. Measured on the real recordings: 60
        // ms of video and 120 ms of audio, enough for the decoder to call the stream corrupt and
        // give up around thirty seconds in. Trimming turns that overlap into a gap of a few
        // milliseconds, which players simply skip.
        if (durationSeconds is double seconds and > 0)
        {
            args.AddRange(["-t", seconds.ToString("0.###", CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-f", "mpegts", "pipe:1"]);

        return args;
    }

    /// <summary>Writes the init and then the batch into ffmpeg's stdin — the bytes a decoder needs,
    /// in the order it needs them. One init, because a batch never spans a session.</summary>
    private static async Task FeedAsync(
        Process process,
        string initPath,
        IReadOnlyList<string> segmentPaths,
        CancellationToken cancellationToken)
    {
        try
        {
            await CopyAsync(initPath, process.StandardInput.BaseStream, cancellationToken);

            foreach (string path in segmentPaths)
            {
                await CopyAsync(path, process.StandardInput.BaseStream, cancellationToken);
            }
        }
        finally
        {
            // ffmpeg will not finish its output until it has seen the end of its input.
            try { process.StandardInput.BaseStream.Close(); } catch { /* already gone */ }
        }
    }

    /// <summary>
    /// <see cref="Stream.CopyToAsync(Stream, CancellationToken)"/>, but says how much it copied.
    /// The size of what ffmpeg produced is the difference between a run that worked and one that
    /// fell over part-way, and nothing else on this path can tell them apart.
    /// </summary>
    private static async Task<long> CopyCountingAsync(
        Stream source, Stream destination, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];
        long total = 0;

        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return total;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
        }
    }

    private static async Task CopyAsync(string path, Stream destination, CancellationToken cancellationToken)
    {
        await using FileStream source = File.OpenRead(path);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private async Task DrainStderrAsync(Process process, string cameraId, CancellationToken cancellationToken)
    {
        string errors = await process.StandardError.ReadToEndAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(errors))
        {
            _logger.LogWarning(
                "ffmpeg transcoding a cast segment for camera {CameraId}: {Errors}",
                cameraId, errors.Trim());
        }
    }
}
