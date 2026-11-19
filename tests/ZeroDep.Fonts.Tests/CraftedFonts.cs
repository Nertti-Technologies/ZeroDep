using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace ZeroDep.Fonts.Tests;

/// <summary>
/// Builds minimal crafted font programs (TrueType, bare CFF, Type 1) in memory for limit regression tests,
/// and runs a case with a time limit and a managed-allocation bound measured on the thread that ran it.
/// </summary>
internal static class CraftedFonts
{
    public const long MaxAllocatedBytes = 300L * 1024 * 1024;

    /// <summary>Runs <paramref name="action"/> within a time limit; returns the exception it threw, or null.</summary>
    public static Exception? Bounded(Action action, long maxBytes = MaxAllocatedBytes, int seconds = 10)
    {
        Exception? thrown = null;
        long allocated = 0;
        var task = Task.Run(() =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        });

        Assert.True(task.Wait(TimeSpan.FromSeconds(seconds)), $"did not finish within {seconds} s");
        Assert.True(allocated < maxBytes, $"allocated {allocated:N0} bytes");
        return thrown;
    }

    // ---- TrueType ----

    public static byte[] BE16(int v) => new[] { (byte)(v >> 8), (byte)v };

    public static byte[] BE32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    /// <summary>A simple glyph: one on-curve right triangle of side <paramref name="s"/> font units.</summary>
    public static byte[] Triangle(int s = 100)
    {
        var gl = new List<byte>();
        gl.AddRange(BE16(1));
        gl.AddRange(new byte[8]);
        gl.AddRange(BE16(2)); // endPts [2]
        gl.AddRange(BE16(0)); // no instructions
        gl.AddRange(new byte[] { 0x01, 0x01, 0x01 }); // on-curve, x/y as S16 deltas
        gl.AddRange(BE16(0));
        gl.AddRange(BE16(s));
        gl.AddRange(BE16(0));
        gl.AddRange(BE16(0));
        gl.AddRange(BE16(0));
        gl.AddRange(BE16(s));
        return gl.ToArray();
    }

    /// <summary>A composite glyph whose <paramref name="count"/> components all reference glyph <paramref name="component"/>.</summary>
    public static byte[] Composite(int component, int count)
    {
        var gl = new List<byte>();
        gl.AddRange(BE16(0xFFFF)); // numberOfContours = -1
        gl.AddRange(new byte[8]);
        for (int i = 0; i < count; i++)
        {
            int flags = 0x0002 | (i < count - 1 ? 0x0020 : 0); // ARGS_ARE_XY_VALUES, MORE_COMPONENTS
            gl.AddRange(BE16(flags));
            gl.AddRange(BE16(component));
            gl.Add(0);
            gl.Add(0);
        }

        return gl.ToArray();
    }

    /// <summary>Wraps glyph records into a glyf-based TrueType program (long loca).</summary>
    public static byte[] Ttf(byte[][] glyphs, byte[]? fpgm = null, byte[]? prep = null, int unitsPerEm = 2048, int maxStack = 0)
    {
        var tables = new List<(string Tag, byte[] Data)>();
        var head = new byte[54];
        head[18] = (byte)(unitsPerEm >> 8);
        head[19] = (byte)unitsPerEm;
        head[51] = 1; // long loca

        byte[] maxp = new byte[32];
        maxp[4] = (byte)(glyphs.Length >> 8);
        maxp[5] = (byte)glyphs.Length;
        maxp[24] = (byte)(maxStack >> 8);
        maxp[25] = (byte)maxStack;

        var glyf = new List<byte>();
        var loca = new List<byte>();
        foreach (byte[] g in glyphs)
        {
            loca.AddRange(BE32((uint)glyf.Count));
            glyf.AddRange(g);
            while (glyf.Count % 4 != 0)
            {
                glyf.Add(0);
            }
        }

        loca.AddRange(BE32((uint)glyf.Count));
        tables.Add(("glyf", glyf.ToArray()));
        tables.Add(("head", head));
        tables.Add(("loca", loca.ToArray()));
        tables.Add(("maxp", maxp));
        if (fpgm != null)
        {
            tables.Add(("fpgm", fpgm));
        }

        if (prep != null)
        {
            tables.Add(("prep", prep));
        }

        var o = new List<byte>();
        o.AddRange(BE32(0x00010000));
        o.AddRange(BE16(tables.Count));
        o.AddRange(new byte[6]);
        int off = 12 + (16 * tables.Count);
        var body = new List<byte>();
        foreach (var (tag, data) in tables)
        {
            o.AddRange(Encoding.ASCII.GetBytes(tag));
            o.AddRange(BE32(0));
            o.AddRange(BE32((uint)(off + body.Count)));
            o.AddRange(BE32((uint)data.Length));
            body.AddRange(data);
            while (body.Count % 4 != 0)
            {
                body.Add(0);
            }
        }

        o.AddRange(body);
        return o.ToArray();
    }

    // ---- CFF ----

    public static byte[] Index(params byte[][] items)
    {
        var r = new List<byte>();
        r.AddRange(BE16(items.Length));
        if (items.Length == 0)
        {
            return r.ToArray();
        }

        r.Add(4);
        uint o = 1;
        r.AddRange(BE32(o));
        foreach (byte[] it in items)
        {
            o += (uint)it.Length;
            r.AddRange(BE32(o));
        }

        foreach (byte[] it in items)
        {
            r.AddRange(it);
        }

        return r.ToArray();
    }

    /// <summary>A bare CFF with one glyph whose charstring is <paramref name="charstring"/>, and the given global subrs.</summary>
    public static byte[] Cff(byte[] charstring, params byte[][] globalSubrs)
    {
        byte[] hdr = { 1, 0, 4, 4 };
        byte[] name = Index(Encoding.ASCII.GetBytes("A"));
        const int TopDictLen = 6;
        int topIndexLen = 2 + 1 + 8 + TopDictLen;
        byte[] strings = Index();
        byte[] gsubrs = Index(globalSubrs);
        int csOff = hdr.Length + name.Length + topIndexLen + strings.Length + gsubrs.Length;
        var top = new List<byte> { 29 };
        top.AddRange(BE32((uint)csOff));
        top.Add(17); // CharStrings
        var all = new List<byte>();
        all.AddRange(hdr);
        all.AddRange(name);
        all.AddRange(Index(top.ToArray()));
        all.AddRange(strings);
        all.AddRange(gsubrs);
        all.AddRange(Index(charstring));
        return all.ToArray();
    }

    // ---- Type 1 ----

    public static byte[] N(int v) => v >= -107 && v <= 107 ? new[] { (byte)(v + 139) } : N32(v);

    public static byte[] N32(int v) => new byte[] { 255, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    public static byte[] Cs(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Encrypt(byte[] plain, ushort r)
    {
        var o = new byte[plain.Length];
        for (int i = 0; i < plain.Length; i++)
        {
            byte c = (byte)(plain[i] ^ (r >> 8));
            o[i] = c;
            r = (ushort)(((c + r) * 52845) + 22719);
        }

        return o;
    }

    /// <summary>A Type 1 program whose first charstring is named <paramref name="glyphName"/>.</summary>
    public static byte[] Type1(byte[][] charstrings, byte[][] subrs, string glyphName)
    {
        var ms = new MemoryStream();
        void W(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        W("dup /Private 8 dict dup begin /lenIV 4 def\n");
        if (subrs.Length > 0)
        {
            W($"/Subrs {subrs.Length} array\n");
            for (int i = 0; i < subrs.Length; i++)
            {
                byte[] e = Encrypt(new byte[4].Concat(subrs[i]).ToArray(), 4330);
                W($"dup {i} {e.Length} RD ");
                ms.Write(e, 0, e.Length);
                W(" NP\n");
            }
        }

        W($"2 index /CharStrings {charstrings.Length} dict dup begin\n");
        for (int i = 0; i < charstrings.Length; i++)
        {
            byte[] e = Encrypt(new byte[4].Concat(charstrings[i]).ToArray(), 4330);
            W($"/{(i == 0 ? glyphName : "g" + i)} {e.Length} RD ");
            ms.Write(e, 0, e.Length);
            W(" ND\n");
        }

        W("end\nend\n");
        return Wrap(ms.ToArray());
    }

    /// <summary>A Type 1 program with a clear private section (lenIV 0) given as text.</summary>
    public static byte[] Type1Raw(string privateText)
        => Wrap(Encoding.ASCII.GetBytes("dup /Private 8 dict dup begin /lenIV 0 def\n" + privateText));

    private static byte[] Wrap(byte[] priv)
    {
        byte[] head = Encoding.ASCII.GetBytes("%!PS-AdobeFont-1.0: X\n/FontMatrix [0.001 0 0 0.001 0 0] readonly def\ncurrentfile eexec\n");
        for (int lead = 0; ; lead++)
        {
            byte[] enc = Encrypt(new byte[] { (byte)lead, 0, 0, 0 }.Concat(priv).ToArray(), 55665);
            byte b = enc[0];
            bool hex = (b >= '0' && b <= '9') || (b >= 'a' && b <= 'f') || (b >= 'A' && b <= 'F');
            bool white = b == ' ' || b == '\t' || b == '\r' || b == '\n' || b == '\f' || b == 0;
            if (!hex && !white)
            {
                return head.Concat(enc).ToArray();
            }
        }
    }
}
