using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortAudioSharp;

using Serval.Ai;

namespace Serval.CameraModule;

/// <summary>
/// Owns the PortAudio input stream and feeds the ring buffer. Nothing else.
///
/// The callback runs on a realtime audio thread: it converts int16 to float in place and
/// returns. It must not allocate, lock, or run inference — running the VAD inline here causes
/// dropouts, badly so on the Pi.
/// </summary>
public sealed class AudioCaptureWorker : BackgroundService
{
    private readonly AudioRingBuffer _ringBuffer;
    private readonly AudioOptions _options;
    private readonly ILogger<AudioCaptureWorker> _logger;

    // Held as a field so the GC cannot collect the delegate while PortAudio holds a
    // native pointer to it.
    private PortAudioSharp.Stream.Callback? _callback;

    // Pre-allocated scratch for the callback. Sized well above FramesPerBuffer because
    // PortAudio may hand us a larger block than requested.
    private readonly float[] _scratch;
    private readonly float _gain;

    private long _framesCaptured;
    private long _lastDropWarning;
    private double _peakLevel;

    public AudioCaptureWorker(
        AudioRingBuffer ringBuffer,
        IOptions<CameraModuleOptions> options,
        ILogger<AudioCaptureWorker> logger)
    {
        _ringBuffer = ringBuffer;
        _options = options.Value.Audio;
        _logger = logger;
        _scratch = new float[Math.Max(_options.FramesPerBuffer * 8, 8192)];
        _gain = _options.InputGain;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            PortAudio.Initialize();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "PortAudio failed to initialize. Audio capture cannot start.");
            return;
        }

        try
        {
            int device = SelectInputDevice();
            var info = PortAudio.GetDeviceInfo(device);
            _logger.LogInformation(
                "Capturing from device {Device}: {Name} ({SampleRate} Hz, gain {Gain}x)",
                device, info.name, _options.SampleRate, _gain);

            var parameters = new StreamParameters
            {
                device = device,
                channelCount = 1,
                // Int16: some ALSA capture devices silently produce nothing when asked for Float32.
                sampleFormat = SampleFormat.Int16,
                suggestedLatency = info.defaultLowInputLatency,
                hostApiSpecificStreamInfo = IntPtr.Zero,
            };

            _callback = OnAudioAvailable;

            using var stream = new PortAudioSharp.Stream(
                inParams: parameters,
                outParams: null,
                sampleRate: _options.SampleRate,
                framesPerBuffer: (uint)_options.FramesPerBuffer,
                streamFlags: StreamFlags.NoFlag,
                callback: _callback,
                userData: IntPtr.Zero);

            stream.Start();
            _logger.LogInformation("Listening.");

            await MonitorHealthAsync(stoppingToken);

            stream.Stop();
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Audio capture failed. Check that an input device is connected.");
        }
        finally
        {
            try
            {
                PortAudio.Terminate();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "PortAudio.Terminate threw during shutdown.");
            }
        }
    }

    private unsafe StreamCallbackResult OnAudioAvailable(
        IntPtr input,
        IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags,
        IntPtr userData)
    {
        if (input == IntPtr.Zero)
        {
            return StreamCallbackResult.Continue;
        }

        int count = (int)frameCount;
        if (count > _scratch.Length)
        {
            count = _scratch.Length;
        }

        var samples = (short*)input;
        double peak = 0;
        for (int i = 0; i < count; i++)
        {
            float value = samples[i] / 32768f * _gain;
            value = Math.Clamp(value, -1f, 1f);
            _scratch[i] = value;

            double magnitude = value < 0 ? -value : value;
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        _ringBuffer.Write(_scratch.AsSpan(0, count));

        Volatile.Write(ref _peakLevel, peak);
        Interlocked.Add(ref _framesCaptured, count);
        return StreamCallbackResult.Continue;
    }

    /// <summary>
    /// Reports capture health on a timer rather than per callback. Silence and a dead device look
    /// identical without a level reading, and guessing between them is how a fixed input gain gets
    /// bolted on to paper over a wiring fault.
    /// </summary>
    private async Task MonitorHealthAsync(CancellationToken stoppingToken)
    {
        long lastFrames = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            long frames = Interlocked.Read(ref _framesCaptured);
            long dropped = _ringBuffer.DroppedSamples;

            if (frames == lastFrames)
            {
                _logger.LogWarning("No audio received in the last 10s. Is the input device still present?");
            }
            else
            {
                _logger.LogDebug(
                    "Capture healthy: {Seconds:F0}s of audio, peak level {Peak:P0}",
                    frames / (double)_options.SampleRate, Volatile.Read(ref _peakLevel));
            }

            if (dropped > _lastDropWarning)
            {
                _logger.LogWarning(
                    "Ring buffer overrun: {Dropped} samples dropped in total. Speech detection is not keeping up.",
                    dropped);
                _lastDropWarning = dropped;
            }

            lastFrames = frames;
        }
    }

    private int SelectInputDevice()
    {
        int deviceCount = PortAudio.DeviceCount;

        if (!string.IsNullOrWhiteSpace(_options.DeviceName))
        {
            for (int i = 0; i < deviceCount; i++)
            {
                var info = PortAudio.GetDeviceInfo(i);
                if (info.maxInputChannels > 0 &&
                    info.name.Contains(_options.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            _logger.LogWarning(
                "No input device matching '{Name}'. Falling back to the default input.", _options.DeviceName);
        }

        int fallback = PortAudio.DefaultInputDevice;
        if (fallback == PortAudio.NoDevice)
        {
            throw new InvalidOperationException("PortAudio reports no default input device.");
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            for (int i = 0; i < deviceCount; i++)
            {
                var info = PortAudio.GetDeviceInfo(i);
                if (info.maxInputChannels > 0)
                {
                    _logger.LogDebug("Input device {Index}: {Name}", i, info.name);
                }
            }
        }

        return fallback;
    }
}
