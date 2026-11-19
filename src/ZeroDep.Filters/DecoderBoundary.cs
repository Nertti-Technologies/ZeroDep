using System;
using System.Collections.Generic;
using System.IO;

namespace ZeroDep.Filters;

/// <summary>
/// The exception boundary of the public decoders (ADR-0010 §5.2 item 7): any failure caused by the input
/// surfaces as <see cref="InvalidDataException"/>, apart from the documented
/// <see cref="NotSupportedException"/>. Out-of-memory is never translated.
/// </summary>
internal static class DecoderBoundary
{
    public static T Run<T>(Func<T> decode, string what)
    {
        try
        {
            return decode();
        }
        catch (Exception ex) when (ShouldTranslate(ex))
        {
            throw new InvalidDataException($"The {what} data could not be decoded.", ex);
        }
    }

    /// <summary>Whether <paramref name="ex"/> is a content failure that must become <see cref="InvalidDataException"/>.</summary>
    public static bool ShouldTranslate(Exception ex)
        => ex is not InvalidDataException && ex is not NotSupportedException && ex is not OutOfMemoryException;

    /// <summary>Whether a decoder that isolates one failing part (a tile, a segment) may swallow <paramref name="ex"/>.</summary>
    public static bool IsIsolatable(Exception ex) => ex is not OutOfMemoryException;
}

/// <summary>
/// A growing output buffer that refuses to exceed <see cref="Limits.MaxDecodedStreamBytes"/>. It grows in
/// chunks (doubling up to 1 MiB) instead of re-allocating, so the peak is the data plus one final copy.
/// </summary>
internal sealed class BoundedOutput
{
    private const int MaxChunk = 1 << 20;

    private readonly List<byte[]> _full = new List<byte[]>();
    private readonly long _limit;
    private byte[] _current;
    private int _used;
    private long _length;

    public BoundedOutput(int sizeHint, long limit = Limits.MaxDecodedStreamBytes)
    {
        _limit = limit;
        _current = new byte[Math.Min(Math.Max(sizeHint, 256), MaxChunk)];
    }

    public long Length => _length;

    public void Write(byte[] data, int offset, int count)
    {
        Ensure(count);
        while (count > 0)
        {
            int n = Math.Min(count, Room());
            Buffer.BlockCopy(data, offset, _current, _used, n);
            _used += n;
            offset += n;
            count -= n;
        }
    }

    public void WriteByte(byte value) => WriteRepeated(value, 1);

    /// <summary>Writes <paramref name="value"/> <paramref name="count"/> times.</summary>
    public void WriteRepeated(byte value, int count)
    {
        Ensure(count);
        while (count > 0)
        {
            int n = Math.Min(count, Room());
            for (int i = 0; i < n; i++) _current[_used + i] = value;
            _used += n;
            count -= n;
        }
    }

    public byte[] ToArray()
    {
        var result = new byte[_length];
        int pos = 0;
        foreach (byte[] chunk in _full)
        {
            Buffer.BlockCopy(chunk, 0, result, pos, chunk.Length);
            pos += chunk.Length;
        }

        Buffer.BlockCopy(_current, 0, result, pos, _used);
        return result;
    }

    // Free space in the current chunk, starting a new chunk when it is full.
    private int Room()
    {
        if (_used == _current.Length)
        {
            _full.Add(_current);
            _current = new byte[Math.Min(_current.Length * 2, MaxChunk)];
            _used = 0;
        }

        return _current.Length - _used;
    }

    private void Ensure(int count)
    {
        if (_length + count > _limit)
        {
            throw new InvalidDataException($"Decoded data exceeds the limit of {_limit / (1024 * 1024)} MiB.");
        }

        _length += count;
    }
}

/// <summary>
/// A pixel allowance shared by every bitmap one decode creates or composites (page, regions, symbols), so
/// that the total, not just each single allocation, stays within <see cref="Limits.MaxImagePixels"/>.
/// </summary>
internal sealed class PixelBudget
{
    private long _remaining;

    public PixelBudget(long pixels) => _remaining = pixels;

    /// <summary>Charges <paramref name="width"/> x <paramref name="height"/> pixels (negative sizes count as 0).</summary>
    /// <exception cref="InvalidDataException">The allowance is exhausted.</exception>
    public void Charge(long width, long height, string what)
    {
        long pixels = Math.Max(0, width) * Math.Max(0, height);
        if (width > int.MaxValue || height > int.MaxValue || pixels > _remaining)
        {
            _remaining = 0;
            throw new InvalidDataException($"{what} of {width}x{height} exceeds the {Limits.MaxImagePixels} pixel limit.");
        }

        _remaining -= pixels;
    }
}
