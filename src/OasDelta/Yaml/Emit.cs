using System.Collections;
using System.Globalization;
using System.Text;

namespace OasDelta.Yaml;

/// <summary>Objects that know how to render themselves into the output value model.</summary>
public interface IOutput { object? ToOutput(); }

/// <summary>Emits the output value model as YAML exactly like go.yaml.in/yaml/v3 (indent 4, no wrapping).</summary>
public sealed class YamlEmitter
{
    private const int Best = 4;
    private const int FlushAt = 1 << 20;
    private readonly StringBuilder _sb;
    private readonly TextWriter? _out;
    private long _flushed;
    private YamlEmitter(StringBuilder sb, TextWriter? output = null) { _sb = sb; _out = output; }

    public static string Emit(object? value)
    {
        var sb = new StringBuilder(4096);
        var e = new YamlEmitter(sb);
        e.EmitRoot(Normalize(value, true));
        return sb.ToString();
    }

    /// <summary>Writes the same text as <c>Emit(value).TrimEnd('\n') + "\n"</c>, streamed to <paramref name="output"/> in
    /// chunks, so the size of the document is not limited by the maximum length of a .NET string (about 1 GB).</summary>
    public static void EmitTo(TextWriter output, object? value)
    {
        var sb = new StringBuilder(FlushAt + 4096);
        var e = new YamlEmitter(sb, output);
        e.EmitRoot(Normalize(value, true));
        int n = sb.Length;
        while (n > 0 && sb[n - 1] == '\n') n--;
        foreach (var chunk in sb.GetChunks())
        {
            int len = Math.Min(chunk.Length, n);
            if (len <= 0) break;
            output.Write(chunk.Span[..len]);
            n -= len;
        }
        output.Write('\n');
    }

    private bool AtStart => _sb.Length == 0 && _flushed == 0;

    /// <summary>Streams everything but trailing newlines (which EmitTo trims at the very end) once the buffer is large.</summary>
    private void Spill()
    {
        if (_out is null || _sb.Length < FlushAt) return;
        int n = _sb.Length;
        while (n > 0 && _sb[n - 1] == '\n') n--;
        int keep = _sb.Length - n;
        _out.Write(_sb.ToString(0, n));
        _flushed += n;
        _sb.Clear().Append('\n', keep);
    }

    /// <summary>Resolves IOutput / dictionaries into OMap / lists so emitters see a uniform tree.</summary>
    public static object? Normalize(object? v, bool yaml = false)
    {
        switch (v)
        {
            case null: return null;
            case Embedded em: return yaml ? new OMap { { em.YamlKey, Normalize(em.Value, yaml) } } : Normalize(em.Value, yaml);
            case IOutput o: return Normalize(o.ToOutput(), yaml);
            case OasDelta.Diff.ComplexKeyMap cm:
            {
                var r = new OasDelta.Diff.ComplexKeyMap();
                foreach (var (k, x) in cm) r.Add(new(Normalize(k, yaml), Normalize(x, yaml)));
                return r;
            }
            case OMap m:
            {
                var r = new OMap(m.Count);
                foreach (var (k, x) in m) r.Add(k, Normalize(x, yaml));
                return r;
            }
            case string or bool or double or long or int or ulong or Int: return v;
            case IDictionary d:
            {
                var keys = new List<string>(d.Count);
                foreach (var k in d.Keys) keys.Add((string)k);
                // yaml.v3 orders map keys "naturally" (digit runs compare numerically); encoding/json uses byte order
                keys.Sort(yaml ? YamlKeyComparer.Instance : Utf8OrdinalComparer.Instance);
                var r = new OMap(keys.Count);
                foreach (var k in keys) r.Add(k, Normalize(d[k], yaml));
                return r;
            }
            case IEnumerable e:
            {
                var r = new List<object?>();
                foreach (var x in e) r.Add(Normalize(x, yaml));
                return r;
            }
            default: return v.ToString();
        }
    }

    private void EmitRoot(object? v)
    {
        switch (v)
        {
            case OMap m when m.Count > 0: EmitMapping(m, 0, false); Break(); break;
            case List<object?> l when l.Count > 0: EmitSequence(l, 0, false); Break(); break;
            case OMap: _sb.Append("{}\n"); break;
            case List<object?>: _sb.Append("[]\n"); break;
            default: EmitScalar(v, 0, false, false); Break(); break;
        }
    }

    // Set when a literal block ended with a line break other than \n (LS or PS), which go-yaml writes as is and treats
    // as the end of the line: its next yaml_emitter_write_indent then adds no line break of its own.
    private bool _lineEnded;

    /// <summary>Starts a new line, unless the last literal block already ended one.</summary>
    private void Break()
    {
        if (_lineEnded) _lineEnded = false;
        else _sb.Append('\n');
    }

    private static int Inc(int indent) => Best * ((indent + Best) / Best);

    // inline: the first key is written on the current line (after "- ")
    private void EmitMapping(OMap m, int indent, bool inline)
    {
        bool first = true;
        foreach (var (k, v) in m)
        {
            Spill();
            if (!(first && inline))
            {
                if (!AtStart) Break();
                _sb.Append(' ', indent);
            }
            first = false;
            if (!IsSimpleKey(k)) { EmitExplicitKey(k, v, indent); continue; }
            EmitScalar(k, indent, true, false);
            _sb.Append(':');
            EmitValue(v, indent, false);
        }
    }

    /// <summary>go-yaml v3.0.5 yaml_emitter_check_simple_key: a scalar key is written as a simple key ("k: v") only
    /// when it has no line break (\r, \n, NEL, LS, PS) and is at most 128 bytes of UTF-8; otherwise the emitter uses an
    /// explicit "? k" / ": v" pair.</summary>
    private static bool IsSimpleKey(string k)
    {
        foreach (var ch in k) if (IsBreak(ch)) return false;
        return k.Length <= 128 / 3 || Encoding.UTF8.GetByteCount(k) <= 128;
    }

    /// <summary>An explicit key in a block mapping whose keys start at column <paramref name="indent"/> (the "?" is
    /// already positioned). go-yaml yaml_emitter_emit_block_mapping_key / _value with simple = false: the key and the
    /// value are nodes at the next indentation level, and a block collection value starts on the ":" line, padded to
    /// that level (the indicator counts as indentation).</summary>
    private void EmitExplicitKey(string k, object? v, int indent)
    {
        int child = Inc(indent);
        _sb.Append("? ");
        EmitScalar(k, child, false, true);
        Break();
        _sb.Append(' ', indent).Append(':');
        switch (v)
        {
            case OMap m when m.Count > 0:
                _sb.Append(' ', child - indent - 1);
                EmitMapping(m, child, true);
                break;
            case List<object?> l when l.Count > 0:
                _sb.Append(' ', child - indent - 1);
                EmitSequence(l, child, true);
                break;
            default:
                EmitValue(v, indent, false);
                break;
        }
    }

    private void EmitComplex(OasDelta.Diff.ComplexKeyMap cm, int indent)
    {
        foreach (var (k, v) in cm)
        {
            Break();
            _sb.Append(' ', indent).Append('?');
            int ci = Inc(indent);
            _sb.Append(' ', ci - indent - 1);
            if (k is OMap km && km.Count > 0) EmitMapping(km, ci, true); else EmitScalar(k, ci, true, false);
            Break();
            _sb.Append(' ', indent).Append(':');
            _sb.Append(' ', ci - indent - 1);
            if (v is OMap vm && vm.Count > 0) EmitMapping(vm, ci, true); else EmitScalar(v, ci, false, true);
        }
    }

    private void EmitValue(object? v, int indent, bool inSeqItem)
    {
        int child = inSeqItem ? indent + 2 : Inc(indent);
        switch (v)
        {
            case OasDelta.Diff.ComplexKeyMap cm when cm.Count > 0:
                EmitComplex(cm, child);
                break;
            case OMap m when m.Count > 0:
                if (inSeqItem) { _sb.Append(' '); EmitMapping(m, child, true); }
                else EmitMapping(m, child, false);
                break;
            case List<object?> l when l.Count > 0:
                if (inSeqItem) { _sb.Append(' '); EmitSequence(l, child, true); }
                else EmitSequence(l, child, false);
                break;
            case OMap: _sb.Append(" {}"); break;
            case List<object?>: _sb.Append(" []"); break;
            default:
                _sb.Append(' ');
                EmitScalar(v, child, false, true);
                break;
        }
    }

    private void EmitSequence(List<object?> l, int indent, bool inline)
    {
        bool first = true;
        foreach (var v in l)
        {
            Spill();
            if (!(first && inline))
            {
                if (!AtStart) Break();
                _sb.Append(' ', indent);
            }
            first = false;
            _sb.Append('-');
            EmitValue(v, indent, true);
        }
    }

    private void EmitScalar(object? v, int blockIndent, bool simpleKey, bool allowBlock)
    {
        switch (v)
        {
            case null: _sb.Append("null"); return;
            case bool b: _sb.Append(b ? "true" : "false"); return;
            case double d: _sb.Append(Values.FormatYamlFloat(d)); return;
            case long l: _sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
            case int i: _sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
            case ulong u: _sb.Append(u.ToString(CultureInfo.InvariantCulture)); return;
            case Int iv: _sb.Append(iv.Value.ToString(CultureInfo.InvariantCulture)); return;
            case string s: EmitString(s, blockIndent, simpleKey, allowBlock); return;
            default: EmitString(v.ToString() ?? "", blockIndent, simpleKey, allowBlock); return;
        }
    }

    /// <summary>go-yaml v3.0.5 resolve.go parseTimestamp: exactly four leading digits and a '-', then Go's time.Parse
    /// with one of "2006-1-2T15:4:5.999999999Z07:00", the same with a lower-case 't', "2006-1-2 15:4:5.999999999" or
    /// "2006-1-2". time.Parse rules that matter here: month, day, hour, minute and second take one or two digits;
    /// hour &lt; 24, minute and second &lt; 60, month 1-12 and the day must exist in that month (leap years included);
    /// the fraction accepts '.' or ',' and any number of digits, and may be left out; the zone is "Z" or +hh:mm or
    /// -hh:mm with hh &lt;= 24 and mm &lt;= 60; nothing may follow.</summary>
    private static bool LooksLikeTimestamp(string s)
    {
        if (s.Length < 5) return false;
        for (int i = 0; i < 4; i++) if (!char.IsAsciiDigit(s[i])) return false;
        if (s[4] != '-') return false;
        return ParseGoTime(s, 'T') || ParseGoTime(s, 't') || ParseGoTime(s, ' ') || ParseGoTime(s, '\0');
    }

    // sep: 'T'/'t' for the RFC 3339 layouts, ' ' for the zoneless one, '\0' for the date-only one
    private static bool ParseGoTime(string s, char sep)
    {
        int year = (s[0] - '0') * 1000 + (s[1] - '0') * 100 + (s[2] - '0') * 10 + (s[3] - '0');
        int p = 4;
        if (!Lit(s, ref p, '-') || !GetNum(s, ref p, out int month) || month is < 1 or > 12) return false;
        if (!Lit(s, ref p, '-') || !GetNum(s, ref p, out int day)) return false;
        if (sep != '\0')
        {
            if (!Lit(s, ref p, sep)) return false;
            if (!GetNum(s, ref p, out int hour) || hour >= 24) return false;
            if (!Lit(s, ref p, ':') || !GetNum(s, ref p, out int min) || min >= 60) return false;
            if (!Lit(s, ref p, ':') || !GetNum(s, ref p, out int sec) || sec >= 60) return false;
            // stdFracSecond9: optional, '.' or ',' followed by at least one digit, then every following digit
            if (p + 1 < s.Length && s[p] is '.' or ',' && char.IsAsciiDigit(s[p + 1]))
            {
                p += 2;
                while (p < s.Length && char.IsAsciiDigit(s[p])) p++;
            }
            if (sep != ' ')
            {
                // stdISO8601ColonTZ
                if (p < s.Length && s[p] == 'Z') p++;
                else
                {
                    if (s.Length - p < 6 || s[p + 3] != ':' || s[p] is not ('+' or '-')) return false;
                    if (!char.IsAsciiDigit(s[p + 1]) || !char.IsAsciiDigit(s[p + 2]) || !char.IsAsciiDigit(s[p + 4]) || !char.IsAsciiDigit(s[p + 5])) return false;
                    int hh = (s[p + 1] - '0') * 10 + (s[p + 2] - '0'), mm = (s[p + 4] - '0') * 10 + (s[p + 5] - '0');
                    if (hh > 24 || mm > 60) return false;
                    p += 6;
                }
            }
        }
        if (p != s.Length) return false;
        bool leap = year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
        int days = month == 2 ? (leap ? 29 : 28) : month is 4 or 6 or 9 or 11 ? 30 : 31;
        return day >= 1 && day <= days;

        static bool Lit(string s, ref int p, char c)
        {
            if (p >= s.Length || s[p] != c) return false;
            p++;
            return true;
        }

        // time.getnum(s, fixed: false): one or two digits
        static bool GetNum(string s, ref int p, out int n)
        {
            n = 0;
            if (p >= s.Length || !char.IsAsciiDigit(s[p])) return false;
            n = s[p++] - '0';
            if (p < s.Length && char.IsAsciiDigit(s[p])) n = n * 10 + (s[p++] - '0');
            return true;
        }
    }

    private static bool IsOldBool(string s) => s is "y" or "Y" or "yes" or "Yes" or "YES" or "on" or "On" or "ON"
        or "n" or "N" or "no" or "No" or "NO" or "off" or "Off" or "OFF";

    private static bool IsBase60Float(string s)
    {
        // ^[-+]?[0-9][0-9_]*(?::[0-5]?[0-9])+(?:\.[0-9_]*)?$
        int i = 0;
        if (s.Length == 0) return false;
        if (s[0] is '-' or '+') i++;
        if (i >= s.Length || !char.IsAsciiDigit(s[i])) return false;
        i++;
        while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '_')) i++;
        int groups = 0;
        while (i < s.Length && s[i] == ':')
        {
            i++;
            int d = 0;
            if (i < s.Length && s[i] >= '0' && s[i] <= '5' && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1])) { i += 2; d = 2; }
            else if (i < s.Length && char.IsAsciiDigit(s[i])) { i++; d = 1; }
            if (d == 0) return false;
            groups++;
        }
        if (groups == 0) return false;
        if (i < s.Length && s[i] == '.') { i++; while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '_')) i++; }
        return i == s.Length;
    }

    private void EmitString(string s, int blockIndent, bool simpleKey, bool allowBlock)
    {
        bool resolvesAsNonString = Node.ResolveScalar(s, true, null) is not string;
        bool canUsePlain = !(resolvesAsNonString || IsBase60Float(s) || IsOldBool(s) || LooksLikeTimestamp(s));
        var a = Analyze(s);
        if (s.Contains('\n'))
        {
            if (allowBlock && !simpleKey && a.BlockAllowed) { WriteLiteral(s, blockIndent); return; }
            WriteDoubleQuoted(s);
            return;
        }
        if (!canUsePlain) { WriteDoubleQuoted(s); return; }
        // plain requested; emitter may downgrade
        if (!a.BlockPlainAllowed || s.Length == 0)
        {
            if (a.SingleQuotedAllowed) { WriteSingleQuoted(s, blockIndent); return; }
            WriteDoubleQuoted(s);
            return;
        }
        _sb.Append(s);
    }

    private struct Analysis { public bool BlockPlainAllowed, SingleQuotedAllowed, BlockAllowed; }

    /// <summary>go-yaml v3 is_printable: #x0A, #x20-#x7E, #xA0-#xD7FF, #xE000-#xFFFD except #xFEFF. NEL (#x85), tab and
    /// everything beyond the BMP (emoji included) are not printable, so they force double quotes and get escaped.</summary>
    private static bool IsPrintable(int c) =>
        c == 0x0A || (c >= 0x20 && c <= 0x7E) || (c >= 0xA0 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD && c != 0xFEFF);
    private static bool IsBreak(int c) => c is '\r' or '\n' or 0x85 or 0x2028 or 0x2029;
    private static bool IsSpace(int c) => c == ' ';
    private static bool IsBlank(int c) => c is ' ' or '\t';
    private static bool IsBlankZ(int c) => c is ' ' or '\t' or 0 || IsBreak(c);

    private static Analysis Analyze(string s)
    {
        var res = new Analysis();
        if (s.Length == 0) { res.SingleQuotedAllowed = true; res.BlockAllowed = false; return res; }
        var runes = new List<int>(s.Length);
        foreach (var r in s.EnumerateRunes()) runes.Add(r.Value);
        bool blockInd = false, flowInd = false, lineBreaks = false, special = false, tabs = false;
        bool leadingSpace = false, leadingBreak = false, trailingSpace = false, trailingBreak = false, breakSpace = false, spaceBreak = false;
        bool precededByWs = true, prevSpace = false, prevBreak = false;
        // go-yaml checks only the first three bytes: "---a" and "...a" are not plain either
        if (s.StartsWith("---", StringComparison.Ordinal) || s.StartsWith("...", StringComparison.Ordinal))
        { blockInd = true; flowInd = true; }
        // followed_by_whitespace is is_blank (space or tab) or the end of the value; a line break does not count
        bool followedByWs = runes.Count <= 1 || IsBlank(runes[1]);
        for (int i = 0; i < runes.Count; i++)
        {
            int c = runes[i];
            bool first = i == 0, last = i == runes.Count - 1;
            if (first)
            {
                switch (c)
                {
                    case '#' or ',' or '[' or ']' or '{' or '}' or '&' or '*' or '!' or '|' or '>' or '\'' or '"' or '%' or '@' or '`':
                        flowInd = true; blockInd = true; break;
                    case '?' or ':':
                        flowInd = true; if (followedByWs) blockInd = true; break;
                    case '-':
                        if (followedByWs) { flowInd = true; blockInd = true; }
                        break;
                }
            }
            else
            {
                switch (c)
                {
                    case ',' or '?' or '[' or ']' or '{' or '}': flowInd = true; break;
                    case ':': flowInd = true; if (followedByWs) blockInd = true; break;
                    case '#': if (precededByWs) { flowInd = true; blockInd = true; } break;
                }
            }
            // go-yaml v3.0.5: a tab rules out plain and single-quoted scalars but, unlike other unprintable characters,
            // still allows a literal block
            if (c == '\t') tabs = true;
            else if (!IsPrintable(c)) special = true;
            if (IsBreak(c)) lineBreaks = true;
            if (IsSpace(c))
            {
                if (first) leadingSpace = true;
                if (last) trailingSpace = true;
                if (prevBreak) breakSpace = true;
                prevSpace = true; prevBreak = false;
            }
            else if (IsBreak(c))
            {
                if (first) leadingBreak = true;
                if (last) trailingBreak = true;
                if (prevSpace) spaceBreak = true;
                prevBreak = true; prevSpace = false;
            }
            else { prevSpace = false; prevBreak = false; }
            precededByWs = IsBlankZ(c);
            followedByWs = i + 2 >= runes.Count || IsBlank(runes[i + 2]);
        }
        bool blockPlain = true, single = true, block = true;
        if (leadingSpace || leadingBreak || trailingSpace || trailingBreak) blockPlain = false;
        if (trailingSpace) block = false;
        if (breakSpace) { blockPlain = false; single = false; }
        if (spaceBreak || tabs || special) { blockPlain = false; single = false; }
        if (spaceBreak || special) block = false;
        if (lineBreaks) blockPlain = false;
        if (blockInd) blockPlain = false;
        _ = flowInd;
        res.BlockPlainAllowed = blockPlain; res.SingleQuotedAllowed = single; res.BlockAllowed = block;
        return res;
    }

    /// <summary>go-yaml v3.0.5 yaml_emitter_write_single_quoted_scalar. The only line breaks a single-quoted scalar can
    /// hold are LS and PS (\n picks a literal block, \r and NEL force double quotes); write_break writes them as is and
    /// the next character starts an indented line.</summary>
    private void WriteSingleQuoted(string s, int indent)
    {
        _sb.Append('\'');
        bool breaks = false;
        foreach (var ch in s)
        {
            if (IsBreak(ch)) { _sb.Append(ch); breaks = true; continue; }
            if (breaks) _sb.Append(' ', indent);
            breaks = false;
            if (ch == '\'') _sb.Append("''"); else _sb.Append(ch);
        }
        _sb.Append('\'');
    }

    private const char Bom = (char)0xFEFF;

    private void WriteDoubleQuoted(string s)
    {
        _sb.Append('"');
        // go-yaml's is_bom(value, i) looks at the first three bytes of the value whatever i is, so when the value
        // starts with a BOM every character is escaped (BOM then "a" gives the escapes uFEFF and x61): a go-yaml
        // quirk, reproduced on purpose
        bool bom = s.Length > 0 && s[0] == Bom;
        foreach (var r in s.EnumerateRunes())
        {
            int c = r.Value;
            if (bom || !IsPrintable(c) || IsBreak(c) || c == '"' || c == '\\')
            {
                _sb.Append('\\');
                switch (c)
                {
                    case 0: _sb.Append('0'); break;
                    case 7: _sb.Append('a'); break;
                    case 8: _sb.Append('b'); break;
                    case 9: _sb.Append('t'); break;
                    case 0xA: _sb.Append('n'); break;
                    case 0xB: _sb.Append('v'); break;
                    case 0xC: _sb.Append('f'); break;
                    case 0xD: _sb.Append('r'); break;
                    case 0x1B: _sb.Append('e'); break;
                    case '"': _sb.Append('"'); break;
                    case '\\': _sb.Append('\\'); break;
                    case 0x85: _sb.Append('N'); break;
                    case 0xA0: _sb.Append('_'); break;
                    case 0x2028: _sb.Append('L'); break;
                    case 0x2029: _sb.Append('P'); break;
                    default:
                        if (c <= 0xFF) _sb.Append('x').Append(c.ToString("X2", CultureInfo.InvariantCulture));
                        else if (c <= 0xFFFF) _sb.Append('u').Append(c.ToString("X4", CultureInfo.InvariantCulture));
                        else _sb.Append('U').Append(c.ToString("X8", CultureInfo.InvariantCulture));
                        break;
                }
            }
            else _sb.Append(r.ToString());
        }
        _sb.Append('"');
    }

    /// <summary>go-yaml v3.0.5 yaml_emitter_write_block_scalar_hints and yaml_emitter_write_literal_scalar. Every line
    /// break counts (\r and NEL never get here, they rule out a block scalar, but LS and PS do): a leading space or
    /// break adds the indentation indicator, the last two characters pick the chomping indicator, and a break other
    /// than \n is written as is and then the next line is indented like after \n.</summary>
    private void WriteLiteral(string s, int indent)
    {
        _sb.Append('|');
        if (s.Length > 0 && (s[0] == ' ' || IsBreak(s[0]))) _sb.Append(Best.ToString(CultureInfo.InvariantCulture));
        // chomping (no astral character is a break, so looking at UTF-16 units is enough)
        if (s.Length == 0 || !IsBreak(s[^1])) _sb.Append('-');
        else if (s.Length == 1 || IsBreak(s[^2])) _sb.Append('+');
        _sb.Append('\n');
        bool breaks = true;
        foreach (var ch in s)
        {
            if (IsBreak(ch)) { _sb.Append(ch); breaks = true; continue; }
            if (breaks) _sb.Append(' ', indent);
            _sb.Append(ch);
            breaks = false;
        }
        // the value's last line break ends the line; the caller's Break() starts the next one
        if (s[^1] == '\n') _sb.Length--;
        else if (IsBreak(s[^1])) _lineEnded = true;
    }
}

/// <summary>Emits the output value model as compact JSON exactly like Go's encoding/json.</summary>
public static class JsonEmitter
{
    public static string Emit(object? value, bool indent = false)
    {
        var sb = new StringBuilder(4096);
        var streaming = s_out;
        s_out = null; // a nested Emit must never spill into an EmitTo in progress
        try { Write(sb, YamlEmitter.Normalize(value), indent ? 0 : -1); }
        finally { s_out = streaming; }
        return sb.ToString();
    }

    private const int FlushAt = 1 << 20;
    [ThreadStatic] private static TextWriter? s_out;

    /// <summary>Writes the same text as <c>Emit(value)</c>, streamed to <paramref name="output"/> in chunks, so the size
    /// of the document is not limited by the maximum length of a .NET string (about 1 GB).</summary>
    public static void EmitTo(TextWriter output, object? value)
    {
        var sb = new StringBuilder(FlushAt + 4096);
        var previous = s_out;
        s_out = output;
        try { Write(sb, YamlEmitter.Normalize(value), -1); }
        finally { s_out = previous; }
        foreach (var chunk in sb.GetChunks()) output.Write(chunk.Span);
    }

    private static void Spill(StringBuilder sb)
    {
        if (s_out is null || sb.Length < FlushAt) return;
        foreach (var chunk in sb.GetChunks()) s_out.Write(chunk.Span);
        sb.Clear();
    }

    private static void NewLine(StringBuilder sb, int level)
    {
        if (level < 0) return;
        sb.Append('\n');
        sb.Append(' ', level * 2);
    }

    public static void Write(StringBuilder sb, object? v, int level)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            // encoding/json floatEncoder: strconv.AppendFloat keeps the sign of negative zero
            case double d: sb.Append(d == 0 && double.IsNegative(d) ? "-0" : Values.FormatJsonNumber(d)); break;
            case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
            case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
            case ulong u: sb.Append(u.ToString(CultureInfo.InvariantCulture)); break;
            case Int iv: sb.Append(iv.Value.ToString(CultureInfo.InvariantCulture)); break;
            case string s: WriteString(sb, s); break;
            case OMap m:
            {
                if (m.Count == 0) { sb.Append("{}"); break; }
                sb.Append('{');
                bool first = true;
                foreach (var (k, x) in m)
                {
                    Spill(sb);
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, level < 0 ? -1 : level + 1);
                    WriteString(sb, k);
                    sb.Append(level < 0 ? ":" : ": ");
                    Write(sb, x, level < 0 ? -1 : level + 1);
                }
                NewLine(sb, level);
                sb.Append('}');
                break;
            }
            case List<object?> l:
            {
                if (l.Count == 0) { sb.Append("[]"); break; }
                sb.Append('[');
                for (int i = 0; i < l.Count; i++)
                {
                    Spill(sb);
                    if (i > 0) sb.Append(',');
                    NewLine(sb, level < 0 ? -1 : level + 1);
                    Write(sb, l[i], level < 0 ? -1 : level + 1);
                }
                NewLine(sb, level);
                sb.Append(']');
                break;
            }
            default: WriteString(sb, v.ToString() ?? ""); break;
        }
    }

    public static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                // encoding/json appendString has short escapes for \b and \f since Go 1.22 (oasdiff 1.35.0 is built with 1.27)
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '<': sb.Append("\\u003c"); break;
                case '>': sb.Append("\\u003e"); break;
                case '&': sb.Append("\\u0026"); break;
                case '\u2028': sb.Append("\\u2028"); break;
                case '\u2029': sb.Append("\\u2029"); break;
                default:
                    if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }
}

/// <summary>Go's string order (bytes of UTF-8, i.e. code points), which encoding/json uses for map keys. It differs
/// from UTF-16 ordinal order only when a character beyond the BMP (a surrogate pair) meets one in U+E000..U+FFFF.</summary>
public sealed class Utf8OrdinalComparer : IComparer<string>
{
    public static readonly Utf8OrdinalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        int n = Math.Min(x.Length, y.Length);
        for (int i = 0; i < n; i++)
        {
            char a = x[i], b = y[i];
            if (a == b) continue;
            if (a >= 0xD800 && b >= 0xD800) return Fix(a) - Fix(b);
            return a - b;
        }
        return x.Length - y.Length;

        // move surrogates (U+D800..U+DFFF) above U+E000..U+FFFF
        static int Fix(char c) => c >= 0xE000 ? c - 0x800 : c + 0x2000;
    }
}

/// <summary>gopkg.in/yaml.v3 sorter.go keyList.Less for string keys.</summary>
public sealed class YamlKeyComparer : IComparer<string>
{
    public static readonly YamlKeyComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (Less(x!, y!)) return -1;
        return Less(y!, x!) ? 1 : 0;
    }

    private static bool Less(string a, string b)
    {
        var ar = a.EnumerateRunes().ToArray();
        var br = b.EnumerateRunes().ToArray();
        bool digits = false;
        for (int i = 0; i < ar.Length && i < br.Length; i++)
        {
            if (ar[i] == br[i]) { digits = Rune.IsDigit(ar[i]); continue; }
            bool al = Rune.IsLetter(ar[i]), bl = Rune.IsLetter(br[i]);
            if (al && bl) return ar[i].Value < br[i].Value;
            if (al || bl) return digits ? al : bl;
            long an = 0, bn = 0;
            if (ar[i].Value == '0' || br[i].Value == '0')
                for (int j = i - 1; j >= 0 && Rune.IsDigit(ar[j]); j--)
                    if (ar[j].Value != '0') { an = 1; bn = 1; break; }
            int ai = i, bi = i;
            for (; ai < ar.Length && Rune.IsDigit(ar[ai]); ai++) an = an * 10 + (ar[ai].Value - '0');
            for (; bi < br.Length && Rune.IsDigit(br[bi]); bi++) bn = bn * 10 + (br[bi].Value - '0');
            if (an != bn) return an < bn;
            if (ai != bi) return ai < bi;
            return ar[i].Value < br[i].Value;
        }
        return ar.Length < br.Length;
    }
}
