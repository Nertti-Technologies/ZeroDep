using System;
using System.Collections.Generic;
using System.Text;
using ZeroDep.Abstractions;
using ZeroDep.Lexing;
using ZeroDep.Objects;

namespace ZeroDep.Content;

/// <summary>
/// How a glyph's character code was resolved to Unicode — the basis for the per-page text-decode trust
/// signal (ADR-0007). Authoritative means a reliable map said so; Fallback means we guessed a standard
/// encoding with no map; Unmapped means nothing usable (emitted empty / non-printable).
/// </summary>
internal enum DecodeTier
{
    /// <summary>A usable /ToUnicode map, a producer-specified /Differences name, or a declared standard /Encoding.</summary>
    Authoritative = 0,

    /// <summary>A blind standard-encoding guess (no /ToUnicode, no /Differences, no declared encoding).</summary>
    Fallback = 1,

    /// <summary>No usable mapping — emitted empty, or a control / non-printable code point.</summary>
    Unmapped = 2,
}

/// <summary>One decoded glyph: its Unicode text, advance width (in 1000-unit em), space flag, and decode tier.</summary>
internal readonly struct Glyph
{
    public Glyph(string text, double widthEm, bool isSpace, DecodeTier tier)
    {
        Text = text;
        WidthEm = widthEm;
        IsSpace = isSpace;
        Tier = tier;
    }

    public string Text { get; }
    public double WidthEm { get; }
    public bool IsSpace { get; }
    public DecodeTier Tier { get; }
}

/// <summary>
/// Decodes a font's shown-text bytes to Unicode and supplies glyph advance widths.
/// Uses /ToUnicode when present (simple and Type0), simple /Widths and Type0 /W,/DW for advances,
/// and a WinAnsi fallback for simple fonts without a ToUnicode map. ISO 32000-2 §9.6–9.10.
/// </summary>
internal sealed class FontInfo
{
    private readonly int _codeBytes;
    private readonly Dictionary<int, string>? _toUnicode;
    private readonly double[] _simpleWidths;
    private readonly int _firstChar;
    private readonly Dictionary<int, double>? _cidWidths;
    private readonly Dictionary<int, string>? _differences;
    private readonly double _defaultWidth;
    private readonly bool _namedBaseEncoding;
    private readonly bool _symbolic;

    public FontInfo(PdfDictionary dict, Func<PdfObject, PdfObject> resolve, Func<PdfStream, byte[]> decode)
    {
        bool isType0 = (dict["Subtype"] as PdfName)?.Value == "Type0";
        _codeBytes = isType0 ? 2 : 1;

        if (resolve(dict["ToUnicode"] ?? PdfNull.Instance) is PdfStream toUnicodeStream)
        {
            // A map that is malformed or exceeds Limits.MaxCMapEntries is dropped (decoding then falls back).
            try { _toUnicode = ToUnicodeCMap.Parse(decode(toUnicodeStream)); }
            catch { _toUnicode = null; }
        }

        if (isType0)
        {
            _defaultWidth = 1000;
            _simpleWidths = Array.Empty<double>();
            if (resolve(dict["DescendantFonts"] ?? PdfNull.Instance) is PdfArray descendants && descendants.Count > 0
                && resolve(descendants[0]) is PdfDictionary cidFont)
            {
                if (resolve(cidFont["DW"] ?? PdfNull.Instance) is PdfNumber dw) _defaultWidth = dw.AsDouble;
                if (resolve(cidFont["W"] ?? PdfNull.Instance) is PdfArray w)
                {
                    // A /W array that exceeds Limits.MaxCMapEntries is dropped (widths fall back to /DW).
                    try { _cidWidths = ParseCidWidths(resolve, w); }
                    catch (PdfSyntaxException) { _cidWidths = null; }
                }
            }
        }
        else
        {
            _defaultWidth = 500;
            // FirstChar is a byte code; clamp before the int cast so NaN or huge values cannot wrap.
            _firstChar = resolve(dict["FirstChar"] ?? PdfNull.Instance) is PdfNumber fc && !double.IsNaN(fc.AsDouble)
                ? (int)Math.Max(-65536.0, Math.Min(65536.0, fc.AsDouble))
                : 0;

            // FontDescriptor /Flags: Symbolic (bit 3 = 4) without Nonsymbolic (bit 6 = 32). A symbolic font's
            // codes index a custom built-in encoding, so a blind WinAnsi guess is unreliable (ADR-0007).
            if (resolve(dict["FontDescriptor"] ?? PdfNull.Instance) is PdfDictionary descriptor
                && resolve(descriptor["Flags"] ?? PdfNull.Instance) is PdfNumber flagsNum)
            {
                long flags = flagsNum.AsInt64;
                _symbolic = (flags & 4) != 0 && (flags & 32) == 0;
            }
            if (resolve(dict["Widths"] ?? PdfNull.Instance) is PdfArray widths)
            {
                _simpleWidths = new double[widths.Count];
                for (int i = 0; i < widths.Count; i++)
                {
                    _simpleWidths[i] = resolve(widths[i]) is PdfNumber n ? n.AsDouble : 0;
                }
            }
            else
            {
                _simpleWidths = Array.Empty<double>();
            }

            // /Encoding can be a standard encoding name, or a dictionary with /BaseEncoding + /Differences.
            PdfObject encodingObj = resolve(dict["Encoding"] ?? PdfNull.Instance);
            string? baseEncodingName = null;
            if (encodingObj is PdfName encName)
            {
                baseEncodingName = encName.Value;
            }
            else if (encodingObj is PdfDictionary encoding)
            {
                if (resolve(encoding["BaseEncoding"] ?? PdfNull.Instance) is PdfName baseName)
                {
                    baseEncodingName = baseName.Value;
                }

                // /Differences (used when there is no /ToUnicode map) — producer-specified glyph names.
                if (_toUnicode is null && resolve(encoding["Differences"] ?? PdfNull.Instance) is PdfArray differences)
                {
                    _differences = new Dictionary<int, string>();
                    int code = 0;
                    foreach (PdfObject item in differences.Items)
                    {
                        if (item is PdfInteger n) code = n.Value >= 0 && n.Value <= 255 ? (int)n.Value : 256;
                        else if (item is PdfName name) _differences[code++] = GlyphList.ToUnicode(name.Value);
                    }
                }
            }

            _namedBaseEncoding = baseEncodingName is "WinAnsiEncoding" or "MacRomanEncoding"
                or "StandardEncoding" or "PDFDocEncoding" or "MacExpertEncoding";
        }

        SpaceWidthEm = ComputeSpaceWidthEm();
    }

    /// <summary>Entries in the font's /ToUnicode and /W maps, for the caller's work budget.</summary>
    public long MapEntryCount => (long)(_toUnicode?.Count ?? 0) + (_cidWidths?.Count ?? 0);

    /// <summary>
    /// The advance width of the space character in em (1.0 = full em), used to detect inter-word gaps that
    /// are encoded positionally rather than as a space glyph (ADR-0008). Falls back to 0.25 when unknown.
    /// </summary>
    public double SpaceWidthEm { get; }

    private double ComputeSpaceWidthEm()
    {
        int spaceCode = -1;
        if (_codeBytes == 1)
        {
            spaceCode = 32;
        }
        else if (_toUnicode is not null)
        {
            foreach (KeyValuePair<int, string> kv in _toUnicode)
            {
                if (kv.Value == " ")
                {
                    spaceCode = kv.Key;
                    break;
                }
            }
        }

        if (spaceCode >= 0)
        {
            double em = WidthOf(spaceCode) / 1000.0;
            if (em > 0.01 && em < 1.0)
            {
                return em;
            }
        }

        return 0.25;
    }

    /// <summary>Decodes a shown-text byte string into glyphs.</summary>
    public List<Glyph> Decode(byte[] bytes)
    {
        var glyphs = new List<Glyph>();
        if (_codeBytes == 1)
        {
            foreach (byte b in bytes)
            {
                DecodeTier tier;
                string? text = Lookup(b);
                if (text is not null)
                {
                    tier = IsMeaningful(text) ? DecodeTier.Authoritative : DecodeTier.Unmapped;
                }
                else if (_differences is not null && _differences.TryGetValue(b, out string? diff))
                {
                    text = diff;
                    tier = IsMeaningful(diff) ? DecodeTier.Authoritative : DecodeTier.Unmapped;
                }
                else
                {
                    text = WinAnsiString(b);
                    if (text.Length == 0)
                    {
                        tier = DecodeTier.Unmapped;
                    }
                    else if (_namedBaseEncoding || !_symbolic)
                    {
                        // A declared standard /Encoding — or a non-symbolic font, for which a standard encoding
                        // is the correct default — is authoritative.
                        tier = DecodeTier.Authoritative;
                    }
                    else
                    {
                        // A symbolic font with no map/encoding: a blind WinAnsi guess is the wrong-decode risk.
                        tier = DecodeTier.Fallback;
                    }
                }

                glyphs.Add(new Glyph(text, WidthOf(b), b == 32, tier));
            }
        }
        else
        {
            for (int i = 0; i + 1 < bytes.Length; i += 2)
            {
                int code = (bytes[i] << 8) | bytes[i + 1];
                string? text = Lookup(code);
                DecodeTier tier = text is not null && IsMeaningful(text) ? DecodeTier.Authoritative : DecodeTier.Unmapped;
                glyphs.Add(new Glyph(text ?? string.Empty, WidthOf(code), false, tier));
            }
        }
        return glyphs;
    }

    private string? Lookup(int code)
        => _toUnicode is not null && _toUnicode.TryGetValue(code, out string? s) ? s : null;

    private double WidthOf(int code)
    {
        if (_codeBytes == 1)
        {
            int idx = code - _firstChar;
            if (idx >= 0 && idx < _simpleWidths.Length && _simpleWidths[idx] > 0) return _simpleWidths[idx];
            return _defaultWidth;
        }
        if (_cidWidths is not null && _cidWidths.TryGetValue(code, out double w)) return w;
        return _defaultWidth;
    }

    // CIDs are 0..65535; entries outside are ignored. Every entry written, including overwrites, counts
    // toward Limits.MaxCMapEntries, so repeated ranges cannot multiply the work.
    private static Dictionary<int, double> ParseCidWidths(Func<PdfObject, PdfObject> resolve, PdfArray w)
    {
        const long MaxCid = 0xFFFF;
        var map = new Dictionary<int, double>();
        long entries = 0;

        void Put(long cid, double width)
        {
            if (++entries > Limits.MaxCMapEntries)
            {
                throw new PdfSyntaxException($"Font /W array exceeds the limit of {Limits.MaxCMapEntries} entries.");
            }

            if (cid >= 0 && cid <= MaxCid) map[(int)cid] = width;
        }

        int i = 0;
        while (i < w.Count)
        {
            if (resolve(w[i]) is not PdfNumber first) { i++; continue; }
            if (i + 1 >= w.Count) break;

            PdfObject next = resolve(w[i + 1]);
            if (next is PdfArray array)
            {
                long c = first.AsInt64;
                for (int j = 0; j < array.Count; j++)
                {
                    if (resolve(array[j]) is PdfNumber wn) Put(c + j, wn.AsDouble);
                }
                i += 2;
            }
            else if (next is PdfNumber last && i + 2 < w.Count && resolve(w[i + 2]) is PdfNumber value)
            {
                // Clip the range to the CID space before iterating.
                long lo = Math.Max(first.AsInt64, 0);
                long hi = Math.Min(last.AsInt64, MaxCid);
                for (long c = lo; c <= hi; c++)
                {
                    Put(c, value.AsDouble);
                }
                i += 3;
            }
            else
            {
                i++;
            }
        }
        return map;
    }

    // A decoded string is "meaningful" if it carries at least one printable, non-replacement character.
    private static bool IsMeaningful(string text)
    {
        foreach (char c in text)
        {
            if (c >= 0x20 && c != '�')
            {
                return true;
            }
        }

        return false;
    }

    private static string WinAnsiString(int code)
    {
        char c = WinAnsiChar(code);
        return c == '\0' ? string.Empty : c.ToString();
    }

    private static char WinAnsiChar(int code)
    {
        if (code >= 0x20 && code <= 0x7E) return (char)code;
        if (code >= 0xA0 && code <= 0xFF) return (char)code;
        switch (code)
        {
            case 0x85: return '…';
            case 0x91: return '‘';
            case 0x92: return '’';
            case 0x93: return '“';
            case 0x94: return '”';
            case 0x95: return '•';
            case 0x96: return '–';
            case 0x97: return '—';
            case 0x99: return '™';
            default: return '\0';
        }
    }
}

/// <summary>Parses a /ToUnicode CMap (bfchar/bfrange) into a code → Unicode map (ISO 32000-2 §9.10.3).</summary>
internal static class ToUnicodeCMap
{
    /// <summary>
    /// Parses the map. Throws <see cref="PdfSyntaxException"/> when it writes more than
    /// <see cref="Limits.MaxCMapEntries"/> entries (overwrites included).
    /// </summary>
    public static Dictionary<int, string> Parse(byte[] data)
    {
        var map = new CountedMap();
        var lexer = new PdfLexer(data, 0, data.Length);
        while (true)
        {
            Token token = lexer.Next();
            if (token.Type == TokenType.Eof) break;
            if (token.Type != TokenType.Keyword) continue;
            if (token.Text == "beginbfchar") ParseBfChar(lexer, map);
            else if (token.Text == "beginbfrange") ParseBfRange(lexer, map);
        }
        return map.Map;
    }

    // Counts every write so that the total, not only the distinct codes, is bounded.
    private sealed class CountedMap
    {
        private long _writes;

        public Dictionary<int, string> Map { get; } = new Dictionary<int, string>();

        public string this[int code]
        {
            set
            {
                if (++_writes > Limits.MaxCMapEntries)
                {
                    throw new PdfSyntaxException($"ToUnicode CMap exceeds the limit of {Limits.MaxCMapEntries} entries.");
                }

                Map[code] = value;
            }
        }
    }

    private static void ParseBfChar(PdfLexer lexer, CountedMap map)
    {
        while (true)
        {
            Token src = lexer.Next();
            if (src.Type != TokenType.HexString) break;
            Token dst = lexer.Next();
            if (dst.Type != TokenType.HexString) break;
            map[ToInt(src.Bytes!)] = Utf16(dst.Bytes!);
        }
    }

    private static void ParseBfRange(PdfLexer lexer, CountedMap map)
    {
        while (true)
        {
            Token lo = lexer.Next();
            if (lo.Type != TokenType.HexString) break;
            Token hi = lexer.Next();
            if (hi.Type != TokenType.HexString) break;
            Token dst = lexer.Next();

            int loCode = ToInt(lo.Bytes!);
            int hiCode = ToInt(hi.Bytes!);
            if (dst.Type == TokenType.HexString)
            {
                for (int c = loCode; c <= hiCode && c - loCode < 65536; c++)
                {
                    map[c] = Utf16Increment(dst.Bytes!, c - loCode);
                }
            }
            else if (dst.Type == TokenType.ArrayStart)
            {
                int c = loCode;
                while (true)
                {
                    Token item = lexer.Next();
                    if (item.Type == TokenType.ArrayEnd || item.Type == TokenType.Eof) break;
                    if (item.Type == TokenType.HexString) map[c++] = Utf16(item.Bytes!);
                }
            }
            else
            {
                break;
            }
        }
    }

    private static int ToInt(byte[] bytes)
    {
        int v = 0;
        foreach (byte b in bytes) v = (v << 8) | b;
        return v;
    }

    private static string Utf16(byte[] bytes)
        => bytes.Length >= 2 ? Encoding.BigEndianUnicode.GetString(bytes, 0, bytes.Length & ~1) : string.Empty;

    private static string Utf16Increment(byte[] baseBytes, int offset)
    {
        if (baseBytes.Length < 2) return Utf16(baseBytes);
        var copy = (byte[])baseBytes.Clone();
        int last = (copy[copy.Length - 2] << 8) | copy[copy.Length - 1];
        last += offset;
        copy[copy.Length - 2] = (byte)((last >> 8) & 0xFF);
        copy[copy.Length - 1] = (byte)(last & 0xFF);
        return Utf16(copy);
    }
}
