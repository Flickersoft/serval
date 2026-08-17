namespace Serval.Ai;

/// <summary>A stretch of audio the segmenter decided was worth classifying.</summary>
public sealed record SoundSegment(float[] Samples, int SampleRate, DateTimeOffset CapturedAt)
{
    public double DurationSeconds => (double)Samples.Length / SampleRate;
}

/// <summary>Receives a segment the segmenter finished.</summary>
public delegate void SegmentSink(SoundSegment segment);

/// <summary>
/// Cuts the audio stream into clips to classify, using level alone.
///
/// The sound path's answer to what the VAD does for speech, and deliberately much simpler: no model,
/// only <see cref="AudioLevelGate"/>. A sound event is the audio between the gate opening and
/// closing, which for a door slam or a car horn is exactly right and costs nothing to compute.
///
/// Two properties of the gate do the real work:
///
/// <b>Pre-roll</b> starts the clip before the sound did. It matters more here than for speech — a
/// transient's onset is the most distinctive part of it, and an AudioSet model shown a bark from
/// halfway through has lost most of what identifies it.
///
/// <b>Hangover</b> runs the clip on past the sound, padding a 0.3-second bark out into the 1-10
/// second range these models are trained on. That is why
/// <see cref="SoundOptions.MinSegmentSeconds"/> can be a safety floor rather than a real constraint
/// on what can be detected.
///
/// One instance per camera: the gate it owns is stateful, and so is the accumulator.
/// </summary>
public sealed class SoundEventSegmenter
{
    private readonly AudioLevelGate _gate;
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _sampleRate;
    private readonly int _minSamples;
    private readonly int _maxSamples;

    // Cached so the hot path allocates no delegate: Accept runs ~31 times a second per camera.
    private readonly WindowSink _append;

    private readonly List<float> _buffer;

    public SoundEventSegmenter(
        SoundOptions options, int windowSize, int sampleRate, Func<DateTimeOffset>? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        _gate = new AudioLevelGate(options.Gate, windowSize, sampleRate);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _sampleRate = sampleRate;
        WindowSize = windowSize;

        _minSamples = (int)(options.MinSegmentSeconds * sampleRate);
        _maxSamples = Math.Max(windowSize, (int)(options.MaxSegmentSeconds * sampleRate));

        // Sized for the longest clip that can be emitted, so a busy scene never grows the list.
        _buffer = new List<float>(_maxSamples);
        _append = window =>
        {
            foreach (float sample in window)
            {
                _buffer.Add(sample);
            }
        };
    }

    public int WindowSize { get; }

    /// <summary>Whether audio is currently being accumulated. Diagnostics only.</summary>
    public bool IsOpen => _gate.IsOpen;

    /// <summary>Segments handed to the sink. With the policy's counters, this is how a badly set
    /// threshold is told apart from a quiet room.</summary>
    public long SegmentsEmitted { get; private set; }

    /// <summary>Segments dropped for being shorter than <see cref="SoundOptions.MinSegmentSeconds"/>.</summary>
    public long SegmentsTooShort { get; private set; }

    /// <summary>Loudness of the most recent window, for threshold tuning.</summary>
    public float LastRms => _gate.LastRms;

    /// <summary>
    /// Feeds one window. Allocation-free except on the window that completes a segment. Pass a
    /// cached delegate: this is called ~31 times a second per camera.
    /// </summary>
    public void Accept(ReadOnlySpan<float> window, SegmentSink sink)
    {
        bool wasOpen = _gate.IsOpen;

        // The gate replays its pre-roll into the sink on the window it opens on, so the buffer
        // picks up the run-up to the sound without the segmenter tracking it separately.
        _gate.Accept(window, _append);

        // Hard cut first. A sound with no end — traffic, a mower, rain — never closes the gate, and
        // without this it would accumulate until the process died with nothing ever classified.
        if (_buffer.Count >= _maxSamples)
        {
            Emit(sink);
            return;
        }

        // The defining event is the gate closing: that is hangover having elapsed, which means the
        // sound stopped a comfortable margin ago and the clip is complete.
        if (wasOpen && !_gate.IsOpen)
        {
            Emit(sink);
        }
    }

    private void Emit(SegmentSink sink)
    {
        if (_buffer.Count < _minSamples)
        {
            // Too short to classify. A single click or a door catch: the model would return
            // something, and it would be noise.
            SegmentsTooShort++;
            _buffer.Clear();
            return;
        }

        float[] samples = _buffer.ToArray();
        _buffer.Clear();

        // Timestamp the *start* of the sound, not the moment it was cut. The clip ends when the
        // hangover expires, which is over a second after anything happened; reporting that instant
        // would put every record consistently late by the hangover.
        var capturedAt = _clock() - TimeSpan.FromSeconds((double)samples.Length / _sampleRate);

        SegmentsEmitted++;
        sink(new SoundSegment(samples, _sampleRate, capturedAt));
    }
}
