using System.Globalization;
using System.Runtime.CompilerServices;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace OasDelta.Yaml;

public enum NodeKind : byte { Scalar, Mapping, Sequence }

/// <summary>
/// A positioned YAML/JSON node. Mappings keep source order; aliases share the anchored instance.
/// </summary>
public sealed class Node
{
    public NodeKind Kind;
    public string? Text;            // scalar text
    public bool Plain;              // plain (unquoted) scalar => type resolution applies
    public string? Tag;
    public List<Node>? Items;       // sequence items
    public List<KeyValuePair<Node, Node>>? Pairs; // mapping pairs (merge keys expanded)
    public int Line, Column, EndLine, EndColumn;
    private object? _value;
    private bool _valueResolved;

    public bool IsMapping => Kind == NodeKind.Mapping;
    public bool IsSequence => Kind == NodeKind.Sequence;
    public bool IsScalar => Kind == NodeKind.Scalar;
    public bool IsNull => Kind == NodeKind.Scalar && ToValue() is null;

    public Node? this[string key]
    {
        get
        {
            if (Pairs is null) return null;
            var pairs = Pairs;
            for (int i = 0; i < pairs.Count; i++)
                if (pairs[i].Key.Kind == NodeKind.Scalar && string.Equals(pairs[i].Key.Text, key, StringComparison.Ordinal))
                    return pairs[i].Value;
            return null;
        }
    }

    public string KeyString => Text ?? "";

    /// <summary>Converts to the JSON-like value model: null, bool, double, string, List&lt;object?&gt;, Dictionary&lt;string, object?&gt;.</summary>
    public object? ToValue()
    {
        if (_valueResolved) return _value;
        _valueResolved = true;
        switch (Kind)
        {
            case NodeKind.Scalar:
                _value = ResolveScalar(Text ?? "", Plain, Tag);
                break;
            case NodeKind.Sequence:
            {
                var list = new List<object?>(Items!.Count);
                _value = list; // set early for alias cycles
                foreach (var it in Items) list.Add(it.ToValue());
                break;
            }
            default:
            {
                var map = new Dictionary<string, object?>(Pairs!.Count, StringComparer.Ordinal);
                _value = map;
                foreach (var (k, v) in Pairs) map[ScalarKey(k)] = v.ToValue();
                break;
            }
        }
        return _value;
    }

    public static string ScalarKey(Node k)
    {
        if (k.Kind != NodeKind.Scalar) return k.Text ?? "";
        var v = k.ToValue();
        return v switch
        {
            null => "null",
            bool b => b ? "true" : "false",
            double d => Values.FormatJsonNumber(d),
            string s => s,
            _ => k.Text ?? ""
        };
    }

    /// <summary>Tag of a number read by <see cref="JsonNodeReader"/>: kin-openapi decodes JSON with encoding/json, where
    /// every number is a float64 (so "-0" is negative zero; in YAML it is the integer 0).</summary>
    internal const string JsonNumberTag = "!oasdelta-json-number";

    public static object? ResolveScalar(string s, bool plain, string? tag)
    {
        if (tag is not null && tag.Length > 0 && tag != "!")
        {
            switch (tag)
            {
                case JsonNumberTag:
                    // out of float64 range: encoding/json fails, and kin-openapi falls back to reading the text as YAML
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var jd) && !double.IsInfinity(jd) ? jd : ResolvePlain(s);
                case "tag:yaml.org,2002:str": return s;
                case "tag:yaml.org,2002:null": return null;
                case "tag:yaml.org,2002:bool": return ResolvePlain(s) is bool b ? b : s;
                case "tag:yaml.org,2002:int":
                case "tag:yaml.org,2002:float": return ResolvePlain(s) is double d ? d : s;
            }
        }
        if (!plain) return s;
        return ResolvePlain(s);
    }

    private static object? ResolvePlain(string s)
    {
        if (s.Length == 0) return null;
        char c = s[0];
        switch (c)
        {
            case '~': if (s.Length == 1) return null; break;
            case 'n': case 'N': if (s is "null" or "Null" or "NULL") return null; break;
            case 't': case 'T': if (s is "true" or "True" or "TRUE") return true; break;
            case 'f': case 'F': if (s is "false" or "False" or "FALSE") return false; break;
            case '.':
                if (s is ".inf" or ".Inf" or ".INF") return double.PositiveInfinity;
                if (s is ".nan" or ".NaN" or ".NAN") return double.NaN;
                break;
            case '-' or '+':
                if (s.Length > 1 && s[1] == '.')
                {
                    var r = s.AsSpan(1);
                    if (r is ".inf" or ".Inf" or ".INF") return c == '-' ? double.NegativeInfinity : double.PositiveInfinity;
                }
                break;
        }
        if (c is (>= '0' and <= '9') or '-' or '+' or '.')
        {
            if (TryParseNumber(s, out var d)) return d;
        }
        return s;
    }

    // yaml.v3 resolve(): strconv.ParseInt(plain, 0, 64), then ParseUint, then the YAML float grammar with ParseFloat.
    // Values that overflow every one of those stay strings, exactly as in Go.
    private static bool TryParseNumber(string s, out double d)
    {
        d = 0;
        ReadOnlySpan<char> span = s;
        if (s[0] == '.') { if (span.Contains('_')) return false; }   // hint '.': ParseFloat(in) on the raw text
        else if (span.Contains('_')) span = s.Replace("_", "");
        bool neg = false;
        var body = span;
        if (body.Length > 0 && (body[0] == '-' || body[0] == '+')) { neg = body[0] == '-'; body = body[1..]; }
        int radix = 0, start = 0;
        if (body.Length > 2 && body[0] == '0' && (body[1] is 'x' or 'X' or 'o' or 'O' or 'b' or 'B'))
        {
            radix = body[1] is 'x' or 'X' ? 16 : body[1] is 'o' or 'O' ? 8 : 2;
            start = 2;
        }
        else if (body.Length > 1 && body[0] == '0' && AllOctal(body)) { radix = 8; start = 1; }
        if (radix != 0)
        {
            UInt128 acc = 0;
            foreach (var ch in body[start..])
            {
                int v = ch switch { >= '0' and <= '9' => ch - '0', >= 'a' and <= 'f' => ch - 'a' + 10, >= 'A' and <= 'F' => ch - 'A' + 10, _ => 99 };
                if (v >= radix) return false;
                acc = acc * (UInt128)radix + (UInt128)v;
                if (acc > ulong.MaxValue) return false;
            }
            if (neg) { if (acc > (UInt128)long.MaxValue + 1) return false; d = acc == 0 ? 0 : -(double)acc; } // ParseInt: "-00" is 0
            else d = (double)acc;
            return true;
        }
        // decimal int / float grammar: [0-9]+ | ([0-9]+)?.[0-9]* ([eE][-+]?[0-9]+)?
        int i = 0, digits = 0;
        while (i < body.Length && char.IsAsciiDigit(body[i])) { i++; digits++; }
        if (i < body.Length && body[i] == '.')
        {
            i++;
            while (i < body.Length && char.IsAsciiDigit(body[i])) { i++; digits++; }
        }
        if (digits == 0) return false;
        if (i < body.Length && (body[i] is 'e' or 'E'))
        {
            i++;
            if (i < body.Length && (body[i] is '+' or '-')) i++;
            int ed = 0;
            while (i < body.Length && char.IsAsciiDigit(body[i])) { i++; ed++; }
            if (ed == 0) return false;
        }
        if (i != body.Length) return false;
        // an integer is ParseInt's (or ParseUint's) value first: "-0" is the integer 0, not negative zero
        if (digits == i)
        {
            if (long.TryParse(span, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) { d = l; return true; }
            if (ulong.TryParse(span, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var u)) { d = u; return true; }
        }
        return double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !double.IsInfinity(d);
    }

    private static bool AllOctal(ReadOnlySpan<char> s)
    {
        foreach (var ch in s) if (ch is < '0' or > '7') return false;
        return true;
    }
}

public sealed class YamlParseException(string message) : Exception(message);

public static class NodeReader
{
    /// <summary>Parses YAML (or JSON) text into a node tree. Returns null for an empty document.</summary>
    public static Node? Parse(string text)
    {
        var parser = new Parser(new StringReader(text));
        var anchors = new Dictionary<string, Node>(StringComparer.Ordinal);
        try
        {
            parser.Consume<StreamStart>();
            if (parser.Accept<StreamEnd>(out _)) return null;
            parser.Consume<DocumentStart>();
            if (parser.Accept<DocumentEnd>(out _)) return null;
            var root = ReadNode(parser, anchors, new SourceLines(text));
            return root;
        }
        catch (YamlException e)
        {
            throw new YamlParseException($"yaml: line {e.Start.Line}: {e.Message}");
        }
        catch (InvalidOperationException e)
        {
            // YamlDotNet's parser can fail this way on some malformed input (e.g. a block scalar indented less than its
            // indentation indicator says, in a sequence, followed by more mapping keys); go-yaml reports a syntax error
            throw new YamlParseException($"yaml: line {parser.Current?.Start.Line ?? 0}: {e.Message}");
        }
    }

    /// <summary>Line access into the source text, built on first use (only block scalars need it).</summary>
    private sealed class SourceLines(string text)
    {
        private List<int>? _starts;

        public string Line(int line) // 1-based, without its line break
        {
            if (_starts is null)
            {
                _starts = [0];
                for (int i = 0; i < text.Length; i++) if (text[i] == '\n') _starts.Add(i + 1);
            }
            if (line < 1 || line > _starts.Count) return "";
            int start = _starts[line - 1], end = line < _starts.Count ? _starts[line] - 1 : text.Length;
            if (end > start && text[end - 1] == '\r') end--;
            return text[start..end];
        }

        /// <summary>A literal or folded block scalar ends where its last non-blank content line ends, as kin-openapi's origin
        /// tracking reports it. YamlDotNet's end mark is after the trailing line break(s), at column 1 of a later line.</summary>
        public (int Line, int Column) BlockScalarEnd(Scalar s)
        {
            if (s.End.Column != 1) return ((int)s.End.Line, (int)s.End.Column);
            for (int l = (int)s.End.Line - 1; l > (int)s.Start.Line; l--)
            {
                var text = Line(l);
                if (!string.IsNullOrWhiteSpace(text)) return (l, text.EnumerateRunes().Count() + 1);
            }
            return ((int)s.End.Line, (int)s.End.Column);
        }
    }

    private static Node ReadNode(IParser parser, Dictionary<string, Node> anchors, SourceLines source)
    {
        var ev = parser.Current ?? throw new YamlParseException("unexpected end of yaml");
        parser.MoveNext();
        switch (ev)
        {
            case Scalar s:
            {
                var (endLine, endColumn) = s.Style is ScalarStyle.Literal or ScalarStyle.Folded
                    ? source.BlockScalarEnd(s)
                    : ((int)s.End.Line, (int)s.End.Column);
                var n = new Node
                {
                    Kind = NodeKind.Scalar,
                    Text = s.Value,
                    Plain = s.Style == ScalarStyle.Plain,
                    Tag = s.Tag.IsEmpty ? null : s.Tag.Value,
                    Line = (int)s.Start.Line, Column = (int)s.Start.Column,
                    EndLine = endLine, EndColumn = endColumn,
                };
                if (!s.Anchor.IsEmpty) anchors[s.Anchor.Value] = n;
                return n;
            }
            case SequenceStart ss:
            {
                var n = new Node { Kind = NodeKind.Sequence, Items = [], Tag = ss.Tag.IsEmpty ? null : ss.Tag.Value, Line = (int)ss.Start.Line, Column = (int)ss.Start.Column };
                if (!ss.Anchor.IsEmpty) anchors[ss.Anchor.Value] = n;
                while (parser.Current is not SequenceEnd)
                    n.Items.Add(ReadNode(parser, anchors, source));
                var end = parser.Current!; parser.MoveNext();
                if (ss.Style != SequenceStyle.Flow && n.Items.Count > 0) { n.EndLine = n.Items[^1].EndLine; n.EndColumn = n.Items[^1].EndColumn; }
                else { n.EndLine = (int)end.Start.Line; n.EndColumn = (int)end.Start.Column + 1; }
                return n;
            }
            case MappingStart ms:
            {
                var n = new Node { Kind = NodeKind.Mapping, Pairs = [], Tag = ms.Tag.IsEmpty ? null : ms.Tag.Value, Line = (int)ms.Start.Line, Column = (int)ms.Start.Column };
                if (!ms.Anchor.IsEmpty) anchors[ms.Anchor.Value] = n;
                List<KeyValuePair<Node, Node>>? merges = null;
                Dictionary<string, int>? index = null;
                while (parser.Current is not MappingEnd)
                {
                    var k = ReadNode(parser, anchors, source);
                    var v = ReadNode(parser, anchors, source);
                    if (k.Kind == NodeKind.Scalar && k.Plain && k.Text == "<<")
                    {
                        merges ??= [];
                        if (v.IsMapping) merges.AddRange(v.Pairs!);
                        else if (v.IsSequence) foreach (var m in v.Items!) if (m.IsMapping) merges.AddRange(m.Pairs!);
                        continue;
                    }
                    // duplicate keys: last wins (matches json semantics)
                    int existing = FindKey(n.Pairs, k, ref index);
                    if (existing >= 0) n.Pairs[existing] = new(k, v);
                    else { if (index is not null && k.Kind == NodeKind.Scalar) index[k.Text ?? ""] = n.Pairs.Count; n.Pairs.Add(new(k, v)); }
                }
                var end = parser.Current!; parser.MoveNext();
                if (ms.Style != MappingStyle.Flow && n.Pairs.Count > 0) { n.EndLine = n.Pairs[^1].Value.EndLine; n.EndColumn = n.Pairs[^1].Value.EndColumn; }
                else { n.EndLine = (int)end.Start.Line; n.EndColumn = (int)end.Start.Column + 1; }
                if (merges is not null)
                    foreach (var p in merges)
                        if (IndexOf(n.Pairs, p.Key) < 0) n.Pairs.Add(p);
                return n;
            }
            case AnchorAlias a:
                return anchors.TryGetValue(a.Value.Value, out var target) ? target : throw new YamlParseException($"yaml: unknown anchor '{a.Value.Value}' referenced");
            default:
                throw new YamlParseException($"yaml: unexpected event {ev.GetType().Name}");
        }
    }

    /// <summary>Position of a scalar key in a mapping; switches to a hash index once the mapping grows past a few keys.</summary>
    internal static int FindKey(List<KeyValuePair<Node, Node>> pairs, Node key, ref Dictionary<string, int>? index)
    {
        if (key.Kind != NodeKind.Scalar) return -1;
        if (index is null)
        {
            if (pairs.Count < 16) return IndexOf(pairs, key);
            index = new Dictionary<string, int>(pairs.Count * 2, StringComparer.Ordinal);
            for (int i = 0; i < pairs.Count; i++)
                if (pairs[i].Key.Kind == NodeKind.Scalar) index.TryAdd(pairs[i].Key.Text ?? "", i);
        }
        return index.TryGetValue(key.Text ?? "", out var at) ? at : -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int IndexOf(List<KeyValuePair<Node, Node>> pairs, Node key)
    {
        if (key.Kind != NodeKind.Scalar) return -1;
        for (int i = 0; i < pairs.Count; i++)
            if (pairs[i].Key.Kind == NodeKind.Scalar && pairs[i].Key.Text == key.Text) return i;
        return -1;
    }
}

/// <summary>Reads JSON straight into the node tree (what the YAML parser would produce for the same text).</summary>
public static class JsonNodeReader
{
    public static bool TryParse(string text, out Node? root)
    {
        root = null;
        var span = text.AsSpan().TrimStart();
        if (span.Length == 0 || (span[0] != '{' && span[0] != '[')) return false;
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var reader = new System.Text.Json.Utf8JsonReader(bytes, new System.Text.Json.JsonReaderOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Disallow });
        try
        {
            if (!reader.Read()) return false;
            root = Read(ref reader);
            while (reader.Read()) { }   // trailing content must be valid too
            return true;
        }
        catch (System.Text.Json.JsonException) { root = null; return false; }
    }

    private static Node Read(ref System.Text.Json.Utf8JsonReader r)
    {
        switch (r.TokenType)
        {
            case System.Text.Json.JsonTokenType.StartObject:
            {
                var n = new Node { Kind = NodeKind.Mapping, Pairs = [] };
                Dictionary<string, int>? index = null;
                while (r.Read() && r.TokenType != System.Text.Json.JsonTokenType.EndObject)
                {
                    var k = new Node { Kind = NodeKind.Scalar, Text = r.GetString(), Plain = false };
                    r.Read();
                    var v = Read(ref r);
                    int existing = NodeReader.FindKey(n.Pairs, k, ref index);
                    if (existing >= 0) n.Pairs[existing] = new(k, v);
                    else { if (index is not null) index[k.Text ?? ""] = n.Pairs.Count; n.Pairs.Add(new(k, v)); }
                }
                return n;
            }
            case System.Text.Json.JsonTokenType.StartArray:
            {
                var n = new Node { Kind = NodeKind.Sequence, Items = [] };
                while (r.Read() && r.TokenType != System.Text.Json.JsonTokenType.EndArray) n.Items.Add(Read(ref r));
                return n;
            }
            case System.Text.Json.JsonTokenType.String:
                return new Node { Kind = NodeKind.Scalar, Text = r.GetString(), Plain = false };
            case System.Text.Json.JsonTokenType.Number:
                return new Node { Kind = NodeKind.Scalar, Text = System.Text.Encoding.UTF8.GetString(r.ValueSpan), Plain = true, Tag = Node.JsonNumberTag };
            case System.Text.Json.JsonTokenType.True: return new Node { Kind = NodeKind.Scalar, Text = "true", Plain = true };
            case System.Text.Json.JsonTokenType.False: return new Node { Kind = NodeKind.Scalar, Text = "false", Plain = true };
            default: return new Node { Kind = NodeKind.Scalar, Text = "null", Plain = true };
        }
    }
}
