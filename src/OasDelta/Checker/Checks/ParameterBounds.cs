using OasDelta.Diff;

namespace OasDelta.Checker;

public static partial class Checks
{
    private static List<Change> Params(OpenApiDiff d, Sources s, CheckerConfig c, Action<ParamInfo, List<Change>> f)
    {
        var result = new List<Change>();
        Walkers.ModifiedParameters(d, s, c, p => f(p, result));
        return result;
    }

    private static void ParamBoundChanged(ParamInfo p, List<Change> r, ValueDiff? vd, string field, string idIfCond, string idElse, Func<ValueDiff, bool> cond, bool requireOrdered)
    {
        if (!BothSet(vd)) return;
        if (requireOrdered && !Walkers.IsIncreasedValue(vd!) && !Walkers.IsDecreasedValue(vd!)) return;
        var (b, rv) = Loc.SchemaField(p.OpInfo.Sources, p.OpInfo.MethodDiff, p.ParamDiff.SchemaDiff, field);
        r.Add(p.OpInfo.NewApiChange(cond(vd!) ? idIfCond : idElse, [p.Location, p.Name, vd!.From, vd.To], "").WithSources(b, rv));
    }

    private static void ParamBoundSet(ParamInfo p, List<Change> r, (bool, object?) set, string field, string id)
    {
        if (set is not (true, var v)) return;
        var (_, rv) = Loc.SchemaField(p.OpInfo.Sources, p.OpInfo.MethodDiff, p.ParamDiff.SchemaDiff, field);
        r.Add(p.OpInfo.NewApiChange(id, [p.Location, p.Name, v], BoundSetComment).WithSources(null, rv));
    }

    public static List<Change> RequestParameterMaxUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.SchemaDiff is not { } sd) return;
        ParamBoundChanged(p, r, sd.MaxDiff, "maximum", Ids.RequestParameterMaxDecreased, Ids.RequestParameterMaxIncreased, Walkers.IsDecreasedValue, true);
        ParamBoundChanged(p, r, sd.ExclusiveMaxDiff, "exclusiveMaximum", Ids.RequestParameterExclusiveMaxDecreased, Ids.RequestParameterExclusiveMaxIncreased, Walkers.IsDecreasedValue, true);
    });

    public static List<Change> RequestParameterMinUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.SchemaDiff is not { } sd) return;
        ParamBoundChanged(p, r, sd.MinDiff, "minimum", Ids.RequestParameterMinIncreased, Ids.RequestParameterMinDecreased, Walkers.IsIncreasedValue, true);
        ParamBoundChanged(p, r, sd.ExclusiveMinDiff, "exclusiveMinimum", Ids.RequestParameterExclusiveMinIncreased, Ids.RequestParameterExclusiveMinDecreased, Walkers.IsIncreasedValue, true);
    });

    public static List<Change> RequestParameterMaxItemsUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.SchemaDiff is not { } sd) return;
        var vd = sd.MaxItemsDiff ?? sd.ItemsDiff?.MaxItemsDiff;
        ParamBoundChanged(p, r, vd, "maxItems", Ids.RequestParameterMaxItemsIncreased, Ids.RequestParameterMaxItemsDecreased, Walkers.IsIncreasedValue, false);
    });

    public static List<Change> RequestParameterMaxLengthUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.SchemaDiff is not { } sd) return;
        ParamBoundChanged(p, r, sd.MaxLengthDiff, "maxLength", Ids.RequestParameterMaxLengthDecreased, Ids.RequestParameterMaxLengthIncreased, Walkers.IsDecreasedValue, false);
    });

    public static List<Change> RequestParameterMinItemsUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.SchemaDiff?.MinItemsDiff is not { } vd || Walkers.UintBoundSet(vd) || Walkers.UintBoundUnset(vd)) return;
        var (b, rv) = Loc.SchemaField(p.OpInfo.Sources, p.OpInfo.MethodDiff, p.ParamDiff.SchemaDiff, "minItems");
        r.Add(p.OpInfo.NewApiChange(Walkers.IsIncreasedValue(vd) ? Ids.RequestParameterMinItemsIncreased : Ids.RequestParameterMinItemsDecreased, [p.Location, p.Name, vd.From, vd.To], "").WithSources(b, rv));
    });

    public static List<Change> RequestParameterMinLengthUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.SchemaDiff?.MinLengthDiff is not { } vd || Walkers.UintBoundSet(vd) || Walkers.UintBoundUnset(vd)) return;
        var (b, rv) = Loc.SchemaField(p.OpInfo.Sources, p.OpInfo.MethodDiff, p.ParamDiff.SchemaDiff, "minLength");
        r.Add(p.OpInfo.NewApiChange(Walkers.IsDecreasedValue(vd) ? Ids.RequestParameterMinLengthDecreased : Ids.RequestParameterMinLengthIncreased, [p.Location, p.Name, vd.From, vd.To], "").WithSources(b, rv));
    });

    public static List<Change> RequestParameterMaxLengthSetCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.SchemaDiff is not { } sd) return;
        ParamBoundSet(p, r, NilSet(sd.MaxLengthDiff), "maxLength", Ids.RequestParameterMaxLengthSet);
    });

    public static List<Change> RequestParameterMinItemsSetCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.SchemaDiff is not { } sd) return;
        ParamBoundSet(p, r, UintSet(sd.MinItemsDiff), "minItems", Ids.RequestParameterMinItemsSet);
    });

    public static List<Change> RequestParameterMaxSetCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.SchemaDiff is not { } sd) return;
        ParamBoundSet(p, r, Bound.MaximumBound.WasSet(sd), "maximum", Ids.RequestParameterMaxSet);
        ParamBoundSet(p, r, Bound.ExclusiveMaximumBound.WasSet(sd), "exclusiveMaximum", Ids.RequestParameterExclusiveMaxSet);
    });

    public static List<Change> RequestParameterMinSetCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.SchemaDiff is not { } sd) return;
        ParamBoundSet(p, r, Bound.MinimumBound.WasSet(sd), "minimum", Ids.RequestParameterMinSet);
        ParamBoundSet(p, r, Bound.ExclusiveMinimumBound.WasSet(sd), "exclusiveMinimum", Ids.RequestParameterExclusiveMinSet);
    });
}
