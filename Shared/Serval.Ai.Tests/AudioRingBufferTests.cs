using Serval.Ai;
using Serval.Contracts;

namespace Serval.Ai.Tests;

public class AudioRingBufferTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 4)]
    [InlineData(1000, 1024)]
    [InlineData(1024, 1024)]
    [InlineData(1025, 2048)]
    public void Capacity_rounds_up_to_a_power_of_two(int requested, int expected)
    {
        var ring = new AudioRingBuffer(requested);
        Assert.Equal(expected, ring.Capacity);
    }

    [Fact]
    public void Write_then_read_returns_the_same_samples()
    {
        var ring = new AudioRingBuffer(16);
        float[] source = [1f, 2f, 3f, 4f];

        Assert.True(ring.Write(source));

        var destination = new float[4];
        Assert.Equal(4, ring.Read(destination));
        Assert.Equal(source, destination);
    }

    [Fact]
    public void Data_wraps_correctly_across_the_mask_boundary()
    {
        // Capacity 8. Advance the cursors most of the way round, then write a run that must
        // straddle the wrap: if the two-chunk copy is wrong the tail is silently corrupted.
        var ring = new AudioRingBuffer(8);
        var scratch = new float[8];

        Assert.True(ring.Write([10f, 11f, 12f, 13f, 14f, 15f]));
        Assert.Equal(6, ring.Read(scratch)); // read cursor now at 6

        float[] straddling = [20f, 21f, 22f, 23f, 24f];
        Assert.True(ring.Write(straddling)); // writes indices 6,7,0,1,2

        var destination = new float[5];
        Assert.Equal(5, ring.Read(destination));
        Assert.Equal(straddling, destination);
    }

    [Fact]
    public void Overrun_drops_the_newest_and_leaves_buffered_data_intact()
    {
        var ring = new AudioRingBuffer(4); // capacity 4
        Assert.True(ring.Write([1f, 2f, 3f]));

        // Only one slot free; this does not fit and must be rejected wholesale.
        Assert.False(ring.Write([4f, 5f]));
        Assert.Equal(2, ring.DroppedSamples);

        // The already-buffered samples must survive the rejected write untouched.
        var destination = new float[3];
        Assert.Equal(3, ring.Read(destination));
        Assert.Equal([1f, 2f, 3f], destination);
    }

    [Fact]
    public void Read_returns_fewer_than_requested_when_less_is_available()
    {
        var ring = new AudioRingBuffer(16);
        ring.Write([1f, 2f]);

        var destination = new float[10];
        Assert.Equal(2, ring.Read(destination));
    }

    [Fact]
    public void Read_of_empty_buffer_returns_zero()
    {
        var ring = new AudioRingBuffer(16);
        Assert.Equal(0, ring.Read(new float[4]));
    }

    [Fact]
    public void Concurrent_producer_and_consumer_preserve_the_exact_sequence()
    {
        // The SPSC contract: with one producer and one consumer, every sample published is
        // read back exactly once and in order. A torn window or a lost publish shows up as a
        // mismatch. 5M samples with a deterministic value stream (i % 997, exact in float).
        const int total = 5_000_000;
        var ring = new AudioRingBuffer(4096);

        var producer = new Thread(() =>
        {
            int written = 0;
            var chunk = new float[256];
            while (written < total)
            {
                int n = Math.Min(chunk.Length, total - written);
                for (int i = 0; i < n; i++)
                {
                    chunk[i] = (written + i) % 997;
                }

                // Retry until the whole chunk lands; the consumer is draining concurrently.
                while (!ring.Write(chunk.AsSpan(0, n)))
                {
                    Thread.SpinWait(1);
                }

                written += n;
            }
        });

        Exception? failure = null;
        var consumer = new Thread(() =>
        {
            int read = 0;
            var chunk = new float[256];
            try
            {
                while (read < total)
                {
                    int n = ring.Read(chunk);
                    for (int i = 0; i < n; i++)
                    {
                        float expected = (read + i) % 997;
                        if (chunk[i] != expected)
                        {
                            throw new Xunit.Sdk.XunitException(
                                $"At index {read + i}: expected {expected}, got {chunk[i]}.");
                        }
                    }

                    read += n;
                    if (n == 0)
                    {
                        Thread.SpinWait(1);
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        producer.Start();
        consumer.Start();
        producer.Join();
        consumer.Join();

        // The only invariant that matters here is that every published sample was read back
        // exactly once and in order. DroppedSamples is expected to be nonzero: the producer
        // retries on a full ring, and each rejected attempt counts — that is the back-pressure
        // signal, not data loss (the retried write always lands).
        Assert.Null(failure);
    }
}
