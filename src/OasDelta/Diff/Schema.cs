using System.Runtime.CompilerServices;
using System.Text;
using OasDelta.Model;
using OasDelta.Yaml;

namespace OasDelta.Diff;

public sealed class SchemaDiff : IDiff
{
    internal int Id; // stable identity for cut keys
    public bool SchemaAdded, SchemaDeleted;
    public ExtensionsDiff? ExtensionsDiff;
    public SubschemasDiff? OneOfDiff, AnyOfDiff, AllOfDiff;
    public SchemaDiff? NotDiff;
    public StringsDiff? TypeDiff;
    public ValueDiff? TitleDiff, FormatDiff, DescriptionDiff;
    public EnumDiff? EnumDiff;
    public ValueDiff? DefaultDiff, ExampleDiff;
    public ExternalDocsDiff? ExternalDocsDiff;
    public ValueDiff? AdditionalPropertiesAllowedDiff, UniqueItemsDiff, ExclusiveMinDiff, ExclusiveMaxDiff, NullableDiff, ReadOnlyDiff, WriteOnlyDiff,
        AllowEmptyValueDiff, XMLDiff, DeprecatedDiff, MinDiff, MaxDiff, MultipleOfDiff, MinLengthDiff, MaxLengthDiff, PatternDiff, MinItemsDiff, MaxItemsDiff;
    public SchemaDiff? ItemsDiff;
    public StringsDiff? RequiredDiff;
    public SchemasDiff? PropertiesDiff;
    public ValueDiff? MinPropsDiff, MaxPropsDiff;
    public SchemaDiff? AdditionalPropertiesDiff;
    public DiscriminatorDiff? DiscriminatorDiff;
    public ValueDiff? AlwaysDiff, ConstDiff, ExamplesDiff;
    public SubschemasDiff? PrefixItemsDiff;
    public SchemaDiff? ContainsDiff;
    public ValueDiff? MinContainsDiff, MaxContainsDiff;
    public SchemasDiff? PatternPropertiesDiff, DependentSchemasDiff;
    public SchemaDiff? PropertyNamesDiff;
    public ValueDiff? UnevaluatedItemsAllowedDiff;
    public SchemaDiff? UnevaluatedItemsDiff;
    public ValueDiff? UnevaluatedPropertiesAllowedDiff;
    public SchemaDiff? UnevaluatedPropertiesDiff, IfDiff, ThenDiff, ElseDiff;
    public DependentRequiredDiff? DependentRequiredDiff;
    public ValueDiff? SchemaIDDiff, AnchorDiff, DynamicRefDiff, DynamicAnchorDiff, ContentMediaTypeDiff, ContentEncodingDiff;
    public SchemaDiff? ContentSchemaDiff;
    public SchemasDiff? DefsDiff;
    public ValueDiff? SchemaDialectDiff, CommentDiff;
    public ListOfTypesDiff? ListOfTypesDiff;
    public OneOfWrappingDiff? OneOfWrappingDiff;
    public NullableWrappingDiff? NullableWrappingDiff;

    public Schema? Base, Revision;
    public string BaseComponent = "", RevisionComponent = "";

    public SchemaDiff Clone() => (SchemaDiff)MemberwiseClone();

    public bool Empty =>
        !SchemaAdded && !SchemaDeleted && ExtensionsDiff is null && OneOfDiff is null && AnyOfDiff is null && AllOfDiff is null && NotDiff is null &&
        TypeDiff is null && TitleDiff is null && FormatDiff is null && DescriptionDiff is null && EnumDiff is null && DefaultDiff is null &&
        ExampleDiff is null && ExternalDocsDiff is null && AdditionalPropertiesAllowedDiff is null && UniqueItemsDiff is null &&
        ExclusiveMinDiff is null && ExclusiveMaxDiff is null && NullableDiff is null && ReadOnlyDiff is null && WriteOnlyDiff is null &&
        AllowEmptyValueDiff is null && XMLDiff is null && DeprecatedDiff is null && MinDiff is null && MaxDiff is null && MultipleOfDiff is null &&
        MinLengthDiff is null && MaxLengthDiff is null && PatternDiff is null && MinItemsDiff is null && MaxItemsDiff is null && ItemsDiff is null &&
        RequiredDiff is null && PropertiesDiff is null && MinPropsDiff is null && MaxPropsDiff is null && AdditionalPropertiesDiff is null &&
        DiscriminatorDiff is null && AlwaysDiff is null && ConstDiff is null && ExamplesDiff is null && PrefixItemsDiff is null && ContainsDiff is null &&
        MinContainsDiff is null && MaxContainsDiff is null && PatternPropertiesDiff is null && DependentSchemasDiff is null && PropertyNamesDiff is null &&
        UnevaluatedItemsAllowedDiff is null && UnevaluatedItemsDiff is null && UnevaluatedPropertiesAllowedDiff is null && UnevaluatedPropertiesDiff is null &&
        IfDiff is null && ThenDiff is null && ElseDiff is null && DependentRequiredDiff is null && SchemaIDDiff is null && AnchorDiff is null &&
        DynamicRefDiff is null && DynamicAnchorDiff is null && ContentMediaTypeDiff is null && ContentEncodingDiff is null && ContentSchemaDiff is null &&
        DefsDiff is null && SchemaDialectDiff is null && CommentDiff is null && ListOfTypesDiff is null && OneOfWrappingDiff is null && NullableWrappingDiff is null;

    // child accessors in Go declaration order (childFields)
    internal const int ChildCount = 19;
    internal object? GetChild(int i) => i switch
    {
        0 => OneOfDiff, 1 => AnyOfDiff, 2 => AllOfDiff, 3 => NotDiff, 4 => ItemsDiff, 5 => PropertiesDiff, 6 => AdditionalPropertiesDiff,
        7 => PrefixItemsDiff, 8 => ContainsDiff, 9 => PatternPropertiesDiff, 10 => DependentSchemasDiff, 11 => PropertyNamesDiff,
        12 => UnevaluatedItemsDiff, 13 => UnevaluatedPropertiesDiff, 14 => IfDiff, 15 => ThenDiff, 16 => ElseDiff, 17 => ContentSchemaDiff, 18 => DefsDiff,
        _ => null,
    };
    internal void SetChild(int i, object? v)
    {
        switch (i)
        {
            case 0: OneOfDiff = (SubschemasDiff?)v; break;
            case 1: AnyOfDiff = (SubschemasDiff?)v; break;
            case 2: AllOfDiff = (SubschemasDiff?)v; break;
            case 3: NotDiff = (SchemaDiff?)v; break;
            case 4: ItemsDiff = (SchemaDiff?)v; break;
            case 5: PropertiesDiff = (SchemasDiff?)v; break;
            case 6: AdditionalPropertiesDiff = (SchemaDiff?)v; break;
            case 7: PrefixItemsDiff = (SubschemasDiff?)v; break;
            case 8: ContainsDiff = (SchemaDiff?)v; break;
            case 9: PatternPropertiesDiff = (SchemasDiff?)v; break;
            case 10: DependentSchemasDiff = (SchemasDiff?)v; break;
            case 11: PropertyNamesDiff = (SchemaDiff?)v; break;
            case 12: UnevaluatedItemsDiff = (SchemaDiff?)v; break;
            case 13: UnevaluatedPropertiesDiff = (SchemaDiff?)v; break;
            case 14: IfDiff = (SchemaDiff?)v; break;
            case 15: ThenDiff = (SchemaDiff?)v; break;
            case 16: ElseDiff = (SchemaDiff?)v; break;
            case 17: ContentSchemaDiff = (SchemaDiff?)v; break;
            case 18: DefsDiff = (SchemasDiff?)v; break;
        }
    }

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptTrue("schemaAdded", SchemaAdded); m.OptTrue("schemaDeleted", SchemaDeleted);
        m.Opt("extensions", ExtensionsDiff); m.Opt("oneOf", OneOfDiff); m.Opt("anyOf", AnyOfDiff); m.Opt("allOf", AllOfDiff);
        m.Opt("not", NotDiff); m.Opt("type", TypeDiff); m.Opt("title", TitleDiff); m.Opt("format", FormatDiff); m.Opt("description", DescriptionDiff);
        m.Opt("enum", EnumDiff); m.Opt("default", DefaultDiff); m.Opt("example", ExampleDiff); m.Opt("externalDocs", ExternalDocsDiff);
        m.Opt("additionalPropertiesAllowed", AdditionalPropertiesAllowedDiff); m.Opt("uniqueItems", UniqueItemsDiff);
        m.Opt("exclusiveMin", ExclusiveMinDiff); m.Opt("exclusiveMax", ExclusiveMaxDiff); m.Opt("nullable", NullableDiff);
        m.Opt("readOnly", ReadOnlyDiff); m.Opt("writeOnly", WriteOnlyDiff); m.Opt("allowEmptyValue", AllowEmptyValueDiff); m.Opt("XML", XMLDiff);
        m.Opt("deprecated", DeprecatedDiff); m.Opt("min", MinDiff); m.Opt("max", MaxDiff); m.Opt("multipleOf", MultipleOfDiff);
        m.Opt("minLength", MinLengthDiff); m.Opt("maxLength", MaxLengthDiff); m.Opt("pattern", PatternDiff); m.Opt("minItems", MinItemsDiff);
        m.Opt("maxItems", MaxItemsDiff); m.Opt("items", ItemsDiff); m.Opt("required", RequiredDiff is null ? null : new Embedded("stringsdiff", RequiredDiff)); m.Opt("properties", PropertiesDiff);
        m.Opt("minProps", MinPropsDiff); m.Opt("maxProps", MaxPropsDiff); m.Opt("additionalProperties", AdditionalPropertiesDiff);
        m.Opt("discriminatorDiff", DiscriminatorDiff); m.Opt("always", AlwaysDiff); m.Opt("const", ConstDiff); m.Opt("examples", ExamplesDiff);
        m.Opt("prefixItems", PrefixItemsDiff); m.Opt("contains", ContainsDiff); m.Opt("minContains", MinContainsDiff); m.Opt("maxContains", MaxContainsDiff);
        m.Opt("patternProperties", PatternPropertiesDiff); m.Opt("dependentSchemas", DependentSchemasDiff); m.Opt("propertyNames", PropertyNamesDiff);
        m.Opt("unevaluatedItemsAllowed", UnevaluatedItemsAllowedDiff); m.Opt("unevaluatedItems", UnevaluatedItemsDiff);
        m.Opt("unevaluatedPropertiesAllowed", UnevaluatedPropertiesAllowedDiff); m.Opt("unevaluatedProperties", UnevaluatedPropertiesDiff);
        m.Opt("if", IfDiff); m.Opt("then", ThenDiff); m.Opt("else", ElseDiff); m.Opt("dependentRequired", DependentRequiredDiff);
        m.Opt("$id", SchemaIDDiff); m.Opt("$anchor", AnchorDiff); m.Opt("$dynamicRef", DynamicRefDiff); m.Opt("$dynamicAnchor", DynamicAnchorDiff);
        m.Opt("contentMediaType", ContentMediaTypeDiff); m.Opt("contentEncoding", ContentEncodingDiff); m.Opt("contentSchema", ContentSchemaDiff);
        m.Opt("$defs", DefsDiff); m.Opt("$schema", SchemaDialectDiff); m.Opt("$comment", CommentDiff);
        m.Opt("listOfTypes", ListOfTypesDiff); m.Opt("oneOfWrapping", OneOfWrappingDiff); m.Opt("nullableWrapping", NullableWrappingDiff);
        return m;
    }

    // ------------------------------------------------------------------ construction

    internal static SchemaDiff? Get(DiffConfig config, DiffState state, Ref<Schema>? s1, Ref<Schema>? s2)
    {
        if (s1 is null || s2 is null || s1.Value is null || s2.Value is null) return GetInternal(config, state, s1, s2);
        var node = state.Graph.GetNode(config, state, s1, s2);
        if (state.Graph.Depth == 0)
        {
            var u = new Unroller(config, state);
            var unrolled = u.Unroll(node);
            if (u.Error is not null) throw u.Error;
            return unrolled;
        }
        return node;
    }

    internal static SchemaDiff? GetInternal(DiffConfig config, DiffState state, Ref<Schema>? r1, Ref<Schema>? r2)
    {
        if (r1 is null && r2 is null) return null;
        if (r1 is null) return new SchemaDiff { SchemaAdded = true };
        if (r2 is null) return new SchemaDiff { SchemaDeleted = true };
        var v1 = r1.Value ?? throw new DiffException("base schema value is nil");
        var v2 = r2.Value ?? throw new DiffException("revision schema value is nil");
        var r = new SchemaDiff { Base = v1, Revision = v2 };
        r.ExtensionsDiff = ExtensionsDiff.Get(config, v1.Extensions, v2.Extensions);
        r.OneOfDiff = SubschemasDiff.Get(config, state, v1.OneOf, v2.OneOf);
        r.AnyOfDiff = SubschemasDiff.Get(config, state, v1.AnyOf, v2.AnyOf);
        r.AllOfDiff = SubschemasDiff.Get(config, state, v1.AllOf, v2.AllOf);
        r.NotDiff = Get(config, state, v1.Not, v2.Not);
        r.TypeDiff = StringsDiff.Get(v1.Type, v2.Type);
        r.ListOfTypesDiff = ListOfTypesDiff.Get(v1, v2);
        r.TitleDiff = ValueDiff.Cond(config.IsExcludeTitle, v1.Title, v2.Title);
        r.FormatDiff = ValueDiff.Get(v1.Format, v2.Format);
        r.DescriptionDiff = ValueDiff.Cond(config.IsExcludeDescription, v1.Description, v2.Description);
        r.EnumDiff = EnumDiff.Get(v1.Enum, v2.Enum);
        r.DefaultDiff = ValueDiff.Get(v1.Default, v2.Default);
        r.ExampleDiff = ValueDiff.Cond(config.IsExcludeExamples, v1.Example, v2.Example);
        r.ExternalDocsDiff = ExternalDocsDiff.Get(config, v1.ExternalDocs, v2.ExternalDocs);
        r.AdditionalPropertiesAllowedDiff = ValueDiff.Ptr(v1.AdditionalProperties.Has, v2.AdditionalProperties.Has);
        r.UniqueItemsDiff = ValueDiff.Get(v1.UniqueItems, v2.UniqueItems);
        r.ExclusiveMinDiff = ValueDiff.Bound(v1.ExclusiveMin, v2.ExclusiveMin);
        r.ExclusiveMaxDiff = ValueDiff.Bound(v1.ExclusiveMax, v2.ExclusiveMax);
        r.NullableDiff = ValueDiff.Get(v1.Nullable, v2.Nullable);
        r.ReadOnlyDiff = ValueDiff.Get(v1.ReadOnly, v2.ReadOnly);
        r.WriteOnlyDiff = ValueDiff.Get(v1.WriteOnly, v2.WriteOnly);
        r.AllowEmptyValueDiff = ValueDiff.Get(v1.AllowEmptyValue, v2.AllowEmptyValue);
        r.XMLDiff = ValueDiff.Get(v1.XML, v2.XML);
        r.DeprecatedDiff = ValueDiff.Get(v1.Deprecated, v2.Deprecated);
        r.MinDiff = ValueDiff.Ptr(v1.Min, v2.Min);
        r.MaxDiff = ValueDiff.Ptr(v1.Max, v2.Max);
        r.MultipleOfDiff = ValueDiff.Ptr(v1.MultipleOf, v2.MultipleOf);
        r.MinLengthDiff = ValueDiff.Get(v1.MinLength, v2.MinLength);
        r.MaxLengthDiff = ValueDiff.Ptr(v1.MaxLength, v2.MaxLength);
        r.PatternDiff = ValueDiff.Get(v1.Pattern, v2.Pattern);
        r.MinItemsDiff = ValueDiff.Get(v1.MinItems, v2.MinItems);
        r.MaxItemsDiff = ValueDiff.Ptr(v1.MaxItems, v2.MaxItems);
        r.ItemsDiff = Get(config, state, v1.Items, v2.Items);
        r.RequiredDiff = StringsDiff.Get(v1.Required, v2.Required);
        r.PropertiesDiff = SchemasDiff.Get(config, state, v1.Properties, v2.Properties);
        r.MinPropsDiff = ValueDiff.Get(v1.MinProps, v2.MinProps);
        r.MaxPropsDiff = ValueDiff.Ptr(v1.MaxProps, v2.MaxProps);
        r.AdditionalPropertiesDiff = Get(config, state, v1.AdditionalProperties.Schema, v2.AdditionalProperties.Schema);
        r.DiscriminatorDiff = DiscriminatorDiff.Get(config, v1.Discriminator, v2.Discriminator);
        r.AlwaysDiff = ValueDiff.Ptr(v1.Always, v2.Always);
        r.ConstDiff = ValueDiff.Get(v1.Const, v2.Const);
        r.ExamplesDiff = ValueDiff.Get(v1.Examples, v2.Examples);
        r.PrefixItemsDiff = SubschemasDiff.GetPrefixItems(config, state, v1.PrefixItems, v2.PrefixItems);
        r.ContainsDiff = Get(config, state, v1.Contains, v2.Contains);
        r.MinContainsDiff = ValueDiff.Ptr(v1.MinContains, v2.MinContains);
        r.MaxContainsDiff = ValueDiff.Ptr(v1.MaxContains, v2.MaxContains);
        r.PatternPropertiesDiff = SchemasDiff.Get(config, state, v1.PatternProperties, v2.PatternProperties);
        r.DependentSchemasDiff = SchemasDiff.Get(config, state, v1.DependentSchemas, v2.DependentSchemas);
        r.PropertyNamesDiff = Get(config, state, v1.PropertyNames, v2.PropertyNames);
        r.UnevaluatedItemsAllowedDiff = ValueDiff.Ptr(v1.UnevaluatedItems.Has, v2.UnevaluatedItems.Has);
        r.UnevaluatedItemsDiff = Get(config, state, v1.UnevaluatedItems.Schema, v2.UnevaluatedItems.Schema);
        r.UnevaluatedPropertiesAllowedDiff = ValueDiff.Ptr(v1.UnevaluatedProperties.Has, v2.UnevaluatedProperties.Has);
        r.UnevaluatedPropertiesDiff = Get(config, state, v1.UnevaluatedProperties.Schema, v2.UnevaluatedProperties.Schema);
        r.IfDiff = Get(config, state, v1.If, v2.If);
        r.ThenDiff = Get(config, state, v1.Then, v2.Then);
        r.ElseDiff = Get(config, state, v1.Else, v2.Else);
        r.DependentRequiredDiff = DependentRequiredDiff.Get(v1.DependentRequired, v2.DependentRequired);
        r.SchemaIDDiff = ValueDiff.Get(v1.SchemaID, v2.SchemaID);
        r.AnchorDiff = ValueDiff.Get(v1.Anchor, v2.Anchor);
        r.DynamicRefDiff = ValueDiff.Get(v1.DynamicRef, v2.DynamicRef);
        r.DynamicAnchorDiff = ValueDiff.Get(v1.DynamicAnchor, v2.DynamicAnchor);
        r.ContentMediaTypeDiff = ValueDiff.Get(v1.ContentMediaType, v2.ContentMediaType);
        r.ContentEncodingDiff = ValueDiff.Get(v1.ContentEncoding, v2.ContentEncoding);
        r.ContentSchemaDiff = Get(config, state, v1.ContentSchema, v2.ContentSchema);
        r.DefsDiff = SchemasDiff.Get(config, state, v1.Defs, v2.Defs);
        r.SchemaDialectDiff = ValueDiff.Get(v1.SchemaDialect, v2.SchemaDialect);
        r.CommentDiff = ValueDiff.Get(v1.Comment, v2.Comment);
        return r;
    }

    internal static string ComponentName(Dictionary<Schema, string> names, Ref<Schema> r)
    {
        if (r.Value is not null && names.TryGetValue(r.Value, out var n)) return n;
        const string prefix = "#/components/schemas/";
        if (!r.RefPath.StartsWith(prefix, StringComparison.Ordinal)) return "";
        var name = r.RefPath[prefix.Length..];
        int slash = name.IndexOf('/');
        return slash >= 0 ? name[..slash] : name;
    }

    internal static Dictionary<Schema, string> ComponentNames(OpenApiDoc spec)
    {
        var names = new Dictionary<Schema, string>(ReferenceEqualityComparer.Instance);
        if (spec.Components is null) return names;
        foreach (var name in Values.SortedKeys(spec.Components.Schemas))
        {
            var r = spec.Components.Schemas[name];
            if (r.Value is not null) names.TryAdd(r.Value, name);
        }
        return names;
    }
}

public sealed class SchemasDiff : IDiff
{
    public List<string> Added = [], Deleted = [];
    public SortedDictionary<string, SchemaDiff> Modified = new(StringComparer.Ordinal);
    public Dictionary<string, Ref<Schema>>? Base, Revision;
    public bool Empty => Added.Count == 0 && Deleted.Count == 0 && Modified.Count == 0;

    internal static SchemasDiff? Get(DiffConfig config, DiffState state, Dictionary<string, Ref<Schema>> s1, Dictionary<string, Ref<Schema>> s2)
    {
        var r = new SchemasDiff();
        var (a, d, common) = DiffUtil.KeysDiff(s1, s2);
        r.Added = a; r.Deleted = d;
        foreach (var name in common)
            if (SchemaDiff.Get(config, state, s1[name], s2[name]) is { } sd) r.Modified[name] = sd;
        r.Base = s1; r.Revision = s2;
        return r.Empty ? null : r;
    }

    public SchemasDiff Clone() => new() { Added = Added, Deleted = Deleted, Modified = new(Modified, StringComparer.Ordinal), Base = Base, Revision = Revision };

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptList("added", Added); m.OptList("deleted", Deleted);
        if (Modified.Count > 0) m.Add("modified", Modified);
        return m;
    }
}

public readonly record struct Subschema(int Index, string Component, string Title) : IOutput
{
    public override string ToString()
    {
        if (Title != "") return $"subschema #{Index + 1}: {Title}";
        if (Component != "") return $"#/components/schemas/{Component}";
        return $"subschema #{Index + 1}";
    }

    public object? ToOutput()
    {
        var m = new OMap { { "index", (long)Index } };
        m.OptStr("component", Component); m.OptStr("title", Title);
        return m;
    }

    public static string Join(IEnumerable<Subschema> list) => string.Join(", ", list.Select(s => s.ToString()));
}

public sealed class ModifiedSubschema : IOutput
{
    public Subschema Base, Revision;
    public SchemaDiff Diff = null!;
    public override string ToString()
    {
        var b = Base.ToString(); var r = Revision.ToString();
        return b == r ? b : $"{b} -> {r}";
    }
    public object? ToOutput() => new OMap { { "base", Base }, { "revision", Revision }, { "diff", Diff } };
}

public sealed class SubschemasDiff : IDiff
{
    public List<Subschema> Added = [], Deleted = [];
    public List<ModifiedSubschema> Modified = [];
    internal bool Pending;
    internal List<Ref<Schema>>? BaseRefs, RevisionRefs;

    public bool Empty => Added.Count == 0 && Deleted.Count == 0 && Modified.Count == 0;

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptList("added", Added); m.OptList("deleted", Deleted); m.OptList("modified", Modified);
        return m;
    }

    internal static string ComponentNameOf(Ref<Schema> r) => r.RefPath[(r.RefPath.LastIndexOf('/') + 1)..];

    internal void AppendAdded(int index, Ref<Schema> r, string title) => Added.Add(new Subschema(index, ComponentNameOf(r), title));
    internal void AppendDeleted(int index, Ref<Schema> r, string title) => Deleted.Add(new Subschema(index, ComponentNameOf(r), title));

    internal static void AddModified(List<ModifiedSubschema> list, Ref<Schema> r1, Ref<Schema> r2, int i1, int i2, SchemaDiff? d)
    {
        if (d is null) return;
        list.Add(new ModifiedSubschema
        {
            Base = new Subschema(i1, ComponentNameOf(r1), r1.Value!.Title),
            Revision = new Subschema(i2, ComponentNameOf(r2), r2.Value!.Title),
            Diff = d,
        });
    }

    internal static SubschemasDiff? Get(DiffConfig config, DiffState state, List<Ref<Schema>> refs1, List<Ref<Schema>> refs2)
    {
        if (refs1.Count == 0 && refs2.Count == 0) return null;
        var d = GetRefDiff(config, state, refs1, refs2);
        d.Pending = true;
        d.BaseRefs = refs1;
        d.RevisionRefs = refs2;
        return d;
    }

    private static bool IsRef(Ref<Schema>? r) => r is not null && r.RefPath != "";
    internal static bool IsInline(Ref<Schema>? r) => r is not null && r.RefPath == "";
    internal static bool IsInlineRefactorBoundary(Ref<Schema>? a, Ref<Schema>? b) => a is not null && b is not null && (a.RefPath == "") != (b.RefPath == "");

    private sealed class RefMap
    {
        private readonly Dictionary<string, (Ref<Schema> r, Queue<int> idx)> _m = new(StringComparer.Ordinal);
        public RefMap(List<Ref<Schema>> refs)
        {
            for (int i = 0; i < refs.Count; i++)
            {
                if (!IsRef(refs[i])) continue;
                if (_m.TryGetValue(refs[i].RefPath, out var e)) e.idx.Enqueue(i);
                else { var q = new Queue<int>(); q.Enqueue(i); _m[refs[i].RefPath] = (refs[i], q); }
            }
        }
        public bool Pop(string key, out Ref<Schema> r, out int index)
        {
            if (_m.TryGetValue(key, out var e))
            {
                if (e.idx.Count > 0) { r = e.r; index = e.idx.Dequeue(); return true; }
                _m.Remove(key);
            }
            r = null!; index = 0;
            return false;
        }
    }

    private static SubschemasDiff GetRefDiff(DiffConfig config, DiffState state, List<Ref<Schema>> refs1, List<Ref<Schema>> refs2)
    {
        var result = new SubschemasDiff();
        var map2 = new RefMap(refs2);
        for (int i1 = 0; i1 < refs1.Count; i1++)
        {
            var r1 = refs1[i1];
            if (!IsRef(r1)) continue;
            if (map2.Pop(r1.RefPath, out var r2, out var i2))
            {
                AddModified(result.Modified, r1, r2, i1, i2, SchemaDiff.Get(config, state, r1, r2));
                continue;
            }
            result.AppendDeleted(i1, r1, "");
        }
        var map1 = new RefMap(refs1);
        for (int i2 = 0; i2 < refs2.Count; i2++)
        {
            var r2 = refs2[i2];
            if (!IsRef(r2)) continue;
            if (!map1.Pop(r2.RefPath, out _, out _)) result.AppendAdded(i2, r2, "");
        }
        return result;
    }

    internal static SubschemasDiff? GetPrefixItems(DiffConfig config, DiffState state, List<Ref<Schema>> refs1, List<Ref<Schema>> refs2)
    {
        if (refs1.Count == 0 && refs2.Count == 0) return null;
        var r = new SubschemasDiff();
        int n = Math.Min(refs1.Count, refs2.Count);
        for (int i = 0; i < n; i++) AddModified(r.Modified, refs1[i], refs2[i], i, i, SchemaDiff.Get(config, state, refs1[i], refs2[i]));
        for (int i = refs2.Count; i < refs1.Count; i++) r.AppendDeleted(i, refs1[i], refs1[i].Value!.Title);
        for (int i = refs1.Count; i < refs2.Count; i++) r.AppendAdded(i, refs2[i], refs2[i].Value!.Title);
        return r.Empty ? null : r;
    }

    internal SubschemasDiff Combine(SubschemasDiff other) => new()
    {
        Added = [.. Added, .. other.Added],
        Deleted = [.. Deleted, .. other.Deleted],
        Modified = [.. Modified, .. other.Modified],
    };

    internal static SubschemasDiff GetInlineDiff(Unroller u, List<Ref<Schema>> refs1, List<Ref<Schema>> refs2)
    {
        var addedIdx = NonContainedInline(u, refs2, refs1, true);
        var deletedIdx = NonContainedInline(u, refs1, refs2, false);
        var (added2, deleted2, modified) = CompareByTitle(u, addedIdx, deletedIdx, refs1, refs2);
        addedIdx = added2; deletedIdx = deleted2;
        if (addedIdx.Count == 1 && deletedIdx.Count == 1 && refs1[deletedIdx[0]].Value!.Title == "" && refs2[addedIdx[0]].Value!.Title == "")
        {
            AddModified(modified, refs1[deletedIdx[0]], refs2[addedIdx[0]], deletedIdx[0], addedIdx[0], u.Diff(refs1[deletedIdx[0]], refs2[addedIdx[0]]));
            addedIdx = []; deletedIdx = [];
        }
        var r = new SubschemasDiff { Modified = modified };
        foreach (var i in addedIdx) r.Added.Add(new Subschema(i, "", refs2[i].Value!.Title));
        foreach (var i in deletedIdx) r.Deleted.Add(new Subschema(i, "", refs1[i].Value!.Title));
        return r;
    }

    private static (List<int>, List<int>, List<ModifiedSubschema>) CompareByTitle(Unroller u, List<int> addedIdx, List<int> deletedIdx, List<Ref<Schema>> refs1, List<Ref<Schema>> refs2)
    {
        var addedMatched = new Dictionary<int, int>();
        var deletedMatched = new Dictionary<int, int>();
        var matchedTitles = new HashSet<string>(StringComparer.Ordinal) { "" };
        foreach (var a in addedIdx)
        {
            var title = refs2[a].Value!.Title;
            if (matchedTitles.Contains(title)) continue;
            foreach (var d in deletedIdx)
                if (title == refs1[d].Value!.Title) { addedMatched[a] = d; deletedMatched[d] = a; matchedTitles.Add(title); break; }
        }
        var modified = new List<ModifiedSubschema>();
        foreach (var a in addedIdx)
        {
            if (!addedMatched.TryGetValue(a, out var d)) continue;
            AddModified(modified, refs1[d], refs2[a], d, a, u.Diff(refs1[d], refs2[a]));
        }
        return (addedIdx.Where(i => !addedMatched.ContainsKey(i)).ToList(), deletedIdx.Where(i => !deletedMatched.ContainsKey(i)).ToList(), modified);
    }

    private static List<int> NonContainedInline(Unroller u, List<Ref<Schema>> refs1, List<Ref<Schema>> refs2, bool reversed)
    {
        var notContained = new List<int>();
        var matched = new HashSet<int>();
        for (int i1 = 0; i1 < refs1.Count; i1++)
        {
            var r1 = refs1[i1];
            if (!IsInline(r1)) continue;
            int found = -1;
            for (int i2 = 0; i2 < refs2.Count; i2++)
            {
                var r2 = refs2[i2];
                if (!IsInline(r2) || matched.Contains(i2)) continue;
                var (b, r) = reversed ? (r2, r1) : (r1, r2);
                if (u.Identical(b, r)) { found = i2; break; }
            }
            if (found < 0) notContained.Add(i1); else matched.Add(found);
        }
        return notContained;
    }

    internal static SubschemasDiff ReconcileInlineRefRefactors(Unroller u, SubschemasDiff combined, List<Ref<Schema>> refs1, List<Ref<Schema>> refs2)
    {
        if (!u.Config.MatchInlineRefs || combined.Empty) return combined;
        var matchedAdded = new HashSet<int>();
        var matchedDeleted = new HashSet<int>();
        for (int di = 0; di < combined.Deleted.Count; di++)
        {
            var ds = combined.Deleted[di];
            if (ds.Index < 0 || ds.Index >= refs1.Count) continue;
            var dref = refs1[ds.Index];
            for (int ai = 0; ai < combined.Added.Count; ai++)
            {
                if (matchedAdded.Contains(ai)) continue;
                var asub = combined.Added[ai];
                if (asub.Index < 0 || asub.Index >= refs2.Count) continue;
                var aref = refs2[asub.Index];
                if (!IsInlineRefactorBoundary(dref, aref)) continue;
                if (!u.Equivalent(dref, aref)) continue;
                matchedAdded.Add(ai); matchedDeleted.Add(di);
                break;
            }
        }
        if (matchedAdded.Count == 0) return combined;
        var r = new SubschemasDiff { Modified = combined.Modified };
        for (int i = 0; i < combined.Added.Count; i++) if (!matchedAdded.Contains(i)) r.Added.Add(combined.Added[i]);
        for (int i = 0; i < combined.Deleted.Count; i++) if (!matchedDeleted.Contains(i)) r.Deleted.Add(combined.Deleted[i]);
        return r;
    }
}

// ====================================================================== graph & unroller

internal sealed class SchemaGraph
{
    public readonly Dictionary<(Schema, Schema), SchemaDiff> Nodes = new(RefPairComparer<Schema>.Instance);
    public int Depth;
    public readonly Dictionary<SchemaDiff, List<SchemaDiff>> Candidates = new(ReferenceEqualityComparer.Instance);
    public readonly Dictionary<SchemaDiff, int> Component = new(ReferenceEqualityComparer.Instance);
    public int Components;
    public readonly Dictionary<SchemaDiff, Dictionary<string, SchemaDiff?>> Unrolled = new(ReferenceEqualityComparer.Instance);
    private int _nextId;

    private static readonly Ref<Schema> EmptyForTrue = new(new Schema());

    public static Ref<Schema>? TrueSchemaAsEmpty(Ref<Schema>? r) =>
        r is null || r.Value is null || r.Value.Always is not true ? r : EmptyForTrue;

    public SchemaDiff GetNode(DiffConfig config, DiffState state, Ref<Schema> s1, Ref<Schema> s2)
    {
        var pair = (s1.Value!, s2.Value!);
        if (Nodes.TryGetValue(pair, out var existing)) return existing;
        var node = new SchemaDiff { Id = ++_nextId };
        Nodes[pair] = node;
        Depth++;
        try
        {
            var diff = SchemaDiff.GetInternal(config, state, s1, s2)!;
            CopyInto(diff, node);
            node.BaseComponent = SchemaDiff.ComponentName(state.BaseComponents, s1);
            node.RevisionComponent = SchemaDiff.ComponentName(state.RevisionComponents, s2);
            Candidates[node] = GetCandidates(config, state, node);
        }
        catch
        {
            Depth--;
            Nodes.Remove(pair);
            throw;
        }
        Depth--;
        return node;
    }

    private static void CopyInto(SchemaDiff src, SchemaDiff dst)
    {
        int id = dst.Id;
        // copy all fields via memberwise clone semantics
        var clone = src.Clone();
        foreach (var f in Fields) f(clone, dst);
        dst.Id = id;
    }

    // field copiers (AOT-friendly, no reflection)
    private static readonly Action<SchemaDiff, SchemaDiff>[] Fields =
    [
        (s, d) => { d.SchemaAdded = s.SchemaAdded; d.SchemaDeleted = s.SchemaDeleted; d.ExtensionsDiff = s.ExtensionsDiff; d.OneOfDiff = s.OneOfDiff; d.AnyOfDiff = s.AnyOfDiff; d.AllOfDiff = s.AllOfDiff; d.NotDiff = s.NotDiff; d.TypeDiff = s.TypeDiff; },
        (s, d) => { d.TitleDiff = s.TitleDiff; d.FormatDiff = s.FormatDiff; d.DescriptionDiff = s.DescriptionDiff; d.EnumDiff = s.EnumDiff; d.DefaultDiff = s.DefaultDiff; d.ExampleDiff = s.ExampleDiff; d.ExternalDocsDiff = s.ExternalDocsDiff; },
        (s, d) => { d.AdditionalPropertiesAllowedDiff = s.AdditionalPropertiesAllowedDiff; d.UniqueItemsDiff = s.UniqueItemsDiff; d.ExclusiveMinDiff = s.ExclusiveMinDiff; d.ExclusiveMaxDiff = s.ExclusiveMaxDiff; d.NullableDiff = s.NullableDiff; d.ReadOnlyDiff = s.ReadOnlyDiff; d.WriteOnlyDiff = s.WriteOnlyDiff; },
        (s, d) => { d.AllowEmptyValueDiff = s.AllowEmptyValueDiff; d.XMLDiff = s.XMLDiff; d.DeprecatedDiff = s.DeprecatedDiff; d.MinDiff = s.MinDiff; d.MaxDiff = s.MaxDiff; d.MultipleOfDiff = s.MultipleOfDiff; d.MinLengthDiff = s.MinLengthDiff; d.MaxLengthDiff = s.MaxLengthDiff; },
        (s, d) => { d.PatternDiff = s.PatternDiff; d.MinItemsDiff = s.MinItemsDiff; d.MaxItemsDiff = s.MaxItemsDiff; d.ItemsDiff = s.ItemsDiff; d.RequiredDiff = s.RequiredDiff; d.PropertiesDiff = s.PropertiesDiff; d.MinPropsDiff = s.MinPropsDiff; d.MaxPropsDiff = s.MaxPropsDiff; },
        (s, d) => { d.AdditionalPropertiesDiff = s.AdditionalPropertiesDiff; d.DiscriminatorDiff = s.DiscriminatorDiff; d.AlwaysDiff = s.AlwaysDiff; d.ConstDiff = s.ConstDiff; d.ExamplesDiff = s.ExamplesDiff; d.PrefixItemsDiff = s.PrefixItemsDiff; d.ContainsDiff = s.ContainsDiff; },
        (s, d) => { d.MinContainsDiff = s.MinContainsDiff; d.MaxContainsDiff = s.MaxContainsDiff; d.PatternPropertiesDiff = s.PatternPropertiesDiff; d.DependentSchemasDiff = s.DependentSchemasDiff; d.PropertyNamesDiff = s.PropertyNamesDiff; },
        (s, d) => { d.UnevaluatedItemsAllowedDiff = s.UnevaluatedItemsAllowedDiff; d.UnevaluatedItemsDiff = s.UnevaluatedItemsDiff; d.UnevaluatedPropertiesAllowedDiff = s.UnevaluatedPropertiesAllowedDiff; d.UnevaluatedPropertiesDiff = s.UnevaluatedPropertiesDiff; },
        (s, d) => { d.IfDiff = s.IfDiff; d.ThenDiff = s.ThenDiff; d.ElseDiff = s.ElseDiff; d.DependentRequiredDiff = s.DependentRequiredDiff; d.SchemaIDDiff = s.SchemaIDDiff; d.AnchorDiff = s.AnchorDiff; d.DynamicRefDiff = s.DynamicRefDiff; },
        (s, d) => { d.DynamicAnchorDiff = s.DynamicAnchorDiff; d.ContentMediaTypeDiff = s.ContentMediaTypeDiff; d.ContentEncodingDiff = s.ContentEncodingDiff; d.ContentSchemaDiff = s.ContentSchemaDiff; d.DefsDiff = s.DefsDiff; },
        (s, d) => { d.SchemaDialectDiff = s.SchemaDialectDiff; d.CommentDiff = s.CommentDiff; d.ListOfTypesDiff = s.ListOfTypesDiff; d.OneOfWrappingDiff = s.OneOfWrappingDiff; d.NullableWrappingDiff = s.NullableWrappingDiff; },
        (s, d) => { d.Base = s.Base; d.Revision = s.Revision; d.BaseComponent = s.BaseComponent; d.RevisionComponent = s.RevisionComponent; },
    ];

    private List<SchemaDiff> GetCandidates(DiffConfig config, DiffState state, SchemaDiff node)
    {
        var pairs = new List<(Ref<Schema>?, Ref<Schema>?)>();
        for (int i = 0; i < SchemaDiff.ChildCount; i++)
        {
            if (node.GetChild(i) is not SubschemasDiff { Pending: true } sub) continue;
            foreach (var r1 in sub.BaseRefs!)
                foreach (var r2 in sub.RevisionRefs!)
                {
                    if (SubschemasDiff.IsInline(r1) && SubschemasDiff.IsInline(r2)) pairs.Add((r1, r2));
                    if (SubschemasDiff.IsInlineRefactorBoundary(r1, r2)) pairs.Add((TrueSchemaAsEmpty(r1), TrueSchemaAsEmpty(r2)));
                }
        }
        foreach (var alt in node.Revision!.OneOf) pairs.Add((TrueSchemaAsEmpty(new Ref<Schema>(node.Base!)), TrueSchemaAsEmpty(alt)));
        foreach (var alt in node.Base!.OneOf) pairs.Add((TrueSchemaAsEmpty(alt), TrueSchemaAsEmpty(new Ref<Schema>(node.Revision))));
        var candidates = new List<SchemaDiff>();
        foreach (var (a, b) in pairs)
        {
            if (a?.Value is null || b?.Value is null) continue;
            candidates.Add(GetNode(config, state, a, b));
        }
        return candidates;
    }

    public List<SchemaDiff> Children(SchemaDiff node)
    {
        var children = Candidates.TryGetValue(node, out var c) ? new List<SchemaDiff>(c) : new List<SchemaDiff>();
        for (int i = 0; i < SchemaDiff.ChildCount; i++)
        {
            switch (node.GetChild(i))
            {
                case SchemaDiff sd: children.Add(sd); break;
                case SchemasDiff ss: foreach (var v in ss.Modified.Values) children.Add(v); break;
                case SubschemasDiff sub: foreach (var m in sub.Modified) children.Add(m.Diff); break;
            }
        }
        return children;
    }

    /// <summary>Tarjan's SCC from root, iterative to avoid deep recursion.</summary>
    public void AssignComponents(SchemaDiff root)
    {
        var index = new Dictionary<SchemaDiff, int>(ReferenceEqualityComparer.Instance);
        var low = new Dictionary<SchemaDiff, int>(ReferenceEqualityComparer.Instance);
        var onStack = new HashSet<SchemaDiff>(ReferenceEqualityComparer.Instance);
        var stack = new List<SchemaDiff>();
        var work = new Stack<(SchemaDiff node, List<SchemaDiff> children, int next)>();

        void Push(SchemaDiff n)
        {
            index[n] = index.Count;
            low[n] = index[n];
            stack.Add(n);
            onStack.Add(n);
            work.Push((n, Children(n), 0));
        }

        Push(root);
        while (work.Count > 0)
        {
            var (node, children, next) = work.Pop();
            if (next < children.Count)
            {
                work.Push((node, children, next + 1));
                var child = children[next];
                if (Component.ContainsKey(child)) continue;
                if (!index.ContainsKey(child)) { Push(child); continue; }
                if (onStack.Contains(child)) low[node] = Math.Min(low[node], index[child]);
                continue;
            }
            // finished node
            if (low[node] == index[node])
            {
                Components++;
                while (true)
                {
                    var top = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    onStack.Remove(top);
                    Component[top] = Components;
                    if (ReferenceEquals(top, node)) break;
                }
            }
            if (work.Count > 0)
            {
                var parent = work.Peek().node;
                low[parent] = Math.Min(low[parent], low[node]);
            }
        }
    }
}

internal sealed class Unroller
{
    public readonly DiffConfig Config;
    private readonly DiffState _state;
    private List<SchemaDiff> _stack;
    private readonly Dictionary<SchemaDiff, int> _path;
    private readonly HashSet<SchemaDiff> _visited = new(ReferenceEqualityComparer.Instance);
    public Exception? Error;

    public Unroller(DiffConfig config, DiffState state)
    {
        Config = config; _state = state;
        _stack = [];
        _path = new(ReferenceEqualityComparer.Instance);
    }

    private Unroller(DiffConfig config, DiffState state, List<SchemaDiff> stack, Dictionary<SchemaDiff, int> path)
    {
        Config = config; _state = state; _stack = stack; _path = path;
    }

    public SchemaDiff? Unroll(SchemaDiff? node)
    {
        if (node is null) return null;
        if (_path.ContainsKey(node)) return null;
        var key = CutKey(node);
        if (key != "" && _visited.Count > 0)
        {
            if (_visited.Contains(node)) return null;
            return Expand(node);
        }
        return Walk(node, key);
    }

    private SchemaDiff? Walk(SchemaDiff node, string key)
    {
        var graph = _state.Graph;
        if (graph.Unrolled.TryGetValue(node, out var byKey) && byKey.TryGetValue(key, out var cached)) return cached;
        var walk = new Unroller(Config, _state, _stack, _path);
        var unrolled = walk.Expand(node);
        _stack = walk._stack;
        if (walk.Error is not null) Fail(walk.Error);
        if (!graph.Unrolled.TryGetValue(node, out byKey)) graph.Unrolled[node] = byKey = new(StringComparer.Ordinal);
        byKey[key] = unrolled;
        return unrolled;
    }

    private SchemaDiff? Expand(SchemaDiff node)
    {
        _visited.Add(node);
        _path[node] = _stack.Count;
        _stack.Add(node);
        var unrolled = Copy(node);
        _stack.RemoveAt(_stack.Count - 1);
        _path.Remove(node);
        return unrolled;
    }

    private string CutKey(SchemaDiff node)
    {
        var graph = _state.Graph;
        if (!graph.Component.TryGetValue(node, out var component))
        {
            graph.AssignComponents(node);
            component = graph.Component[node];
        }
        List<int>? cut = null;
        foreach (var a in _stack)
            if (graph.Component.TryGetValue(a, out var c) && c == component) (cut ??= []).Add(a.Id);
        if (cut is null) return "";
        cut.Sort();
        var sb = new StringBuilder(cut.Count * 4);
        foreach (var id in cut) sb.Append(id).Append(',');
        return sb.ToString();
    }

    private SchemaDiff? Copy(SchemaDiff node)
    {
        var u = node.Clone();
        for (int i = 0; i < SchemaDiff.ChildCount; i++)
        {
            switch (node.GetChild(i))
            {
                case SchemaDiff sd: u.SetChild(i, Unroll(sd)); break;
                case SchemasDiff ss: u.SetChild(i, Schemas(ss)); break;
                case SubschemasDiff sub: u.SetChild(i, Subschemas(sub)); break;
            }
        }
        u.OneOfWrappingDiff = OneOfWrappingDiff.Get(this, node.Base, node.Revision);
        u.NullableWrappingDiff = NullableWrappingDiff.Get(this, node.Base, node.Revision);
        return u.Empty ? null : u;
    }

    private SchemasDiff? Schemas(SchemasDiff diff)
    {
        var u = new SchemasDiff { Added = diff.Added, Deleted = diff.Deleted, Base = diff.Base, Revision = diff.Revision };
        foreach (var (name, child) in diff.Modified)
            if (Unroll(child) is { } c) u.Modified[name] = c;
        return u.Empty ? null : u;
    }

    private SubschemasDiff? Subschemas(SubschemasDiff diff)
    {
        var u = new SubschemasDiff { Added = [.. diff.Added], Deleted = [.. diff.Deleted] };
        foreach (var m in diff.Modified)
            if (Unroll(m.Diff) is { } c) u.Modified.Add(new ModifiedSubschema { Base = m.Base, Revision = m.Revision, Diff = c });
        if (diff.Pending)
        {
            SubschemasDiff inline;
            try { inline = SubschemasDiff.GetInlineDiff(this, diff.BaseRefs!, diff.RevisionRefs!); }
            catch (Exception e) { Fail(e); return null; }
            u = SubschemasDiff.ReconcileInlineRefRefactors(this, u.Combine(inline), diff.BaseRefs!, diff.RevisionRefs!);
        }
        return u.Empty ? null : u;
    }

    private void Fail(Exception e) => Error ??= e;

    public SchemaDiff? Diff(Ref<Schema>? r1, Ref<Schema>? r2)
    {
        if (r1 is null || r2 is null || r1.Value is null || r2.Value is null) return SchemaDiff.GetInternal(Config, _state, r1, r2);
        var node = _state.Graph.GetNode(Config, _state, r1, r2);
        if (_path.ContainsKey(node)) return null;
        return Walk(node, CutKey(node));
    }

    public bool Identical(Ref<Schema> r1, Ref<Schema> r2) => Diff(r1, r2) is null;

    public bool Equivalent(Ref<Schema>? r1, Ref<Schema>? r2)
    {
        SchemaDiff? d;
        try { d = Diff(SchemaGraph.TrueSchemaAsEmpty(r1), SchemaGraph.TrueSchemaAsEmpty(r2)); }
        catch (Exception e) { Fail(e); return false; }
        return !SchemaEquivalence.HasValidationChanges(d);
    }
}

public static class SchemaEquivalence
{
    public static bool RefsValidationEquivalent(DiffConfig config, Ref<Schema>? r1, Ref<Schema>? r2) =>
        new Unroller(config, new DiffState()).Equivalent(r1, r2);

    internal static bool HasValidationChanges(SchemaDiff? d) => WithoutAnnotations(d) is { Empty: false };

    internal static SchemaDiff? WithoutAnnotations(SchemaDiff? d)
    {
        if (d is null) return null;
        var r = d.Clone();
        r.TitleDiff = null; r.DescriptionDiff = null; r.DefaultDiff = null; r.ExampleDiff = null; r.ExternalDocsDiff = null; r.ExamplesDiff = null; r.CommentDiff = null;
        r.OneOfDiff = Sub(d.OneOfDiff); r.AnyOfDiff = Sub(d.AnyOfDiff); r.AllOfDiff = Sub(d.AllOfDiff);
        r.NotDiff = WithoutAnnotations(d.NotDiff); r.ItemsDiff = WithoutAnnotations(d.ItemsDiff);
        r.PropertiesDiff = Map(d.PropertiesDiff); r.AdditionalPropertiesDiff = WithoutAnnotations(d.AdditionalPropertiesDiff);
        r.PrefixItemsDiff = Sub(d.PrefixItemsDiff); r.ContainsDiff = WithoutAnnotations(d.ContainsDiff);
        r.PatternPropertiesDiff = Map(d.PatternPropertiesDiff); r.DependentSchemasDiff = Map(d.DependentSchemasDiff);
        r.PropertyNamesDiff = WithoutAnnotations(d.PropertyNamesDiff); r.UnevaluatedItemsDiff = WithoutAnnotations(d.UnevaluatedItemsDiff);
        r.UnevaluatedPropertiesDiff = WithoutAnnotations(d.UnevaluatedPropertiesDiff);
        r.IfDiff = WithoutAnnotations(d.IfDiff); r.ThenDiff = WithoutAnnotations(d.ThenDiff); r.ElseDiff = WithoutAnnotations(d.ElseDiff);
        r.ContentSchemaDiff = WithoutAnnotations(d.ContentSchemaDiff); r.DefsDiff = Map(d.DefsDiff);
        return r.Empty ? null : r;
    }

    private static SubschemasDiff? Sub(SubschemasDiff? d)
    {
        if (d is null) return null;
        var r = new SubschemasDiff { Added = d.Added, Deleted = d.Deleted };
        foreach (var m in d.Modified)
            if (WithoutAnnotations(m.Diff) is { } sd) r.Modified.Add(new ModifiedSubschema { Base = m.Base, Revision = m.Revision, Diff = sd });
        return r.Empty ? null : r;
    }

    private static SchemasDiff? Map(SchemasDiff? d)
    {
        if (d is null) return null;
        var r = new SchemasDiff { Added = d.Added, Deleted = d.Deleted, Base = d.Base, Revision = d.Revision };
        foreach (var (k, v) in d.Modified) if (WithoutAnnotations(v) is { } sd) r.Modified[k] = sd;
        return r.Empty ? null : r;
    }

    public static bool PrefixItemsValidationEquivalent(DiffConfig config, Schema? b, Schema? r)
    {
        if (b is null || r is null) return false;
        int n = Math.Max(b.PrefixItems.Count, r.PrefixItems.Count);
        for (int i = 0; i < n; i++)
            if (!RefsValidationEquivalent(config, ItemAt(b, i), ItemAt(r, i))) return false;
        return true;
    }

    private static Ref<Schema> ItemAt(Schema s, int i) => i < s.PrefixItems.Count ? s.PrefixItems[i] : s.Items ?? new Ref<Schema>(new Schema());
}
