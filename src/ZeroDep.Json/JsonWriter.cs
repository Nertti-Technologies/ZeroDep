using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ZeroDep.Json;

/// <summary>
/// A minimal, dependency-free JSON writer (no System.Text.Json) — used to keep ZeroDep 100% BCL
/// on every target framework, including netstandard2.0. It writes straight to a <see cref="TextWriter"/>, so
/// output of any size is never held in memory as a whole (ADR-0013).
/// </summary>
internal sealed class JsonWriter
{
    private readonly TextWriter _out;
    private readonly Stack<Frame> _stack = new Stack<Frame>();
    private readonly bool _indent;
    private bool _afterName;

    /// <summary>Creates a writer that collects the JSON in memory; <see cref="ToString"/> returns it.</summary>
    public JsonWriter(bool indent)
        : this(new StringWriter(CultureInfo.InvariantCulture), indent)
    {
    }

    /// <summary>Creates a writer over <paramref name="output"/>, which the caller owns and flushes.</summary>
    public JsonWriter(TextWriter output, bool indent)
    {
        _out = output;
        _indent = indent;
    }

    /// <summary>The JSON written so far when the writer collects in memory; otherwise the writer's type name.</summary>
    public override string ToString() => _out is StringWriter sw ? sw.ToString() : base.ToString()!;

    public JsonWriter BeginObject()
    {
        BeforeValue();
        _out.Write('{');
        _stack.Push(new Frame());
        return this;
    }

    public JsonWriter EndObject()
    {
        Frame frame = _stack.Pop();
        if (frame.HasItems) Indent();
        _out.Write('}');
        return this;
    }

    public JsonWriter BeginArray()
    {
        BeforeValue();
        _out.Write('[');
        _stack.Push(new Frame());
        return this;
    }

    public JsonWriter EndArray()
    {
        Frame frame = _stack.Pop();
        if (frame.HasItems) Indent();
        _out.Write(']');
        return this;
    }

    public JsonWriter Property(string name)
    {
        Frame frame = _stack.Peek();
        if (frame.HasItems) _out.Write(',');
        frame.HasItems = true;
        Indent();
        AppendString(name);
        _out.Write(':');
        if (_indent) _out.Write(' ');
        _afterName = true;
        return this;
    }

    public JsonWriter Value(string? value)
    {
        BeforeValue();
        if (value is null) _out.Write("null");
        else AppendString(value);
        return this;
    }

    public JsonWriter Value(int value)
    {
        BeforeValue();
        _out.Write(value.ToString(CultureInfo.InvariantCulture));
        return this;
    }

    public JsonWriter Value(double value)
    {
        BeforeValue();
        if (double.IsNaN(value) || double.IsInfinity(value)) _out.Write("null"); // JSON has no NaN or infinity
        else _out.Write(value.ToString("R", CultureInfo.InvariantCulture));
        return this;
    }

    public JsonWriter Value(bool value)
    {
        BeforeValue();
        _out.Write(value ? "true" : "false");
        return this;
    }

    public JsonWriter Null()
    {
        BeforeValue();
        _out.Write("null");
        return this;
    }

    private void BeforeValue()
    {
        if (_afterName)
        {
            _afterName = false;
            return;
        }
        if (_stack.Count > 0)
        {
            Frame frame = _stack.Peek();
            if (frame.HasItems) _out.Write(',');
            frame.HasItems = true;
            Indent();
        }
    }

    private void Indent()
    {
        if (!_indent) return;
        _out.Write('\n');
        for (int i = 0; i < _stack.Count; i++) _out.Write("  ");
    }

    private void AppendString(string s)
    {
        _out.Write('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': _out.Write("\\\""); break;
                case '\\': _out.Write("\\\\"); break;
                case '\b': _out.Write("\\b"); break;
                case '\f': _out.Write("\\f"); break;
                case '\n': _out.Write("\\n"); break;
                case '\r': _out.Write("\\r"); break;
                case '\t': _out.Write("\\t"); break;
                case (char)0x2028: // line and paragraph separators: valid JSON, but they end a JavaScript line
                case (char)0x2029:
                    AppendUnicodeEscape(c);
                    break;
                default:
                    if (c < 0x20)
                    {
                        AppendUnicodeEscape(c);
                    }
                    else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        _out.Write(c);
                        _out.Write(s[i + 1]);
                        i++;
                    }
                    else if (char.IsSurrogate(c))
                    {
                        AppendUnicodeEscape(c); // an unpaired surrogate cannot be encoded as UTF-8
                    }
                    else
                    {
                        _out.Write(c);
                    }
                    break;
            }
        }
        _out.Write('"');
    }

    private void AppendUnicodeEscape(char c)
    {
        _out.Write("\\u");
        _out.Write(((int)c).ToString("x4", CultureInfo.InvariantCulture));
    }

    private sealed class Frame
    {
        public bool HasItems;
    }
}
