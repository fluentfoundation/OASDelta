using System.Globalization;
using OasDelta.Diff;
using OasDelta.Model;
using OasDelta.Yaml;

namespace OasDelta.Checker;

public sealed class OpInfo
{
    public required CheckerConfig Config;
    public required Operation Operation;
    public required Sources Sources;
    public required string Method, Path;
    public MethodDiff MethodDiff = null!;

    public Change NewApiChange(string id, List<object?> args, string comment) => Walkers.NewApiChange(id, Config, args, comment, Sources, Operation, Method, Path);

    public static OpInfo FromDiff(CheckerConfig config, MethodDiff md, Sources sources, string method, string path) =>
        new() { Config = config, Operation = md.Revision!, Sources = sources, Method = method, Path = path, MethodDiff = md };

    public static OpInfo Of(CheckerConfig config, Operation op, Sources sources, string method, string path) =>
        new() { Config = config, Operation = op, Sources = sources, Method = method, Path = path };
}

public class MediaTypeInfo
{
    public string Path = "", Method = "", ResponseStatus = "";
    public MethodDiff OperationItem = null!;
    public string MediaType = "", MediaTypeDetails = "";
    public SchemaDiff SchemaDiff = null!;
    public CheckerConfig Config = null!;
    public Sources Sources = null!;

    public MediaTypeInfo Copy() => (MediaTypeInfo)MemberwiseClone();

    public Change NewChange(string id, List<object?> args, string comment) =>
        Walkers.NewApiChange(id, Config, args, comment, Sources, OperationItem.Revision!, Method, Path)
            .WithSchema(SchemaDiff, SchemaDiff, "").WithDetails(MediaTypeDetails).WithAllOfDisclaimers(false, SchemaDiff);

    public void WalkProperties(Action<PropertyInfo> processor)
    {
        if (SchemaDiff is null) return;
        new SchemaWalker
        {
            Enter = (path, name, d, parent, underAllOf) =>
            {
                if (d?.Base is null || d.Revision is null) return;
                processor(new PropertyInfo(this) { UnderAllOf = underAllOf, PropertyPath = path, PropertyName = name, PropertyDiff = d, Parent = parent });
            },
        }.Walk(SchemaDiff);
    }
}

public sealed class PropertyInfo(MediaTypeInfo info)
{
    public readonly MediaTypeInfo Info = info;
    public string PropertyPath = "", PropertyName = "";
    public bool UnderAllOf;
    public SchemaDiff PropertyDiff = null!;
    public SchemaDiff? Parent;

    public string FullName => SchemaWalker.PropertyFullName(PropertyPath, PropertyName);

    public Change NewChange(string id, List<object?> args, string comment) =>
        Info.NewChange(id, args, comment).WithSchema(Info.SchemaDiff, PropertyDiff, FullName)
            .WithAllOfDisclaimers(UnderAllOf, PropertyDiff).WithGuards(Walkers.PropertyGuards(PropertyDiff));
}

public sealed class ParamInfo
{
    public required OpInfo OpInfo;
    public required string Location, Name;
    public required ParameterDiff ParamDiff;
}

public sealed class HeaderInfo
{
    public required OpInfo OpInfo;
    public required string ResponseStatus;
    public required ResponseDiff ResponseDiff;
    public required string Name;
    public required HeaderDiff HeaderDiff;
}

public static class Walkers
{
    public static Change NewApiChange(string id, CheckerConfig config, List<object?> args, string comment, Sources sources, Operation op, string method, string path)
    {
        Dictionary<string, object?>? attrs = null;
        foreach (var tag in config.Attributes)
            if (op.Extensions is not null && op.Extensions.TryGetValue(tag, out var v)) (attrs ??= new(StringComparer.Ordinal))[tag] = v;
        return new Change
        {
            Kind = ChangeSection.Api, Id = id, Level = config.LevelOf(id), Args = args, Comment = comment,
            OperationId = op.OperationID, Operation = method, Path = path, SourcePath = sources[op], Attributes = attrs,
        };
    }

    public static List<Guard> PropertyGuards(SchemaDiff d)
    {
        var g = new List<Guard>();
        if (d.Revision!.ReadOnly) g.Add(Guard.ReadOnly);
        if (d.Revision.WriteOnly) g.Add(Guard.WriteOnly);
        return g;
    }

    /// <summary>allOfDisclaimers (oasdiff 1.35.0): under an allOf branch, or the schema itself has an allOf. Since 1.35.0 this
    /// applies to every rule, the allOf-added and annotation-only ones included.</summary>
    public static List<Disclaimer> AllOfDisclaimers(bool underAllOf, SchemaDiff? schemaDiff)
    {
        if (schemaDiff is not null && (schemaDiff.Base is { AllOf.Count: > 0 } || schemaDiff.Revision is { AllOf.Count: > 0 })) underAllOf = true;
        return underAllOf ? [Disclaimer.AllOfNotFlattened] : [];
    }

    public static string FormatMediaTypeDetails(string mediaType, int total) => total <= 1 ? "" : "(media type: " + mediaType + ")";

    public static string CombineDetails(params string[] details)
    {
        var r = "";
        foreach (var d in details)
            if (d != "") r = r == "" ? d : r + " " + d;
        return r;
    }

    private static bool BothSides(SchemaDiff? d) => d is { Base: not null, Revision: not null };
    public const string ItemSchemaDetail = "(item schema)";

    private static void WalkMediaTypeSchemas(MediaTypeDiff md, MediaTypeInfo info, Action<MediaTypeInfo> processor)
    {
        var details = info.MediaTypeDetails;
        if (BothSides(md.SchemaDiff)) { var i = info.Copy(); i.SchemaDiff = md.SchemaDiff!; processor(i); }
        if (BothSides(md.ItemSchemaDiff)) { var i = info.Copy(); i.SchemaDiff = md.ItemSchemaDiff!; i.MediaTypeDetails = CombineDetails(details, ItemSchemaDetail); processor(i); }
    }

    public static void ModifiedRequestBodySchemas(OpenApiDiff? d, Sources sources, CheckerConfig config, Action<MediaTypeInfo> processor)
    {
        if (d?.PathsDiff is null) return;
        foreach (var (path, pathItem) in d.PathsDiff.Modified)
        {
            if (pathItem.OperationsDiff is null) continue;
            foreach (var (method, op) in pathItem.OperationsDiff.Modified)
            {
                var mts = op.RequestBodyDiff?.ContentDiff?.MediaTypeModified;
                if (mts is null) continue;
                foreach (var (mt, mtd) in mts)
                {
                    if (mtd is null) continue;
                    WalkMediaTypeSchemas(mtd, new MediaTypeInfo
                    {
                        Path = path, Method = method, OperationItem = op, MediaType = mt, MediaTypeDetails = FormatMediaTypeDetails(mt, mts.Count),
                        Config = config, Sources = sources,
                    }, processor);
                }
            }
        }
    }

    public static void ModifiedResponseSchemas(OpenApiDiff? d, Sources sources, CheckerConfig config, Action<MediaTypeInfo> processor)
    {
        if (d?.PathsDiff is null) return;
        foreach (var (path, pathItem) in d.PathsDiff.Modified)
        {
            if (pathItem.OperationsDiff is null) continue;
            foreach (var (method, op) in pathItem.OperationsDiff.Modified)
            {
                if (op.ResponsesDiff is null) continue;
                foreach (var (status, rd) in op.ResponsesDiff.Modified)
                {
                    var mts = rd.ContentDiff?.MediaTypeModified;
                    if (mts is null) continue;
                    foreach (var (mt, mtd) in mts)
                    {
                        if (mtd is null) continue;
                        WalkMediaTypeSchemas(mtd, new MediaTypeInfo
                        {
                            Path = path, Method = method, ResponseStatus = status, OperationItem = op, MediaType = mt,
                            MediaTypeDetails = FormatMediaTypeDetails(mt, mts.Count), Config = config, Sources = sources,
                        }, processor);
                    }
                }
            }
        }
    }

    public static void ModifiedParameters(OpenApiDiff d, Sources sources, CheckerConfig config, Action<ParamInfo> processor)
    {
        if (d.PathsDiff is null) return;
        foreach (var (path, pathItem) in d.PathsDiff.Modified)
        {
            if (pathItem.OperationsDiff is null) continue;
            foreach (var (method, op) in pathItem.OperationsDiff.Modified)
            {
                if (op.ParametersDiff is null) continue;
                var oi = OpInfo.FromDiff(config, op, sources, method, path);
                foreach (var (loc, ps) in op.ParametersDiff.Modified)
                    foreach (var (name, pd) in ps)
                        processor(new ParamInfo { OpInfo = oi, Location = loc, Name = name, ParamDiff = pd });
            }
        }
    }

    public static void ModifiedResponseHeaders(OpenApiDiff d, Sources sources, CheckerConfig config, Action<HeaderInfo> processor)
    {
        if (d.PathsDiff is null) return;
        foreach (var (path, pathItem) in d.PathsDiff.Modified)
        {
            if (pathItem.OperationsDiff is null) continue;
            foreach (var (method, op) in pathItem.OperationsDiff.Modified)
            {
                if (op.ResponsesDiff is null) continue;
                var oi = OpInfo.FromDiff(config, op, sources, method, path);
                foreach (var (status, rd) in op.ResponsesDiff.Modified)
                {
                    if (rd.HeadersDiff is null) continue;
                    foreach (var (name, hd) in rd.HeadersDiff.Modified)
                        processor(new HeaderInfo { OpInfo = oi, ResponseStatus = status, ResponseDiff = rd, Name = name, HeaderDiff = hd });
                }
            }
        }
    }

    /// <summary>Iterates modified operations: (path, pathDiff, method, methodDiff).</summary>
    public static IEnumerable<(string path, PathDiff pathDiff, string method, MethodDiff op)> ModifiedOperations(OpenApiDiff d)
    {
        if (d.PathsDiff is null) yield break;
        foreach (var (path, pathItem) in d.PathsDiff.Modified)
        {
            if (pathItem.OperationsDiff is null) continue;
            foreach (var (method, op) in pathItem.OperationsDiff.Modified) yield return (path, pathItem, method, op);
        }
    }

    // ------------------------------------------------------------------ utils (checks_utils.go)

    public static bool IsIncreased(object? from, object? to) => (from, to) switch
    {
        (ulong a, ulong b) => a < b,
        (double a, double b) => a < b,
        _ => false,
    };

    public static bool IsDecreased(object? from, object? to) => (from, to) switch
    {
        (ulong a, ulong b) => a > b,
        (double a, double b) => a > b,
        _ => false,
    };

    public static bool UintBoundSet(ValueDiff? d) => d is not null && d.From is ulong f && f == 0 && !(d.To is ulong t && t == 0);
    public static bool UintBoundUnset(ValueDiff? d) => d is not null && !(d.From is ulong f && f == 0) && d.To is ulong t && t == 0;
    public static bool IsIncreasedValue(ValueDiff d) => IsIncreased(d.From, d.To);
    public static bool IsDecreasedValue(ValueDiff d) => IsDecreased(d.From, d.To);

    public static (List<Subschema> kept, List<Subschema> annotationOnly) SplitSubschemasByAnnotationOnly(List<Subschema> subschemas, List<Ref<Schema>> refs)
    {
        if (subschemas.Count == 0) return (subschemas, []);
        var kept = new List<Subschema>();
        var ann = new List<Subschema>();
        var empty = new Ref<Schema>(new Schema());
        foreach (var s in subschemas)
        {
            bool isAnn = s.Index >= 0 && s.Index < refs.Count && refs[s.Index]?.Value is not null &&
                SchemaEquivalence.RefsValidationEquivalent(new DiffConfig(), empty, refs[s.Index]);
            (isAnn ? ann : kept).Add(s);
        }
        return (kept, ann);
    }

    public static bool PrefixItemsChangedContract(SchemaDiff d) =>
        d.PrefixItemsDiff is not null && !SchemaEquivalence.PrefixItemsValidationEquivalent(new DiffConfig(), d.Base, d.Revision);

    public static bool MayRestrictValues(Schema? s) => s is not null && (s.OneOf.Count > 0 || s.AnyOf.Count > 0 || s.AllOf.Count > 0 ||
        s.Not is not null || s.Const is not null || s.If is not null || s.Then is not null || s.Else is not null);

    public static bool BecameNotEnum(SchemaDiff d) => d.EnumDiff is { EnumDeleted: true } && !MayRestrictValues(d.Revision);
    public static bool BecameEnum(SchemaDiff d) => d.EnumDiff is { EnumAdded: true } && !MayRestrictValues(d.Base);
    public static List<object?>? DeletedEnumValues(SchemaDiff d) => d.EnumDiff is null || BecameNotEnum(d) ? null : d.EnumDiff.Deleted;
    public static List<object?>? AddedEnumValues(SchemaDiff d) => d.EnumDiff is null || BecameEnum(d) ? null : d.EnumDiff.Added;

    // nullable_type_utils.go
    public static bool OnlyNull(List<string> types) => types.All(t => t == "null");
    public static bool IsNullTypeChange(StringsDiff? d) => d is not null && OnlyNull(d.Added) && OnlyNull(d.Deleted);
    public static bool NullAddedToTypeArray(StringsDiff? d, List<string>? baseType) => d is not null && baseType is { Count: > 0 } && d.Added.Contains("null");
    public static bool NullRemovedFromTypeArray(StringsDiff? d, List<string>? revType) => d is not null && revType is { Count: > 0 } && d.Deleted.Contains("null");
    public static bool IsNullableWrapping(SchemaDiff? d) => d?.NullableWrappingDiff is { NullabilityAdded: true };
    public static bool IsNullableUnwrapping(SchemaDiff? d) => d?.NullableWrappingDiff is { NullabilityRemoved: true };

    public enum Nullability { Unchanged, BecameNullable, BecameNotNullable }

    public static Nullability NullabilityChange(SchemaDiff? d)
    {
        if (d?.Base is null || d.Revision is null) return Nullability.Unchanged;
        if (d.NullableDiff is not null)
        {
            if (d.NullableDiff.From is true) return Nullability.BecameNotNullable;
            if (d.NullableDiff.To is true) return Nullability.BecameNullable;
            return Nullability.Unchanged;
        }
        if (NullRemovedFromTypeArray(d.TypeDiff, d.Revision.Type)) return Nullability.BecameNotNullable;
        if (NullAddedToTypeArray(d.TypeDiff, d.Base.Type)) return Nullability.BecameNullable;
        if (IsNullableWrapping(d)) return Nullability.BecameNullable;
        if (IsNullableUnwrapping(d)) return Nullability.BecameNotNullable;
        return Nullability.Unchanged;
    }

    public static string NullabilityChangeId(SchemaDiff? d, string nullableId, string notNullableId) => NullabilityChange(d) switch
    {
        Nullability.BecameNullable => nullableId,
        Nullability.BecameNotNullable => notNullableId,
        _ => "",
    };

    // type_format.go
    public static string TypeFormatDimension(SchemaDiff d)
    {
        bool t = d.TypeDiff is not null, f = d.FormatDiff is not null;
        return t && f ? "type/format" : t ? "type" : f ? "format" : "";
    }
    public static string BaseTypeFormat(SchemaDiff d) => TypeFormatValue(d.Base, TypeFormatDimension(d));
    public static string RevisionTypeFormat(SchemaDiff d) => TypeFormatValue(d.Revision, TypeFormatDimension(d));
    public static string TypeFormatValue(Schema? s, string dim) => dim switch { "format" => FormatString(s), "type" => TypeString(s), _ => TypeFormatString(s) };

    public static string TypeString(Schema? s)
    {
        if (s is null) return "any";
        var parts = new List<string>();
        foreach (var t in s.Type ?? [])
            parts.Add(t == "array" && s.Items?.Value is not null ? "array<" + TypeFormatString(s.Items.Value) + ">" : t);
        return parts.Count == 0 ? "any" : string.Join(", ", parts);
    }
    public static string FormatString(Schema? s) => s is null || s.Format == "" ? "none" : s.Format;
    public static string TypeFormatString(Schema? s)
    {
        if (s is null) return "";
        var t = TypeString(s);
        if (s.Format == "") return t;
        return t == "any" ? s.Format : t + "/" + s.Format;
    }

    public static string JoinTypes(List<string> types)
    {
        if (types.Count == 0) return "";
        if (types.Count == 1) return types[0];
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < types.Count; i++)
        {
            if (i > 0) sb.Append(i == types.Count - 1 ? " and " : ", ");
            sb.Append(types[i]);
        }
        return sb.ToString();
    }

    public static string FormatDependentRequiredModification(StringsDiff d)
    {
        var parts = new List<string>();
        if (d.Added.Count > 0) parts.Add(string.Join(", ", d.Added) + " added");
        if (d.Deleted.Count > 0) parts.Add(string.Join(", ", d.Deleted) + " removed");
        return string.Join(", ", parts);
    }

    // url_equivalence.go
    public static bool SameURL(object? from, object? to) => from is string f && to is string t && NormalizeURL(f) == NormalizeURL(t);

    private static readonly System.Text.RegularExpressions.Regex PercentEncoding = new("%[0-9a-fA-F]{2}", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string NormalizePercentEncoding(string s) => PercentEncoding.Replace(s, m =>
    {
        int v = Convert.ToInt32(m.Value[1..], 16);
        char c = (char)v;
        bool unreserved = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '.' or '_' or '~';
        return unreserved ? c.ToString() : m.Value.ToUpperInvariant();
    });

    private static string NormalizeURL(string raw)
    {
        // minimal net/url semantics: scheme://host[:port]/path?query#fragment
        int schemeEnd = raw.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0) return NormalizePercentEncoding(raw);
        var scheme = raw[..schemeEnd].ToLowerInvariant();
        var rest = raw[(schemeEnd + 3)..];
        int pathStart = rest.IndexOfAny(['/', '?', '#']);
        var host = (pathStart < 0 ? rest : rest[..pathStart]).ToLowerInvariant();
        var tail = pathStart < 0 ? "" : rest[pathStart..];
        if (host == "") return NormalizePercentEncoding(raw);
        int colon = host.LastIndexOf(':');
        if (colon >= 0 && !host.EndsWith(']'))
        {
            var port = host[(colon + 1)..];
            if ((scheme == "https" && port == "443") || (scheme == "http" && port == "80")) host = host[..colon];
        }
        if ((tail == "" || tail[0] != '/') && (scheme is "http" or "https")) tail = "/" + tail;
        return NormalizePercentEncoding(scheme + "://" + host + tail);
    }
}

// ====================================================================== schemawalk

public sealed class SchemaWalker
{
    public Action<string, string, SchemaDiff, SchemaDiff?, bool>? Enter;
    public Action<string, SchemaDiff, bool>? Properties;
    internal Action<string, (SchemaDiff, bool)>? Arrive;
    private readonly HashSet<(SchemaDiff, bool)> _seen = new(VisitComparer.Instance);

    private sealed class VisitComparer : IEqualityComparer<(SchemaDiff, bool)>
    {
        public static readonly VisitComparer Instance = new();
        public bool Equals((SchemaDiff, bool) a, (SchemaDiff, bool) b) => ReferenceEquals(a.Item1, b.Item1) && a.Item2 == b.Item2;
        public int GetHashCode((SchemaDiff, bool) v) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(v.Item1), v.Item2);
    }

    public void Walk(SchemaDiff d) => WalkNode("", "", d, null, false);

    private void WalkNode(string propertyPath, string propertyName, SchemaDiff d, SchemaDiff? parent, bool underAllOf)
    {
        var nodePath = propertyName != "" ? PropertyFullName(propertyPath, propertyName) : propertyPath;
        var visit = (d, underAllOf);
        Arrive?.Invoke(nodePath, visit);
        if (!_seen.Add(visit)) return;
        if (Enter is not null && (propertyName != "" || propertyPath != "")) Enter(propertyPath, propertyName, d, parent, underAllOf);
        propertyPath = nodePath;
        if (d.AllOfDiff is not null) foreach (var v in d.AllOfDiff.Modified) WalkNode(PropertyFullName(propertyPath, $"allOf[{v}]"), "", v.Diff, d, true);
        if (d.AnyOfDiff is not null) foreach (var v in d.AnyOfDiff.Modified) WalkNode(PropertyFullName(propertyPath, $"anyOf[{v}]"), "", v.Diff, d, underAllOf);
        if (d.OneOfDiff is not null) foreach (var v in d.OneOfDiff.Modified) WalkNode(PropertyFullName(propertyPath, $"oneOf[{v}]"), "", v.Diff, d, underAllOf);
        if (d.ItemsDiff is not null) WalkNode(PropertyFullName(propertyPath, "items"), "", d.ItemsDiff, d, underAllOf);
        if (d.PropertiesDiff is not null)
        {
            Properties?.Invoke(propertyPath, d, underAllOf);
            foreach (var (name, v) in d.PropertiesDiff.Modified) WalkNode(propertyPath, name, v, d, underAllOf);
        }
        if (d.AdditionalPropertiesDiff is not null) WalkNode(PropertyFullName(propertyPath, "additionalProperties"), "", d.AdditionalPropertiesDiff, d, underAllOf);
        if (d.PrefixItemsDiff is not null) foreach (var v in d.PrefixItemsDiff.Modified) WalkNode($"{propertyPath}/prefixItems[{v}]", "", v.Diff, d, underAllOf);
        if (d.ContainsDiff is not null) WalkNode($"{propertyPath}/contains", "", d.ContainsDiff, d, underAllOf);
        if (d.PropertyNamesDiff is not null) WalkNode($"{propertyPath}/propertyNames", "", d.PropertyNamesDiff, d, underAllOf);
        if (d.UnevaluatedItemsDiff is not null) WalkNode($"{propertyPath}/unevaluatedItems", "", d.UnevaluatedItemsDiff, d, underAllOf);
        if (d.UnevaluatedPropertiesDiff is not null) WalkNode($"{propertyPath}/unevaluatedProperties", "", d.UnevaluatedPropertiesDiff, d, underAllOf);
        if (d.IfDiff is not null) WalkNode($"{propertyPath}/if", "", d.IfDiff, d, underAllOf);
        if (d.ThenDiff is not null) WalkNode($"{propertyPath}/then", "", d.ThenDiff, d, underAllOf);
        if (d.ElseDiff is not null) WalkNode($"{propertyPath}/else", "", d.ElseDiff, d, underAllOf);
        if (d.NotDiff is not null) WalkNode($"{propertyPath}/not", "", d.NotDiff, d, underAllOf);
        if (d.ContentSchemaDiff is not null) WalkNode($"{propertyPath}/contentSchema", "", d.ContentSchemaDiff, d, underAllOf);
        if (d.PatternPropertiesDiff is not null) foreach (var (k, v) in d.PatternPropertiesDiff.Modified) WalkNode($"{propertyPath}/patternProperties[{k}]", "", v, d, underAllOf);
        if (d.DependentSchemasDiff is not null) foreach (var (k, v) in d.DependentSchemasDiff.Modified) WalkNode($"{propertyPath}/dependentSchemas[{k}]", "", v, d, underAllOf);
    }

    public static void ModifiedProperties(SchemaDiff? d, Action<string, string, SchemaDiff, SchemaDiff?> processor)
    {
        if (d is null) return;
        new SchemaWalker { Enter = (p, n, item, parent, _) => processor(p, n, item, parent) }.Walk(d);
    }

    public static void AddedProperties(SchemaDiff? d, Action<string, string, Schema, SchemaDiff, bool> processor)
    {
        if (d is null) return;
        new SchemaWalker { Properties = (p, sd, under) => { foreach (var name in sd.PropertiesDiff!.Added) if (sd.Revision!.Properties[name].Value is { } v) processor(p, name, v, sd, under); } }.Walk(d);
    }

    public static void DeletedProperties(SchemaDiff? d, Action<string, string, Schema, SchemaDiff, bool> processor)
    {
        if (d is null) return;
        new SchemaWalker { Properties = (p, sd, under) => { foreach (var name in sd.PropertiesDiff!.Deleted) if (sd.Base!.Properties[name].Value is { } v) processor(p, name, v, sd, under); } }.Walk(d);
    }

    /// <summary>schemawalk.PropertyFullName (oasdiff 1.35.0): names joined with "/", appended to the path with "/". No
    /// names (a sub-schema itself, e.g. the items of an array) leaves a trailing "/": "items/".</summary>
    public static string PropertyFullName(string propertyPath, params string[] names)
    {
        var fullName = string.Join("/", names);
        return propertyPath != "" ? propertyPath + "/" + fullName : fullName;
    }
}

/// <summary>schemawalk.References: shared schemas reachable on several paths.</summary>
internal sealed class References
{
    private sealed class Shared { public string Name = ""; public List<string> Paths = []; }
    private readonly List<Shared> _shared = [];
    private readonly Dictionary<string, Shared> _byPath = new(StringComparer.Ordinal);
    private readonly Dictionary<Shared, (int count, bool bounded)> _counted = new(ReferenceEqualityComparer.Instance);

    public References(SchemaDiff root)
    {
        var reaches = new Dictionary<(SchemaDiff, bool), List<string>>(new VisitEq());
        var order = new List<(SchemaDiff, bool)>();
        new SchemaWalker
        {
            Arrive = (path, visit) =>
            {
                if (!reaches.TryGetValue(visit, out var l)) { order.Add(visit); reaches[visit] = l = []; }
                l.Add(path);
            },
        }.Walk(root);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var visit in order)
        {
            var name = visit.Item1.RevisionComponent != "" ? visit.Item1.RevisionComponent : visit.Item1.BaseComponent;
            if (name != "") names.TryAdd(reaches[visit][0], name);
        }
        foreach (var visit in order)
        {
            var paths = reaches[visit];
            if (paths.Count < 2) continue;
            _shared.Add(new Shared { Name = Innermost(names, paths[0]) ?? "", Paths = paths });
        }
        foreach (var s in _shared) _byPath.TryAdd(s.Paths[0], s);
    }

    private sealed class VisitEq : IEqualityComparer<(SchemaDiff, bool)>
    {
        public bool Equals((SchemaDiff, bool) a, (SchemaDiff, bool) b) => ReferenceEquals(a.Item1, b.Item1) && a.Item2 == b.Item2;
        public int GetHashCode((SchemaDiff, bool) v) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(v.Item1), v.Item2);
    }

    private static T? Innermost<T>(Dictionary<string, T> byPath, string path) where T : class
    {
        while (true)
        {
            if (byPath.TryGetValue(path, out var v)) return v;
            if (path == "") return null;
            int i = path.LastIndexOf('/');
            path = i >= 0 ? path[..i] : "";
        }
    }

    private Shared? Enclosing(string path, bool strict)
    {
        if (strict)
        {
            if (path == "") return null;
            int i = path.LastIndexOf('/');
            path = i >= 0 ? path[..i] : "";
        }
        return Innermost(_byPath, path);
    }

    public (string name, List<string> paths, int count, bool cyclic)? At(string path, int limit)
    {
        var found = Enclosing(path, false);
        if (found is null) return null;
        var paths = new List<string> { path };
        foreach (var other in PathsAt(path, false, ""))
        {
            if (paths.Count >= limit) break;
            if (!paths.Contains(other)) paths.Add(other);
        }
        var (count, ok) = CountAt(path, false, new HashSet<Shared>(ReferenceEqualityComparer.Instance));
        return (found.Name, paths, ok ? count : 0, !ok);
    }

    private IEnumerable<string> PathsAt(string path, bool strict, string below)
    {
        var s = Enclosing(path, strict);
        if (s is null) { yield return JoinPath(path, below); yield break; }
        below = JoinPath(Trim(path, s.Paths[0]), below);
        foreach (var reference in s.Paths)
            foreach (var p in PathsAt(reference, true, below)) yield return p;
    }

    private static string Trim(string path, string prefix)
    {
        var r = path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
        return r.StartsWith('/') ? r[1..] : r;
    }

    private static string JoinPath(string path, string below) => below == "" ? path : SchemaWalker.PropertyFullName(path, below);

    private (int, bool) CountAt(string path, bool strict, HashSet<Shared> visiting)
    {
        var s = Enclosing(path, strict);
        if (s is null) return (1, true);
        if (_counted.TryGetValue(s, out var c)) return c;
        if (!visiting.Add(s)) return (0, false);
        int count = 0; bool bounded = true;
        foreach (var reference in s.Paths)
        {
            var (n, ok) = CountAt(reference, true, visiting);
            if (!ok) { count = 0; bounded = false; break; }
            count = Math.Min(count, int.MaxValue - n) + n;
        }
        visiting.Remove(s);
        _counted[s] = (count, bounded);
        return (count, bounded);
    }
}

// ====================================================================== location

public static class Loc
{
    /// <summary>location.displayFilePath of the file kin-openapi records in an origin (see SpecLoader.OriginFile).</summary>
    public static string DisplayFilePath(string file) => Change.SourceDisplay(Load.SpecLoader.OriginFile(file));

    private static SourceLoc FromKey(Sources sources, Operation? op, Location key) => new()
    {
        File = DisplayFilePath(key.File) is { Length: > 0 } f ? f : sources[op],
        Line = key.Line, Column = key.Column, EndLine = key.EndLine, EndColumn = key.EndColumn,
    };

    public static SourceLoc FromOrigin(Sources sources, Operation? op, Origin? origin)
    {
        if (origin?.Key is null) return new SourceLoc { File = sources[op] };
        return FromKey(sources, op, origin.Key);
    }

    public static SourceLoc? FromField(Sources sources, Operation? op, Origin? origin, string field)
    {
        if (origin?.Field(field) is not { } loc) return null;
        return new SourceLoc
        {
            File = DisplayFilePath(loc.File) is { Length: > 0 } f ? f : sources[op],
            Line = loc.Line, Column = loc.Column, EndLine = loc.EndLine, EndColumn = loc.EndColumn,
        };
    }

    private static (SourceLoc?, SourceLoc?) OperationSources(Sources s, Operation? b, Operation? r)
    {
        if (b?.Origin is null && r?.Origin is null) return (null, null);
        return (b is null ? null : FromOrigin(s, b, b.Origin), r is null ? null : FromOrigin(s, r, r.Origin));
    }

    public static SourceLoc? FromSequenceItem(Sources sources, Operation? op, Origin? origin, string field, string value)
    {
        if (origin?.Sequences is null || !origin.Sequences.TryGetValue(field, out var items)) return null;
        foreach (var item in items)
            if (item.Name == value)
                return new SourceLoc { File = DisplayFilePath(item.File) is { Length: > 0 } f ? f : sources[op], Line = item.Line, Column = item.Column, EndLine = item.EndLine, EndColumn = item.EndColumn };
        return null;
    }

    public static SourceLoc? SequenceItemSource(Sources sources, Operation? op, string field, string value, SourceLoc? fallback) =>
        op is not null && FromSequenceItem(sources, op, op.Origin, field, value) is { } s ? s : fallback;

    public static (SourceLoc?, SourceLoc?) OperationField(Sources s, MethodDiff m, string field)
    {
        if (m.Base?.Origin is null && m.Revision?.Origin is null) return (null, null);
        return (m.Base is null ? null : FromField(s, m.Base, m.Base.Origin, field), m.Revision is null ? null : FromField(s, m.Revision, m.Revision.Origin, field));
    }

    public static (SourceLoc?, SourceLoc?) ParameterField(Sources s, MethodDiff m, ParameterDiff? p, string field)
    {
        if (p is null || (p.Base?.Origin is null && p.Revision?.Origin is null)) return OperationSources(s, m.Base, m.Revision);
        return (p.Base is not null && m.Base is not null ? FromField(s, m.Base, p.Base.Origin, field) : null,
                p.Revision is not null && m.Revision is not null ? FromField(s, m.Revision, p.Revision.Origin, field) : null);
    }

    public static (SourceLoc?, SourceLoc?) Schema(Sources s, MethodDiff m, SchemaDiff? d)
    {
        if (d is null || (d.Base?.Origin is null && d.Revision?.Origin is null)) return OperationSources(s, m.Base, m.Revision);
        return (d.Base is not null && m.Base is not null ? FromOrigin(s, m.Base, d.Base.Origin) : null,
                d.Revision is not null && m.Revision is not null ? FromOrigin(s, m.Revision, d.Revision.Origin) : null);
    }

    public static (SourceLoc?, SourceLoc?) Parameter(Sources s, MethodDiff m, ParameterDiff? p)
    {
        if (p is null || (p.Base?.Origin is null && p.Revision?.Origin is null)) return OperationSources(s, m.Base, m.Revision);
        return (p.Base is not null && m.Base is not null ? FromOrigin(s, m.Base, p.Base.Origin) : null,
                p.Revision is not null && m.Revision is not null ? FromOrigin(s, m.Revision, p.Revision.Origin) : null);
    }

    public static (SourceLoc?, SourceLoc?) Response(Sources s, MethodDiff m, ResponseDiff? r)
    {
        if (r is null || (r.Base?.Origin is null && r.Revision?.Origin is null)) return OperationSources(s, m.Base, m.Revision);
        return (r.Base is not null && m.Base is not null ? FromOrigin(s, m.Base, r.Base.Origin) : null,
                r.Revision is not null && m.Revision is not null ? FromOrigin(s, m.Revision, r.Revision.Origin) : null);
    }

    public static (SourceLoc?, SourceLoc?) SchemaField(Sources s, MethodDiff m, SchemaDiff? d, string field)
    {
        if (d is null || (d.Base?.Origin is null && d.Revision?.Origin is null)) return OperationSources(s, m.Base, m.Revision);
        return (d.Base is not null && m.Base is not null ? FromField(s, m.Base, d.Base.Origin, field) : null,
                d.Revision is not null && m.Revision is not null ? FromField(s, m.Revision, d.Revision.Origin, field) : null);
    }

    public static (SourceLoc?, SourceLoc?) SchemaDeletedItem(Sources s, MethodDiff m, SchemaDiff? d, string field, string value)
    {
        if (d?.Base is null || m.Base is null) return (null, null);
        return (FromSequenceItem(s, m.Base, d.Base.Origin, field, value), null);
    }

    public static (SourceLoc?, SourceLoc?) SchemaAddedItem(Sources s, MethodDiff m, SchemaDiff? d, string field, string value)
    {
        if (d?.Revision is null || m.Revision is null) return (null, null);
        return (null, FromSequenceItem(s, m.Revision, d.Revision.Origin, field, value));
    }

    public static SourceLoc? SchemaMapItem(Sources s, Operation? op, Dictionary<string, Ref<Model.Schema>>? schemas, string name)
    {
        if (schemas is null || !schemas.TryGetValue(name, out var r) || r?.Value?.Origin is null) return null;
        return FromOrigin(s, op, r.Value.Origin);
    }

    public static (SourceLoc?, SourceLoc?) Subschema(Sources s, MethodDiff m, SchemaDiff? d, string field, int baseIndex, int revisionIndex)
    {
        if (d is null) return OperationSources(s, m.Base, m.Revision);
        SourceLoc? b = null, r = null;
        if (baseIndex >= 0 && d.Base is not null && m.Base is not null) b = SubschemaSource(s, m.Base, d.Base, field, baseIndex);
        if (revisionIndex >= 0 && d.Revision is not null && m.Revision is not null) r = SubschemaSource(s, m.Revision, d.Revision, field, revisionIndex);
        if (b is null && r is null) return SchemaField(s, m, d, field);
        return (b, r);
    }

    private static SourceLoc? SubschemaSource(Sources s, Operation op, Model.Schema schema, string field, int index)
    {
        var refs = field switch { "allOf" => schema.AllOf, "oneOf" => schema.OneOf, "anyOf" => schema.AnyOf, _ => null };
        if (refs is null || index >= refs.Count || refs[index] is null) return null;
        var origin = refs[index].Origin;
        if (origin?.Key is null)
        {
            origin = refs[index].Value?.Origin;
            if (origin?.Key is null) return null;
        }
        return FromKey(s, op, origin.Key);
    }

    public static SourceLoc? ParameterSource(Sources s, Operation? op, Model.Parameter? p) => op is null || p?.Origin is null ? null : FromOrigin(s, op, p.Origin);

    public static (SourceLoc?, SourceLoc?) RequestBodyField(Sources s, MethodDiff m, string field)
    {
        var bo = m.Base?.RequestBody?.Value?.Origin;
        var ro = m.Revision?.RequestBody?.Value?.Origin;
        if (bo is null && ro is null) return OperationSources(s, m.Base, m.Revision);
        return (m.Base is null ? null : FromField(s, m.Base, bo, field), m.Revision is null ? null : FromField(s, m.Revision, ro, field));
    }

    public static SourceLoc? RequestBody(Sources s, Operation? op) => op?.RequestBody?.Value?.Origin is null ? null : FromOrigin(s, op, op.RequestBody.Value.Origin);

    public static SourceLoc? RequestBodyMediaType(Sources s, Operation? op, string mediaType)
    {
        if (op?.RequestBody?.Value is null) return null;
        return op.RequestBody.Value.Content.TryGetValue(mediaType, out var mt) && mt?.Origin is not null ? FromOrigin(s, op, mt.Origin) : null;
    }

    public static SourceLoc? MediaType(Sources s, Operation? op, Model.Response? response, string mediaType)
    {
        if (op is null || response is null) return null;
        return response.Content.TryGetValue(mediaType, out var mt) && mt?.Origin is not null ? FromOrigin(s, op, mt.Origin) : null;
    }

    public static (SourceLoc?, SourceLoc?) ResponseMediaTypeName(Sources s, MethodDiff m, ResponseDiff r, string from, string to)
    {
        var b = MediaType(s, m.Base, r.Base, from);
        var rv = MediaType(s, m.Revision, r.Revision, to);
        return b is null && rv is null ? Response(s, m, r) : (b, rv);
    }

    public static (SourceLoc?, SourceLoc?) Header(Sources s, MethodDiff m, ResponseDiff r, string headerName)
    {
        Origin? bo = null, ro = null;
        if (r.Base is not null && r.Base.Headers.TryGetValue(headerName, out var h1) && h1?.Value is not null) bo = h1.Value.Origin;
        if (r.Revision is not null && r.Revision.Headers.TryGetValue(headerName, out var h2) && h2?.Value is not null) ro = h2.Value.Origin;
        if (bo is null && ro is null) return Response(s, m, r);
        return (m.Base is null ? null : FromOrigin(s, m.Base, bo), m.Revision is null ? null : FromOrigin(s, m.Revision, ro));
    }

    public static SourceLoc? Property(Sources s, Operation? op, Model.Schema? schema) => op is null || schema?.Origin is null ? null : FromOrigin(s, op, schema.Origin);

    public static SourceLoc? FromOriginOnly(Origin? origin)
    {
        if (origin?.Key is null) return null;
        var k = origin.Key;
        return new SourceLoc { File = DisplayFilePath(k.File), Line = k.Line, Column = k.Column, EndLine = k.EndLine, EndColumn = k.EndColumn };
    }

    public static SourceLoc? Security(Sources s, Operation? op)
    {
        if (op?.Origin is null) return null;
        return FromField(s, op, op.Origin, "security") ?? FromOrigin(s, op, op.Origin);
    }

    public static SourceLoc? FromFieldOnly(Origin? origin, string field)
    {
        if (origin?.Field(field) is not { } loc) return null;
        return new SourceLoc { File = DisplayFilePath(loc.File), Line = loc.Line, Column = loc.Column };
    }

    public static SourceLoc? StabilityField(Sources s, Operation? op, Origin? origin)
    {
        if (origin is null) return null;
        return FromField(s, op, origin, DiffConfig.XStabilityLevelExtension) ?? FromOrigin(s, op, origin);
    }

    public static SourceLoc? ResponseSource(Sources s, Operation? op, string status)
    {
        if (op is null) return null;
        if (op.Responses?.Value(status) is { Value.Origin: not null } rr) return FromOrigin(s, op, rr.Value!.Origin);
        if (op.Origin is null) return null;
        return FromField(s, op, op.Origin, "responses");
    }
}

// ====================================================================== deprecation

public static class Deprecation
{
    public sealed record Ids(string Reactivated, string Deprecated, string WithSunset, string SunsetMissing, string SunsetParse, string SunsetTooSmall);
    public sealed record Stage(string Id, List<object?> Args, DateOnly? Sunset, bool AtSunset);
    public sealed record RemovalIds(string Removed, string WithDeprecation, string BeforeSunset, string SunsetParse);

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public static bool NowDeprecated(ValueDiff d) => d.To is not null && d.To is not false;

    public static (DateOnly date, GoError? error) SunsetDate(object? sunset)
    {
        if (sunset is string s)
        {
            if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return (d, null);
            if (DateTimeOffset.TryParseExact(s, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFFFK"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return (DateOnly.FromDateTime(dt.DateTime), null);
        }
        return (default, new GoError("sunset date doesn't conform with RFC3339: " + Fmt.ExtValueText(sunset)));
    }

    private static (object? value, bool found) SunsetExtension(Dictionary<string, object?>? ext) =>
        ext is not null && ext.TryGetValue(DiffConfig.SunsetExtension, out var v) ? (v, true) : (null, false);

    public static Stage Announce(ValueDiff deprecatedDiff, Dictionary<string, object?>? ext, uint days, Ids ids)
    {
        if (!NowDeprecated(deprecatedDiff)) return new Stage(ids.Reactivated, [], null, false);
        var (sunset, found) = SunsetExtension(ext);
        if (!found) return new Stage(days > 0 ? ids.SunsetMissing : ids.Deprecated, [], null, false);
        var (date, err) = SunsetDate(sunset);
        if (err is not null) return new Stage(ids.SunsetParse, [err], null, true);
        if (date.DayNumber - Today.DayNumber < (int)days) return new Stage(ids.SunsetTooSmall, [date, (ulong)days], date, true);
        if (ids.WithSunset == "") return new Stage(ids.Deprecated, [], date, false);
        return new Stage(ids.WithSunset, [date], date, false);
    }

    public static (string id, List<object?> args, bool report) Removal(bool deprecated, Dictionary<string, object?>? ext, RemovalIds ids)
    {
        if (!deprecated) return (ids.Removed, [], true);
        var (sunset, found) = SunsetExtension(ext);
        if (!found) return (ids.WithDeprecation, [], true);
        var (date, err) = SunsetDate(sunset);
        if (err is not null) return ids.SunsetParse == "" ? (ids.Removed, [], true) : (ids.SunsetParse, [err], true);
        if (Today < date) return (ids.BeforeSunset, [date], true);
        return ("", [], false);
    }

    /// <summary>checker.deprecationDays</summary>
    public static (uint days, bool ok) Days(CheckerConfig config, Dictionary<string, object?>? ext)
    {
        var (stability, err) = Stability.Get(ext);
        if (err is not null) return (0, false);
        return (Stability.DeprecationDays(config, stability), true);
    }
}
