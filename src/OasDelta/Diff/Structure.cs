using StringBuilder = System.Text.StringBuilder;
using System.Text.RegularExpressions;
using Encoding = OasDelta.Model.Encoding;
using OasDelta.Model;
using OasDelta.Yaml;

namespace OasDelta.Diff;

// =========================================================== generic keyed-map diff

public class MapDiff<TDiff> : IDiff where TDiff : class, IDiff
{
    public List<string> Added = [], Deleted = [];
    public SortedDictionary<string, TDiff> Modified = new(StringComparer.Ordinal);
    public bool Empty => Added.Count == 0 && Deleted.Count == 0 && Modified.Count == 0;
    public SummaryDetails GetSummary() => new(Added.Count, Deleted.Count, Modified.Count);

    public virtual object? ToOutput()
    {
        var m = new OMap();
        m.OptList("added", Added); m.OptList("deleted", Deleted);
        if (Modified.Count > 0) m.Add("modified", Modified);
        return m;
    }

    internal static TSelf? Build<TSelf, TV>(Dictionary<string, TV>? m1, Dictionary<string, TV>? m2, Func<TV, TV, TDiff?> diff) where TSelf : MapDiff<TDiff>, new()
    {
        var r = new TSelf();
        var (a, d, common) = DiffUtil.KeysDiff(m1, m2);
        r.Added = a; r.Deleted = d;
        foreach (var k in common) if (diff(m1![k], m2![k]) is { } x && !x.Empty) r.Modified[k] = x;
        return r.Empty ? null : r;
    }
}

public readonly record struct SummaryDetails(int Added, int Deleted, int Modified) : IOutput
{
    public object? ToOutput()
    {
        var m = new OMap();
        if (Added != 0) m.Add("added", (long)Added);
        if (Deleted != 0) m.Add("deleted", (long)Deleted);
        if (Modified != 0) m.Add("modified", (long)Modified);
        return m;
    }
}

// =========================================================== paths

internal sealed class PathItemPair
{
    public required PathItem PathItem1, PathItem2;
    public Dictionary<string, string> PathParamsMap = new(StringComparer.Ordinal);
}

internal static class PathUtil
{
    /// <summary>normalizeTemplatedPath: /person/{name} -> /person/{}</summary>
    public static (string normalized, int count, List<string> vars) NormalizeTemplatedPath(string path)
    {
        if (path.IndexOf('{') < 0) return (path, 0, []);
        var tpl = new StringBuilder(path.Length);
        var vars = new List<string>();
        var v = new StringBuilder();
        bool isVar = false;
        int count = 0;
        char cc = '\0';
        for (int i = 0; i < path.Length; i++)
        {
            char c = path[i];
            if (isVar)
            {
                if (c == '}')
                {
                    isVar = false;
                    vars.Add(v.ToString());
                    v.Clear();
                    if (i > 0 && cc == '*') tpl.Append(cc);
                }
                else { v.Append(c); continue; }
            }
            else if (c == '{') { isVar = true; count++; }
            tpl.Append(c);
            cc = c;
        }
        return (tpl.ToString(), count, vars);
    }

    public static bool PathParamsMatch(Dictionary<string, string> map, string p1, string p2) =>
        map.Count == 0 ? p1 == p2 : map.TryGetValue(p1, out var m) && m == p2;

    public static Paths RewritePrefix(Dictionary<string, PathItem> paths, string strip, string prepend)
    {
        var r = new Paths();
        foreach (var (p, item) in paths)
            r.Map[prepend + (strip.Length > 0 && p.StartsWith(strip, StringComparison.Ordinal) ? p[strip.Length..] : p)] = item;
        return r;
    }

    /// <summary>Templated-path index: the first path (in sorted order) for each (normalized template, variable count).</summary>
    public sealed class TemplateIndex
    {
        public readonly Dictionary<(string norm, int count, int vars), (string path, List<string> vars)> First = new();

        public TemplateIndex(Paths paths)
        {
            foreach (var path in Values.SortedKeys(paths.Map))
            {
                var (n, count, vars) = NormalizeTemplatedPath(path);
                First.TryAdd((n, count, vars.Count), (path, vars));
            }
        }
    }

    public static (PathItem? item, Dictionary<string, string>? map) FindEndpoint(DiffConfig config, string endpoint, Paths paths, TemplateIndex? index = null)
    {
        if (paths.Value(endpoint) is { } item) return (item, new(StringComparer.Ordinal));
        if (config.IncludePathParams) return (null, null);
        var (norm, expected, params1) = NormalizeTemplatedPath(endpoint);
        index ??= new TemplateIndex(paths);
        if (!index.First.TryGetValue((norm, expected, params1.Count), out var hit)) return (null, null);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < params1.Count; i++) map[params1[i]] = hit.vars[i];
        return (paths.Map[hit.path], map);
    }

    public static (List<string> added, List<string> deleted, List<string> common, Dictionary<string, PathItemPair> pairs) GetPathItemsDiff(DiffConfig config, Paths p1, Paths p2)
    {
        var added = new List<string>(); var deleted = new List<string>(); var common = new List<string>();
        var pairs = new Dictionary<string, PathItemPair>(StringComparer.Ordinal);
        TemplateIndex? index1 = null, index2 = null;
        foreach (var e1 in Values.SortedKeys(p1.Map))
        {
            var (item2, map) = FindEndpoint(config, e1, p2, index2 ??= new TemplateIndex(p2));
            if (item2 is not null) { common.Add(e1); pairs[e1] = new PathItemPair { PathItem1 = p1.Map[e1], PathItem2 = item2, PathParamsMap = map! }; }
            else deleted.Add(e1);
        }
        foreach (var e2 in Values.SortedKeys(p2.Map))
            if (FindEndpoint(config, e2, p1, index1 ??= new TemplateIndex(p1)).item is null) added.Add(e2);
        return (added, deleted, common, pairs);
    }

    public static PathFilters GetFilters(DiffConfig config, DiffState state)
    {
        if (state.Filters is not null) return state.Filters;
        state.Filters = new PathFilters
        {
            MatchPath = Compile(config.MatchPath, "match-path filter"),
            UnmatchPath = Compile(config.UnmatchPath, "unmatch-path filter"),
            Extension = Compile(config.FilterExtension, "extension filter"),
        };
        return state.Filters;
    }

    private static Regex? Compile(string pattern, string name)
    {
        if (pattern == "") return null;
        try { return new Regex(pattern, RegexOptions.CultureInvariant); }
        catch (RegexParseException e) { throw new DiffException($"failed to compile {name} regex {Checker.Fmt.GoQuote(pattern)}: error parsing regexp: {GoRegexError(e, pattern)}"); }
        catch (ArgumentException e) { throw new DiffException($"failed to compile {name} regex {Checker.Fmt.GoQuote(pattern)}: {e.Message}"); }
    }

    /// <summary>Go regexp/syntax wording for the common parse errors (the .NET messages are stripped in the lean build).</summary>
    private static string GoRegexError(RegexParseException e, string pattern) => e.Error switch
    {
        RegexParseError.InsufficientClosingParentheses => $"missing closing ): `{pattern}`",
        RegexParseError.InsufficientOpeningParentheses => $"unexpected ): `{pattern}`",
        RegexParseError.UnterminatedBracket => $"missing closing ]: `{pattern[Math.Max(0, pattern.LastIndexOf('['))..]}`",
        RegexParseError.QuantifierAfterNothing => $"missing argument to repetition operator: `{pattern[Math.Min(Math.Max(0, e.Offset - 1), pattern.Length - 1)]}`",
        RegexParseError.NestedQuantifiersNotParenthesized => $"invalid nested repetition operator: `{pattern.Substring(Math.Max(0, e.Offset - 2), Math.Min(2, pattern.Length))}`",
        RegexParseError.UnescapedEndingBackslash => "trailing backslash at end of expression: ``",
        RegexParseError.ReversedCharacterRange => $"invalid character class range: `{pattern}`",
        _ => $"invalid regular expression: `{pattern}`",
    };

    public static void FilterPaths(PathFilters f, Paths p1, Paths p2)
    {
        foreach (var p in new[] { p1, p2 })
        {
            if (f.MatchPath is not null) foreach (var k in p.Map.Keys.ToList()) if (!f.MatchPath.IsMatch(k)) p.Map.Remove(k);
            if (f.UnmatchPath is not null) foreach (var k in p.Map.Keys.ToList()) if (f.UnmatchPath.IsMatch(k)) p.Map.Remove(k);
            if (f.Extension is not null)
                foreach (var (k, item) in p.Map.ToList())
                    if (item.Extensions is not null && item.Extensions.Keys.Any(f.Extension.IsMatch)) p.Map.Remove(k);
        }
    }

    public static void FilterOperationsByExtensions(Regex? r, PathItemPair pair)
    {
        if (r is null) return;
        foreach (var item in new[] { pair.PathItem1, pair.PathItem2 })
            foreach (var (method, op) in item.Operations())
                if (op.Extensions is not null && op.Extensions.Keys.Any(r.IsMatch)) item.SetOperation(method, null);
    }

    public static List<string> MethodsToCompare(PathItem p1, PathItem p2)
    {
        var custom = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var p in new[] { p1, p2 })
            foreach (var m in p.Operations().Keys)
                if (Array.IndexOf(PathItem.FixedMethods, m) < 0) custom.Add(m);
        var r = new List<string>(PathItem.FixedMethods);
        r.AddRange(custom);
        return r;
    }
}

public sealed class PathsDiff : MapDiff<PathDiff>
{
    public Paths? Base, Revision;

    internal static PathsDiff? Get(DiffConfig config, DiffState state, Paths paths1, Paths paths2)
    {
        PathUtil.FilterPaths(PathUtil.GetFilters(config, state), paths1, paths2);
        var r = new PathsDiff();
        var p1 = PathUtil.RewritePrefix(paths1.Map, config.PathStripPrefixBase, config.PathPrefixBase);
        var p2 = PathUtil.RewritePrefix(paths2.Map, config.PathStripPrefixRevision, config.PathPrefixRevision);
        var (added, deleted, common, pairs) = PathUtil.GetPathItemsDiff(config, p1, p2);
        r.Added = added; r.Deleted = deleted;
        foreach (var e in common)
            if (PathDiff.Get(config, state, pairs[e]) is { } d) r.Modified[e] = d;
        r.Base = p1; r.Revision = p2;
        return r.Empty ? null : r;
    }
}

public sealed class PathDiff : IDiff
{
    public ExtensionsDiff? ExtensionsDiff;
    public ValueDiff? RefDiff, SummaryDiff, DescriptionDiff;
    public OperationsDiff? OperationsDiff;
    public ServersDiff? ServersDiff;
    public ParametersDiffByLocation? ParametersDiff;
    public PathItem? Base, Revision;

    public bool Empty => ExtensionsDiff is null && RefDiff is null && SummaryDiff is null && DescriptionDiff is null && OperationsDiff is null && ServersDiff is null && ParametersDiff is null;

    internal static PathDiff? Get(DiffConfig config, DiffState state, PathItemPair pair)
    {
        var i1 = pair.PathItem1; var i2 = pair.PathItem2;
        var r = new PathDiff
        {
            ExtensionsDiff = ExtensionsDiff.Get(config, i1.Extensions, i2.Extensions),
            RefDiff = ValueDiff.Get(i1.Ref, i2.Ref),
            SummaryDiff = ValueDiff.Cond(config.IsExcludeSummary, i1.Summary, i2.Summary),
            DescriptionDiff = ValueDiff.Cond(config.IsExcludeDescription, i1.Description, i2.Description),
        };
        r.OperationsDiff = OperationsDiff.Get(config, state, pair);
        r.ServersDiff = ServersDiff.Get(config, i1.Servers, i2.Servers);
        r.ParametersDiff = ParametersDiffByLocation.Get(config, state, i1.Parameters, i2.Parameters, pair.PathParamsMap);
        r.Base = i1; r.Revision = i2;
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.Opt("extensions", ExtensionsDiff); m.Opt("ref", RefDiff); m.Opt("summary", SummaryDiff); m.Opt("description", DescriptionDiff);
        m.Opt("operations", OperationsDiff); m.Opt("servers", ServersDiff); m.Opt("parameters", ParametersDiff);
        return m;
    }
}

public sealed class OperationsDiff : IDiff
{
    public List<string> Added = [], Deleted = [];
    public SortedDictionary<string, MethodDiff> Modified = new(StringComparer.Ordinal);
    public bool Empty => Added.Count == 0 && Deleted.Count == 0 && Modified.Count == 0;

    internal static OperationsDiff? Get(DiffConfig config, DiffState state, PathItemPair pair)
    {
        PathUtil.FilterOperationsByExtensions(PathUtil.GetFilters(config, state).Extension, pair);
        var r = new OperationsDiff();
        foreach (var method in PathUtil.MethodsToCompare(pair.PathItem1, pair.PathItem2))
        {
            var o1 = pair.PathItem1.GetOperation(method);
            var o2 = pair.PathItem2.GetOperation(method);
            if (o1 is null && o2 is null) continue;
            if (o1 is null) { r.Added.Add(method); continue; }
            if (o2 is null) { r.Deleted.Add(method); continue; }
            if (MethodDiff.Get(config, state, o1, o2, pair.PathParamsMap) is { } d) r.Modified[method] = d;
        }
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptList("added", Added); m.OptList("deleted", Deleted);
        if (Modified.Count > 0) m.Add("modified", Modified);
        return m;
    }
}

public sealed class MethodDiff : IDiff
{
    public ExtensionsDiff? ExtensionsDiff;
    public StringsDiff? TagsDiff;
    public ValueDiff? SummaryDiff, DescriptionDiff, OperationIDDiff;
    public ParametersDiffByLocation? ParametersDiff;
    public RequestBodyDiff? RequestBodyDiff;
    public ResponsesDiff? ResponsesDiff;
    public CallbacksDiff? CallbacksDiff;
    public ValueDiff? DeprecatedDiff;
    public SecurityRequirementsDiff? SecurityDiff;
    public ServersDiff? ServersDiff;
    public ExternalDocsDiff? ExternalDocsDiff;
    public Operation? Base, Revision;

    public bool Empty => ExtensionsDiff is null && TagsDiff is null && SummaryDiff is null && DescriptionDiff is null && OperationIDDiff is null &&
        ParametersDiff is null && RequestBodyDiff is null && ResponsesDiff is null && CallbacksDiff is null && DeprecatedDiff is null &&
        SecurityDiff is null && ServersDiff is null && ExternalDocsDiff is null;

    internal static MethodDiff? Get(DiffConfig config, DiffState state, Operation o1, Operation o2, Dictionary<string, string> pathParams)
    {
        var r = new MethodDiff
        {
            ExtensionsDiff = ExtensionsDiff.Get(config, o1.Extensions, o2.Extensions),
            TagsDiff = config.IsExcludeTags ? null : StringsDiff.Get(o1.Tags, o2.Tags),
            SummaryDiff = ValueDiff.Cond(config.IsExcludeSummary, o1.Summary, o2.Summary),
            DescriptionDiff = ValueDiff.Cond(config.IsExcludeDescription, o1.Description, o2.Description),
            OperationIDDiff = ValueDiff.Get(o1.OperationID, o2.OperationID),
        };
        r.ParametersDiff = ParametersDiffByLocation.Get(config, state, o1.Parameters, o2.Parameters, pathParams);
        r.RequestBodyDiff = RequestBodyDiff.Get(config, state, o1.RequestBody, o2.RequestBody);
        r.ResponsesDiff = ResponsesDiff.Get(config, state, o1.Responses, o2.Responses);
        r.CallbacksDiff = CallbacksDiff.Get(config, state, o1.Callbacks, o2.Callbacks);
        r.DeprecatedDiff = ValueDiff.Get(o1.Deprecated, o2.Deprecated);
        r.SecurityDiff = SecurityRequirementsDiff.Get(o1.Security, o2.Security);
        r.ServersDiff = ServersDiff.Get(config, o1.Servers, o2.Servers);
        r.ExternalDocsDiff = ExternalDocsDiff.Get(config, o1.ExternalDocs, o2.ExternalDocs);
        r.Base = o1; r.Revision = o2;
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.Opt("extensions", ExtensionsDiff); m.Opt("tags", TagsDiff); m.Opt("summary", SummaryDiff); m.Opt("description", DescriptionDiff);
        m.Opt("operationID", OperationIDDiff); m.Opt("parameters", ParametersDiff); m.Opt("requestBody", RequestBodyDiff);
        m.Opt("responses", ResponsesDiff); m.Opt("callbacks", CallbacksDiff); m.Opt("deprecated", DeprecatedDiff);
        m.Opt("securityRequirements", SecurityDiff); m.Opt("servers", ServersDiff); m.Opt("externalDocs", ExternalDocsDiff);
        return m;
    }
}

// =========================================================== parameters

public sealed class ParametersDiffByLocation : IDiff
{
    public static readonly string[] ParamLocations = [Parameter.InPath, Parameter.InQuery, Parameter.InHeader, Parameter.InCookie];
    public SortedDictionary<string, List<string>> Added = new(StringComparer.Ordinal), Deleted = new(StringComparer.Ordinal);
    public SortedDictionary<string, SortedDictionary<string, ParameterDiff>> Modified = new(StringComparer.Ordinal);
    public bool Empty => Added.Count == 0 && Deleted.Count == 0 && Modified.Count == 0;
    public SummaryDetails GetSummary() => new(Added.Count, Deleted.Count, Modified.Count);

    public object? ToOutput()
    {
        var m = new OMap();
        if (Added.Count > 0) m.Add("added", Added);
        if (Deleted.Count > 0) m.Add("deleted", Deleted);
        if (Modified.Count > 0) m.Add("modified", Modified);
        return m;
    }

    private void AddAdded(Parameter p) { if (!Added.TryGetValue(p.In, out var l)) Added[p.In] = l = []; l.Add(p.Name); }
    private void AddDeleted(Parameter p) { if (!Deleted.TryGetValue(p.In, out var l)) Deleted[p.In] = l = []; l.Add(p.Name); }
    private void AddModified(Parameter p, ParameterDiff d) { if (!Modified.TryGetValue(p.In, out var m)) Modified[p.In] = m = new(StringComparer.Ordinal); m[p.Name] = d; }

    private static Parameter Deref(Ref<Parameter> r) => r.Value ?? throw new DiffException("parameter reference is nil");

    internal static ParametersDiffByLocation? Get(DiffConfig config, DiffState state, List<Ref<Parameter>> params1, List<Ref<Parameter>> params2, Dictionary<string, string> pathParams)
    {
        var r = new ParametersDiffByLocation();
        var processed1 = new HashSet<Parameter>(ReferenceEqualityComparer.Instance);
        var processed2 = new HashSet<Parameter>(ReferenceEqualityComparer.Instance);
        MatchExplodedWithSimple(config, state, params1, params2, r, processed1, processed2, true);
        MatchExplodedWithSimple(config, state, params2, params1, r, processed2, processed1, false);
        foreach (var pr1 in params1)
        {
            var p1 = Deref(pr1);
            if (processed1.Contains(p1)) continue;
            Parameter? p2 = null;
            foreach (var pr2 in params2) { var c = Deref(pr2); if (EqualParams(p1, c, pathParams)) { p2 = c; break; } }
            if (p2 is not null && !processed2.Contains(p2))
            {
                if (ParameterDiff.Get(config, state, p1, p2) is { } d) r.AddModified(p1, d);
                processed2.Add(p2);
            }
            else r.AddDeleted(p1);
        }
        foreach (var pr2 in params2)
        {
            var p2 = Deref(pr2);
            if (processed2.Contains(p2)) continue;
            r.AddAdded(p2);
        }
        return r.Empty ? null : r;
    }

    private static bool EqualParams(Parameter p1, Parameter p2, Dictionary<string, string> pathParams)
    {
        if (p1.In != p2.In) return false;
        if (p1.In != Parameter.InPath)
        {
            if (p1.Name == p2.Name) return true;
            if (IsExplodedObjectParam(p2)) return IsParamInExplodedObject(p1, p2);
            if (IsExplodedObjectParam(p1)) return IsParamInExplodedObject(p2, p1);
            return false;
        }
        return PathUtil.PathParamsMatch(pathParams, p1.Name, p2.Name);
    }

    private static bool IsQueryOrCookie(Parameter p) => p.In is Parameter.InQuery or Parameter.InCookie;

    internal static bool IsExplodedObjectParam(Parameter? p)
    {
        if (p?.Schema is null) return false;
        bool explode = p.Explode is true;
        bool form = p.Style switch { "form" => true, "" => IsQueryOrCookie(p), _ => false };
        var s = p.Schema.Value!;
        bool obj = s.Type is { Count: 1 } && s.Type[0] == "object" && s.Properties.Count > 0;
        return explode && form && obj;
    }

    private static bool IsParamInExplodedObject(Parameter simple, Parameter exploded)
    {
        if (!IsExplodedObjectParam(exploded) || simple.In != exploded.In) return false;
        return exploded.Schema!.Value!.Properties.ContainsKey(simple.Name);
    }

    private static ParameterDiff? PropertyDiff(DiffConfig config, DiffState state, Parameter simple, Parameter exploded, bool simpleToExploded)
    {
        var props = exploded.Schema!.Value!.Properties;
        if (!props.TryGetValue(simple.Name, out var propSchema)) throw new DiffException($"property {simple.Name} not found in exploded parameter");
        var virt = new Parameter
        {
            Name = simple.Name, In = simple.In, Schema = propSchema, Required = exploded.Required, Style = exploded.Style, Explode = exploded.Explode,
            Description = exploded.Description, Deprecated = exploded.Deprecated, AllowEmptyValue = exploded.AllowEmptyValue, AllowReserved = exploded.AllowReserved,
            Example = exploded.Example, Examples = exploded.Examples, Content = exploded.Content, Extensions = exploded.Extensions,
        };
        return simpleToExploded ? ParameterDiff.Get(config, state, simple, virt) : ParameterDiff.Get(config, state, virt, simple);
    }

    private static void MatchExplodedWithSimple(DiffConfig config, DiffState state, List<Ref<Parameter>> simpleParams, List<Ref<Parameter>> explodedParams,
        ParametersDiffByLocation result, HashSet<Parameter> processedSimple, HashSet<Parameter> processedExploded, bool simpleToExploded)
    {
        foreach (var er in explodedParams)
        {
            var exploded = Deref(er);
            if (!IsExplodedObjectParam(exploded) || processedExploded.Contains(exploded) || !IsQueryOrCookie(exploded)) continue;
            var matching = new List<Parameter>();
            foreach (var sr in simpleParams)
            {
                var simple = Deref(sr);
                if (processedSimple.Contains(simple) || IsExplodedObjectParam(simple) || !IsQueryOrCookie(simple)) continue;
                if (IsParamInExplodedObject(simple, exploded)) matching.Add(simple);
            }
            if (matching.Count == 0) continue;
            foreach (var simple in matching)
            {
                if (PropertyDiff(config, state, simple, exploded, simpleToExploded) is { } d) result.AddModified(simple, d);
                processedSimple.Add(simple);
            }
            processedExploded.Add(exploded);
        }
    }
}

public sealed class ParameterDiff : IDiff
{
    public ValueDiff? NameDiff, InDiff;
    public ExtensionsDiff? ExtensionsDiff;
    public ValueDiff? DescriptionDiff, StyleDiff, ExplodeDiff, AllowEmptyValueDiff, AllowReservedDiff, DeprecatedDiff, RequiredDiff;
    public SchemaDiff? SchemaDiff;
    public ValueDiff? ExampleDiff;
    public ExamplesDiff? ExamplesDiff;
    public ContentDiff? ContentDiff;
    public Parameter? Base, Revision;

    public bool Empty => NameDiff is null && InDiff is null && ExtensionsDiff is null && DescriptionDiff is null && StyleDiff is null && ExplodeDiff is null &&
        AllowEmptyValueDiff is null && AllowReservedDiff is null && DeprecatedDiff is null && RequiredDiff is null && SchemaDiff is null &&
        ExampleDiff is null && ExamplesDiff is null && ContentDiff is null;

    internal static ParameterDiff? Get(DiffConfig config, DiffState state, Parameter p1, Parameter p2)
    {
        var r = new ParameterDiff
        {
            NameDiff = ValueDiff.Get(p1.Name, p2.Name),
            InDiff = ValueDiff.Get(p1.In, p2.In),
            ExtensionsDiff = ExtensionsDiff.Get(config, p1.Extensions, p2.Extensions),
            DescriptionDiff = ValueDiff.Cond(config.IsExcludeDescription, p1.Description, p2.Description),
            StyleDiff = ValueDiff.Get(p1.Style, p2.Style),
            ExplodeDiff = ValueDiff.Ptr(p1.Explode, p2.Explode),
            AllowEmptyValueDiff = ValueDiff.Get(p1.AllowEmptyValue, p2.AllowEmptyValue),
            AllowReservedDiff = ValueDiff.Get(p1.AllowReserved, p2.AllowReserved),
            DeprecatedDiff = ValueDiff.Get(p1.Deprecated, p2.Deprecated),
            RequiredDiff = ValueDiff.Get(p1.Required, p2.Required),
        };
        r.SchemaDiff = SchemaDiff.Get(config, state, p1.Schema, p2.Schema);
        r.ExampleDiff = ValueDiff.Cond(config.IsExcludeExamples, p1.Example, p2.Example);
        r.ExamplesDiff = ExamplesDiff.Get(config, p1.Examples, p2.Examples);
        r.ContentDiff = ContentDiff.Get(config, state, p1.Content, p2.Content);
        r.Base = p1; r.Revision = p2;
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.Opt("name", NameDiff); m.Opt("in", InDiff); m.Opt("extensions", ExtensionsDiff); m.Opt("description", DescriptionDiff);
        m.Opt("style", StyleDiff); m.Opt("explode", ExplodeDiff); m.Opt("allowEmptyValue", AllowEmptyValueDiff); m.Opt("allowReserved", AllowReservedDiff);
        m.Opt("deprecated", DeprecatedDiff); m.Opt("required", RequiredDiff); m.Opt("schema", SchemaDiff); m.Opt("example", ExampleDiff);
        m.Opt("examples", ExamplesDiff); m.Opt("content", ContentDiff);
        return m;
    }
}

public sealed class ParametersDiff : MapDiff<ParameterDiff>
{
    internal static ParametersDiff? Get(DiffConfig config, DiffState state, Dictionary<string, Ref<Parameter>> p1, Dictionary<string, Ref<Parameter>> p2) =>
        Build<ParametersDiff, Ref<Parameter>>(p1, p2, (a, b) => ParameterDiff.Get(config, state, a.Value ?? throw new DiffException("parameter reference is nil"), b.Value ?? throw new DiffException("parameter reference is nil")));
}

// =========================================================== request bodies, responses, content

public sealed class RequestBodyDiff : IDiff
{
    public bool Added, Deleted;
    public ExtensionsDiff? ExtensionsDiff;
    public ValueDiff? DescriptionDiff, RequiredDiff;
    public ContentDiff? ContentDiff;
    public bool Empty => !Added && !Deleted && ExtensionsDiff is null && DescriptionDiff is null && RequiredDiff is null && ContentDiff is null;

    internal static RequestBodyDiff? Get(DiffConfig config, DiffState state, Ref<RequestBody>? r1, Ref<RequestBody>? r2)
    {
        if (r1 is null && r2 is null) return null;
        if (r1 is null) return new RequestBodyDiff { Added = true };
        if (r2 is null) return new RequestBodyDiff { Deleted = true };
        var b1 = r1.Value ?? throw new DiffException("request body reference is nil");
        var b2 = r2.Value ?? throw new DiffException("request body reference is nil");
        var r = new RequestBodyDiff
        {
            ExtensionsDiff = ExtensionsDiff.Get(config, b1.Extensions, b2.Extensions),
            DescriptionDiff = ValueDiff.Cond(config.IsExcludeDescription, b1.Description, b2.Description),
            RequiredDiff = ValueDiff.Get(b1.Required, b2.Required),
        };
        r.ContentDiff = ContentDiff.Get(config, state, b1.Content, b2.Content);
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptTrue("added", Added); m.OptTrue("deleted", Deleted);
        m.Opt("extensions", ExtensionsDiff); m.Opt("description", DescriptionDiff); m.Opt("required", RequiredDiff); m.Opt("content", ContentDiff);
        return m;
    }
}

public sealed class RequestBodiesDiff : MapDiff<RequestBodyDiff>
{
    internal static RequestBodiesDiff? Get(DiffConfig config, DiffState state, Dictionary<string, Ref<RequestBody>> m1, Dictionary<string, Ref<RequestBody>> m2) =>
        Build<RequestBodiesDiff, Ref<RequestBody>>(m1, m2, (a, b) => RequestBodyDiff.Get(config, state, a, b));
}

public sealed class ResponsesDiff : MapDiff<ResponseDiff>
{
    internal static ResponsesDiff? Get(DiffConfig config, DiffState state, Responses? r1, Responses? r2) =>
        Build<ResponsesDiff, Ref<Response>>(r1?.Map, r2?.Map, (a, b) => ResponseDiff.Get(config, state,
            a.Value ?? throw new DiffException("response reference is nil"), b.Value ?? throw new DiffException("response reference is nil")));

    internal static ResponsesDiff? Get(DiffConfig config, DiffState state, Dictionary<string, Ref<Response>> r1, Dictionary<string, Ref<Response>> r2) =>
        Build<ResponsesDiff, Ref<Response>>(r1, r2, (a, b) => ResponseDiff.Get(config, state,
            a.Value ?? throw new DiffException("response reference is nil"), b.Value ?? throw new DiffException("response reference is nil")));
}

public sealed class ResponseDiff : IDiff
{
    public ExtensionsDiff? ExtensionsDiff;
    public ValueDiff? DescriptionDiff;
    public HeadersDiff? HeadersDiff;
    public ContentDiff? ContentDiff;
    public LinksDiff? LinksDiff;
    public Response? Base, Revision;
    public bool Empty => ExtensionsDiff is null && DescriptionDiff is null && HeadersDiff is null && ContentDiff is null && LinksDiff is null;

    internal static ResponseDiff? Get(DiffConfig config, DiffState state, Response r1, Response r2)
    {
        var r = new ResponseDiff
        {
            ExtensionsDiff = ExtensionsDiff.Get(config, r1.Extensions, r2.Extensions),
            DescriptionDiff = config.IsExcludeDescription ? null : ValueDiff.Get(r1.Description!, r2.Description!),
        };
        r.HeadersDiff = HeadersDiff.Get(config, state, r1.Headers, r2.Headers);
        r.ContentDiff = ContentDiff.Get(config, state, r1.Content, r2.Content);
        r.LinksDiff = LinksDiff.Get(config, r1.Links, r2.Links);
        r.Base = r1; r.Revision = r2;
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.Opt("extensions", ExtensionsDiff); m.Opt("description", DescriptionDiff); m.Opt("headers", HeadersDiff); m.Opt("content", ContentDiff); m.Opt("links", LinksDiff);
        return m;
    }
}

public sealed class ContentDiff : IDiff
{
    public List<string> MediaTypeAdded = [], MediaTypeDeleted = [];
    public SortedDictionary<string, MediaTypeDiff> MediaTypeModified = new(StringComparer.Ordinal);
    public bool Empty => MediaTypeAdded.Count == 0 && MediaTypeDeleted.Count == 0 && MediaTypeModified.Count == 0;

    internal static ContentDiff? Get(DiffConfig config, DiffState state, Dictionary<string, MediaType> c1, Dictionary<string, MediaType> c2)
    {
        var (added, deleted, common) = DiffUtil.KeysDiff(c1, c2);
        var modified = new SortedDictionary<string, MediaTypeDiff>(StringComparer.Ordinal);
        foreach (var name in common)
            if (MediaTypeDiff.Get(config, state, name, name, c1[name], c2[name]) is { } d) modified[name] = d;
        var processedDeleted = new HashSet<string>(StringComparer.Ordinal);
        var finalAdded = new List<string>();
        foreach (var a in added)
        {
            bool found = false;
            foreach (var d in deleted)
            {
                if (processedDeleted.Contains(d)) continue;
                if (MediaTypeName.IsContained(a, d) || MediaTypeName.IsContained(d, a))
                {
                    modified[a] = MediaTypeDiff.Get(config, state, d, a, c1[d], c2[a])!;
                    processedDeleted.Add(d);
                    found = true;
                    break;
                }
            }
            if (!found) finalAdded.Add(a);
        }
        var r = new ContentDiff { MediaTypeAdded = finalAdded, MediaTypeDeleted = deleted.Where(d => !processedDeleted.Contains(d)).ToList(), MediaTypeModified = modified };
        // a paired media type with an empty diff is still recorded as modified (nil entry in Go)
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptList("added", MediaTypeAdded); m.OptList("deleted", MediaTypeDeleted);
        if (MediaTypeModified.Count > 0) m.Add("modified", MediaTypeModified);
        return m;
    }
}

public sealed class MediaTypeName
{
    public string Name = "", Type = "", Subtype = "", Suffix = "";
    public Dictionary<string, string> Parameters = new(StringComparer.Ordinal);

    public string BareName() => Suffix == "" ? Type + "/" + Subtype : Type + "/" + Subtype + "+" + Suffix;

    /// <summary>mime.ParseMediaType + split into type/subtype/suffix.</summary>
    public static MediaTypeName Parse(string mediaType)
    {
        var parts = mediaType.Split(';');
        var bare = parts[0].Trim().ToLowerInvariant();
        if (bare.Length == 0) throw new DiffException($"failed to parse media type '{mediaType}': mime: no media type");
        var r = new MediaTypeName { Name = mediaType };
        for (int i = 1; i < parts.Length; i++)
        {
            var p = parts[i].Trim();
            if (p.Length == 0) continue;
            int eq = p.IndexOf('=');
            if (eq <= 0) throw new DiffException($"failed to parse media type '{mediaType}': mime: invalid media parameter");
            var v = p[(eq + 1)..].Trim();
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1];
            r.Parameters[p[..eq].Trim().ToLowerInvariant()] = v;
        }
        var tp = bare.Split('/');
        if (tp.Length != 2) throw new DiffException($"invalid media type format (missing '/'): {bare}");
        r.Type = tp[0].Trim();
        var sub = tp[1].Trim().Split('+');
        r.Subtype = sub[0].Trim();
        if (sub.Length == 2)
        {
            r.Suffix = sub[1].Trim();
            if (r.Suffix == "") throw new DiffException($"invalid media type: empty suffix in '{bare}'");
        }
        else if (sub.Length > 2) throw new DiffException($"multiple suffixes not supported in '{bare}'");
        return r;
    }

    public static bool TryParse(string s, out MediaTypeName r)
    {
        try { r = Parse(s); return true; }
        catch (DiffException) { r = null!; return false; }
    }

    public static bool IsContained(string m1, string m2)
    {
        if (!TryParse(m1, out var p1) || !TryParse(m2, out var p2)) return false;
        if (p1.Type != p2.Type) return false;
        if (p1.Suffix == "" && p2.Suffix != "" && p2.Suffix == p1.Subtype) return true;
        return p1.Subtype == p2.Subtype && p1.Suffix == p2.Suffix;
    }
}

public sealed class MediaTypeNameDiff : IDiff
{
    public ValueDiff? NameDiff, TypeDiff, SubtypeDiff, SuffixDiff;
    public StringMapDiff? ParametersDiff;
    public bool Contained;
    public string BaseBareName = "";
    public bool BareNameChanged => TypeDiff is not null || SubtypeDiff is not null || SuffixDiff is not null;
    public bool Empty => NameDiff is null && TypeDiff is null && SubtypeDiff is null && SuffixDiff is null && ParametersDiff is null && !Contained && BaseBareName == "";

    public static MediaTypeNameDiff? Get(string n1, string n2)
    {
        if (n1 == n2) return null;
        var m1 = MediaTypeName.Parse(n1);
        var m2 = MediaTypeName.Parse(n2);
        return new MediaTypeNameDiff
        {
            NameDiff = ValueDiff.Get(n1, n2), TypeDiff = ValueDiff.Get(m1.Type, m2.Type), SubtypeDiff = ValueDiff.Get(m1.Subtype, m2.Subtype),
            SuffixDiff = ValueDiff.Get(m1.Suffix, m2.Suffix), ParametersDiff = StringMapDiff.Get(m1.Parameters, m2.Parameters),
            Contained = MediaTypeName.IsContained(n1, n2), BaseBareName = m1.BareName(),
        };
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.Opt("name", NameDiff); m.Opt("type", TypeDiff); m.Opt("subtype", SubtypeDiff); m.Opt("suffix", SuffixDiff); m.Opt("parameters", ParametersDiff);
        return m;
    }
}

public sealed class MediaTypeDiff : IDiff
{
    public ExtensionsDiff? ExtensionsDiff;
    public SchemaDiff? SchemaDiff, ItemSchemaDiff;
    public ValueDiff? ExampleDiff;
    public ExamplesDiff? ExamplesDiff;
    public EncodingsDiff? EncodingsDiff;
    public MediaTypeNameDiff? NameDiff;
    public bool Empty => ExtensionsDiff is null && SchemaDiff is null && ItemSchemaDiff is null && ExampleDiff is null && ExamplesDiff is null && EncodingsDiff is null && NameDiff is null;

    internal static MediaTypeDiff? Get(DiffConfig config, DiffState state, string n1, string n2, MediaType m1, MediaType m2)
    {
        var r = new MediaTypeDiff { NameDiff = MediaTypeNameDiff.Get(n1, n2), ExtensionsDiff = ExtensionsDiff.Get(config, m1.Extensions, m2.Extensions) };
        r.SchemaDiff = SchemaDiff.Get(config, state, m1.Schema, m2.Schema);
        r.ItemSchemaDiff = SchemaDiff.Get(config, state, m1.ItemSchema, m2.ItemSchema);
        r.ExampleDiff = ValueDiff.Cond(config.IsExcludeExamples, m1.Example, m2.Example);
        r.EncodingsDiff = EncodingsDiff.Get(config, state, m1.Encoding, m2.Encoding);
        r.ExamplesDiff = ExamplesDiff.Get(config, m1.Examples, m2.Examples);
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.Opt("extensions", ExtensionsDiff); m.Opt("schema", SchemaDiff); m.Opt("itemSchema", ItemSchemaDiff); m.Opt("example", ExampleDiff);
        m.Opt("examples", ExamplesDiff); m.Opt("encoding", EncodingsDiff); m.Opt("name", NameDiff);
        return m;
    }
}

public sealed class EncodingsDiff : MapDiff<EncodingDiff>
{
    internal static EncodingsDiff? Get(DiffConfig config, DiffState state, Dictionary<string, Encoding> e1, Dictionary<string, Encoding> e2) =>
        Build<EncodingsDiff, Encoding>(e1, e2, (a, b) => EncodingDiff.Get(config, state, a, b));
}

public sealed class EncodingDiff : IDiff
{
    public ExtensionsDiff? ExtensionsDiff;
    public ValueDiff? ContentTypeDiff;
    public HeadersDiff? HeadersDiff;
    public ValueDiff? StyleDiff, ExplodeDiff, AllowReservedDiff;
    public bool Empty => ExtensionsDiff is null && ContentTypeDiff is null && HeadersDiff is null && StyleDiff is null && ExplodeDiff is null && AllowReservedDiff is null;

    internal static EncodingDiff? Get(DiffConfig config, DiffState state, Encoding e1, Encoding e2)
    {
        var r = new EncodingDiff
        {
            ExtensionsDiff = ExtensionsDiff.Get(config, e1.Extensions, e2.Extensions),
            ContentTypeDiff = ValueDiff.Get(e1.ContentType, e2.ContentType),
            HeadersDiff = HeadersDiff.Get(config, state, e1.Headers, e2.Headers),
            StyleDiff = ValueDiff.Get(e1.Style, e2.Style),
            ExplodeDiff = ValueDiff.Ptr(e1.Explode, e2.Explode),
            AllowReservedDiff = ValueDiff.Get(e1.AllowReserved, e2.AllowReserved),
        };
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.Opt("extensions", ExtensionsDiff); m.Opt("contentType", ContentTypeDiff); m.Opt("headers", HeadersDiff);
        m.Opt("styleDiff", StyleDiff); m.Opt("explode", ExplodeDiff); m.Opt("allowReservedDiff", AllowReservedDiff);
        return m;
    }
}

public sealed class HeadersDiff : MapDiff<HeaderDiff>
{
    internal static HeadersDiff? Get(DiffConfig config, DiffState state, Dictionary<string, Ref<Header>> h1, Dictionary<string, Ref<Header>> h2) =>
        Build<HeadersDiff, Ref<Header>>(h1, h2, (a, b) => HeaderDiff.Get(config, state,
            a.Value ?? throw new DiffException("header reference is nil"), b.Value ?? throw new DiffException("header reference is nil")));
}

public sealed class HeaderDiff : IDiff
{
    public ExtensionsDiff? ExtensionsDiff;
    public ValueDiff? DescriptionDiff, DeprecatedDiff, RequiredDiff, ExampleDiff;
    public ExamplesDiff? ExamplesDiff;
    public SchemaDiff? SchemaDiff;
    public ContentDiff? ContentDiff;
    public bool Empty => ExtensionsDiff is null && DescriptionDiff is null && DeprecatedDiff is null && RequiredDiff is null && ExampleDiff is null && ExamplesDiff is null && SchemaDiff is null && ContentDiff is null;

    internal static HeaderDiff? Get(DiffConfig config, DiffState state, Header h1, Header h2)
    {
        var r = new HeaderDiff
        {
            ExtensionsDiff = ExtensionsDiff.Get(config, h1.Extensions, h2.Extensions),
            DescriptionDiff = ValueDiff.Cond(config.IsExcludeDescription, h1.Description, h2.Description),
            DeprecatedDiff = ValueDiff.Get(h1.Deprecated, h2.Deprecated),
            RequiredDiff = ValueDiff.Get(h1.Required, h2.Required),
            ExamplesDiff = ExamplesDiff.Get(config, h1.Examples, h2.Examples),
        };
        r.SchemaDiff = SchemaDiff.Get(config, state, h1.Schema, h2.Schema);
        r.ExampleDiff = ValueDiff.Cond(config.IsExcludeExamples, h1.Example, h2.Example);
        r.ContentDiff = ContentDiff.Get(config, state, h1.Content, h2.Content);
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.Opt("extensions", ExtensionsDiff); m.Opt("description", DescriptionDiff); m.Opt("deprecated", DeprecatedDiff); m.Opt("required", RequiredDiff);
        m.Opt("example", ExampleDiff); m.Opt("examples", ExamplesDiff); m.Opt("schema", SchemaDiff); m.Opt("content", ContentDiff);
        return m;
    }
}

public sealed class ExamplesDiff : MapDiff<ExampleDiff>
{
    internal static ExamplesDiff? Get(DiffConfig config, Dictionary<string, Ref<Example>> e1, Dictionary<string, Ref<Example>> e2)
    {
        if (config.IsExcludeExamples) return null;
        return Build<ExamplesDiff, Ref<Example>>(e1, e2, (a, b) => ExampleDiff.Get(config,
            a.Value ?? throw new DiffException("example reference is nil"), b.Value ?? throw new DiffException("example reference is nil")));
    }
}

public sealed class ExampleDiff : IDiff
{
    public ExtensionsDiff? ExtensionsDiff;
    public ValueDiff? SummaryDiff, DescriptionDiff, ValueDiff, ExternalValueDiff;
    public bool Empty => ExtensionsDiff is null && SummaryDiff is null && DescriptionDiff is null && ValueDiff is null && ExternalValueDiff is null;

    internal static ExampleDiff? Get(DiffConfig config, Example e1, Example e2)
    {
        var r = new ExampleDiff
        {
            ExtensionsDiff = ExtensionsDiff.Get(config, e1.Extensions, e2.Extensions),
            SummaryDiff = Diff.ValueDiff.Cond(config.IsExcludeSummary, e1.Summary, e2.Summary),
            DescriptionDiff = Diff.ValueDiff.Cond(config.IsExcludeDescription, e1.Description, e2.Description),
            ValueDiff = Diff.ValueDiff.Get(e1.Value, e2.Value),
            ExternalValueDiff = Diff.ValueDiff.Get(e1.ExternalValue, e2.ExternalValue),
        };
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.Opt("extensions", ExtensionsDiff); m.Opt("summary", SummaryDiff); m.Opt("description", DescriptionDiff); m.Opt("value", ValueDiff); m.Opt("externalValue", ExternalValueDiff);
        return m;
    }
}

public sealed class LinksDiff : MapDiff<LinkDiff>
{
    internal static LinksDiff? Get(DiffConfig config, Dictionary<string, Ref<Link>> l1, Dictionary<string, Ref<Link>> l2) =>
        Build<LinksDiff, Ref<Link>>(l1, l2, (a, b) => LinkDiff.Get(config,
            a.Value ?? throw new DiffException("link reference is nil"), b.Value ?? throw new DiffException("link reference is nil")));
}

public sealed class LinkDiff : IDiff
{
    public ExtensionsDiff? ExtensionsDiff;
    public ValueDiff? OperationIDDiff, OperationRefDiff, DescriptionDiff;
    public ExtensionsDiff? ParametersDiff;
    public ServerDiff? ServerDiff;
    public ValueDiff? RequestBodyDiff;
    public bool Empty => ExtensionsDiff is null && OperationIDDiff is null && OperationRefDiff is null && DescriptionDiff is null && ParametersDiff is null && ServerDiff is null && RequestBodyDiff is null;

    internal static LinkDiff? Get(DiffConfig config, Link l1, Link l2)
    {
        var r = new LinkDiff
        {
            ExtensionsDiff = ExtensionsDiff.Get(config, l1.Extensions, l2.Extensions),
            OperationIDDiff = ValueDiff.Get(l1.OperationID, l2.OperationID),
            OperationRefDiff = ValueDiff.Get(l1.OperationRef, l2.OperationRef),
            DescriptionDiff = ValueDiff.Cond(config.IsExcludeDescription, l1.Description, l2.Description),
            ParametersDiff = ExtensionsDiff.GetInterfaceMapDiff(l1.Parameters, l2.Parameters),
            ServerDiff = ServerDiff.Get(config, l1.Server, l2.Server),
            RequestBodyDiff = ValueDiff.Get(l1.RequestBody, l2.RequestBody),
        };
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.Opt("extensions", ExtensionsDiff); m.Opt("operationId", OperationIDDiff); m.Opt("operationRef", OperationRefDiff); m.Opt("description", DescriptionDiff);
        m.Opt("parameters", ParametersDiff); m.Opt("server", ServerDiff); m.Opt("requestBody", RequestBodyDiff);
        return m;
    }
}

public sealed class CallbacksDiff : MapDiff<PathsDiff>
{
    internal static CallbacksDiff? Get(DiffConfig config, DiffState state, Dictionary<string, Ref<Callback>> c1, Dictionary<string, Ref<Callback>> c2) =>
        Build<CallbacksDiff, Ref<Callback>>(c1, c2, (a, b) =>
        {
            var v1 = a.Value ?? throw new DiffException("callback reference is nil");
            var v2 = b.Value ?? throw new DiffException("callback reference is nil");
            var pair = (v1, v2);
            if (!state.CallbacksInProgress.Add(pair)) return null;
            try
            {
                var p1 = new Paths(); foreach (var (k, v) in v1.Map) p1.Map[k] = v;
                var p2 = new Paths(); foreach (var (k, v) in v2.Map) p2.Map[k] = v;
                return PathsDiff.Get(config, state, p1, p2);
            }
            finally { state.CallbacksInProgress.Remove(pair); }
        });
}

// =========================================================== security

public sealed class SecurityAlternative : IOutput
{
    public int Index;
    public SortedDictionary<string, List<string>> Schemes = new(StringComparer.Ordinal);

    public SecurityAlternative(int index, SecurityRequirement req)
    {
        Index = index;
        foreach (var (k, v) in req) Schemes[k] = v;
    }

    public override string ToString()
    {
        if (Schemes.Count == 0) return "{}";
        var parts = new List<string>();
        foreach (var (scheme, scopes) in Schemes)
        {
            if (scopes.Count == 0) { parts.Add(scheme); continue; }
            var s = new List<string>(scopes); s.Sort(StringComparer.Ordinal);
            parts.Add($"{scheme}: [{string.Join(", ", s)}]");
        }
        return string.Join(" AND ", parts);
    }

    public string SchemeNames() => string.Join(" AND ", Schemes.Keys);

    public object? ToOutput() => new OMap { { "index", (long)Index }, { "schemes", Schemes } };
}

public sealed class ModifiedSecurityRequirement : IOutput
{
    public required SecurityAlternative Base, Revision;
    public SortedDictionary<string, StringsDiff> Scopes = new(StringComparer.Ordinal);
    public object? ToOutput() => new OMap { { "base", Base }, { "revision", Revision }, { "scopes", Scopes } };
}

public sealed class SecurityRequirementsDiff : IDiff
{
    public List<SecurityAlternative> Added = [], Deleted = [];
    public List<ModifiedSecurityRequirement> Modified = [];
    public bool ListAdded, ListDeleted;
    public Origin? BaseOrigin, RevisionOrigin;
    public bool Empty => Added.Count == 0 && Deleted.Count == 0 && Modified.Count == 0 && !ListAdded && !ListDeleted;
    public SummaryDetails GetSummary() => new(Added.Count, Deleted.Count, Modified.Count);

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptList("added", Added); m.OptList("deleted", Deleted); m.OptList("modified", Modified);
        m.OptTrue("listAdded", ListAdded); m.OptTrue("listDeleted", ListDeleted);
        return m;
    }

    private static HashSet<string> Schemes(SecurityRequirement r) => new(r.Keys, StringComparer.Ordinal);

    internal static SortedDictionary<string, StringsDiff> ScopesDiff(SecurityRequirement r1, SecurityRequirement r2)
    {
        var res = new SortedDictionary<string, StringsDiff>(StringComparer.Ordinal);
        foreach (var (scheme, scopes1) in r1)
            if (r2.TryGetValue(scheme, out var scopes2) && StringsDiff.Get(scopes1, scopes2) is { } d) res[scheme] = d;
        return res;
    }

    private static int Find(SecurityRequirement r, List<SecurityRequirement> candidates, bool[] matched, bool exact)
    {
        var schemes = Schemes(r);
        for (int j = 0; j < candidates.Count; j++)
        {
            if (matched[j] || !schemes.SetEquals(Schemes(candidates[j]))) continue;
            if (exact && ScopesDiff(r, candidates[j]).Count != 0) continue;
            return j;
        }
        return -1;
    }

    private static bool UniqueUnmatched(List<SecurityRequirement> reqs, bool[] matched, HashSet<string> schemes)
    {
        int count = 0;
        for (int i = 0; i < reqs.Count; i++)
        {
            if (matched[i]) continue;
            if (schemes.SetEquals(Schemes(reqs[i])) && ++count > 1) return false;
        }
        return count == 1;
    }

    public static SecurityRequirementsDiff? Get(List<SecurityRequirement>? s1, List<SecurityRequirement>? s2)
    {
        var r = new SecurityRequirementsDiff { ListAdded = s1 is null && s2 is not null, ListDeleted = s1 is not null && s2 is null };
        var reqs1 = s1 ?? []; var reqs2 = s2 ?? [];
        var m1 = new bool[reqs1.Count]; var m2 = new bool[reqs2.Count];
        for (int i = 0; i < reqs1.Count; i++)
        {
            int j = Find(reqs1[i], reqs2, m2, true);
            if (j >= 0) { m1[i] = true; m2[j] = true; }
        }
        for (int i = 0; i < reqs1.Count; i++)
        {
            if (m1[i] || !UniqueUnmatched(reqs1, m1, Schemes(reqs1[i]))) continue;
            int j = Find(reqs1[i], reqs2, m2, false);
            if (j < 0 || !UniqueUnmatched(reqs2, m2, Schemes(reqs2[j]))) continue;
            m1[i] = true; m2[j] = true;
            var sd = ScopesDiff(reqs1[i], reqs2[j]);
            if (sd.Count > 0) r.Modified.Add(new ModifiedSecurityRequirement { Base = new(i, reqs1[i]), Revision = new(j, reqs2[j]), Scopes = sd });
        }
        for (int i = 0; i < reqs1.Count; i++) if (!m1[i]) r.Deleted.Add(new(i, reqs1[i]));
        for (int j = 0; j < reqs2.Count; j++) if (!m2[j]) r.Added.Add(new(j, reqs2[j]));
        return r.Empty ? null : r;
    }
}

public sealed class SecuritySchemesDiff : MapDiff<SecuritySchemeDiff>
{
    public Dictionary<string, Ref<SecurityScheme>>? Base, Revision;

    internal static SecuritySchemesDiff? Get(DiffConfig config, Dictionary<string, Ref<SecurityScheme>> s1, Dictionary<string, Ref<SecurityScheme>> s2)
    {
        var r = Build<SecuritySchemesDiff, Ref<SecurityScheme>>(s1, s2, (a, b) => SecuritySchemeDiff.Get(config,
            a.Value ?? throw new DiffException("security scheme reference is nil"), b.Value ?? throw new DiffException("security scheme reference is nil")));
        if (r is not null) { r.Base = s1; r.Revision = s2; }
        return r;
    }
}

public sealed class SecuritySchemeDiff : IDiff
{
    public ExtensionsDiff? ExtensionsDiff;
    public ValueDiff? TypeDiff, DescriptionDiff, NameDiff, InDiff, SchemeDiff, BearerFormatDiff;
    public OAuthFlowsDiff? OAuthFlowsDiff;
    public ValueDiff? OpenIDConnectURLDiff;
    public bool Empty => ExtensionsDiff is null && TypeDiff is null && DescriptionDiff is null && NameDiff is null && InDiff is null && SchemeDiff is null && BearerFormatDiff is null && OAuthFlowsDiff is null && OpenIDConnectURLDiff is null;

    internal static SecuritySchemeDiff? Get(DiffConfig config, SecurityScheme s1, SecurityScheme s2)
    {
        var r = new SecuritySchemeDiff
        {
            ExtensionsDiff = ExtensionsDiff.Get(config, s1.Extensions, s2.Extensions),
            TypeDiff = ValueDiff.Get(s1.Type, s2.Type),
            DescriptionDiff = ValueDiff.Cond(config.IsExcludeDescription, s1.Description, s2.Description),
            NameDiff = ValueDiff.Get(s1.Name, s2.Name), InDiff = ValueDiff.Get(s1.In, s2.In), SchemeDiff = ValueDiff.Get(s1.Scheme, s2.Scheme),
            BearerFormatDiff = ValueDiff.Get(s1.BearerFormat, s2.BearerFormat),
            OAuthFlowsDiff = OAuthFlowsDiff.Get(config, s1.Flows, s2.Flows),
            OpenIDConnectURLDiff = ValueDiff.Get(s1.OpenIdConnectUrl, s2.OpenIdConnectUrl),
        };
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.Opt("extensions", ExtensionsDiff); m.Opt("type", TypeDiff); m.Opt("description", DescriptionDiff); m.Opt("name", NameDiff); m.Opt("in", InDiff);
        m.Opt("scheme", SchemeDiff); m.Opt("bearerFormat", BearerFormatDiff); m.Opt("OAuthFlows", OAuthFlowsDiff); m.Opt("openIDConnectURL", OpenIDConnectURLDiff);
        return m;
    }
}

public sealed class OAuthFlowsDiff : IDiff
{
    public bool Added, Deleted;
    public ExtensionsDiff? ExtensionsDiff;
    public OAuthFlowDiff? ImplicitDiff, PasswordDiff, ClientCredentialsDiff, AuthorizationCodeDiff;
    public bool Empty => !Added && !Deleted && ExtensionsDiff is null && ImplicitDiff is null && PasswordDiff is null && ClientCredentialsDiff is null && AuthorizationCodeDiff is null;

    internal static OAuthFlowsDiff? Get(DiffConfig config, OAuthFlows? f1, OAuthFlows? f2)
    {
        if (f1 is null && f2 is null) return null;
        if (f1 is null) return new OAuthFlowsDiff { Added = true };
        if (f2 is null) return new OAuthFlowsDiff { Deleted = true };
        var r = new OAuthFlowsDiff
        {
            ExtensionsDiff = ExtensionsDiff.Get(config, f1.Extensions, f2.Extensions),
            ImplicitDiff = OAuthFlowDiff.Get(config, f1.Implicit, f2.Implicit),
            PasswordDiff = OAuthFlowDiff.Get(config, f1.Password, f2.Password),
            ClientCredentialsDiff = OAuthFlowDiff.Get(config, f1.ClientCredentials, f2.ClientCredentials),
            AuthorizationCodeDiff = OAuthFlowDiff.Get(config, f1.AuthorizationCode, f2.AuthorizationCode),
        };
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptTrue("added", Added); m.OptTrue("deleted", Deleted); m.Opt("extensions", ExtensionsDiff);
        m.Opt("implicit", ImplicitDiff); m.Opt("password", PasswordDiff); m.Opt("clientCredentials", ClientCredentialsDiff); m.Opt("authorizationCode", AuthorizationCodeDiff);
        return m;
    }
}

public sealed class OAuthFlowDiff : IDiff
{
    public bool Added, Deleted;
    public ExtensionsDiff? ExtensionsDiff;
    public ValueDiff? AuthorizationURLDiff, TokenURLDiff, RefreshURLDiff;
    public StringMapDiff? ScopesDiff;
    public bool Empty => !Added && !Deleted && ExtensionsDiff is null && AuthorizationURLDiff is null && TokenURLDiff is null && RefreshURLDiff is null && ScopesDiff is null;

    internal static OAuthFlowDiff? Get(DiffConfig config, OAuthFlow? f1, OAuthFlow? f2)
    {
        if (f1 is null && f2 is null) return null;
        if (f1 is null) return new OAuthFlowDiff { Added = true };
        if (f2 is null) return new OAuthFlowDiff { Deleted = true };
        var r = new OAuthFlowDiff
        {
            ExtensionsDiff = ExtensionsDiff.Get(config, f1.Extensions, f2.Extensions),
            AuthorizationURLDiff = ValueDiff.Get(f1.AuthorizationURL, f2.AuthorizationURL),
            TokenURLDiff = ValueDiff.Get(f1.TokenURL, f2.TokenURL),
            RefreshURLDiff = ValueDiff.Get(f1.RefreshURL, f2.RefreshURL),
            ScopesDiff = StringMapDiff.Get(f1.Scopes, f2.Scopes),
        };
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptTrue("added", Added); m.OptTrue("deleted", Deleted); m.Opt("extensions", ExtensionsDiff);
        m.Opt("authorizationURL", AuthorizationURLDiff); m.Opt("tokenURL", TokenURLDiff); m.Opt("refresh", RefreshURLDiff); m.Opt("scopes", ScopesDiff);
        return m;
    }
}

// =========================================================== servers, tags

public sealed class ServersDiff : MapDiff<ServerDiff>
{
    internal static ServersDiff? Get(DiffConfig config, List<Server>? s1, List<Server>? s2)
    {
        var r = new ServersDiff();
        s1 ??= []; s2 ??= [];
        foreach (var a in s1)
        {
            var b = s2.FirstOrDefault(x => x.URL == a.URL);
            if (b is not null) { if (ServerDiff.Get(config, a, b) is { } d) r.Modified[a.URL] = d; }
            else r.Deleted.Add(a.URL);
        }
        foreach (var b in s2) if (!s1.Any(x => x.URL == b.URL)) r.Added.Add(b.URL);
        return r.Empty ? null : r;
    }
}

public sealed class ServerDiff : IDiff
{
    public bool Added, Deleted;
    public ExtensionsDiff? ExtensionsDiff;
    public ValueDiff? URLDiff, DescriptionDiff;
    public VariablesDiff? VariablesDiff;
    public bool Empty => !Added && !Deleted && ExtensionsDiff is null && URLDiff is null && DescriptionDiff is null && VariablesDiff is null;

    internal static ServerDiff? Get(DiffConfig config, Server? s1, Server? s2)
    {
        if (s1 is null && s2 is null) return null;
        if (s1 is null) return new ServerDiff { Added = true };
        if (s2 is null) return new ServerDiff { Deleted = true };
        var r = new ServerDiff
        {
            ExtensionsDiff = ExtensionsDiff.Get(config, s1.Extensions, s2.Extensions),
            URLDiff = ValueDiff.Get(s1.URL, s2.URL),
            DescriptionDiff = ValueDiff.Cond(config.IsExcludeDescription, s1.Description, s2.Description),
            VariablesDiff = VariablesDiff.Get(config, s1.Variables, s2.Variables),
        };
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptTrue("added", Added); m.OptTrue("deleted", Deleted); m.Opt("extensions", ExtensionsDiff);
        m.Opt("urlType", URLDiff); m.Opt("description", DescriptionDiff); m.Opt("variables", VariablesDiff);
        return m;
    }
}

public sealed class VariablesDiff : MapDiff<VariableDiff>
{
    internal static VariablesDiff? Get(DiffConfig config, Dictionary<string, ServerVariable> v1, Dictionary<string, ServerVariable> v2) =>
        Build<VariablesDiff, ServerVariable>(v1, v2, (a, b) => VariableDiff.Get(config, a, b));
}

public sealed class VariableDiff : IDiff
{
    public ExtensionsDiff? ExtensionsDiff;
    public StringsDiff? EnumDiff;
    public ValueDiff? DefaultDiff, DescriptionDiff;
    public bool Empty => ExtensionsDiff is null && EnumDiff is null && DefaultDiff is null && DescriptionDiff is null;

    internal static VariableDiff? Get(DiffConfig config, ServerVariable v1, ServerVariable v2)
    {
        var r = new VariableDiff
        {
            ExtensionsDiff = ExtensionsDiff.Get(config, v1.Extensions, v2.Extensions),
            EnumDiff = StringsDiff.Get(v1.Enum, v2.Enum),
            DefaultDiff = ValueDiff.Get(v1.Default, v2.Default),
            DescriptionDiff = ValueDiff.Cond(config.IsExcludeDescription, v1.Description, v2.Description),
        };
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.Opt("extensions", ExtensionsDiff); m.Opt("enum", EnumDiff); m.Opt("default", DefaultDiff); m.Opt("description", DescriptionDiff);
        return m;
    }
}

public sealed class TagsDiff : MapDiff<TagDiff>
{
    internal static TagsDiff? Get(DiffConfig config, List<Tag> t1, List<Tag> t2)
    {
        var r = new TagsDiff();
        foreach (var a in t1)
        {
            var b = t2.FirstOrDefault(x => x.Name == a.Name);
            if (b is not null) { if (TagDiff.Get(config, a, b) is { } d) r.Modified[a.Name] = d; }
            else r.Deleted.Add(a.Name);
        }
        foreach (var b in t2) if (!t1.Any(x => x.Name == b.Name)) r.Added.Add(b.Name);
        return r.Empty ? null : r;
    }
}

public sealed class TagDiff : IDiff
{
    public ValueDiff? NameDiff, DescriptionDiff;
    public bool Empty => NameDiff is null && DescriptionDiff is null;

    internal static TagDiff? Get(DiffConfig config, Tag t1, Tag t2)
    {
        var r = new TagDiff { NameDiff = ValueDiff.Get(t1.Name, t2.Name), DescriptionDiff = ValueDiff.Cond(config.IsExcludeDescription, t1.Description, t2.Description) };
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.Opt("name", NameDiff); m.Opt("description", DescriptionDiff);
        return m;
    }
}

// =========================================================== components, webhooks, endpoints

public sealed class ComponentsDiff : IDiff
{
    public SchemasDiff? SchemasDiff;
    public ParametersDiff? ParametersDiff;
    public HeadersDiff? HeadersDiff;
    public RequestBodiesDiff? RequestBodiesDiff;
    public ResponsesDiff? ResponsesDiff;
    public SecuritySchemesDiff? SecuritySchemesDiff;
    public ExamplesDiff? ExamplesDiff;
    public LinksDiff? LinksDiff;
    public CallbacksDiff? CallbacksDiff;
    public bool Empty => SchemasDiff is null && ParametersDiff is null && HeadersDiff is null && RequestBodiesDiff is null && ResponsesDiff is null && SecuritySchemesDiff is null && ExamplesDiff is null && LinksDiff is null && CallbacksDiff is null;

    internal static ComponentsDiff? Get(DiffConfig config, DiffState state, Components? c1, Components? c2)
    {
        if (c1 is null && c2 is null) return null;
        c1 ??= new Components(); c2 ??= new Components();
        var r = new ComponentsDiff
        {
            SchemasDiff = SchemasDiff.Get(config, state, c1.Schemas, c2.Schemas),
            ParametersDiff = ParametersDiff.Get(config, state, c1.Parameters, c2.Parameters),
            HeadersDiff = HeadersDiff.Get(config, state, c1.Headers, c2.Headers),
            RequestBodiesDiff = RequestBodiesDiff.Get(config, state, c1.RequestBodies, c2.RequestBodies),
            ResponsesDiff = ResponsesDiff.Get(config, state, c1.Responses, c2.Responses),
            SecuritySchemesDiff = SecuritySchemesDiff.Get(config, c1.SecuritySchemes, c2.SecuritySchemes),
            ExamplesDiff = ExamplesDiff.Get(config, c1.Examples, c2.Examples),
            LinksDiff = LinksDiff.Get(config, c1.Links, c2.Links),
            CallbacksDiff = CallbacksDiff.Get(config, state, c1.Callbacks, c2.Callbacks),
        };
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.Opt("schemas", SchemasDiff); m.Opt("parameters", ParametersDiff); m.Opt("headers", HeadersDiff); m.Opt("requestBodies", RequestBodiesDiff);
        m.Opt("responses", ResponsesDiff); m.Opt("securitySchemes", SecuritySchemesDiff); m.Opt("examples", ExamplesDiff); m.Opt("links", LinksDiff); m.Opt("callbacks", CallbacksDiff);
        return m;
    }
}

public sealed class WebhooksDiff : MapDiff<PathDiff>
{
    public Dictionary<string, PathItem>? Base, Revision;

    internal static WebhooksDiff? Get(DiffConfig config, DiffState state, Dictionary<string, PathItem>? w1, Dictionary<string, PathItem>? w2)
    {
        if ((w1 is null || w1.Count == 0) && (w2 is null || w2.Count == 0)) return null;
        var r = Build<WebhooksDiff, PathItem>(w1, w2, (a, b) => PathDiff.Get(config, state, new PathItemPair { PathItem1 = a, PathItem2 = b }));
        if (r is not null) { r.Base = w1; r.Revision = w2; }
        return r;
    }
}

public readonly record struct Endpoint(string Method, string Path) : IOutput, IComparable<Endpoint>
{
    public int CompareTo(Endpoint other)
    {
        int c = string.CompareOrdinal(Path, other.Path);
        return c != 0 ? c : string.CompareOrdinal(Method, other.Method);
    }

    public static bool operator <(Endpoint left, Endpoint right) => left.CompareTo(right) < 0;
    public static bool operator <=(Endpoint left, Endpoint right) => left.CompareTo(right) <= 0;
    public static bool operator >(Endpoint left, Endpoint right) => left.CompareTo(right) > 0;
    public static bool operator >=(Endpoint left, Endpoint right) => left.CompareTo(right) >= 0;

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptStr("method", Method); m.OptStr("path", Path);
        return m;
    }
}

/// <summary>An OMap whose keys are complex YAML nodes (rendered as "? key : value").</summary>
public sealed class ComplexKeyMap : List<KeyValuePair<object?, object?>>;

public sealed class EndpointsDiff : IDiff
{
    public List<Endpoint> Added = [], Deleted = [];
    public SortedDictionary<Endpoint, MethodDiff> Modified = new();
    public bool Empty => Added.Count == 0 && Deleted.Count == 0 && Modified.Count == 0;
    public SummaryDetails GetSummary() => new(Added.Count, Deleted.Count, Modified.Count);

    internal static EndpointsDiff? Get(DiffConfig config, DiffState state, Paths paths1, Paths paths2)
    {
        if (config.IsExcludeEndpoints) return null;
        PathUtil.FilterPaths(PathUtil.GetFilters(config, state), paths1, paths2);
        var r = new EndpointsDiff();
        var p1 = PathUtil.RewritePrefix(paths1.Map, config.PathStripPrefixBase, config.PathPrefixBase);
        var p2 = PathUtil.RewritePrefix(paths2.Map, config.PathStripPrefixRevision, config.PathPrefixRevision);
        var (added, deleted, common, pairs) = PathUtil.GetPathItemsDiff(config, p1, p2);
        foreach (var path in added) foreach (var m in p2.Map[path].Operations().Keys) r.Added.Add(new Endpoint(m, path));
        foreach (var path in deleted) foreach (var m in p1.Map[path].Operations().Keys) r.Deleted.Add(new Endpoint(m, path));
        foreach (var path in common)
        {
            var pd = PathDiff.Get(config, state, pairs[path]);
            if (pd?.OperationsDiff is null) continue;
            foreach (var m in pd.OperationsDiff.Added) r.Added.Add(new Endpoint(m, path));
            foreach (var m in pd.OperationsDiff.Deleted) r.Deleted.Add(new Endpoint(m, path));
            foreach (var (m, md) in pd.OperationsDiff.Modified) r.Modified[new Endpoint(m, path)] = md;
        }
        r.Added.Sort(); r.Deleted.Sort();
        return r.Empty ? null : r;
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptList("added", Added); m.OptList("deleted", Deleted);
        if (Modified.Count > 0)
        {
            var cm = new ComplexKeyMap();
            foreach (var (k, v) in Modified) cm.Add(new(k, v));
            m.Add("modified", cm);
        }
        return m;
    }
}

// =========================================================== root diff & summary

public sealed class OperationPair
{
    public required string Path, Method;
    public required Operation Base, Revision;
}

public sealed class SecurityContext
{
    public List<SecurityRequirement> Base = [], Revision = [];
    public List<OperationPair> Operations = [];
}

public sealed class Summary : IOutput
{
    public bool Diff;
    public SortedDictionary<string, SummaryDetails> Details = new(StringComparer.Ordinal);
    public SummaryDetails GetSummaryDetails(string name) => Details.TryGetValue(name, out var d) ? d : default;
    public object? ToOutput()
    {
        var m = new OMap { { "diff", Diff } };
        if (Details.Count > 0) m.Add("details", Details);
        return m;
    }
}

public sealed class DuplicateEndpointException(string method, string path1, string source1, string path2, string source2) : Exception(
    $"duplicate endpoint: {method} {path1} in {source1} and {method} {path2} in {source2}. You may add the {DiffConfig.SinceDateExtension} extension to specify order" +
    (path1 != path2 ? ", or include path parameter names in endpoint matching with --include-path-params" : ""));

public sealed class OpenApiDiff : IDiff
{
    public ExtensionsDiff? ExtensionsDiff;
    public ValueDiff? OpenAPIDiff;
    public InfoDiff? InfoDiff;
    public PathsDiff? PathsDiff;
    public WebhooksDiff? WebhooksDiff;
    public EndpointsDiff? EndpointsDiff;
    public SecurityRequirementsDiff? SecurityDiff;
    public ServersDiff? ServersDiff;
    public TagsDiff? TagsDiff;
    public ExternalDocsDiff? ExternalDocsDiff;
    public ComponentsDiff? ComponentsDiff;
    public ValueDiff? JSONSchemaDialectDiff;

    public Info? BaseInfo, RevisionInfo;
    public string BaseOpenAPI = "", RevisionOpenAPI = "";
    public OpenApiDiff ShallowClone() => (OpenApiDiff)MemberwiseClone();
    public SecurityContext? SecurityContext;

    public bool Empty => ExtensionsDiff is null && OpenAPIDiff is null && InfoDiff is null && PathsDiff is null && WebhooksDiff is null && EndpointsDiff is null &&
        SecurityDiff is null && ServersDiff is null && TagsDiff is null && ExternalDocsDiff is null && ComponentsDiff is null && JSONSchemaDialectDiff is null;

    public object? ToOutput()
    {
        var m = new OMap();
        m.Opt("extensions", ExtensionsDiff); m.Opt("openAPI", OpenAPIDiff); m.Opt("info", InfoDiff); m.Opt("paths", PathsDiff);
        m.Opt("webhooks", WebhooksDiff); m.Opt("endpoints", EndpointsDiff); m.Opt("security", SecurityDiff); m.Opt("servers", ServersDiff);
        m.Opt("tags", TagsDiff); m.Opt("externalDocs", ExternalDocsDiff); m.Opt("components", ComponentsDiff); m.Opt("jsonSchemaDialect", JSONSchemaDialectDiff);
        return m;
    }

    /// <summary>diff.Get</summary>
    public static OpenApiDiff? Get(DiffConfig config, OpenApiDoc s1, OpenApiDoc s2)
    {
        var state = new DiffState
        {
            BaseComponents = SchemaDiff.ComponentNames(s1),
            RevisionComponents = SchemaDiff.ComponentNames(s2),
        };
        var r = new OpenApiDiff
        {
            ExtensionsDiff = ExtensionsDiff.Get(config, s1.Extensions, s2.Extensions),
            OpenAPIDiff = ValueDiff.Get(s1.OpenAPI, s2.OpenAPI),
            InfoDiff = InfoDiff.Get(config, s1.Info, s2.Info),
        };
        r.PathsDiff = PathsDiff.Get(config, state, s1.Paths, s2.Paths);
        r.WebhooksDiff = WebhooksDiff.Get(config, state, s1.Webhooks, s2.Webhooks);
        r.EndpointsDiff = EndpointsDiff.Get(config, state, s1.Paths, s2.Paths);
        r.SecurityDiff = SecurityRequirementsDiff.Get(s1.Security, s2.Security);
        if (r.SecurityDiff is not null) { r.SecurityDiff.BaseOrigin = s1.Origin; r.SecurityDiff.RevisionOrigin = s2.Origin; }
        r.ServersDiff = ServersDiff.Get(config, s1.Servers, s2.Servers);
        if (!config.IsExcludeTags) r.TagsDiff = TagsDiff.Get(config, s1.Tags, s2.Tags);
        r.ExternalDocsDiff = ExternalDocsDiff.Get(config, s1.ExternalDocs, s2.ExternalDocs);
        r.ComponentsDiff = ComponentsDiff.Get(config, state, s1.Components, s2.Components);
        r.JSONSchemaDialectDiff = ValueDiff.Get(s1.JSONSchemaDialect, s2.JSONSchemaDialect);
        if (r.Empty) return null;
        r.BaseInfo = s1.Info; r.RevisionInfo = s2.Info;
        r.BaseOpenAPI = s1.OpenAPI; r.RevisionOpenAPI = s2.OpenAPI;
        r.SecurityContext = new SecurityContext { Base = s1.Security, Revision = s2.Security, Operations = OperationPairs(config, s1.Paths, s2.Paths) };
        return r;
    }

    private static List<OperationPair> OperationPairs(DiffConfig config, Paths paths1, Paths paths2)
    {
        var p1 = PathUtil.RewritePrefix(paths1.Map, config.PathStripPrefixBase, config.PathPrefixBase);
        var p2 = PathUtil.RewritePrefix(paths2.Map, config.PathStripPrefixRevision, config.PathPrefixRevision);
        var (_, _, _, pairs) = PathUtil.GetPathItemsDiff(config, p1, p2);
        var r = new List<OperationPair>();
        foreach (var (path, pair) in pairs)
            foreach (var method in PathUtil.MethodsToCompare(pair.PathItem1, pair.PathItem2))
            {
                var o1 = pair.PathItem1.GetOperation(method); var o2 = pair.PathItem2.GetOperation(method);
                if (o1 is null || o2 is null) continue;
                r.Add(new OperationPair { Path = path, Method = method, Base = o1, Revision = o2 });
            }
        r.Sort((a, b) => { int c = string.CompareOrdinal(a.Path, b.Path); return c != 0 ? c : string.CompareOrdinal(a.Method, b.Method); });
        return r;
    }

    /// <summary>Sources: operation → source url.</summary>
    public static Dictionary<Operation, string> OperationSources(OpenApiDoc spec, string url)
    {
        var r = new Dictionary<Operation, string>(ReferenceEqualityComparer.Instance);
        foreach (var item in spec.Paths.Map.Values) foreach (var op in item.Operations().Values) r[op] = url;
        return r;
    }

    /// <summary>diff.GetPathsDiff for composed mode.</summary>
    public static (OpenApiDiff? diff, Dictionary<Operation, string> sources) GetPathsDiff(DiffConfig config, List<(OpenApiDoc spec, string url)> s1, List<(OpenApiDoc spec, string url)> s2)
    {
        var state = new DiffState();
        var (paths1, src1) = MergedPaths(s1, config.IncludePathParams);
        var (paths2, src2) = MergedPaths(s2, config.IncludePathParams);
        var r = new OpenApiDiff
        {
            PathsDiff = PathsDiff.Get(config, state, paths1, paths2),
            EndpointsDiff = EndpointsDiff.Get(config, state, paths1, paths2),
        };
        foreach (var (k, v) in src2) src1[k] = v;
        return (r.Empty ? null : r, src1);
    }

    private static string PathKey(string path, bool includePathParams) => includePathParams ? path : PathUtil.NormalizeTemplatedPath(path).normalized;

    private static (Paths, Dictionary<Operation, string>) MergedPaths(List<(OpenApiDoc spec, string url)> specs, bool includePathParams)
    {
        var result = new Paths();
        var byKey = new Dictionary<string, PathItem>(StringComparer.Ordinal);
        var sources = new Dictionary<Operation, string>(ReferenceEqualityComparer.Instance);
        var opPaths = new Dictionary<Operation, string>(ReferenceEqualityComparer.Instance);
        foreach (var (spec, url) in specs)
        {
            foreach (var path in Values.SortedKeys(spec.Paths.Map))
            {
                var item = spec.Paths.Map[path];
                var key = PathKey(path, includePathParams);
                if (!byKey.TryGetValue(key, out var p))
                {
                    p = item.ShallowClone();
                    result.Map[path] = p;
                    byKey[key] = p;
                    foreach (var op in item.Operations().Values) { sources[op] = url; opPaths[op] = path; }
                    continue;
                }
                var ops = item.Operations();
                foreach (var method in Values.SortedKeys(ops))
                {
                    var op = ops[method];
                    var old = p.GetOperation(method);
                    if (old is null) { p.SetOperation(method, op); sources[op] = url; opPaths[op] = path; continue; }
                    var oldSince = SinceDate(p, old, sources.GetValueOrDefault(old, ""), method, opPaths.GetValueOrDefault(old, ""));
                    var newSince = SinceDate(item, op, url, method, path);
                    if (newSince > oldSince) { p.SetOperation(method, op); sources[op] = url; opPaths[op] = path; }
                    if (newSince == oldSince) throw new DuplicateEndpointException(method, opPaths.GetValueOrDefault(old, ""), sources.GetValueOrDefault(old, ""), path, url);
                }
            }
        }
        return (result, sources);
    }

    private static readonly DateOnly DefaultSinceDate = new(2000, 1, 1);

    private static DateOnly SinceDate(PathItem item, Operation op, string source, string method, string path)
    {
        var since = DefaultSinceDate;
        if (ParseSince(item.Extensions, source, method, path) is { } s) since = s;
        if (ParseSince(op.Extensions, source, method, path) is { } os) since = os;
        return since;
    }

    private static DateOnly? ParseSince(Dictionary<string, object?>? ext, string source, string method, string path)
    {
        if (ext is null || !ext.TryGetValue(DiffConfig.SinceDateExtension, out var v)) return null;
        if (v is not string s)
            throw new DiffException($"invalid {DiffConfig.SinceDateExtension} extension value in {source} ({method} {path}), {Values.GoV(v)} is not a date in YYYY-MM-DD format");
        if (!DateOnly.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
            throw new DiffException($"invalid {DiffConfig.SinceDateExtension} extension value in {source} ({method} {path}), \"{s}\" is not a date in YYYY-MM-DD format");
        return d;
    }

    public Summary GetSummary()
    {
        var s = new Summary();
        if (Empty) return s;
        s.Diff = true;
        void Add(string name, SummaryDetails? d) { if (d is { } x) s.Details[name] = x; }
        Add("paths", PathsDiff?.GetSummary());
        Add("webhooks", WebhooksDiff?.GetSummary());
        Add("security", SecurityDiff?.GetSummary());
        Add("servers", ServersDiff?.GetSummary());
        Add("tags", TagsDiff?.GetSummary());
        if (ComponentsDiff is { } c)
        {
            Add("schemas", c.SchemasDiff is null ? null : new SummaryDetails(c.SchemasDiff.Added.Count, c.SchemasDiff.Deleted.Count, c.SchemasDiff.Modified.Count));
            Add("parameters", c.ParametersDiff?.GetSummary());
            Add("headers", c.HeadersDiff?.GetSummary());
            Add("requestBodies", c.RequestBodiesDiff?.GetSummary());
            Add("responses", c.ResponsesDiff?.GetSummary());
            Add("securitySchemes", c.SecuritySchemesDiff?.GetSummary());
            Add("examples", c.ExamplesDiff?.GetSummary());
            Add("links", c.LinksDiff?.GetSummary());
            Add("callbacks", c.CallbacksDiff?.GetSummary());
        }
        Add("endpoints", EndpointsDiff?.GetSummary());
        return s;
    }
}
