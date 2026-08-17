using Serval.Ai;
using Serval.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Serval.Ai.Tests;

public class ConversationAudioBufferTests : IDisposable
{
    private const int SampleRate = 16000;
    private readonly string _dir;

    public ConversationAudioBufferTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "camera-module-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private ConversationAudioBuffer NewBuffer(double maxMinutes = 30.0)
        => new(Guid.NewGuid(), _dir, SampleRate, maxMinutes, NullLogger.Instance);

    [Fact]
    public void Written_audio_reads_back_within_int16_quantization()
    {
        var buffer = NewBuffer();

        // A short ramp of distinct values, all inside [-1, 1].
        var samples = new float[8000];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)Math.Sin(i * 0.01);
        }

        buffer.Append(samples);
        buffer.MarkSpeech();
        double seconds = buffer.Close();

        Assert.Equal(samples.Length / (double)SampleRate, seconds, 3);

        (float[] read, int rate) = WavFile.Read(buffer.Path);
        Assert.Equal(SampleRate, rate);
        Assert.Equal(samples.Length, read.Length);
        // The buffer encodes with a truncating ×32767 cast while WavFile decodes with ÷32768,
        // so worst-case round-trip error is a shade under two least-significant bits. Well
        // inside this bound, but far tighter than any scaling bug (which would be ~3%).
        const float tolerance = 2.0f / 32767;
        for (int i = 0; i < samples.Length; i++)
        {
            Assert.True(Math.Abs(samples[i] - read[i]) < tolerance,
                $"sample {i}: wrote {samples[i]}, read {read[i]}");
        }
    }

    [Fact]
    public void Header_sizes_match_the_data_after_close()
    {
        var buffer = NewBuffer();
        buffer.Append(new float[1000]);
        buffer.MarkSpeech();
        buffer.Close();

        byte[] bytes = File.ReadAllBytes(buffer.Path);
        long dataBytes = bytes.Length - 44;

        int riffSize = BitConverter.ToInt32(bytes, 4);
        int dataSize = BitConverter.ToInt32(bytes, 40);

        Assert.Equal(36 + dataBytes, riffSize);
        Assert.Equal(dataBytes, dataSize);
    }

    [Fact]
    public void Trailing_silence_past_the_last_speech_is_trimmed_to_a_one_second_tail()
    {
        var buffer = NewBuffer();

        buffer.Append(new float[SampleRate]);        // 1s of "speech"
        buffer.MarkSpeech();                         // mark end of speech here
        buffer.AppendSilence(5.0);                   // 5s of trailing silence (the timeout)

        double seconds = buffer.Close();

        // Keep everything up to last speech plus a 1s tail: 1s + 1s = 2s, not 6s.
        Assert.Equal(2.0, seconds, 3);
    }

    [Fact]
    public void Out_of_range_samples_clamp_rather_than_wrap()
    {
        var buffer = NewBuffer();
        buffer.Append([2.0f, -2.0f]); // beyond [-1, 1]; a wrap would be a loud pop
        buffer.MarkSpeech();
        buffer.Close();

        (float[] read, _) = WavFile.Read(buffer.Path);
        Assert.Equal(2, read.Length);
        Assert.True(read[0] > 0.99f, $"positive overshoot should clamp near +1, got {read[0]}");
        Assert.True(read[1] < -0.99f, $"negative overshoot should clamp near -1, got {read[1]}");
    }

    [Fact]
    public void Size_cap_sets_is_full()
    {
        // 1/60 minute = 1s cap = 16000 samples.
        var buffer = NewBuffer(maxMinutes: 1.0 / 60);
        Assert.False(buffer.IsFull);

        buffer.Append(new float[SampleRate]);
        Assert.True(buffer.IsFull);

        buffer.Close();
    }

    [Fact]
    public void RepairHeader_recovers_a_crash_orphaned_wav()
    {
        // The crash shape: a valid 44-byte header declaring data=0 (written at construction and
        // never patched, because a clean Close never ran) followed by real PCM on disk. This is
        // exactly what SIGKILL leaves; recovery must read the audio back from the file length.
        string path = Path.Combine(_dir, "crash-orphan.wav");
        short[] pcm = Enumerable.Range(0, 4000).Select(i => (short)(i % 500 * 60)).ToArray();
        WriteCrashShapeWav(path, SampleRate, pcm);

        double seconds = ConversationAudioBuffer.RepairHeader(path, SampleRate);
        Assert.Equal(pcm.Length / (double)SampleRate, seconds, 3);

        (float[] read, int rate) = WavFile.Read(path);
        Assert.Equal(SampleRate, rate);
        Assert.Equal(pcm.Length, read.Length);
        for (int i = 0; i < pcm.Length; i++)
        {
            Assert.Equal(pcm[i] / 32768f, read[i], 6);
        }
    }

    [Fact]
    public void RepairHeader_returns_zero_for_a_header_only_file()
    {
        string path = Path.Combine(_dir, "empty.wav");
        WriteCrashShapeWav(path, SampleRate, []);
        Assert.Equal(0, ConversationAudioBuffer.RepairHeader(path, SampleRate));
    }

    // Writes the on-disk shape a crashed ConversationAudioBuffer leaves: a header with the
    // RIFF/data sizes still at their construction-time zero, followed by raw PCM.
    private static void WriteCrashShapeWav(string path, int sampleRate, short[] pcm)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);

        const short channels = 1, bits = 16;
        w.Write("RIFF"u8);
        w.Write(36);                 // unpatched: 36 + 0 data bytes
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * bits / 8);
        w.Write((short)(channels * bits / 8));
        w.Write(bits);
        w.Write("data"u8);
        w.Write(0);                  // unpatched: declares zero data despite the PCM below

        foreach (short s in pcm)
        {
            w.Write(s);
        }
    }
}
