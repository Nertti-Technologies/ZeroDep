using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ZeroDep.Abstractions;

namespace ZeroDep.Color;

/// <summary>
/// Type 4 — PostScript calculator function (ISO 32000-2 §7.10.5). A small, bounded stack machine over the
/// allowed arithmetic, comparison, boolean, and stack operators plus <c>if</c>/<c>ifelse</c>. Inputs are
/// pushed onto the stack; after execution the top <see cref="OutputCount"/> values are the outputs.
/// Execution is bounded: at most <see cref="Limits.MaxPostScriptSteps"/> steps (the element counts of
/// <c>copy</c>/<c>index</c>/<c>roll</c> included) and <see cref="Limits.MaxPostScriptStack"/> operands; a
/// program that exceeds either stops, leaving the stack as it was at that point.
/// </summary>
internal sealed class PostScriptFunction : PdfFunction
{
    private readonly PsProc _program;
    private readonly int _outputs;
    private readonly long _cost;

    private PostScriptFunction(double[] domain, double[] range, PsProc program)
        : base(domain, range)
    {
        _program = program;
        _outputs = range.Length / 2;
        _cost = program.TokenCount + 1;
    }

    public override int OutputCount => _outputs;

    // There are no loops in the calculator language, so one evaluation runs each token at most once.
    internal override long Cost => _cost;

    public static PostScriptFunction Create(byte[] data, double[] domain, double[]? range)
    {
        var text = Encoding.ASCII.GetString(data);
        PsProc program = Parse(text);
        return new PostScriptFunction(domain, range ?? Array.Empty<double>(), program);
    }

    protected override double[] EvaluateCore(double[] input)
    {
        var stack = new List<double>(input.Length + 16);
        stack.AddRange(input);

        int steps = 0;
        Execute(_program, stack, ref steps, 0);

        int n = _outputs;
        var outv = new double[n];
        int start = stack.Count - n;
        for (int j = 0; j < n; j++)
        {
            int idx = start + j;
            outv[j] = idx >= 0 && idx < stack.Count ? stack[idx] : 0.0;
        }

        return outv;
    }

    // ---- execution ----

    // 'depth' is the procedure nesting level; the parser already bounds it, the check keeps Execute safe
    // on its own. Exceeding the stack or step limit sets steps past the limit, which unwinds every level.
    private static void Execute(PsProc proc, List<double> s, ref int steps, int depth)
    {
        if (depth > Limits.MaxPostScriptBraceDepth)
        {
            steps = Limits.MaxPostScriptSteps + 1;
            return;
        }

        var pendingProcs = new List<PsProc>(2);

        foreach (PsToken t in proc.Tokens)
        {
            if (++steps > Limits.MaxPostScriptSteps)
            {
                return;
            }

            if (t.IsNumber)
            {
                if (s.Count >= Limits.MaxPostScriptStack)
                {
                    steps = Limits.MaxPostScriptSteps + 1;
                    return;
                }

                s.Add(t.Number);
                continue;
            }

            if (t.Proc is { } nested)
            {
                pendingProcs.Add(nested);
                continue;
            }

            switch (t.Op)
            {
                case "if":
                {
                    PsProc body = pendingProcs.Count > 0 ? pendingProcs[pendingProcs.Count - 1] : EmptyProc;
                    pendingProcs.Clear();
                    bool cond = Pop(s) != 0.0;
                    if (cond)
                    {
                        Execute(body, s, ref steps, depth + 1);
                    }

                    break;
                }

                case "ifelse":
                {
                    PsProc p2 = pendingProcs.Count > 1 ? pendingProcs[pendingProcs.Count - 1] : EmptyProc;
                    PsProc p1 = pendingProcs.Count > 1 ? pendingProcs[pendingProcs.Count - 2] : EmptyProc;
                    pendingProcs.Clear();
                    bool cond = Pop(s) != 0.0;
                    Execute(cond ? p1 : p2, s, ref steps, depth + 1);
                    break;
                }

                default:
                    pendingProcs.Clear();
                    if (!ApplyOperator(t.Op, s, ref steps) || s.Count > Limits.MaxPostScriptStack)
                    {
                        steps = Limits.MaxPostScriptSteps + 1;
                        return;
                    }

                    break;
            }

            if (steps > Limits.MaxPostScriptSteps)
            {
                return;
            }
        }
    }

    // Returns false when the operator would exceed the operand stack (the program then stops).
    private static bool ApplyOperator(string op, List<double> s, ref int steps)
    {
        switch (op)
        {
            // arithmetic
            case "add": { double b = Pop(s), a = Pop(s); s.Add(a + b); break; }
            case "sub": { double b = Pop(s), a = Pop(s); s.Add(a - b); break; }
            case "mul": { double b = Pop(s), a = Pop(s); s.Add(a * b); break; }
            case "div": { double b = Pop(s), a = Pop(s); s.Add(b == 0 ? 0 : a / b); break; }
            case "idiv": { long b = ToLong(Pop(s)), a = ToLong(Pop(s)); s.Add(IntDiv(a, b)); break; }
            case "mod": { long b = ToLong(Pop(s)), a = ToLong(Pop(s)); s.Add(b == 0 || b == -1 ? 0 : a % b); break; }
            case "neg": s.Add(-Pop(s)); break;
            case "abs": s.Add(Math.Abs(Pop(s))); break;
            case "sqrt": s.Add(Math.Sqrt(Math.Max(0, Pop(s)))); break;
            case "sin": s.Add(Math.Sin(Pop(s) * Math.PI / 180.0)); break;
            case "cos": s.Add(Math.Cos(Pop(s) * Math.PI / 180.0)); break;
            case "atan": { double den = Pop(s), num = Pop(s); double d = Math.Atan2(num, den) * 180.0 / Math.PI; if (d < 0) d += 360.0; s.Add(d); break; }
            case "exp": { double e = Pop(s), b = Pop(s); s.Add(Math.Pow(b, e)); break; }
            case "ln": s.Add(Math.Log(Math.Max(double.Epsilon, Pop(s)))); break;
            case "log": s.Add(Math.Log10(Math.Max(double.Epsilon, Pop(s)))); break;
            case "cvi": s.Add(ToLong(Pop(s))); break;
            case "cvr": break; // already real
            case "floor": s.Add(Math.Floor(Pop(s))); break;
            case "ceiling": s.Add(Math.Ceiling(Pop(s))); break;
            case "round": s.Add(Math.Round(Pop(s), MidpointRounding.AwayFromZero)); break;
            case "truncate": s.Add(Math.Truncate(Pop(s))); break;

            // comparison -> boolean (1/0)
            case "eq": { double b = Pop(s), a = Pop(s); s.Add(a == b ? 1 : 0); break; }
            case "ne": { double b = Pop(s), a = Pop(s); s.Add(a != b ? 1 : 0); break; }
            case "gt": { double b = Pop(s), a = Pop(s); s.Add(a > b ? 1 : 0); break; }
            case "ge": { double b = Pop(s), a = Pop(s); s.Add(a >= b ? 1 : 0); break; }
            case "lt": { double b = Pop(s), a = Pop(s); s.Add(a < b ? 1 : 0); break; }
            case "le": { double b = Pop(s), a = Pop(s); s.Add(a <= b ? 1 : 0); break; }

            // boolean / bitwise
            case "and": { long b = ToLong(Pop(s)), a = ToLong(Pop(s)); s.Add(a & b); break; }
            case "or": { long b = ToLong(Pop(s)), a = ToLong(Pop(s)); s.Add(a | b); break; }
            case "xor": { long b = ToLong(Pop(s)), a = ToLong(Pop(s)); s.Add(a ^ b); break; }
            case "not": { double a = Pop(s); s.Add(a == 0 ? 1 : 0); break; }
            case "bitshift": { int sh = ToInt(Pop(s)); long a = ToLong(Pop(s)); s.Add(sh >= 64 || sh <= -64 ? 0 : (sh >= 0 ? a << sh : a >> -sh)); break; }
            case "true": s.Add(1); break;
            case "false": s.Add(0); break;

            // stack operators
            case "pop": Pop(s); break;
            case "exch": { double b = Pop(s), a = Pop(s); s.Add(b); s.Add(a); break; }
            case "dup": { double a = Peek(s); s.Add(a); break; }
            case "copy":
            {
                int nn = ToInt(Pop(s));
                int count = s.Count;
                if (nn <= 0)
                {
                    break;
                }

                // The element count is charged to the step budget, and the copy must fit the stack.
                if (nn > Limits.MaxPostScriptStack - count)
                {
                    return false;
                }

                steps += nn;
                for (int i = 0; i < nn; i++)
                {
                    int idx = count - nn + i;
                    s.Add(idx >= 0 && idx < count ? s[idx] : 0.0);
                }

                break;
            }

            case "index":
            {
                int nn = ToInt(Pop(s));
                if (nn > 0)
                {
                    steps += Math.Min(nn, Limits.MaxPostScriptStack);
                }

                int idx = s.Count - 1 - nn;
                s.Add(nn >= 0 && idx >= 0 && idx < s.Count ? s[idx] : 0.0);
                break;
            }

            case "roll":
            {
                int j = ToInt(Pop(s));
                int nn = ToInt(Pop(s));
                if (nn > 0)
                {
                    steps += Math.Min(nn, Limits.MaxPostScriptStack);
                }

                Roll(s, nn, j);
                break;
            }

            default:
                break; // unknown token: ignore (keep deterministic, never throw mid-eval)
        }

        return true;
    }

    // NaN maps to 0 and out-of-range values saturate: netstandard2.0 does not saturate (long)/(int) casts.
    private static long ToLong(double v)
    {
        if (double.IsNaN(v))
        {
            return 0;
        }

        return v >= 9.2233720368547758E+18 ? long.MaxValue : (v <= -9.2233720368547758E+18 ? long.MinValue : (long)v);
    }

    // long.MinValue / -1 overflows; negate instead (wrapping, like the two's-complement result).
    private static long IntDiv(long a, long b)
    {
        if (b == 0)
        {
            return 0;
        }

        return b == -1 ? unchecked(-a) : a / b;
    }

    private static void Roll(List<double> s, int n, int j)
    {
        if (n <= 0 || n > s.Count)
        {
            return;
        }

        int start = s.Count - n;
        var slice = new double[n];
        for (int i = 0; i < n; i++)
        {
            slice[i] = s[start + i];
        }

        j = ((j % n) + n) % n; // normalize, positive = roll toward top
        var rolled = new double[n];
        for (int i = 0; i < n; i++)
        {
            rolled[(i + j) % n] = slice[i];
        }

        for (int i = 0; i < n; i++)
        {
            s[start + i] = rolled[i];
        }
    }

    private static double Pop(List<double> s)
    {
        if (s.Count == 0)
        {
            return 0.0;
        }

        double v = s[s.Count - 1];
        s.RemoveAt(s.Count - 1);
        return v;
    }

    private static double Peek(List<double> s) => s.Count == 0 ? 0.0 : s[s.Count - 1];

    // ---- parsing ----

    private static readonly PsProc EmptyProc = new PsProc(new List<PsToken>());

    private static PsProc Parse(string text)
    {
        List<string> raw = Tokenize(text);
        int pos = 0;

        // Skip to the outer '{'.
        while (pos < raw.Count && raw[pos] != "{")
        {
            pos++;
        }

        if (pos >= raw.Count)
        {
            return EmptyProc;
        }

        pos++; // consume outer '{'
        return ParseProc(raw, ref pos, 1);
    }

    private static PsProc ParseProc(List<string> raw, ref int pos, int depth)
    {
        if (depth > Limits.MaxPostScriptBraceDepth)
        {
            throw new PdfSyntaxException($"PostScript function braces nest deeper than {Limits.MaxPostScriptBraceDepth}.");
        }

        var tokens = new List<PsToken>();
        while (pos < raw.Count)
        {
            string tok = raw[pos++];
            if (tok == "}")
            {
                break;
            }

            if (tok == "{")
            {
                PsProc nested = ParseProc(raw, ref pos, depth + 1);
                tokens.Add(PsToken.FromProc(nested));
                continue;
            }

            if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            {
                tokens.Add(PsToken.FromNumber(num));
            }
            else
            {
                tokens.Add(PsToken.FromOp(tok));
            }
        }

        return new PsProc(tokens);
    }

    private static List<string> Tokenize(string text)
    {
        var result = new List<string>();
        var sb = new StringBuilder();

        void Flush()
        {
            if (sb.Length > 0)
            {
                result.Add(sb.ToString());
                sb.Clear();
            }
        }

        foreach (char c in text)
        {
            if (c == '{' || c == '}')
            {
                Flush();
                result.Add(c.ToString());
            }
            else if (char.IsWhiteSpace(c))
            {
                Flush();
            }
            else if (c == '%')
            {
                // PostScript comment to end of line — treat '%' as a separator (rare in functions).
                Flush();
            }
            else
            {
                sb.Append(c);
            }
        }

        Flush();
        return result;
    }

    private sealed class PsProc
    {
        public PsProc(List<PsToken> tokens)
        {
            Tokens = tokens;
            long count = tokens.Count;
            foreach (PsToken t in tokens)
            {
                if (t.Proc is { } nested)
                {
                    count += nested.TokenCount;
                }
            }

            TokenCount = count;
        }

        public List<PsToken> Tokens { get; }

        /// <summary>Tokens in this procedure and every nested one.</summary>
        public long TokenCount { get; }
    }

    private readonly struct PsToken
    {
        private PsToken(bool isNumber, double number, string op, PsProc? proc)
        {
            IsNumber = isNumber;
            Number = number;
            Op = op;
            Proc = proc;
        }

        public bool IsNumber { get; }

        public double Number { get; }

        public string Op { get; }

        public PsProc? Proc { get; }

        public static PsToken FromNumber(double n) => new PsToken(true, n, string.Empty, null);

        public static PsToken FromOp(string op) => new PsToken(false, 0, op, null);

        public static PsToken FromProc(PsProc p) => new PsToken(false, 0, string.Empty, p);
    }
}
