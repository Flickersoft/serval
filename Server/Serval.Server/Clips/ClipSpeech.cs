using Serval.Contracts;

namespace Serval.Server.Clips;

/// <summary>One thing said inside a clip, positioned against the clip rather than the wall clock.</summary>
/// <param name="Timestamp">When it was said.</param>
/// <param name="OffsetSeconds">How far into the clip that is — the only clock a viewer has.</param>
/// <param name="Speaker">Who said it, where that could be established.</param>
/// <param name="Text">What they said.</param>
public sealed record ClipSpeechLine(
    DateTimeOffset Timestamp, double OffsetSeconds, string? Speaker, string Text);

/// <summary>
/// "Said in it" — the frozen speech of a clip, flattened into one ordered list.
///
/// Two sources have to be reconciled here, and the rule matters. A settled conversation transcript
/// is the considered reading and carries speaker attribution; the live utterances it was built from
/// are still stored, unmodified, because a consumer that showed them in realtime should not have
/// them rewritten underneath. Printing both would show every sentence twice — so a conversation
/// that settled speaks through its turns, and its live utterances are dropped.
/// </summary>
public static class ClipSpeech
{
    public static IReadOnlyList<ClipSpeechLine> Of(SavedClip clip)
    {
        var settled = clip.Documents.ConversationTranscripts
            .Where(c => c.Turns.Count > 0)
            .Select(c => c.ConversationId)
            .ToHashSet(StringComparer.Ordinal);

        var lines = new List<ClipSpeechLine>();

        foreach (ConversationTranscriptDocument transcript in clip.Documents.ConversationTranscripts)
        {
            foreach (TranscriptTurn turn in transcript.Turns)
            {
                Add(lines, clip, transcript.StartedAt.AddSeconds(turn.Start), SpeakerLabel(turn.Speaker), turn.Text);
            }
        }

        foreach (UtteranceDocument utterance in clip.Documents.Utterances)
        {
            // A conversation that never settled — every one still open when the clip was taken —
            // has no turns, so its live utterances are all there is.
            if (utterance.ConversationId is { } id && settled.Contains(id))
            {
                continue;
            }

            Add(lines, clip, utterance.Timestamp, utterance.Speaker, utterance.Transcript);
        }

        return [.. lines.OrderBy(line => line.Timestamp)];
    }

    private static void Add(
        List<ClipSpeechLine> lines, SavedClip clip, DateTimeOffset at, string? speaker, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // A turn's time is its conversation's start plus an offset, so a conversation that began
        // before the clip puts its early turns before the clip's first frame. Those were not said
        // in it, whatever their conversation was doing.
        if (at < clip.From || at > clip.To)
        {
            return;
        }

        lines.Add(new ClipSpeechLine(at, (at - clip.From).TotalSeconds, speaker, text.Trim()));
    }

    /// <summary>
    /// Speaker numbers are scoped to their conversation and mean nothing outside it, so they are
    /// shown as an ordinal rather than as an identity. One-based, because "Speaker 0" reads as an
    /// index rather than as a person.
    /// </summary>
    private static string SpeakerLabel(int speaker) => $"Speaker {speaker + 1}";
}
