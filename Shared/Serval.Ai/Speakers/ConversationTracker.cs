using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Serval.Ai;

/// <summary>A conversation whose audio is complete and ready to diarize.</summary>
public sealed record FinishedConversation(
    Guid ConversationId, string AudioPath, DateTimeOffset StartedAt, double AudioSeconds);

/// <summary>
/// Owns conversation boundaries and the <c>conversation_id</c> that joins the two output
/// streams.
///
/// A conversation starts at the first utterance after a gap and ends after
/// <see cref="SpeakerOptions.SilenceTimeoutMinutes"/> without one. That boundary is what makes
/// after-the-fact diarization possible at all: the objection to it was always "it needs the
/// whole recording, but we stream" — at conversation end, the whole recording exists.
///
/// The boundary also scopes speaker identity. Numbering restarts each conversation and means
/// nothing across them, which is what keeps a multi-week run from accumulating drifted
/// near-duplicate speakers.
/// </summary>
public sealed class ConversationTracker : IDisposable
{
    private readonly SpeakerOptions _options;
    private readonly string _audioDirectory;
    private readonly int _sampleRate;
    private readonly double _vadMinSilenceSeconds;
    private readonly ILogger<ConversationTracker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly object _gate = new();

    private readonly Channel<FinishedConversation> _finished =
        Channel.CreateUnbounded<FinishedConversation>(new UnboundedChannelOptions { SingleReader = true });

    private ConversationAudioBuffer? _buffer;
    private Guid _conversationId;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _lastUtteranceAt;

    /// <param name="scope">
    /// Distinguishes concurrent trackers writing to the same directory. The edge module runs one
    /// tracker for its single camera and leaves this null; the Server runs one per camera and
    /// passes the camera id, so two cameras' conversation audio cannot collide on disk.
    /// </param>
    public ConversationTracker(
        SpeakerOptions options,
        VadOptions vad,
        ILogger<ConversationTracker> logger,
        ILoggerFactory loggerFactory,
        string? scope = null)
    {
        _options = options;
        _sampleRate = vad.SampleRate;
        _vadMinSilenceSeconds = vad.MinSilenceSeconds;
        _logger = logger;
        _loggerFactory = loggerFactory;

        Scope = scope;
        _audioDirectory = scope is null
            ? options.ConversationAudioDirectory
            : Path.Combine(options.ConversationAudioDirectory, scope);
    }

    /// <summary>Which camera these conversations belong to, or null for a single-camera host.</summary>
    public string? Scope { get; }

    /// <summary>Conversations whose audio is complete, for <see cref="ConversationReprocessor"/>.</summary>
    public ChannelReader<FinishedConversation> Finished => _finished.Reader;

    /// <summary>
    /// The conversation an utterance belongs to, starting one if needed.
    ///
    /// The utterance that *starts* a conversation has to be seeded into the audio explicitly.
    /// The VAD only emits a segment once it has seen <c>MinSilenceSeconds</c> of trailing
    /// silence, so by the time we learn a conversation has begun, that speech has already
    /// flowed past the tee and would be missing from the diarization entirely — enough to
    /// lose a whole speaker in a short exchange.
    /// </summary>
    public Guid NoteUtterance(DateTimeOffset at, float[] samples)
    {
        lock (_gate)
        {
            if (_buffer is null)
            {
                // Backdate the start so buffer position 0 corresponds to started_at: the seeded
                // speech and the silence the VAD waited through both precede `at`, and without
                // this the two output streams stop aligning by subtraction.
                double lead = (double)samples.Length / _sampleRate + _vadMinSilenceSeconds;
                Start(at - TimeSpan.FromSeconds(lead));

                _buffer!.Append(samples);
                _buffer.AppendSilence(_vadMinSilenceSeconds);
            }

            _lastUtteranceAt = at;
            _buffer!.MarkSpeech();
            return _conversationId;
        }
    }

    /// <summary>
    /// Tees audio into the current conversation. Called from the VAD thread for every window,
    /// including silence — see <see cref="ConversationAudioBuffer"/> for why continuous audio
    /// matters. A no-op when no conversation is active.
    /// </summary>
    public void AppendAudio(ReadOnlySpan<float> samples)
    {
        lock (_gate)
        {
            _buffer?.Append(samples);
        }
    }

    /// <summary>
    /// Finalises the conversation if it has gone quiet or hit the size cap. Called on a timer;
    /// the boundary cannot be detected from utterances alone, since the defining event is the
    /// <em>absence</em> of one.
    /// </summary>
    public void CheckForEnd(DateTimeOffset now)
    {
        ConversationAudioBuffer? finished;
        DateTimeOffset startedAt;
        string reason;

        lock (_gate)
        {
            if (_buffer is null)
            {
                return;
            }

            bool quiet = now - _lastUtteranceAt >= TimeSpan.FromMinutes(_options.SilenceTimeoutMinutes);
            if (!quiet && !_buffer.IsFull)
            {
                return;
            }

            // Detach under the lock, close outside it. Closing flushes and rewrites the WAV
            // header, and AppendAudio runs on the VAD thread — holding the lock across that
            // I/O would stall speech detection on a slow SD card. Once _buffer is null,
            // AppendAudio is a no-op and cannot wait on us.
            finished = _buffer;
            startedAt = _startedAt;
            reason = quiet ? "silence" : "size cap";
            _buffer = null;
        }

        Complete(finished, startedAt, reason);
    }

    /// <summary>Finalises any open conversation so a clean shutdown still yields a diarization record.</summary>
    public void FlushOnShutdown()
    {
        ConversationAudioBuffer? finished;
        DateTimeOffset startedAt;

        lock (_gate)
        {
            finished = _buffer;
            startedAt = _startedAt;
            _buffer = null;
        }

        if (finished is not null)
        {
            Complete(finished, startedAt, "shutdown");
        }

        _finished.Writer.TryComplete();
    }

    private void Start(DateTimeOffset at)
    {
        _conversationId = Guid.NewGuid();
        _startedAt = at;
        _buffer = new ConversationAudioBuffer(
            _conversationId,
            _audioDirectory,
            _sampleRate,
            _options.MaxConversationMinutes,
            _loggerFactory.CreateLogger<ConversationAudioBuffer>());

        _logger.LogInformation("Conversation {Id} started.", _conversationId);
    }

    /// <summary>Closes the audio and queues it for diarization. Must run outside <see cref="_gate"/>.</summary>
    private void Complete(ConversationAudioBuffer buffer, DateTimeOffset startedAt, string reason)
    {
        double seconds = buffer.Close();

        _logger.LogInformation(
            "Conversation {Id} ended ({Reason}): {Seconds:F1}s of audio.", buffer.ConversationId, reason, seconds);

        // Nothing to diarize; drop the file rather than leave an empty conversation behind.
        if (seconds < 1.0)
        {
            TryDelete(buffer.Path);
            return;
        }

        _finished.Writer.TryWrite(
            new FinishedConversation(buffer.ConversationId, buffer.Path, startedAt, seconds));
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not delete {Path}.", path);
        }
    }

    public void Dispose() => FlushOnShutdown();
}
