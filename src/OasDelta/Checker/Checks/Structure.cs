using OasDelta.Diff;

namespace OasDelta.Checker;

// Structural (JSON Schema 2020-12 applicator) checks shared by the request and response sides.
// Every ID has the shape "request-..." / "response-..."; the response variants append the status code.
public static partial class Checks
{
    private static string Side(bool response, string suffix) => (response ? "response-" : "request-") + suffix;

    private static List<Change> Bodies(bool response, OpenApiDiff d, Sources s, CheckerConfig c, Action<MediaTypeInfo, List<Change>> f) =>
        response ? ResponseBodies(d, s, c, f) : RequestBodies(d, s, c, f);

    private static List<object?> Args(MediaTypeInfo i, bool response, params object?[] a)
    {
        var l = new List<object?>(a.Length + 1);
        l.AddRange(a);
        if (response) l.Add(i.ResponseStatus);
        return l;
    }

    // ---- added/removed sub-schema keywords (propertyNames, unevaluated*, contains, if/then/else, contentSchema) ----

    private static void SchemaAddedRemoved(MediaTypeInfo i, List<Change> r, bool resp, SchemaDiff? sd, string keyword, string bodyIdStem)
    {
        if (sd is null) return;
        var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, i.SchemaDiff, keyword);
        if (sd.SchemaAdded) r.Add(i.NewChange(Side(resp, $"body-{bodyIdStem}-added"), Args(i, resp), "").WithSources(null, rv));
        if (sd.SchemaDeleted) r.Add(i.NewChange(Side(resp, $"body-{bodyIdStem}-removed"), Args(i, resp), "").WithSources(b, null));
    }

    private static void PropSchemaAddedRemoved(MediaTypeInfo i, PropertyInfo p, List<Change> r, bool resp, SchemaDiff? sd, string keyword, string idStem)
    {
        if (sd is null) return;
        var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, keyword);
        if (sd.SchemaAdded) r.Add(p.NewChange(Side(resp, $"property-{idStem}-added"), Args(i, resp, p.FullName), "").WithSources(null, rv));
        if (sd.SchemaDeleted) r.Add(p.NewChange(Side(resp, $"property-{idStem}-removed"), Args(i, resp, p.FullName), "").WithSources(b, null));
    }

    private static List<Change> PropertyNames(bool resp, OpenApiDiff d, Sources s, CheckerConfig c) => Bodies(resp, d, s, c, (i, r) =>
    {
        SchemaAddedRemoved(i, r, resp, i.SchemaDiff.PropertyNamesDiff, "propertyNames", "property-names");
        i.WalkProperties(p => PropSchemaAddedRemoved(i, p, r, resp, p.PropertyDiff.PropertyNamesDiff, "propertyNames", "property-names"));
    });
    public static List<Change> RequestPropertyPropertyNamesUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => PropertyNames(false, d, s, c);
    public static List<Change> ResponsePropertyPropertyNamesUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => PropertyNames(true, d, s, c);

    private static List<Change> Unevaluated(bool resp, OpenApiDiff d, Sources s, CheckerConfig c) => Bodies(resp, d, s, c, (i, r) =>
    {
        SchemaAddedRemoved(i, r, resp, i.SchemaDiff.UnevaluatedItemsDiff, "unevaluatedItems", "unevaluated-items");
        SchemaAddedRemoved(i, r, resp, i.SchemaDiff.UnevaluatedPropertiesDiff, "unevaluatedProperties", "unevaluated-properties");
        i.WalkProperties(p =>
        {
            PropSchemaAddedRemoved(i, p, r, resp, p.PropertyDiff.UnevaluatedItemsDiff, "unevaluatedItems", "unevaluated-items");
            PropSchemaAddedRemoved(i, p, r, resp, p.PropertyDiff.UnevaluatedPropertiesDiff, "unevaluatedProperties", "unevaluated-properties");
        });
    });
    public static List<Change> RequestPropertyUnevaluatedUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Unevaluated(false, d, s, c);
    public static List<Change> ResponsePropertyUnevaluatedUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Unevaluated(true, d, s, c);

    private static List<Change> IfThenElse(bool resp, OpenApiDiff d, Sources s, CheckerConfig c) => Bodies(resp, d, s, c, (i, r) =>
    {
        SchemaAddedRemoved(i, r, resp, i.SchemaDiff.IfDiff, "if", "if");
        SchemaAddedRemoved(i, r, resp, i.SchemaDiff.ThenDiff, "then", "then");
        SchemaAddedRemoved(i, r, resp, i.SchemaDiff.ElseDiff, "else", "else");
        i.WalkProperties(p =>
        {
            PropSchemaAddedRemoved(i, p, r, resp, p.PropertyDiff.IfDiff, "if", "if");
            PropSchemaAddedRemoved(i, p, r, resp, p.PropertyDiff.ThenDiff, "then", "then");
            PropSchemaAddedRemoved(i, p, r, resp, p.PropertyDiff.ElseDiff, "else", "else");
        });
    });
    public static List<Change> RequestPropertyIfUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => IfThenElse(false, d, s, c);
    public static List<Change> ResponsePropertyIfUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => IfThenElse(true, d, s, c);

    // ---- contains / minContains / maxContains ----

    private static void ContainsBound(MediaTypeInfo i, List<Change> r, bool resp, PropertyInfo? p, ValueDiff? vd, string keyword, string stem)
    {
        if (vd is null || vd.From is null || vd.To is null) return;
        var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p is null ? i.SchemaDiff : p.PropertyDiff, keyword);
        string? dir = Walkers.IsIncreasedValue(vd) ? "increased" : Walkers.IsDecreasedValue(vd) ? "decreased" : null;
        if (dir is null) return;
        if (p is null) r.Add(i.NewChange(Side(resp, $"body-{stem}-{dir}"), Args(i, resp, vd.From, vd.To), "").WithSources(b, rv));
        else r.Add(p.NewChange(Side(resp, $"property-{stem}-{dir}"), Args(i, resp, p.FullName, vd.From, vd.To), "").WithSources(b, rv));
    }

    private static List<Change> Contains(bool resp, OpenApiDiff d, Sources s, CheckerConfig c) => Bodies(resp, d, s, c, (i, r) =>
    {
        SchemaAddedRemoved(i, r, resp, i.SchemaDiff.ContainsDiff, "contains", "contains");
        ContainsBound(i, r, resp, null, i.SchemaDiff.MinContainsDiff, "minContains", "min-contains");
        ContainsBound(i, r, resp, null, i.SchemaDiff.MaxContainsDiff, "maxContains", "max-contains");
        i.WalkProperties(p =>
        {
            PropSchemaAddedRemoved(i, p, r, resp, p.PropertyDiff.ContainsDiff, "contains", "contains");
            ContainsBound(i, r, resp, p, p.PropertyDiff.MinContainsDiff, "minContains", "min-contains");
            ContainsBound(i, r, resp, p, p.PropertyDiff.MaxContainsDiff, "maxContains", "max-contains");
        });
    });
    public static List<Change> RequestPropertyContainsUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Contains(false, d, s, c);
    public static List<Change> ResponsePropertyContainsUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Contains(true, d, s, c);

    // ---- patternProperties / dependentSchemas (map of schemas) ----

    private static void SchemaMapAddedRemoved(MediaTypeInfo i, List<Change> r, bool resp, PropertyInfo? p, SchemasDiff? md, string stem)
    {
        if (md is null) return;
        foreach (var name in md.Added)
        {
            var rv = Loc.SchemaMapItem(i.Sources, i.OperationItem.Revision, md.Revision, name);
            r.Add(p is null
                ? i.NewChange(Side(resp, $"body-{stem}-added"), Args(i, resp, name), "").WithSources(null, rv)
                : p.NewChange(Side(resp, $"property-{stem}-added"), Args(i, resp, name, p.FullName), "").WithSources(null, rv));
        }
        foreach (var name in md.Deleted)
        {
            var b = Loc.SchemaMapItem(i.Sources, i.OperationItem.Base, md.Base, name);
            r.Add(p is null
                ? i.NewChange(Side(resp, $"body-{stem}-removed"), Args(i, resp, name), "").WithSources(b, null)
                : p.NewChange(Side(resp, $"property-{stem}-removed"), Args(i, resp, name, p.FullName), "").WithSources(b, null));
        }
    }

    private static List<Change> PatternProperties(bool resp, OpenApiDiff d, Sources s, CheckerConfig c) => Bodies(resp, d, s, c, (i, r) =>
    {
        SchemaMapAddedRemoved(i, r, resp, null, i.SchemaDiff.PatternPropertiesDiff, "pattern-property");
        i.WalkProperties(p => SchemaMapAddedRemoved(i, r, resp, p, p.PropertyDiff.PatternPropertiesDiff, "pattern-property"));
    });
    public static List<Change> RequestPropertyPatternPropertiesUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => PatternProperties(false, d, s, c);
    public static List<Change> ResponsePropertyPatternPropertiesUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => PatternProperties(true, d, s, c);

    private static List<Change> DependentSchemas(bool resp, OpenApiDiff d, Sources s, CheckerConfig c) => Bodies(resp, d, s, c, (i, r) =>
    {
        SchemaMapAddedRemoved(i, r, resp, null, i.SchemaDiff.DependentSchemasDiff, "dependent-schema");
        i.WalkProperties(p => SchemaMapAddedRemoved(i, r, resp, p, p.PropertyDiff.DependentSchemasDiff, "dependent-schema"));
    });
    public static List<Change> RequestPropertyDependentSchemasUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => DependentSchemas(false, d, s, c);
    public static List<Change> ResponsePropertyDependentSchemasUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => DependentSchemas(true, d, s, c);

    // ---- dependentRequired: upstream places the response status inconsistently, reproduced exactly ----

    private static List<Change> DependentRequired(bool resp, OpenApiDiff d, Sources s, CheckerConfig c) => Bodies(resp, d, s, c, (i, r) =>
    {
        void Emit(PropertyInfo? p, DependentRequiredDiff? dr)
        {
            if (dr is null) return;
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p is null ? i.SchemaDiff : p.PropertyDiff, "dependentRequired");
            string scope = p is null ? "body" : "property";
            Change New(string id, List<object?> a) => p is null ? i.NewChange(id, a, "") : p.NewChange(id, a, "");
            List<object?> Lead() => p is null ? [] : [p.FullName];
            List<object?> AddedRemoved(string key, List<string> values)
            {
                var a = Lead();
                if (resp) a.Add(i.ResponseStatus);
                a.Add(key); a.Add(string.Join(", ", values));
                return a;
            }
            foreach (var (key, values) in dr.Added)
                r.Add(New(Side(resp, $"{scope}-dependent-required-added"), AddedRemoved(key, values)).WithSources(null, rv));
            foreach (var (key, values) in dr.Deleted)
                r.Add(New(Side(resp, $"{scope}-dependent-required-removed"), AddedRemoved(key, values)).WithSources(b, null));
            foreach (var (key, sd) in dr.Modified)
            {
                var a = Lead();
                a.Add(key);
                if (resp) a.Add(i.ResponseStatus);
                a.Add(Walkers.FormatDependentRequiredModification(sd));
                r.Add(New(Side(resp, $"{scope}-dependent-required-changed"), a).WithSources(b, rv));
            }
        }
        Emit(null, i.SchemaDiff.DependentRequiredDiff);
        i.WalkProperties(p => Emit(p, p.PropertyDiff.DependentRequiredDiff));
    });
    public static List<Change> RequestPropertyDependentRequiredChangedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => DependentRequired(false, d, s, c);
    public static List<Change> ResponsePropertyDependentRequiredChangedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => DependentRequired(true, d, s, c);

    // ---- contentSchema / contentMediaType / contentEncoding ----

    private static List<Change> Content(bool resp, OpenApiDiff d, Sources s, CheckerConfig c) => Bodies(resp, d, s, c, (i, r) =>
    {
        void Changed(PropertyInfo? p, ValueDiff? vd, string keyword, string stem)
        {
            if (vd is null) return;
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p is null ? i.SchemaDiff : p.PropertyDiff, keyword);
            r.Add(p is null
                ? i.NewChange(Side(resp, $"body-{stem}-changed"), Args(i, resp, vd.From, vd.To), "").WithSources(b, rv)
                : p.NewChange(Side(resp, $"property-{stem}-changed"), Args(i, resp, p.FullName, vd.From, vd.To), "").WithSources(b, rv));
        }
        SchemaAddedRemoved(i, r, resp, i.SchemaDiff.ContentSchemaDiff, "contentSchema", "content-schema");
        Changed(null, i.SchemaDiff.ContentMediaTypeDiff, "contentMediaType", "content-media-type");
        Changed(null, i.SchemaDiff.ContentEncodingDiff, "contentEncoding", "content-encoding");
        i.WalkProperties(p =>
        {
            PropSchemaAddedRemoved(i, p, r, resp, p.PropertyDiff.ContentSchemaDiff, "contentSchema", "content-schema");
            Changed(p, p.PropertyDiff.ContentMediaTypeDiff, "contentMediaType", "content-media-type");
            Changed(p, p.PropertyDiff.ContentEncodingDiff, "contentEncoding", "content-encoding");
        });
    });
    public static List<Change> RequestPropertyContentUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Content(false, d, s, c);
    public static List<Change> ResponsePropertyContentUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Content(true, d, s, c);

    // ---- prefixItems ----

    private static List<Change> PrefixItems(bool resp, OpenApiDiff d, Sources s, CheckerConfig c) => Bodies(resp, d, s, c, (i, r) =>
    {
        const string comment = "prefix-items-changed-warning-comment";
        if (Walkers.PrefixItemsChangedContract(i.SchemaDiff))
        {
            var pd = i.SchemaDiff.PrefixItemsDiff!;
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, i.SchemaDiff, "prefixItems");
            if (pd.Added.Count > 0) r.Add(i.NewChange(Side(resp, "body-prefix-items-added"), Args(i, resp, Subschema.Join(pd.Added)), comment).WithSources(null, rv));
            if (pd.Deleted.Count > 0) r.Add(i.NewChange(Side(resp, "body-prefix-items-removed"), Args(i, resp, Subschema.Join(pd.Deleted)), comment).WithSources(b, null));
        }
        i.WalkProperties(p =>
        {
            if (!Walkers.PrefixItemsChangedContract(p.PropertyDiff)) return;
            var pd = p.PropertyDiff.PrefixItemsDiff!;
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, "prefixItems");
            if (pd.Added.Count > 0) r.Add(p.NewChange(Side(resp, "property-prefix-items-added"), Args(i, resp, Subschema.Join(pd.Added), p.FullName), comment).WithSources(null, rv));
            if (pd.Deleted.Count > 0) r.Add(p.NewChange(Side(resp, "property-prefix-items-removed"), Args(i, resp, Subschema.Join(pd.Deleted), p.FullName), comment).WithSources(b, null));
        });
    });
    public static List<Change> RequestPropertyPrefixItemsUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => PrefixItems(false, d, s, c);
    public static List<Change> ResponsePropertyPrefixItemsUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => PrefixItems(true, d, s, c);

    // ---- allOf / anyOf / oneOf ----

    private static void SubschemaList(MediaTypeInfo i, List<Change> r, bool resp, PropertyInfo? p, SchemaDiff sd, string keyword, string stem, List<Subschema> list, bool added, string suffix)
    {
        if (list.Count == 0) return;
        var (b, rv) = added
            ? Loc.Subschema(i.Sources, i.OperationItem, sd, keyword, -1, list[0].Index)
            : Loc.Subschema(i.Sources, i.OperationItem, sd, keyword, list[0].Index, -1);
        string verb = added ? "added" : "removed";
        var joined = Subschema.Join(list);
        r.Add(p is null
            ? i.NewChange(Side(resp, $"body-{stem}-{verb}{suffix}"), Args(i, resp, joined), "").WithSources(b, rv)
            : p.NewChange(Side(resp, $"property-{stem}-{verb}{suffix}"), Args(i, resp, joined, p.FullName), "").WithSources(b, rv));
    }

    private static List<Change> AllOf(bool resp, OpenApiDiff d, Sources s, CheckerConfig c) => Bodies(resp, d, s, c, (i, r) =>
    {
        void Emit(PropertyInfo? p, SchemaDiff sd)
        {
            if (sd.AllOfDiff is not { } ad) return;
            var (added, annAdded) = Walkers.SplitSubschemasByAnnotationOnly(ad.Added, sd.Revision?.AllOf ?? []);
            SubschemaList(i, r, resp, p, sd, "allOf", "all-of", added, true, "");
            SubschemaList(i, r, resp, p, sd, "allOf", "all-of", annAdded, true, "-annotation-only");
            var (deleted, annDeleted) = Walkers.SplitSubschemasByAnnotationOnly(ad.Deleted, sd.Base?.AllOf ?? []);
            SubschemaList(i, r, resp, p, sd, "allOf", "all-of", deleted, false, "");
            SubschemaList(i, r, resp, p, sd, "allOf", "all-of", annDeleted, false, "-annotation-only");
        }
        Emit(null, i.SchemaDiff);
        i.WalkProperties(p => Emit(p, p.PropertyDiff));
    });
    public static List<Change> RequestPropertyAllOfUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => AllOf(false, d, s, c);
    public static List<Change> ResponsePropertyAllOfUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => AllOf(true, d, s, c);

    private static List<Change> XOf(bool resp, bool one, OpenApiDiff d, Sources s, CheckerConfig c) => Bodies(resp, d, s, c, (i, r) =>
    {
        string keyword = one ? "oneOf" : "anyOf", stem = one ? "one-of" : "any-of";
        void Emit(PropertyInfo? p, SchemaDiff sd)
        {
            if ((one ? sd.OneOfDiff : sd.AnyOfDiff) is not { } xd) return;
            SubschemaList(i, r, resp, p, sd, keyword, stem, xd.Added, true, "");
            SubschemaList(i, r, resp, p, sd, keyword, stem, xd.Deleted, false, "");
        }
        Emit(null, i.SchemaDiff);
        i.WalkProperties(p => Emit(p, p.PropertyDiff));
    });
    public static List<Change> RequestPropertyAnyOfUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => XOf(false, false, d, s, c);
    public static List<Change> ResponsePropertyAnyOfUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => XOf(true, false, d, s, c);
    public static List<Change> RequestPropertyOneOfUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => XOf(false, true, d, s, c);
    public static List<Change> ResponsePropertyOneOfUpdated(OpenApiDiff d, Sources s, CheckerConfig c) => XOf(true, true, d, s, c);
}
