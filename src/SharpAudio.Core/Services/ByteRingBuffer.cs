// wiki: asr-engines#nemotron-engine
namespace SharpAudio.Core.Services;

/// <summary>Growable byte ring buffer holding the audio a streaming session has received but not yet
/// decoded. Reading a chunk copies into the caller's scratch span and advances an index, so the
/// <c>List&lt;byte&gt;</c> <c>GetRange(...).ToArray()</c> + <c>RemoveRange(...)</c> pattern — which
/// allocated a new array and shifted the remaining bytes on every chunk — is gone. Single-owner: one
/// streaming session owns one buffer, so no locking.</summary>
public sealed class ByteRingBuffer
{
    private byte[] _buffer;
    private int _head;
    private int _length;

    public ByteRingBuffer(int capacity)
    {
        _buffer = new byte[Math.Max(1, capacity)];
    }

    public int Length => _length;

    public void Add(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        if (_length + bytes.Length > _buffer.Length)
        {
            Grow(_length + bytes.Length);
        }

        // New bytes go at the write position, not at the read position: once Advance has moved the
        // read head forward, appending must not overwrite the data that is still pending.
        var write = (_head + _length) % _buffer.Length;
        var first = Math.Min(bytes.Length, _buffer.Length - write);
        bytes.Slice(0, first).CopyTo(new Span<byte>(_buffer, write, first));
        if (first < bytes.Length)
        {
            bytes.Slice(first).CopyTo(new Span<byte>(_buffer, 0, bytes.Length - first));
        }

        _length += bytes.Length;
    }

    /// <summary>Copies at most <paramref name="destination"/>.Length bytes from the front of the buffer
    /// without consuming them; <see cref="Advance"/> is what consumes.</summary>
    public void CopyTo(Span<byte> destination)
    {
        var count = Math.Min(destination.Length, _length);
        var first = Math.Min(count, _buffer.Length - _head);
        new Span<byte>(_buffer, _head, first).CopyTo(destination.Slice(0, first));

        if (count > first)
        {
            new Span<byte>(_buffer, 0, count - first).CopyTo(destination.Slice(first));
        }
    }

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _length);

        _head = (_head + count) % _buffer.Length;
        _length -= count;
    }

    public void Clear()
    {
        _head = 0;
        _length = 0;
    }

    private void Grow(int minimumCapacity)
    {
        var capacity = _buffer.Length;
        while (capacity < minimumCapacity)
        {
            capacity *= 2;
        }

        var next = new byte[capacity];
        var first = Math.Min(_length, _buffer.Length - _head);
        new Span<byte>(_buffer, _head, first).CopyTo(next.AsSpan(0, first));

        if (_length > first)
        {
            new Span<byte>(_buffer, 0, _length - first).CopyTo(next.AsSpan(first));
        }

        _buffer = next;
        _head = 0;
    }
}
