using Microsoft.Extensions.Logging.Abstractions;

namespace Serval.Ai.Tests;

public class ConversationTrackerTests : IDisposable
{
    private const int SampleRate = 16000;
    private readonly string _dir;
    private readonly SpeakerOptions _speaker;
    private readonly VadOptions _vad;

    public ConversationTrackerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "serval-ai-tracker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _vad = new VadOptions { SampleRate = SampleRate };
        _speaker = new SpeakerOptions { ConversationAudioDirectory = Path.Combine(_dir, "conversations") };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private ConversationTracker NewTracker()
        => new(_speaker, _vad, NullLogger<ConversationTracker>.Instance, NullLoggerFactory.Instance);

    private float[] Seconds(double s) => new float[(int)(s * SampleRate)];

    private string ConversationsDir => _speaker.ConversationAudioDirectory;

    [Fact]
    public void First_utterance_is_seeded_into_the_conversation_audio()
    {
        // Without seeding, the conversation's buffer would be empty when it finalises (the VAD
        // only emits after the speech has already passed the tee) and the conversation would be
        // dropped for having under a second of audio — silently losing a speaker. The presence
        // of a finished conversation of the right length is proof the seed happened.
        using var tracker = NewTracker();
        var t0 = DateTimeOffset.Parse("2026-07-15T20:00:00Z");
        double utteranceSeconds = 2.0;

        tracker.NoteUtterance(t0, Seconds(utteranceSeconds));
        tracker.CheckForEnd(t0.AddMinutes(10)); // long past the silence timeout

        Assert.True(tracker.Finished.TryRead(out FinishedConversation? finished));
        double expected = utteranceSeconds + _vad.MinSilenceSeconds;
        Assert.Equal(expected, finished!.AudioSeconds, 2);
    }

    [Fact]
    public void Started_at_is_backdated_so_position_zero_aligns_with_the_conversation_start()
    {
        using var tracker = NewTracker();
        var t0 = DateTimeOffset.Parse("2026-07-15T20:00:00Z");
        double utteranceSeconds = 2.0;

        tracker.NoteUtterance(t0, Seconds(utteranceSeconds));
        tracker.CheckForEnd(t0.AddMinutes(10));

        Assert.True(tracker.Finished.TryRead(out FinishedConversation? finished));
        double lead = utteranceSeconds + _vad.MinSilenceSeconds;
        Assert.Equal(t0 - TimeSpan.FromSeconds(lead), finished!.StartedAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void Every_utterance_aligns_into_the_diarization_timeline_by_subtraction()
    {
        // The entire two-stream design in one assertion: an utterance captured at wall-clock T
        // must map to offset (T - StartedAt) inside [0, AudioSeconds]. If it cannot, the live
        // and after-the-fact streams cannot be joined and the design has failed.
        using var tracker = NewTracker();
        var t0 = DateTimeOffset.Parse("2026-07-15T20:00:00Z");

        tracker.NoteUtterance(t0, Seconds(1.5));

        // Simulate three seconds of continuous capture teeing through, then a second utterance.
        tracker.AppendAudio(Seconds(3.0));
        var t1 = t0.AddSeconds(3.0);
        tracker.NoteUtterance(t1, Seconds(1.0));

        tracker.CheckForEnd(t1.AddMinutes(10));

        Assert.True(tracker.Finished.TryRead(out FinishedConversation? finished));
        foreach (DateTimeOffset at in new[] { t0, t1 })
        {
            double offset = (at - finished!.StartedAt).TotalSeconds;
            Assert.True(offset >= 0, $"offset {offset} < 0");
            Assert.True(offset <= finished.AudioSeconds + 1e-3, $"offset {offset} > audio {finished.AudioSeconds}");
        }
    }

    [Fact]
    public void Same_conversation_id_within_the_silence_timeout()
    {
        using var tracker = NewTracker();
        var t0 = DateTimeOffset.Parse("2026-07-15T20:00:00Z");

        Guid first = tracker.NoteUtterance(t0, Seconds(1.5));
        tracker.CheckForEnd(t0.AddSeconds(30)); // well under the 3-minute default
        Guid second = tracker.NoteUtterance(t0.AddSeconds(31), Seconds(1.5));

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_gap_past_the_silence_timeout_starts_a_new_conversation()
    {
        // A new id is what lets the live labeller reset its per-conversation speaker numbering.
        using var tracker = NewTracker();
        var t0 = DateTimeOffset.Parse("2026-07-15T20:00:00Z");

        Guid first = tracker.NoteUtterance(t0, Seconds(1.5));
        var afterTimeout = t0.AddMinutes(_speaker.SilenceTimeoutMinutes + 1);
        tracker.CheckForEnd(afterTimeout);
        Guid second = tracker.NoteUtterance(afterTimeout.AddSeconds(1), Seconds(1.5));

        Assert.NotEqual(first, second);

        // Two finished conversations, one per gap.
        Assert.True(tracker.Finished.TryRead(out FinishedConversation? a));
        tracker.CheckForEnd(afterTimeout.AddMinutes(10));
        Assert.True(tracker.Finished.TryRead(out FinishedConversation? b));
        Assert.NotEqual(a!.ConversationId, b!.ConversationId);
    }

    [Fact]
    public void A_conversation_under_one_second_is_dropped_and_its_file_deleted()
    {
        using var tracker = NewTracker();
        var t0 = DateTimeOffset.Parse("2026-07-15T20:00:00Z");

        // Tiny utterance; seed + MinSilence stays under a second, so there is nothing to diarize.
        tracker.NoteUtterance(t0, new float[100]);
        tracker.CheckForEnd(t0.AddMinutes(10));

        Assert.False(tracker.Finished.TryRead(out _));
        if (Directory.Exists(ConversationsDir))
        {
            Assert.Empty(Directory.GetFiles(ConversationsDir, "conversation-*.wav"));
        }
    }

    [Fact]
    public void FlushOnShutdown_finishes_the_open_conversation_and_completes_the_channel()
    {
        using var tracker = NewTracker();
        var t0 = DateTimeOffset.Parse("2026-07-15T20:00:00Z");

        tracker.NoteUtterance(t0, Seconds(1.5));
        tracker.FlushOnShutdown();

        Assert.True(tracker.Finished.TryRead(out FinishedConversation? finished));
        Assert.NotNull(finished);

        // The channel is completed, so no further reads are possible.
        Assert.False(tracker.Finished.TryRead(out _));
        Assert.True(tracker.Finished.Completion.IsCompleted);
    }

    [Fact]
    public void AppendAudio_after_finalise_is_a_no_op()
    {
        using var tracker = NewTracker();
        var t0 = DateTimeOffset.Parse("2026-07-15T20:00:00Z");

        tracker.NoteUtterance(t0, Seconds(1.5));
        tracker.CheckForEnd(t0.AddMinutes(10)); // buffer detached and closed

        // Must not throw on the now-null buffer; the VAD thread keeps calling this.
        tracker.AppendAudio(Seconds(0.5));
    }
}
