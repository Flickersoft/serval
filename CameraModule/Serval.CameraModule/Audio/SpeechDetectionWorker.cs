using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serval.Ai;

namespace Serval.CameraModule;

/// <summary>
/// Drains the ring buffer, gates it on level, and runs Silero VAD to cut the stream into
/// utterances. Also tees the same audio to the sound tagger, when that is enabled.
///
/// The RMS gate in front of the detector is what keeps a quiet room cheap: Silero is an ONNX
/// forward pass on every 512-sample window, which in an empty house is entirely wasted work and on
/// the Pi competes for the same cores as vision. The gate's pre-roll and hangover mean it only
/// closes well after speech ends, so nothing is clipped — see <see cref="AudioLevelGate"/>.
///
/// Sound detection runs <em>beside</em> the VAD, not behind it: Silero rejects everything that is
/// not speech, so a car horn could never reach the speech path however it were configured. The
/// segmenter gets its own copy of every window and its own level gate. The two are expected to
/// overlap — a conversation with a dog barking over it produces an utterance and a sound record.
///
/// Both live on this one thread because <see cref="AudioRingBuffer"/> is single-consumer. That is a
/// constraint on who may read the ring, not a coupling between the detectors.
/// </summary>
public sealed class SpeechDetectionWorker : BackgroundService
{
    private readonly AudioRingBuffer _ringBuffer;
    private readonly ChannelWriter<CapturedSpeech> _output;
    private readonly CameraModuleOptions _options;
    private readonly ILogger<SpeechDetectionWorker> _logger;
    private readonly ConversationTracker? _conversations;
    private readonly SoundEventSegmenter? _segmenter;
    private readonly ChannelWriter<SoundSegment>? _sounds;

    public SpeechDetectionWorker(
        AudioRingBuffer ringBuffer,
        Channel<CapturedSpeech> channel,
        IOptions<CameraModuleOptions> options,
        ILogger<SpeechDetectionWorker> logger,
        ConversationTracker? conversations = null,
        SoundEventSegmenter? segmenter = null,
        Channel<SoundSegment>? sounds = null)
    {
        _ringBuffer = ringBuffer;
        _output = channel.Writer;
        _options = options.Value;
        _logger = logger;
        _conversations = conversations;
        _segmenter = segmenter;
        _sounds = sounds?.Writer;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A dedicated long-running thread, not a thread-pool task: this loop runs
        // continuously and would otherwise occupy a pool thread indefinitely.
        return Task.Factory.StartNew(
            () => RunDetectionLoop(stoppingToken),
            stoppingToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void RunDetectionLoop(CancellationToken stoppingToken)
    {
        VadOptions vad = _options.Vad;

        using var detector = new SileroSpeechDetector(vad, _logger);
        var gate = new AudioLevelGate(_options.AudioGate, detector.WindowSize, _options.Audio.SampleRate);

        // Cached so the hot path allocates no delegate: these run ~31 times a second.
        WindowSink sink = detector.Accept;
        SegmentSink segmentSink = EmitSegment;

        _logger.LogInformation(
            "Audio level gate {State} (threshold={Threshold}, pre-roll={PreRoll} windows, hangover={Hangover}s)",
            _options.AudioGate.Enabled ? "enabled" : "disabled",
            _options.AudioGate.RmsThreshold,
            _options.AudioGate.PreRollWindows,
            _options.AudioGate.HangoverSeconds);

        // Silero v5 requires exactly WindowSize samples per call, so partial reads are
        // accumulated here rather than passed through.
        var window = new float[detector.WindowSize];
        int filled = 0;
        long lastReport = Environment.TickCount64;

        while (!stoppingToken.IsCancellationRequested)
        {
            int read = _ringBuffer.Read(window.AsSpan(filled));
            if (read == 0)
            {
                // Roughly one window at 16 kHz; long enough to avoid a spin, short enough
                // that the ring buffer cannot overrun.
                Thread.Sleep(16);
                continue;
            }

            filled += read;
            if (filled < window.Length)
            {
                continue;
            }

            filled = 0;

            // Tee every window — including silence, and including windows the gate will drop —
            // into the current conversation's audio. This thread already holds all the audio, so
            // no second ring-buffer consumer is needed and the single-producer/single-consumer
            // invariant is preserved. Continuous audio (rather than concatenated utterances) is
            // what lets diarization segment times be plain offsets from the conversation start,
            // so it must not be gated: a hole here would silently shift every later timestamp.
            _conversations?.AppendAudio(window);

            // Parallel to the VAD below, not behind it. This branch has its own level gate and its
            // own segmentation, and neither branch can gate or starve the other.
            _segmenter?.Accept(window, segmentSink);

            gate.Accept(window, sink);

            while (detector.TryDequeue(out float[] utterance))
            {
                Emit(utterance);
            }

            lastReport = ReportGateSavings(gate, lastReport);
        }
    }

    /// <summary>
    /// Periodically reports how much VAD work the gate avoided. Worth logging rather than
    /// inferring: a gate saving nothing means the threshold is below the room's noise floor, and a
    /// gate saving everything means it is above the speech.
    /// </summary>
    private long ReportGateSavings(AudioLevelGate gate, long lastReport)
    {
        const long IntervalMs = 60_000;

        long now = Environment.TickCount64;
        if (now - lastReport < IntervalMs)
        {
            return lastReport;
        }

        long total = gate.WindowsAdmitted + gate.WindowsSkipped;
        if (total > 0)
        {
            _logger.LogDebug(
                "Audio gate: {Skipped}/{Total} windows skipped ({Percent:F0}%), last RMS {Rms:F4}.",
                gate.WindowsSkipped, total, 100.0 * gate.WindowsSkipped / total, gate.LastRms);
        }

        // Reported for the same reason as the gate savings above: a segmenter emitting nothing
        // means its threshold is above everything the camera hears, and one emitting constantly
        // means it is below the noise floor and the per-label cooldown is the only thing holding
        // the outbox back. Neither is visible without counting.
        if (_segmenter is not null)
        {
            _logger.LogDebug(
                "Sound segments: {Emitted} emitted, {TooShort} too short.",
                _segmenter.SegmentsEmitted, _segmenter.SegmentsTooShort);
        }

        return now;
    }

    private void EmitSegment(SoundSegment segment)
    {
        // Bounded with DropOldest, so a failed write means shutdown rather than a full channel.
        // Under load the newest segment is the one worth keeping — an old one describes a sound
        // that has already finished happening.
        if (_sounds?.TryWrite(segment) == false)
        {
            _logger.LogDebug(
                "Dropped a {Seconds:F2}s sound segment: the pipeline is shutting down.",
                segment.DurationSeconds);
        }
    }

    private void Emit(float[] samples)
    {
        double seconds = (double)samples.Length / _options.Audio.SampleRate;

        var captured = new CapturedSpeech(samples, _options.Audio.SampleRate, DateTimeOffset.UtcNow);

        if (_output.TryWrite(captured))
        {
            _logger.LogInformation("Utterance detected ({Seconds:F2}s)", seconds);
            return;
        }

        // The channel is bounded with DropOldest, so TryWrite failing means the writer is
        // completed (shutdown) rather than full.
        _logger.LogDebug("Dropped a {Seconds:F2}s utterance: the pipeline is shutting down.", seconds);
    }
}
