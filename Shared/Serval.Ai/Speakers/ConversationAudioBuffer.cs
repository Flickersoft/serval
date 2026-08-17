using Microsoft.Extensions.Logging;

namespace Serval.Ai;

/// <summary>
/// Writes one conversation's audio to a 16-bit PCM WAV, so it can be diarized once the
/// conversation ends.
///
/// Buffers <em>continuous</em> audio rather than concatenated utterances, for two reasons.
/// It keeps the timebase trivial: segment times become seconds from the conversation's start,
/// and every utterance already carries a wall-clock timestamp, so the two output streams
/// align by subtraction with no offset table to publish. And pyannote is trained on natural
/// speech with pauses — splicing utterances together would create an artificial discontinuity
/// at every join, exactly where a segmentation model would invent a speaker turn.
///
/// Written from the VAD thread, so it must not block: a slow SD card must never stall speech
/// detection. Hence a buffered stream and no flush-per-window.
/// </summary>
public sealed class ConversationAudioBuffer : IDisposable
{
    private const int HeaderBytes = 44;

    private readonly ILogger _logger;
    private readonly FileStream _file;
    private readonly BinaryWriter _writer;
    private readonly int _sampleRate;
    private readonly long _maxSamples;

    private long _samplesWritten;
    private long _samplesAtLastSpeech;
    private bool _capped;
    private bool _disposed;

    public string Path { get; }

    public Guid ConversationId { get; }

    /// <summary>Audio written so far, in seconds.</summary>
    public double Seconds => (double)_samplesWritten / _sampleRate;

    /// <summary>True once the size cap is hit; the tracker should finalise the conversation.</summary>
    public bool IsFull => _capped;

    public ConversationAudioBuffer(
        Guid conversationId, string directory, int sampleRate, double maxMinutes, ILogger logger)
    {
        ConversationId = conversationId;
        _sampleRate = sampleRate;
        _logger = logger;
        _maxSamples = (long)(sampleRate * maxMinutes * 60);

        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"conversation-{conversationId:N}.wav");

        _file = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
        _writer = new BinaryWriter(_file);

        // Write a structurally valid header immediately, with zero data length; the sizes are
        // patched on Close once the real length is known. Writing a blank placeholder instead
        // would leave a crash-orphaned file unparseable — precisely the file that recovery
        // exists to rescue. See RepairHeader.
        WriteHeader(_writer, sampleRate, dataBytes: 0);
    }

    /// <summary>Appends a window of audio. Called from the VAD thread; never blocks on flush.</summary>
    public void Append(ReadOnlySpan<float> samples)
    {
        if (_disposed || _capped)
        {
            return;
        }

        foreach (float sample in samples)
        {
            float clamped = Math.Clamp(sample, -1f, 1f);
            _writer.Write((short)(clamped * 32767f));
        }

        _samplesWritten += samples.Length;

        if (_samplesWritten >= _maxSamples)
        {
            _capped = true;
            _logger.LogWarning(
                "Conversation {Id} hit the {Minutes:F0} minute cap; finalising early.",
                ConversationId, _maxSamples / (double)_sampleRate / 60);
        }
    }

    /// <summary>Appends silence, used to preserve the timeline across a seam.</summary>
    public void AppendSilence(double seconds)
    {
        int count = (int)(seconds * _sampleRate);
        for (int i = 0; i < count; i++)
        {
            _writer.Write((short)0);
        }

        _samplesWritten += count;
    }

    /// <summary>
    /// Marks the current position as the end of real speech. On finalise, everything after
    /// this is dropped — otherwise every conversation would carry the full silence timeout
    /// (minutes of nothing) into the WAV and into diarization.
    /// </summary>
    public void MarkSpeech() => _samplesAtLastSpeech = _samplesWritten;

    /// <summary>Finalises the WAV: trims trailing silence and patches the header. Returns audio seconds.</summary>
    public double Close()
    {
        if (_disposed)
        {
            return 0;
        }

        _disposed = true;

        // Keep a short tail past the last speech so a final word is not clipped.
        long keep = Math.Min(_samplesWritten, _samplesAtLastSpeech + _sampleRate);
        long dataBytes = keep * 2;

        _writer.Flush();
        _file.SetLength(HeaderBytes + dataBytes);

        _file.Seek(0, SeekOrigin.Begin);
        WriteHeader(_writer, _sampleRate, dataBytes);

        _writer.Flush();
        _writer.Dispose();
        _file.Dispose();

        return (double)keep / _sampleRate;
    }

    /// <summary>
    /// Patches a crash-orphaned WAV's sizes from its actual length. A conversation killed
    /// mid-write still has all its audio and a valid header shape, but the RIFF and data sizes
    /// were never filled in — they are only written on a clean Close. Returns audio seconds.
    /// </summary>
    public static double RepairHeader(string path, int sampleRate)
    {
        var info = new FileInfo(path);
        long dataBytes = info.Length - HeaderBytes;
        if (dataBytes <= 0)
        {
            return 0;
        }

        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        using var writer = new BinaryWriter(file);
        WriteHeader(writer, sampleRate, dataBytes);
        writer.Flush();

        return dataBytes / 2.0 / sampleRate;
    }

    private static void WriteHeader(BinaryWriter w, int sampleRate, long dataBytes)
    {
        const short channels = 1;
        const short bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;

        w.Write("RIFF"u8);
        w.Write((int)(36 + dataBytes));
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);                 // PCM chunk size
        w.Write((short)1);           // PCM
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)(channels * bitsPerSample / 8));
        w.Write(bitsPerSample);
        w.Write("data"u8);
        w.Write((int)dataBytes);
    }

    public void Dispose() => Close();
}
