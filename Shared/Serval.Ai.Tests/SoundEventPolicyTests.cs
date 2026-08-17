using Serval.Ai;

namespace Serval.Ai.Tests;

/// <summary>
/// Pins every decision that shapes what a sound record says. The tagger itself is a few lines of
/// model configuration; this is where the judgement lives, and all of it is testable with a list of
/// strings and a clock the test controls.
/// </summary>
public class SoundEventPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private static SoundOptions Options(Action<SoundOptions>? configure = null)
    {
        var options = new SoundOptions();
        configure?.Invoke(options);
        return options;
    }

    private static ScoredSound[] Shortlist(params (string Label, float Confidence)[] entries) =>
        [.. entries.Select(e => new ScoredSound(e.Label, e.Confidence))];

    [Fact]
    public void Speech_is_not_special_cased_by_default()
    {
        // Sound and speech are detected independently over the same audio and are expected to
        // overlap. A conversation producing both an utterance and a "Speech" sound record is the
        // designed behaviour, not a leak.
        var policy = new SoundEventPolicy(Options());

        SoundVerdict? verdict = policy.Decide(
            Shortlist(("Speech", 0.9f), ("Glass", 0.7f)), T0);

        Assert.NotNull(verdict);
        Assert.Equal("Speech", verdict.Label);
        Assert.Equal(0, policy.SuppressedByLabel);
    }

    [Fact]
    public void An_ignored_label_drops_from_the_shortlist_rather_than_rejecting_the_segment()
    {
        // The distinction that matters: with "Speech" ignored, glass breaking behind a conversation
        // must still be reported. Rejecting the segment on its top label would lose exactly the
        // case worth catching.
        var policy = new SoundEventPolicy(Options(o => o.IgnoredLabels = ["Speech"]));

        SoundVerdict? verdict = policy.Decide(
            Shortlist(("Speech", 0.9f), ("Glass", 0.7f)), T0);

        Assert.NotNull(verdict);
        Assert.Equal("Glass", verdict.Label);
        Assert.Equal(1, policy.SuppressedByLabel);
        Assert.DoesNotContain(verdict.Alternates, a => a.Label == "Speech");
    }

    [Fact]
    public void A_shortlist_of_nothing_but_ignored_labels_publishes_nothing()
    {
        var policy = new SoundEventPolicy(
            Options(o => o.IgnoredLabels = ["Speech", "Conversation"]));

        Assert.Null(policy.Decide(Shortlist(("Speech", 0.9f), ("Conversation", 0.6f)), T0));
    }

    [Fact]
    public void An_empty_shortlist_publishes_nothing()
    {
        // The tagger returns an empty list when inference failed, rather than throwing.
        var policy = new SoundEventPolicy(Options());

        Assert.Null(policy.Decide([], T0));
    }

    [Fact]
    public void Below_the_ordinary_floor_publishes_nothing()
    {
        var policy = new SoundEventPolicy(Options(o => o.MinConfidence = 0.35f));

        Assert.Null(policy.Decide(Shortlist(("Dog", 0.2f)), T0));
        Assert.Equal(1, policy.BelowThreshold);
    }

    [Fact]
    public void An_alert_between_the_two_floors_publishes_nothing()
    {
        // The whole reason alerts carry their own threshold. 0.5 clears the ordinary floor and
        // would publish a "Dog"; for "Gunshot, gunfire" it is not nearly enough.
        var policy = new SoundEventPolicy(Options(o =>
        {
            o.MinConfidence = 0.35f;
            o.AlertMinConfidence = 0.60f;
            o.AlertLabels = ["Gunshot, gunfire"];
        }));

        Assert.Null(policy.Decide(Shortlist(("Gunshot, gunfire", 0.5f)), T0));
        Assert.Equal(1, policy.BelowThreshold);

        // The same confidence on an ordinary label does publish.
        Assert.NotNull(policy.Decide(Shortlist(("Dog", 0.5f)), T0));
    }

    [Fact]
    public void Alert_labels_are_matched_exactly_as_the_model_spells_them()
    {
        var policy = new SoundEventPolicy(Options(o => o.AlertLabels = ["Gunshot, gunfire"]));

        SoundVerdict? alert = policy.Decide(Shortlist(("Gunshot, gunfire", 0.9f)), T0);
        Assert.NotNull(alert);
        Assert.True(alert.IsAlert);

        // A near-miss is not an alert. AudioSet labels are fixed strings; guessing at them is how
        // an alert list silently stops matching anything.
        SoundVerdict? notAlert = policy.Decide(Shortlist(("Gunshot", 0.9f)), T0);
        Assert.NotNull(notAlert);
        Assert.False(notAlert.IsAlert);
    }

    [Fact]
    public void The_default_alert_list_catches_a_siren_by_its_general_class()
    {
        // Measured, not guessed. Against the model's own test clips two sirens came back as
        // "Siren" at 0.88 and 0.98, with the specific "Civil defense siren" second at 0.74 and
        // 0.82. AudioSet is hierarchical and the parent usually wins, so an alert list naming only
        // the child let both sirens through silently. Pinned so the general class cannot be
        // "tidied" back out of the defaults.
        var policy = new SoundEventPolicy(Options());

        SoundVerdict? verdict = policy.Decide(
            Shortlist(("Siren", 0.879f), ("Civil defense siren", 0.740f), ("Vehicle", 0.011f)), T0);

        Assert.NotNull(verdict);
        Assert.Equal("Siren", verdict.Label);
        Assert.True(verdict.IsAlert);
    }

    [Fact]
    public void The_same_label_is_held_off_for_the_cooldown()
    {
        var policy = new SoundEventPolicy(Options(o => o.CooldownSeconds = 60));

        Assert.NotNull(policy.Decide(Shortlist(("Dog", 0.9f)), T0));
        Assert.Null(policy.Decide(Shortlist(("Dog", 0.9f)), T0 + TimeSpan.FromSeconds(30)));
        Assert.Equal(1, policy.SuppressedByCooldown);

        Assert.NotNull(policy.Decide(Shortlist(("Dog", 0.9f)), T0 + TimeSpan.FromSeconds(61)));
    }

    [Fact]
    public void The_cooldown_is_per_label_so_two_sounds_never_silence_each_other()
    {
        var policy = new SoundEventPolicy(Options(o => o.CooldownSeconds = 60));

        Assert.NotNull(policy.Decide(Shortlist(("Dog", 0.9f)), T0));

        // A siren a second later is a different event and must not be swallowed by the dog.
        SoundVerdict? siren = policy.Decide(
            Shortlist(("Siren", 0.9f)), T0 + TimeSpan.FromSeconds(1));

        Assert.NotNull(siren);
        Assert.Equal("Siren", siren.Label);
        Assert.Equal(0, policy.SuppressedByCooldown);
    }

    [Fact]
    public void Alerts_use_the_shorter_cooldown()
    {
        // A repeated alarm is information; a repeated dog is not.
        var policy = new SoundEventPolicy(Options(o =>
        {
            o.CooldownSeconds = 60;
            o.AlertCooldownSeconds = 15;
            o.AlertLabels = ["Fire alarm"];
        }));

        Assert.NotNull(policy.Decide(Shortlist(("Fire alarm", 0.9f)), T0));
        Assert.NotNull(policy.Decide(Shortlist(("Fire alarm", 0.9f)), T0 + TimeSpan.FromSeconds(20)));

        // An ordinary label at the same spacing is still held off.
        Assert.NotNull(policy.Decide(Shortlist(("Dog", 0.9f)), T0));
        Assert.Null(policy.Decide(Shortlist(("Dog", 0.9f)), T0 + TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void A_segment_suppressed_by_cooldown_does_not_restart_the_cooldown()
    {
        // Otherwise continuous traffic would extend its own silence indefinitely and the label
        // would never be published again.
        var policy = new SoundEventPolicy(Options(o => o.CooldownSeconds = 60));

        Assert.NotNull(policy.Decide(Shortlist(("Dog", 0.9f)), T0));
        Assert.Null(policy.Decide(Shortlist(("Dog", 0.9f)), T0 + TimeSpan.FromSeconds(50)));
        Assert.NotNull(policy.Decide(Shortlist(("Dog", 0.9f)), T0 + TimeSpan.FromSeconds(61)));
    }

    [Fact]
    public void The_winner_is_the_highest_confidence_regardless_of_shortlist_order()
    {
        // The tagger returns best-first, but not depending on that means a future model that does
        // not can be swapped in without a silent misclassification.
        var policy = new SoundEventPolicy(Options());

        SoundVerdict? verdict = policy.Decide(
            Shortlist(("Dog", 0.4f), ("Siren", 0.8f), ("Cat", 0.5f)), T0);

        Assert.NotNull(verdict);
        Assert.Equal("Siren", verdict.Label);
        Assert.Equal(0.8f, verdict.Confidence);
    }

    [Fact]
    public void Alternates_carry_the_whole_surviving_shortlist_winner_first()
    {
        // Stored so a threshold can be re-derived later from real recordings rather than guessed
        // at again — which means the winner has to be in there too, not just the runners-up.
        var policy = new SoundEventPolicy(Options());

        SoundVerdict? verdict = policy.Decide(
            Shortlist(("Siren", 0.8f), ("Dog", 0.4f)), T0);

        Assert.NotNull(verdict);
        Assert.Equal(2, verdict.Alternates.Count);
        Assert.Equal("Siren", verdict.Alternates[0].Label);
        Assert.Equal("Dog", verdict.Alternates[1].Label);
    }
}
