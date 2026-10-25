// wiki: testing#unit-tests
using System.Reflection;
using SharpAudio.Core.Services;

namespace SharpAudio.Api.Tests;

/// <summary>Covers the streaming session's pending-audio ring buffer: wrap-around reads, growth while
/// preserving order, the copy-then-advance contract that replaced the List&lt;byte&gt;
/// GetRange/RemoveRange pattern, and the shape that keeps per-chunk work constant.</summary>
public sealed class ByteRingBufferTests
{
    private const int ChunkSamples = 10_000;

    [Fact]
    public void CopyTo_reads_without_consuming_and_Advance_consumes()
    {
        var ring = new ByteRingBuffer(8);
        ring.Add(new byte[] { 1, 2, 3, 4 });

        var scratch = new byte[2];
        ring.CopyTo(scratch);
        Assert.Equal(new byte[] { 1, 2 }, scratch);
        Assert.Equal(4, ring.Length);

        ring.Advance(2);
        Assert.Equal(2, ring.Length);

        ring.CopyTo(scratch);
        Assert.Equal(new byte[] { 3, 4 }, scratch);
    }

    [Fact]
    public void Reads_wrap_around_the_end_of_the_buffer()
    {
        var ring = new ByteRingBuffer(4);
        ring.Add(new byte[] { 1, 2, 3, 4 });
        ring.Advance(3);

        // Only one byte remains at the tail; the next add must continue at the start of the buffer.
        ring.Add(new byte[] { 5, 6 });

        var scratch = new byte[3];
        ring.CopyTo(scratch);
        Assert.Equal(new byte[] { 4, 5, 6 }, scratch);
    }

    [Fact]
    public void Growing_keeps_the_bytes_in_order()
    {
        var ring = new ByteRingBuffer(4);
        ring.Add(new byte[] { 1, 2, 3, 4 });
        ring.Advance(2);
        ring.Add(new byte[] { 5, 6, 7, 8, 9 });

        var scratch = new byte[7];
        ring.CopyTo(scratch);
        Assert.Equal(new byte[] { 3, 4, 5, 6, 7, 8, 9 }, scratch);
    }

    [Fact]
    public void CopyTo_is_capped_by_the_destination_and_the_available_bytes()
    {
        var ring = new ByteRingBuffer(16);
        ring.Add(new byte[] { 1, 2, 3 });

        var scratch = new byte[8];
        scratch[3] = 42;
        ring.CopyTo(scratch);

        Assert.Equal(new byte[] { 1, 2, 3 }, scratch[..3]);
        Assert.Equal(42, scratch[3]);
    }

    [Fact]
    public void Advancing_past_the_available_bytes_is_rejected()
    {
        var ring = new ByteRingBuffer(4);
        ring.Add(new byte[] { 1, 2 });

        Assert.Throws<ArgumentOutOfRangeException>(() => ring.Advance(3));
    }

    [Fact]
    public void Clear_resets_the_buffer()
    {
        var ring = new ByteRingBuffer(4);
        ring.Add(new byte[] { 1, 2, 3 });
        ring.Clear();

        Assert.Equal(0, ring.Length);
        ring.Add(new byte[] { 9 });

        var scratch = new byte[1];
        ring.CopyTo(scratch);
        Assert.Equal(9, scratch[0]);
    }

    [Fact]
    public void Steady_state_chunks_never_reallocate() {
        var ring = new ByteRingBuffer(ChunkSamples);
        var scratch = new byte[ChunkSamples];
        byte[]? firstArray = ReadBackingArray(ring);

        for (var i = 0; i < 200; i++)
        {
            ring.Add(new byte[ChunkSamples]);
            ring.CopyTo(scratch);
            ring.Advance(ChunkSamples);
        }

        // The per-chunk cost is one copy into the caller's span plus an index move: the backing array
        // must stay the same instance for the whole run, which is what the old
        // GetRange(...).ToArray() + RemoveRange(...) pattern could not guarantee.
        Assert.Same(firstArray, ReadBackingArray(ring));
        Assert.Equal(0, ring.Length);
    }

    [Fact]
    public void Growth_is_geometric_rather_than_linear() {
        var ring = new ByteRingBuffer(8);
        var capacities = new List<int>();

        for (var i = 0; i < 6; i++)
        {
            capacities.Add(ReadBackingArray(ring).Length);
            ring.Add(new byte[8]);
        }

        Assert.True(capacities[^1] > capacities[0], "the buffer never grew, so nothing was asserted");

        for (var i = 1; i < capacities.Count; i++)
        {
            if (capacities[i] == capacities[i - 1])
            {
                continue; // growth only happens when a chunk no longer fits
            }

            Assert.True(capacities[i] >= capacities[i - 1] * 2,
                $"capacity went {capacities[i - 1]} -> {capacities[i]}, which is not a doubling step");
        }
    }

    private static byte[] ReadBackingArray(ByteRingBuffer ring)
    {
        var field = typeof(ByteRingBuffer).GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (byte[])field.GetValue(ring)!;
    }
}
