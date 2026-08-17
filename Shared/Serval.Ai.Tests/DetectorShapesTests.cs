using Serval.Ai;

namespace Serval.Ai.Tests;

/// <summary>
/// The shape ladder, which decides what every camera's frames are fitted into.
///
/// Worth testing exhaustively for the same reason <see cref="FramePreparer"/> is: it is pure, it
/// sits between every camera and every detector, and getting it wrong costs picture in a way nothing
/// reports — a camera handed a badly matched shape still detects, just less well.
/// </summary>
public class DetectorShapesTests
{
    private const int Budget = 640 * 384;

    [Theory]
    // The four aspects that exist on real hardware, and what each should land on.
    [InlineData(640, 360, 640, 384)]     // 16:9, the commonest sub stream
    [InlineData(1920, 1080, 640, 384)]   // the same aspect at a different size
    [InlineData(1536, 432, 960, 256)]    // 32:9 stitched panoramic
    [InlineData(480, 640, 416, 576)]     // 3:4 doorbell
    [InlineData(640, 480, 576, 416)]     // 4:3
    // A square frame is exactly as far from 480x512 as from 512x480. The tie goes to the frame's own
    // orientation, and a square counts as landscape, so it must not come back on its side.
    [InlineData(512, 512, 512, 480)]
    public void A_frame_lands_on_the_shape_nearest_its_aspect(
        int frameWidth, int frameHeight, int expectedWidth, int expectedHeight)
    {
        Assert.Equal(
            (expectedWidth, expectedHeight),
            DetectorShapes.Fit(frameWidth, frameHeight, Budget));
    }

    [Fact]
    public void Every_shape_is_stride_aligned_and_close_to_the_budget()
    {
        // Both properties are load-bearing and neither fails loudly. An axis off the stride makes a
        // backbone lose the remainder silently, and a shape well off the budget breaks the one thing
        // that lets InferenceBudget stay a single number for every camera.
        foreach ((int width, int height) in DetectorShapes.Ladder(Budget))
        {
            Assert.Equal(0, width % DetectorShapes.Stride);
            Assert.Equal(0, height % DetectorShapes.Stride);

            double area = (double)width * height;
            Assert.InRange(area / Budget, 0.90, 1.10);
        }
    }

    [Fact]
    public void The_ladder_spans_the_aspects_real_cameras_have()
    {
        IReadOnlyList<(int Width, int Height)> ladder = DetectorShapes.Ladder(Budget);

        double widest = ladder.Max(s => (double)s.Width / s.Height);
        double tallest = ladder.Min(s => (double)s.Width / s.Height);

        // 32:9 panoramics are sold hardware, and a 9:16 doorbell is the other end of the same
        // problem. A ladder that stops short of either silently hands those cameras a bad shape.
        Assert.True(widest >= 3.55, $"widest shape is {widest:0.00}, short of a 32:9 panoramic");
        Assert.True(tallest <= 0.5, $"tallest shape is {tallest:0.00}, short of a 9:16 doorbell");
    }

    [Fact]
    public void No_frame_is_fitted_worse_than_the_ladder_allows()
    {
        // The guarantee the whole design rests on: whatever aspect a camera has, the shape it gets
        // spends most of itself on picture. Swept rather than spot-checked, because the gaps between
        // rungs are where a bad fit would hide.
        for (int width = 128; width <= 4096; width += 16)
        {
            foreach (int height in new[] { 96, 240, 360, 432, 640, 1080, 1440 })
            {
                (int inputWidth, int inputHeight) = DetectorShapes.Fit(width, height, Budget);

                double fit = Math.Min(
                    (double)inputWidth / width, (double)inputHeight / height);
                double picture = Math.Round(width * fit) * Math.Round(height * fit)
                    / ((double)inputWidth * inputHeight);

                double aspect = (double)width / height;
                if (aspect is > 16.0 or < 1.0 / 16.0)
                {
                    continue;
                }

                Assert.True(
                    picture >= 0.80,
                    $"{width}x{height} (aspect {aspect:0.00}) fitted into "
                    + $"{inputWidth}x{inputHeight} is only {picture:P0} picture");
            }
        }
    }

    [Fact]
    public void A_smaller_budget_gives_smaller_shapes_at_the_same_aspects()
    {
        // Halving the budget is the operator's lever for a smaller host, and it has to keep working
        // for every camera rather than collapsing the ladder to one square.
        (int width, int height) = DetectorShapes.Fit(1536, 432, Budget / 4);

        Assert.Equal(0, width % DetectorShapes.Stride);
        Assert.Equal(0, height % DetectorShapes.Stride);
        Assert.InRange((double)width / height, 2.5, 5.0);
        Assert.InRange((double)width * height / (Budget / 4), 0.90, 1.10);
    }

    [Theory]
    [InlineData(640, 360)]
    [InlineData(360, 640)]
    [InlineData(1536, 432)]
    [InlineData(432, 1536)]
    [InlineData(512, 512)]
    public void A_shape_never_comes_back_on_its_side(int frameWidth, int frameHeight)
    {
        // The failure this guards against is silent and expensive: a portrait camera handed a
        // landscape shape still detects, at 60% scale and 45% picture, and nothing anywhere reports
        // a fault. Orientation is the one property worth asserting on every aspect.
        (int width, int height) = DetectorShapes.Fit(frameWidth, frameHeight, Budget);

        Assert.Equal(frameWidth >= frameHeight, width >= height);
    }

    [Fact]
    public void A_frame_of_no_size_resolves_to_something_usable()
    {
        // A caller with nothing to say still needs a buffer it can allocate, and a zero here would
        // divide by zero deep inside the preparer instead.
        (int width, int height) = DetectorShapes.Fit(0, 0, Budget);

        Assert.True(width >= DetectorShapes.Stride);
        Assert.True(height >= DetectorShapes.Stride);
    }
}
