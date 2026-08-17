using Serval.Server.Ingest;

namespace Serval.Server.Tests;

/// <summary>
/// The budget arithmetic and the shape of the filter it emits.
///
/// What the filter actually does to a frame is not testable from here — the arithmetic lives in an
/// ffmpeg expression — so <see cref="SnapshotScaleTests"/> runs ffmpeg against real sources and
/// checks the dimensions that come out. These are the cheap half: the conversion, and the fact that
/// zero means no filter at all rather than a filter that scales to nothing.
/// </summary>
public class PixelBudgetTests
{
    [Theory]
    [InlineData(0.25, 250_000)]
    [InlineData(1.0, 1_000_000)]
    [InlineData(0.23, 230_000)]
    [InlineData(4.0, 4_000_000)]
    public void Megapixels_convert_to_pixels(double megapixels, long expected) =>
        Assert.Equal(expected, PixelBudget.Pixels(megapixels));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void A_non_positive_budget_is_no_budget(double megapixels)
    {
        Assert.Equal(0, PixelBudget.Pixels(megapixels));
        Assert.Null(PixelBudget.ScaleFilter(megapixels));
    }

    [Fact]
    public void No_budget_means_no_filter_rather_than_an_empty_one() =>
        Assert.Null(PixelBudget.ScaleFilter(0L));

    [Fact]
    public void The_filter_carries_the_budget_in_pixels()
    {
        string? filter = PixelBudget.ScaleFilter(0.25);

        Assert.NotNull(filter);
        Assert.Contains("250000", filter);
        Assert.StartsWith("scale=", filter);
    }

    /// <summary>
    /// The whole point of an area budget: the same setting produces the same pixel count whatever
    /// shape the camera is, so the filter must never mention a fixed width or height of its own.
    /// </summary>
    [Fact]
    public void The_filter_derives_both_axes_from_the_source()
    {
        string filter = PixelBudget.ScaleFilter(0.25)!;

        Assert.Contains("iw", filter);
        Assert.Contains("ih", filter);
        Assert.Contains("sqrt", filter);
        Assert.Contains("h=-2", filter);
    }
}
