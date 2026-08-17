using Serval.Ai;

namespace Serval.Ai.Tests;

/// <summary>
/// Pins how audio is cut into clips for the tagger. The interesting cases are all about the
/// boundaries: a clip that starts too late has lost the onset the model needs, a clip that never
/// ends is never classified at all, and a timestamp taken at the wrong moment puts every sound
/// record consistently late.
/// </summary>
public class SoundEventSegmenterTests
{
    private const int WindowSize = 512;
    private const int SampleRate = 16000;

    /// <summary>Collects emitted segments so a test can assert on what the tagger would see.</summary>
    private sealed class Sink
    {
        public List<SoundSegment> Segments { get; } = [];

        public SegmentSink Delegate => segment => Segments.Add(segment);
    }

    /// <summary>A clock the test advances by hand, one window at a time.</summary>
    private sealed class Clock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

        public Func<DateTimeOffset> Read => () => Now;

        public void AdvanceOneWindow() =>
            Now += TimeSpan.FromSeconds((double)WindowSize / SampleRate);
    }

    private static float[] Loud(float amplitude = 0.5f)
    {
        var window = new float[WindowSize];
        for (int i = 0; i < window.Length; i++)
        {
            window[i] = i % 2 == 0 ? amplitude : -amplitude;
        }

        return window;
    }

    private static float[] Silence() => new float[WindowSize];

    private static SoundOptions Options(Action<SoundOptions>? configure = null)
    {
        var options = new SoundOptions();
        configure?.Invoke(options);
        return options;
    }

    private static (SoundEventSegmenter Segmenter, Sink Sink, Clock Clock) Build(
        SoundOptions? options = null)
    {
        var clock = new Clock();
        var segmenter = new SoundEventSegmenter(
            options ?? Options(), WindowSize, SampleRate, clock.Read);

        return (segmenter, new Sink(), clock);
    }

    /// <summary>Feeds windows, advancing the clock as real audio would.</summary>
    private static void Feed(
        SoundEventSegmenter segmenter, Sink sink, Clock clock, float[] window, int count)
    {
        for (int i = 0; i < count; i++)
        {
            segmenter.Accept(window, sink.Delegate);
            clock.AdvanceOneWindow();
        }
    }

    [Fact]
    public void Silence_produces_no_segments()
    {
        (SoundEventSegmenter segmenter, Sink sink, Clock clock) = Build();

        Feed(segmenter, sink, clock, Silence(), 200);

        Assert.Empty(sink.Segments);
        Assert.Equal(0, segmenter.SegmentsEmitted);
    }

    [Fact]
    public void A_sound_is_emitted_once_the_gate_closes_behind_it()
    {
        (SoundEventSegmenter segmenter, Sink sink, Clock clock) = Build();

        Feed(segmenter, sink, clock, Loud(), 30);

        // Nothing yet: the gate is still open, so the clip is not finished.
        Assert.Empty(sink.Segments);

        // Hangover is 1.5s by default — comfortably more than 60 windows (~1.9s).
        Feed(segmenter, sink, clock, Silence(), 60);

        Assert.Single(sink.Segments);
        Assert.Equal(1, segmenter.SegmentsEmitted);
    }

    [Fact]
    public void The_clip_includes_pre_roll_from_before_the_sound_started()
    {
        // The onset is the most identifying part of a transient. If the clip started on the window
        // the gate opened on, the model would be shown the bark from halfway through.
        (SoundEventSegmenter segmenter, Sink sink, Clock clock) = Build();

        // Fill the pre-roll ring with silence the gate is discarding.
        Feed(segmenter, sink, clock, Silence(), 40);
        Feed(segmenter, sink, clock, Loud(), 10);
        Feed(segmenter, sink, clock, Silence(), 60);

        SoundSegment segment = Assert.Single(sink.Segments);

        // 10 loud windows alone would be well under a second. The pre-roll (16 windows) and the
        // hangover both landed in the clip, so it is far longer than the sound itself.
        int loudSamples = 10 * WindowSize;
        Assert.True(
            segment.Samples.Length > loudSamples + (16 * WindowSize),
            $"Expected pre-roll and hangover in the clip; got {segment.Samples.Length} samples.");
    }

    [Fact]
    public void A_blip_shorter_than_the_minimum_is_discarded()
    {
        // MinSegmentSeconds is a floor against single clicks. Set the gate's padding to nothing so
        // the blip is not padded past the floor by pre-roll and hangover.
        SoundOptions options = Options(o =>
        {
            o.MinSegmentSeconds = 1.0;
            o.Gate.PreRollWindows = 0;
            o.Gate.HangoverSeconds = 0;
        });

        (SoundEventSegmenter segmenter, Sink sink, Clock clock) = Build(options);

        // Two windows ≈ 64 ms, far under the 1s floor.
        Feed(segmenter, sink, clock, Loud(), 2);
        Feed(segmenter, sink, clock, Silence(), 40);

        Assert.Empty(sink.Segments);
        Assert.Equal(1, segmenter.SegmentsTooShort);
        Assert.Equal(0, segmenter.SegmentsEmitted);
    }

    [Fact]
    public void A_sound_that_never_stops_is_cut_at_the_maximum_and_keeps_going()
    {
        // Traffic, rain, a mower: the gate never closes. Without the hard cut nothing would ever be
        // classified and the buffer would grow until the process died.
        SoundOptions options = Options(o => o.MaxSegmentSeconds = 1.0);
        (SoundEventSegmenter segmenter, Sink sink, Clock clock) = Build(options);

        // ~6.4s of continuous sound against a 1s cap.
        Feed(segmenter, sink, clock, Loud(), 200);

        Assert.True(sink.Segments.Count >= 5, $"Expected repeated cuts; got {sink.Segments.Count}.");

        foreach (SoundSegment segment in sink.Segments)
        {
            Assert.True(
                segment.DurationSeconds <= 1.1,
                $"Segment of {segment.DurationSeconds:F2}s exceeded the 1s cap.");
        }
    }

    [Fact]
    public void The_timestamp_is_the_start_of_the_sound_not_the_moment_it_was_cut()
    {
        // The clip ends when hangover expires, over a second after anything happened. Timestamping
        // that instant would put every sound record consistently late by the hangover.
        (SoundEventSegmenter segmenter, Sink sink, Clock clock) = Build();

        DateTimeOffset started = clock.Now;

        Feed(segmenter, sink, clock, Loud(), 30);
        Feed(segmenter, sink, clock, Silence(), 60);

        SoundSegment segment = Assert.Single(sink.Segments);

        // The clip's start is where the pre-roll began, which is at or before the first loud
        // window, and well before the moment the gate finally closed.
        Assert.True(
            segment.CapturedAt <= started + TimeSpan.FromSeconds(0.1),
            $"Expected a timestamp at the start of the sound; got {segment.CapturedAt - started} after it.");

        Assert.True(segment.CapturedAt < clock.Now - TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Two_separate_sounds_produce_two_segments()
    {
        (SoundEventSegmenter segmenter, Sink sink, Clock clock) = Build();

        // Silence long enough on both sides to saturate the pre-roll ring, so the two clips are
        // built from the same ingredients and can be compared directly. Without the leading run the
        // first sound would start with an empty ring and be legitimately shorter — a real
        // difference, but not the one this test is about. The default hangover is ~47 windows, so
        // 80 leaves well over the 16 the ring holds.
        Feed(segmenter, sink, clock, Silence(), 40);
        Feed(segmenter, sink, clock, Loud(), 30);
        Feed(segmenter, sink, clock, Silence(), 80);
        Feed(segmenter, sink, clock, Loud(), 30);
        Feed(segmenter, sink, clock, Silence(), 80);

        Assert.Equal(2, sink.Segments.Count);

        // The second clip does not carry the first: a stale accumulator would make every segment
        // longer than the last.
        Assert.Equal(
            sink.Segments[0].Samples.Length, sink.Segments[1].Samples.Length);
    }

    [Fact]
    public void The_sample_rate_travels_with_the_segment()
    {
        (SoundEventSegmenter segmenter, Sink sink, Clock clock) = Build();

        Feed(segmenter, sink, clock, Loud(), 30);
        Feed(segmenter, sink, clock, Silence(), 60);

        Assert.Equal(SampleRate, Assert.Single(sink.Segments).SampleRate);
    }
}
