using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace OasDelta.Yaml;

/// <summary>An ordered mapping used for output (struct-like, fields in declaration order).</summary>
public sealed class OMap : List<KeyValuePair<string, object?>>
{
    public OMap() { }
    public OMap(int capacity) : base(capacity) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(string key, object? value) => Add(new KeyValuePair<string, object?>(key, value));
    /// <summary>Adds only when value is non-null (omitempty semantics are handled by the caller).</summary>
    public void Opt(string key, object? value) { if (value is not null) Add(key, value); }
    public void OptStr(string key, string? value) { if (!string.IsNullOrEmpty(value)) Add(key, value); }
    public void OptTrue(string key, bool value) { if (value) Add(key, true); }
    public void OptList<T>(string key, ICollection<T>? value) { if (value is { Count: > 0 }) Add(key, value); }
}

/// <summary>A Go embedded struct: yaml.v3 nests it under the lowercased type name, encoding/json inlines its fields.</summary>
public sealed class Embedded(string yamlKey, object? value)
{
    public readonly string YamlKey = yamlKey;
    public readonly object? Value = value;
}

/// <summary>Marker for integers so that output keeps integer formatting.</summary>
public readonly record struct Int(long Value);

/// <summary>Values in the JSON-like model: null, bool, double, string, List&lt;object?&gt;, Dictionary&lt;string, object?&gt;.</summary>
public static class Values
{
    public static bool DeepEqual(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        switch (a)
        {
            case string sa: return b is string sb && string.Equals(sa, sb, StringComparison.Ordinal);
            case double da: return b is double db && (da == db);
            case bool ba: return b is bool bb && ba == bb;
            case long la: return b is long lb && la == lb;
            case ulong ua: return b is ulong ub && ua == ub;
            case int ia: return b is int ib && ia == ib;
            case List<object?> la2:
                if (b is not List<object?> lb2 || la2.Count != lb2.Count) return false;
                for (int i = 0; i < la2.Count; i++) if (!DeepEqual(la2[i], lb2[i])) return false;
                return true;
            case List<string> lsa:
                if (b is not List<string> lsb || lsa.Count != lsb.Count) return false;
                for (int i = 0; i < lsa.Count; i++) if (lsa[i] != lsb[i]) return false;
                return true;
            case Dictionary<string, object?> ma:
                if (b is not Dictionary<string, object?> mb || ma.Count != mb.Count) return false;
                foreach (var (k, v) in ma)
                    if (!mb.TryGetValue(k, out var v2) || !DeepEqual(v, v2)) return false;
                return true;
            case IDeepEquatable de: return de.DeepEquals(b);
            default: return a.Equals(b);
        }
    }

    // ---------------- number formatting (Go-compatible) ----------------

    /// <summary>Shortest decimal digits and decimal-point position: value = 0.d1d2d3... * 10^dp.</summary>
    private static (string digits, int dp) ShortestDigits(double f)
    {
        f = Math.Abs(f);
        if (f == 0) return ("0", 1);
        // Use round-trip shortest representation
        string s = f.ToString("R", CultureInfo.InvariantCulture);
        int eIdx = s.IndexOfAny(['E', 'e']);
        int exp10 = 0;
        string mant = s;
        if (eIdx >= 0) { exp10 = int.Parse(s.AsSpan(eIdx + 1), CultureInfo.InvariantCulture); mant = s[..eIdx]; }
        int dot = mant.IndexOf('.');
        string intPart = dot >= 0 ? mant[..dot] : mant;
        string frac = dot >= 0 ? mant[(dot + 1)..] : "";
        string all = intPart + frac;
        int dp = intPart.Length + exp10;
        // strip leading zeros
        int lead = 0;
        while (lead < all.Length - 1 && all[lead] == '0') { lead++; dp--; }
        all = all[lead..];
        all = all.TrimEnd('0');
        if (all.Length == 0) all = "0";
        return (all, dp);
    }

    /// <summary>strconv.FormatFloat(f, 'g', -1, 64)</summary>
    public static string FormatGoG(double f)
    {
        if (double.IsNaN(f)) return "NaN";
        if (double.IsPositiveInfinity(f)) return "+Inf";
        if (double.IsNegativeInfinity(f)) return "-Inf";
        var (digits, dp) = ShortestDigits(f);
        var sb = new StringBuilder(24);
        if (f < 0 || (f == 0 && double.IsNegative(f))) sb.Append('-');
        int exp = dp - 1;
        if (f != 0 && (exp < -4 || exp >= 6)) AppendE(sb, digits, exp);
        else AppendF(sb, digits, dp);
        return sb.ToString();
    }

    private static void AppendE(StringBuilder sb, string digits, int exp)
    {
        sb.Append(digits[0]);
        if (digits.Length > 1) { sb.Append('.'); sb.Append(digits, 1, digits.Length - 1); }
        sb.Append('e');
        sb.Append(exp < 0 ? '-' : '+');
        int ae = Math.Abs(exp);
        if (ae < 10) sb.Append('0');
        sb.Append(ae.ToString(CultureInfo.InvariantCulture));
    }

    private static void AppendF(StringBuilder sb, string digits, int dp)
    {
        if (dp <= 0)
        {
            sb.Append("0.");
            sb.Append('0', -dp);
            sb.Append(digits);
        }
        else if (dp >= digits.Length)
        {
            sb.Append(digits);
            sb.Append('0', dp - digits.Length);
        }
        else
        {
            sb.Append(digits, 0, dp);
            sb.Append('.');
            sb.Append(digits, dp, digits.Length - dp);
        }
    }

    /// <summary>encoding/json float64 formatting.</summary>
    public static string FormatJsonNumber(double f)
    {
        if (double.IsNaN(f) || double.IsInfinity(f)) return "null";
        double abs = Math.Abs(f);
        var (digits, dp) = ShortestDigits(f);
        var sb = new StringBuilder(24);
        if (f < 0) sb.Append('-');
        if (abs != 0 && (abs < 1e-6 || abs >= 1e21))
        {
            int exp = dp - 1;
            sb.Append(digits[0]);
            if (digits.Length > 1) { sb.Append('.'); sb.Append(digits, 1, digits.Length - 1); }
            sb.Append('e');
            sb.Append(exp < 0 ? '-' : '+');
            sb.Append(Math.Abs(exp).ToString(CultureInfo.InvariantCulture));
        }
        else AppendF(sb, digits, dp);
        return sb.ToString();
    }

    /// <summary>yaml.v3 float formatting.</summary>
    public static string FormatYamlFloat(double f)
    {
        var s = FormatGoG(f);
        return s switch { "+Inf" => ".inf", "-Inf" => "-.inf", "NaN" => ".nan", _ => s };
    }

    /// <summary>Go fmt %v for values of the JSON-like model.</summary>
    public static string GoV(object? v)
    {
        switch (v)
        {
            case null: return "<nil>";
            case string s: return s;
            case bool b: return b ? "true" : "false";
            case double d: return FormatGoG(d);
            case long l: return l.ToString(CultureInfo.InvariantCulture);
            case ulong ul: return ul.ToString(CultureInfo.InvariantCulture);
            case int i: return i.ToString(CultureInfo.InvariantCulture);
            case Int iv: return iv.Value.ToString(CultureInfo.InvariantCulture);
            case List<object?> list:
            {
                var sb = new StringBuilder("[");
                for (int i = 0; i < list.Count; i++) { if (i > 0) sb.Append(' '); sb.Append(GoV(list[i])); }
                return sb.Append(']').ToString();
            }
            case List<string> ls: return "[" + string.Join(' ', ls) + "]";
            case Dictionary<string, object?> map:
            {
                var sb = new StringBuilder("map[");
                bool first = true;
                foreach (var k in SortedKeys(map)) { if (!first) sb.Append(' '); first = false; sb.Append(k).Append(':').Append(GoV(map[k])); }
                return sb.Append(']').ToString();
            }
            default: return v.ToString() ?? "";
        }
    }

    public static List<string> SortedKeys<T>(IDictionary<string, T>? map)
    {
        if (map is null || map.Count == 0) return [];
        var keys = new List<string>(map.Keys);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    public static List<string> SortedKeys<T>(Dictionary<string, T>? map) => SortedKeys((IDictionary<string, T>?)map);

    public static IEnumerable<string> SortedKeys<T>(IReadOnlyDictionary<string, T>? map)
    {
        if (map is null || map.Count == 0) return [];
        var keys = new List<string>(map.Keys);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }
}

public interface IDeepEquatable { bool DeepEquals(object? other); }
