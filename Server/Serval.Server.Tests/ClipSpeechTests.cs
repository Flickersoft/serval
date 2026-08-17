using MongoDB.Bson;
using Serval.Contracts;
using Serval.Server.Clips;

namespace Serval.Server.Tests;

/// <summary>
/// "Said in it" — how a clip's frozen speech becomes the list the detail panel draws.
///
/// The reconciliation is the whole content of this: a conversation exists twice in storage, once as
/// the live utterances that were transcribed as they happened and once as the settled, re-diarized
/// transcript. Both are frozen with the clip, deliberately, so the choice of which to show is made
/// here rather than at write time.
/// </summary>
public class ClipSpeechTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 9, 16, 3, 12, TimeSpan.Zero);

    private static SavedClip Clip(ClipDocuments documents) => new()
    {
        Id = ObjectId.GenerateNewId(),
        CameraId = "front-door",
        CameraName = "Front door",
        Name = "Parcel behind the planter",
        SavedBy = "jeremiah",
        From = Start,
        To = Start.AddSeconds(55),
        SavedAt = Start,
        DurationSeconds = 55,
        Documents = documents,
    };

    private static UtteranceDocument Utterance(
        double atSeconds, string text, string? conversationId = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        CameraId = "front-door",
        ConversationId = conversationId,
        Timestamp = Start.AddSeconds(atSeconds),
        Transcript = text,
    };

    [Fact]
    public void Lines_carry_their_offset_from_the_start_of_the_clip()
    {
        SavedClip clip = Clip(new ClipDocuments
        {
            Utterances = [Utterance(6, "Hello? Delivery for number twelve.")],
        });

        ClipSpeechLine line = Assert.Single(ClipSpeech.Of(clip));

        Assert.Equal(6, line.OffsetSeconds, precision: 3);
        Assert.Equal("Hello? Delivery for number twelve.", line.Text);
    }

    [Fact]
    public void Lines_come_out_in_the_order_they_were_said()
    {
        SavedClip clip = Clip(new ClipDocuments
        {
            Utterances = [Utterance(50, "Behind the planter."), Utterance(6, "Delivery.")],
        });

        Assert.Equal(["Delivery.", "Behind the planter."], ClipSpeech.Of(clip).Select(l => l.Text));
    }

    [Fact]
    public void A_settled_conversation_speaks_through_its_turns_rather_than_twice()
    {
        // The live utterances of a settled conversation are still stored — nothing rewrites them —
        // so without this rule every sentence of a finished doorstep exchange appears twice.
        var transcript = new ConversationTranscriptDocument
        {
            ConversationId = "conv-1",
            CameraId = "front-door",
            StartedAt = Start,
            AudioSeconds = 55,
            Turns =
            [
                new TranscriptTurn { Start = 6, End = 9, Speaker = 0, Text = "Delivery for number twelve." },
                new TranscriptTurn { Start = 29, End = 32, Speaker = 1, Text = "Behind the planter, please." },
            ],
        };

        SavedClip clip = Clip(new ClipDocuments
        {
            ConversationTranscripts = [transcript],
            Utterances =
            [
                Utterance(6, "Delivery for number twelve.", conversationId: "conv-1"),
                Utterance(29, "Behind the planter, please.", conversationId: "conv-1"),
            ],
        });

        IReadOnlyList<ClipSpeechLine> lines = ClipSpeech.Of(clip);

        Assert.Equal(2, lines.Count);
        Assert.Equal(["Speaker 1", "Speaker 2"], lines.Select(l => l.Speaker));
    }

    [Fact]
    public void A_conversation_still_open_when_the_clip_was_taken_keeps_its_live_utterances()
    {
        // It has no settled transcript and never will have one inside this clip, so dropping its
        // utterances would leave a clip of somebody talking with nothing said in it.
        SavedClip clip = Clip(new ClipDocuments
        {
            Utterances = [Utterance(6, "Is anyone home?", conversationId: "conv-open")],
        });

        Assert.Single(ClipSpeech.Of(clip));
    }

    [Fact]
    public void A_settled_conversation_with_no_turns_still_yields_its_utterances()
    {
        // A transcript row that produced no turns explains nothing, and suppressing the live half
        // on the strength of it would lose the speech entirely.
        SavedClip clip = Clip(new ClipDocuments
        {
            ConversationTranscripts =
            [
                new ConversationTranscriptDocument
                {
                    ConversationId = "conv-1",
                    CameraId = "front-door",
                    StartedAt = Start,
                    AudioSeconds = 55,
                },
            ],
            Utterances = [Utterance(6, "Delivery.", conversationId: "conv-1")],
        });

        Assert.Single(ClipSpeech.Of(clip));
    }

    [Fact]
    public void Turns_from_before_the_clip_started_are_not_said_in_it()
    {
        // A conversation is frozen when it overlaps the clip, which is right — but its early turns
        // happened before the first frame, and a viewer scrubbing to 0:00 would not hear them.
        var transcript = new ConversationTranscriptDocument
        {
            ConversationId = "conv-1",
            CameraId = "front-door",
            StartedAt = Start.AddSeconds(-40),
            AudioSeconds = 120,
            Turns =
            [
                new TranscriptTurn { Start = 2, End = 5, Speaker = 0, Text = "Number twelve or fourteen?" },
                new TranscriptTurn { Start = 46, End = 49, Speaker = 0, Text = "Delivery for number twelve." },
            ],
        };

        SavedClip clip = Clip(new ClipDocuments { ConversationTranscripts = [transcript] });

        ClipSpeechLine line = Assert.Single(ClipSpeech.Of(clip));

        Assert.Equal("Delivery for number twelve.", line.Text);
        Assert.Equal(6, line.OffsetSeconds, precision: 3);
    }

    [Fact]
    public void Speech_after_the_clip_ended_is_left_out_too()
    {
        SavedClip clip = Clip(new ClipDocuments
        {
            Utterances = [Utterance(6, "Inside."), Utterance(94, "Long after.")],
        });

        Assert.Equal(["Inside."], ClipSpeech.Of(clip).Select(l => l.Text));
    }

    [Fact]
    public void Empty_transcripts_are_not_drawn_as_blank_lines()
    {
        SavedClip clip = Clip(new ClipDocuments
        {
            Utterances = [Utterance(6, "   "), Utterance(10, "Something.")],
        });

        Assert.Equal(["Something."], ClipSpeech.Of(clip).Select(l => l.Text));
    }
}
