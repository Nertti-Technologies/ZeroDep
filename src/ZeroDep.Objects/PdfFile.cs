using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ZeroDep.Abstractions;
using ZeroDep.IO;
using ZeroDep.Lexing;
using ZeroDep.Security;

namespace ZeroDep.Objects;

/// <summary>
/// A low-level opened PDF file: resolves the cross-reference (classic tables, xref streams,
/// object streams, and <c>/Prev</c>/hybrid chains) and materializes indirect objects on demand.
/// </summary>
internal sealed class PdfFile : IDisposable
{
    private static readonly byte[] StartXrefKeyword = Encoding.ASCII.GetBytes("startxref");
    private static readonly byte[] TrailerKeyword = Encoding.ASCII.GetBytes("trailer");
    private static readonly byte[] XrefKeyword = Encoding.ASCII.GetBytes("xref");
    private static readonly byte[] EndObjKeyword = Encoding.ASCII.GetBytes("endobj");

    private readonly PdfByteSource _source;
    private readonly Dictionary<int, PdfObject> _objectCache = new Dictionary<int, PdfObject>();
    private readonly Dictionary<int, ObjStmData?> _objectStreamCache = new Dictionary<int, ObjStmData?>();

    // Objects being materialized right now. An object stream that (directly or through another object
    // stream) contains itself would otherwise recurse until the stack overflows.
    private readonly HashSet<int> _loading = new HashSet<int>();

    private Dictionary<int, XrefEntry>? _xref;
    private PdfDictionary? _trailer;
    private StandardSecurityHandler? _handler;
    private int _encryptObjectNumber = -1;

    private readonly ResourceBudget _budget;

    private PdfFile(PdfByteSource source, ResourceBudget budget)
    {
        _source = source;
        _budget = budget;
    }

    /// <summary>The per-document limits and work counter, shared by every layer that reads this file.</summary>
    public ResourceBudget Budget => _budget;

    /// <summary>The document trailer (newest), providing <c>/Root</c>, <c>/Size</c>, etc.</summary>
    public PdfDictionary Trailer => _trailer!;

    /// <summary>The set of object numbers known to the cross-reference.</summary>
    public IReadOnlyCollection<int> KnownObjectNumbers => _xref!.Keys;

    /// <summary>Whether the document declares encryption.</summary>
    public bool IsEncrypted { get; private set; }

    /// <summary>Whether the encryption handler is supported (Standard).</summary>
    public bool HandlerSupported { get; private set; } = true;

    /// <summary>The cipher in effect.</summary>
    public EncryptionAlgorithm Algorithm { get; private set; } = EncryptionAlgorithm.None;

    /// <summary>The security-handler revision.</summary>
    public int EncryptionRevision { get; private set; }

    /// <summary>Which password authenticated, or Failed / NotRequired.</summary>
    public AuthenticationResult Authentication { get; private set; } = AuthenticationResult.NotRequired;

    /// <summary>Whether metadata is encrypted.</summary>
    public bool EncryptMetadata { get; private set; } = true;

    /// <summary>The permission flags (<c>/P</c>).</summary>
    public int Permissions { get; private set; }

    /// <summary>Opens a PDF from a stream, resolves its cross-reference, and configures decryption.</summary>
    public static PdfFile Open(Stream stream, string? password = null) => Open(stream, password, PdfLimits.Default);

    /// <summary>
    /// Opens a PDF from a stream within <paramref name="limits"/>, resolves its cross-reference, and configures
    /// decryption. The file owns a new <see cref="ResourceBudget"/> for these limits.
    /// </summary>
    /// <exception cref="PdfLimitExceededException">The file exceeds a limit while its structure is read.</exception>
    /// <exception cref="PdfSyntaxException">The file structure cannot be read.</exception>
    public static PdfFile Open(Stream stream, string? password, PdfLimits limits)
    {
        if (limits is null) throw new ArgumentNullException(nameof(limits));
        var budget = new ResourceBudget(limits);
        var source = PdfByteSource.Create(stream);
        try
        {
            var file = new PdfFile(source, budget);
            file.BuildCrossReference();
            file.SetupEncryption(password);
            return file;
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            source.Dispose();
            throw new PdfSyntaxException("The file structure could not be read.", ex);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether <paramref name="ex"/> should surface as a <see cref="PdfSyntaxException"/>: every failure
    /// caused by the file's content does, while out-of-memory is left to the host.
    /// </summary>
    internal static bool IsTranslatable(Exception ex) => ex is not PdfSyntaxException && ex is not OutOfMemoryException;

    /// <summary>Resolves <paramref name="value"/> if it is an indirect reference; otherwise returns it unchanged.</summary>
    public PdfObject Resolve(PdfObject value)
        => value is PdfReference reference ? GetObject(reference.ObjectNumber) : value;

    /// <summary>Materializes the object with the given number, or <see cref="PdfNull"/> if unknown/free.</summary>
    /// <exception cref="PdfSyntaxException">The object is malformed or exceeds a structural limit.</exception>
    public PdfObject GetObject(int number)
    {
        if (_objectCache.TryGetValue(number, out var cached)) return cached;

        // A reference back to an object that is still being loaded is a cycle (for example an object
        // stream listed as compressed inside itself). It resolves to null and is not cached, so the
        // outer load still completes normally.
        if (!_loading.Add(number)) return PdfNull.Instance;
        try
        {
            PdfObject result = PdfNull.Instance;
            if (_xref!.TryGetValue(number, out var entry))
            {
                switch (entry.Type)
                {
                    case XrefEntryType.InUse:
                        result = LoadObjectAt(entry.Field2);
                        break;
                    case XrefEntryType.Compressed:
                        result = entry.Field2 >= 0 && entry.Field2 <= int.MaxValue
                            ? LoadFromObjectStream((int)entry.Field2, entry.Field3)
                            : PdfNull.Instance;
                        break;
                    case XrefEntryType.Free:
                    default:
                        result = PdfNull.Instance;
                        break;
                }
            }

            _objectCache[number] = result;
            return result;
        }
        catch (Exception ex) when (IsTranslatable(ex))
        {
            throw new PdfSyntaxException($"Object {number} could not be read.", ex);
        }
        finally
        {
            _loading.Remove(number);
        }
    }

    private void BuildCrossReference()
    {
        long start = FindStartXref();
        var table = new Dictionary<int, XrefEntry>();
        PdfDictionary? trailer = null;
        var visited = new HashSet<long>();

        long? next = start;
        while (next.HasValue && next.Value >= 0 && next.Value < _source.Length && visited.Add(next.Value))
        {
            XrefSection section = ReadSectionAt(next.Value);
            Merge(table, section.Entries);
            trailer ??= section.Trailer;

            // An /XRefStm outside the file is ignored, exactly like an out-of-range /Prev.
            if (section.XRefStm.HasValue && section.XRefStm.Value >= 0 && section.XRefStm.Value < _source.Length
                && visited.Add(section.XRefStm.Value))
            {
                XrefSection hybrid = ReadSectionAt(section.XRefStm.Value);
                Merge(table, hybrid.Entries);
            }

            next = section.Prev;
        }

        if (trailer is null) throw new PdfSyntaxException("No trailer dictionary was found.");
        _xref = table;
        _trailer = trailer;
    }

    private static void Merge(Dictionary<int, XrefEntry> target, Dictionary<int, XrefEntry> source)
    {
        // Newest entries are merged first; never overwrite an already-known object.
        foreach (var pair in source)
        {
            if (!target.ContainsKey(pair.Key)) target[pair.Key] = pair.Value;
        }
    }

    private long FindStartXref()
    {
        long pos = _source.LastIndexOf(StartXrefKeyword, _source.Length, maxScan: 2048);
        if (pos < 0) throw new PdfSyntaxException("Missing 'startxref'.");

        int windowLen = (int)Math.Min(2048, _source.Length - pos);
        byte[] window = _source.ReadBytes(pos, windowLen);
        var lexer = new PdfLexer(window, 0, window.Length);
        lexer.Next(); // 'startxref'
        Token offset = lexer.Next();
        if (offset.Type != TokenType.Integer || offset.IntValue < 0 || offset.IntValue >= _source.Length)
        {
            throw new PdfSyntaxException("Invalid 'startxref' offset.");
        }
        return offset.IntValue;
    }

    private XrefSection ReadSectionAt(long position)
    {
        int peekLen = (int)Math.Min(16, _source.Length - position);
        byte[] peek = _source.ReadBytes(position, peekLen);
        var lexer = new PdfLexer(peek, 0, peek.Length);
        Token first = lexer.Next();
        return first.Type == TokenType.Keyword && first.Text == "xref"
            ? ReadClassicSection(position)
            : ReadXrefStreamSection(position);
    }

    private XrefSection ReadClassicSection(long position)
    {
        long trailerPos = _source.IndexOf(TrailerKeyword, position);
        if (trailerPos < 0) throw new PdfSyntaxException("Classic cross-reference table without a trailer.");
        if (trailerPos - position > MaxObjectWindow)
        {
            throw WindowLimit($"Classic cross-reference table at offset {position}");
        }

        byte[] tableBytes = _source.ReadBytes(position, (int)(trailerPos - position));
        var lexer = new PdfLexer(tableBytes, 0, tableBytes.Length);
        lexer.Next(); // 'xref'

        var entries = new Dictionary<int, XrefEntry>();
        while (true)
        {
            Token startToken = lexer.Next();
            if (startToken.Type != TokenType.Integer) break;
            Token countToken = lexer.Next();
            if (countToken.Type != TokenType.Integer) throw new PdfSyntaxException("Malformed xref subsection header.");

            long startNumber = startToken.IntValue;
            long count = countToken.IntValue;
            for (long i = 0; i < count; i++)
            {
                Token offsetToken = lexer.Next();
                Token genToken = lexer.Next();
                Token typeToken = lexer.Next();
                if (offsetToken.Type != TokenType.Integer || genToken.Type != TokenType.Integer || typeToken.Type != TokenType.Keyword)
                {
                    throw new PdfSyntaxException("Malformed xref entry.");
                }

                int objectNumber = CheckedObjectNumber(startNumber + i);
                if (entries.ContainsKey(objectNumber)) continue;
                entries[objectNumber] = typeToken.Text == "n"
                    ? XrefEntry.InUse(offsetToken.IntValue, CheckedGeneration(genToken.IntValue))
                    : XrefEntry.Free();
            }
        }

        byte[] trailerWindow = _source.ReadBytes(trailerPos, (int)Math.Min(8192, _source.Length - trailerPos));
        var trailerLexer = new PdfLexer(trailerWindow, 0, trailerWindow.Length);
        trailerLexer.Next(); // 'trailer'
        PdfObject trailerObj = new PdfObjectParser(trailerLexer).ParseValue();
        if (trailerObj is not PdfDictionary trailer) throw new PdfSyntaxException("Trailer is not a dictionary.");

        return new XrefSection(entries, trailer, GetIntOrNull(trailer, "Prev"), GetIntOrNull(trailer, "XRefStm"));
    }

    private XrefSection ReadXrefStreamSection(long position)
    {
        if (LoadObjectAt(position) is not PdfStream stream) throw new PdfSyntaxException("Expected a cross-reference stream.");

        PdfDictionary dict = stream.Dictionary;
        byte[] data = StreamDecoder.Decode(stream, _budget.Limits);

        if (dict["W"] is not PdfArray widths || widths.Count < 3) throw new PdfSyntaxException("Cross-reference stream missing /W.");
        int w0 = WidthAt(widths, 0);
        int w1 = WidthAt(widths, 1);
        int w2 = WidthAt(widths, 2);
        if (w0 < 0 || w0 > Limits.MaxXrefFieldWidth || w1 < 0 || w1 > Limits.MaxXrefFieldWidth
            || w2 < 0 || w2 > Limits.MaxXrefFieldWidth)
        {
            throw new PdfSyntaxException($"Cross-reference stream /W entries must each be 0..{Limits.MaxXrefFieldWidth}.");
        }
        int recordLength = w0 + w1 + w2;
        if (recordLength <= 0) throw new PdfSyntaxException("Invalid /W widths in cross-reference stream.");

        var subsections = new List<long[]>();
        if (dict["Index"] is PdfArray index)
        {
            for (int i = 0; i + 1 < index.Count; i += 2)
            {
                subsections.Add(new[] { AsLong(index[i]), AsLong(index[i + 1]) });
            }
        }
        else
        {
            subsections.Add(new[] { 0L, GetIntOrNull(dict, "Size") ?? 0L });
        }

        var entries = new Dictionary<int, XrefEntry>();
        int pos = 0;
        foreach (long[] sub in subsections)
        {
            long startNumber = sub[0];
            long count = sub[1];
            for (long i = 0; i < count && pos + recordLength <= data.Length; i++)
            {
                long type = w0 == 0 ? 1 : ReadBigEndian(data, pos, w0);
                pos += w0;
                long field2 = ReadBigEndian(data, pos, w1);
                pos += w1;
                long field3 = ReadBigEndian(data, pos, w2);
                pos += w2;

                int objectNumber = CheckedObjectNumber(startNumber + i);
                if (entries.ContainsKey(objectNumber)) continue;
                switch (type)
                {
                    case 1:
                        entries[objectNumber] = XrefEntry.InUse(field2, CheckedGeneration(field3));
                        break;
                    case 2:
                        // field3 is the index inside the object stream; out-of-range values resolve to null later.
                        entries[objectNumber] = XrefEntry.Compressed(field2, field3 >= 0 && field3 <= int.MaxValue ? (int)field3 : -1);
                        break;
                    default:
                        entries[objectNumber] = XrefEntry.Free();
                        break;
                }
            }
        }

        return new XrefSection(entries, dict, GetIntOrNull(dict, "Prev"), null);
    }

    private PdfObject LoadObjectAt(long offset)
    {
        if (offset < 0 || offset >= _source.Length) throw new PdfSyntaxException($"Object offset {offset} is outside the file.");

        // Read a window that starts at 64 KiB and doubles, instead of reading up to the next 'endobj' or
        // EOF. Many objects without 'endobj' would otherwise each read the rest of the file, which is
        // quadratic in time and allocation (ADR-0010 §5.2 item 6). The bytes each read examines are charged
        // to the document work budget (one unit per 64 bytes, ADR-0010 amendment 4), so many such objects
        // together are bounded too: the doubling bounds one object, the budget bounds their number.
        long available = _source.Length - offset;
        int maxWindow = MaxObjectWindow;
        int windowLength = (int)Math.Min(Math.Min(Limits.InitialObjectWindow, available), maxWindow);
        while (true)
        {
            byte[] window = _source.ReadBytes(offset, windowLength);
            bool atEof = window.Length >= available;
            int endObj = IndexOf(window, EndObjKeyword);
            if (endObj >= 0 || atEof)
            {
                int end = endObj >= 0 ? endObj + EndObjKeyword.Length : window.Length;
                _budget.Charge(1 + (end / Limits.ObjectBytesPerWorkUnit));
                return ParseIndirectObject(window, end, offset, requireComplete: false)!;
            }

            _budget.Charge(1 + (window.Length / Limits.ObjectBytesPerWorkUnit));
            PdfObject? value = ParseIndirectObject(window, window.Length, offset, requireComplete: true);
            if (value is not null) return value;

            if (windowLength >= maxWindow)
            {
                throw WindowLimit($"Object at offset {offset}");
            }
            windowLength = (int)Math.Min(Math.Min((long)windowLength * 2, maxWindow), available);
        }
    }

    // The largest object window: the caller's decoded-stream budget, capped at the largest byte array.
    private int MaxObjectWindow => (int)Math.Min(_budget.Limits.MaxDecodedStreamBytes, Limits.MaxArrayLength);

    private PdfLimitExceededException WindowLimit(string what)
        => new PdfLimitExceededException(
            nameof(PdfLimits.MaxDecodedStreamBytes),
            _budget.Limits.MaxDecodedStreamBytes,
            $"{what} exceeds the window limit of {MaxObjectWindow} bytes.");

    // Parses "n g obj value" from window[0..end). With requireComplete, returns null when the result could
    // change if the window were wider: the parse ran into the window end, or the token after the value is
    // cut off by it (e.g. "stream" or the "R" of a reference).
    private PdfObject? ParseIndirectObject(byte[] window, int end, long offset, bool requireComplete)
    {
        var lexer = new PdfLexer(window, 0, end);
        Token number = lexer.Next();
        Token generation = lexer.Next();
        Token obj = lexer.Next();
        if (number.Type != TokenType.Integer || generation.Type != TokenType.Integer
            || obj.Type != TokenType.Keyword || obj.Text != "obj")
        {
            if (requireComplete && obj.Type == TokenType.Eof) return null;
            throw new PdfSyntaxException($"Malformed indirect object header at offset {offset}.");
        }

        int objectNumber = CheckedObjectNumber(number.IntValue);
        int generationNumber = CheckedGeneration(generation.IntValue);

        var parser = new PdfObjectParser(lexer);
        PdfObject value;
        try
        {
            value = parser.ParseValue();
        }
        catch (PdfSyntaxException ex) when (requireComplete && parser.ReachedEnd && ex is not PdfLimitExceededException)
        {
            return null;
        }

        if (requireComplete)
        {
            if (parser.ReachedEnd) return null;
            Token after = lexer.Next();
            if (after.Type == TokenType.Eof || lexer.Position >= end) return null;
        }

        if (_handler is not null && objectNumber != _encryptObjectNumber)
        {
            value = DecryptObject(value, objectNumber, generationNumber, 0);
        }
        return value;
    }

    private static int CheckedObjectNumber(long value)
    {
        if (value < 0 || value > int.MaxValue) throw new PdfSyntaxException("Object number is outside 0..2147483647.");
        return (int)value;
    }

    private static int CheckedGeneration(long value)
    {
        if (value < 0 || value > int.MaxValue) throw new PdfSyntaxException("Generation number is outside 0..2147483647.");
        return (int)value;
    }

    private static int IndexOf(byte[] buffer, byte[] pattern)
    {
        int last = buffer.Length - pattern.Length;
        for (int i = 0; i <= last; i++)
        {
            int j = 0;
            while (j < pattern.Length && buffer[i + j] == pattern[j]) j++;
            if (j == pattern.Length) return i;
        }
        return -1;
    }

    private void SetupEncryption(string? password)
    {
        PdfObject? encryptObj = _trailer!["Encrypt"];
        if (encryptObj is null) return;

        IsEncrypted = true;
        if (encryptObj is PdfReference encReference) _encryptObjectNumber = encReference.ObjectNumber;

        if (Resolve(encryptObj) is not PdfDictionary enc)
        {
            HandlerSupported = false;
            return;
        }
        if ((enc["Filter"] as PdfName)?.Value != "Standard")
        {
            HandlerSupported = false; // e.g. public-key handler — out of scope
            return;
        }

        int v = IntOf(enc, "V", 0);
        int r = IntOf(enc, "R", 0);
        EncryptionAlgorithm algorithm = DetermineAlgorithm(enc, v, r);

        Algorithm = algorithm;
        EncryptionRevision = r;
        Permissions = IntOf(enc, "P", 0);
        EncryptMetadata = !(enc["EncryptMetadata"] is PdfBoolean meta) || meta.Value;

        // Strings and streams can use different crypt filters (/StrF, /StmF); "Identity" means "not encrypted".
        EncryptionAlgorithm streamMethod = CryptMethod(enc, "StmF", v, r, algorithm);
        EncryptionAlgorithm stringMethod = CryptMethod(enc, "StrF", v, r, algorithm);

        int keyLengthBytes = algorithm == EncryptionAlgorithm.Aes256 ? 32 : KeyLengthBytes(enc, v, algorithm);
        _handler = new StandardSecurityHandler(
            algorithm, r, keyLengthBytes,
            StringBytes(enc, "O"), StringBytes(enc, "U"), StringBytes(enc, "OE"), StringBytes(enc, "UE"),
            Permissions, FirstIdBytes(), EncryptMetadata, password, streamMethod, stringMethod);
        Authentication = _handler.Authentication;
    }

    // The file-key length for revisions 2-4. Version 4 states it on the crypt filter (/CF/<StmF>/Length, which
    // writers give either in bytes or in bits); otherwise the top-level /Length in bits applies. Without either,
    // AESV2 implies a 128-bit key and RC4 the 40-bit default.
    private int KeyLengthBytes(PdfDictionary enc, int v, EncryptionAlgorithm algorithm)
    {
        if (v == 4 && CryptFilter(enc, "StmF") is PdfDictionary filter && filter["Length"] is PdfNumber cfLength)
        {
            long value = cfLength.AsInt64;
            return value >= 40 ? (int)Math.Min(value / 8, int.MaxValue) : (int)Math.Max(value, 0);
        }
        if (enc["Length"] is PdfNumber length)
        {
            return (int)Math.Max(Math.Min(length.AsInt64 / 8, int.MaxValue), 0);
        }
        return algorithm == EncryptionAlgorithm.Aes128 ? 16 : 5;
    }

    private EncryptionAlgorithm DetermineAlgorithm(PdfDictionary enc, int v, int r)
    {
        if (v >= 5 || r >= 5) return EncryptionAlgorithm.Aes256;
        if (v == 4)
        {
            switch (CryptFilterMethod(enc))
            {
                case "AESV2": return EncryptionAlgorithm.Aes128;
                case "AESV3": return EncryptionAlgorithm.Aes256;
                default: return EncryptionAlgorithm.Rc4;
            }
        }
        return EncryptionAlgorithm.Rc4;
    }

    // The cipher one crypt filter applies, with None for the Identity filter and for /CFM /None.
    private EncryptionAlgorithm CryptMethod(PdfDictionary enc, string key, int v, int r, EncryptionAlgorithm algorithm)
    {
        if (v < 4) return algorithm;
        if ((enc[key] as PdfName)?.Value == "Identity") return EncryptionAlgorithm.None;

        string? cfm = (CryptFilter(enc, key)?["CFM"] as PdfName)?.Value;
        switch (cfm)
        {
            case "None": return EncryptionAlgorithm.None;
            case "AESV2": return v >= 5 || r >= 5 ? EncryptionAlgorithm.Aes256 : EncryptionAlgorithm.Aes128;
            case "AESV3": return EncryptionAlgorithm.Aes256;
            case "V2": return v >= 5 || r >= 5 ? EncryptionAlgorithm.Aes256 : EncryptionAlgorithm.Rc4;
            default: return v >= 5 || r >= 5 ? EncryptionAlgorithm.Aes256 : EncryptionAlgorithm.Rc4;
        }
    }

    private PdfDictionary? CryptFilter(PdfDictionary enc, string key)
    {
        string name = (enc[key] as PdfName)?.Value ?? "StdCF";
        return Resolve(enc["CF"] ?? PdfNull.Instance) is PdfDictionary cf
            && Resolve(cf[name] ?? PdfNull.Instance) is PdfDictionary filter
            ? filter
            : null;
    }

    private string CryptFilterMethod(PdfDictionary enc)
        => (CryptFilter(enc, "StmF")?["CFM"] as PdfName)?.Value ?? "V2";

    private PdfObject DecryptObject(PdfObject value, int objectNumber, int generation, int depth)
    {
        // The parser bounds nesting already; the walk checks again so it never relies on that.
        if (depth > Limits.MaxNestingDepth)
        {
            throw new PdfLimitExceededException(
                nameof(Limits.MaxNestingDepth),
                Limits.MaxNestingDepth,
                $"Object nesting exceeds the limit of {Limits.MaxNestingDepth} levels.");
        }

        switch (value)
        {
            case PdfString s:
                return new PdfString(_handler!.DecryptString(s.ToArray(), objectNumber, generation), s.IsHexString);
            case PdfArray a:
            {
                var items = new List<PdfObject>(a.Count);
                for (int i = 0; i < a.Count; i++) items.Add(DecryptObject(a[i], objectNumber, generation, depth + 1));
                return new PdfArray(items);
            }
            case PdfDictionary d:
                return DecryptDictionary(d, objectNumber, generation, depth + 1);
            case PdfStream stream:
            {
                PdfDictionary dict = DecryptDictionary(stream.Dictionary, objectNumber, generation, depth + 1);
                string? type = (stream.Dictionary["Type"] as PdfName)?.Value;

                // Cross-reference streams are never encrypted, and with /EncryptMetadata false neither is
                // the document metadata stream (ISO 32000-2 §7.5.8.2, §7.6.3.1).
                bool plain = type == "XRef" || (type == "Metadata" && !EncryptMetadata);
                byte[] raw = plain ? stream.GetRawBytes() : _handler!.DecryptStream(stream.GetRawBytes(), objectNumber, generation);
                return new PdfStream(dict, raw);
            }
            default:
                return value;
        }
    }

    private PdfDictionary DecryptDictionary(PdfDictionary dictionary, int objectNumber, int generation, int depth)
    {
        var map = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
        foreach (string key in dictionary.Keys)
        {
            PdfObject? entry = dictionary[key];
            if (entry is not null) map[key] = DecryptObject(entry, objectNumber, generation, depth);
        }
        return new PdfDictionary(map);
    }

    private static int IntOf(PdfDictionary dict, string key, int fallback)
        => dict[key] is PdfNumber n ? (int)n.AsInt64 : fallback;

    private static byte[] StringBytes(PdfDictionary dict, string key)
        => dict[key] is PdfString s ? s.ToArray() : Array.Empty<byte>();

    private byte[] FirstIdBytes()
        => _trailer!["ID"] is PdfArray id && id.Count > 0 && id[0] is PdfString s ? s.ToArray() : Array.Empty<byte>();

    private PdfObject LoadFromObjectStream(int objectStreamNumber, int index)
    {
        ObjStmData? data = GetObjectStream(objectStreamNumber);
        if (data is null || index < 0 || index >= data.Entries.Length) return PdfNull.Instance;

        long start = (long)data.First + data.Entries[index].Offset;
        if (start < 0 || start > data.Bytes.Length) return PdfNull.Instance;
        return new PdfObjectParser(data.Bytes, (int)start, data.Bytes.Length).ParseValue();
    }

    private ObjStmData? GetObjectStream(int objectStreamNumber)
    {
        if (_objectStreamCache.TryGetValue(objectStreamNumber, out var cached)) return cached;

        // An object stream must itself be an ordinary (type 1) object (ISO 32000-2 §7.5.7), so one object
        // stream can never be loaded through another; this also rules out chains and cycles of them.
        ObjStmData? data = null;
        if (_xref!.TryGetValue(objectStreamNumber, out var entry) && entry.Type == XrefEntryType.InUse
            && GetObject(objectStreamNumber) is PdfStream stream)
        {
            byte[] decoded = StreamDecoder.Decode(stream, _budget.Limits);
            long count = GetIntOrNull(stream.Dictionary, "N") ?? 0;
            long first = GetIntOrNull(stream.Dictionary, "First") ?? 0;

            // Each header entry takes at least two digits and two separators, so /N cannot exceed the header size.
            if (count < 0 || count > decoded.Length) throw new PdfSyntaxException("Object stream /N is out of range.");
            if (first < 0 || first > decoded.Length) throw new PdfSyntaxException("Object stream /First is out of range.");

            var headerLexer = new PdfLexer(decoded, 0, (int)first);
            var entries = new List<ObjStmEntry>();
            for (long i = 0; i < count; i++)
            {
                Token num = headerLexer.Next();
                Token off = headerLexer.Next();
                if (num.Type != TokenType.Integer || off.Type != TokenType.Integer)
                {
                    throw new PdfSyntaxException("Malformed object stream header.");
                }
                if (off.IntValue < 0 || off.IntValue > decoded.Length) throw new PdfSyntaxException("Object stream offset is out of range.");
                entries.Add(new ObjStmEntry(CheckedObjectNumber(num.IntValue), (int)off.IntValue));
            }

            data = new ObjStmData(decoded, (int)first, entries.ToArray());
        }

        _objectStreamCache[objectStreamNumber] = data;
        return data;
    }

    // Values outside the int range map to -1 so that the /W range check rejects them instead of truncating.
    private static int WidthAt(PdfArray widths, int i)
        => widths[i] is PdfInteger v ? (v.Value < 0 || v.Value > int.MaxValue ? -1 : (int)v.Value) : 0;

    private static long AsLong(PdfObject value) => value is PdfInteger v ? v.Value : 0L;

    private static long? GetIntOrNull(PdfDictionary dict, string key)
        => dict[key] is PdfInteger value ? value.Value : (long?)null;

    private static long ReadBigEndian(byte[] data, int offset, int width)
    {
        long value = 0;
        for (int i = 0; i < width; i++) value = (value << 8) | data[offset + i];
        return value;
    }

    /// <summary>Releases the underlying byte source.</summary>
    public void Dispose() => _source.Dispose();

    private readonly struct XrefSection
    {
        public XrefSection(Dictionary<int, XrefEntry> entries, PdfDictionary trailer, long? prev, long? xrefStm)
        {
            Entries = entries;
            Trailer = trailer;
            Prev = prev;
            XRefStm = xrefStm;
        }

        public Dictionary<int, XrefEntry> Entries { get; }

        public PdfDictionary Trailer { get; }

        public long? Prev { get; }

        public long? XRefStm { get; }
    }

    private readonly struct ObjStmEntry
    {
        public ObjStmEntry(int number, int offset)
        {
            Number = number;
            Offset = offset;
        }

        public int Number { get; }

        public int Offset { get; }
    }

    private sealed class ObjStmData
    {
        public ObjStmData(byte[] bytes, int first, ObjStmEntry[] entries)
        {
            Bytes = bytes;
            First = first;
            Entries = entries;
        }

        public byte[] Bytes { get; }

        public int First { get; }

        public ObjStmEntry[] Entries { get; }
    }
}
