using OasDelta.Diff;
using OasDelta.Model;

namespace OasDelta.Checker;

public static partial class Checks
{
    public static List<Change> APIAddedCheck(OpenApiDiff d, Sources src, CheckerConfig config)
    {
        var result = new List<Change>();
        if (d.PathsDiff is null) return result;
        void Add(string path, string method, Operation op) =>
            result.Add(Walkers.NewApiChange(Ids.EndpointAdded, config, [], "", src, op, method, path).WithSources(null, Loc.FromOrigin(src, op, op.Origin)));
        foreach (var path in d.PathsDiff.Added)
            foreach (var (m, op) in d.PathsDiff.Revision!.Value(path)!.Operations()) Add(path, m, op);
        foreach (var (path, pd) in d.PathsDiff.Modified)
            foreach (var (m, op) in pd.Revision!.Operations())
                if (pd.Base!.GetOperation(m) is null) Add(path, m, op);
        return result;
    }

    public static List<Change> APIRemovedCheck(OpenApiDiff d, Sources src, CheckerConfig config)
    {
        var result = new List<Change>();
        var p = d.PathsDiff;
        if (p is null) return result;
        foreach (var path in p.Deleted)
        {
            var item = p.Base!.Value(path);
            if (item is null) continue;
            foreach (var (m, op) in item.Operations())
            {
                var (stability, err) = Stability.Get(op.Extensions);
                if (err is not null || stability is Stability.Alpha or Stability.Draft) continue;
                if (ApiRemoval(OpInfo.Of(config, op, src, m, path), true) is { } c) result.Add(c);
            }
        }
        foreach (var (path, pd) in p.Modified)
        {
            if (pd.OperationsDiff is null) continue;
            foreach (var m in pd.OperationsDiff.Deleted)
                if (ApiRemoval(OpInfo.Of(config, pd.Base!.GetOperation(m)!, src, m, path), false) is { } c) result.Add(c);
        }
        return result;
    }

    private static Change? ApiRemoval(OpInfo oi, bool isPath)
    {
        var (id, args, ok) = Deprecation.Removal(oi.Operation.Deprecated, oi.Operation.Extensions, new Deprecation.RemovalIds(
            isPath ? Ids.APIPathRemovedWithoutDeprecation : Ids.APIRemovedWithoutDeprecation,
            isPath ? Ids.APIPathRemovedWithDeprecation : Ids.APIRemovedWithDeprecation,
            isPath ? Ids.APIPathRemovedBeforeSunset : Ids.APIRemovedBeforeSunset,
            Ids.APIPathSunsetParse));
        if (!ok) return null;
        return oi.NewApiChange(id, args, "").WithSources(Loc.FromOrigin(oi.Sources, oi.Operation, oi.Operation.Origin), null);
    }

    private static string FormatDeprecationDetails(Dictionary<string, object?>? ext)
    {
        var (s, err) = Stability.Get(ext);
        return err is not null || s == "" ? "" : "(stability: " + s + ")";
    }

    private static string FormatDeprecationDetailsWithSunset(DateOnly sunset, Dictionary<string, object?>? ext)
    {
        var parts = new List<string> { "sunset: " + Fmt.GoV(sunset) };
        var (s, err) = Stability.Get(ext);
        if (err is null && s != "") parts.Add("stability: " + s);
        return "(" + string.Join(", ", parts) + ")";
    }

    private static readonly Deprecation.Ids ApiDeprecationIds = new(Ids.EndpointReactivated, Ids.EndpointDeprecated, Ids.EndpointDeprecatedWithSunset,
        Ids.APIDeprecatedSunsetMissing, Ids.APIDeprecatedSunsetParse, Ids.APISunsetDateTooSmall);

    public static List<Change> APIDeprecationCheck(OpenApiDiff d, Sources src, CheckerConfig config)
    {
        var result = new List<Change>();
        foreach (var (path, pd, m, md) in Walkers.ModifiedOperations(d))
        {
            if (md.DeprecatedDiff is null) continue;
            var op = pd.Revision!.GetOperation(m)!;
            var (days, ok) = Deprecation.Days(config, op.Extensions);
            if (!ok && Deprecation.NowDeprecated(md.DeprecatedDiff)) continue;
            var stage = Deprecation.Announce(md.DeprecatedDiff, op.Extensions, days, ApiDeprecationIds);
            var (b, r) = Loc.OperationField(src, md, "deprecated"); // as upstream, at every stage
            var details = stage.Id is Ids.EndpointReactivated or Ids.EndpointDeprecated or Ids.EndpointDeprecatedWithSunset ? FormatDeprecationDetails(op.Extensions) : "";
            result.Add(OpInfo.Of(config, op, src, m, path).NewApiChange(stage.Id, stage.Args, "").WithSources(b, r).WithDetails(details));
        }
        return result;
    }

    public static List<Change> APISunsetChangedCheck(OpenApiDiff d, Sources src, CheckerConfig config)
    {
        var result = new List<Change>();
        foreach (var (path, pd, m, md) in Walkers.ModifiedOperations(d))
        {
            var rev = pd.Revision!.GetOperation(m)!;
            var bop = pd.Base!.GetOperation(m)!;
            var (b, r) = Loc.OperationField(src, md, DiffConfig.SunsetExtension);
            if (!rev.Deprecated || md.ExtensionsDiff is null) continue;
            if (md.ExtensionsDiff.Deleted.Contains(DiffConfig.SunsetExtension))
            {
                result.Add(Walkers.NewApiChange(Ids.APISunsetDeleted, config, [], "", src, rev, m, path).WithSources(b, r));
                continue;
            }
            if (!md.ExtensionsDiff.Modified.ContainsKey(DiffConfig.SunsetExtension)) continue;
            var (date, err) = Deprecation.SunsetDate(rev.Ext(DiffConfig.SunsetExtension));
            if (err is not null) { result.Add(OpInfo.Of(config, rev, src, m, path).NewApiChange(Ids.APIPathSunsetParse, [err], "").WithSources(null, r)); continue; }
            var (baseDate, err2) = Deprecation.SunsetDate(bop.Ext(DiffConfig.SunsetExtension));
            if (err2 is not null) { result.Add(OpInfo.Of(config, bop, src, m, path).NewApiChange(Ids.APIPathSunsetParse, [err2], "").WithSources(b, null)); continue; }
            int days = date.DayNumber - Deprecation.Today.DayNumber;
            var (stability, serr) = Stability.Get(rev.Extensions);
            if (serr is not null) continue;
            var depDays = Stability.DeprecationDays(config, stability);
            if (baseDate > date && days < (int)depDays)
                result.Add(Walkers.NewApiChange(Ids.APISunsetDateChangedTooSmall, config, [baseDate, date, baseDate, (ulong)depDays], "", src, rev, m, path).WithSources(b, r));
        }
        return result;
    }

    public static List<Change> APIOperationIdUpdatedCheck(OpenApiDiff d, Sources src, CheckerConfig config)
    {
        var result = new List<Change>();
        foreach (var (path, pd, m, md) in Walkers.ModifiedOperations(d))
        {
            if (md.OperationIDDiff is null) continue;
            var (b, r) = Loc.OperationField(src, md, "operationId");
            var op = pd.Base!.GetOperation(m)!;
            var id = Ids.APIOperationIdRemoved;
            List<object?> args = [md.Base!.OperationID, md.Revision!.OperationID];
            if (md.OperationIDDiff.From is null or "")
            {
                id = Ids.APIOperationIdAdd;
                op = pd.Revision!.GetOperation(m)!;
                args = [md.Revision.OperationID];
                b = null;
            }
            else r = null;
            result.Add(Walkers.NewApiChange(id, config, args, "", src, op, m, path).WithSources(b, r));
        }
        return result;
    }

    public static List<Change> APITagUpdatedCheck(OpenApiDiff d, Sources src, CheckerConfig config)
    {
        var result = new List<Change>();
        foreach (var (path, pd, m, md) in Walkers.ModifiedOperations(d))
        {
            if (md.TagsDiff is null) continue;
            var bop = pd.Base!.GetOperation(m)!;
            var rop = pd.Revision!.GetOperation(m)!;
            var (fb, fr) = Loc.OperationField(src, md, "tags");
            foreach (var tag in md.TagsDiff.Deleted)
                result.Add(Walkers.NewApiChange(Ids.APITagRemoved, config, [tag], "", src, bop, m, path).WithSources(Loc.SequenceItemSource(src, bop, "tags", tag, fb), null));
            foreach (var tag in md.TagsDiff.Added)
                result.Add(Walkers.NewApiChange(Ids.APITagAdded, config, [tag], "", src, bop, m, path).WithSources(null, Loc.SequenceItemSource(src, rop, "tags", tag, fr)));
        }
        return result;
    }
}
