using OasDelta.Diff;
using OasDelta.Model;

namespace OasDelta.Checker;

/// <summary>Port of checker/check_types.go: when a type or format change is breaking.</summary>
public static class TypeRules
{
    public const string LooselyTypedComment = "type-change-loosely-typed-comment";

    private static List<string> BaseType(SchemaDiff d) => d.Base?.Type ?? [];
    private static List<string> RevisionType(SchemaDiff d) => d.Revision?.Type ?? [];

    public static bool RequestTypeFormatBreaking(StringsDiff? typeDiff, ValueDiff? formatDiff, bool stronglyTyped, SchemaDiff d)
    {
        if (TypeSetWidened(typeDiff, d)) typeDiff = null;
        return TypeFormatBreaking(typeDiff, formatDiff, stronglyTyped, d.Revision?.Type);
    }

    public static bool ResponseTypeFormatBreaking(StringsDiff? typeDiff, ValueDiff? formatDiff, bool stronglyTyped, SchemaDiff d)
    {
        if (TypeSetNarrowed(typeDiff, d)) typeDiff = null;
        return TypeFormatBreaking(typeDiff?.Reverse(), formatDiff?.Reverse(), stronglyTyped, d.Base?.Type);
    }

    public static bool TypeSetWidened(StringsDiff? t, SchemaDiff d) => t is not null && IsTypeSetSubset(BaseType(d), RevisionType(d));
    public static bool TypeSetNarrowed(StringsDiff? t, SchemaDiff d) => t is not null && IsTypeSetSubset(RevisionType(d), BaseType(d));
    private static bool BothTypesConcrete(SchemaDiff d) => BaseType(d).Count > 0 && RevisionType(d).Count > 0;
    public static bool IsRequestLooseTypeSwap(StringsDiff? t, SchemaDiff d) => t is { Empty: false } && BothTypesConcrete(d) && !TypeSetWidened(t, d);
    public static bool IsResponseLooseTypeSwap(StringsDiff? t, SchemaDiff d) => t is { Empty: false } && BothTypesConcrete(d) && !TypeSetNarrowed(t, d);

    private static bool IsTypeSetSubset(List<string> sub, List<string> super)
    {
        if (sub.Count == 0) return false;
        foreach (var t in sub) if (!super.Exists(b => IsSubtype(t, b))) return false;
        return true;
    }

    private static bool IsSubtype(string sub, string super) => sub == super || (sub == "integer" && super == "number");

    private static bool TypeFormatBreaking(StringsDiff? typeDiff, ValueDiff? formatDiff, bool stronglyTyped, List<string>? toType)
    {
        if (typeDiff is not null && (toType is null || toType.Count == 0)) return false;
        return TypeOrFormatBreaking(typeDiff, formatDiff, stronglyTyped, toType);
    }

    public static bool TypeOrFormatBreaking(StringsDiff? typeDiff, ValueDiff? formatDiff, bool stronglyTyped, List<string>? toType)
    {
        bool typeBreaking = typeDiff is not null && !IsTypeContained(typeDiff.Added, typeDiff.Deleted, stronglyTyped);
        bool formatBreaking = formatDiff is not null && !IsFormatContained(toType, formatDiff.To, formatDiff.From);
        return typeBreaking || formatBreaking;
    }

    public static bool IsTypeContained(List<string> to, List<string> from, bool stronglyTyped)
    {
        if (to.Count == 1 && from.Count == 1 && IsSubtype(from[0], to[0])) return true;
        if (!stronglyTyped) return to.Count == 0 || (to.Count == 1 && to[0] == "string");
        return false;
    }

    public static bool IsStronglyTyped(string mediaType) => mediaType == "application/json" || mediaType.EndsWith("+json", StringComparison.Ordinal);

    private static bool IsFormatContained(List<string>? revisionType, object? to, object? from)
    {
        if (to is "") return true;
        if (revisionType is null || revisionType.Count > 1) return false;
        string single = revisionType.Count == 0 ? "" : revisionType[0];
        return single switch
        {
            "number" => to is "double" && from is "float",
            "integer" => (to is "int64" && from is "int32") || (to is "bigint" && from is "int32") || (to is "bigint" && from is "int64"),
            _ => false,
        };
    }

    public static List<string> WithoutNull(List<string>? types)
    {
        var o = new List<string>(types?.Count ?? 0);
        if (types is not null) foreach (var t in types) if (t != "null") o.Add(t);
        return o;
    }

    /// <summary>kin-openapi Parameter.SerializationMethod (style, explode) with defaults.</summary>
    public static (string style, bool explode)? SerializationMethod(Parameter p)
    {
        switch (p.In)
        {
            case Parameter.InPath or Parameter.InHeader:
                return (p.Style == "" ? "simple" : p.Style, p.Explode ?? false);
            case Parameter.InQuery or Parameter.InCookie:
                var style = p.Style == "" ? "form" : p.Style;
                return (style, p.Explode ?? (style != "spaceDelimited" && style != "pipeDelimited"));
            default:
                return null;
        }
    }
}
