namespace Serval.Ai;

/// <summary>
/// Minimal 16-bit PCM WAV reader. Sufficient for fixtures, replay, and diarization recovery —
/// not a general decoder.
///
/// Shared, not a replay concern: the offline conversation pass reads finished conversation WAVs
/// with it, and <c>ConversationAudioBuffer</c> writes the files it reads back.
/// </summary>
public static class WavFile
{
    public static (float[] Samples, int SampleRate) Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));

        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new InvalidDataException($"{path} is not a RIFF file.");
        }

        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidDataException($"{path} is not a WAVE file.");
        }

        int sampleRate = 0;
        short channels = 1;
        short bitsPerSample = 0;

        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            string chunkId = new(reader.ReadChars(4));
            int chunkSize = reader.ReadInt32();

            if (chunkId == "fmt ")
            {
                reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                bitsPerSample = reader.ReadInt16();

                if (chunkSize > 16)
                {
                    reader.BaseStream.Seek(chunkSize - 16, SeekOrigin.Current);
                }
            }
            else if (chunkId == "data")
            {
                if (bitsPerSample != 16)
                {
                    throw new NotSupportedException($"{path}: only 16-bit PCM is supported, got {bitsPerSample}-bit.");
                }

                var samples = new float[chunkSize / 2 / channels];
                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] = reader.ReadInt16() / 32768f;
                    for (int c = 1; c < channels; c++)
                    {
                        reader.ReadInt16();
                    }
                }

                return (samples, sampleRate);
            }
            else
            {
                reader.BaseStream.Seek(chunkSize, SeekOrigin.Current);
            }
        }

        throw new InvalidDataException($"{path} has no data chunk.");
    }
}
