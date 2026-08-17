using Serval.Ai;

namespace Serval.Ai.Tests;

/// <summary>
/// Pins the RMS gate. Its two subtle behaviours — pre-roll and hangover — are the difference
/// between saving VAD work and quietly losing speech, and neither is visible from the outside
/// until a word goes missing in production.
/// </summary>
public class AudioLevelGateTests
{
    private const int WindowSize = 512;
    private const int SampleRate = 16000;

    /// <summary>Collects what the gate forwards, so a test can assert on the detector's-eye view.</summary>
    private sealed class Sink
    {
        public List<float[]> Windows { get; } = [];

        public WindowSink Delegate => window => Windows.Add(window.ToArray());
    }

    private static float[] Loud(float amplitude = 0.5f)
    {
        var window = new float[WindowSize];
        // Alternating sign: a DC block would have the same RMS but is not a signal any real
        // capture path produces, and squaring makes the two identical anyway.
        for (int i = 0; i < window.Length; i++)
        {
            window[i] = i % 2 == 0 ? amplitude : -amplitude;
        }

        return window;
    }

    private static float[] Silence() => new float[WindowSize];

    private static AudioLevelGate Gate(AudioGateOptions? options = null) =>
        new(options ?? new AudioGateOptions(), WindowSize, SampleRate);

    [Fact]
    public void Silence_is_not_forwarded_to_the_detector()
    {
        var gate = Gate();
        var sink = new Sink();

        for (int i = 0; i < 100; i++)
        {
            gate.Accept(Silence(), sink.Delegate);
        }

        Assert.Empty(sink.Windows);
        Assert.False(gate.IsOpen);
        Assert.Equal(100, gate.WindowsSkipped);
        Assert.Equal(0, gate.WindowsAdmitted);
    }

    [Fact]
    public void A_loud_window_opens_the_gate()
    {
        var gate = Gate();
        var sink = new Sink();

        gate.Accept(Loud(), sink.Delegate);

        Assert.True(gate.IsOpen);
        Assert.NotEmpty(sink.Windows);
    }

    [Fact]
    public void Buffered_pre_roll_is_replayed_in_order_when_the_gate_opens()
    {
        // The onset of the first word is the whole reason pre-roll exists: a VAD handed a word
        // that starts mid-vowel reports no speech at all.
        var gate = Gate(new AudioGateOptions { PreRollWindows = 4 });
        var sink = new Sink();

        // Distinguishable quiet windows, all under the threshold, so we can assert on ordering.
        for (int i = 1; i <= 4; i++)
        {
            var quiet = new float[WindowSize];
            quiet[0] = i * 1e-6f;
            gate.Accept(quiet, sink.Delegate);
        }

        Assert.Empty(sink.Windows);

        gate.Accept(Loud(), sink.Delegate);

        // Four buffered windows, oldest first, then the loud one that opened the gate.
        Assert.Equal(5, sink.Windows.Count);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal((i + 1) * 1e-6f, sink.Windows[i][0], precision: 9);
        }
    }

    [Fact]
    public void Pre_roll_keeps_only_the_most_recent_windows()
    {
        var gate = Gate(new AudioGateOptions { PreRollWindows = 3 });
        var sink = new Sink();

        for (int i = 1; i <= 10; i++)
        {
            var quiet = new float[WindowSize];
            quiet[0] = i * 1e-6f;
            gate.Accept(quiet, sink.Delegate);
        }

        gate.Accept(Loud(), sink.Delegate);

        // The last three quiet windows (8, 9, 10), then the loud one.
        Assert.Equal(4, sink.Windows.Count);
        Assert.Equal(8e-6f, sink.Windows[0][0], precision: 9);
        Assert.Equal(9e-6f, sink.Windows[1][0], precision: 9);
        Assert.Equal(10e-6f, sink.Windows[2][0], precision: 9);
    }

    [Fact]
    public void Hangover_keeps_forwarding_silence_after_speech_stops()
    {
        // Silero only emits a finished utterance once it has seen MinSilenceSeconds of trailing
        // silence. A gate that shut the instant speech stopped would starve it of exactly that,
        // and the last utterance would never be emitted at all.
        var gate = Gate(new AudioGateOptions { HangoverSeconds = 1.0, PreRollWindows = 0 });
        var sink = new Sink();

        gate.Accept(Loud(), sink.Delegate);
        int afterSpeech = sink.Windows.Count;

        // 1.0s at 16 kHz / 512 = ~31 windows of hangover.
        for (int i = 0; i < 20; i++)
        {
            gate.Accept(Silence(), sink.Delegate);
        }

        Assert.True(gate.IsOpen);
        Assert.Equal(afterSpeech + 20, sink.Windows.Count);
    }

    [Fact]
    public void The_gate_closes_once_the_hangover_elapses()
    {
        var gate = Gate(new AudioGateOptions { HangoverSeconds = 0.1, PreRollWindows = 0 });
        var sink = new Sink();

        gate.Accept(Loud(), sink.Delegate);

        // 0.1s / (512/16000 = 32ms) = 4 windows.
        for (int i = 0; i < 10; i++)
        {
            gate.Accept(Silence(), sink.Delegate);
        }

        Assert.False(gate.IsOpen);

        long admitted = gate.WindowsAdmitted;
        gate.Accept(Silence(), sink.Delegate);
        Assert.Equal(admitted, gate.WindowsAdmitted);
    }

    [Fact]
    public void A_short_dip_inside_speech_does_not_close_the_gate()
    {
        // The gap between two words is silence too. Closing there would cut an utterance in half.
        var gate = Gate(new AudioGateOptions { HangoverSeconds = 1.0, PreRollWindows = 0 });
        var sink = new Sink();

        gate.Accept(Loud(), sink.Delegate);
        for (int i = 0; i < 5; i++)
        {
            gate.Accept(Silence(), sink.Delegate);
        }

        Assert.True(gate.IsOpen);

        gate.Accept(Loud(), sink.Delegate);
        Assert.True(gate.IsOpen);
        Assert.Equal(0, gate.WindowsSkipped);
    }

    [Fact]
    public void A_disabled_gate_forwards_everything()
    {
        // The escape hatch: restores the original behaviour of running Silero on every window.
        var gate = Gate(new AudioGateOptions { Enabled = false });
        var sink = new Sink();

        for (int i = 0; i < 10; i++)
        {
            gate.Accept(Silence(), sink.Delegate);
        }

        Assert.Equal(10, sink.Windows.Count);
        Assert.Equal(0, gate.WindowsSkipped);
    }

    [Fact]
    public void The_threshold_decides_what_counts_as_speech()
    {
        var sink = new Sink();
        var strict = Gate(new AudioGateOptions { RmsThreshold = 0.4f });

        strict.Accept(Loud(amplitude: 0.1f), sink.Delegate);
        Assert.False(strict.IsOpen);

        strict.Accept(Loud(amplitude: 0.5f), sink.Delegate);
        Assert.True(strict.IsOpen);
    }

    [Fact]
    public void Hangover_is_never_shorter_than_one_window()
    {
        // A zero hangover would let the gate close inside the same utterance it just opened.
        var gate = Gate(new AudioGateOptions { HangoverSeconds = 0, PreRollWindows = 0 });
        var sink = new Sink();

        gate.Accept(Loud(), sink.Delegate);
        gate.Accept(Silence(), sink.Delegate);

        Assert.Equal(2, sink.Windows.Count);
    }

    [Fact]
    public void A_wrongly_sized_window_is_rejected()
    {
        var gate = Gate();
        var sink = new Sink();

        Assert.Throws<ArgumentException>(() => gate.Accept(new float[256], sink.Delegate));
    }

    /// <summary>
    /// The level meter draws its threshold line against <see cref="AudioLevelGate.Rms"/>, so the
    /// number it reports has to be the number the gate compares. A meter reading a fraction
    /// differently would show audio crossing a line while the gate stayed shut, which is worse
    /// than no meter — it is the one instrument someone tuning the threshold would trust.
    ///
    /// Asserted against a closed form: <see cref="Loud"/> alternates ±amplitude, whose RMS is
    /// exactly the amplitude.
    /// </summary>
    [Theory]
    [InlineData(0.5f)]
    [InlineData(0.01f)]
    [InlineData(0.0015f)]
    public void Rms_is_the_same_number_the_gate_compares_against(float amplitude)
    {
        var gate = Gate(new AudioGateOptions { RmsThreshold = amplitude });
        var sink = new Sink();

        float[] window = Loud(amplitude);

        Assert.Equal(amplitude, AudioLevelGate.Rms(window), 5);

        // At exactly the threshold the gate opens, which is what makes the static the right number
        // to draw the line with.
        gate.Accept(window, sink.Delegate);
        Assert.True(gate.IsOpen);
        Assert.Equal(amplitude, gate.LastRms, 5);
    }

    /// <summary>
    /// Why the meter computes its own RMS rather than reading <see cref="AudioLevelGate.LastRms"/>:
    /// the disabled path forwards everything and never measures. Reading the property would show a
    /// flat zero to precisely the person who turned the gate off to hear what the room sounds like.
    /// </summary>
    [Fact]
    public void A_disabled_gate_reports_no_level()
    {
        var gate = Gate(new AudioGateOptions { Enabled = false });
        var sink = new Sink();

        gate.Accept(Loud(), sink.Delegate);

        Assert.Single(sink.Windows);      // forwarded, as a disabled gate should
        Assert.Equal(0f, gate.LastRms);   // but never measured
    }
}
