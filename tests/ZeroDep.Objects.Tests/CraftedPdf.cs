using System.Security.Cryptography;
using System.Text;

namespace ZeroDep.Objects.Tests;

/// <summary>Builds small PDFs byte by byte for the resource-limit regression tests.</summary>
internal static class CraftedPdf
{
    public static readonly byte[] Id = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");

    private static readonly byte[] Padding =
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    };

    public static byte[] A(string s) => Encoding.Latin1.GetBytes(s);

    public static byte[] Concat(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (byte[] p in parts) ms.Write(p, 0, p.Length);
        return ms.ToArray();
    }

    /// <summary>A PDF with a classic cross-reference table over the numbered object bodies.</summary>
    public static byte[] Classic(SortedDictionary<int, byte[]> objects, string trailerExtra = "")
    {
        var ms = new MemoryStream();
        ms.Write(A("%PDF-1.7\n"));
        var offsets = new Dictionary<int, long>();
        foreach (KeyValuePair<int, byte[]> kv in objects)
        {
            offsets[kv.Key] = ms.Position;
            ms.Write(A($"{kv.Key} 0 obj\n"));
            ms.Write(kv.Value);
            ms.Write(A("\nendobj\n"));
        }

        int size = objects.Keys.Max() + 1;
        long xref = ms.Position;
        var sb = new StringBuilder($"xref\n0 {size}\n0000000000 65535 f \n");
        for (int i = 1; i < size; i++)
        {
            sb.Append(offsets.TryGetValue(i, out long o) ? $"{o:D10} 00000 n \n" : "0000000000 00000 f \n");
        }

        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R {trailerExtra} >>\nstartxref\n{xref}\n%%EOF\n");
        ms.Write(A(sb.ToString()));
        return ms.ToArray();
    }

    /// <summary>A catalog, a one-page tree and a content stream (object 4) with the given raw data.</summary>
    public static SortedDictionary<int, byte[]> BasicPage(byte[] content, string contentDict = "")
        => new SortedDictionary<int, byte[]>
        {
            [1] = A("<< /Type /Catalog /Pages 2 0 R >>"),
            [2] = A("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            [3] = A("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>"),
            [4] = Stream(contentDict, content),
        };

    public static byte[] Stream(string dict, byte[] data)
        => Concat(A($"<< {dict} /Length {data.Length} >>\nstream\n"), data, A("\nendstream"));

    /// <summary>A PDF whose only cross-reference is a stream (object <paramref name="xrefNumber"/>) with /W [1 1 1] records.</summary>
    public static byte[] WithXrefStream(Func<MemoryStream, byte[]> bodyAndRecords, int xrefNumber, int size, string extra = "")
    {
        var ms = new MemoryStream();
        ms.Write(A("%PDF-1.7\n"));
        byte[] records = bodyAndRecords(ms);
        long xoff = ms.Position;
        ms.Write(A($"{xrefNumber} 0 obj\n<< /Type /XRef /W [1 1 1] /Size {size} /Root 1 0 R {extra} /Length {records.Length} >>\nstream\n"));
        ms.Write(records);
        ms.Write(A("\nendstream\nendobj\n"));
        ms.Write(A($"startxref\n{xoff}\n%%EOF\n"));
        return ms.ToArray();
    }

    // ---- standard security handler, revisions 3 and 4 (ISO 32000-2 §7.6.4), empty user password ----

    /// <summary>The O value used by every encrypted fixture (any 32 bytes work for user-password authentication).</summary>
    public static byte[] OwnerValue()
    {
        var o = new byte[32];
        for (int i = 0; i < 32; i++) o[i] = (byte)((i * 7) + 1);
        return o;
    }

    /// <summary>Algorithm 2: the file key for the empty user password.</summary>
    public static byte[] FileKey(int lengthBytes, int permissions, bool encryptMetadata = true)
    {
        byte[] input = Concat(Padding, OwnerValue(), BitConverter.GetBytes(permissions), Id,
            encryptMetadata ? Array.Empty<byte>() : new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        byte[] h = MD5.HashData(input);
        for (int i = 0; i < 50; i++) h = MD5.HashData(h.AsSpan(0, lengthBytes));
        return h[..lengthBytes];
    }

    /// <summary>Algorithm 5: the U value for <paramref name="key"/>.</summary>
    public static byte[] UserValue(byte[] key)
    {
        byte[] u = Rc4(key, MD5.HashData(Concat(Padding, Id)));
        for (int i = 1; i <= 19; i++)
        {
            byte[] k2 = key.Select(b => (byte)(b ^ i)).ToArray();
            u = Rc4(k2, u);
        }

        return Concat(u, new byte[16]);
    }

    /// <summary>Algorithm 1: the per-object key.</summary>
    public static byte[] ObjectKey(byte[] fileKey, int number, bool aes)
    {
        byte[] input = Concat(fileKey, new[] { (byte)number, (byte)(number >> 8), (byte)(number >> 16), (byte)0, (byte)0 },
            aes ? new byte[] { 0x73, 0x41, 0x6C, 0x54 } : Array.Empty<byte>());
        return MD5.HashData(input)[..Math.Min(fileKey.Length + 5, 16)];
    }

    public static byte[] AesEncrypt(byte[] key, byte[] plain)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        byte[] iv = new byte[16];
        for (int i = 0; i < 16; i++) iv[i] = (byte)(i * 3);
        return Concat(iv, aes.EncryptCbc(plain, iv));
    }

    public static byte[] Rc4(byte[] key, byte[] data)
    {
        byte[] s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 255;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var o = new byte[data.Length];
        int a = 0, b = 0;
        for (int k = 0; k < data.Length; k++)
        {
            a = (a + 1) & 255;
            b = (b + s[a]) & 255;
            (s[a], s[b]) = (s[b], s[a]);
            o[k] = (byte)(data[k] ^ s[(s[a] + s[b]) & 255]);
        }

        return o;
    }

    public static string Hex(byte[] b) => Convert.ToHexString(b);

    public static string IdTrailer => $"/ID [<{Hex(Id)}> <{Hex(Id)}>]";
}
