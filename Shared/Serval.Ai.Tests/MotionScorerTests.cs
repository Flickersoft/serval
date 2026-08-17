using Serval.Ai;

namespace Serval.Ai.Tests;

/// <summary>
/// Pins the motion gate's decisions. This is the code that decides whether the vision model runs
/// at all, so its failure modes are the expensive ones: a gate stuck open means continuous
/// inference on a still scene, and a gate stuck shut means a camera that silently sees nothing.
/// </summary>
public class MotionScorerTests
{
    private const int Width = 64;
    private const int Height = 48;

    private static MotionOptions Options() => new();

    private static byte[] Flat(byte value)
    {
        var frame = new byte[Width * Height];
        Array.Fill(frame, value);
        return frame;
    }

    /// <summary>Draws a filled rectangle, the stand-in for something being somewhere.</summary>
    private static byte[] WithBlock(byte background, byte block, int x, int y, int w, int h)
    {
        byte[] frame = Flat(background);
        for (int row = y; row < y + h; row++)
        {
            for (int column = x; column < x + w; column++)
            {
                frame[(row * Width) + column] = block;
            }
        }

        return frame;
    }

    [Fact]
    public void Identical_frames_report_no_motion()
    {
        byte[] frame = WithBlock(background: 40, block: 200, x: 10, y: 10, w: 8, h: 8);

        MotionResult result = MotionScorer.Compare(frame, frame, Options());

        Assert.False(result.Moved);
        Assert.False(result.Rejected);
        Assert.Equal(0, result.ChangedFraction);
    }

    [Fact]
    public void A_block_that_moves_reports_motion()
    {
        // 2 x (8x8) blocks changed out of 3072 px = ~4.2%, comfortably over the 2% default.
        byte[] before = WithBlock(background: 40, block: 200, x: 10, y: 10, w: 8, h: 8);
        byte[] after = WithBlock(background: 40, block: 200, x: 30, y: 10, w: 8, h: 8);

        MotionResult result = MotionScorer.Compare(before, after, Options());

        Assert.True(result.Moved);
        Assert.False(result.Rejected);
        Assert.True(result.ChangedFraction > 0.02, $"changed fraction was {result.ChangedFraction}");
    }

    [Fact]
    public void A_tiny_change_stays_below_the_threshold()
    {
        // A single 4x4 block is 0.5% of the frame — a bird, a leaf, or compression noise, and not
        // worth waking a model that costs seconds of CPU.
        byte[] before = Flat(40);
        byte[] after = WithBlock(background: 40, block: 200, x: 2, y: 2, w: 4, h: 4);

        MotionResult result = MotionScorer.Compare(before, after, Options());

        Assert.False(result.Moved);
        Assert.False(result.Rejected);
    }

    [Fact]
    public void Noise_under_the_pixel_delta_is_not_motion()
    {
        // Every pixel changes, but only slightly: sensor noise and JPEG ringing, not movement.
        // Without the per-pixel delta this would look like the whole scene had changed.
        byte[] before = Flat(100);
        byte[] after = Flat(115); // 15 < PixelDelta 25

        MotionResult result = MotionScorer.Compare(before, after, Options());

        Assert.False(result.Moved);
        Assert.False(result.Rejected);
        Assert.Equal(0, result.ChangedFraction);
    }

    [Fact]
    public void A_whole_frame_brightness_shift_is_rejected_rather_than_reported_as_motion()
    {
        // The IR-cut filter flipping to night mode, or a light being switched on. Every pixel
        // changes a lot, and calling that "movement" would fire a description at dusk every day.
        byte[] before = Flat(30);
        byte[] after = Flat(220);

        MotionResult result = MotionScorer.Compare(before, after, Options());

        Assert.True(result.Rejected);
        Assert.False(result.Moved);
        Assert.Equal(1.0, result.ChangedFraction);
    }

    [Fact]
    public void Large_negative_differences_are_not_wrapped_into_small_positive_ones()
    {
        // Guards the widen-before-subtract: in byte arithmetic 10 - 200 wraps to 66, which would
        // read as a modest change when the pixel actually went from near-black to near-white.
        byte[] before = Flat(10);
        byte[] after = Flat(200);

        MotionResult result = MotionScorer.Compare(before, after, Options());

        Assert.Equal(1.0, result.ChangedFraction);
    }

    [Fact]
    public void The_changed_fraction_is_reported_even_when_no_motion_is_declared()
    {
        // A gate that only ever says "no" cannot be tuned. The number is what tells an operator
        // whether a threshold is nearly right or wildly off.
        byte[] before = Flat(40);
        byte[] after = WithBlock(background: 40, block: 200, x: 2, y: 2, w: 4, h: 4);

        MotionResult result = MotionScorer.Compare(before, after, Options());

        Assert.False(result.Moved);
        Assert.Equal(16.0 / (Width * Height), result.ChangedFraction, precision: 6);
    }

    [Fact]
    public void Thresholds_are_configurable_in_both_directions()
    {
        byte[] before = Flat(40);
        byte[] after = WithBlock(background: 40, block: 200, x: 2, y: 2, w: 4, h: 4);

        var sensitive = new MotionOptions { MinChangedFraction = 0.001 };

        Assert.True(MotionScorer.Compare(before, after, sensitive).Moved);
        Assert.False(MotionScorer.Compare(before, after, Options()).Moved);
    }

    [Fact]
    public void Mismatched_frame_sizes_are_rejected()
    {
        // Comparing different geometries would silently produce nonsense, so it must throw rather
        // than return a number a caller would believe.
        Assert.Throws<ArgumentException>(() =>
            MotionScorer.Compare(new byte[10], new byte[20], Options()));
    }

    [Fact]
    public void Empty_frames_are_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            MotionScorer.Compare([], [], Options()));
    }
}
