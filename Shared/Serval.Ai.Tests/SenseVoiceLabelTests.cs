using Serval.Ai;
using Serval.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Serval.Ai.Tests;

public class SenseVoiceLabelTests
{
    private static string? Normalize(string? tag, HashSet<string>? known, string kind = "test")
        => SenseVoiceAnalyzer.NormalizeTag(tag, known, kind, NullLogger.Instance);

    [Fact]
    public void Well_formed_known_tag_is_unwrapped_and_lowercased()
    {
        Assert.Equal("happy", Normalize("<|HAPPY|>", SenseVoiceAnalyzer.KnownEmotions));
    }

    [Fact]
    public void Unknown_label_is_treated_as_undetermined()
    {
        // A label outside the model's own vocabulary means the native struct layout has shifted;
        // the no-fabricated-data rule says surface null, not a plausible-looking wrong answer.
        Assert.Null(Normalize("<|CANTONESE_ONLY|>", SenseVoiceAnalyzer.KnownEmotions));
    }

    [Fact]
    public void A_bare_label_not_in_tag_form_is_rejected()
    {
        Assert.Null(Normalize("HAPPY", SenseVoiceAnalyzer.KnownEmotions));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Null_or_blank_is_undetermined(string? tag)
    {
        Assert.Null(Normalize(tag, SenseVoiceAnalyzer.KnownEmotions));
    }

    [Fact]
    public void With_no_known_set_any_well_formed_tag_passes()
    {
        // Language has no fixed vocabulary to validate against, so a well-formed tag is accepted.
        Assert.Equal("en", Normalize("<|en|>", known: null, kind: "language"));
    }

    [Fact]
    public void Known_events_validate_against_the_real_vocabulary()
    {
        Assert.Equal("laughter", Normalize("<|Laughter|>", SenseVoiceAnalyzer.KnownEvents));
        Assert.Null(Normalize("<|Explosion|>", SenseVoiceAnalyzer.KnownEvents));
    }
}
