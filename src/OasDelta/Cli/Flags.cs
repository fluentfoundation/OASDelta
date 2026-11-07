using System.Globalization;
using System.Text;

namespace OasDelta.Cli;

public enum FlagKind : byte { Bool, String, Uint, Int, Strings, Enum, EnumSlice }

/// <summary>A command-line flag, modelled on spf13/pflag so usage and error text match upstream.</summary>
public sealed class FlagDef(string name, string shorthand, FlagKind kind, object? defaultValue, string usage)
{
    public readonly string Name = name, Shorthand = shorthand;
    public readonly FlagKind Kind = kind;
    public readonly object? Default = defaultValue;
    public string Usage = usage;
    public bool Hidden;
    public string[] Allowed = [];

    public string TypeName => Kind switch
    {
        FlagKind.Bool => "", FlagKind.Uint => "uint", FlagKind.Int => "int", FlagKind.Strings or FlagKind.EnumSlice => "strings", _ => "string",
    };

    public string DefaultText => Default switch
    {
        null => Kind is FlagKind.Strings or FlagKind.EnumSlice ? "[]" : "",
        bool b => b ? "true" : "false",
        List<string> l => "[" + string.Join(",", l) + "]",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        var o => o.ToString() ?? "",
    };

    public bool DefaultIsZero => DefaultText is "" or "false" or "0" or "[]";

    /// <summary>"a, b, or c" as in internal/enum.go listOf.</summary>
    public static string ListOf(IReadOnlyList<string> v) => v.Count switch
    {
        0 => "no options available",
        1 => v[0],
        2 => v[0] + " or " + v[1],
        _ => string.Join(", ", v.Take(v.Count - 1)) + ", or " + v[^1],
    };
}

public sealed class FlagException(string message) : Exception(message);

/// <summary>Parsed flag values: explicit command-line values, then config-file values, then defaults (viper precedence).</summary>
public sealed class FlagValues
{
    private readonly Dictionary<string, FlagDef> _defs;
    private readonly Dictionary<string, object?> _set = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _config = new(StringComparer.Ordinal);
    public readonly List<string> Args = [];
    public bool Help;

    public FlagValues(IEnumerable<FlagDef> defs)
    {
        _defs = new(StringComparer.Ordinal);
        foreach (var d in defs) _defs[d.Name] = d;
    }

    public bool Changed(string name) => _set.ContainsKey(name);
    public void SetConfig(string name, object? value) => _config[name] = value;
    public void Override(string name, object? value) => _set[name] = value;

    private object? Raw(string name)
    {
        if (_set.TryGetValue(name, out var v)) return v;
        if (_config.TryGetValue(name, out var c)) return c;
        return _defs.TryGetValue(name, out var d) ? d.Default : null;
    }

    public string Str(string name) => Raw(name) switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        List<string> l => string.Join(",", l),
        var o => o.ToString() ?? "",
    };

    public bool Bool(string name) => Raw(name) switch
    {
        bool b => b,
        string s => bool.TryParse(s, out var b) && b,
        long l => l != 0,
        ulong u => u != 0,
        _ => false,
    };

    public uint Uint(string name) => Raw(name) switch
    {
        ulong u => (uint)u,
        long l when l >= 0 => (uint)l,
        double d when d >= 0 => (uint)d,
        string s when uint.TryParse(s, out var u) => u,
        _ => 0,
    };

    /// <summary>viper GetStringSlice plus upstream's fixViperStringSlice (a single element is split on commas).</summary>
    public List<string> Strings(string name)
    {
        List<string> l = Raw(name) switch
        {
            null => [],
            List<string> ls => ls,
            List<object?> lo => lo.Select(o => o?.ToString() ?? "").ToList(),
            string s => s.Length == 0 ? [] : s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList(),
            var o => [o.ToString() ?? ""],
        };
        return l.Count == 1 ? l[0].Split(',').ToList() : new List<string>(l);
    }

    // ---- command-line parsing (pflag semantics: interspersed flags and arguments, "--" ends flags) ----

    public void Parse(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--") { for (i++; i < args.Count; i++) Args.Add(args[i]); break; }
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var body = a[2..];
                string name = body; string? value = null;
                int eq = body.IndexOf('=');
                if (eq >= 0) { name = body[..eq]; value = body[(eq + 1)..]; }
                if (name is "help") { Help = true; continue; }
                if (!_defs.TryGetValue(name, out var def)) throw new FlagException($"unknown flag: --{name}");
                if (value is null && def.Kind != FlagKind.Bool)
                {
                    if (i + 1 >= args.Count) throw new FlagException($"flag needs an argument: --{name}");
                    value = args[++i];
                }
                Assign(def, value);
            }
            else if (a.Length > 1 && a[0] == '-')
            {
                // shorthand cluster: -abc, -fjson, -f json, -f=json
                for (int j = 1; j < a.Length; j++)
                {
                    char c = a[j];
                    if (c == 'h') { Help = true; continue; }
                    var def = _defs.Values.FirstOrDefault(d => d.Shorthand.Length == 1 && d.Shorthand[0] == c)
                        ?? throw new FlagException($"unknown shorthand flag: '{c}' in {a}");
                    if (def.Kind == FlagKind.Bool)
                    {
                        if (j + 1 < a.Length && a[j + 1] == '=') { Assign(def, a[(j + 2)..]); break; }
                        Assign(def, null);
                        continue;
                    }
                    string value;
                    if (j + 1 < a.Length) value = a[j + 1] == '=' ? a[(j + 2)..] : a[(j + 1)..];
                    else if (i + 1 < args.Count) value = args[++i];
                    else throw new FlagException($"flag needs an argument: '{c}' in -{c}");
                    Assign(def, value);
                    break;
                }
            }
            else Args.Add(a);
        }
    }

    private static string Display(FlagDef d) => d.Shorthand.Length > 0 ? $"-{d.Shorthand}, --{d.Name}" : $"--{d.Name}";

    private void Assign(FlagDef def, string? value)
    {
        switch (def.Kind)
        {
            case FlagKind.Bool:
                if (value is null) { _set[def.Name] = true; return; }
                _set[def.Name] = ParseGoBool(value) ?? throw new FlagException($"invalid argument {Fmt(value)} for {Fmt("--" + def.Name)} flag: must be true or false");
                return;
            case FlagKind.Uint:
                if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var u) || u > uint.MaxValue)
                    throw new FlagException($"invalid argument {Fmt(value!)} for {Fmt("--" + def.Name)} flag: must be a non-negative integer");
                _set[def.Name] = u;
                return;
            case FlagKind.Int:
                if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
                    throw new FlagException($"invalid argument {Fmt(value!)} for {Fmt("--" + def.Name)} flag: must be an integer");
                _set[def.Name] = n;
                return;
            case FlagKind.Enum:
                var canon = def.Allowed.FirstOrDefault(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase))
                    ?? throw new FlagException($"invalid argument {Fmt(value!)} for {Fmt(Display(def))} flag: {value} is not one of the allowed values: {FlagDef.ListOf(def.Allowed)}");
                _set[def.Name] = canon;
                return;
            case FlagKind.Strings:
            case FlagKind.EnumSlice:
                var items = ReadCsv(value!);
                if (def.Kind == FlagKind.EnumSlice)
                {
                    var bad = new List<string>();
                    for (int k = 0; k < items.Count; k++)
                    {
                        var c = def.Allowed.FirstOrDefault(x => string.Equals(x, items[k], StringComparison.OrdinalIgnoreCase));
                        if (c is null) bad.Add(items[k]); else items[k] = c;
                    }
                    if (bad.Count > 0)
                        throw new FlagException($"invalid argument {Fmt(value!)} for {Fmt(Display(def))} flag: {string.Join(", ", bad)} {(bad.Count == 1 ? "is" : "are")} not one of the allowed values: {FlagDef.ListOf(def.Allowed)}");
                }
                // pflag: the first explicit use replaces the default, later uses append
                if (_set.TryGetValue(def.Name, out var prev) && prev is List<string> pl) pl.AddRange(items);
                else _set[def.Name] = items;
                return;
            default:
                _set[def.Name] = value ?? "";
                return;
        }
    }

    private static string Fmt(string s) => OasDelta.Checker.Fmt.GoQuote(s);

    private static bool? ParseGoBool(string s) => s switch
    {
        "1" or "t" or "T" or "TRUE" or "true" or "True" => true,
        "0" or "f" or "F" or "FALSE" or "false" or "False" => false,
        _ => null,
    };

    /// <summary>encoding/csv single record, as pflag's string slices use.</summary>
    private static List<string> ReadCsv(string s)
    {
        var result = new List<string>();
        if (s.Length == 0) return result;
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quoted)
            {
                if (c == '"') { if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i++; } else quoted = false; }
                else sb.Append(c);
            }
            else if (c == '"' && sb.Length == 0) quoted = true;
            else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }

    // ---- help text (pflag FlagUsages) ----

    public static string Usages(IEnumerable<FlagDef> defs)
    {
        var lines = new List<(string head, string usage)>();
        int maxlen = 0;
        foreach (var f in defs.Where(d => !d.Hidden).OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            var head = f.Shorthand.Length > 0 ? $"  -{f.Shorthand}, --{f.Name}" : $"      --{f.Name}";
            if (f.TypeName.Length > 0) head += " " + f.TypeName;
            maxlen = Math.Max(maxlen, head.Length);
            var usage = f.Usage;
            if (!f.DefaultIsZero) usage += f.TypeName == "string" ? $" (default {Fmt(f.DefaultText)})" : $" (default {f.DefaultText})";
            lines.Add((head, usage));
        }
        var sb = new StringBuilder();
        foreach (var (head, usage) in lines)
            sb.Append(head).Append(' ', maxlen - head.Length + 3).Append(usage).Append('\n');
        return sb.ToString();
    }
}
