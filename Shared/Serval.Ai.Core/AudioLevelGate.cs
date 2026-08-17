namespace Serval.Ai;

/// <summary>Receives a window the gate decided to let through.</summary>
public delegate void WindowSink(ReadOnlySpan<float> window);

/// <summary>
/// The cheap half of the audio pipeline: an RMS threshold in front of the VAD, so the Silero ONNX
/// model is not run on a silent room. Pure and allocation-free after construction — it sees every
/// 512-sample window from every camera.
///
/// Two details are what make skipping windows safe, and neither is optional:
///
/// <b>Pre-roll.</b> While closed, windows are kept in a small ring and replayed into the detector
/// the moment the gate opens. Without it the gate eats the attack of the first word, and a VAD fed
/// a word starting mid-vowel reports no speech.
///
/// <b>Hangover.</b> Once open the gate stays open for <see cref="AudioGateOptions.HangoverSeconds"/>
/// past the last loud window, still forwarding those quiet windows. Silero only emits a finished
/// utterance after <c>MinSilenceSeconds</c> of trailing silence, so a gate slamming shut the instant
/// speech stops starves it of the silence it is waiting for and the last utterance never arrives.
/// The default hangover is deliberately longer than the default min-silence.
///
/// Together the gate only ever closes well after speech has ended and opens before it begins, so the
/// detector never sees a discontinuity inside an utterance.
/// </summary>
public sealed class AudioLevelGate
{
    private readonly AudioGateOptions _options;
    private readonly float[][] _preRoll;
    private readonly int _hangoverWindows;

    private int _preRollCount;
    private int _preRollHead;
    private int _quietWindows;

    /// <param name="windowSize">Samples per window; must match the detector's window size.</param>
    public AudioLevelGate(AudioGateOptions options, int windowSize, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        _options = options;
        WindowSize = windowSize;

        int preRollCapacity = Math.Max(options.PreRollWindows, 0);
        _preRoll = new float[preRollCapacity][];
        for (int i = 0; i < preRollCapacity; i++)
        {
            _preRoll[i] = new float[windowSize];
        }

        // At least one window of hangover, so a configured-to-zero value cannot close the gate
        // inside the same utterance that just opened it.
        double windowSeconds = (double)windowSize / sampleRate;
        _hangoverWindows = Math.Max(1, (int)Math.Ceiling(options.HangoverSeconds / windowSeconds));
    }

    public int WindowSize { get; }

    /// <summary>Whether audio is currently being forwarded to the detector.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Windows handed to the detector. With <see cref="WindowsSkipped"/>, this is the gate's whole value.</summary>
    public long WindowsAdmitted { get; private set; }

    /// <summary>Windows the detector never had to run on.</summary>
    public long WindowsSkipped { get; private set; }

    /// <summary>Loudness of the most recent window, for diagnostics and threshold tuning.</summary>
    public float LastRms { get; private set; }

    /// <summary>
    /// Feeds one window, forwarding it (and any buffered pre-roll) to <paramref name="sink"/> if the
    /// gate is open. Pass a cached delegate: this is called ~31 times a second per camera.
    /// </summary>
    public void Accept(ReadOnlySpan<float> window, WindowSink sink)
    {
        if (window.Length != WindowSize)
        {
            throw new ArgumentException(
                $"Expected a {WindowSize}-sample window, got {window.Length}.", nameof(window));
        }

        if (!_options.Enabled)
        {
            Forward(window, sink);
            return;
        }

        LastRms = Rms(window);

        if (LastRms >= _options.RmsThreshold)
        {
            if (!IsOpen)
            {
                IsOpen = true;
                FlushPreRoll(sink);
            }

            _quietWindows = 0;
            Forward(window, sink);
            return;
        }

        if (IsOpen)
        {
            // Still forwarding: this is the trailing silence the detector needs in order to decide
            // an utterance has ended.
            Forward(window, sink);

            if (++_quietWindows >= _hangoverWindows)
            {
                IsOpen = false;
                _quietWindows = 0;
            }

            return;
        }

        Remember(window);
        WindowsSkipped++;
    }

    private void Forward(ReadOnlySpan<float> window, WindowSink sink)
    {
        WindowsAdmitted++;
        sink(window);
    }

    /// <summary>Replays the buffered windows oldest-first, then empties the ring.</summary>
    private void FlushPreRoll(WindowSink sink)
    {
        // Oldest entry sits _preRollCount places behind the write head, modulo capacity.
        int capacity = _preRoll.Length;
        int start = (_preRollHead - _preRollCount + capacity) % Math.Max(capacity, 1);

        for (int i = 0; i < _preRollCount; i++)
        {
            Forward(_preRoll[(start + i) % capacity], sink);
        }

        _preRollCount = 0;
        _preRollHead = 0;
    }

    private void Remember(ReadOnlySpan<float> window)
    {
        if (_preRoll.Length == 0)
        {
            return;
        }

        window.CopyTo(_preRoll[_preRollHead]);
        _preRollHead = (_preRollHead + 1) % _preRoll.Length;
        _preRollCount = Math.Min(_preRollCount + 1, _preRoll.Length);
    }

    /// <summary>
    /// Window loudness, as the gate itself measures it.
    ///
    /// Public so a level meter reports the same number the threshold is compared against. A meter
    /// that computed its own RMS a fraction differently would draw a line the audio appears to
    /// cross while the gate stays shut — which is worse than no meter, because it is the one
    /// instrument a person tuning <see cref="AudioGateOptions.RmsThreshold"/> would trust.
    ///
    /// Callers outside the gate need it because <see cref="LastRms"/> is only assigned on the
    /// enabled path: a disabled gate forwards everything and never measures, so a meter reading
    /// that property would show a flat zero to precisely the person who turned the gate off to
    /// find out what the room sounds like.
    /// </summary>
    public static float Rms(ReadOnlySpan<float> window)
    {
        // Accumulate in double: 512 squared floats is fine in single precision, but the cost is
        // nil and it keeps the threshold meaning the same thing at any window size.
        double sum = 0;
        for (int i = 0; i < window.Length; i++)
        {
            sum += (double)window[i] * window[i];
        }

        return (float)Math.Sqrt(sum / window.Length);
    }
}
