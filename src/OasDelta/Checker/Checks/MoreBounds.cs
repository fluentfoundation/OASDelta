using System.Globalization;
using System.Numerics;
using OasDelta.Diff;
using OasDelta.Yaml;

namespace OasDelta.Checker;

public static partial class Checks
{
    /// <summary>a/b is an integer, using the shortest decimal representations (big.Rat semantics).</summary>
    internal static bool IsIntegerMultiple(double a, double b)
    {
        if (b == 0) return false;
        var (na, da) = Rational(a);
        var (nb, db) = Rational(b);
        var num = na * db;
        var den = da * nb;
        if (den.IsZero) return false;
        return BigInteger.Remainder(num, den).IsZero;
    }

    private static (BigInteger num, BigInteger den) Rational(double v)
    {
        var s = Values.FormatGoG(v);
        bool neg = s.StartsWith('-');
        if (neg) s = s[1..];
        int exp = 0;
        int e = s.IndexOf('e');
        if (e >= 0) { exp = int.Parse(s[(e + 1)..], CultureInfo.InvariantCulture); s = s[..e]; }
        int dot = s.IndexOf('.');
        if (dot >= 0) { exp -= s.Length - dot - 1; s = s.Remove(dot, 1); }
        var n = BigInteger.Parse(s, CultureInfo.InvariantCulture);
        if (neg) n = -n;
        return exp >= 0 ? (n * BigInteger.Pow(10, exp), BigInteger.One) : (n, BigInteger.Pow(10, -exp));
    }

    public static List<Change> RequestPropertyMultipleOfUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
    {
        if (i.SchemaDiff.MultipleOfDiff is { } md)
        {
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, i.SchemaDiff, "multipleOf");
            if (md.From is null) r.Add(i.NewChange(Ids.RequestBodyMultipleOfSet, [md.To], BoundSetComment).WithSources(null, rv));
            else if (md.To is null) r.Add(i.NewChange(Ids.RequestBodyMultipleOfUnset, [md.From], "").WithSources(b, rv));
            else if (IsIntegerMultiple((double)md.From, (double)md.To)) r.Add(i.NewChange(Ids.RequestBodyMultipleOfGeneralized, [md.From, md.To], Ids.RequestBodyMultipleOfGeneralized + "-comment").WithSources(b, rv));
            else r.Add(i.NewChange(Ids.RequestBodyMultipleOfChanged, [md.From, md.To], "").WithSources(b, rv));
        }
        i.WalkProperties(p =>
        {
            if (p.PropertyDiff.MultipleOfDiff is not { } md) return;
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, "multipleOf");
            if (md.From is null) r.Add(p.NewChange(Ids.RequestPropertyMultipleOfSet, [p.FullName, md.To], BoundSetComment).WithSources(null, rv));
            else if (md.To is null) r.Add(p.NewChange(Ids.RequestPropertyMultipleOfUnset, [p.FullName, md.From], "").WithSources(b, rv));
            else if (IsIntegerMultiple((double)md.From, (double)md.To)) r.Add(p.NewChange(Ids.RequestPropertyMultipleOfGeneralized, [p.FullName, md.From, md.To], Ids.RequestPropertyMultipleOfGeneralized + "-comment").WithSources(b, rv));
            else r.Add(p.NewChange(Ids.RequestPropertyMultipleOfChanged, [p.FullName, md.From, md.To], "").WithSources(b, rv));
        });
    });

    public static List<Change> ResponsePropertyMultipleOfUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
    {
        if (i.SchemaDiff.MultipleOfDiff is { From: not null } md)
        {
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, i.SchemaDiff, "multipleOf");
            if (md.To is null) r.Add(i.NewChange(Ids.ResponseBodyMultipleOfUnset, [md.From], "").WithSources(b, rv));
            else if (IsIntegerMultiple((double)md.To, (double)md.From)) r.Add(i.NewChange(Ids.ResponseBodyMultipleOfSpecialized, [md.From, md.To], Ids.ResponseBodyMultipleOfSpecialized + "-comment").WithSources(b, rv));
            else r.Add(i.NewChange(Ids.ResponseBodyMultipleOfChanged, [md.From, md.To], "").WithSources(b, rv));
        }
        i.WalkProperties(p =>
        {
            if (p.PropertyDiff.MultipleOfDiff is not { From: not null } md) return;
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, "multipleOf");
            if (md.To is null) r.Add(p.NewChange(Ids.ResponsePropertyMultipleOfUnset, [p.FullName, md.From, i.ResponseStatus], "").WithSources(b, rv));
            else if (IsIntegerMultiple((double)md.To, (double)md.From)) r.Add(p.NewChange(Ids.ResponsePropertyMultipleOfSpecialized, [p.FullName, md.From, md.To, i.ResponseStatus], Ids.ResponsePropertyMultipleOfSpecialized + "-comment").WithSources(b, rv));
            else r.Add(p.NewChange(Ids.ResponsePropertyMultipleOfChanged, [p.FullName, md.From, md.To, i.ResponseStatus], "").WithSources(b, rv));
        });
    });

    public static List<Change> RequestPropertyUniqueItemsUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
    {
        if (i.SchemaDiff.UniqueItemsDiff is { } ud)
        {
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, i.SchemaDiff, "uniqueItems");
            r.Add(i.NewChange(ud.To is true ? Ids.RequestBodyUniqueItemsSet : Ids.RequestBodyUniqueItemsUnset, [], "").WithSources(b, rv));
        }
        i.WalkProperties(p =>
        {
            if (p.PropertyDiff.UniqueItemsDiff is not { } ud) return;
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, "uniqueItems");
            r.Add(p.NewChange(ud.To is true ? Ids.RequestPropertyUniqueItemsSet : Ids.RequestPropertyUniqueItemsUnset, [p.FullName], "").WithSources(b, rv));
        });
    });

    public static List<Change> ResponsePropertyUniqueItemsUnsetCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
    {
        if (i.SchemaDiff.UniqueItemsDiff is { To: false })
        {
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, i.SchemaDiff, "uniqueItems");
            r.Add(i.NewChange(Ids.ResponseBodyUniqueItemsUnset, [], "").WithSources(b, rv));
        }
        i.WalkProperties(p =>
        {
            if (p.PropertyDiff.UniqueItemsDiff is not { To: false }) return;
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, "uniqueItems");
            r.Add(p.NewChange(Ids.ResponsePropertyUniqueItemsUnset, [p.FullName, i.ResponseStatus], "").WithSources(b, rv));
        });
    });

    // ------------------------------------------------------------------ response bounds: report (from, to) for body, (prop, from, to, status) for properties

    private static void ResponseMoved(MediaTypeInfo i, List<Change> r, Func<SchemaDiff, ValueDiff?> get, string keyword, Func<ValueDiff, bool> cond, string bodyId, string propId)
    {
        if (get(i.SchemaDiff) is { } vd && cond(vd))
        {
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, i.SchemaDiff, keyword);
            r.Add(i.NewChange(bodyId, [vd.From, vd.To], "").WithSources(b, rv));
        }
        i.WalkProperties(p =>
        {
            if (get(p.PropertyDiff) is not { } pd || !cond(pd)) return;
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, keyword);
            r.Add(p.NewChange(propId, [p.FullName, pd.From, pd.To, i.ResponseStatus], "").WithSources(b, rv));
        });
    }

    private static bool BothIncreased(ValueDiff d) => BothSet(d) && Walkers.IsIncreasedValue(d);
    private static bool BothDecreased(ValueDiff d) => BothSet(d) && Walkers.IsDecreasedValue(d);
    private static bool UintDecreased(ValueDiff d) => !Walkers.UintBoundUnset(d) && Walkers.IsDecreasedValue(d);

    public static List<Change> ResponsePropertyMaxIncreasedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
    {
        BodyMoved(i, r, x => x.MaxDiff, "maximum", BothIncreased, Ids.ResponseBodyMaxIncreased);
        BodyMoved(i, r, x => x.ExclusiveMaxDiff, "exclusiveMaximum", BothIncreased, Ids.ResponseBodyExclusiveMaxIncreased);
        i.WalkProperties(p =>
        {
            PropMoved(p, r, x => x.MaxDiff, "maximum", BothIncreased, Ids.ResponsePropertyMaxIncreased);
            PropMoved(p, r, x => x.ExclusiveMaxDiff, "exclusiveMaximum", BothIncreased, Ids.ResponsePropertyExclusiveMaxIncreased);
        });
    });

    public static List<Change> ResponsePropertyMinDecreasedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
    {
        BodyMoved(i, r, x => x.MinDiff, "minimum", BothDecreased, Ids.ResponseBodyMinDecreased);
        BodyMoved(i, r, x => x.ExclusiveMinDiff, "exclusiveMinimum", BothDecreased, Ids.ResponseBodyExclusiveMinDecreased);
        i.WalkProperties(p =>
        {
            PropMoved(p, r, x => x.MinDiff, "minimum", BothDecreased, Ids.ResponsePropertyMinDecreased);
            PropMoved(p, r, x => x.ExclusiveMinDiff, "exclusiveMinimum", BothDecreased, Ids.ResponsePropertyExclusiveMinDecreased);
        });
    });

    private static void BodyMoved(MediaTypeInfo i, List<Change> r, Func<SchemaDiff, ValueDiff?> get, string keyword, Func<ValueDiff, bool> cond, string id)
    {
        if (get(i.SchemaDiff) is not { } vd || !cond(vd)) return;
        var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, i.SchemaDiff, keyword);
        r.Add(i.NewChange(id, [vd.From, vd.To], "").WithSources(b, rv));
    }

    private static void PropMoved(PropertyInfo p, List<Change> r, Func<SchemaDiff, ValueDiff?> get, string keyword, Func<ValueDiff, bool> cond, string id)
    {
        if (get(p.PropertyDiff) is not { } pd || !cond(pd)) return;
        var (b, rv) = Loc.SchemaField(p.Info.Sources, p.Info.OperationItem, p.PropertyDiff, keyword);
        r.Add(p.NewChange(id, [p.FullName, pd.From, pd.To, p.Info.ResponseStatus], "").WithSources(b, rv));
    }

    public static List<Change> ResponsePropertyMaxItemsIncreasedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
        ResponseMoved(i, r, x => x.MaxItemsDiff, "maxItems", BothIncreased, Ids.ResponseBodyMaxItemsIncreased, Ids.ResponsePropertyMaxItemsIncreased));

    public static List<Change> ResponsePropertyMaxLengthIncreasedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
        ResponseMoved(i, r, x => x.MaxLengthDiff, "maxLength", BothIncreased, Ids.ResponseBodyMaxLengthIncreased, Ids.ResponsePropertyMaxLengthIncreased));

    public static List<Change> ResponsePropertyMaxPropertiesIncreasedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
        ResponseMoved(i, r, x => x.MaxPropsDiff, "maxProperties", BothIncreased, Ids.ResponseBodyMaxPropertiesIncreased, Ids.ResponsePropertyMaxPropertiesIncreased));

    public static List<Change> ResponsePropertyMinItemsDecreasedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
        ResponseMoved(i, r, x => x.MinItemsDiff, "minItems", UintDecreased, Ids.ResponseBodyMinItemsDecreased, Ids.ResponsePropertyMinItemsDecreased));

    public static List<Change> ResponsePropertyMinLengthDecreasedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
        ResponseMoved(i, r, x => x.MinLengthDiff, "minLength", UintDecreased, Ids.ResponseBodyMinLengthDecreased, Ids.ResponsePropertyMinLengthDecreased));

    public static List<Change> ResponsePropertyMinPropertiesDecreasedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
        ResponseMoved(i, r, x => x.MinPropsDiff, "minProperties", UintDecreased, Ids.ResponseBodyMinPropertiesDecreased, Ids.ResponsePropertyMinPropertiesDecreased));

    private static void ResponseUnset(MediaTypeInfo i, List<Change> r, Func<SchemaDiff, ValueDiff?> get, string keyword, Func<ValueDiff?, bool> unset, string bodyId, string propId)
    {
        if (get(i.SchemaDiff) is var vd && unset(vd))
        {
            var (b, _) = Loc.SchemaField(i.Sources, i.OperationItem, i.SchemaDiff, keyword);
            r.Add(i.NewChange(bodyId, [vd!.From], "").WithSources(b, null));
        }
        i.WalkProperties(p =>
        {
            var pd = get(p.PropertyDiff);
            if (!unset(pd)) return;
            var (b, _) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, keyword);
            r.Add(p.NewChange(propId, [p.FullName, pd!.From, i.ResponseStatus], "").WithSources(b, null));
        });
    }

    public static List<Change> ResponsePropertyMaxLengthUnsetCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
        ResponseUnset(i, r, x => x.MaxLengthDiff, "maxLength", x => x is { From: not null, To: null }, Ids.ResponseBodyMaxLengthUnset, Ids.ResponsePropertyMaxLengthUnset));

    public static List<Change> ResponsePropertyMinItemsUnsetCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
        ResponseUnset(i, r, x => x.MinItemsDiff, "minItems", Walkers.UintBoundUnset, Ids.ResponseBodyMinItemsUnset, Ids.ResponsePropertyMinItemsUnset));
}
