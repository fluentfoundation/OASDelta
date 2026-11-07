using OasDelta.Diff;

namespace OasDelta.Checker;

public static partial class Checks
{
    private const string BoundSetComment = Bound.SetComment;

    private static bool BothSet(ValueDiff? d) => d is { From: not null, To: not null };
    private static bool SetFromNil(ValueDiff? d) => d is { From: null, To: not null };

    /// <summary>A bound that changed with both values present: narrowing reports (to), widening reports (from, to).</summary>
    private static void BoundUpdated(MediaTypeInfo info, List<Change> result, Func<SchemaDiff, ValueDiff?> get, string keyword, bool narrowsOnDecrease, bool requireOrdered,
        string bodyNarrow, string bodyWiden, string propNarrow, string? propReadOnlyNarrow, string propWiden)
    {
        bool Narrowed(ValueDiff d) => narrowsOnDecrease ? Walkers.IsDecreasedValue(d) : Walkers.IsIncreasedValue(d);
        bool Ok(ValueDiff? d) => BothSet(d) && (!requireOrdered || Walkers.IsIncreasedValue(d!) || Walkers.IsDecreasedValue(d!));
        if (get(info.SchemaDiff) is { } vd && Ok(vd))
        {
            var (b, r) = Loc.SchemaField(info.Sources, info.OperationItem, info.SchemaDiff, keyword);
            result.Add(Narrowed(vd) ? info.NewChange(bodyNarrow, [vd.To], "").WithSources(b, r) : info.NewChange(bodyWiden, [vd.From, vd.To], "").WithSources(b, r));
        }
        info.WalkProperties(p =>
        {
            var pd = get(p.PropertyDiff);
            if (!Ok(pd)) return;
            var (b, r) = Loc.SchemaField(info.Sources, info.OperationItem, p.PropertyDiff, keyword);
            if (Narrowed(pd!))
            {
                var id = propReadOnlyNarrow is not null && p.PropertyDiff.Revision!.ReadOnly ? propReadOnlyNarrow : propNarrow;
                result.Add(p.NewChange(id, [p.FullName, pd!.To], "").WithSources(b, r));
            }
            else result.Add(p.NewChange(propWiden, [p.FullName, pd!.From, pd.To], "").WithSources(b, r));
        });
    }

    /// <summary>A bound that went from absent to present.</summary>
    private static void BoundSet(MediaTypeInfo info, List<Change> result, Func<SchemaDiff, (bool, object?)> wasSet, string keyword, string bodyId, string propId, string comment = BoundSetComment)
    {
        if (wasSet(info.SchemaDiff) is (true, var v))
        {
            var (_, r) = Loc.SchemaField(info.Sources, info.OperationItem, info.SchemaDiff, keyword);
            result.Add(info.NewChange(bodyId, [v], comment).WithSources(null, r));
        }
        info.WalkProperties(p =>
        {
            if (wasSet(p.PropertyDiff) is not (true, var pv)) return;
            var (_, r) = Loc.SchemaField(info.Sources, info.OperationItem, p.PropertyDiff, keyword);
            result.Add(p.NewChange(propId, [p.FullName, pv], comment).WithSources(null, r));
        });
    }

    private static (bool, object?) NilSet(ValueDiff? d) => SetFromNil(d) ? (true, d!.To) : (false, null);
    private static (bool, object?) UintSet(ValueDiff? d) => Walkers.UintBoundSet(d) ? (true, d!.To) : (false, null);

    /// <summary>A bound that only moved in one direction (e.g. minItems increased): reports (to).</summary>
    private static void BoundMoved(MediaTypeInfo info, List<Change> result, Func<SchemaDiff, ValueDiff?> get, string keyword, Func<ValueDiff, bool> moved, string bodyId, string propId,
        Func<ValueDiff, bool>? skip = null, Func<ValueDiff, List<object?>>? bodyArgs = null, Func<ValueDiff, List<object?>>? propArgs = null)
    {
        skip ??= _ => false;
        if (get(info.SchemaDiff) is { } vd && !skip(vd) && moved(vd))
        {
            var (b, r) = Loc.SchemaField(info.Sources, info.OperationItem, info.SchemaDiff, keyword);
            result.Add(info.NewChange(bodyId, bodyArgs?.Invoke(vd) ?? [vd.To], "").WithSources(b, r));
        }
        info.WalkProperties(p =>
        {
            var pd = get(p.PropertyDiff);
            if (pd is null || skip(pd) || !moved(pd)) return;
            var (b, r) = Loc.SchemaField(info.Sources, info.OperationItem, p.PropertyDiff, keyword);
            result.Add(p.NewChange(propId, propArgs?.Invoke(pd) ?? [p.FullName, pd.To], "").WithSources(b, r));
        });
    }

    private static List<Change> RequestBodies(OpenApiDiff d, Sources src, CheckerConfig config, Action<MediaTypeInfo, List<Change>> f)
    {
        var result = new List<Change>();
        Walkers.ModifiedRequestBodySchemas(d, src, config, info => f(info, result));
        return result;
    }

    private static List<Change> ResponseBodies(OpenApiDiff d, Sources src, CheckerConfig config, Action<MediaTypeInfo, List<Change>> f)
    {
        var result = new List<Change>();
        Walkers.ModifiedResponseSchemas(d, src, config, info => f(info, result));
        return result;
    }

    // ------------------------------------------------------------------ request property bounds

    public static List<Change> RequestPropertyMaxItemsUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
        BoundUpdated(i, r, x => x.MaxItemsDiff, "maxItems", true, false, Ids.RequestBodyMaxItemsDecreased, Ids.RequestBodyMaxItemsIncreased,
            Ids.RequestPropertyMaxItemsDecreased, Ids.RequestReadOnlyPropertyMaxItemsDecreased, Ids.RequestPropertyMaxItemsIncreased));

    public static List<Change> RequestPropertyMaxLengthUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
        BoundUpdated(i, r, x => x.MaxLengthDiff, "maxLength", true, false, Ids.RequestBodyMaxLengthDecreased, Ids.RequestBodyMaxLengthIncreased,
            Ids.RequestPropertyMaxLengthDecreased, Ids.RequestReadOnlyPropertyMaxLengthDecreased, Ids.RequestPropertyMaxLengthIncreased));

    public static List<Change> RequestPropertyMaxPropertiesUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
        BoundUpdated(i, r, x => x.MaxPropsDiff, "maxProperties", true, false, Ids.RequestBodyMaxPropertiesDecreased, Ids.RequestBodyMaxPropertiesIncreased,
            Ids.RequestPropertyMaxPropertiesDecreased, Ids.RequestReadOnlyPropertyMaxPropertiesDecreased, Ids.RequestPropertyMaxPropertiesIncreased));

    public static List<Change> RequestPropertyMaxDecreasedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
    {
        var result = new List<Change>();
        // the body-level maximum and exclusiveMaximum are reported first, then the properties
        var bodyOnly = i.Copy();
        BoundUpdatedBodyOnly(bodyOnly, result, x => x.MaxDiff, "maximum", true, false, Ids.RequestBodyMaxDecreased, Ids.RequestBodyMaxIncreased);
        BoundUpdatedBodyOnly(bodyOnly, result, x => x.ExclusiveMaxDiff, "exclusiveMaximum", true, true, Ids.RequestBodyExclusiveMaxDecreased, Ids.RequestBodyExclusiveMaxIncreased);
        i.WalkProperties(p =>
        {
            BoundUpdatedProperty(p, result, x => x.MaxDiff, "maximum", true, false, Ids.RequestPropertyMaxDecreased, Ids.RequestReadOnlyPropertyMaxDecreased, Ids.RequestPropertyMaxIncreased);
            BoundUpdatedProperty(p, result, x => x.ExclusiveMaxDiff, "exclusiveMaximum", true, true, Ids.RequestPropertyExclusiveMaxDecreased, Ids.RequestReadOnlyPropertyExclusiveMaxDecreased, Ids.RequestPropertyExclusiveMaxIncreased);
        });
        r.AddRange(result);
    });

    public static List<Change> RequestPropertyMinIncreasedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
    {
        BoundUpdatedBodyOnly(i, r, x => x.MinDiff, "minimum", false, false, Ids.RequestBodyMinIncreased, Ids.RequestBodyMinDecreased);
        BoundUpdatedBodyOnly(i, r, x => x.ExclusiveMinDiff, "exclusiveMinimum", false, true, Ids.RequestBodyExclusiveMinIncreased, Ids.RequestBodyExclusiveMinDecreased);
        i.WalkProperties(p =>
        {
            BoundUpdatedProperty(p, r, x => x.MinDiff, "minimum", false, false, Ids.RequestPropertyMinIncreased, Ids.RequestReadOnlyPropertyMinIncreased, Ids.RequestPropertyMinDecreased);
            BoundUpdatedProperty(p, r, x => x.ExclusiveMinDiff, "exclusiveMinimum", false, true, Ids.RequestPropertyExclusiveMinIncreased, Ids.RequestReadOnlyPropertyExclusiveMinIncreased, Ids.RequestPropertyExclusiveMinDecreased);
        });
    });

    private static void BoundUpdatedBodyOnly(MediaTypeInfo info, List<Change> result, Func<SchemaDiff, ValueDiff?> get, string keyword, bool narrowsOnDecrease, bool requireOrdered, string narrowId, string widenId)
    {
        var vd = get(info.SchemaDiff);
        if (!BothSet(vd) || (requireOrdered && !Walkers.IsIncreasedValue(vd!) && !Walkers.IsDecreasedValue(vd!))) return;
        bool narrowed = narrowsOnDecrease ? Walkers.IsDecreasedValue(vd!) : Walkers.IsIncreasedValue(vd!);
        var (b, r) = Loc.SchemaField(info.Sources, info.OperationItem, info.SchemaDiff, keyword);
        result.Add(narrowed ? info.NewChange(narrowId, [vd!.To], "").WithSources(b, r) : info.NewChange(widenId, [vd!.From, vd.To], "").WithSources(b, r));
    }

    private static void BoundUpdatedProperty(PropertyInfo p, List<Change> result, Func<SchemaDiff, ValueDiff?> get, string keyword, bool narrowsOnDecrease, bool requireOrdered, string narrowId, string? readOnlyNarrowId, string widenId)
    {
        var vd = get(p.PropertyDiff);
        if (!BothSet(vd) || (requireOrdered && !Walkers.IsIncreasedValue(vd!) && !Walkers.IsDecreasedValue(vd!))) return;
        bool narrowed = narrowsOnDecrease ? Walkers.IsDecreasedValue(vd!) : Walkers.IsIncreasedValue(vd!);
        var (b, r) = Loc.SchemaField(p.Info.Sources, p.Info.OperationItem, p.PropertyDiff, keyword);
        if (narrowed)
        {
            var id = readOnlyNarrowId is not null && p.PropertyDiff.Revision!.ReadOnly ? readOnlyNarrowId : narrowId;
            result.Add(p.NewChange(id, [p.FullName, vd!.To], "").WithSources(b, r));
        }
        else result.Add(p.NewChange(widenId, [p.FullName, vd!.From, vd.To], "").WithSources(b, r));
    }

    public static List<Change> RequestPropertyMaxItemsSetCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
        BoundSet(i, r, x => NilSet(x.MaxItemsDiff), "maxItems", Ids.RequestBodyMaxItemsSet, Ids.RequestPropertyMaxItemsSet));

    public static List<Change> RequestPropertyMaxLengthSetCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
        BoundSet(i, r, x => NilSet(x.MaxLengthDiff), "maxLength", Ids.RequestBodyMaxLengthSet, Ids.RequestPropertyMaxLengthSet));

    public static List<Change> RequestPropertyMaxPropertiesSetCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
        BoundSet(i, r, x => NilSet(x.MaxPropsDiff), "maxProperties", Ids.RequestBodyMaxPropertiesSet, Ids.RequestPropertyMaxPropertiesSet));

    public static List<Change> RequestPropertyMinItemsSetCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
        BoundSet(i, r, x => UintSet(x.MinItemsDiff), "minItems", Ids.RequestBodyMinItemsSet, Ids.RequestPropertyMinItemsSet));

    public static List<Change> RequestPropertyMaxSetCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
    {
        SetBodyOnly(i, r, Bound.MaximumBound.WasSet, "maximum", Ids.RequestBodyMaxSet);
        SetBodyOnly(i, r, Bound.ExclusiveMaximumBound.WasSet, "exclusiveMaximum", Ids.RequestBodyExclusiveMaxSet);
        i.WalkProperties(p =>
        {
            SetProperty(p, r, Bound.MaximumBound.WasSet, "maximum", Ids.RequestPropertyMaxSet);
            SetProperty(p, r, Bound.ExclusiveMaximumBound.WasSet, "exclusiveMaximum", Ids.RequestPropertyExclusiveMaxSet);
        });
    });

    public static List<Change> RequestPropertyMinSetCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
    {
        SetBodyOnly(i, r, Bound.MinimumBound.WasSet, "minimum", Ids.RequestBodyMinSet);
        SetBodyOnly(i, r, Bound.ExclusiveMinimumBound.WasSet, "exclusiveMinimum", Ids.RequestBodyExclusiveMinSet);
        i.WalkProperties(p =>
        {
            SetProperty(p, r, Bound.MinimumBound.WasSet, "minimum", Ids.RequestPropertyMinSet);
            SetProperty(p, r, Bound.ExclusiveMinimumBound.WasSet, "exclusiveMinimum", Ids.RequestPropertyExclusiveMinSet);
        });
    });

    private static void SetBodyOnly(MediaTypeInfo info, List<Change> result, Func<SchemaDiff, (bool, object?)> wasSet, string keyword, string id)
    {
        if (wasSet(info.SchemaDiff) is not (true, var v)) return;
        var (_, r) = Loc.SchemaField(info.Sources, info.OperationItem, info.SchemaDiff, keyword);
        result.Add(info.NewChange(id, [v], BoundSetComment).WithSources(null, r));
    }

    private static void SetProperty(PropertyInfo p, List<Change> result, Func<SchemaDiff, (bool, object?)> wasSet, string keyword, string id)
    {
        if (wasSet(p.PropertyDiff) is not (true, var v)) return;
        var (_, r) = Loc.SchemaField(p.Info.Sources, p.Info.OperationItem, p.PropertyDiff, keyword);
        result.Add(p.NewChange(id, [p.FullName, v], BoundSetComment).WithSources(null, r));
    }

    public static List<Change> RequestPropertyMinItemsIncreasedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
        BoundMoved(i, r, x => x.MinItemsDiff, "minItems", Walkers.IsIncreasedValue, Ids.RequestBodyMinItemsIncreased, Ids.RequestPropertyMinItemsIncreased, Walkers.UintBoundSet));

    public static List<Change> RequestPropertyMinPropertiesIncreasedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
        BoundMoved(i, r, x => x.MinPropsDiff, "minProperties", Walkers.IsIncreasedValue, Ids.RequestBodyMinPropertiesIncreased, Ids.RequestPropertyMinPropertiesIncreased, Walkers.UintBoundSet));

    public static List<Change> RequestPropertyMinLengthUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
    {
        bool Skip(ValueDiff x) => Walkers.UintBoundSet(x) || Walkers.UintBoundUnset(x);
        if (i.SchemaDiff.MinLengthDiff is { } vd && !Skip(vd))
        {
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, i.SchemaDiff, "minLength");
            r.Add(i.NewChange(Walkers.IsIncreasedValue(vd) ? Ids.RequestBodyMinLengthIncreased : Ids.RequestBodyMinLengthDecreased, [vd.From, vd.To], "").WithSources(b, rv));
        }
        i.WalkProperties(p =>
        {
            if (p.PropertyDiff.MinLengthDiff is not { } pd || Skip(pd)) return;
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, "minLength");
            r.Add(p.NewChange(Walkers.IsDecreasedValue(pd) ? Ids.RequestPropertyMinLengthDecreased : Ids.RequestPropertyMinLengthIncreased, [p.FullName, pd.From, pd.To], "").WithSources(b, rv));
        });
    });
}
