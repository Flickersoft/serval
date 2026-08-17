using System.Diagnostics;
using Serval.Ai;
using Serval.Server.Cameras;
using Serval.Server.Configuration;
using Serval.Server.Ingest;

namespace Serval.Server.Ai;

/// <summary>
/// Pulls one camera's audio track and decodes it to the 16 kHz mono PCM the detection path wants,
/// writing into an <see cref="AudioRingBuffer"/> exactly as the edge module's microphone callback
/// does. Everything downstream of the ring buffer is then the same code on both sides.
///
/// This is a second, audio-only session rather than a tap on the recording ffmpeg, and that
/// separation is deliberate:
///
/// <list type="bullet">
/// <item>Recording is the Server's primary job. A reader that stalls on a shared process's stdout
/// applies backpressure to the muxer and takes recording down with it.</item>
/// <item>Turning AI on or off for a camera would otherwise restart the recording ffmpeg, putting
/// a visible gap in the recording for a change that has nothing to do with recording.</item>
/// </list>
///
/// Over RTSP the extra cost is small because of <c>-allowed_media_types audio</c>: ffmpeg only
/// RTSP-SETUPs the audio stream, so no video is pulled over the network or decoded. What remains
/// is one process and an AAC-to-PCM decode, well under a percent of a core. Other protocols have
/// no equivalent — an HTTP-FLV or SRT source carries its video whether we want it or not — so the
/// saving there is only the decode, not the bandwidth.
/// </summary>
public sealed class FfmpegAudioSession
{
    /// <summary>
    /// Bytes read from ffmpeg per pass. 32 KiB is 16k int16 samples — a second of audio — so the
    /// loop wakes rarely while staying far inside the ring buffer's depth.
    /// </summary>
    private const int ReadBufferBytes = 32 * 1024;

    private readonly Camera _camera;
    private readonly CameraStream _stream;
    private readonly AudioRingBuffer _ringBuffer;
    private readonly IngestOptions _options;
    private readonly int _sampleRate;
    private readonly ILogger _logger;

    /// <param name="stream">
    /// The camera's detect stream. Its audio is what gets transcribed — normally the sub stream,
    /// which carries the same sound as the main one at no extra cost to the camera.
    /// </param>
    /// <param name="ingest">
    /// Only <see cref="IngestOptions.FfmpegPath"/> is read. Narrowed to the slice rather than
    /// taking <c>IOptions&lt;ServerOptions&gt;</c>: this session's caller now composes a
    /// per-camera <see cref="AiOptions"/>, and a constructor asking for the whole server options
    /// would have kept a path to the global settings open through a class that has no business
    /// reading them.
    /// </param>
    public FfmpegAudioSession(
        Camera camera,
        CameraStream stream,
        AudioRingBuffer ringBuffer,
        int sampleRate,
        IngestOptions ingest,
        ILogger logger)
    {
        _camera = camera;
        _stream = stream;
        _ringBuffer = ringBuffer;
        _sampleRate = sampleRate;
        _options = ingest;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_options.FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string arg in BuildArguments())
        {
            startInfo.ArgumentList.Add(arg);
        }

        _logger.LogInformation(
            "Starting audio ingest for camera {CameraId} at {Rate} Hz mono.", _camera.Id, _sampleRate);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start ffmpeg for camera '{_camera.Id}' audio.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task stderrPump = PumpStderrAsync(process, linked.Token);

        try
        {
            await ReadPcmAsync(process, linked.Token);
        }
        finally
        {
            await linked.CancelAsync();

            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }

            try { await stderrPump; } catch { /* shutting down */ }
        }

        if (process.ExitCode != 0 && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"ffmpeg audio for camera '{_camera.Id}' exited with code {process.ExitCode}.");
        }
    }

    private IReadOnlyList<string> BuildArguments()
    {
        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "warning" };

        // audioOnly asks for the RTSP setup that makes a separate session cheap: only the audio
        // stream is negotiated, so the camera never sends us its video. No other protocol offers
        // that, so a non-RTSP source transports video anyway and the -map below discards it.
        args.AddRange(SourceArguments.InputArgs(_stream.Url, audioOnly: true));

        args.AddRange([
            // The '?' makes the audio stream optional. A camera with no audio then produces an
            // empty stream and exits cleanly instead of failing, which the supervisor reads as
            // "nothing to listen to" rather than as a fault to retry forever.
            "-map", "0:a?",
            "-f", "s16le",
            "-acodec", "pcm_s16le",
            "-ar", _sampleRate.ToString(),
            "-ac", "1",
            "pipe:1",
        ]);

        return args;
    }

    /// <summary>Converts interleaved int16 to the float samples the ring buffer carries.</summary>
    private async Task ReadPcmAsync(Process process, CancellationToken cancellationToken)
    {
        var bytes = new byte[ReadBufferBytes];
        var samples = new float[ReadBufferBytes / 2];
        int carry = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            int read = await process.StandardOutput.BaseStream.ReadAsync(
                bytes.AsMemory(carry, bytes.Length - carry), cancellationToken);

            if (read == 0)
            {
                _logger.LogInformation("Audio stream for camera {CameraId} ended.", _camera.Id);
                return;
            }

            int available = carry + read;

            // A read can land mid-sample; keep the odd trailing byte for the next pass rather
            // than dropping it, which would swap the high and low bytes of every later sample.
            int usable = available - (available % 2);
            int count = usable / 2;

            for (int i = 0; i < count; i++)
            {
                samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
            }

            if (!_ringBuffer.Write(samples.AsSpan(0, count)))
            {
                // The detection thread is not keeping up. Dropping is the only lock-free option
                // and it is counted, so a sustained fault is visible rather than silent.
                _logger.LogWarning(
                    "Dropped audio for camera {CameraId}; detection is falling behind ({Dropped} samples total).",
                    _camera.Id, _ringBuffer.DroppedSamples);
            }

            carry = available - usable;
            if (carry > 0)
            {
                bytes[0] = bytes[usable];
            }
        }
    }

    private async Task PumpStderrAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                _logger.LogWarning("[ffmpeg-audio {CameraId}] {Line}", _camera.Id, line);
            }
        }
        catch (OperationCanceledException) { }
    }
}
