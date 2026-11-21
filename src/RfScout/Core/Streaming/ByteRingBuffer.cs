using System.Numerics;

namespace RfScout.Core.Streaming;

/// <summary>
/// Single-producer single-consumer byte ring buffer, sized to a power of two so the wrap is a
/// mask instead of a division.
///
/// The producer is the libhackrf RX callback, which must return immediately and must not
/// allocate, so <see cref="Write"/> only does two bounds-checked copies and one interlocked
/// publish. The consumer is the DSP worker thread, which reads whole chunks.
///
/// When the consumer falls behind, whole incoming chunks are dropped rather than overwriting
/// part of what is still queued: a sweep block that arrives in pieces frames wrongly, whereas a
/// missing block only loses one sweep and is counted in <see cref="DroppedBytes"/> for the
/// status bar.
/// </summary>
public sealed class ByteRingBuffer
{
    private readonly byte[] _buffer;
    private readonly int _mask;

    // Written only by the producer, read by the consumer. Interlocked/Volatile on 32-bit CLR
    // guarantees the 64-bit counters are read atomically.
    private long _head;
    private long _tail;
    private long _droppedBytes;
    private long _droppedChunks;

    /// <summary>Create a buffer whose capacity is <paramref name="capacityBytes"/> rounded up to
    /// a power of two. Sweep streams want a whole number of 16384-byte blocks, several
    /// transfers deep, so a slow DSP thread costs sweeps rather than crashes the callback.</summary>
    public ByteRingBuffer(int capacityBytes)
    {
        uint capacity = BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, capacityBytes));
        _buffer = new byte[capacity];
        _mask = unchecked((int)capacity) - 1;
    }

    /// <summary>Usable capacity in bytes, always a power of two.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Bytes the producer could not queue because the consumer was behind.</summary>
    public long DroppedBytes => Interlocked.Read(ref _droppedBytes);

    /// <summary>Incoming chunks discarded whole. At 20 Msps one 262144-byte chunk is 6.5 ms of RF.</summary>
    public long DroppedChunks => Interlocked.Read(ref _droppedChunks);

    /// <summary>Bytes queued and waiting for the consumer.</summary>
    public int BytesAvailable
    {
        get
        {
            long head = Volatile.Read(ref _head);
            long tail = Volatile.Read(ref _tail);
            return (int)(head - tail);
        }
    }

    /// <summary>
    /// Queue <paramref name="data"/>. Returns false and counts a drop when the chunk is larger
    /// than the whole buffer or the buffer cannot hold it right now, which keeps queued data
    /// block-aligned for the sweep framer.
    /// </summary>
    public bool Write(ReadOnlySpan<byte> data)
    {
        long head = Volatile.Read(ref _head);
        long tail = Volatile.Read(ref _tail);
        int free = (int)((tail - head) + Capacity);

        if (data.Length == 0)
        {
            return true;
        }

        if (data.Length > free)
        {
            Interlocked.Add(ref _droppedBytes, data.Length);
            Interlocked.Increment(ref _droppedChunks);
            return false;
        }

        // Two copies at most: up to the end of the array, then the remainder at the start.
        int offset = (int)(head & _mask);
        int first = Math.Min(data.Length, Capacity - offset);
        data[..first].CopyTo(_buffer.AsSpan(offset));
        if (first < data.Length)
        {
            data[first..].CopyTo(_buffer.AsSpan(0));
        }

        // Publish only after the bytes are in place, so the consumer never sees a partial chunk.
        Interlocked.Add(ref _head, data.Length);
        return true;
    }

    /// <summary>
    /// Copy out at most <paramref name="destination"/>.Length queued bytes. Returns the number
    /// copied, zero when empty.
    /// </summary>
    public int Read(Span<byte> destination)
    {
        long head = Volatile.Read(ref _head);
        long tail = Volatile.Read(ref _tail);
        int used = (int)(head - tail);
        int take = Math.Min(used, destination.Length);
        if (take == 0)
        {
            return 0;
        }

        int offset = (int)(tail & _mask);
        int first = Math.Min(take, Capacity - offset);
        _buffer.AsSpan(offset, first).CopyTo(destination);
        if (first < take)
        {
            _buffer.AsSpan(0, take - first).CopyTo(destination[first..]);
        }

        Interlocked.Add(ref _tail, take);
        return take;
    }

    /// <summary>Discard everything queued (call when restarting a stream, so stale bytes from the
    /// previous one are not framed into the new sweep).</summary>
    public void Reset()
    {
        long drain = Volatile.Read(ref _head);
        _buffer.AsSpan().Clear();
        Interlocked.Exchange(ref _tail, drain);
    }
}
