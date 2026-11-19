using System;
using System.Collections.Generic;
using System.IO;
using ZeroDep.Abstractions;

namespace ZeroDep.Filters;

/// <summary>
/// The exception boundary of the public decoders (ADR-0010 §5.2 item 7): any failure caused by the input
/// surfaces as <see cref="InvalidDataException"/>, apart from the documented
/// <see cref="NotSupportedException"/>. Out-of-memory is never translated.
/// </summary>
/// <remarks>
/// Inside the decoders a limit breach is thrown as <see cref="PdfLimitExceededException"/>. At this boundary
/// it becomes an <see cref="InvalidDataException"/> with the same message whose
/// <see cref="Exception.InnerException"/> is that <see cref="PdfLimitExceededException"/>, so the public
/// decoders keep their documented exception type while the document pipeline can still report the breach
/// under its own type.
/// </remarks>
internal static class DecoderBoundary
{
    /// <summary>
    /// The working memory an image decode may use, in bytes per pixel of <see cref="PdfLimits.MaxImagePixels"/>: 4,
    /// so the default 2^28 pixels gives the 1 GiB per-image target of ADR-0010 (§5.1: up to 4 bytes per pixel).
    /// </summary>
    public const long WorkingSetBytesPerPixel = 4;

    public static T Run<T>(Func<T> decode, string what)
    {
        try
        {
            return decode();
        }
        catch (PdfLimitExceededException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
        catch (Exception ex) when (ShouldTranslate(ex))
        {
            throw new InvalidDataException($"The {what} data could not be decoded.", ex);
        }
    }

    /// <summary>Whether <paramref name="ex"/> is a content failure that must become <see cref="InvalidDataException"/>.</summary>
    public static bool ShouldTranslate(Exception ex)
        => ex is not InvalidDataException && ex is not NotSupportedException && ex is not OutOfMemoryException;

    /// <summary>
    /// Whether a decoder that isolates one failing part (a tile, a segment) may swallow <paramref name="ex"/>. A part
    /// that exceeds a per-part count or overdraws the decode's pixel allowance is skipped like a malformed one; the
    /// size checks made before the decode starts are not inside such a part, so they always surface.
    /// </summary>
    public static bool IsIsolatable(Exception ex) => ex is not OutOfMemoryException;

    /// <summary>The working-memory budget of one image decode under <paramref name="limits"/>, saturating at <see cref="long.MaxValue"/>.</summary>
    public static long WorkingSetBytes(PdfLimits limits)
        => limits.MaxImagePixels > long.MaxValue / WorkingSetBytesPerPixel ? long.MaxValue : limits.MaxImagePixels * WorkingSetBytesPerPixel;

    /// <summary>The breach of <see cref="PdfLimits.MaxImagePixels"/> by an image of <paramref name="width"/> x <paramref name="height"/>.</summary>
    public static PdfLimitExceededException PixelLimit(string what, long width, long height, PdfLimits limits)
        => new PdfLimitExceededException(
            nameof(PdfLimits.MaxImagePixels),
            limits.MaxImagePixels,
            $"{what} of {width}x{height} exceeds the {limits.MaxImagePixels} pixel limit.");

    /// <summary>The breach of the working-memory budget derived from <see cref="PdfLimits.MaxImagePixels"/>.</summary>
    public static PdfLimitExceededException WorkingSetLimit(string what, PdfLimits limits)
        => new PdfLimitExceededException(
            nameof(PdfLimits.MaxImagePixels),
            limits.MaxImagePixels,
            $"{what} exceeds the decode memory budget of {WorkingSetBytes(limits)} bytes ({WorkingSetBytesPerPixel} bytes per pixel of the {limits.MaxImagePixels} pixel limit).");

    /// <summary>The breach of a fixed structural limit of a decoder, such as a symbol count.</summary>
    public static PdfLimitExceededException FixedLimit(string name, long limit, string message)
        => new PdfLimitExceededException(name, limit, message);
}

/// <summary>
/// A growing output buffer that refuses to exceed <see cref="PdfLimits.MaxDecodedStreamBytes"/>. It grows in
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

    public BoundedOutput(long sizeHint, PdfLimits limits)
    {
        _limit = limits.MaxDecodedStreamBytes;
        _current = new byte[(int)Math.Min(Math.Min(Math.Max(sizeHint, 256), MaxChunk), Math.Max(_limit, 1))];
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
            throw new PdfLimitExceededException(
                nameof(PdfLimits.MaxDecodedStreamBytes),
                _limit,
                $"Decoded data exceeds the limit of {_limit} bytes.");
        }

        _length += count;
    }
}

/// <summary>
/// A pixel allowance shared by every bitmap one decode creates or composites (page, regions, symbols), so
/// that the total, not just each single allocation, stays within a multiple of <see cref="PdfLimits.MaxImagePixels"/>.
/// </summary>
internal sealed class PixelBudget
{
    private readonly PdfLimits _limits;
    private long _remaining;

    public PixelBudget(long pixels, PdfLimits limits)
    {
        _remaining = pixels;
        _limits = limits;
    }

    /// <summary>Charges <paramref name="width"/> x <paramref name="height"/> pixels (negative sizes count as 0).</summary>
    /// <exception cref="PdfLimitExceededException">The allowance is exhausted.</exception>
    public void Charge(long width, long height, string what)
    {
        long pixels = Math.Max(0, width) * Math.Max(0, height);
        if (width > int.MaxValue || height > int.MaxValue || pixels > _remaining)
        {
            _remaining = 0;
            throw DecoderBoundary.PixelLimit(what, width, height, _limits);
        }

        _remaining -= pixels;
    }
}
