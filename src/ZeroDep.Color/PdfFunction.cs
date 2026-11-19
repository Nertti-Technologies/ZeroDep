using System;
using System.Collections.Generic;
using ZeroDep.Abstractions;
using ZeroDep.Objects;

namespace ZeroDep.Color;

/// <summary>
/// A PDF function (ISO 32000-2 §7.10): a mapping from <see cref="InputCount"/> inputs to
/// <see cref="OutputCount"/> outputs. Supports the four sampled/analytic types used by tint transforms,
/// shadings, and transfer functions: Type 0 (sampled), Type 2 (exponential interpolation), Type 3
/// (stitching), and Type 4 (PostScript calculator). An array of one-output functions is also accepted and
/// combined. Pure-BCL and deterministic; no rendering.
/// </summary>
public abstract class PdfFunction
{
    /// <summary>Creates a function with the given domain and (optional) range bounds.</summary>
    /// <param name="domain">Flat [min0,max0, min1,max1, …] over the inputs.</param>
    /// <param name="range">Flat [min0,max0, …] over the outputs, or null when the type does not require it.</param>
    protected PdfFunction(double[] domain, double[]? range)
    {
        Domain = domain ?? throw new ArgumentNullException(nameof(domain));
        Range = range;
    }

    /// <summary>Input bounds, flat: [min0,max0, min1,max1, …].</summary>
    public double[] Domain { get; }

    /// <summary>Output bounds, flat: [min0,max0, …], or null.</summary>
    public double[]? Range { get; }

    /// <summary>Number of inputs.</summary>
    public int InputCount => Domain.Length / 2;

    /// <summary>Number of outputs.</summary>
    public abstract int OutputCount { get; }

    /// <summary>
    /// Upper bound on the work units one evaluation costs (one unit per operation), used to budget
    /// per-pixel tint conversion. Saturates instead of overflowing.
    /// </summary>
    internal virtual long Cost => 1;

    /// <summary>
    /// Evaluates the function: clamps inputs to <see cref="Domain"/>, evaluates, and clamps outputs to
    /// <see cref="Range"/> when present.
    /// </summary>
    /// <param name="input">The input values (length should equal <see cref="InputCount"/>).</param>
    public double[] Evaluate(double[] input)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        int m = InputCount;
        var x = new double[m];
        for (int i = 0; i < m; i++)
        {
            double v = i < input.Length ? input[i] : 0.0;
            if (double.IsNaN(v))
            {
                v = Domain[2 * i]; // NaN would pass the clamp and reach integer conversions
            }

            x[i] = Clamp(v, Domain[2 * i], Domain[(2 * i) + 1]);
        }

        double[] y = EvaluateCore(x);

        if (Range is { } r)
        {
            int n = Math.Min(y.Length, r.Length / 2);
            for (int j = 0; j < n; j++)
            {
                y[j] = Clamp(y[j], r[2 * j], r[(2 * j) + 1]);
            }
        }

        return y;
    }

    /// <summary>Evaluates the function on inputs already clamped to <see cref="Domain"/>.</summary>
    /// <param name="input">The domain-clamped inputs.</param>
    protected abstract double[] EvaluateCore(double[] input);

    /// <summary>
    /// Parses a PDF function object (dictionary, stream, or an array of one-output functions).
    /// </summary>
    /// <param name="obj">The function object (may be an indirect reference).</param>
    /// <param name="resolve">Resolves indirect references to direct objects.</param>
    /// <param name="decodeStream">Decodes a stream's filters to its raw bytes (for Type 0 and Type 4).</param>
    /// <exception cref="NotSupportedException">The function type is unsupported or its entries are malformed.</exception>
    /// <exception cref="PdfSyntaxException">
    /// The function refers back to itself; or it nests deeper than the fixed limit, has too many
    /// sub-functions, or (Type 4) nests braces deeper than the fixed limit, reported as the derived
    /// <see cref="PdfLimitExceededException"/>.
    /// </exception>
    public static PdfFunction Parse(PdfObject obj, Func<PdfObject, PdfObject> resolve, Func<PdfStream, byte[]> decodeStream)
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

        return Parse(obj, new ParseContext(resolve, decodeStream), 0);
    }

    // Recursive parse. 'depth' bounds nesting; the context's active set holds the object numbers on the
    // current path (a function that contains itself is reported), and its node count bounds the total
    // number of sub-functions, which also bounds fan-out through shared sub-functions.
    private protected static PdfFunction Parse(PdfObject obj, ParseContext ctx, int depth)
    {
        if (depth > Limits.MaxFunctionDepth)
        {
            throw new PdfLimitExceededException(
                nameof(Limits.MaxFunctionDepth), Limits.MaxFunctionDepth, $"Function nesting exceeds the limit of {Limits.MaxFunctionDepth}.");
        }

        if (++ctx.Nodes > Limits.MaxFunctionNodes)
        {
            throw new PdfLimitExceededException(
                nameof(Limits.MaxFunctionNodes), Limits.MaxFunctionNodes, $"Function has more than {Limits.MaxFunctionNodes} sub-functions.");
        }

        int objectNumber = obj is PdfReference reference ? reference.ObjectNumber : -1;
        if (objectNumber >= 0 && !ctx.Active.Add(objectNumber))
        {
            throw new PdfSyntaxException("Function refers to itself.");
        }

        try
        {
            return ParseCore(obj, ctx, depth);
        }
        finally
        {
            if (objectNumber >= 0)
            {
                ctx.Active.Remove(objectNumber);
            }
        }
    }

    private static PdfFunction ParseCore(PdfObject obj, ParseContext ctx, int depth)
    {
        Func<PdfObject, PdfObject> resolve = ctx.Resolve;
        PdfObject ro = resolve(obj);

        if (ro is PdfArray array)
        {
            var parts = new List<PdfFunction>(array.Count);
            foreach (PdfObject item in array.Items)
            {
                parts.Add(Parse(item, ctx, depth + 1));
            }

            return new FunctionArray(parts.ToArray());
        }

        PdfDictionary dict;
        byte[]? streamData = null;
        if (ro is PdfStream stream)
        {
            dict = stream.Dictionary;
            streamData = ctx.DecodeStream(stream);
        }
        else if (ro is PdfDictionary d)
        {
            dict = d;
        }
        else
        {
            throw new NotSupportedException("Unsupported PDF function object.");
        }

        double typeNum = Num(dict["FunctionType"], resolve) ?? -1;
        int type = typeNum >= 0 && typeNum <= 4 ? (int)typeNum : -1;
        double[] domain = NumArray(dict["Domain"], resolve) ?? new double[] { 0.0, 1.0 };
        double[]? range = NumArray(dict["Range"], resolve);

        return type switch
        {
            0 => SampledFunction.Create(dict, streamData ?? Array.Empty<byte>(), domain, range, resolve),
            2 => ExponentialFunction.Create(dict, domain, range, resolve),
            3 => StitchingFunction.Create(dict, domain, range, ctx, depth),
            4 => PostScriptFunction.Create(streamData ?? Array.Empty<byte>(), domain, range),
            _ => throw new NotSupportedException($"PDF function type {type} is not supported."),
        };
    }

    /// <summary>Adds two non-negative costs, saturating at a large bound instead of overflowing.</summary>
    private protected static long AddCost(long a, long b)
    {
        const long Cap = long.MaxValue / 4;
        return a >= Cap - b ? Cap : a + b;
    }

    /// <summary>Multiplies two non-negative costs, saturating at a large bound instead of overflowing.</summary>
    private protected static long MulCost(long a, long b)
    {
        const long Cap = long.MaxValue / 4;
        return b != 0 && a > Cap / b ? Cap : a * b;
    }

    /// <summary>Converts a double to int, mapping NaN to 0 and saturating out-of-range values.</summary>
    private protected static int ToInt(double v)
    {
        if (double.IsNaN(v))
        {
            return 0;
        }

        return v >= int.MaxValue ? int.MaxValue : (v <= int.MinValue ? int.MinValue : (int)v);
    }

    private protected static double Clamp(double v, double lo, double hi)
    {
        if (hi < lo)
        {
            (lo, hi) = (hi, lo);
        }

        return v < lo ? lo : (v > hi ? hi : v);
    }

    private protected static double Interpolate(double x, double xMin, double xMax, double yMin, double yMax)
    {
        if (xMax == xMin)
        {
            return yMin;
        }

        return yMin + ((x - xMin) * (yMax - yMin) / (xMax - xMin));
    }

    private protected static double? Num(PdfObject? o, Func<PdfObject, PdfObject> resolve)
        => resolve(o ?? PdfNull.Instance) is PdfNumber n ? n.AsDouble : null;

    private protected static double[]? NumArray(PdfObject? o, Func<PdfObject, PdfObject> resolve)
    {
        if (resolve(o ?? PdfNull.Instance) is not PdfArray a)
        {
            return null;
        }

        var result = new double[a.Count];
        for (int i = 0; i < a.Count; i++)
        {
            result[i] = resolve(a[i]) is PdfNumber n ? n.AsDouble : 0.0;
        }

        return result;
    }

    private protected static int[]? IntArray(PdfObject? o, Func<PdfObject, PdfObject> resolve)
    {
        double[]? d = NumArray(o, resolve);
        if (d is null)
        {
            return null;
        }

        var result = new int[d.Length];
        for (int i = 0; i < d.Length; i++)
        {
            result[i] = ToInt(Math.Round(d[i]));
        }

        return result;
    }
}

/// <summary>The delegates, active-reference set, and node count shared by one <see cref="PdfFunction.Parse(PdfObject, Func{PdfObject, PdfObject}, Func{PdfStream, byte[]})"/> call.</summary>
internal sealed class ParseContext
{
    public ParseContext(Func<PdfObject, PdfObject> resolve, Func<PdfStream, byte[]> decodeStream)
    {
        Resolve = resolve;
        DecodeStream = decodeStream;
    }

    public Func<PdfObject, PdfObject> Resolve { get; }

    public Func<PdfStream, byte[]> DecodeStream { get; }

    /// <summary>Object numbers of the functions being parsed on the current path.</summary>
    public HashSet<int> Active { get; } = new HashSet<int>();

    /// <summary>Function objects parsed so far, including sub-functions.</summary>
    public int Nodes { get; set; }
}

/// <summary>An array of one-output functions presented as a single multi-output function (§7.10.1).</summary>
internal sealed class FunctionArray : PdfFunction
{
    private readonly PdfFunction[] _parts;

    public FunctionArray(PdfFunction[] parts)
        : base(parts.Length > 0 ? parts[0].Domain : new double[] { 0.0, 1.0 }, null)
        => _parts = parts;

    public override int OutputCount
    {
        get
        {
            int n = 0;
            foreach (PdfFunction p in _parts)
            {
                n += p.OutputCount;
            }

            return n;
        }
    }

    internal override long Cost
    {
        get
        {
            long c = 1;
            foreach (PdfFunction p in _parts)
            {
                c = AddCost(c, p.Cost);
            }

            return c;
        }
    }

    protected override double[] EvaluateCore(double[] input)
    {
        var outv = new double[OutputCount];
        int k = 0;
        foreach (PdfFunction p in _parts)
        {
            double[] y = p.Evaluate(input);
            Array.Copy(y, 0, outv, k, y.Length);
            k += y.Length;
        }

        return outv;
    }
}

/// <summary>Type 2 — exponential interpolation: y_j = C0_j + x^N · (C1_j − C0_j) (§7.10.3).</summary>
internal sealed class ExponentialFunction : PdfFunction
{
    private readonly double[] _c0;
    private readonly double[] _c1;
    private readonly double _n;

    private ExponentialFunction(double[] domain, double[]? range, double[] c0, double[] c1, double n)
        : base(domain, range)
    {
        _c0 = c0;
        _c1 = c1;
        _n = n;
    }

    public override int OutputCount => _c0.Length;

    internal override long Cost => _c0.Length + 1;

    public static ExponentialFunction Create(PdfDictionary dict, double[] domain, double[]? range, Func<PdfObject, PdfObject> resolve)
    {
        double[] c0 = NumArray(dict["C0"], resolve) ?? new[] { 0.0 };
        double[] c1 = NumArray(dict["C1"], resolve) ?? new[] { 1.0 };
        double n = Num(dict["N"], resolve) ?? 1.0;
        return new ExponentialFunction(domain, range, c0, c1, n);
    }

    protected override double[] EvaluateCore(double[] input)
    {
        double x = input.Length > 0 ? input[0] : 0.0;
        double xn = _n == 1.0 ? x : Math.Pow(x, _n);
        var y = new double[_c0.Length];
        for (int j = 0; j < y.Length; j++)
        {
            double c1 = j < _c1.Length ? _c1[j] : 0.0;
            y[j] = _c0[j] + (xn * (c1 - _c0[j]));
        }

        return y;
    }
}

/// <summary>Type 3 — stitching: routes the single input to one of k sub-functions by bounds (§7.10.4).</summary>
internal sealed class StitchingFunction : PdfFunction
{
    private readonly PdfFunction[] _functions;
    private readonly double[] _bounds;
    private readonly double[] _encode;

    private StitchingFunction(double[] domain, double[]? range, PdfFunction[] functions, double[] bounds, double[] encode)
        : base(domain, range)
    {
        _functions = functions;
        _bounds = bounds;
        _encode = encode;
    }

    public override int OutputCount => _functions.Length > 0 ? _functions[0].OutputCount : 0;

    internal override long Cost
    {
        get
        {
            // One sub-function runs per evaluation; charge the most expensive.
            long c = 0;
            foreach (PdfFunction f in _functions)
            {
                c = Math.Max(c, f.Cost);
            }

            return AddCost(c, _functions.Length + 1);
        }
    }

    public static StitchingFunction Create(PdfDictionary dict, double[] domain, double[]? range, ParseContext ctx, int depth)
    {
        Func<PdfObject, PdfObject> resolve = ctx.Resolve;
        var functions = new List<PdfFunction>();
        if (resolve(dict["Functions"] ?? PdfNull.Instance) is PdfArray fns)
        {
            foreach (PdfObject f in fns.Items)
            {
                functions.Add(Parse(f, ctx, depth + 1));
            }
        }

        double[] bounds = NumArray(dict["Bounds"], resolve) ?? Array.Empty<double>();
        double[] encode = NumArray(dict["Encode"], resolve) ?? Array.Empty<double>();
        if (functions.Count > 1 && bounds.Length < functions.Count - 1)
        {
            throw new NotSupportedException("Stitching function /Bounds is shorter than /Functions requires.");
        }

        return new StitchingFunction(domain, range, functions.ToArray(), bounds, encode);
    }

    protected override double[] EvaluateCore(double[] input)
    {
        int k = _functions.Length;
        if (k == 0)
        {
            return new double[OutputCount];
        }

        double x = input.Length > 0 ? input[0] : 0.0;

        int i = 0;
        while (i < k - 1 && x >= _bounds[i])
        {
            i++;
        }

        double lo = i == 0 ? Domain[0] : _bounds[i - 1];
        double hi = i == k - 1 ? Domain[1] : _bounds[i];
        double eLo = (2 * i) < _encode.Length ? _encode[2 * i] : 0.0;
        double eHi = ((2 * i) + 1) < _encode.Length ? _encode[(2 * i) + 1] : 1.0;

        double e = Interpolate(x, lo, hi, eLo, eHi);
        return _functions[i].Evaluate(new[] { e });
    }
}

/// <summary>Type 0 — sampled function with multilinear interpolation over the sample grid (§7.10.2).</summary>
internal sealed class SampledFunction : PdfFunction
{
    private readonly int[] _size;
    private readonly int _bps;
    private readonly double[] _encode;
    private readonly double[] _decode;
    private readonly double[] _range;
    private readonly byte[] _samples;
    private readonly double _maxSample;

    private SampledFunction(double[] domain, double[] range, int[] size, int bps, double[] encode, double[] decode, byte[] samples)
        : base(domain, range)
    {
        _size = size;
        _bps = bps;
        _encode = encode;
        _decode = decode;
        _range = range;
        _samples = samples;
        _maxSample = bps >= 32 ? 4294967295.0 : (double)((1L << bps) - 1);
    }

    public override int OutputCount => _range.Length / 2;

    // 2^m grid corners, each visiting m inputs and reading n samples.
    internal override long Cost => MulCost(1L << InputCount, InputCount + OutputCount + 1);

    public static SampledFunction Create(PdfDictionary dict, byte[] data, double[] domain, double[]? range, Func<PdfObject, PdfObject> resolve)
    {
        int[] size = IntArray(dict["Size"], resolve) ?? Array.Empty<int>();
        double bpsNum = Num(dict["BitsPerSample"], resolve) ?? 8;
        int bps = ToInt(bpsNum);
        if (bps != bpsNum || !IsAllowedBitsPerSample(bps))
        {
            throw new NotSupportedException($"Sampled function BitsPerSample {bpsNum} is not one of 1, 2, 4, 8, 12, 16, 24, 32.");
        }

        double[] rng = range ?? Array.Empty<double>();
        int m = domain.Length / 2;
        int n = rng.Length / 2;
        if (m > Limits.MaxSampledInputs)
        {
            throw new NotSupportedException($"Sampled function has {m} inputs; at most {Limits.MaxSampledInputs} are supported.");
        }

        if (n > Limits.MaxComponents)
        {
            throw new NotSupportedException($"Sampled function has {n} outputs; at most {Limits.MaxComponents} are supported.");
        }

        for (int i = 0; i < m && i < size.Length; i++)
        {
            if (size[i] < 1)
            {
                throw new NotSupportedException("Sampled function /Size entries must be positive.");
            }
        }

        double[] encode = NumArray(dict["Encode"], resolve) ?? DefaultEncode(size, m);
        double[] decode = NumArray(dict["Decode"], resolve) ?? rng;
        if (encode.Length < 2 * m || decode.Length < 2 * n)
        {
            throw new NotSupportedException("Sampled function /Encode or /Decode is shorter than its inputs or outputs require.");
        }

        return new SampledFunction(domain, rng, size, bps, encode, decode, data);
    }

    // ISO 32000-2 Table 39: BitsPerSample is 1, 2, 4, 8, 12, 16, 24, or 32.
    private static bool IsAllowedBitsPerSample(int bps)
        => bps is 1 or 2 or 4 or 8 or 12 or 16 or 24 or 32;

    private static double[] DefaultEncode(int[] size, int m)
    {
        var e = new double[2 * m];
        for (int i = 0; i < m; i++)
        {
            e[2 * i] = 0.0;
            e[(2 * i) + 1] = (i < size.Length ? size[i] : 1) - 1;
        }

        return e;
    }

    protected override double[] EvaluateCore(double[] input)
    {
        int m = InputCount;
        int n = OutputCount;
        var outv = new double[n];
        if (m == 0 || n == 0 || _size.Length < m)
        {
            return outv;
        }

        // Encode each input to a grid coordinate in [0, size_i - 1].
        var e = new double[m];
        for (int i = 0; i < m; i++)
        {
            double ei = Interpolate(input[i], Domain[2 * i], Domain[(2 * i) + 1], _encode[2 * i], _encode[(2 * i) + 1]);
            e[i] = Clamp(ei, 0, _size[i] - 1);
        }

        // Multilinear interpolation over the 2^m surrounding grid corners.
        int corners = 1 << m;
        for (int c = 0; c < corners; c++)
        {
            double w = 1.0;
            long idx = 0;
            long stride = 1;
            for (int i = 0; i < m; i++)
            {
                int lo = (int)Math.Floor(e[i]);
                double f = e[i] - lo;
                int bit = (c >> i) & 1;
                int coord = lo + bit;
                if (coord > _size[i] - 1)
                {
                    coord = _size[i] - 1;
                }

                if (coord < 0)
                {
                    coord = 0;
                }

                w *= bit == 1 ? f : (1.0 - f);
                idx += coord * stride;
                stride *= _size[i];
            }

            if (w == 0.0)
            {
                continue;
            }

            for (int j = 0; j < n; j++)
            {
                double s = ReadSample((idx * n) + j);
                double dec = Interpolate(s, 0.0, _maxSample, _decode[2 * j], _decode[(2 * j) + 1]);
                outv[j] += w * dec;
            }
        }

        return outv;
    }

    private double ReadSample(long sampleIndex)
    {
        long bitPos = sampleIndex * _bps;
        long v = 0;
        if ((_bps & 7) == 0)
        {
            long first = bitPos >> 3;
            for (int i = 0; i < _bps >> 3; i++)
            {
                long bytePos = first + i;
                v = (v << 8) | (bytePos >= 0 && bytePos < _samples.Length ? _samples[bytePos] : 0u);
            }

            return v;
        }

        for (int i = 0; i < _bps; i++)
        {
            long bp = bitPos + i;
            long bytePos = bp >> 3;
            int bit = 7 - (int)(bp & 7);
            int b = bytePos >= 0 && bytePos < _samples.Length ? (_samples[bytePos] >> bit) & 1 : 0;
            v = (v << 1) | (uint)b;
        }

        return v;
    }
}
