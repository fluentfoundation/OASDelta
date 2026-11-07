using OasDelta.Diff;
using OasDelta.Model;

namespace OasDelta.Checker;

// Request body, new-parameter, header-property and request-property checks.
public static partial class Checks
{
    public static List<Change> AddedRequestBodyCheck(OpenApiDiff d, Sources s, CheckerConfig c)
    {
        var result = new List<Change>();
        foreach (var (path, _, m, md) in Walkers.ModifiedOperations(d))
        {
            if (md.RequestBodyDiff is not { Added: true }) continue;
            var id = md.Revision!.RequestBody!.Value!.Required ? Ids.AddedRequiredRequestBody : Ids.AddedOptionalRequestBody;
            result.Add(OpInfo.FromDiff(c, md, s, m, path).NewApiChange(id, [], "").WithSources(null, Loc.RequestBody(s, md.Revision)));
        }
        return result;
    }

    public static List<Change> RequestBodyRemovedCheck(OpenApiDiff d, Sources s, CheckerConfig c)
    {
        var result = new List<Change>();
        foreach (var (path, _, m, md) in Walkers.ModifiedOperations(d))
        {
            if (md.RequestBodyDiff is not { Deleted: true }) continue;
            result.Add(OpInfo.FromDiff(c, md, s, m, path).NewApiChange(Ids.RequestBodyRemoved, [], "").WithSources(Loc.RequestBody(s, md.Base), null));
        }
        return result;
    }

    public static List<Change> RequestBodyRequiredUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c)
    {
        var result = new List<Change>();
        foreach (var (path, _, m, md) in Walkers.ModifiedOperations(d))
        {
            if (md.RequestBodyDiff?.RequiredDiff is not { } rd) continue;
            var id = rd.To is true ? Ids.RequestBodyBecameRequired : Ids.RequestBodyBecameOptional;
            var (b, rv) = Loc.RequestBodyField(s, md, "required");
            result.Add(OpInfo.FromDiff(c, md, s, m, path).NewApiChange(id, [], "").WithSources(b, rv));
        }
        return result;
    }

    public static List<Change> RequestBodyMediaTypeChangedCheck(OpenApiDiff d, Sources s, CheckerConfig c)
    {
        var result = new List<Change>();
        foreach (var (path, _, m, md) in Walkers.ModifiedOperations(d))
        {
            // upstream requires the modified-media-type map to be non-nil, which it always is once a content diff exists
            if (md.RequestBodyDiff?.ContentDiff is not { } cd) continue;
            var oi = OpInfo.FromDiff(c, md, s, m, path);
            foreach (var mt in cd.MediaTypeAdded)
                result.Add(oi.NewApiChange(Ids.RequestBodyMediaTypeAdded, [mt], "").WithSources(null, Loc.RequestBodyMediaType(s, md.Revision, mt)));
            foreach (var mt in cd.MediaTypeDeleted)
                result.Add(oi.NewApiChange(Ids.RequestBodyMediaTypeRemoved, [mt], "").WithSources(Loc.RequestBodyMediaType(s, md.Base, mt), null));
        }
        return result;
    }

    // ---- new parameters ----

    public static List<Change> NewRequestNonPathDefaultParameterCheck(OpenApiDiff d, Sources s, CheckerConfig c)
    {
        var result = new List<Change>();
        if (d.PathsDiff is null) return result;
        foreach (var (path, pd) in d.PathsDiff.Modified)
        {
            if (pd.ParametersDiff is null || pd.Revision is null) continue;
            var ops = pd.Revision.Operations();
            if (ops.Count == 0) continue;
            foreach (var (loc, names) in pd.ParametersDiff.Added)
            {
                if (loc == "path") continue;
                foreach (var pr in pd.Revision.Parameters)
                {
                    var param = pr.Value!;
                    if (!names.Contains(param.Name)) continue;
                    var id = param.Required ? Ids.NewRequiredRequestDefaultParameterToExistingPath : Ids.NewOptionalRequestDefaultParameterToExistingPath;
                    foreach (var (method, op) in ops)
                        result.Add(Walkers.NewApiChange(id, c, [loc, param.Name], "", s, op, method, path).WithSources(null, Loc.ParameterSource(s, op, param)));
                }
            }
        }
        return result;
    }

    public static List<Change> NewRequestNonPathParameterCheck(OpenApiDiff d, Sources s, CheckerConfig c)
    {
        var result = new List<Change>();
        foreach (var (path, _, m, md) in Walkers.ModifiedOperations(d))
        {
            if (md.ParametersDiff is null) continue;
            var oi = OpInfo.FromDiff(c, md, s, m, path);
            foreach (var (loc, names) in md.ParametersDiff.Added)
            {
                if (loc == "path") continue;
                foreach (var name in names)
                    foreach (var pr in md.Revision!.Parameters)
                    {
                        // upstream matches by name only, ignoring the location
                        if (pr.Value!.Name != name) continue;
                        var id = pr.Value.Required ? Ids.NewRequiredRequestParameter : Ids.NewOptionalRequestParameter;
                        result.Add(oi.NewApiChange(id, [loc, name], "").WithSources(null, Loc.ParameterSource(s, md.Revision, pr.Value)));
                        break;
                    }
            }
        }
        return result;
    }

    public static List<Change> NewRequestPathParameterCheck(OpenApiDiff d, Sources s, CheckerConfig c)
    {
        var result = new List<Change>();
        foreach (var (path, _, m, md) in Walkers.ModifiedOperations(d))
        {
            if (md.ParametersDiff is null) continue;
            var oi = OpInfo.FromDiff(c, md, s, m, path);
            foreach (var (loc, names) in md.ParametersDiff.Added)
            {
                if (loc != "path") continue;
                foreach (var name in names)
                {
                    SourceLoc? rv = null;
                    if (FindParam(md.Revision!.Parameters, loc, name) is { } param) rv = Loc.ParameterSource(s, md.Revision, param);
                    result.Add(oi.NewApiChange(Ids.NewRequestPathParameter, [name], "").WithSources(null, rv));
                }
            }
        }
        return result;
    }

    // ---- header parameter object properties ----

    public static List<Change> NewRequiredRequestHeaderPropertyCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.Location != "header") return;
        var (b, rv) = Loc.Parameter(p.OpInfo.Sources, p.OpInfo.MethodDiff, p.ParamDiff);
        SchemaWalker.AddedProperties(p.ParamDiff.SchemaDiff, (path, name, prop, parent, underAllOf) =>
        {
            if (prop.ReadOnly || !(parent.Revision?.Required.Contains(name) ?? false)) return;
            var full = SchemaWalker.PropertyFullName(path, name);
            r.Add(ParamChange(p, Ids.NewRequiredRequestHeaderProperty, [p.Name, full]).WithSchema(p.ParamDiff.SchemaDiff, parent, full)
                .WithAllOfDisclaimers(underAllOf, null).WithSources(b, rv));
        });
    });

    public static List<Change> RequestHeaderPropertyBecameEnumCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.Location != "header" || p.ParamDiff.SchemaDiff is not { } sd) return;
        var (b, rv) = ParamSchemaField(p, sd, "enum");
        if (sd.EnumDiff is { EnumAdded: true })
            r.Add(ParamChange(p, Ids.RequestHeaderPropertyBecameEnum, [p.Name]).WithSchema(sd, sd, "").WithSources(b, rv));
        SchemaWalker.ModifiedProperties(sd, (path, name, pd, _) =>
        {
            if (pd.EnumDiff is not { EnumAdded: true }) return;
            var (pb, prv) = ParamSchemaField(p, pd, "enum");
            var full = SchemaWalker.PropertyFullName(path, name);
            r.Add(ParamChange(p, Ids.RequestHeaderPropertyBecameEnum, [p.Name, full]).WithSchema(sd, pd, full).WithSources(pb, prv));
        });
    });

    public static List<Change> RequestHeaderPropertyBecameRequiredCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Params(d, s, c, (p, r) =>
    {
        if (p.Location != "header" || p.ParamDiff.SchemaDiff is not { } sd) return;
        if (sd.RequiredDiff is { } rd)
            foreach (var name in rd.Added)
            {
                if (!sd.Revision!.Properties.TryGetValue(name, out var rp) || rp is null) continue;
                if (rp.Value!.ReadOnly) continue;
                if (!sd.Base!.Properties.TryGetValue(name, out var bp) || bp is null) continue;
                var (b, rv) = Loc.SchemaAddedItem(p.OpInfo.Sources, p.OpInfo.MethodDiff, sd, "required", name);
                r.Add(ParamChange(p, Ids.RequestHeaderPropertyBecameRequired, [p.Name, name]).WithSchema(sd, sd, "").WithSources(b, rv));
            }
        SchemaWalker.ModifiedProperties(sd, (path, pname, pd, _) =>
        {
            if (pd.RequiredDiff is not { } prd) return;
            foreach (var name in prd.Added)
            {
                if (!pd.Revision!.Properties.TryGetValue(name, out var rp) || rp is null) continue;
                if (rp.Value!.ReadOnly) continue;
                var (b, rv) = Loc.SchemaAddedItem(p.OpInfo.Sources, p.OpInfo.MethodDiff, pd, "required", name);
                var full = SchemaWalker.PropertyFullName(path, SchemaWalker.PropertyFullName(pname, name));
                r.Add(ParamChange(p, Ids.RequestHeaderPropertyBecameRequired, [p.Name, full]).WithSchema(sd, pd, full).WithSources(b, rv));
            }
        });
    });

    // ---- request body properties ----

    private const string RequiredRequestPropertyWithDefaultComment = "required-request-property-with-default-error-comment";

    private static bool ChangedRequiredPropertyRelevant(SchemaDiff sd, string name) =>
        sd.Base!.Properties.TryGetValue(name, out var b) && b is not null &&
        sd.Revision!.Properties.TryGetValue(name, out var r) && r is not null && !r.Value!.ReadOnly;

    public static List<Change> RequestPropertyRequiredUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
    {
        void Process(SchemaDiff sd, string path, string pname)
        {
            if (sd.RequiredDiff is not { } rd) return;
            var schemaPath = SchemaWalker.PropertyFullName(path, pname);
            foreach (var name in rd.Added)
            {
                if (!ChangedRequiredPropertyRelevant(sd, name)) continue;
                var (b, rv) = Loc.SchemaAddedItem(i.Sources, i.OperationItem, sd, "required", name);
                List<object?> args = [SchemaWalker.PropertyFullName(path, SchemaWalker.PropertyFullName(pname, name))];
                var ch = sd.Revision!.Properties[name].Value!.Default is null
                    ? i.NewChange(Ids.RequestPropertyBecameRequired, args, "")
                    : i.NewChange(Ids.RequestPropertyBecameRequiredWithDefault, args, RequiredRequestPropertyWithDefaultComment);
                r.Add(ch.WithSchema(i.SchemaDiff, sd, schemaPath).WithSources(b, rv));
            }
            foreach (var name in rd.Deleted)
            {
                if (!ChangedRequiredPropertyRelevant(sd, name)) continue;
                var (b, rv) = Loc.SchemaDeletedItem(i.Sources, i.OperationItem, sd, "required", name);
                List<object?> args = [SchemaWalker.PropertyFullName(path, SchemaWalker.PropertyFullName(pname, name))];
                r.Add(i.NewChange(Ids.RequestPropertyBecameOptional, args, "").WithSchema(i.SchemaDiff, sd, schemaPath).WithSources(b, rv));
            }
        }
        Process(i.SchemaDiff, "", "");
        i.WalkProperties(p => Process(p.PropertyDiff, p.PropertyPath, p.PropertyName));
    });

    public static List<Change> RequestPropertyUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) =>
    {
        if (i.SchemaDiff.OneOfWrappingDiff is { Empty: false } w)
        {
            var (id, comment) = w.OriginalPreserved
                ? (Ids.RequestBodyWrappedInOneOfOriginalPreserved, "wrapped-in-one-of-original-preserved-warning-comment")
                : (Ids.RequestBodyWrappedInOneOf, "");
            r.Add(i.NewChange(id, [], comment).WithSources(null, null));
        }
        SchemaWalker.DeletedProperties(i.SchemaDiff, (path, name, prop, parent, underAllOf) =>
        {
            if (prop.ReadOnly) return;
            if (parent.OneOfWrappingDiff is { } pw && pw.MovedProperties.Contains(name)) return;
            var (id, extra, ok) = Deprecation.Removal(prop.Deprecated, prop.Extensions, new Deprecation.RemovalIds(
                Ids.RequestPropertyRemoved, Ids.RequestPropertyRemovedWithDeprecation, Ids.RequestPropertyRemovedBeforeSunset, ""));
            if (!ok) return;
            var full = SchemaWalker.PropertyFullName(path, name);
            List<object?> args = [full, .. extra];
            r.Add(i.NewChange(id, args, "").WithSchema(i.SchemaDiff, parent, full).WithAllOfDisclaimers(underAllOf, null)
                .WithSources(Loc.Property(i.Sources, i.OperationItem.Base, prop), null));
        });
        SchemaWalker.AddedProperties(i.SchemaDiff, (path, name, prop, parent, underAllOf) =>
        {
            if (prop.ReadOnly) return;
            var full = SchemaWalker.PropertyFullName(path, name);
            var rv = Loc.Property(i.Sources, i.OperationItem.Revision, prop);
            Change ch = parent.Revision!.Required.Contains(name)
                ? prop.Default is null
                    ? i.NewChange(Ids.NewRequiredRequestProperty, [full], "")
                    : i.NewChange(Ids.NewRequiredRequestPropertyWithDefault, [full], RequiredRequestPropertyWithDefaultComment)
                : i.NewChange(Ids.NewOptionalRequestProperty, [full], "");
            r.Add(ch.WithSchema(i.SchemaDiff, parent, full).WithAllOfDisclaimers(underAllOf, null).WithSources(null, rv));
        });
    });

    public static List<Change> RequestPropertyXExtensibleEnumValueRemovedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) => i.WalkProperties(p =>
    {
        if (p.PropertyDiff.ExtensionsDiff is not { } ed || !ed.Modified.ContainsKey(DiffConfig.XExtensibleEnumExtension)) return;
        foreach (var v in DeletedExtensibleEnumValues(p.PropertyDiff))
        {
            var (b, rv) = Loc.SchemaDeletedItem(i.Sources, i.OperationItem, p.PropertyDiff, DiffConfig.XExtensibleEnumExtension, v);
            r.Add(p.NewChange(Ids.RequestPropertyXExtensibleEnumValueRemoved, [v, p.FullName], "").WithSources(b, rv));
        }
    }));

    public static List<Change> RequestPropertyWriteOnlyReadOnlyCheck(OpenApiDiff d, Sources s, CheckerConfig c) => RequestBodies(d, s, c, (i, r) => i.WalkProperties(p =>
    {
        var parent = p.Parent!;
        if (!parent.Revision!.Properties.TryGetValue(p.PropertyName, out var rp) || rp is null) return;
        bool requiredBoth = parent.Base!.Required.Contains(p.PropertyName) && parent.Revision.Required.Contains(p.PropertyName);
        if (p.PropertyDiff.WriteOnlyDiff is { } wo)
        {
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, "writeOnly");
            bool on = wo.To is true;
            var id = requiredBoth
                ? on ? Ids.RequestRequiredPropertyBecameWriteOnlyCheck : Ids.RequestRequiredPropertyBecameNonWriteOnlyCheck
                : on ? Ids.RequestOptionalPropertyBecameWriteOnlyCheck : Ids.RequestOptionalPropertyBecameNonWriteOnlyCheck;
            r.Add(p.NewChange(id, [p.FullName], "").WithSources(b, rv));
        }
        if (p.PropertyDiff.ReadOnlyDiff is { } ro)
        {
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, "readOnly");
            bool on = ro.To is true;
            var id = requiredBoth
                ? on ? Ids.RequestRequiredPropertyBecameReadOnlyCheck : Ids.RequestRequiredPropertyBecameNonReadOnlyCheck
                : on ? Ids.RequestOptionalPropertyBecameReadOnlyCheck : Ids.RequestOptionalPropertyBecameNonReadOnlyCheck;
            var comment = "";
            if (on) comment = "request-property-became-read-only-comment";
            else if (requiredBoth && OpenApiDoc.Is31OrLater(d.BaseOpenAPI)) { id = Ids.RequestRequiredPropertyBecameNonReadOnly31; comment = "required-in-requests-comment"; }
            r.Add(p.NewChange(id, [p.FullName], comment).WithoutGuard(Guard.ReadOnly).WithSources(b, rv));
        }
    }));
}
