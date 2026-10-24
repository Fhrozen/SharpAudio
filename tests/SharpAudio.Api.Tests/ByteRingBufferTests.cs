// wiki: testing#unit-tests
using SharpAudio.Core.Services;

namespace SharpAudio.Api.Tests;

/// <summary>Covers the streaming session's pending-audio ring buffer: wrap-around reads, growth while
/// preserving order, and the copy-then-advance contract that replaced the List&lt;byte&gt;
/// GetRange/RemoveRange pattern.</summary>
public sealed class ByteRingBufferTests
{
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
}
