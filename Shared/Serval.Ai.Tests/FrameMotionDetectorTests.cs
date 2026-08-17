namespace Serval.Ai.Tests;

/// <summary>
/// Scoring motion straight off a raw frame's luma plane.
///
/// This replaces a decode-and-resize through an image library with a box average over a span, which
/// is the reason frames are carried as planar YUV rather than RGB. The behaviour it must keep is the
/// JPEG detector's: no motion on the first frame, a whole-frame change rejected rather than reported,
/// and a reference that advances even when the comparison was rejected.
/// </summary>
public class FrameMotionDetectorTests
{
    private const int Width = 128;
    private const int Height = 96;

    private static MotionOptions Options() => new()
    {
        CompareWidth = 32,
        CompareHeight = 24,
        PixelDelta = 25,
        MinChangedFraction = 0.02,
        MaxChangedFraction = 0.5,
    };

    private static byte[] Luma(byte value)
    {
        var plane = new byte[Width * Height];
        plane.AsSpan().Fill(value);
        return plane;
    }

    /// <summary>A flat plane with one bright rectangle, in frame pixels.</summary>
    private static byte[] LumaWith(byte background, int x, int y, int width, int height, byte value)
    {
        byte[] plane = Luma(background);

        for (int row = y; row < y + height; row++)
        {
            plane.AsSpan((row * Width) + x, width).Fill(value);
        }

        return plane;
    }

    [Fact]
    public void The_first_frame_reports_no_motion()
    {
        // With nothing to compare against there is no evidence of change. Treating startup as motion
        // would mean every camera proposes regions over its whole frame on every restart.
        var detector = new FrameMotionDetector(Options());

        FrameMotion motion = detector.Accept(Luma(80), Width, Height);

        Assert.False(motion.Result.Moved);
        Assert.False(motion.Result.Rejected);
        Assert.DoesNotContain((byte)1, motion.ChangedCells.ToArray());
    }

    [Fact]
    public void A_still_scene_reports_no_motion()
    {
        var detector = new FrameMotionDetector(Options());
        detector.Accept(Luma(80), Width, Height);

        Assert.False(detector.Accept(Luma(80), Width, Height).Result.Moved);
    }

    [Fact]
    public void Something_moving_is_reported_where_it_moved()
    {
        var detector = new FrameMotionDetector(Options());
        detector.Accept(Luma(40), Width, Height);

        FrameMotion motion = detector.Accept(
            LumaWith(40, x: 32, y: 24, width: 32, height: 24, value: 220), Width, Height);

        Assert.True(motion.Result.Moved);

        // The changed cells sit where the bright block is — a quarter across and a quarter down the
        // 32x24 compare grid — and nowhere else. That location is what a region is cut around.
        ReadOnlySpan<byte> cells = motion.ChangedCells;
        Assert.Equal(1, cells[(6 * 32) + 8]);
        Assert.Equal(0, cells[(2 * 32) + 2]);
    }

    [Fact]
    public void A_whole_frame_change_is_rejected_rather_than_called_motion()
    {
        // Night mode switching on changes every pixel. Describing that as movement is worse than
        // saying nothing, because it happens at dusk every single day.
        var detector = new FrameMotionDetector(Options());
        detector.Accept(Luma(20), Width, Height);

        FrameMotion motion = detector.Accept(Luma(220), Width, Height);

        Assert.True(motion.Result.Rejected);
        Assert.False(motion.Result.Moved);
    }

    [Fact]
    public void The_reference_advances_through_a_rejected_frame()
    {
        // Keeping the pre-flip frame as the reference would make every subsequent frame look wildly
        // different and jam the comparison for as long as the new lighting lasted.
        var detector = new FrameMotionDetector(Options());
        detector.Accept(Luma(20), Width, Height);
        detector.Accept(Luma(220), Width, Height);

        FrameMotion after = detector.Accept(Luma(220), Width, Height);

        Assert.False(after.Result.Rejected);
        Assert.False(after.Result.Moved);
    }

    [Fact]
    public void The_score_is_reported_even_when_it_is_not_motion()
    {
        // A gate that only ever says no is impossible to tune; the number is what tells you whether
        // a threshold is nearly right or wildly off.
        var detector = new FrameMotionDetector(Options());
        detector.Accept(Luma(40), Width, Height);

        FrameMotion motion = detector.Accept(
            LumaWith(40, x: 0, y: 0, width: 8, height: 4, value: 220), Width, Height);

        Assert.False(motion.Result.Moved);
        Assert.True(motion.Result.ChangedFraction > 0);
    }

    [Fact]
    public void The_downscale_averages_rather_than_samples()
    {
        // Point-sampling would let a single noisy pixel stand for a whole cell, which is how sensor
        // noise turns into false motion and a crop over nothing.
        var detector = new FrameMotionDetector(Options());
        detector.Accept(Luma(100), Width, Height);

        // One pixel changed, hard, inside a cell of sixteen. Averaged it moves the cell by ~10,
        // under the delta; sampled it could move it by 155.
        byte[] speck = Luma(100);
        speck[(50 * Width) + 50] = 255;

        Assert.False(detector.Accept(speck, Width, Height).Result.Moved);
    }

    [Fact]
    public void A_short_luma_plane_is_refused()
    {
        var detector = new FrameMotionDetector(Options());

        Assert.Throws<ArgumentException>(
            () => detector.Accept(new byte[10], Width, Height));
    }
}
