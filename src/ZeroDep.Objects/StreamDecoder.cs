using System;
using System.Collections.Generic;
using ZeroDep.Abstractions;
using ZeroDep.Filters;

namespace ZeroDep.Objects;

/// <summary>
/// Applies the filter chain needed to read *structural* streams (cross-reference and
/// object streams). Supports FlateDecode (with predictors) and unfiltered data; other
/// filters are rejected here because structural streams never use them.
/// </summary>
internal static class StreamDecoder
{
    /// <summary>Decodes a stream by applying its <c>/Filter</c> chain, within the default limits.</summary>
    public static byte[] Decode(PdfStream stream) => Decode(stream, PdfLimits.Default);

    /// <summary>Decodes a stream by applying its <c>/Filter</c> chain, within <paramref name="limits"/>.</summary>
    /// <exception cref="PdfLimitExceededException">The decoded output exceeds <see cref="PdfLimits.MaxDecodedStreamBytes"/>.</exception>
    /// <exception cref="PdfSyntaxException">The stream cannot be decoded.</exception>
    public static byte[] Decode(PdfStream stream, PdfLimits limits)
    {
        byte[] data = stream.GetRawBytes();
        PdfDictionary dict = stream.Dictionary;

        PdfObject? filter = dict["Filter"];
        if (filter is null) return data;

        List<string> filters = NamesOf(filter);
        List<PdfObject?> parms = ParmsOf(dict["DecodeParms"] ?? dict["DP"], filters.Count);

        try
        {
            for (int i = 0; i < filters.Count; i++)
            {
                data = ApplyOne(filters[i], parms[i], data, limits);
            }
            return data;
        }
        catch (PdfSyntaxException)
        {
            throw;
        }
        catch (Exception ex) when (ResourceBudget.AsLimitExceeded(ex) is { } limit)
        {
            // A public decoder reports a breach as InvalidDataException wrapping the PdfLimitExceededException;
            // the document pipeline reports it under its own type again, naming the same limit.
            throw new PdfLimitExceededException(limit.LimitName, limit.Limit, limit.Message, ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A stream that won't decode (corrupt, or still-encrypted after failed auth) is a
            // structural failure, surfaced as the handled exception type rather than a raw crash.
            throw new PdfSyntaxException("Failed to decode a structural stream.", ex);
        }
    }

    private static byte[] ApplyOne(string filter, PdfObject? parm, byte[] data, PdfLimits limits)
    {
        switch (filter)
        {
            case "FlateDecode":
            case "Fl":
            {
                var p = parm as PdfDictionary;
                int predictor = GetInt(p, "Predictor", 1);
                int colors = GetInt(p, "Colors", 1);
                int bitsPerComponent = GetInt(p, "BitsPerComponent", 8);
                int columns = GetInt(p, "Columns", 1);
                return FlateDecode.Decode(data, limits, predictor, colors, bitsPerComponent, columns);
            }
            case "LZWDecode":
            case "LZW":
            {
                var p = parm as PdfDictionary;
                int predictor = GetInt(p, "Predictor", 1);
                int colors = GetInt(p, "Colors", 1);
                int bitsPerComponent = GetInt(p, "BitsPerComponent", 8);
                int columns = GetInt(p, "Columns", 1);
                int earlyChange = GetInt(p, "EarlyChange", 1);
                return LzwDecode.Decode(data, limits, predictor, colors, bitsPerComponent, columns, earlyChange);
            }
            case "ASCII85Decode":
            case "A85":
                return Ascii85Decode.Decode(data, limits);
            case "ASCIIHexDecode":
            case "AHx":
                return AsciiHexDecode.Decode(data, limits);
            case "RunLengthDecode":
            case "RL":
                return RunLengthDecode.Decode(data, limits);
            default:
                throw new PdfSyntaxException($"Unsupported filter '{filter}' on a structural stream.");
        }
    }

    private static List<string> NamesOf(PdfObject filter)
    {
        var names = new List<string>();
        if (filter is PdfName name)
        {
            names.Add(name.Value);
        }
        else if (filter is PdfArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                if (array[i] is PdfName n) names.Add(n.Value);
            }
        }
        return names;
    }

    private static List<PdfObject?> ParmsOf(PdfObject? parms, int count)
    {
        var list = new List<PdfObject?>(count);
        if (parms is PdfArray array)
        {
            for (int i = 0; i < count; i++) list.Add(i < array.Count ? array[i] : null);
        }
        else
        {
            for (int i = 0; i < count; i++) list.Add(i == 0 ? parms : null);
        }
        return list;
    }

    private static int GetInt(PdfDictionary? dict, string key, int fallback)
    {
        if (dict is null) return fallback;
        return dict[key] is PdfInteger i ? (int)i.Value : fallback;
    }
}
