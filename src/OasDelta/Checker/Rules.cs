using System.Text.RegularExpressions;
using OasDelta.Diff;
using OasDelta.Model;

namespace OasDelta.Checker;

public static partial class Rules
{
    private static List<Rule>? _all;
    private static Dictionary<string, Rule>? _byId;
    private static HashSet<string>? _handWritten;

    public static List<Rule> All()
    {
        if (_all is not null) return _all;
        var hw = HandWritten();
        _handWritten = new HashSet<string>(hw.Select(r => r.Id), StringComparer.Ordinal);
        _all = [.. hw, .. Bound.GenerateRules(_handWritten)];
        return _all;
    }

    internal static HashSet<string> HandWrittenIds { get { All(); return _handWritten!; } }

    public static Dictionary<string, Rule> ById()
    {
        if (_byId is not null) return _byId;
        var d = new Dictionary<string, Rule>(StringComparer.Ordinal);
        foreach (var r in All()) d[r.Id] = r;
        return _byId = d;
    }

    public static Dictionary<string, Level> CheckLevels()
    {
        var d = new Dictionary<string, Level>(StringComparer.Ordinal);
        foreach (var r in All()) d[r.Id] = r.Level;
        return d;
    }

    public static List<CheckFn> AllChecks()
    {
        var seen = new HashSet<CheckFn>();
        var r = new List<CheckFn>();
        foreach (var rule in All()) if (rule.Handler is not null && seen.Add(rule.Handler)) r.Add(rule.Handler);
        return r;
    }

    public static List<string> AllIds() => All().Select(r => r.Id).ToList();

    /// <summary>GetSeverityLevels from a "id level" per line file.</summary>
    public static Dictionary<string, Level> ParseSeverityLevels(string text)
    {
        var valid = new HashSet<string>(AllIds(), StringComparer.Ordinal);
        var result = new Dictionary<string, Level>(StringComparer.Ordinal);
        // bufio.Scanner: a trailing newline does not yield an empty last line; "\r\n" endings are trimmed
        var lines = text.Split('\n');
        int count = text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        for (int n = 1; n <= count; n++)
        {
            var l = lines[n - 1];
            if (l.EndsWith('\r')) l = l[..^1];
            var frags = l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (frags.Length != 2) throw new FormatException($"invalid line #{n}: {l}");
            if (!valid.Contains(frags[0])) throw new FormatException($"invalid rule id {Fmt.GoQuote(frags[0])} on line {n}");
            var level = EnumText.ParseLevel(frags[1]);
            if (level == Level.INVALID) throw new FormatException($"invalid level {Fmt.GoQuote(frags[1])} on line {n}");
            result[frags[0]] = level;
        }
        return result;
    }
}

// ====================================================================== bound rules (bound_rules.go)

internal static class Bound
{
    private enum Polarity { Lower, Upper, Unordered }
    private sealed record Spec(string IdName, string Keyword, Polarity Polarity);
    private sealed record Action(string Verb, string Claim, string Comment);

    private static readonly Spec[] Specs =
    [
        new("max", "maximum", Polarity.Upper), new("min", "minimum", Polarity.Lower), new("multiple-of", "multipleOf", Polarity.Unordered),
        new("max-length", "maxLength", Polarity.Upper), new("min-length", "minLength", Polarity.Lower),
        new("max-items", "maxItems", Polarity.Upper), new("min-items", "minItems", Polarity.Lower),
        new("max-properties", "maxProperties", Polarity.Upper), new("min-properties", "minProperties", Polarity.Lower),
        new("min-contains", "minContains", Polarity.Lower), new("max-contains", "maxContains", Polarity.Upper),
        new("exclusive-min", "exclusiveMinimum", Polarity.Lower), new("exclusive-max", "exclusiveMaximum", Polarity.Upper),
    ];

    public const string SetComment = "bound-set-comment";
    private static readonly Action Set = new("set", "set", SetComment), Unset = new("unset", "unset", ""), Increased = new("increased", "increase", ""), Decreased = new("decreased", "decrease", "");
    private static readonly Action[] Actions = [Set, Unset, Increased, Decreased];

    private static (Effect, bool) EffectOf(Polarity p, Action a)
    {
        if (a == Set) return (Effect.Narrows, true);
        if (a == Unset) return (Effect.Widens, true);
        if (p == Polarity.Unordered) return (Effect.None, false);
        return (a == Increased) == (p == Polarity.Lower) ? (Effect.Narrows, true) : (Effect.Widens, true);
    }

    private static string RuleId(Direction d, string scope, string idName, string verb) => (d == Direction.Response ? "response" : "request") + "-" + scope + "-" + idName + "-" + verb;
    private static string[] Scopes(Direction d) => d == Direction.Response ? ["body", "property", "header"] : ["body", "property", "parameter"];

    private static string Location(Direction d, string scope, string keyword) => scope switch
    {
        "parameter" => "paths.*.*.parameters.*.schema." + keyword,
        "header" => "paths.*.*.responses.*.headers.*.schema." + keyword,
        _ => d == Direction.Response ? "paths.*.*.responses.*.content.*.schema." + keyword : "paths.*.*.requestBody.content.*.schema." + keyword,
    };

    public static List<Rule> GenerateRules(HashSet<string> handWritten)
    {
        var r = new List<Rule>();
        foreach (var spec in Specs)
            foreach (var dir in new[] { Direction.Request, Direction.Response })
                foreach (var scope in Scopes(dir))
                    foreach (var a in Actions)
                    {
                        var (eff, ok) = EffectOf(spec.Polarity, a);
                        if (!ok) continue;
                        var id = RuleId(dir, scope, spec.IdName, a.Verb);
                        if (handWritten.Contains(id)) continue;
                        r.Add(new Rule(id, Derive.LevelOf(eff, dir, []), Check, dir, Area.Schema, Kind.Constraints, eff, [], [Location(dir, scope, spec.Keyword) + ":" + a.Claim]) { Generated = true });
                    }
        return r;
    }

    public static readonly SchemaBound MaximumBound = SchemaBounds.ByKeyword("maximum"), MinimumBound = SchemaBounds.ByKeyword("minimum"),
        ExclusiveMaximumBound = SchemaBounds.ByKeyword("exclusiveMaximum"), ExclusiveMinimumBound = SchemaBounds.ByKeyword("exclusiveMinimum");

    private static (Action?, List<object?>) Classify(Spec spec, SchemaDiff d)
    {
        var bound = SchemaBounds.ByKeyword(spec.Keyword);
        if (bound.WasSet(d) is (true, var v1)) return (Set, [v1]);
        if (bound.WasUnset(d) is (true, var v2)) return (Unset, [v2]);
        if (spec.Polarity == Polarity.Unordered) return (null, []);
        if (bound.WasIncreased(d) is (true, var f1, var t1)) return (Increased, [f1, t1]);
        if (bound.WasDecreased(d) is (true, var f2, var t2)) return (Decreased, [f2, t2]);
        return (null, []);
    }

    public static List<Change> Check(OpenApiDiff d, Sources sources, CheckerConfig config)
    {
        var result = new List<Change>();
        Walkers.ModifiedRequestBodySchemas(d, sources, config, info => result.AddRange(Changes(info, Direction.Request, sources)));
        Walkers.ModifiedResponseSchemas(d, sources, config, info => result.AddRange(Changes(info, Direction.Response, sources)));
        Walkers.ModifiedParameters(d, sources, config, p => result.AddRange(SchemaChanges(p.ParamDiff.SchemaDiff, Direction.Request, "parameter", sources, p.OpInfo.MethodDiff,
            values => [p.Location, p.Name, .. values], p.OpInfo.NewApiChange)));
        Walkers.ModifiedResponseHeaders(d, sources, config, h => result.AddRange(SchemaChanges(h.HeaderDiff.SchemaDiff, Direction.Response, "header", sources, h.OpInfo.MethodDiff,
            values => [h.Name, .. values, h.ResponseStatus], h.OpInfo.NewApiChange)));
        return result;
    }

    private static List<Change> Changes(MediaTypeInfo info, Direction dir, Sources sources)
    {
        var result = SchemaChanges(info.SchemaDiff, dir, "body", sources, info.OperationItem, v => v, info.NewChange);
        info.WalkProperties(p => result.AddRange(SchemaChanges(p.PropertyDiff, dir, "property", sources, info.OperationItem,
            values =>
            {
                List<object?> args = [p.FullName, .. values];
                if (dir == Direction.Response) args.Add(info.ResponseStatus);
                return args;
            }, p.NewChange)));
        return result;
    }

    private static List<Change> SchemaChanges(SchemaDiff? d, Direction dir, string scope, Sources sources, MethodDiff md,
        Func<List<object?>, List<object?>> args, Func<string, List<object?>, string, Change> newChange)
    {
        var result = new List<Change>();
        if (d is null) return result;
        foreach (var spec in Specs)
        {
            var (action, values) = Classify(spec, d);
            if (action is null) continue;
            var id = RuleId(dir, scope, spec.IdName, action.Verb);
            if (Rules.HandWrittenIds.Contains(id)) continue;
            var (eff, _) = EffectOf(spec.Polarity, action);
            var (b, r) = Loc.SchemaField(sources, md, d, spec.Keyword);
            var comment = Derive.LevelOf(eff, dir, []) == Level.ERR ? action.Comment : "";
            result.Add(newChange(id, args(values), comment).WithSources(b, r));
        }
        return result;
    }
}

/// <summary>diff.SchemaBounds</summary>
public sealed class SchemaBound(string keyword, Func<SchemaDiff, ValueDiff?> get, int encoding)
{
    public readonly string Keyword = keyword;
    private readonly Func<SchemaDiff, ValueDiff?> _get = get;
    private readonly int _enc = encoding; // 0 nil-absent, 1 zero-absent, 2 false-absent

    private bool Absent(object? v) => _enc switch
    {
        1 => v is null || v is ulong u && u == 0,
        2 => v is null || v is false,
        _ => v is null,
    };

    public (bool, object?) WasSet(SchemaDiff d)
    {
        var vd = _get(d);
        if (vd is null || !Absent(vd.From) || Absent(vd.To)) return (false, null);
        return (true, vd.To);
    }

    public (bool, object?) WasUnset(SchemaDiff d)
    {
        var vd = _get(d);
        if (vd is null || Absent(vd.From) || !Absent(vd.To)) return (false, null);
        return (true, vd.From);
    }

    public (bool, object?, object?) WasIncreased(SchemaDiff d) => Ordered(d, false);
    public (bool, object?, object?) WasDecreased(SchemaDiff d) => Ordered(d, true);

    private (bool, object?, object?) Ordered(SchemaDiff d, bool reverse)
    {
        var vd = _get(d);
        if (vd is null || Absent(vd.From) || Absent(vd.To)) return (false, null, null);
        bool less = reverse ? Less(vd.To, vd.From) : Less(vd.From, vd.To);
        return less ? (true, vd.From, vd.To) : (false, null, null);
    }

    private static bool Less(object? a, object? b) => (a, b) switch { (ulong x, ulong y) => x < y, (double x, double y) => x < y, _ => false };
}

public static class SchemaBounds
{
    public static readonly SchemaBound[] All =
    [
        new("maximum", d => d.MaxDiff, 0), new("minimum", d => d.MinDiff, 0), new("multipleOf", d => d.MultipleOfDiff, 0),
        new("maxLength", d => d.MaxLengthDiff, 0), new("minLength", d => d.MinLengthDiff, 1), new("maxItems", d => d.MaxItemsDiff, 0),
        new("minItems", d => d.MinItemsDiff, 1), new("maxProperties", d => d.MaxPropsDiff, 0), new("minProperties", d => d.MinPropsDiff, 1),
        new("minContains", d => d.MinContainsDiff, 0), new("maxContains", d => d.MaxContainsDiff, 0),
        new("exclusiveMinimum", d => d.ExclusiveMinDiff, 2), new("exclusiveMaximum", d => d.ExclusiveMaxDiff, 2),
    ];

    public static SchemaBound ByKeyword(string k) => All.First(b => b.Keyword == k);
}

// ====================================================================== pipeline (checker.go and friends)

public static class BackwardCompatibility
{
    public static List<Change> Check(CheckerConfig config, OpenApiDiff? diff, Sources sources, Level minLevel = Level.WARN)
    {
        var result = new List<Change>();
        if (diff is null) return result;
        diff = CloneForCheck(diff);
        result = StabilityPolicy.Apply(config, diff, result, sources);
        MergeWebhooks(diff);
        foreach (var check in config.Checks) result.AddRange(check(diff, sources, config));
        result = Transitions.DropClaimed(result);
        result = AttachSharedSchemas(result);
        result = CapByGuards(config, result);
        result = CapByDisclaimers(config, result);
        result = VersioningPolicy.Apply(config, diff, result);
        var filtered = result.Where(c => c.Level >= minLevel).ToList();
        filtered.Sort(Change.Compare);
        // Go's slices.SortFunc is not stable; equal elements are rare and compare equal on all printed fields
        return filtered;
    }

    private static void MergeWebhooks(OpenApiDiff d)
    {
        if (d.WebhooksDiff is null || d.WebhooksDiff.Modified.Count == 0) return;
        d.PathsDiff ??= new PathsDiff();
        foreach (var (name, pd) in d.WebhooksDiff.Modified) d.PathsDiff.Modified["webhook:" + name] = pd;
    }

    private static OpenApiDiff CloneForCheck(OpenApiDiff d)
    {
        var c = d.ShallowClone();
        if (d.SecurityContext is not null)
            c.SecurityContext = new SecurityContext { Base = d.SecurityContext.Base, Revision = d.SecurityContext.Revision, Operations = [.. d.SecurityContext.Operations] };
        if (d.PathsDiff is not null)
        {
            var p = new PathsDiff { Added = d.PathsDiff.Added, Deleted = [.. d.PathsDiff.Deleted], Base = d.PathsDiff.Base, Revision = d.PathsDiff.Revision };
            foreach (var (path, pd) in d.PathsDiff.Modified)
            {
                var pc = new PathDiff
                {
                    ExtensionsDiff = pd.ExtensionsDiff, RefDiff = pd.RefDiff, SummaryDiff = pd.SummaryDiff, DescriptionDiff = pd.DescriptionDiff,
                    ServersDiff = pd.ServersDiff, ParametersDiff = pd.ParametersDiff, Base = pd.Base, Revision = pd.Revision,
                };
                if (pd.OperationsDiff is not null)
                {
                    pc.OperationsDiff = new OperationsDiff { Added = pd.OperationsDiff.Added, Deleted = [.. pd.OperationsDiff.Deleted] };
                    foreach (var (m, md) in pd.OperationsDiff.Modified) pc.OperationsDiff.Modified[m] = md;
                }
                p.Modified[path] = pc;
            }
            c.PathsDiff = p;
        }
        return c;
    }

    private static List<Change> AttachSharedSchemas(List<Change> changes)
    {
        var refs = new Dictionary<SchemaDiff, References>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            if (c.Kind != ChangeSection.Api || c.Root is null || c.PropertyPath == "") continue;
            if (!refs.TryGetValue(c.Root, out var r)) refs[c.Root] = r = new References(c.Root);
            if (r.At(c.PropertyPath, 3) is not { } shared) continue;
            changes[i] = c.WithShared(new SharedSchema { Name = shared.name, Properties = shared.paths, Count = shared.count, Cyclic = shared.cyclic });
        }
        return changes;
    }

    private static List<Change> CapByGuards(CheckerConfig config, List<Change> changes)
    {
        for (int i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            if (c.Kind != ChangeSection.Api || c.Guards.Count == 0) continue;
            c = c.Clone();
            if (!Rules.ById().TryGetValue(c.Id, out var rule) || config.OverriddenLevels.Contains(c.Id)) { c.Guards = []; changes[i] = c; continue; }
            var baseLevel = rule.DerivedLevel;
            var level = baseLevel;
            var applied = new List<Guard>();
            foreach (var g in c.Guards)
            {
                var derived = Derive.LevelOf(rule.Effect, rule.Direction, [.. rule.Guards, g]);
                if (derived < baseLevel) { applied.Add(g); if (derived < level) level = derived; }
            }
            c.Guards = applied;
            if (level < c.Level) { c.Level = level; c.Comment = ""; }
            changes[i] = c;
        }
        return changes;
    }

    private static List<Change> CapByDisclaimers(CheckerConfig config, List<Change> changes)
    {
        for (int i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            if (c.Kind != ChangeSection.Api || config.OverriddenLevels.Contains(c.Id)) continue;
            foreach (var d in c.Disclaimers)
                if (d == Disclaimer.AllOfNotFlattened && c.Level > Level.WARN) { c = c.Clone(); c.Level = Level.WARN; }
            changes[i] = c;
        }
        return changes;
    }

    /// <summary>ProcessIgnoredBackwardCompatibilityErrors</summary>
    public static List<Change> ProcessIgnored(Level level, List<Change> errs, string ignoreFile, Localizer l)
    {
        var ignored = new bool[errs.Count];
        foreach (var raw in File.ReadAllLines(ignoreFile))
        {
            var line = raw.ToLowerInvariant();
            var path = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(c => c.StartsWith('/')) ?? "";
            for (int i = 0; i < errs.Count; i++)
                if (errs[i].Level == level && errs[i].MatchIgnore(path, line, l)) ignored[i] = true;
        }
        return errs.Where((_, i) => !ignored[i]).ToList();
    }
}

internal static class Transitions
{
    private sealed record T(string Name, Func<SchemaDiff, bool> Present, HashSet<Kind> Claims, HashSet<string> ReportedBy);

    private static readonly HashSet<Kind> AllButLifecycle = [Kind.Type, Kind.Values, Kind.Constraints, Kind.Structure, Kind.Existence, Kind.Requiredness];

    private static readonly T[] List =
    [
        new("nullable-wrapping", d => d.NullableWrappingDiff is not null, AllButLifecycle, new(StringComparer.Ordinal)
        {
            Ids.RequestBodyBecomeNullable, Ids.RequestPropertyBecomeNullable, Ids.RequestBodyBecomeNotNullable, Ids.RequestPropertyBecomeNotNullable,
            Ids.ResponseBodyBecameNullable, Ids.ResponsePropertyBecameNullable, Ids.ResponseBodyBecameNotNullable, Ids.ResponsePropertyBecameNotNullable,
            Ids.RequestParameterBecameNullable, Ids.RequestParameterBecameNotNullable, Ids.RequestParameterPropertyBecameNullable, Ids.RequestParameterPropertyBecameNotNullable,
        }),
        new("boolean-false-schema", d => d.AlwaysDiff is not null && (d.AlwaysDiff.From is false || d.AlwaysDiff.To is false), AllButLifecycle, new(StringComparer.Ordinal)
        {
            Ids.RequestBodySchemaBecameFalse, Ids.RequestBodySchemaBecameNotFalse, Ids.RequestPropertySchemaBecameFalse, Ids.RequestPropertySchemaBecameNotFalse,
            Ids.ResponseBodySchemaBecameFalse, Ids.ResponseBodySchemaBecameNotFalse, Ids.ResponsePropertySchemaBecameFalse, Ids.ResponsePropertySchemaBecameNotFalse,
            Ids.RequestParameterSchemaBecameFalse, Ids.RequestParameterSchemaBecameNotFalse, Ids.RequestParameterPropertySchemaBecameFalse, Ids.RequestParameterPropertySchemaBecameNotFalse,
            Ids.ResponseHeaderSchemaBecameFalse, Ids.ResponseHeaderSchemaBecameNotFalse,
        }),
        new("oneof-wrapping", d => d.OneOfWrappingDiff is not null, [Kind.Type, Kind.Structure], new(StringComparer.Ordinal)
        {
            Ids.RequestBodyWrappedInOneOf, Ids.ResponseBodyWrappedInOneOf, Ids.RequestBodyWrappedInOneOfOriginalPreserved, Ids.ResponseBodyWrappedInOneOfOriginalPreserved,
            Ids.RequestPropertyOneOfAdded, Ids.ResponsePropertyOneOfAdded,
        }),
        new("list-of-types", d => d.ListOfTypesDiff is not null, [Kind.Type, Kind.Structure], new(StringComparer.Ordinal)
        {
            Ids.RequestBodyListOfTypesWidened, Ids.RequestBodyListOfTypesNarrowed, Ids.RequestPropertyListOfTypesWidened, Ids.RequestPropertyListOfTypesNarrowed,
            Ids.ResponseBodyListOfTypesWidened, Ids.ResponseBodyListOfTypesNarrowed, Ids.ResponsePropertyListOfTypesWidened, Ids.ResponsePropertyListOfTypesNarrowed,
            Ids.RequestParameterListOfTypesWidened, Ids.RequestParameterListOfTypesNarrowed, Ids.RequestParameterPropertyListOfTypesWidened, Ids.RequestParameterPropertyListOfTypesNarrowed,
        }),
        new("null-only-type-change", d => Walkers.IsNullTypeChange(d.TypeDiff) && d.FormatDiff is null, [Kind.Type], new(StringComparer.Ordinal)
        {
            Ids.RequestBodyBecomeNullable, Ids.RequestBodyBecomeNotNullable, Ids.RequestPropertyBecomeNullable, Ids.RequestPropertyBecomeNotNullable,
            Ids.ResponseBodyBecameNullable, Ids.ResponsePropertyBecameNullable, Ids.ResponseBodyBecameNotNullable, Ids.ResponsePropertyBecameNotNullable,
            Ids.RequestParameterBecameNullable, Ids.RequestParameterBecameNotNullable, Ids.RequestParameterPropertyBecameNullable, Ids.RequestParameterPropertyBecameNotNullable,
        }),
    ];

    public static List<Change> DropClaimed(List<Change> changes) => changes.Where(c => !(c.Kind == ChangeSection.Api && Claimed(c.Schema, c.Id))).ToList();

    private static bool Claimed(SchemaDiff? d, string id)
    {
        if (d is null || !Rules.ById().TryGetValue(id, out var rule)) return false;
        foreach (var t in List)
            if (t.Claims.Contains(rule.Kind) && t.Present(d) && !t.ReportedBy.Contains(id)) return true;
        return false;
    }
}

internal static partial class VersioningPolicy
{
    [GeneratedRegex(@"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$")]
    private static partial Regex Semver();

    private static (int, int, int)? Parse(string v)
    {
        var m = Semver().Match(v);
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[1].Value, out var a) || !int.TryParse(m.Groups[2].Value, out var b) || !int.TryParse(m.Groups[3].Value, out var c)) return null;
        return (a, b, c);
    }

    public static List<Change> Apply(CheckerConfig config, OpenApiDiff d, List<Change> result)
    {
        if (d.BaseInfo is null || d.RevisionInfo is null) return result;
        var bv = d.BaseInfo.Version; var rv = d.RevisionInfo.Version;
        if (Parse(bv) is not { } b || Parse(rv) is not { } r) return result;
        if (!result.Any(c => c.Level >= Level.ERR)) return result;
        string id; List<object?> args;
        if (bv == rv) { id = Ids.APIVersionNotBumped; args = [rv]; }
        else if (!Increased(b, r)) { id = Ids.APIVersionDecreased; args = [bv, rv]; }
        else if (!MajorBumped(b, r)) { id = Ids.APIMajorVersionNotBumped; args = [bv, rv]; }
        else return result;
        result.Add(new Change { Kind = ChangeSection.Info, Id = id, Level = config.LevelOf(id), Args = args });
        return result;
    }

    private static bool Increased((int, int, int) b, (int, int, int) r) =>
        r.Item1 != b.Item1 ? r.Item1 > b.Item1 : r.Item2 != b.Item2 ? r.Item2 > b.Item2 : r.Item3 > b.Item3;

    private static bool MajorBumped((int, int, int) b, (int, int, int) r) =>
        b.Item1 == 0 && r.Item1 == 0 ? r.Item2 > b.Item2 : r.Item1 > b.Item1;
}

internal static class StabilityPolicy
{
    public static List<Change> Apply(CheckerConfig config, OpenApiDiff d, List<Change> result, Sources sources)
    {
        if (d.SecurityContext is not null) d.SecurityContext.Operations.RemoveAll(p => Below(config, p.Revision));
        if (d.PathsDiff is null) return result;
        result.AddRange(InvalidLevels(config, d, sources));
        result.AddRange(LevelChanged(config, d, sources));
        FilterBelow(config, d);
        result.AddRange(Checks.RequestPropertyStabilityUpdatedCheck(d, sources, config));
        result.AddRange(Checks.ResponsePropertyStabilityUpdatedCheck(d, sources, config));
        return result;
    }

    internal static Change Invalid(CheckerConfig config, Operation op, Sources sources, string method, string path, string err) =>
        Walkers.NewApiChange(Ids.APIInvalidStabilityLevel, config, [new GoError(err)], "", sources, op, method, path);

    private static List<Change> InvalidLevels(CheckerConfig config, OpenApiDiff d, Sources sources)
    {
        var r = new List<Change>();
        foreach (var (path, pd) in d.PathsDiff!.Modified)
        {
            if (pd.OperationsDiff is null) continue;
            foreach (var (method, md) in pd.OperationsDiff.Modified)
            {
                if (Stability.Get(pd.Base!.GetOperation(method)!.Extensions).error is { } e1)
                    r.Add(Invalid(config, md.Base!, sources, method, path, e1).WithSources(Loc.StabilityField(sources, md.Base, md.Base!.Origin), null));
                if (Stability.Get(pd.Revision!.GetOperation(method)!.Extensions).error is { } e2)
                    r.Add(Invalid(config, md.Revision!, sources, method, path, e2).WithSources(null, Loc.StabilityField(sources, md.Revision, md.Revision!.Origin)));
            }
            foreach (var method in pd.OperationsDiff.Deleted)
            {
                var op = pd.Base!.GetOperation(method)!;
                if (Stability.Get(op.Extensions).error is { } e)
                    r.Add(Invalid(config, op, sources, method, path, e).WithSources(Loc.StabilityField(sources, op, op.Origin), null));
            }
        }
        foreach (var path in d.PathsDiff.Deleted)
        {
            var item = d.PathsDiff.Base!.Value(path)!;
            foreach (var (method, op) in item.Operations())
                if (Stability.Get(op.Extensions).error is { } e)
                    r.Add(Invalid(config, op, sources, method, path, e).WithSources(Loc.StabilityField(sources, op, op.Origin), null));
        }
        return r;
    }

    private static List<Change> LevelChanged(CheckerConfig config, OpenApiDiff d, Sources sources)
    {
        var r = new List<Change>();
        foreach (var (path, pd) in d.PathsDiff!.Modified)
        {
            if (pd.OperationsDiff is null) continue;
            foreach (var (method, md) in pd.OperationsDiff.Modified)
            {
                var (bs, e1) = Stability.Get(pd.Base!.GetOperation(method)!.Extensions);
                if (e1 is not null) continue;
                var (rs, e2) = Stability.Get(pd.Revision!.GetOperation(method)!.Extensions);
                if (e2 is not null) continue;
                var bl = Stability.Normalized(bs); var rl = Stability.Normalized(rs);
                var blv = Stability.Parse(bl); var rlv = Stability.Parse(rl);
                if (blv == rlv) continue;
                if (!config.StabilityLevel.IsIncluded(bl)) continue;
                var id = rlv < blv ? Ids.APIStabilityDecreased : Ids.APIStabilityIncreased;
                var bop = pd.Base!.GetOperation(method)!;
                r.Add(OpInfo.FromDiff(config, md, sources, method, path).NewApiChange(id, [bl, rl], "")
                    .WithSources(Loc.StabilityField(sources, bop, bop.Origin), Loc.StabilityField(sources, md.Revision, md.Revision!.Origin)));
            }
        }
        return r;
    }

    private static bool Below(CheckerConfig config, Operation op)
    {
        var (s, e) = Stability.Get(op.Extensions);
        return e is null && !config.StabilityLevel.IsIncluded(s);
    }

    private static void FilterBelow(CheckerConfig config, OpenApiDiff d)
    {
        var p = d.PathsDiff!;
        p.Deleted = p.Deleted.Where(path =>
        {
            var item = p.Base!.Value(path)!;
            foreach (var op in item.Operations().Values)
            {
                var (s, e) = Stability.Get(op.Extensions);
                if (e is not null) continue;
                if (config.StabilityLevel.IsIncluded(s)) return true;
            }
            return false;
        }).ToList();
        foreach (var pd in p.Modified.Values)
        {
            if (pd.OperationsDiff is null) continue;
            pd.OperationsDiff.Deleted = pd.OperationsDiff.Deleted.Where(m =>
            {
                var (s, e) = Stability.Get(pd.Base!.GetOperation(m)!.Extensions);
                return e is null && config.StabilityLevel.IsIncluded(s);
            }).ToList();
            foreach (var m in pd.OperationsDiff.Modified.Keys.ToList())
                if (Below(config, pd.Revision!.GetOperation(m)!)) pd.OperationsDiff.Modified.Remove(m);
        }
    }
}
