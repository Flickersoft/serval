namespace Serval.Ai;

/// <summary>One AudioSet class the model scored. <paramref name="Label"/> is the model's own
/// string, verbatim.</summary>
public readonly record struct ScoredSound(string Label, float Confidence);

/// <summary>What the policy decided to publish.</summary>
public sealed record SoundVerdict(
    string Label,
    float Confidence,
    IReadOnlyList<ScoredSound> Alternates,
    bool IsAlert);

/// <summary>
/// Everything between "the model scored a shortlist" and "publish this, or nothing".
///
/// Pure, and separated from <see cref="SoundEventTagger"/>-shaped code on purpose: the tagger is a
/// few lines of model configuration that cannot be tested without 26 MB of weights, while every
/// decision that actually shapes what lands in telemetry lives here and is testable with a list of
/// strings and a fake clock.
///
/// One instance per camera — the cooldown state is per-camera by definition.
/// </summary>
public sealed class SoundEventPolicy
{
    private readonly SoundOptions _options;
    private readonly HashSet<string> _ignored;
    private readonly HashSet<string> _alerts;
    private readonly Dictionary<string, DateTimeOffset> _lastPublished = new(StringComparer.Ordinal);

    public SoundEventPolicy(SoundOptions options)
    {
        _options = options;

        // Ordinal: these are matched against the model's own label strings, which are fixed ASCII
        // from its class list. Culture-sensitive comparison here would be a way to be surprised.
        _ignored = new HashSet<string>(options.IgnoredLabels, StringComparer.Ordinal);
        _alerts = new HashSet<string>(options.EffectiveAlertLabels, StringComparer.Ordinal);
    }

    /// <summary>Shortlist entries dropped by <see cref="SoundOptions.IgnoredLabels"/>. Zero unless configured.</summary>
    public long SuppressedByLabel { get; private set; }

    /// <summary>Segments whose best surviving label was under its confidence floor.</summary>
    public long BelowThreshold { get; private set; }

    /// <summary>Segments dropped because the same label was published too recently.</summary>
    public long SuppressedByCooldown { get; private set; }

    /// <summary>Segments that produced a record.</summary>
    public long Published { get; private set; }

    /// <summary>
    /// Decides what to publish for one classified segment, or null to publish nothing.
    /// </summary>
    /// <param name="now">Injected so the cooldown is testable without sleeping.</param>
    public SoundVerdict? Decide(IReadOnlyList<ScoredSound> shortlist, DateTimeOffset now)
    {
        if (shortlist.Count == 0)
        {
            return null;
        }

        // Filter the shortlist rather than rejecting the segment. The difference matters: with
        // "Speech" ignored, a segment scored [Speech 0.9, Glass 0.7] should still report the glass.
        // Rejecting on the top label would lose exactly the case worth catching.
        List<ScoredSound> candidates = [];
        foreach (ScoredSound scored in shortlist)
        {
            if (_ignored.Contains(scored.Label))
            {
                SuppressedByLabel++;
                continue;
            }

            candidates.Add(scored);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // The tagger returns the shortlist best-first, but sorting here means the policy does not
        // depend on that and a future model that does not can be swapped in.
        candidates.Sort(static (a, b) => b.Confidence.CompareTo(a.Confidence));

        ScoredSound winner = candidates[0];
        bool isAlert = _alerts.Contains(winner.Label);

        float floor = isAlert ? _options.AlertMinConfidence : _options.MinConfidence;
        if (winner.Confidence < floor)
        {
            BelowThreshold++;
            return null;
        }

        double cooldown = isAlert ? _options.AlertCooldownSeconds : _options.CooldownSeconds;
        if (_lastPublished.TryGetValue(winner.Label, out DateTimeOffset last)
            && now - last < TimeSpan.FromSeconds(cooldown))
        {
            SuppressedByCooldown++;
            return null;
        }

        _lastPublished[winner.Label] = now;
        Published++;

        return new SoundVerdict(winner.Label, winner.Confidence, candidates, isAlert);
    }
}
