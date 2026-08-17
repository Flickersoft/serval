using Serval.Ai;

namespace Serval.Ai.Tests;

/// <summary>
/// Choosing which emotion a diarized turn ends up wearing.
///
/// Split out of <c>Attribute</c> and made pure for the same reason the ONVIF parsers are: getting
/// it wrong does not throw, it labels somebody's words with a feeling measured off somebody else's.
/// The whole of the interesting judgement is here, and it is decidable without a diarizer, an ASR
/// model, or a WAV.
///
/// Why this lives on the module side at all: an utterance's timestamp is when the VAD *emitted*
/// it — after the speech plus the trailing silence it waited through — so lining utterances up
/// against turn times needs the VAD's own minimum-silence setting, which never leaves the module.
/// A client trying to do this join has the span both backwards and offset.
/// </summary>
public class ConversationReprocessorTests
{
    [Fact]
    public void A_turn_nobody_could_read_wears_nothing()
    {
        // Absent, never neutral. The whole vocabulary treats a missing reading as "we could not
        // say", and a turn is no different from an utterance in that.
        Assert.Null(ConversationReprocessor.WinningEmotion(null));
        Assert.Null(ConversationReprocessor.WinningEmotion([]));
    }

    [Fact]
    public void A_single_reading_is_the_answer()
    {
        Assert.Equal(
            "happy",
            ConversationReprocessor.WinningEmotion([new EmotionClaim("happy", 2.5)]));
    }

    [Fact]
    public void The_reading_covering_most_of_the_turn_wins()
    {
        // A turn often spans several utterances, because the VAD cuts on silence rather than on
        // sentences. The reading with the most audio behind it is the one with the most evidence.
        string? emotion = ConversationReprocessor.WinningEmotion(
        [
            new EmotionClaim("angry", 0.4),
            new EmotionClaim("sad", 6.0),
            new EmotionClaim("happy", 1.2),
        ]);

        Assert.Equal("sad", emotion);
    }

    [Fact]
    public void A_short_interjection_does_not_relabel_a_long_turn()
    {
        // The failure this rule exists to prevent: half a second of surprise turning ten seconds
        // of calm speech into a startled turn.
        string? emotion = ConversationReprocessor.WinningEmotion(
        [
            new EmotionClaim("neutral", 10.0),
            new EmotionClaim("surprised", 0.5),
        ]);

        Assert.Equal("neutral", emotion);
    }

    [Fact]
    public void Ties_go_to_the_earlier_reading()
    {
        // So the answer does not depend on the order the utterances happened to be iterated in.
        string? emotion = ConversationReprocessor.WinningEmotion(
        [
            new EmotionClaim("happy", 3.0),
            new EmotionClaim("sad", 3.0),
        ]);

        Assert.Equal("happy", emotion);
    }

    [Fact]
    public void It_is_longest_wins_and_deliberately_not_a_vote()
    {
        // Three short claims outnumber one long one. Counting them would let the VAD's cutting
        // decide the answer, and where the audio was cut is not evidence about how it sounded.
        string? emotion = ConversationReprocessor.WinningEmotion(
        [
            new EmotionClaim("happy", 0.6),
            new EmotionClaim("happy", 0.6),
            new EmotionClaim("happy", 0.6),
            new EmotionClaim("angry", 5.0),
        ]);

        Assert.Equal("angry", emotion);
    }

    [Fact]
    public void The_order_readings_arrive_in_does_not_change_the_answer()
    {
        EmotionClaim[] claims =
        [
            new EmotionClaim("angry", 0.4),
            new EmotionClaim("sad", 6.0),
            new EmotionClaim("happy", 1.2),
        ];

        Assert.Equal(
            ConversationReprocessor.WinningEmotion(claims),
            ConversationReprocessor.WinningEmotion([.. claims.Reverse()]));
    }
}
