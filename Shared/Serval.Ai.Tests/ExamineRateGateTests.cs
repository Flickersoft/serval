namespace Serval.Ai.Tests;

/// <summary>
/// The per-camera ceiling on how often a frame is looked at.
///
/// The failure worth guarding reads like the opposite of itself: setting the ceiling to the rate
/// frames arrive at looks like "examine everything", and without the gate's tolerance it means
/// "examine about two thirds of everything", with nothing anywhere reporting the difference.
/// </summary>
public class ExamineRateGateTests
{
    private static readonly DateTimeOffset Session =
        new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Frame times exactly as the ingest path produces them: a session anchor plus an integer frame
    /// index over the detect rate. Reproducing that arithmetic is the whole point — the rounding it
    /// carries is what the gate has to survive.
    /// </summary>
    private static DateTimeOffset FrameAt(int index, double fps) =>
        Session.AddSeconds(index / fps);

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(5.0)]
    [InlineData(10.0)]
    public void A_ceiling_matched_to_the_frame_rate_examines_every_frame(double fps)
    {
        // The regression this type exists for. Measured on a real camera at 5 fps with a matched
        // ceiling: 3.32 frames a second examined where removing the ceiling gave 4.84.
        var gate = new ExamineRateGate(fps);
        int admitted = 0;

        for (int i = 0; i < 200; i++)
        {
            if (gate.Admit(FrameAt(i, fps)))
            {
                admitted++;
            }
        }

        Assert.Equal(200, admitted);
        Assert.Equal(0, gate.Skipped);
    }

    [Theory]
    [InlineData(2.0, 1.0, 100)]    // half the rate: every other frame
    [InlineData(4.0, 1.0, 50)]     // a quarter
    [InlineData(10.0, 2.0, 40)]    // a fifth
    public void A_lower_ceiling_thins_to_about_its_own_rate(
        double fps, double maxFps, int expected)
    {
        // The setting doing its actual job, which is making one camera cheaper than the rest.
        var gate = new ExamineRateGate(maxFps);
        int admitted = 0;

        for (int i = 0; i < 200; i++)
        {
            if (gate.Admit(FrameAt(i, fps)))
            {
                admitted++;
            }
        }

        // Within one frame either way: which frame lands first depends on where the run starts.
        Assert.InRange(admitted, expected - 1, expected + 1);
    }

    [Fact]
    public void No_ceiling_admits_everything()
    {
        var gate = new ExamineRateGate(0);

        Assert.False(gate.Limits);
        Assert.All(
            Enumerable.Range(0, 50),
            i => Assert.True(gate.Admit(FrameAt(i, 5.0))));
        Assert.Equal(0, gate.Skipped);
    }

    [Fact]
    public void A_session_restart_does_not_stall_the_gate()
    {
        // A new session anchors its own clock, so its first frames are dated *earlier* than the last
        // one admitted from the session before. Read as frames arriving early, the gate would refuse
        // everything until the new session caught up to an instant that may be hours away — which on
        // a camera that has been up for a day is a gate that never opens again.
        var gate = new ExamineRateGate(2.0);

        for (int i = 0; i < 20; i++)
        {
            gate.Admit(Session.AddHours(3).AddSeconds(i / 2.0));
        }

        Assert.True(gate.Admit(Session), "the restarted session's first frame must be examined");
        Assert.True(gate.Admit(Session.AddSeconds(0.5)));
    }

    [Fact]
    public void Frames_genuinely_inside_the_period_are_still_turned_away()
    {
        // The tolerance absorbs tick-level rounding and nothing more. A frame at half the period is
        // early by any reading and must not slip through, or the ceiling stops being one.
        var gate = new ExamineRateGate(2.0);

        Assert.True(gate.Admit(Session));
        Assert.False(gate.Admit(Session.AddSeconds(0.25)));
        Assert.False(gate.Admit(Session.AddSeconds(0.4)));
        Assert.True(gate.Admit(Session.AddSeconds(0.5)));
        Assert.Equal(2, gate.Skipped);
    }
}
