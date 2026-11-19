using System;
using System.Collections.Generic;
using ZeroDep.Abstractions;
using ZeroDep.Objects;

namespace ZeroDep.Color;

/// <summary>
/// A resolved PDF color space (ISO 32000-2 §8.6) that maps an N-component sample tuple to 8-bit RGB.
/// Supports DeviceGray/RGB/CMYK, CalGray/CalRGB (approximated), Lab, ICCBased (pragmatic — by component
/// count and <c>/Alternate</c>, no profile parsing), Indexed (palette lookup), and Separation/DeviceN
/// (tint transform via <see cref="PdfFunction"/>). Pure-BCL; output is "correct-enough" sRGB, not a CMM.
/// </summary>
public abstract class PdfColorSpace
{
    /// <summary>Number of colour components a sample tuple carries in this space.</summary>
    public abstract int ComponentCount { get; }

    /// <summary>The colour-space family name (e.g. <c>DeviceRGB</c>, <c>Indexed</c>, <c>Separation</c>).</summary>
    public abstract string Family { get; }

    /// <summary>True for achromatic single-component spaces that can be emitted as 8-bit grey (DeviceGray/CalGray).</summary>
    internal virtual bool IsGrayscale => false;

    /// <summary>
    /// The default <c>/Decode</c> bounds (flat [min0,max0, …]) used to map raw samples to component values
    /// when the image supplies no explicit <c>/Decode</c>. Depends on <paramref name="bitsPerComponent"/>
    /// only for Indexed (index range 0…2^bpc−1).
    /// </summary>
    /// <param name="bitsPerComponent">The image's bits per component.</param>
    public abstract double[] DefaultDecode(int bitsPerComponent);

    /// <summary>Maps component values (already mapped through <c>/Decode</c>) to 8-bit RGB.</summary>
    /// <param name="comps">Buffer holding the component values.</param>
    /// <param name="offset">Index of the first component in <paramref name="comps"/>.</param>
    /// <param name="r">Red output (0–255).</param>
    /// <param name="g">Green output (0–255).</param>
    /// <param name="b">Blue output (0–255).</param>
    public abstract void ToRgb(double[] comps, int offset, out byte r, out byte g, out byte b);

    /// <summary>
    /// Resolves a PDF <c>/ColorSpace</c> object (name, array, or a name referencing a resource) into a
    /// <see cref="PdfColorSpace"/>. Throws <see cref="NotSupportedException"/> for Pattern / unknown spaces.
    /// </summary>
    /// <param name="obj">The color-space object (may be an indirect reference).</param>
    /// <param name="resolve">Resolves indirect references.</param>
    /// <param name="decodeStream">Decodes a stream's filters (for ICCBased, Indexed lookup, tint streams).</param>
    /// <param name="namedLookup">Resolves a colour-space name against the page resources, or null.</param>
    /// <exception cref="NotSupportedException">The colour space is unsupported, unknown, or malformed.</exception>
    /// <exception cref="PdfSyntaxException">
    /// The colour space nests deeper than the fixed limit or refers back to itself.
    /// </exception>
    public static PdfColorSpace Resolve(
        PdfObject obj,
        Func<PdfObject, PdfObject> resolve,
        Func<PdfStream, byte[]> decodeStream,
        Func<string, PdfObject?>? namedLookup = null)
    {
        if (obj is null)
        {
            throw new ArgumentNullException(nameof(obj));
        }

        if (resolve is null)
        {
            throw new ArgumentNullException(nameof(resolve));
        }

        if (decodeStream is null)
        {
            throw new ArgumentNullException(nameof(decodeStream));
        }

        var ctx = new ResolveContext(resolve, decodeStream, namedLookup);
        return Resolve(obj, ctx, 0);
    }

    // Recursive resolution. 'depth' bounds nesting (Indexed base, ICC /Alternate, Separation/DeviceN
    // alternate, named lookups); the context's active set holds the object numbers on the current path,
    // so a space that refers back to itself is reported instead of recursing forever.
    private static PdfColorSpace Resolve(PdfObject obj, ResolveContext ctx, int depth)
    {
        if (depth > Limits.MaxColorSpaceDepth)
        {
            throw new PdfSyntaxException($"Colour space nesting exceeds the limit of {Limits.MaxColorSpaceDepth}.");
        }

        int objectNumber = obj is PdfReference reference ? reference.ObjectNumber : -1;
        if (objectNumber >= 0 && !ctx.Active.Add(objectNumber))
        {
            throw new PdfSyntaxException("Colour space refers to itself.");
        }

        try
        {
            return ResolveCore(obj, ctx, depth);
        }
        finally
        {
            if (objectNumber >= 0)
            {
                ctx.Active.Remove(objectNumber);
            }
        }
    }

    private static PdfColorSpace ResolveCore(PdfObject obj, ResolveContext ctx, int depth)
    {
        Func<PdfObject, PdfObject> resolve = ctx.Resolve;
        PdfObject ro = resolve(obj);

        if (ro is PdfName name)
        {
            switch (name.Value)
            {
                case "DeviceGray":
                case "G":
                    return DeviceGray.Instance;
                case "DeviceRGB":
                case "RGB":
                    return DeviceRgb.Instance;
                case "DeviceCMYK":
                case "CMYK":
                    return DeviceCmyk.Instance;
                case "Pattern":
                    throw new NotSupportedException("Pattern color space is out of scope.");
                default:
                    PdfObject? named = ctx.NamedLookup?.Invoke(name.Value);
                    if (named is not null && !ReferenceEquals(resolve(named), ro))
                    {
                        return Resolve(named, ctx, depth + 1);
                    }

                    throw new NotSupportedException($"Unknown color space '{name.Value}'.");
            }
        }

        if (ro is PdfArray array && array.Count > 0 && resolve(array[0]) is PdfName family)
        {
            return ResolveArray(family.Value, array, ctx, depth);
        }

        throw new NotSupportedException("Unsupported color space object.");
    }

    private static PdfColorSpace ResolveArray(string family, PdfArray array, ResolveContext ctx, int depth)
    {
        Func<PdfObject, PdfObject> resolve = ctx.Resolve;
        switch (family)
        {
            case "ICCBased":
                return ResolveIcc(array, ctx, depth);

            case "CalGray":
                return DeviceGray.Instance; // approximate (gamma ignored)

            case "CalRGB":
                return DeviceRgb.Instance; // approximate (gamma/matrix ignored)

            case "Lab":
                return LabColorSpace.Create(array.Count > 1 ? resolve(array[1]) : PdfNull.Instance, resolve);

            case "Indexed":
            case "I":
                return IndexedColorSpace.Create(array, ctx, depth);

            case "Separation":
                return SeparationColorSpace.Create(array, ctx, depth);

            case "DeviceN":
                return SeparationColorSpace.CreateDeviceN(array, ctx, depth);

            case "Pattern":
                throw new NotSupportedException("Pattern color space is out of scope.");

            default:
                throw new NotSupportedException($"Unknown color space family '{family}'.");
        }
    }

    private static PdfColorSpace ResolveIcc(PdfArray array, ResolveContext ctx, int depth)
    {
        Func<PdfObject, PdfObject> resolve = ctx.Resolve;
        // Pragmatic: use /Alternate when present, else map by /N (no profile parsing).
        if (array.Count > 1 && resolve(array[1]) is PdfStream st)
        {
            PdfDictionary d = st.Dictionary;
            if (d["Alternate"] is { } alt)
            {
                try
                {
                    return Resolve(alt, ctx, depth + 1);
                }
                catch (NotSupportedException)
                {
                    // fall through to N-based mapping
                }
            }

            int n = resolve(d["N"] ?? PdfNull.Instance) is PdfNumber num ? (int)num.AsInt64 : 3;
            return ByComponentCount(n);
        }

        return DeviceRgb.Instance;
    }

    /// <summary>Resolves a nested colour space one level deeper (for the derived spaces' base/alternate).</summary>
    private protected static PdfColorSpace ResolveNested(PdfObject obj, ResolveContext ctx, int depth)
        => Resolve(obj, ctx, depth + 1);

    /// <summary>Throws <see cref="NotSupportedException"/> unless <paramref name="array"/> has at least <paramref name="count"/> entries.</summary>
    private protected static void RequireCount(PdfArray array, int count, string family)
    {
        if (array.Count < count)
        {
            throw new NotSupportedException($"{family} color space array is too short.");
        }
    }

    private protected static PdfColorSpace ByComponentCount(int n)
        => n switch
        {
            1 => DeviceGray.Instance,
            4 => DeviceCmyk.Instance,
            _ => DeviceRgb.Instance,
        };

    private protected static byte ToByte(double v01)
    {
        // Clamp in floating point: NaN and huge values must not reach the int cast.
        double x = Math.Round(v01 * 255.0, MidpointRounding.AwayFromZero);
        return x >= 255.0 ? (byte)255 : (x > 0.0 ? (byte)x : (byte)0);
    }
}

/// <summary>The delegates and the active-reference set shared by one <see cref="PdfColorSpace.Resolve(PdfObject, Func{PdfObject, PdfObject}, Func{PdfStream, byte[]}, Func{string, PdfObject?})"/> call.</summary>
internal sealed class ResolveContext
{
    public ResolveContext(Func<PdfObject, PdfObject> resolve, Func<PdfStream, byte[]> decodeStream, Func<string, PdfObject?>? namedLookup)
    {
        Resolve = resolve;
        DecodeStream = decodeStream;
        NamedLookup = namedLookup;
    }

    public Func<PdfObject, PdfObject> Resolve { get; }

    public Func<PdfStream, byte[]> DecodeStream { get; }

    public Func<string, PdfObject?>? NamedLookup { get; }

    /// <summary>Object numbers of the colour spaces being resolved on the current path.</summary>
    public HashSet<int> Active { get; } = new HashSet<int>();
}

/// <summary>DeviceGray — a single [0,1] component rendered as neutral grey.</summary>
internal sealed class DeviceGray : PdfColorSpace
{
    public static DeviceGray Instance { get; } = new DeviceGray();

    public override int ComponentCount => 1;

    public override string Family => "DeviceGray";

    internal override bool IsGrayscale => true;

    public override double[] DefaultDecode(int bitsPerComponent) => new[] { 0.0, 1.0 };

    public override void ToRgb(double[] comps, int offset, out byte r, out byte g, out byte b)
    {
        byte v = ToByte(comps[offset]);
        r = g = b = v;
    }
}

/// <summary>DeviceRGB — three [0,1] components.</summary>
internal sealed class DeviceRgb : PdfColorSpace
{
    public static DeviceRgb Instance { get; } = new DeviceRgb();

    public override int ComponentCount => 3;

    public override string Family => "DeviceRGB";

    public override double[] DefaultDecode(int bitsPerComponent) => new[] { 0.0, 1.0, 0.0, 1.0, 0.0, 1.0 };

    public override void ToRgb(double[] comps, int offset, out byte r, out byte g, out byte b)
    {
        r = ToByte(comps[offset]);
        g = ToByte(comps[offset + 1]);
        b = ToByte(comps[offset + 2]);
    }
}

/// <summary>DeviceCMYK — four [0,1] inks, converted with the Adobe-style polynomial (ADR-0004 §7-6).</summary>
internal sealed class DeviceCmyk : PdfColorSpace
{
    public static DeviceCmyk Instance { get; } = new DeviceCmyk();

    public override int ComponentCount => 4;

    public override string Family => "DeviceCMYK";

    public override double[] DefaultDecode(int bitsPerComponent) => new[] { 0.0, 1.0, 0.0, 1.0, 0.0, 1.0, 0.0, 1.0 };

    public override void ToRgb(double[] comps, int offset, out byte r, out byte g, out byte b)
    {
        double c = comps[offset];
        double m = comps[offset + 1];
        double y = comps[offset + 2];
        double k = comps[offset + 3];

        // Adobe-derived polynomial; coefficients reproduced under Apache-2.0 (see THIRD-PARTY-NOTICES.txt).
        // Inputs in [0,1], output 0..255.
        double red = 255.0 +
            (c * ((-4.387332384609988 * c) + (54.48615194189176 * m) + (18.82290502165302 * y) + (212.25662451639585 * k) - 285.2331026137004)) +
            (m * ((1.7149763477362134 * m) - (5.6096736904047315 * y) - (17.873870861415444 * k) - 5.497006427196366)) +
            (y * ((-2.5217340131683033 * y) - (21.248923337353073 * k) + 17.5119270841813)) +
            (k * ((-21.86122147463605 * k) - 189.48180835922747));

        double green = 255.0 +
            (c * ((8.841041422036149 * c) + (60.118027045597366 * m) + (6.871425592049007 * y) + (31.159100130055922 * k) - 79.2970844816548)) +
            (m * ((-15.310361306967817 * m) + (17.575251261109482 * y) + (131.35250912493976 * k) - 190.9453302588951)) +
            (y * ((4.444339102852739 * y) + (9.8632861493405 * k) - 24.86741582555878)) +
            (k * ((-20.737325471181034 * k) - 187.80453709719578));

        double blue = 255.0 +
            (c * ((0.8842522430003296 * c) + (8.078677503112928 * m) + (30.89978309703729 * y) - (0.23883238689178934 * k) - 14.183576799673286)) +
            (m * ((10.49593273432072 * m) + (63.02378494754052 * y) + (50.606957656360734 * k) - 112.23884253719248)) +
            (y * ((0.03296041114873217 * y) + (115.60384449646641 * k) - 193.58209356861505)) +
            (k * ((-22.33816807309886 * k) - 180.12613974708367));

        r = ClampByte(red);
        g = ClampByte(green);
        b = ClampByte(blue);
    }

    private static byte ClampByte(double v)
    {
        double x = Math.Round(v, MidpointRounding.AwayFromZero);
        return x >= 255.0 ? (byte)255 : (x > 0.0 ? (byte)x : (byte)0);
    }
}

/// <summary>CIE L*a*b* → sRGB (pragmatic: standard Lab→XYZ→sRGB, white-point adaptation omitted).</summary>
internal sealed class LabColorSpace : PdfColorSpace
{
    private readonly double _xn;
    private readonly double _yn;
    private readonly double _zn;
    private readonly double _aMin;
    private readonly double _aMax;
    private readonly double _bMin;
    private readonly double _bMax;

    private LabColorSpace(double xn, double yn, double zn, double aMin, double aMax, double bMin, double bMax)
    {
        _xn = xn;
        _yn = yn;
        _zn = zn;
        _aMin = aMin;
        _aMax = aMax;
        _bMin = bMin;
        _bMax = bMax;
    }

    public override int ComponentCount => 3;

    public override string Family => "Lab";

    public override double[] DefaultDecode(int bitsPerComponent) => new[] { 0.0, 100.0, _aMin, _aMax, _bMin, _bMax };

    public static LabColorSpace Create(PdfObject dictObj, Func<PdfObject, PdfObject> resolve)
    {
        double xn = 0.9505, yn = 1.0, zn = 1.089; // D65 default
        double aMin = -100, aMax = 100, bMin = -100, bMax = 100;

        if (resolve(dictObj) is PdfDictionary dict)
        {
            if (resolve(dict["WhitePoint"] ?? PdfNull.Instance) is PdfArray wp && wp.Count == 3)
            {
                xn = AsD(resolve(wp[0]));
                yn = AsD(resolve(wp[1]));
                zn = AsD(resolve(wp[2]));
            }

            if (resolve(dict["Range"] ?? PdfNull.Instance) is PdfArray rg && rg.Count == 4)
            {
                aMin = AsD(resolve(rg[0]));
                aMax = AsD(resolve(rg[1]));
                bMin = AsD(resolve(rg[2]));
                bMax = AsD(resolve(rg[3]));
            }
        }

        return new LabColorSpace(xn, yn, zn, aMin, aMax, bMin, bMax);
    }

    public override void ToRgb(double[] comps, int offset, out byte r, out byte g, out byte b)
    {
        double l = comps[offset];
        double a = comps[offset + 1];
        double bb = comps[offset + 2];

        double fy = (l + 16.0) / 116.0;
        double fx = fy + (a / 500.0);
        double fz = fy - (bb / 200.0);

        double x = _xn * Finv(fx);
        double y = _yn * Finv(fy);
        double z = _zn * Finv(fz);

        double rl = (3.2406 * x) - (1.5372 * y) - (0.4986 * z);
        double gl = (-0.9689 * x) + (1.8758 * y) + (0.0415 * z);
        double bl = (0.0557 * x) - (0.2040 * y) + (1.0570 * z);

        r = ToByte(Gamma(rl));
        g = ToByte(Gamma(gl));
        b = ToByte(Gamma(bl));
    }

    private static double Finv(double t)
    {
        const double D = 6.0 / 29.0;
        return t > D ? (t * t * t) : (3.0 * D * D * (t - (4.0 / 29.0)));
    }

    private static double Gamma(double c)
    {
        if (c <= 0.0)
        {
            return 0.0;
        }

        if (c >= 1.0)
        {
            return 1.0;
        }

        return c <= 0.0031308 ? (12.92 * c) : ((1.055 * Math.Pow(c, 1.0 / 2.4)) - 0.055);
    }

    private static double AsD(PdfObject o) => o is PdfNumber n ? n.AsDouble : 0.0;
}

/// <summary>Indexed — a single index into a precomputed RGB palette derived from the base space (§8.6.6.3).</summary>
internal sealed class IndexedColorSpace : PdfColorSpace
{
    private readonly byte[] _palette; // (hival+1) * 3
    private readonly int _hival;

    private IndexedColorSpace(byte[] palette, int hival)
    {
        _palette = palette;
        _hival = hival;
    }

    public override int ComponentCount => 1;

    public override string Family => "Indexed";

    public override double[] DefaultDecode(int bitsPerComponent)
        => new[] { 0.0, (1 << bitsPerComponent) - 1.0 };

    public static IndexedColorSpace Create(PdfArray array, ResolveContext ctx, int depth)
    {
        // [/Indexed base hival lookup]
        RequireCount(array, 4, "Indexed");
        Func<PdfObject, PdfObject> resolve = ctx.Resolve;
        PdfColorSpace baseCs = ResolveNested(array[1], ctx, depth);

        // §8.6.6.3: hival is 0..255; clamp so a huge value cannot size the palette.
        double hv = resolve(array[2]) is PdfNumber n ? n.AsDouble : 0.0;
        int hival = hv >= Limits.MaxIndexedHival ? Limits.MaxIndexedHival : (hv > 0 ? (int)hv : 0);

        byte[] lookup = ReadLookup(resolve(array[3]), ctx.DecodeStream);
        int baseComps = baseCs.ComponentCount;
        double[] baseDecode = baseCs.DefaultDecode(8);

        var palette = new byte[(hival + 1) * 3];
        var comp = new double[baseComps];
        for (int i = 0; i <= hival; i++)
        {
            for (int k = 0; k < baseComps; k++)
            {
                long li = ((long)i * baseComps) + k;
                double sample = li < lookup.Length ? lookup[li] : 0.0;
                double lo = (2 * k) < baseDecode.Length ? baseDecode[2 * k] : 0.0;
                double hi = ((2 * k) + 1) < baseDecode.Length ? baseDecode[(2 * k) + 1] : 1.0;
                comp[k] = lo + ((sample / 255.0) * (hi - lo));
            }

            baseCs.ToRgb(comp, 0, out byte r, out byte g, out byte b);
            palette[(i * 3) + 0] = r;
            palette[(i * 3) + 1] = g;
            palette[(i * 3) + 2] = b;
        }

        return new IndexedColorSpace(palette, hival);
    }

    public override void ToRgb(double[] comps, int offset, out byte r, out byte g, out byte b)
    {
        // Clamp in floating point before converting: NaN and out-of-range values must not reach the int cast.
        double v = Math.Round(comps[offset], MidpointRounding.AwayFromZero);
        int idx = v >= _hival ? _hival : (v > 0 ? (int)v : 0);

        r = _palette[(idx * 3) + 0];
        g = _palette[(idx * 3) + 1];
        b = _palette[(idx * 3) + 2];
    }

    private static byte[] ReadLookup(PdfObject lookupObj, Func<PdfStream, byte[]> decodeStream)
    {
        return lookupObj switch
        {
            PdfString s => s.ToArray(),
            PdfStream st => decodeStream(st),
            _ => Array.Empty<byte>(),
        };
    }
}

/// <summary>Separation / DeviceN — n tint colorants mapped through a tint transform to an alternate space.</summary>
internal sealed class SeparationColorSpace : PdfColorSpace
{
    private readonly int _n;
    private readonly PdfColorSpace _alternate;
    private readonly PdfFunction _tint;
    private readonly string _family;

    private SeparationColorSpace(int n, PdfColorSpace alternate, PdfFunction tint, string family)
    {
        _n = n;
        _alternate = alternate;
        _tint = tint;
        _family = family;
    }

    public override int ComponentCount => _n;

    public override string Family => _family;

    public override double[] DefaultDecode(int bitsPerComponent)
    {
        var d = new double[2 * _n];
        for (int i = 0; i < _n; i++)
        {
            d[2 * i] = 0.0;
            d[(2 * i) + 1] = 1.0;
        }

        return d;
    }

    /// <summary>Work units one tint conversion costs (the tint transform plus the alternate space).</summary>
    internal long EvaluationCost => _tint.Cost + 1;

    public static SeparationColorSpace Create(PdfArray array, ResolveContext ctx, int depth)
    {
        // [/Separation name alternateSpace tintTransform]
        RequireCount(array, 4, "Separation");
        return Build(1, array, ctx, depth, "Separation");
    }

    public static SeparationColorSpace CreateDeviceN(PdfArray array, ResolveContext ctx, int depth)
    {
        // [/DeviceN names alternateSpace tintTransform attributes?]
        RequireCount(array, 4, "DeviceN");
        int n = ctx.Resolve(array[1]) is PdfArray names ? names.Count : 1;
        if (n < 1 || n > Limits.MaxComponents)
        {
            throw new NotSupportedException($"DeviceN with {n} colorants is not supported.");
        }

        return Build(n, array, ctx, depth, "DeviceN");
    }

    private static SeparationColorSpace Build(int n, PdfArray array, ResolveContext ctx, int depth, string family)
    {
        PdfColorSpace alt = ResolveNested(array[2], ctx, depth);
        PdfFunction tint = PdfFunction.Parse(array[3], ctx.Resolve, ctx.DecodeStream);
        if (tint.OutputCount < alt.ComponentCount)
        {
            throw new NotSupportedException(
                $"{family} tint transform yields {tint.OutputCount} outputs; the alternate space needs {alt.ComponentCount}.");
        }

        return new SeparationColorSpace(n, alt, tint, family);
    }

    public override void ToRgb(double[] comps, int offset, out byte r, out byte g, out byte b)
    {
        var tintIn = new double[_n];
        for (int i = 0; i < _n; i++)
        {
            tintIn[i] = comps[offset + i];
        }

        double[] alt = _tint.Evaluate(tintIn);
        if (alt.Length < _alternate.ComponentCount)
        {
            // A stitching sub-function may yield fewer outputs than the first one declared.
            Array.Resize(ref alt, _alternate.ComponentCount);
        }

        _alternate.ToRgb(alt, 0, out r, out g, out b);
    }
}
