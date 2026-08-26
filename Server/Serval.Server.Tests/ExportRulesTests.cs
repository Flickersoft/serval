using Serval.Server.Media;

namespace Serval.Server.Tests;

/// <summary>
/// What the streamed export route accepts.
///
/// Separate from <c>ClipRulesTests</c> because the two limits are different kinds of limit and were
/// being confused for one: a saved clip is a file kept until somebody deletes it, so its ceiling is
/// disk, while an export is built as it is sent and kept by nobody. The route also had no check at
/// all until this existed — the cap lived only in the App, so a hand-built request could ask for a
/// week.
/// </summary>
public class ExportRulesTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_range_inside_the_cap_is_accepted()
    {
        Assert.Null(ExportRules.RejectExport(Noon, Noon.AddHours(11), maxMinutes: 720));
    }

    [Fact]
    public void The_cap_itself_is_allowed()
    {
        // Exactly the maximum is legal. An off-by-one here is a range the trimmer offers and the
        // Server then refuses, which reads as a bug in the trimmer.
        Assert.Null(ExportRules.RejectExport(Noon, Noon.AddMinutes(720), maxMinutes: 720));
    }

    [Fact]
    public void A_range_over_the_cap_is_refused_in_the_units_a_person_would_use()
    {
        // Twelve hours reported as 720 minutes reads like a different limit than the one the
        // settings screen offered.
        string? refusal = ExportRules.RejectExport(Noon, Noon.AddHours(13), maxMinutes: 720);

        Assert.NotNull(refusal);
        Assert.Contains("13 hours", refusal);
        Assert.Contains("12 hours", refusal);
    }

    [Fact]
    public void A_short_cap_is_reported_in_minutes()
    {
        string? refusal = ExportRules.RejectExport(Noon, Noon.AddMinutes(31), maxMinutes: 30);

        Assert.NotNull(refusal);
        Assert.Contains("31 minutes", refusal);
        Assert.Contains("30 minutes", refusal);
    }

    [Fact]
    public void A_backwards_or_empty_range_is_refused()
    {
        Assert.NotNull(ExportRules.RejectExport(Noon.AddMinutes(1), Noon, 720));
        Assert.NotNull(ExportRules.RejectExport(Noon, Noon, 720));
    }
}
