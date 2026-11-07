using OasDelta.Diff;

namespace OasDelta.Checker;

public static partial class Checks
{
    public static List<Change> RequestPropertyStabilityUpdatedCheck(OpenApiDiff d, Sources src, CheckerConfig config)
    {
        var result = new List<Change>();
        Walkers.ModifiedRequestBodySchemas(d, src, config, info => info.WalkProperties(p =>
            PropertyStabilityChange(p, Ids.RequestPropertyStabilityDecreased, Ids.RequestPropertyStabilityIncreased, result)));
        return result;
    }

    public static List<Change> ResponsePropertyStabilityUpdatedCheck(OpenApiDiff d, Sources src, CheckerConfig config)
    {
        var result = new List<Change>();
        Walkers.ModifiedResponseSchemas(d, src, config, info => info.WalkProperties(p =>
            PropertyStabilityChange(p, Ids.ResponsePropertyStabilityDecreased, Ids.ResponsePropertyStabilityIncreased, result)));
        return result;
    }

    private static void PropertyStabilityChange(PropertyInfo p, string decreasedId, string increasedId, List<Change> result)
    {
        if (p.PropertyDiff.ExtensionsDiff is null) return;
        var info = p.Info;
        var op = info.OperationItem.Revision!;
        var (bs, e1) = Stability.Get(p.PropertyDiff.Base!.Extensions);
        if (e1 is not null)
        {
            result.Add(StabilityPolicy.Invalid(info.Config, op, info.Sources, info.Method, info.Path, e1).WithSources(Loc.StabilityField(info.Sources, op, p.PropertyDiff.Base.Origin), null));
            return;
        }
        var (rs, e2) = Stability.Get(p.PropertyDiff.Revision!.Extensions);
        if (e2 is not null)
        {
            result.Add(StabilityPolicy.Invalid(info.Config, op, info.Sources, info.Method, info.Path, e2).WithSources(null, Loc.StabilityField(info.Sources, op, p.PropertyDiff.Revision.Origin)));
            return;
        }
        var bl = Stability.Normalized(bs); var rl = Stability.Normalized(rs);
        var blv = Stability.Parse(bl); var rlv = Stability.Parse(rl);
        if (blv == rlv || !info.Config.StabilityLevel.IsIncluded(bl)) return;
        var id = rlv < blv ? decreasedId : increasedId;
        result.Add(p.NewChange(id, [p.FullName, bl, rl], "")
            .WithSources(Loc.StabilityField(info.Sources, op, p.PropertyDiff.Base.Origin), Loc.StabilityField(info.Sources, op, p.PropertyDiff.Revision.Origin)));
    }
}
