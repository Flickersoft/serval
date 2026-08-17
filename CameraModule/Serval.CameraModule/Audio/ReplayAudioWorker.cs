using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Serval.Ai;

namespace Serval.CameraModule;

/// <summary>
/// Feeds a WAV file into the ring buffer in place of the microphone, so the real VAD,
/// inference, and outbox path can be exercised deterministically:
///   dotnet run -- --replay recording.wav
///
/// This is how you reproduce a bad transcript or verify a deployment without standing in
/// front of a mic. It writes through the same AudioRingBuffer the PortAudio callback uses,
/// so everything downstream is genuinely under test.
/// </summary>
public sealed class ReplayAudioWorker : BackgroundService
{
    private readonly AudioRingBuffer _ringBuffer;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly AudioOptions _options;
    private readonly ILogger<ReplayAudioWorker> _logger;
    private readonly string _path;
    private readonly bool _visionEnabled;
    private readonly bool _speakerEnabled;
    private readonly TimeSpan _silenceTimeout;

    public ReplayAudioWorker(
        AudioRingBuffer ringBuffer,
        IHostApplicationLifetime lifetime,
        IOptions<CameraModuleOptions> options,
        ILogger<ReplayAudioWorker> logger,
        ReplaySource source)
    {
        _ringBuffer = ringBuffer;
        _lifetime = lifetime;
        _options = options.Value.Audio;
        _visionEnabled = options.Value.Vision.Enabled;
        _speakerEnabled = options.Value.Speaker.Enabled;
        _silenceTimeout = TimeSpan.FromMinutes(options.Value.Speaker.SilenceTimeoutMinutes);
        _logger = logger;
        _path = source.Path;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        (float[] samples, int sampleRate) = WavFile.Read(_path);

        if (sampleRate != _options.SampleRate)
        {
            _logger.LogCritical(
                "{Path} is {Actual} Hz but the pipeline expects {Expected} Hz. Resample it first, e.g. "
                + "sox in.wav -r {Expected} -c 1 out.wav",
                _path, sampleRate, _options.SampleRate, _options.SampleRate);
            _lifetime.StopApplication();
            return;
        }

        _logger.LogInformation(
            "Replaying {Path} ({Seconds:F2}s) through the live pipeline.",
            _path, samples.Length / (double)sampleRate);

        // Feed at roughly realtime rather than as fast as the ring accepts. This is the whole
        // point of replaying "through the live pipeline": timing-dependent behaviour has to
        // behave the same. Pushing at full speed made a 19s file land in ~2s, which starved
        // the vision path of any chance to attach a description and hid the real ordering.
        int chunk = _options.FramesPerBuffer;
        double secondsPerChunk = (double)chunk / _options.SampleRate;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        for (int offset = 0; offset < samples.Length && !stoppingToken.IsCancellationRequested; offset += chunk)
        {
            int count = Math.Min(chunk, samples.Length - offset);

            while (!_ringBuffer.Write(samples.AsSpan(offset, count)) && !stoppingToken.IsCancellationRequested)
            {
                // Ring full: the VAD is behind. Unlike the realtime callback we can wait
                // rather than drop.
                await Task.Delay(5, stoppingToken);
            }

            double due = (offset / (double)chunk + 1) * secondsPerChunk;
            double behind = due - clock.Elapsed.TotalSeconds;
            if (behind > 0.001)
            {
                await Task.Delay(TimeSpan.FromSeconds(behind), stoppingToken);
            }
        }

        // Append silence so the VAD's hangover fires and the final utterance is emitted
        // rather than left open.
        var silence = new float[chunk];
        int silenceChunks = (int)(_options.SampleRate * 2.0 / chunk);
        for (int i = 0; i < silenceChunks && !stoppingToken.IsCancellationRequested; i++)
        {
            _ringBuffer.Write(silence);
            await Task.Delay(1, stoppingToken);
        }

        // Let the slower stages drain before shutting the host down; stopping mid-inference
        // just throws the work away. Vision takes seconds per image (tens on the Pi). Speaker
        // work needs longer still: the conversation must go quiet, be noticed by the timer,
        // and then be diarized — all while the host is still running, since a conversation
        // finalised during shutdown is left for startup recovery instead.
        double seconds = 5;
        if (_visionEnabled)
        {
            seconds = Math.Max(seconds, 60);
        }

        if (_speakerEnabled)
        {
            seconds = Math.Max(seconds, _silenceTimeout.TotalSeconds + 60);
        }

        await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);

        _logger.LogInformation("Replay complete.");
        _lifetime.StopApplication();
    }
}

/// <summary>Carries the replay file path to <see cref="ReplayAudioWorker"/> via DI.</summary>
public sealed record ReplaySource(string Path);
