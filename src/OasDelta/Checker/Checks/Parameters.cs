using OasDelta.Diff;
using OasDelta.Model;

namespace OasDelta.Checker;

// Request parameter checks (query, path, header and cookie parameters).
public static partial class Checks
{
    private static Change ParamChange(ParamInfo p, string id, List<object?> args, string comment = "") => p.OpInfo.NewApiChange(id, args, comment);

    private static (SourceLoc?, SourceLoc?) ParamSchemaField(ParamInfo p, SchemaDiff? sd, string field) =>
        Loc.SchemaField(p.OpInfo.Sources, p.OpInfo.MethodDiff, sd, field);

    public static List<Change> RequestParameterBecameEnumCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.SchemaDiff is not { } sd) return;
        var (b, rv) = ParamSchemaField(p, sd, "enum");
        if (sd.EnumDiff is not { EnumAdded: true }) return;
        r.Add(ParamChange(p, Ids.RequestParameterBecameEnum, [p.Location, p.Name]).WithSchema(sd, sd, "").WithSources(b, rv));
    });

    public static List<Change> RequestParameterBecameNotEnumCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.SchemaDiff is not { } sd) return;
        if (Walkers.BecameNotEnum(sd))
        {
            var (b, rv) = ParamSchemaField(p, sd, "enum");
            r.Add(ParamChange(p, Ids.RequestParameterBecameNotEnum, [p.Location, p.Name]).WithSchema(sd, sd, "").WithSources(b, rv));
        }
        SchemaWalker.ModifiedProperties(sd, (path, name, pd, _) =>
        {
            if (!Walkers.BecameNotEnum(pd)) return;
            var (b, rv) = ParamSchemaField(p, pd, "enum");
            var full = SchemaWalker.PropertyFullName(path, name);
            r.Add(ParamChange(p, Ids.RequestParameterPropertyBecameNotEnum, [full, p.Location, p.Name]).WithSchema(sd, pd, full).WithSources(b, rv));
        });
    });

    public static List<Change> RequestParameterBecameNullableCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.SchemaDiff is not { } sd) return;
        if (Walkers.NullabilityChangeId(sd, Ids.RequestParameterBecameNullable, Ids.RequestParameterBecameNotNullable) is { Length: > 0 } id)
        {
            var (b, rv) = ParamSchemaField(p, sd, "nullable");
            r.Add(ParamChange(p, id, [p.Location, p.Name]).WithSchema(sd, sd, "").WithSources(b, rv));
        }
        SchemaWalker.ModifiedProperties(sd, (path, name, pd, _) =>
        {
            if (pd.Base is null || pd.Revision is null) return;
            if (Walkers.NullabilityChangeId(pd, Ids.RequestParameterPropertyBecameNullable, Ids.RequestParameterPropertyBecameNotNullable) is not { Length: > 0 } pid) return;
            var (b, rv) = ParamSchemaField(p, pd, "nullable");
            var full = SchemaWalker.PropertyFullName(path, name);
            r.Add(ParamChange(p, pid, [full, p.Location, p.Name]).WithSchema(sd, pd, full).WithSources(b, rv));
        });
    });

    public static List<Change> RequestParameterDefaultValueChangedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.Base is not { Required: false } || p.ParamDiff.Revision is not { Required: false }) return;
        if (p.ParamDiff.SchemaDiff is not { } sd || sd.DefaultDiff is not { } dd) return;
        var (b, rv) = ParamSchemaField(p, sd, "default");
        var ch = dd.From is null ? ParamChange(p, Ids.RequestParameterDefaultValueAdded, [p.Location, p.Name, dd.To])
            : dd.To is null ? ParamChange(p, Ids.RequestParameterDefaultValueRemoved, [p.Location, p.Name, dd.From])
            : ParamChange(p, Ids.RequestParameterDefaultValueChanged, [p.Location, p.Name, dd.From, dd.To]);
        r.Add(ch.WithSources(b, rv));
    });

    public static List<Change> RequestParameterRequiredValueUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.RequiredDiff is not { } rd) return;
        var (b, rv) = Loc.ParameterField(p.OpInfo.Sources, p.OpInfo.MethodDiff, p.ParamDiff, "required");
        var id = rd.To is true ? Ids.RequestParameterBecomeRequired : Ids.RequestParameterBecomeOptional;
        r.Add(ParamChange(p, id, [p.Location, p.Name]).WithSources(b, rv));
    });

    private static readonly Deprecation.Ids ParamDeprecationIds = new(Ids.RequestParameterReactivated, Ids.RequestParameterDeprecated, "",
        Ids.RequestParameterDeprecatedSunsetMissing, Ids.RequestParameterSunsetParse, Ids.RequestParameterSunsetDateTooSmall);

    public static List<Change> RequestParameterDeprecationCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.DeprecatedDiff is not { } dd) return;
        var opExt = p.OpInfo.Operation.Extensions;
        var (days, ok) = Deprecation.Days(c, opExt);
        if (!ok && Deprecation.NowDeprecated(dd)) return;
        var stage = Deprecation.Announce(dd, p.ParamDiff.Revision?.Extensions, days, ParamDeprecationIds);
        // as upstream: the "deprecated" field of the parameter's *schema* diff, so usually the operation's location
        var (b, rv) = Loc.SchemaField(p.OpInfo.Sources, p.OpInfo.MethodDiff, p.ParamDiff.SchemaDiff, "deprecated");
        string details = stage.Id switch
        {
            Ids.RequestParameterReactivated => FormatDeprecationDetails(opExt),
            Ids.RequestParameterDeprecated => stage.Sunset is { } sun ? FormatDeprecationDetailsWithSunset(sun, opExt) : FormatDeprecationDetails(opExt),
            _ => "",
        };
        List<object?> args = [p.Location, p.Name, .. stage.Args];
        r.Add(ParamChange(p, stage.Id, args).WithSources(b, rv).WithDetails(details));
    });

    public static List<Change> RequestParameterEnumValueUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.SchemaDiff is not { } sd) return;
        ParamEnumDiff(p, r, sd, sd, "", Ids.RequestParameterEnumValueRemoved, Ids.RequestParameterEnumValueAdded, true, v => [v, p.Location, p.Name]);
        SchemaWalker.ModifiedProperties(sd, (path, name, pd, _) =>
        {
            var full = SchemaWalker.PropertyFullName(path, name);
            ParamEnumDiff(p, r, sd, pd, full, Ids.RequestParameterPropertyEnumValueRemoved, Ids.RequestParameterPropertyEnumValueAdded, false, v => [v, full, p.Location, p.Name]);
        });
    });

    private static void ParamEnumDiff(ParamInfo p, List<Change> r, SchemaDiff root, SchemaDiff sd, string propertyPath, string removedId, string addedId,
        bool becameEnumReported, Func<object?, List<object?>> makeArgs)
    {
        if (sd.EnumDiff is null) return;
        foreach (var v in Walkers.DeletedEnumValues(sd) ?? [])
        {
            var (b, rv) = Loc.SchemaDeletedItem(p.OpInfo.Sources, p.OpInfo.MethodDiff, sd, "enum", V(v));
            r.Add(ParamChange(p, removedId, makeArgs(v)).WithSchema(root, sd, propertyPath).WithSources(b, rv));
        }
        var added = becameEnumReported ? Walkers.AddedEnumValues(sd) ?? [] : sd.EnumDiff.Added;
        foreach (var v in added)
        {
            var (b, rv) = Loc.SchemaAddedItem(p.OpInfo.Sources, p.OpInfo.MethodDiff, sd, "enum", V(v));
            r.Add(ParamChange(p, addedId, makeArgs(v)).WithSchema(root, sd, propertyPath).WithSources(b, rv));
        }
    }

    public static List<Change> RequestParameterListOfTypesChangedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.Revision is not { } param) return;
        if (p.ParamDiff.SchemaDiff is { ListOfTypesDiff: { Empty: false } ld } sd)
        {
            var narrowed = ld.Deleted.Count > 0;
            var (b, rv) = ParamSchemaField(p, sd, "type");
            r.Add(ParamChange(p, narrowed ? Ids.RequestParameterListOfTypesNarrowed : Ids.RequestParameterListOfTypesWidened,
                [param.In, param.Name, Walkers.JoinTypes(narrowed ? ld.Deleted : ld.Added)]).WithSchema(sd, sd, "").WithSources(b, rv));
        }
        if (p.ParamDiff.SchemaDiff is not { } root) return;
        SchemaWalker.ModifiedProperties(root, (path, name, pd, _) =>
        {
            if (pd.ListOfTypesDiff is not { Empty: false } ld) return;
            var narrowed = ld.Deleted.Count > 0;
            var full = SchemaWalker.PropertyFullName(path, name);
            var (b, rv) = ParamSchemaField(p, pd, "type");
            r.Add(ParamChange(p, narrowed ? Ids.RequestParameterPropertyListOfTypesNarrowed : Ids.RequestParameterPropertyListOfTypesWidened,
                [full, param.In, param.Name, Walkers.JoinTypes(narrowed ? ld.Deleted : ld.Added)]).WithSchema(root, pd, full).WithSources(b, rv));
        });
    });

    private const string PatternChangedComment = "pattern-changed-warn-comment", PatternAddedComment = "pattern-added-error-comment";

    public static List<Change> RequestParameterPatternAddedOrChangedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.SchemaDiff is not { } sd) return;
        var (b, rv) = ParamSchemaField(p, sd, "pattern");
        if (sd.PatternDiff is not { } pd) return;
        if (pd.From is "")
            r.Add(ParamChange(p, Ids.RequestParameterPatternAdded, [pd.To, p.Location, p.Name], PatternAddedComment).WithSchema(sd, sd, "").WithSources(null, rv));
        else if (pd.To is "")
            r.Add(ParamChange(p, Ids.RequestParameterPatternRemoved, [pd.From, p.Location, p.Name]).WithSchema(sd, sd, "").WithSources(b, null));
        else
        {
            bool gen = pd.To is ".*";
            r.Add(ParamChange(p, gen ? Ids.RequestParameterPatternGeneralized : Ids.RequestParameterPatternChanged, [p.Location, p.Name, pd.From, pd.To],
                gen ? "" : PatternChangedComment).WithSchema(sd, sd, "").WithSources(b, rv));
        }
    });

    public static List<Change> RequestParameterRemovedCheck(OpenApiDiff d, Sources src, CheckerConfig config)
    {
        var result = new List<Change>();
        if (d.PathsDiff is null) return result;
        foreach (var (path, pathItem) in d.PathsDiff.Modified)
        {
            if (pathItem.OperationsDiff is null) continue;
            foreach (var (method, op) in pathItem.OperationsDiff.Modified)
            {
                if (op.ParametersDiff is null || op.Base is null) continue;
                var oi = OpInfo.FromDiff(config, op, src, method, path);
                foreach (var (loc, names) in op.ParametersDiff.Deleted)
                    foreach (var name in names)
                    {
                        var param = FindParam(op.Base.Parameters, loc, name);
                        if (param is null) continue;
                        var (id, extra, ok) = Deprecation.Removal(param.Deprecated, param.Extensions, new Deprecation.RemovalIds(
                            Ids.RequestParameterRemoved, Ids.RequestParameterRemovedWithDeprecation, Ids.ParameterRemovedBeforeSunset, Ids.RequestParameterSunsetParse));
                        if (!ok) continue;
                        var comment = id == Ids.RequestParameterRemoved ? Comment(Ids.RequestParameterRemoved) : "";
                        List<object?> args = [param.In, param.Name, .. extra];
                        result.Add(oi.NewApiChange(id, args, comment).WithSources(Loc.ParameterSource(src, op.Base, param), null));
                    }
            }
        }
        return result;
    }

    private static Parameter? FindParam(List<Ref<Parameter>> ps, string loc, string name)
    {
        foreach (var r in ps) if (r.Value is { } v && v.Name == name && v.In == loc) return v;
        return null;
    }

    public static List<Change> RequestParameterSunsetChangedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        var (b, rv) = Loc.ParameterField(p.OpInfo.Sources, p.OpInfo.MethodDiff, p.ParamDiff, DiffConfig.SunsetExtension);
        var pb = p.ParamDiff.Base; var pr = p.ParamDiff.Revision;
        if (pr is null || pb is null || !pr.Deprecated || p.ParamDiff.ExtensionsDiff is not { } ed) return;
        if (ed.Deleted.Contains(DiffConfig.SunsetExtension))
        {
            r.Add(ParamChange(p, Ids.RequestParameterSunsetDeleted, [p.Location, p.Name]).WithSources(b, rv));
            return;
        }
        if (!ed.Modified.ContainsKey(DiffConfig.SunsetExtension)) return;
        var (date, err) = Deprecation.SunsetDate(pr.Extensions?.GetValueOrDefault(DiffConfig.SunsetExtension));
        if (err is not null)
        {
            r.Add(ParamChange(p, Ids.RequestParameterSunsetParse, [pr.In, pr.Name, err]).WithSources(null, rv));
            return;
        }
        var (baseDate, err2) = Deprecation.SunsetDate(pb.Extensions?.GetValueOrDefault(DiffConfig.SunsetExtension));
        if (err2 is not null)
        {
            var oi = OpInfo.Of(c, p.OpInfo.MethodDiff.Base!, s, p.OpInfo.Method, p.OpInfo.Path);
            r.Add(oi.NewApiChange(Ids.RequestParameterSunsetParse, [pb.In, pb.Name, err2], "").WithSources(b, null));
            return;
        }
        int days = date.DayNumber - Deprecation.Today.DayNumber;
        var (stability, serr) = Stability.Get(p.OpInfo.Operation.Extensions);
        if (serr is not null) return;
        var depDays = Stability.DeprecationDays(c, stability);
        if (baseDate > date && days < (int)depDays)
            r.Add(ParamChange(p, Ids.RequestParameterSunsetDateChangedTooSmall, [pr.In, pr.Name, baseDate, date, baseDate, (ulong)depDays]).WithSources(b, rv));
    });

    // ---- type changes ----

    private static bool IsParameterScalarToFormExplodeArray(ParameterDiff pd, StringsDiff? typeDiff)
    {
        if (pd.Revision is null || pd.SchemaDiff?.Base is null || pd.SchemaDiff.Revision is null || typeDiff is null) return false;
        var added = TypeRules.WithoutNull(typeDiff.Added);
        var baseTypes = TypeRules.WithoutNull(pd.SchemaDiff.Base.Type);
        if (added.Count != 1 || added[0] != "array" || baseTypes.Count == 0) return false;
        if (TypeRules.SerializationMethod(pd.Revision) is not ("form", true)) return false;
        var rev = pd.SchemaDiff.Revision;
        if (rev.Items?.Value is null) return false;
        return ItemAcceptsBaseScalarValues(pd.SchemaDiff.Base, rev.Items.Value);
    }

    private static bool ItemAcceptsBaseScalarValues(Schema? baseS, Schema? item)
    {
        if (baseS is null || item is null) return false;
        if (!TypeRules.IsTypeContained(TypeRules.WithoutNull(item.Type), TypeRules.WithoutNull(baseS.Type), false)) return false;
        var bv = baseS.ShallowClone(); var iv = item.ShallowClone();
        bv.Type = null; iv.Type = null; bv.Nullable = false; iv.Nullable = false;
        return SchemaEquivalence.RefsValidationEquivalent(new DiffConfig(), new Ref<Schema>(bv), new Ref<Schema>(iv));
    }

    public static List<Change> RequestParameterTypeChangedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.SchemaDiff is not { } sd) return;
        var (b, rv) = ParamSchemaField(p, sd, "type");
        if (sd.TypeDiff is { Empty: false } || sd.FormatDiff is not null)
        {
            var id = Ids.RequestParameterTypeGeneralized; var comment = "";
            if (TypeRules.TypeOrFormatBreaking(sd.TypeDiff, sd.FormatDiff, false, sd.Revision?.Type))
            {
                if (IsParameterScalarToFormExplodeArray(p.ParamDiff, sd.TypeDiff)) comment = Ids.RequestParameterTypeFormExplodeArrayComment;
                else id = Ids.RequestParameterTypeChanged;
            }
            r.Add(ParamChange(p, id, [p.Location, p.Name, Walkers.TypeFormatDimension(sd), Walkers.BaseTypeFormat(sd), Walkers.RevisionTypeFormat(sd)], comment)
                .WithSchema(sd, sd, "").WithSources(b, rv));
        }
        SchemaWalker.ModifiedProperties(sd, (path, name, pd, _) =>
        {
            if (pd.TypeDiff is not { Empty: false } && pd.FormatDiff is null) return;
            var (pb, prv) = ParamSchemaField(p, pd, "type");
            bool strong = TypeRules.TypeOrFormatBreaking(pd.TypeDiff, pd.FormatDiff, true, pd.Revision?.Type);
            bool loose = TypeRules.TypeOrFormatBreaking(pd.TypeDiff, pd.FormatDiff, false, pd.Revision?.Type);
            var (id, comment) = strong != loose ? (Ids.RequestParameterPropertyTypeChanged, Ids.RequestParameterPropertyTypeChangedComment)
                : strong ? (Ids.RequestParameterPropertyTypeSpecialized, "") : (Ids.RequestParameterPropertyTypeGeneralized, "");
            var full = SchemaWalker.PropertyFullName(path, name);
            r.Add(ParamChange(p, id, [p.Location, p.Name, Walkers.TypeFormatDimension(pd), full, Walkers.BaseTypeFormat(pd), Walkers.RevisionTypeFormat(pd)], comment)
                .WithSchema(sd, pd, full).WithSources(pb, prv));
        });
    });

    public static List<Change> RequestParameterXExtensibleEnumValueRemovedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.SchemaDiff is not { } sd || sd.ExtensionsDiff is not { } ed || !ed.Modified.ContainsKey(DiffConfig.XExtensibleEnumExtension)) return;
        foreach (var v in DeletedExtensibleEnumValues(sd))
        {
            var (b, rv) = Loc.SchemaDeletedItem(p.OpInfo.Sources, p.OpInfo.MethodDiff, sd, DiffConfig.XExtensibleEnumExtension, v);
            r.Add(ParamChange(p, Ids.RequestParameterXExtensibleEnumValueRemoved, [v, p.Location, p.Name]).WithSources(b, rv));
        }
    });

    private static List<string> DeletedExtensibleEnumValues(SchemaDiff sd)
    {
        if (sd.Base?.Extensions?.GetValueOrDefault(DiffConfig.XExtensibleEnumExtension) is not List<object?> from ||
            sd.Revision?.Extensions?.GetValueOrDefault(DiffConfig.XExtensibleEnumExtension) is not List<object?> to) return [];
        var toSet = to.Select(Fmt.ExtValueText).ToList();
        var deleted = new List<string>();
        foreach (var f in from) { var t = Fmt.ExtValueText(f); if (!toSet.Contains(t)) deleted.Add(t); }
        return deleted;
    }

    // ---- schema became false (request body/property, response body/property, parameter, response header) ----

    private static string FalseSchemaChangeId(SchemaDiff? d, string falseId, string notFalseId) =>
        d?.AlwaysDiff is not { } ad ? "" : ad.To is false ? falseId : ad.From is false ? notFalseId : "";

    private static string FalseItemsChangeId(SchemaDiff? d, string falseId, string notFalseId)
    {
        if (d?.ItemsDiff is null) return "";
        if (d.ItemsDiff.SchemaAdded && d.Revision is not null && IsFalseSchemaRef(d.Revision.Items)) return falseId;
        if (d.ItemsDiff.SchemaDeleted && d.Base is not null && IsFalseSchemaRef(d.Base.Items)) return notFalseId;
        return "";
    }

    private static bool IsFalseSchemaRef(Ref<Schema>? r) => r?.Value?.Always is false;

    private static List<Change> SchemaBecameFalse(bool resp, OpenApiDiff d, Sources s, CheckerConfig c) => Bodies(resp, d, s, c, (i, r) =>
    {
        string Cmt(string id) => id == Ids.ResponsePropertySchemaBecameFalse ? Ids.ResponsePropertySchemaBecameFalseComment : "";
        string bodyFalse = Side(resp, "body-schema-became-false"), bodyNotFalse = Side(resp, "body-schema-became-not-false");
        string propFalse = Side(resp, "property-schema-became-false"), propNotFalse = Side(resp, "property-schema-became-not-false");
        if (FalseSchemaChangeId(i.SchemaDiff, bodyFalse, bodyNotFalse) is { Length: > 0 } id)
        {
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, i.SchemaDiff, "type");
            r.Add(i.NewChange(id, [], Cmt(id)).WithSources(b, rv)); // upstream passes no status here, even for responses
        }
        if (FalseItemsChangeId(i.SchemaDiff, propFalse, propNotFalse) is { Length: > 0 } iid)
        {
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, i.SchemaDiff, "items");
            r.Add(i.NewChange(iid, Args(i, resp, "items"), Cmt(iid)).WithSources(b, rv));
        }
        i.WalkProperties(p =>
        {
            if (FalseSchemaChangeId(p.PropertyDiff, propFalse, propNotFalse) is { Length: > 0 } pid)
            {
                var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, "type");
                r.Add(p.NewChange(pid, Args(i, resp, p.FullName), Cmt(pid)).WithSources(b, rv));
            }
            if (FalseItemsChangeId(p.PropertyDiff, propFalse, propNotFalse) is { Length: > 0 } piid)
            {
                var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, "items");
                r.Add(p.NewChange(piid, Args(i, resp, SchemaWalker.PropertyFullName(p.PropertyPath, p.PropertyName, "items")), Cmt(piid)).WithSources(b, rv));
            }
        });
    });
    public static List<Change> RequestPropertySchemaBecameFalseCheck(OpenApiDiff d, Sources s, CheckerConfig c) => SchemaBecameFalse(false, d, s, c);
    public static List<Change> ResponsePropertySchemaBecameFalseCheck(OpenApiDiff d, Sources s, CheckerConfig c) => SchemaBecameFalse(true, d, s, c);

    public static List<Change> RequestParameterSchemaBecameFalseCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.ParamDiff.SchemaDiff is not { } sd) return;
        if (FalseSchemaChangeId(sd, Ids.RequestParameterSchemaBecameFalse, Ids.RequestParameterSchemaBecameNotFalse) is { Length: > 0 } id)
        {
            var (b, rv) = ParamSchemaField(p, sd, "type");
            r.Add(ParamChange(p, id, [p.Location, p.Name]).WithSchema(sd, sd, "").WithSources(b, rv));
        }
        SchemaWalker.ModifiedProperties(sd, (path, name, pd, _) =>
        {
            if (FalseSchemaChangeId(pd, Ids.RequestParameterPropertySchemaBecameFalse, Ids.RequestParameterPropertySchemaBecameNotFalse) is not { Length: > 0 } pid) return;
            var (b, rv) = ParamSchemaField(p, pd, "type");
            var full = SchemaWalker.PropertyFullName(path, name);
            r.Add(ParamChange(p, pid, [full, p.Location, p.Name]).WithSchema(sd, pd, full).WithSources(b, rv));
        });
    });

    public static List<Change> ResponseHeaderSchemaBecameFalseCheck(OpenApiDiff d, Sources s, CheckerConfig c)
    {
        var result = new List<Change>();
        Walkers.ModifiedResponseHeaders(d, s, c, h =>
        {
            if (h.HeaderDiff.SchemaDiff is not { } sd) return;
            if (FalseSchemaChangeId(sd, Ids.ResponseHeaderSchemaBecameFalse, Ids.ResponseHeaderSchemaBecameNotFalse) is not { Length: > 0 } id) return;
            var (b, rv) = Loc.SchemaField(s, h.OpInfo.MethodDiff, sd, "type");
            result.Add(h.OpInfo.NewApiChange(id, [h.Name, h.ResponseStatus], "").WithSchema(sd, sd, "").WithSources(b, rv));
        });
        return result;
    }

    // ---- request/response body + property type and list-of-types changes ----

    private static List<Change> TypeChanged(bool resp, OpenApiDiff d, Sources s, CheckerConfig c) => Bodies(resp, d, s, c, (i, r) =>
    {
        (string, string) IdFor(SchemaDiff sd, string scope)
        {
            bool strong = TypeRules.IsStronglyTyped(i.MediaType);
            if (!resp)
            {
                if (TypeRules.RequestTypeFormatBreaking(sd.TypeDiff, sd.FormatDiff, strong, sd)) return (Side(false, $"{scope}-type-changed"), "");
                if (TypeRules.IsRequestLooseTypeSwap(sd.TypeDiff, sd)) return (Side(false, $"{scope}-type-compatible"), TypeRules.LooselyTypedComment);
                return (Side(false, $"{scope}-type-generalized"), "");
            }
            return ResponseTypeChangeId(sd, strong, TypeRules.LooselyTypedComment, Side(true, $"{scope}-type-specialized"), Side(true, $"{scope}-type-compatible"),
                Side(true, $"{scope}-type-generalized"), Side(true, $"{scope}-type-changed"));
        }
        var sd = i.SchemaDiff;
        if (sd.TypeDiff is { Empty: false } || sd.FormatDiff is not null)
        {
            var (id, comment) = IdFor(sd, "body");
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, sd, "type");
            r.Add(i.NewChange(id, Args(i, resp, Walkers.TypeFormatDimension(sd), Walkers.BaseTypeFormat(sd), Walkers.RevisionTypeFormat(sd)), comment).WithSources(b, rv));
        }
        i.WalkProperties(p =>
        {
            var pd = p.PropertyDiff;
            if (pd.Revision is null) return;
            if (pd.TypeDiff is not { Empty: false } && pd.FormatDiff is null) return;
            var (id, comment) = IdFor(pd, "property");
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, pd, "type");
            r.Add(p.NewChange(id, Args(i, resp, p.FullName, Walkers.TypeFormatDimension(pd), Walkers.BaseTypeFormat(pd), Walkers.RevisionTypeFormat(pd)), comment).WithSources(b, rv));
        });
    });
    public static List<Change> RequestPropertyTypeChangedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => TypeChanged(false, d, s, c);
    public static List<Change> ResponsePropertyTypeChangedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => TypeChanged(true, d, s, c);

    private static (string, string) ResponseTypeChangeId(SchemaDiff sd, bool strong, string looseComment, string specializedId, string compatibleId, string generalizedId, string changedId)
    {
        if (TypeRules.ResponseTypeFormatBreaking(sd.TypeDiff, sd.FormatDiff, strong, sd))
            return (TypeRules.TypeSetWidened(sd.TypeDiff, sd) ? generalizedId : changedId, "");
        if (TypeRules.IsResponseLooseTypeSwap(sd.TypeDiff, sd)) return (compatibleId, looseComment);
        return (specializedId, "");
    }

    public static List<Change> ResponseHeaderTypeChangedCheck(OpenApiDiff d, Sources s, CheckerConfig c)
    {
        var result = new List<Change>();
        Walkers.ModifiedResponseHeaders(d, s, c, h =>
        {
            if (h.HeaderDiff.SchemaDiff is not { Base: not null, Revision: not null } sd) return;
            if (sd.TypeDiff is not { Empty: false } && sd.FormatDiff is null) return;
            var (id, comment) = ResponseTypeChangeId(sd, false, Ids.ResponseHeaderTypeCompatibleComment, Ids.ResponseHeaderTypeSpecialized,
                Ids.ResponseHeaderTypeCompatible, Ids.ResponseHeaderTypeGeneralized, Ids.ResponseHeaderTypeChanged);
            var (b, rv) = Loc.Header(s, h.OpInfo.MethodDiff, h.ResponseDiff, h.Name);
            result.Add(h.OpInfo.NewApiChange(id, [h.Name, Walkers.TypeFormatDimension(sd), Walkers.BaseTypeFormat(sd), Walkers.RevisionTypeFormat(sd), h.ResponseStatus], comment)
                .WithSchema(sd, sd, "").WithSources(b, rv));
        });
        return result;
    }

    private static List<Change> ListOfTypes(bool resp, OpenApiDiff d, Sources s, CheckerConfig c) => Bodies(resp, d, s, c, (i, r) =>
    {
        // request: narrowed when any type was deleted; response: widened when any type was added
        (string verb, List<string> types) Pick(ListOfTypesDiff ld) => resp
            ? (ld.Added.Count > 0 ? ("widened", ld.Added) : ("narrowed", ld.Deleted))
            : (ld.Deleted.Count > 0 ? ("narrowed", ld.Deleted) : ("widened", ld.Added));
        var oi = OpInfo.FromDiff(c, i.OperationItem, s, i.Method, i.Path);
        if (i.SchemaDiff.ListOfTypesDiff is { Empty: false } bld)
        {
            var (verb, types) = Pick(bld);
            var (b, rv) = Loc.SchemaField(s, i.OperationItem, i.SchemaDiff, "type");
            r.Add(oi.NewApiChange(Side(resp, $"body-list-of-types-{verb}"), Args(i, resp, Walkers.JoinTypes(types), i.MediaType), "")
                .WithSchema(i.SchemaDiff, i.SchemaDiff, "").WithSources(b, rv));
        }
        i.WalkProperties(p =>
        {
            if (p.PropertyDiff.Revision is null || p.PropertyDiff.ListOfTypesDiff is not { Empty: false } ld) return;
            var (verb, types) = Pick(ld);
            var (b, rv) = Loc.SchemaField(s, i.OperationItem, p.PropertyDiff, "type");
            r.Add(oi.NewApiChange(Side(resp, $"property-list-of-types-{verb}"), Args(i, resp, p.FullName, Walkers.JoinTypes(types), i.MediaType), "")
                .WithSchema(i.SchemaDiff, p.PropertyDiff, p.FullName).WithSources(b, rv));
        });
    });
    public static List<Change> RequestPropertyListOfTypesChangedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ListOfTypes(false, d, s, c);
    public static List<Change> ResponsePropertyListOfTypesChangedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ListOfTypes(true, d, s, c);

    // ---- pattern on request/response properties ----

    public static List<Change> RequestPropertyPatternUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) => i.WalkProperties(p =>
    {
        if (p.PropertyDiff.PatternDiff is not { } pd) return;
        var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, "pattern");
        if (pd.To is "") r.Add(p.NewChange(Ids.RequestPropertyPatternRemoved, [pd.From, p.FullName], "").WithSources(b, rv));
        else if (pd.From is "") r.Add(p.NewChange(Ids.RequestPropertyPatternAdded, [pd.To, p.FullName], PatternAddedComment).WithSources(b, rv));
        else
        {
            bool gen = pd.To is ".*";
            r.Add(p.NewChange(gen ? Ids.RequestPropertyPatternGeneralized : Ids.RequestPropertyPatternChanged, [p.FullName, pd.From, pd.To], gen ? "" : PatternChangedComment).WithSources(b, rv));
        }
    }));

    public static List<Change> ResponsePatternAddedOrChangedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) => i.WalkProperties(p =>
    {
        if (p.PropertyDiff.PatternDiff is not { } pd) return;
        var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, "pattern");
        string id = Ids.ResponsePropertyPatternChanged;
        List<object?> args = [p.FullName, pd.From, pd.To, i.ResponseStatus];
        if (pd.To is "" or null) { id = Ids.ResponsePropertyPatternRemoved; args = [p.FullName, pd.From, i.ResponseStatus]; }
        else if (pd.From is "" or null) { id = Ids.ResponsePropertyPatternAdded; args = [p.FullName, pd.To, i.ResponseStatus]; }
        r.Add(p.NewChange(id, args, "").WithSources(b, rv));
    }));
}
