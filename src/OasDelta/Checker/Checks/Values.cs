using OasDelta.Diff;
using OasDelta.Yaml;

namespace OasDelta.Checker;

public static partial class Checks
{
    private static string Comment(string id) => id + "-comment";
    private static string V(object? v) => Fmt.GoV(v);

    public static List<Change> RequestPropertyBecameEnumCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) => i.WalkProperties(p =>
    {
        if (p.PropertyDiff.EnumDiff is not { EnumAdded: true }) return;
        var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, "enum");
        r.Add(p.NewChange(Ids.RequestPropertyBecameEnum, [p.FullName], "").WithSources(b, rv));
    }));

    public static List<Change> RequestPropertyBecameNotEnumCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) => i.WalkProperties(p =>
    {
        if (!Walkers.BecameNotEnum(p.PropertyDiff)) return;
        var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, "enum");
        r.Add(p.NewChange(Ids.RequestPropertyBecameNotEnum, [p.FullName], "").WithSources(b, rv));
    }));

    public static List<Change> ResponsePropertyBecameEnumCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) => i.WalkProperties(p =>
    {
        if (!Walkers.BecameEnum(p.PropertyDiff)) return;
        var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, "enum");
        r.Add(p.NewChange(Ids.ResponsePropertyBecameEnum, [p.FullName, i.ResponseStatus], "").WithSources(b, rv));
    }));

    public static List<Change> ResponsePropertyBecameNotEnumCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) => i.WalkProperties(p =>
    {
        if (!Walkers.BecameNotEnum(p.PropertyDiff)) return;
        var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, "enum");
        r.Add(p.NewChange(Ids.ResponsePropertyBecameNotEnum, [p.FullName, i.ResponseStatus], Comment(Ids.ResponsePropertyBecameNotEnum)).WithSources(b, rv));
    }));

    public static List<Change> RequestPropertyBecameNotNullableCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
    {
        if (Walkers.NullabilityChangeId(i.SchemaDiff, Ids.RequestBodyBecomeNullable, Ids.RequestBodyBecomeNotNullable) is { Length: > 0 } id)
        {
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, i.SchemaDiff, "nullable");
            r.Add(i.NewChange(id, [], "").WithSources(b, rv));
        }
        i.WalkProperties(p =>
        {
            if (Walkers.NullabilityChangeId(p.PropertyDiff, Ids.RequestPropertyBecomeNullable, Ids.RequestPropertyBecomeNotNullable) is not { Length: > 0 } pid) return;
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, "nullable");
            r.Add(p.NewChange(pid, [p.FullName], "").WithSources(b, rv));
        });
    });

    public static List<Change> ResponsePropertyBecameNullableCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
    {
        if (Walkers.NullabilityChangeId(i.SchemaDiff, Ids.ResponseBodyBecameNullable, Ids.ResponseBodyBecameNotNullable) is { Length: > 0 } id)
        {
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, i.SchemaDiff, "nullable");
            r.Add(i.NewChange(id, [], "").WithSources(b, rv));
        }
        i.WalkProperties(p =>
        {
            if (Walkers.NullabilityChangeId(p.PropertyDiff, Ids.ResponsePropertyBecameNullable, Ids.ResponsePropertyBecameNotNullable) is not { Length: > 0 } pid) return;
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, "nullable");
            r.Add(p.NewChange(pid, [p.FullName, i.ResponseStatus], "").WithSources(b, rv));
        });
    });

    // const / default: added, removed or changed; the response variants append the status
    private static void ValueAddedRemovedChanged(MediaTypeInfo i, List<Change> r, Func<SchemaDiff, ValueDiff?> get, string keyword, bool response, bool requireRevision,
        string bodyAdded, string bodyRemoved, string bodyChanged, string propAdded, string propRemoved, string propChanged, bool sourcesBySide)
    {
        List<object?> Tail(List<object?> a) { if (response) a.Add(i.ResponseStatus); return a; }
        if (get(i.SchemaDiff) is { } vd)
        {
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, i.SchemaDiff, keyword);
            if (vd.From is null) r.Add(i.NewChange(bodyAdded, Tail([i.MediaType, vd.To]), "").WithSources(sourcesBySide ? null : b, rv));
            else if (vd.To is null) r.Add(i.NewChange(bodyRemoved, Tail([i.MediaType, vd.From]), "").WithSources(b, sourcesBySide ? null : rv));
            else r.Add(i.NewChange(bodyChanged, Tail([i.MediaType, vd.From, vd.To]), "").WithSources(b, rv));
        }
        i.WalkProperties(p =>
        {
            if (requireRevision && p.PropertyDiff.Revision is null) return;
            if (get(p.PropertyDiff) is not { } vd) return;
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, keyword);
            if (vd.From is null) r.Add(p.NewChange(propAdded, Tail([p.PropertyName, vd.To]), "").WithSources(sourcesBySide ? null : b, rv));
            else if (vd.To is null) r.Add(p.NewChange(propRemoved, Tail([p.PropertyName, vd.From]), "").WithSources(b, sourcesBySide ? null : rv));
            else r.Add(p.NewChange(propChanged, Tail([p.PropertyName, vd.From, vd.To]), "").WithSources(b, rv));
        });
    }

    public static List<Change> RequestPropertyConstChangedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
        ValueAddedRemovedChanged(i, r, x => x.ConstDiff, "const", false, false, Ids.RequestBodyConstAdded, Ids.RequestBodyConstRemoved, Ids.RequestBodyConstChanged,
            Ids.RequestPropertyConstAdded, Ids.RequestPropertyConstRemoved, Ids.RequestPropertyConstChanged, true));

    public static List<Change> ResponsePropertyConstChangedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
        ValueAddedRemovedChanged(i, r, x => x.ConstDiff, "const", true, true, Ids.ResponseBodyConstAdded, Ids.ResponseBodyConstRemoved, Ids.ResponseBodyConstChanged,
            Ids.ResponsePropertyConstAdded, Ids.ResponsePropertyConstRemoved, Ids.ResponsePropertyConstChanged, true));

    public static List<Change> RequestPropertyDefaultValueChangedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
        ValueAddedRemovedChanged(i, r, x => x.DefaultDiff, "default", false, false, Ids.RequestBodyDefaultValueAdded, Ids.RequestBodyDefaultValueRemoved, Ids.RequestBodyDefaultValueChanged,
            Ids.RequestPropertyDefaultValueAdded, Ids.RequestPropertyDefaultValueRemoved, Ids.RequestPropertyDefaultValueChanged, false));

    public static List<Change> ResponsePropertyDefaultValueChangedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
        ValueAddedRemovedChanged(i, r, x => x.DefaultDiff, "default", true, true, Ids.ResponseBodyDefaultValueAdded, Ids.ResponseBodyDefaultValueRemoved, Ids.ResponseBodyDefaultValueChanged,
            Ids.ResponsePropertyDefaultValueAdded, Ids.ResponsePropertyDefaultValueRemoved, Ids.ResponsePropertyDefaultValueChanged, false));

    public static List<Change> RequestPropertyEnumValueUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) => i.WalkProperties(p =>
    {
        foreach (var v in Walkers.DeletedEnumValues(p.PropertyDiff) ?? [])
        {
            var (b, rv) = Loc.SchemaDeletedItem(i.Sources, i.OperationItem, p.PropertyDiff, "enum", V(v));
            var id = p.PropertyDiff.Revision!.ReadOnly ? Ids.RequestReadOnlyPropertyEnumValueRemoved : Ids.RequestPropertyEnumValueRemoved;
            r.Add(p.NewChange(id, [v, p.FullName], "").WithSources(b, rv));
        }
        foreach (var v in Walkers.AddedEnumValues(p.PropertyDiff) ?? [])
        {
            var (b, rv) = Loc.SchemaAddedItem(i.Sources, i.OperationItem, p.PropertyDiff, "enum", V(v));
            r.Add(p.NewChange(Ids.RequestPropertyEnumValueAdded, [v, p.FullName], "").WithSources(b, rv));
        }
    }));

    public static List<Change> ResponsePropertyEnumValueAddedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) => i.WalkProperties(p =>
    {
        var id = Ids.ResponsePropertyEnumValueAdded;
        var comment = Comment(Ids.ResponsePropertyEnumValueAdded);
        if (p.PropertyDiff.Revision!.WriteOnly) { id = Ids.ResponseWriteOnlyPropertyEnumValueAdded; comment = ""; }
        foreach (var v in Walkers.AddedEnumValues(p.PropertyDiff) ?? [])
        {
            var (b, rv) = Loc.SchemaAddedItem(i.Sources, i.OperationItem, p.PropertyDiff, "enum", V(v));
            r.Add(p.NewChange(id, [v, p.FullName, i.ResponseStatus], comment).WithSources(b, rv));
        }
    }));

    public static List<Change> ResponseParameterEnumValueRemovedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) => i.WalkProperties(p =>
    {
        foreach (var v in Walkers.DeletedEnumValues(p.PropertyDiff) ?? [])
        {
            var (b, rv) = Loc.SchemaDeletedItem(i.Sources, i.OperationItem, p.PropertyDiff, "enum", V(v));
            r.Add(p.NewChange(Ids.ResponsePropertyEnumValueRemoved, [v, p.FullName, i.ResponseStatus], "").WithSources(b, rv));
        }
    }));

    public static List<Change> RequestBodyBecameEnumCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
    {
        if (i.SchemaDiff.EnumDiff is not { EnumAdded: true }) return;
        var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, i.SchemaDiff, "enum");
        r.Add(i.NewChange(Ids.RequestBodyBecameEnum, [], "").WithSources(b, rv));
    });

    public static List<Change> RequestBodyBecameNotEnumCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
    {
        if (!Walkers.BecameNotEnum(i.SchemaDiff)) return;
        var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, i.SchemaDiff, "enum");
        r.Add(i.NewChange(Ids.RequestBodyBecameNotEnum, [], "").WithSources(b, rv));
    });

    public static List<Change> RequestBodyEnumValueRemovedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
    {
        foreach (var v in Walkers.DeletedEnumValues(i.SchemaDiff) ?? [])
        {
            var (b, rv) = Loc.SchemaDeletedItem(i.Sources, i.OperationItem, i.SchemaDiff, "enum", V(v));
            r.Add(i.NewChange(Ids.RequestBodyEnumValueRemoved, [v], "").WithSources(b, rv));
        }
    });

    public static List<Change> RequestBodyEnumValueAddedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
    {
        foreach (var v in Walkers.AddedEnumValues(i.SchemaDiff) ?? [])
        {
            var (b, rv) = Loc.SchemaAddedItem(i.Sources, i.OperationItem, i.SchemaDiff, "enum", V(v));
            r.Add(i.NewChange(Ids.RequestBodyEnumValueAdded, [v], "").WithSources(b, rv));
        }
    });

    public static List<Change> ResponseMediaTypeBecameEnumCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
    {
        if (!Walkers.BecameEnum(i.SchemaDiff)) return;
        var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, i.SchemaDiff, "enum");
        r.Add(i.NewChange(Ids.ResponseMediaTypeBecameEnum, [i.MediaType, i.ResponseStatus], "").WithSources(b, rv));
    });

    public static List<Change> ResponseMediaTypeBecameNotEnumCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
    {
        if (!Walkers.BecameNotEnum(i.SchemaDiff)) return;
        var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, i.SchemaDiff, "enum");
        r.Add(i.NewChange(Ids.ResponseMediaTypeBecameNotEnum, [i.MediaType, i.ResponseStatus], Comment(Ids.ResponseMediaTypeBecameNotEnum)).WithSources(b, rv));
    });

    public static List<Change> ResponseMediaTypeEnumValueAddedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
    {
        foreach (var v in Walkers.AddedEnumValues(i.SchemaDiff) ?? [])
        {
            var (b, rv) = Loc.SchemaAddedItem(i.Sources, i.OperationItem, i.SchemaDiff, "enum", V(v));
            r.Add(i.NewChange(Ids.ResponseMediaTypeEnumValueAdded, [v, i.MediaType, i.ResponseStatus], Comment(Ids.ResponseMediaTypeEnumValueAdded)).WithSources(b, rv));
        }
    });

    public static List<Change> ResponseMediaTypeEnumValueRemovedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
    {
        foreach (var v in Walkers.DeletedEnumValues(i.SchemaDiff) ?? [])
        {
            var (b, rv) = Loc.SchemaDeletedItem(i.Sources, i.OperationItem, i.SchemaDiff, "enum", V(v));
            r.Add(i.NewChange(Ids.ResponseMediaTypeEnumValueRemoved, [i.MediaType, v], "").WithSources(b, rv));
        }
    });
}
