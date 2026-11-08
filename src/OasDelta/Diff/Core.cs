using System.Text.RegularExpressions;
using OasDelta.Model;
using OasDelta.Yaml;

namespace OasDelta.Diff;

public interface IDiff : IOutput { bool Empty { get; } }

public sealed class DiffException(string message) : Exception(message);

public sealed class DiffConfig
{
    public string MatchPath = "", UnmatchPath = "", FilterExtension = "";
    public string PathPrefixBase = "", PathPrefixRevision = "", PathStripPrefixBase = "", PathStripPrefixRevision = "";
    public HashSet<string> ExcludeElements = new(StringComparer.Ordinal);
    public HashSet<string> ExcludeExtensions = new(StringComparer.Ordinal);
    public bool IncludePathParams;
    public bool MatchInlineRefs = true;

    public const string ExcludeExamplesOption = "examples", ExcludeDescriptionOption = "description", ExcludeEndpointsOption = "endpoints",
        ExcludeTitleOption = "title", ExcludeSummaryOption = "summary", ExcludeExtensionsOption = "extensions", ExcludeTagsOption = "tags";

    public static readonly string[] ExcludeDiffOptions = [ExcludeExamplesOption, ExcludeDescriptionOption, ExcludeEndpointsOption, ExcludeTitleOption, ExcludeSummaryOption, ExcludeExtensionsOption, ExcludeTagsOption];

    public bool IsExcludeExamples => ExcludeElements.Contains(ExcludeExamplesOption);
    public bool IsExcludeDescription => ExcludeElements.Contains(ExcludeDescriptionOption);
    public bool IsExcludeEndpoints => ExcludeElements.Contains(ExcludeEndpointsOption);
    public bool IsExcludeTitle => ExcludeElements.Contains(ExcludeTitleOption);
    public bool IsExcludeSummary => ExcludeElements.Contains(ExcludeSummaryOption);
    public bool IsExcludeExtensions => ExcludeElements.Contains(ExcludeExtensionsOption);
    public bool IsExcludeTags => ExcludeElements.Contains(ExcludeTagsOption);
    public bool IsExcludedExtension(string name) => ExcludeExtensions.Contains(name);

    public const string SunsetExtension = "x-sunset", XStabilityLevelExtension = "x-stability-level", XExtensibleEnumExtension = "x-extensible-enum", SinceDateExtension = "x-since-date";
}

internal sealed class PathFilters
{
    public Regex? MatchPath, UnmatchPath, Extension;
}

internal sealed class DiffState
{
    public SchemaGraph Graph = new();
    public Dictionary<Schema, string> BaseComponents = new(ReferenceEqualityComparer.Instance);
    public Dictionary<Schema, string> RevisionComponents = new(ReferenceEqualityComparer.Instance);
    public HashSet<(Callback, Callback)> CallbacksInProgress = new(RefPairComparer<Callback>.Instance);
    public PathFilters? Filters;
}

internal sealed class RefPairComparer<T> : IEqualityComparer<(T, T)> where T : class
{
    public static readonly RefPairComparer<T> Instance = new();
    public bool Equals((T, T) a, (T, T) b) => ReferenceEquals(a.Item1, b.Item1) && ReferenceEquals(a.Item2, b.Item2);
    public int GetHashCode((T, T) k) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.Item1), System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.Item2));
}

internal static class DiffUtil
{
    /// <summary>keysDiff: sorted added / deleted / common keys.</summary>
    public static (List<string> added, List<string> deleted, List<string> common) KeysDiff<V1, V2>(IReadOnlyDictionary<string, V1>? m1, IReadOnlyDictionary<string, V2>? m2)
    {
        var added = new List<string>();
        var deleted = new List<string>();
        var common = new List<string>();
        if (m1 is not null)
            foreach (var k in Values.SortedKeys(m1))
                if (m2 is not null && m2.ContainsKey(k)) common.Add(k); else deleted.Add(k);
        if (m2 is not null)
            foreach (var k in Values.SortedKeys(m2))
                if (m1 is null || !m1.ContainsKey(k)) added.Add(k);
        return (added, deleted, common);
    }

    public static List<string> SortedSet(IEnumerable<string> items)
    {
        var set = new HashSet<string>(items, StringComparer.Ordinal);
        var list = new List<string>(set);
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    public static OMap? Out(IDiff? d) => d is null || d.Empty ? null : (OMap?)d.ToOutput();
}

public sealed class ValueDiff(object? from, object? to) : IDiff
{
    public object? From = from, To = to;
    public bool Empty => false;
    public ValueDiff Reverse() => new(To, From);
    public object? ToOutput() => new OMap { { "from", From }, { "to", To } };

    public static ValueDiff? Get(object? a, object? b) => Values.DeepEqual(a, b) ? null : new ValueDiff(a, b);
    public static ValueDiff? Cond(bool exclude, object? a, object? b) => exclude ? null : Get(a, b);
    public static ValueDiff? Get(string a, string b) => a == b ? null : new ValueDiff(a, b);
    public static ValueDiff? Get(bool a, bool b) => a == b ? null : new ValueDiff(a, b);
    public static ValueDiff? Get(ulong a, ulong b) => a == b ? null : new ValueDiff(a, b);
    public static ValueDiff? Cond(bool exclude, string a, string b) => exclude ? null : Get(a, b);
    public static ValueDiff? Ptr(double? a, double? b) => a == b ? null : new ValueDiff(a, b);
    public static ValueDiff? Ptr(ulong? a, ulong? b) => a == b ? null : new ValueDiff(a, b);
    public static ValueDiff? Ptr(bool? a, bool? b) => a == b ? null : new ValueDiff(a, b);

    public static object? BoundValue(ExclusiveBound b) => b.Value is { } v ? v : b.Bool is { } bb ? bb : null;
    public static ValueDiff? Bound(ExclusiveBound a, ExclusiveBound b) => Get(BoundValue(a), BoundValue(b));
}

public sealed class StringsDiff : IDiff
{
    public List<string> Added = [], Deleted = [];
    public bool Empty => Added.Count == 0 && Deleted.Count == 0;
    public StringsDiff Reverse() => new() { Added = Deleted, Deleted = Added };

    public static StringsDiff? Get(IEnumerable<string>? s1, IEnumerable<string>? s2)
    {
        var set1 = new HashSet<string>(s1 ?? [], StringComparer.Ordinal);
        var set2 = new HashSet<string>(s2 ?? [], StringComparer.Ordinal);
        var d = new StringsDiff();
        foreach (var s in set2) if (!set1.Contains(s)) d.Added.Add(s);
        foreach (var s in set1) if (!set2.Contains(s)) d.Deleted.Add(s);
        d.Added.Sort(StringComparer.Ordinal);
        d.Deleted.Sort(StringComparer.Ordinal);
        return d.Empty ? null : d;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptList("added", Added);
        m.OptList("deleted", Deleted);
        return m;
    }
}

public sealed class StringMapDiff : IDiff
{
    public List<string> Added = [], Deleted = [];
    public SortedDictionary<string, ValueDiff> Modified = new(StringComparer.Ordinal);
    public bool Empty => Added.Count == 0 && Deleted.Count == 0 && Modified.Count == 0;
    public bool Mixed => (Added.Count > 0 ? 1 : 0) + (Deleted.Count > 0 ? 1 : 0) + (Modified.Count > 0 ? 1 : 0) > 1;

    public static StringMapDiff? Get(IReadOnlyDictionary<string, string>? m1, IReadOnlyDictionary<string, string>? m2)
    {
        var d = new StringMapDiff();
        var (a, del, common) = DiffUtil.KeysDiff(m1, m2);
        d.Added = a; d.Deleted = del;
        foreach (var k in common)
            if (m1![k] != m2![k]) d.Modified[k] = new ValueDiff(m1[k], m2[k]);
        return d.Empty ? null : d;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptList("added", Added);
        m.OptList("deleted", Deleted);
        if (Modified.Count > 0) m.Add("modified", Modified);
        return m;
    }
}

public sealed class JsonOperation : IOutput
{
    public object? OldValue, Value;
    public string Type = "", From = "", Path = "";

    public object? ToOutput() => new OMap { { "oldValue", OldValue }, { "value", Value }, { "op", Type }, { "from", From }, { "path", Path } };

    public override string ToString() => Type switch
    {
        "add" => $"Added {Path} with value: '{Values.GoV(Value)}'",
        "remove" => $"Removed {Path} with value: '{Values.GoV(OldValue)}'",
        "replace" => $"Modified {(Path == "" ? "value" : Path)} from '{Values.GoV(OldValue)}' to '{Values.GoV(Value)}'",
        _ => $"{Type} {Path}",
    };
}

/// <summary>Port of github.com/wI2L/jsondiff Compare with default options.</summary>
public static class JsonDiff
{
    private enum JType { Null, Bool, Number, String, Array, Object, Invalid }

    private static JType TypeOf(object? v) => v switch
    {
        null => JType.Null,
        bool => JType.Bool,
        double or long or int or ulong => JType.Number,
        string => JType.String,
        List<object?> => JType.Array,
        Dictionary<string, object?> => JType.Object,
        _ => JType.Invalid,
    };

    public static List<JsonOperation> Compare(object? src, object? tgt)
    {
        var patch = new List<JsonOperation>();
        Diff(patch, "", src, tgt);
        return patch;
    }

    private static string EscapeKey(string k) => k.Replace("~", "~0").Replace("/", "~1");

    private static void Diff(List<JsonOperation> patch, string ptr, object? src, object? tgt)
    {
        if (TypeOf(src) != TypeOf(tgt))
        {
            if (ptr.Length == 0) patch.Add(new JsonOperation { Type = "add", Path = ptr, OldValue = src, Value = tgt });
            else patch.Add(new JsonOperation { Type = "replace", Path = ptr, OldValue = src, Value = tgt });
            return;
        }
        if (Values.DeepEqual(src, tgt)) return;
        switch (src)
        {
            case List<object?> sa:
            {
                var ta = (List<object?>)tgt!;
                int sl = sa.Count, tl = ta.Count, ml = Math.Min(sl, tl);
                if (tl < sl)
                {
                    var p = ptr + "/" + ml;
                    for (int i = ml; i < sl; i++) patch.Add(new JsonOperation { Type = "remove", Path = p, OldValue = sa[i] });
                }
                for (int i = 0; i < ml; i++) Diff(patch, ptr + "/" + i, sa[i], ta[i]);
                if (tl > sl)
                {
                    var p = ptr + "/-";
                    for (int i = ml; i < tl; i++) patch.Add(new JsonOperation { Type = "add", Path = p, Value = ta[i] });
                }
                break;
            }
            case Dictionary<string, object?> so:
            {
                var to = (Dictionary<string, object?>)tgt!;
                var keys = new HashSet<string>(so.Keys, StringComparer.Ordinal);
                keys.UnionWith(to.Keys);
                var sorted = new List<string>(keys);
                sorted.Sort(StringComparer.Ordinal);
                foreach (var k in sorted)
                {
                    bool inOld = so.TryGetValue(k, out var ov), inNew = to.TryGetValue(k, out var nv);
                    var p = ptr + "/" + EscapeKey(k);
                    if (inOld && inNew) Diff(patch, p, ov, nv);
                    else if (inOld) patch.Add(new JsonOperation { Type = "remove", Path = p, OldValue = ov });
                    else patch.Add(new JsonOperation { Type = "add", Path = p, Value = nv });
                }
                break;
            }
            default:
                patch.Add(new JsonOperation { Type = "replace", Path = ptr, OldValue = src, Value = tgt });
                break;
        }
    }
}

public sealed class JsonPatchList : List<JsonOperation>, IOutput
{
    public object? ToOutput() => this.Select(o => (object?)o).ToList();
}

/// <summary>InterfaceMapDiff / ExtensionsDiff.</summary>
public sealed class ExtensionsDiff : IDiff
{
    public List<string> Added = [], Deleted = [];
    public SortedDictionary<string, JsonPatchList> Modified = new(StringComparer.Ordinal);
    public bool Empty => Added.Count == 0 && Deleted.Count == 0 && Modified.Count == 0;

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptList("added", Added);
        m.OptList("deleted", Deleted);
        if (Modified.Count > 0) m.Add("modified", Modified);
        return m;
    }

    public static ExtensionsDiff? GetInterfaceMapDiff(IReadOnlyDictionary<string, object?>? m1, IReadOnlyDictionary<string, object?>? m2)
    {
        var d = new ExtensionsDiff();
        var (a, del, common) = DiffUtil.KeysDiff(m1, m2);
        d.Added = a; d.Deleted = del;
        foreach (var name in common)
        {
            var patch = JsonDiff.Compare(m1![name], m2![name]);
            if (patch.Count > 0) { var p = new JsonPatchList(); p.AddRange(patch); d.Modified[name] = p; }
        }
        return d.Empty ? null : d;
    }

    public static ExtensionsDiff? Get(DiffConfig config, Dictionary<string, object?>? e1, Dictionary<string, object?>? e2)
    {
        if (config.IsExcludeExtensions) return null;
        return GetInterfaceMapDiff(Filter(e1, config), Filter(e2, config));
    }

    private static Dictionary<string, object?>? Filter(Dictionary<string, object?>? ext, DiffConfig config)
    {
        if (ext is null || config.ExcludeExtensions.Count == 0) return ext;
        var r = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in ext) if (!config.IsExcludedExtension(k)) r[k] = v;
        return r;
    }
}

public sealed class EnumDiff : IDiff
{
    public bool EnumAdded, EnumDeleted;
    public List<object?> Added = [], Deleted = [];
    public bool Empty => Added.Count == 0 && Deleted.Count == 0;

    public static EnumDiff? Get(List<object?>? e1, List<object?>? e2)
    {
        var d = new EnumDiff { EnumAdded = e1 is null && e2 is not null, EnumDeleted = e1 is not null && e2 is null };
        if (e1 is not null) foreach (var v in e1) if (!Contains(e2, v)) d.Deleted.Add(v);
        if (e2 is not null) foreach (var v in e2) if (!Contains(e1, v)) d.Added.Add(v);
        return d.Empty ? null : d;
    }

    private static bool Contains(List<object?>? list, object? v)
    {
        if (list is null) return false;
        foreach (var x in list) if (Values.DeepEqual(v, x)) return true;
        return false;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptTrue("enumAdded", EnumAdded);
        m.OptTrue("enumDeleted", EnumDeleted);
        m.OptList("added", Added);
        m.OptList("deleted", Deleted);
        return m;
    }
}

public sealed class DependentRequiredDiff : IDiff
{
    public SortedDictionary<string, List<string>> Added = new(StringComparer.Ordinal), Deleted = new(StringComparer.Ordinal);
    public SortedDictionary<string, StringsDiff> Modified = new(StringComparer.Ordinal);
    public bool Empty => Added.Count == 0 && Deleted.Count == 0 && Modified.Count == 0;

    public static DependentRequiredDiff? Get(Dictionary<string, List<string>>? d1, Dictionary<string, List<string>>? d2)
    {
        if ((d1 is null || d1.Count == 0) && (d2 is null || d2.Count == 0)) return null;
        var r = new DependentRequiredDiff();
        if (d2 is not null)
            foreach (var (k, v2) in d2)
            {
                if (d1 is null || !d1.TryGetValue(k, out var v1)) { r.Added[k] = v2; continue; }
                if (StringsDiff.Get(v1, v2) is { } sd) r.Modified[k] = sd;
            }
        if (d1 is not null)
            foreach (var (k, v1) in d1)
                if (d2 is null || !d2.ContainsKey(k)) r.Deleted[k] = v1;
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        if (Added.Count > 0) m.Add("added", Added);
        if (Deleted.Count > 0) m.Add("deleted", Deleted);
        if (Modified.Count > 0) m.Add("modified", Modified);
        return m;
    }
}

public sealed class DiscriminatorDiff : IDiff
{
    public bool Added, Deleted;
    public ExtensionsDiff? ExtensionsDiff;
    public ValueDiff? PropertyNameDiff;
    public StringMapDiff? MappingDiff;
    public bool Empty => !Added && !Deleted && ExtensionsDiff is null && PropertyNameDiff is null && MappingDiff is null;

    public static DiscriminatorDiff? Get(DiffConfig config, Discriminator? d1, Discriminator? d2)
    {
        if (d1 is null && d2 is null) return null;
        if (d1 is null) return new DiscriminatorDiff { Added = true };
        if (d2 is null) return new DiscriminatorDiff { Deleted = true };
        var r = new DiscriminatorDiff
        {
            ExtensionsDiff = ExtensionsDiff.Get(config, d1.Extensions, d2.Extensions),
            PropertyNameDiff = ValueDiff.Get(d1.PropertyName, d2.PropertyName),
            MappingDiff = StringMapDiff.Get(d1.Mapping, d2.Mapping),
        };
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptTrue("added", Added); m.OptTrue("deleted", Deleted);
        m.Opt("extensions", ExtensionsDiff); m.Opt("propertyName", PropertyNameDiff); m.Opt("mapping", MappingDiff);
        return m;
    }
}

public sealed class ExternalDocsDiff : IDiff
{
    public bool Added, Deleted;
    public ExtensionsDiff? ExtensionsDiff;
    public ValueDiff? DescriptionDiff, URLDiff;
    public bool Empty => !Added && !Deleted && ExtensionsDiff is null && DescriptionDiff is null && URLDiff is null;

    public static ExternalDocsDiff? Get(DiffConfig config, ExternalDocs? d1, ExternalDocs? d2)
    {
        if (d1 is null && d2 is null) return null;
        if (d1 is null) return new ExternalDocsDiff { Added = true };
        if (d2 is null) return new ExternalDocsDiff { Deleted = true };
        var r = new ExternalDocsDiff
        {
            ExtensionsDiff = ExtensionsDiff.Get(config, d1.Extensions, d2.Extensions),
            DescriptionDiff = ValueDiff.Cond(config.IsExcludeDescription, d1.Description, d2.Description),
            URLDiff = ValueDiff.Get(d1.URL, d2.URL),
        };
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptTrue("added", Added); m.OptTrue("deleted", Deleted);
        m.Opt("extensions", ExtensionsDiff); m.Opt("description", DescriptionDiff); m.Opt("url", URLDiff);
        return m;
    }
}

public sealed class InfoDiff : IDiff
{
    public bool Added, Deleted;
    public ExtensionsDiff? ExtensionsDiff;
    public ValueDiff? TitleDiff, SummaryDiff, DescriptionDiff, TermsOfServiceDiff, VersionDiff;
    public ContactDiff? ContactDiff;
    public LicenseDiff? LicenseDiff;
    public bool Empty => !Added && !Deleted && ExtensionsDiff is null && TitleDiff is null && SummaryDiff is null && DescriptionDiff is null
        && TermsOfServiceDiff is null && ContactDiff is null && LicenseDiff is null && VersionDiff is null;

    public static InfoDiff? Get(DiffConfig config, Info? i1, Info? i2)
    {
        if (i1 is null && i2 is null) return null;
        if (i1 is null) return new InfoDiff { Added = true };
        if (i2 is null) return new InfoDiff { Deleted = true };
        var r = new InfoDiff
        {
            ExtensionsDiff = ExtensionsDiff.Get(config, i1.Extensions, i2.Extensions),
            TitleDiff = ValueDiff.Cond(config.IsExcludeTitle, i1.Title, i2.Title),
            SummaryDiff = ValueDiff.Cond(config.IsExcludeSummary, i1.Summary, i2.Summary),
            DescriptionDiff = ValueDiff.Cond(config.IsExcludeDescription, i1.Description, i2.Description),
            TermsOfServiceDiff = ValueDiff.Get(i1.TermsOfService, i2.TermsOfService),
            ContactDiff = ContactDiff.Get(config, i1.Contact, i2.Contact),
            LicenseDiff = LicenseDiff.Get(config, i1.License, i2.License),
            VersionDiff = ValueDiff.Get(i1.Version, i2.Version),
        };
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptTrue("added", Added); m.OptTrue("deleted", Deleted);
        m.Opt("extensions", ExtensionsDiff); m.Opt("title", TitleDiff); m.Opt("summary", SummaryDiff); m.Opt("description", DescriptionDiff);
        m.Opt("termsOfService", TermsOfServiceDiff); m.Opt("contact", ContactDiff); m.Opt("license", LicenseDiff); m.Opt("version", VersionDiff);
        return m;
    }
}

public sealed class ContactDiff : IDiff
{
    public bool Added, Deleted;
    public ExtensionsDiff? ExtensionsDiff;
    public ValueDiff? NameDiff, URLDiff, EmailDiff;
    public bool Empty => !Added && !Deleted && ExtensionsDiff is null && NameDiff is null && URLDiff is null && EmailDiff is null;

    public static ContactDiff? Get(DiffConfig config, Contact? c1, Contact? c2)
    {
        if (c1 is null && c2 is null) return null;
        if (c1 is null) return new ContactDiff { Added = true };
        if (c2 is null) return new ContactDiff { Deleted = true };
        var r = new ContactDiff
        {
            ExtensionsDiff = ExtensionsDiff.Get(config, c1.Extensions, c2.Extensions),
            NameDiff = ValueDiff.Get(c1.Name, c2.Name), URLDiff = ValueDiff.Get(c1.URL, c2.URL), EmailDiff = ValueDiff.Get(c1.Email, c2.Email),
        };
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptTrue("added", Added); m.OptTrue("deleted", Deleted);
        m.Opt("extensions", ExtensionsDiff); m.Opt("name", NameDiff); m.Opt("url", URLDiff); m.Opt("email", EmailDiff);
        return m;
    }
}

public sealed class LicenseDiff : IDiff
{
    public bool Added, Deleted;
    public ExtensionsDiff? ExtensionsDiff;
    public ValueDiff? NameDiff, URLDiff, IdentifierDiff;
    public bool Empty => !Added && !Deleted && ExtensionsDiff is null && NameDiff is null && URLDiff is null && IdentifierDiff is null;

    public static LicenseDiff? Get(DiffConfig config, License? l1, License? l2)
    {
        if (l1 is null && l2 is null) return null;
        if (l1 is null) return new LicenseDiff { Added = true };
        if (l2 is null) return new LicenseDiff { Deleted = true };
        var r = new LicenseDiff
        {
            ExtensionsDiff = ExtensionsDiff.Get(config, l1.Extensions, l2.Extensions),
            NameDiff = ValueDiff.Get(l1.Name, l2.Name), URLDiff = ValueDiff.Get(l1.URL, l2.URL), IdentifierDiff = ValueDiff.Get(l1.Identifier, l2.Identifier),
        };
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptTrue("added", Added); m.OptTrue("deleted", Deleted);
        m.Opt("extensions", ExtensionsDiff); m.Opt("name", NameDiff); m.Opt("url", URLDiff); m.Opt("identifier", IdentifierDiff);
        return m;
    }
}

public sealed class ListOfTypesDiff : IDiff
{
    public List<string> Added = [], Deleted = [];
    public bool Empty => Added.Count == 0 && Deleted.Count == 0;

    private sealed record TypePattern(List<string> Types, string Source);

    public static ListOfTypesDiff? Get(Schema? b, Schema? r)
    {
        var bp = Detect(b);
        var rp = Detect(r);
        if (PatternsEqual(bp, rp)) return null;
        if (bp is null || rp is null) return null;
        if (bp.Source != "list" && rp.Source != "list") return null;
        var d = new ListOfTypesDiff { Added = Missing(rp, bp), Deleted = Missing(bp, rp) };
        return d.Empty ? null : d;
    }

    private static List<string> Missing(TypePattern p, TypePattern o)
    {
        var exclude = new HashSet<string>(o.Types, StringComparer.Ordinal);
        var r = new List<string>();
        foreach (var t in p.Types) if (exclude.Add(t)) r.Add(t);
        return r;
    }

    private static bool PatternsEqual(TypePattern? b, TypePattern? r)
    {
        if (b is null && r is null) return true;
        if (b is null || r is null) return false;
        if (b.Types.Count != r.Types.Count) return false;
        var set = new HashSet<string>(b.Types, StringComparer.Ordinal);
        foreach (var t in r.Types) if (!set.Contains(t)) return false;
        return true;
    }

    private static TypePattern? Detect(Schema? s)
    {
        if (s is null) return null;
        if (s.Not is not null || s.AllOf.Count > 0 || s.Properties.Count > 0) return null;
        if (s.Type is { Count: >= 1 }) return new TypePattern([s.Type[0]], "single");
        var one = CheckList(s.OneOf);
        var any = CheckList(s.AnyOf);
        if (one is { Count: > 0 }) return new TypePattern(one, "list");
        if (any is { Count: > 0 }) return new TypePattern(any, "list");
        return null;
    }

    private static List<string>? CheckList(List<Ref<Schema>> schemas)
    {
        if (schemas.Count == 0) return null;
        var types = new List<string>();
        foreach (var r in schemas)
        {
            // a schema without a value (a cycle of bare references) is no simple type; oasdiff panics here
            if (r.Value is not { } s || s.Type is not { Count: 1 } || s.Properties.Count > 0 || s.OneOf.Count > 0 || s.AnyOf.Count > 0 || s.AllOf.Count > 0) return null;
            types.Add(s.Type[0]);
        }
        return types;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptList("added", Added); m.OptList("deleted", Deleted);
        return m;
    }
}

public sealed class OneOfWrappingDiff : IDiff
{
    public int NumAlternatives;
    public List<string> MovedProperties = [];
    public bool OriginalPreserved;
    public bool Empty => NumAlternatives == 0 && MovedProperties.Count == 0;

    internal static OneOfWrappingDiff? Get(Unroller u, Schema? b, Schema? r)
    {
        if (b is null || r is null) return null;
        if (b.OneOf.Count > 0 || b.Properties.Count == 0) return null;
        if (r.OneOf.Count == 0 || r.Properties.Count > 0 || r.Type is { Count: > 0 }) return null;
        var moved = new List<string>();
        foreach (var name in b.Properties.Keys)
            foreach (var alt in r.OneOf)
                if (alt.Value?.Properties.ContainsKey(name) == true) { moved.Add(name); break; }
        if (moved.Count == 0) return null;
        moved.Sort(StringComparer.Ordinal);
        bool preserved = false;
        var baseRef = new Ref<Schema>(b);
        foreach (var alt in r.OneOf) if (u.Equivalent(baseRef, alt)) { preserved = true; break; }
        return new OneOfWrappingDiff { NumAlternatives = r.OneOf.Count, MovedProperties = moved, OriginalPreserved = preserved };
    }

    public object? ToOutput()
    {
        var m = new OMap();
        if (NumAlternatives != 0) m.Add("numAlternatives", (long)NumAlternatives);
        m.OptList("movedProperties", MovedProperties);
        m.OptTrue("originalPreserved", OriginalPreserved);
        return m;
    }
}

public sealed class NullableWrappingDiff : IDiff
{
    public bool NullabilityAdded, NullabilityRemoved;
    public bool Empty => !NullabilityAdded && !NullabilityRemoved;

    internal static NullableWrappingDiff? Get(Unroller u, Schema? b, Schema? r)
    {
        if (b is null || r is null) return null;
        if (IsNullableWrap(u, b, r, false)) return new NullableWrappingDiff { NullabilityAdded = true };
        if (IsNullableWrap(u, r, b, true)) return new NullableWrappingDiff { NullabilityRemoved = true };
        return null;
    }

    private static bool IsNullableWrap(Unroller u, Schema plain, Schema wrapped, bool reversed)
    {
        if (plain.OneOf.Count > 0 || plain.AnyOf.Count > 0 || plain.AllOf.Count > 0 || plain.Not is not null) return false;
        if (plain.Nullable || (plain.Type?.Contains("null") ?? false)) return false;
        if (wrapped.OneOf.Count != 2 || !ConstrainsNothingBeyondOneOf(u.Config, wrapped)) return false;
        Ref<Schema>? payload = null;
        int nullBranches = 0;
        foreach (var r in wrapped.OneOf)
        {
            if (IsBareNullSchema(u.Config, r.Value)) { nullBranches++; continue; }
            payload = r;
        }
        if (nullBranches != 1 || payload is null) return false;
        return reversed ? u.Equivalent(payload, new Ref<Schema>(plain)) : u.Equivalent(new Ref<Schema>(plain), payload);
    }

    private static bool ConstrainsNothingBeyondOneOf(DiffConfig config, Schema s)
    {
        var bare = s.ShallowClone();
        bare.OneOf = [];
        return SchemaEquivalence.RefsValidationEquivalent(config, new Ref<Schema>(bare), new Ref<Schema>(new Schema()));
    }

    private static bool IsBareNullSchema(DiffConfig config, Schema? s)
    {
        if (s is null || s.Type is not { Count: 1 } || s.Type[0] != "null") return false;
        var bare = s.ShallowClone();
        bare.Type = null;
        return SchemaEquivalence.RefsValidationEquivalent(config, new Ref<Schema>(bare), new Ref<Schema>(new Schema()));
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptTrue("nullabilityAdded", NullabilityAdded); m.OptTrue("nullabilityRemoved", NullabilityRemoved);
        return m;
    }
}
