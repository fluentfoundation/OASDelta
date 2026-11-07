using System.Collections;
using System.Text;

namespace OasDelta.Formatters;

/// <summary>Objects exposing fields/methods to templates.</summary>
public interface ITemplateObject { bool TryGet(string name, out object? value); }

public sealed class TemplateException(string message) : Exception(message);

/// <summary>A small interpreter for the subset of Go's text/template (and html/template text-context escaping) used by oasdiff templates.</summary>
public sealed class GoTemplate
{
    private abstract class N;
    private sealed class TextN(string t) : N { public string Text = t; }
    private sealed class ActionN(string p) : N { public string Pipe = p; }
    private sealed class IfN : N { public List<(string? cond, List<N> body)> Branches = []; }
    private sealed class RangeN(string p) : N { public string Pipe = p; public List<N> Body = []; public List<N>? Else; }
    private sealed class WithN(string p) : N { public string Pipe = p; public List<N> Body = []; public List<N>? Else; }
    private sealed class TemplateN(string name, string? p) : N { public string Name = name; public string? Pipe = p; }

    private readonly List<N> _root;
    private readonly Dictionary<string, List<N>> _defines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<object?[], object?>> _funcs;
    private readonly bool _html;

    public GoTemplate(string text, Dictionary<string, Func<object?[], object?>> funcs, bool html)
    {
        _funcs = funcs; _html = html;
        var tokens = Tokenize(text);
        int pos = 0;
        _root = ParseList(tokens, ref pos, out var term);
        if (term is not null) throw new TemplateException("unexpected {{" + term + "}}");
    }

    // ------------------------------------------------------------------ lexing

    private static List<(bool action, string text)> Tokenize(string s)
    {
        var r = new List<(bool, string)>();
        int i = 0;
        while (i < s.Length)
        {
            int open = s.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0) { r.Add((false, s[i..])); break; }
            var text = s[i..open];
            int close = s.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) throw new TemplateException("unclosed action");
            var inner = s[(open + 2)..close];
            bool trimLeft = inner.StartsWith("- ", StringComparison.Ordinal) || inner == "-";
            bool trimRight = inner.EndsWith(" -", StringComparison.Ordinal);
            if (trimLeft) { text = text.TrimEnd(' ', '\t', '\r', '\n'); inner = inner[1..]; }
            if (trimRight) inner = inner[..^1];
            if (text.Length > 0) r.Add((false, text));
            inner = inner.Trim();
            if (!inner.StartsWith("/*", StringComparison.Ordinal)) r.Add((true, inner));
            i = close + 2;
            if (trimRight) while (i < s.Length && s[i] is ' ' or '\t' or '\r' or '\n') i++;
        }
        return r;
    }

    private static string Keyword(string a, out string rest)
    {
        int sp = a.IndexOf(' ');
        var kw = sp < 0 ? a : a[..sp];
        rest = sp < 0 ? "" : a[(sp + 1)..].Trim();
        return kw;
    }

    private List<N> ParseList(List<(bool action, string text)> t, ref int pos, out string? terminator)
    {
        var list = new List<N>();
        terminator = null;
        while (pos < t.Count)
        {
            var (isAction, text) = t[pos++];
            if (!isAction) { list.Add(new TextN(text)); continue; }
            var kw = Keyword(text, out var rest);
            switch (kw)
            {
                case "end": case "else": terminator = text; return list;
                case "if":
                {
                    var n = new IfN();
                    string? cond = rest;
                    while (true)
                    {
                        var body = ParseList(t, ref pos, out var term);
                        n.Branches.Add((cond, body));
                        if (term is null) throw new TemplateException("unterminated if");
                        if (term == "end") break;
                        var ek = Keyword(term, out var erest);
                        if (ek != "else") throw new TemplateException("unexpected " + term);
                        if (erest.StartsWith("if ", StringComparison.Ordinal)) cond = erest[3..].Trim();
                        else cond = null;
                    }
                    list.Add(n);
                    break;
                }
                case "range" or "with":
                {
                    var body = ParseList(t, ref pos, out var term);
                    List<N>? els = null;
                    if (term == "else") { els = ParseList(t, ref pos, out term); }
                    if (term != "end") throw new TemplateException("unterminated " + kw);
                    list.Add(kw == "range" ? new RangeN(rest) { Body = body, Else = els } : new WithN(rest) { Body = body, Else = els });
                    break;
                }
                case "define":
                {
                    var name = Unquote(rest);
                    var body = ParseList(t, ref pos, out var term);
                    if (term != "end") throw new TemplateException("unterminated define");
                    _defines[name] = body;
                    break;
                }
                case "template":
                {
                    int q = rest.IndexOf('"', 1);
                    var name = rest[1..q];
                    var pipe = rest[(q + 1)..].Trim();
                    list.Add(new TemplateN(name, pipe == "" ? null : pipe));
                    break;
                }
                default: list.Add(new ActionN(text)); break;
            }
        }
        return list;
    }

    private static string Unquote(string s) => s.Length >= 2 && s[0] == '"' ? s[1..^1] : s;

    // ------------------------------------------------------------------ execution

    public string Execute(object? data)
    {
        var sb = new StringBuilder();
        Exec(_root, data, data, sb);
        return sb.ToString();
    }

    private void Exec(List<N> nodes, object? dot, object? root, StringBuilder sb)
    {
        foreach (var n in nodes)
        {
            switch (n)
            {
                case TextN t: sb.Append(t.Text); break;
                case ActionN a:
                {
                    var v = Eval(a.Pipe, dot, root);
                    var s = Print(v);
                    sb.Append(_html ? HtmlEscape(s) : s);
                    break;
                }
                case IfN i:
                    foreach (var (cond, body) in i.Branches)
                        if (cond is null || Truthy(Eval(cond, dot, root))) { Exec(body, dot, root, sb); break; }
                    break;
                case WithN w:
                {
                    var v = Eval(w.Pipe, dot, root);
                    if (Truthy(v)) Exec(w.Body, v, root, sb);
                    else if (w.Else is not null) Exec(w.Else, dot, root, sb);
                    break;
                }
                case RangeN r:
                {
                    var v = Eval(r.Pipe, dot, root);
                    bool any = false;
                    foreach (var item in Items(v)) { any = true; Exec(r.Body, item, root, sb); }
                    if (!any && r.Else is not null) Exec(r.Else, dot, root, sb);
                    break;
                }
                case TemplateN tn:
                    if (!_defines.TryGetValue(tn.Name, out var def)) throw new TemplateException($"no such template \"{tn.Name}\"");
                    Exec(def, tn.Pipe is null ? null : Eval(tn.Pipe, dot, root), root, sb);
                    break;
            }
        }
    }

    private static IEnumerable<object?> Items(object? v)
    {
        switch (v)
        {
            case null: yield break;
            case string: yield break;
            case IDictionary d:
            {
                var keys = new List<object>();
                foreach (var k in d.Keys) keys.Add(k);
                keys.Sort((a, b) => string.CompareOrdinal(a.ToString(), b.ToString()));
                foreach (var k in keys) yield return d[k];
                break;
            }
            case IEnumerable e: foreach (var x in e) yield return x; break;
        }
    }

    private static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        int i => i != 0,
        long l => l != 0,
        double d => d != 0,
        ICollection c => c.Count > 0,
        IEnumerable e => e.GetEnumerator().MoveNext(),
        _ => true,
    };

    private static string Print(object? v) => v switch
    {
        null => "<no value>",
        bool b => b ? "true" : "false",
        string s => s,
        _ => v.ToString() ?? "",
    };

    private static string HtmlEscape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&#34;"); break;
                case '\'': sb.Append("&#39;"); break;
                case '+': sb.Append("&#43;"); break;
                case '\0': sb.Append("�"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private object? Eval(string pipe, object? dot, object? root)
    {
        var parts = SplitArgs(pipe);
        if (parts.Count == 0) return null;
        if (_funcs.TryGetValue(parts[0], out var fn))
        {
            var args = parts.Skip(1).Select(p => Term(p, dot, root)).ToArray();
            return fn(args);
        }
        if (parts.Count != 1) throw new TemplateException("can't evaluate " + pipe);
        return Term(parts[0], dot, root);
    }

    private static List<string> SplitArgs(string s)
    {
        var r = new List<string>();
        var cur = new StringBuilder();
        bool q = false;
        foreach (var c in s)
        {
            if (c == '"') q = !q;
            if (c == ' ' && !q) { if (cur.Length > 0) { r.Add(cur.ToString()); cur.Clear(); } continue; }
            cur.Append(c);
        }
        if (cur.Length > 0) r.Add(cur.ToString());
        return r;
    }

    private static object? Term(string t, object? dot, object? root)
    {
        if (t.Length >= 2 && t[0] == '"') return t[1..^1];
        if (t == ".") return dot;
        object? cur;
        string path;
        if (t.StartsWith('$')) { cur = root; path = t.Length > 1 ? t[1..] : ""; }
        else if (t.StartsWith('.')) { cur = dot; path = t; }
        else throw new TemplateException("unsupported term " + t);
        foreach (var seg in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cur is ITemplateObject o && o.TryGet(seg, out var v)) cur = v;
            else if (cur is IDictionary d && d.Contains(seg)) cur = d[seg];
            else throw new TemplateException($"can't evaluate field {seg}");
        }
        return cur;
    }
}
