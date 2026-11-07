using System.Globalization;
using OasDelta.Diff;
using OasDelta.Model;

namespace OasDelta.Checker;

// Response status, header, media-type and property checks; discriminators; property deprecation; webhooks.
public static partial class Checks
{
    private static IEnumerable<(string path, string method, MethodDiff md, OpInfo oi, string status, ResponseDiff rd)> ModifiedResponses(OpenApiDiff d, Sources s, CheckerConfig c)
    {
        foreach (var (path, _, m, md) in Walkers.ModifiedOperations(d))
        {
            if (md.ResponsesDiff is null) continue;
            var oi = OpInfo.FromDiff(c, md, s, m, path);
            foreach (var (status, rd) in md.ResponsesDiff.Modified) yield return (path, m, md, oi, status, rd);
        }
    }

    // ---- headers ----

    public static List<Change> ResponseHeaderAddedCheck(OpenApiDiff d, Sources s, CheckerConfig c)
    {
        var result = new List<Change>();
        foreach (var (_, _, md, oi, status, rd) in ModifiedResponses(d, s, c))
        {
            if (rd.HeadersDiff is null) continue;
            foreach (var name in rd.HeadersDiff.Added)
            {
                if (!rd.Revision!.Headers.TryGetValue(name, out var h) || h is null) continue;
                result.Add(oi.NewApiChange(Ids.ResponseHeaderAdded, [name, status], "").WithSources(null, Loc.FromOrigin(s, md.Revision, h.Value!.Origin)));
            }
        }
        return result;
    }

    public static List<Change> ResponseHeaderRemovedCheck(OpenApiDiff d, Sources s, CheckerConfig c)
    {
        var result = new List<Change>();
        foreach (var (_, _, md, oi, status, rd) in ModifiedResponses(d, s, c))
        {
            if (rd.HeadersDiff is null) continue;
            foreach (var name in rd.HeadersDiff.Deleted)
            {
                if (!rd.Base!.Headers.TryGetValue(name, out var h) || h is null) continue;
                var header = h.Value!;
                var id = header.Required ? Ids.RequiredResponseHeaderRemoved : Ids.OptionalResponseHeaderRemoved;
                result.Add(oi.NewApiChange(id, [name, status], "").WithSources(Loc.FromOrigin(s, md.Base, header.Origin), null));
            }
        }
        return result;
    }

    public static List<Change> ResponseHeaderBecameNullableCheck(OpenApiDiff d, Sources s, CheckerConfig c)
    {
        var result = new List<Change>();
        Walkers.ModifiedResponseHeaders(d, s, c, h =>
        {
            if (h.HeaderDiff.SchemaDiff is not { } sd) return;
            if (Walkers.NullabilityChangeId(sd, Ids.ResponseHeaderBecameNullable, Ids.ResponseHeaderBecameNotNullable) is not { Length: > 0 } id) return;
            var (b, rv) = Loc.Header(s, h.OpInfo.MethodDiff, h.ResponseDiff, h.Name);
            result.Add(h.OpInfo.NewApiChange(id, [h.Name, h.ResponseStatus], "").WithSources(b, rv));
        });
        return result;
    }

    public static List<Change> ResponseHeaderBecameOptionalCheck(OpenApiDiff d, Sources s, CheckerConfig c)
    {
        var result = new List<Change>();
        Walkers.ModifiedResponseHeaders(d, s, c, h =>
        {
            if (h.HeaderDiff.RequiredDiff is not { From: true }) return;
            var (b, rv) = Loc.Header(s, h.OpInfo.MethodDiff, h.ResponseDiff, h.Name);
            result.Add(h.OpInfo.NewApiChange(Ids.ResponseHeaderBecameOptional, [h.Name, h.ResponseStatus], "").WithSources(b, rv));
        });
        return result;
    }

    // ---- status codes ----

    public static List<Change> ResponseSuccessStatusUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) =>
        ResponseStatusUpdated(d, s, c, st => st is >= 200 and <= 299, Ids.ResponseSuccessStatusRemoved, Ids.ResponseSuccessStatusAdded);

    public static List<Change> ResponseNonSuccessStatusUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) =>
        ResponseStatusUpdated(d, s, c, st => st is < 200 or > 299, Ids.ResponseNonSuccessStatusRemoved, Ids.ResponseNonSuccessStatusAdded);

    private static bool GoAtoi(string s, out int v) => int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v);

    private static List<Change> ResponseStatusUpdated(OpenApiDiff d, Sources s, CheckerConfig c, Func<int, bool> filter, string removedId, string addedId)
    {
        var result = new List<Change>();
        foreach (var (path, _, m, md) in Walkers.ModifiedOperations(d))
        {
            if (md.ResponsesDiff is null) continue;
            var oi = OpInfo.FromDiff(c, md, s, m, path);
            foreach (var status in md.ResponsesDiff.Deleted)
                if (GoAtoi(status, out var st) && filter(st))
                    result.Add(oi.NewApiChange(removedId, [status], "").WithSources(Loc.ResponseSource(s, md.Base, status), null));
            foreach (var status in md.ResponsesDiff.Added)
                if (GoAtoi(status, out var st) && filter(st))
                    result.Add(oi.NewApiChange(addedId, [status], "").WithSources(null, Loc.ResponseSource(s, md.Revision, status)));
        }
        return result;
    }

    // ---- media types ----

    public static List<Change> ResponseMediaTypeUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c)
    {
        var result = new List<Change>();
        foreach (var (_, _, md, oi, status, rd) in ModifiedResponses(d, s, c))
        {
            if (rd.ContentDiff is null) continue;
            foreach (var mt in rd.ContentDiff.MediaTypeDeleted)
                result.Add(oi.NewApiChange(Ids.ResponseMediaTypeRemoved, [mt, status], "").WithSources(Loc.MediaType(s, md.Base, rd.Base, mt), null));
            foreach (var mt in rd.ContentDiff.MediaTypeAdded)
                result.Add(oi.NewApiChange(Ids.ResponseMediaTypeAdded, [mt, status], "").WithSources(null, Loc.MediaType(s, md.Revision, rd.Revision, mt)));
        }
        return result;
    }

    public static List<Change> ResponseMediaTypeNameUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c)
    {
        var result = new List<Change>();
        foreach (var (_, _, md, oi, status, rd) in ModifiedResponses(d, s, c))
        {
            if (rd.ContentDiff is null) continue;
            foreach (var (_, mtd) in rd.ContentDiff.MediaTypeModified)
            {
                if (mtd.NameDiff is not { } nd) continue;
                var from = nd.NameDiff?.From as string ?? ""; var to = nd.NameDiff?.To as string ?? "";
                var (b, rv) = Loc.ResponseMediaTypeName(s, md, rd, from, to);
                if (nd.ParametersDiff is { Empty: false } pd)
                {
                    if (pd.Mixed || nd.BareNameChanged)
                        result.Add(oi.NewApiChange(Ids.ResponseMediaTypeNameChanged, [nd.NameDiff?.From, nd.NameDiff?.To, status], Ids.ResponseMediaTypeNameChangedComment).WithSources(b, rv));
                    var bare = nd.BaseBareName;
                    foreach (var p in pd.Added) result.Add(oi.NewApiChange(Ids.ResponseMediaTypeParameterAdded, [p, bare, status], "").WithSources(b, rv));
                    foreach (var p in pd.Deleted) result.Add(oi.NewApiChange(Ids.ResponseMediaTypeParameterRemoved, [p, bare, status], "").WithSources(b, rv));
                    foreach (var (p, vd) in pd.Modified) result.Add(oi.NewApiChange(Ids.ResponseMediaTypeParameterChanged, [p, bare, vd.From, vd.To, status], "").WithSources(b, rv));
                    continue;
                }
                var id = nd.Contained ? Ids.ResponseMediaTypeNameSpecialized : Ids.ResponseMediaTypeNameGeneralized;
                result.Add(oi.NewApiChange(id, [nd.NameDiff?.From, nd.NameDiff?.To, status], "").WithSources(b, rv));
            }
        }
        return result;
    }

    private enum OneSided { Regular, False, NoContract }

    private static OneSided ClassifyOneSidedSchema(Schema? schema)
    {
        if (schema is null) return OneSided.Regular;
        if (schema.Always is false) return OneSided.False;
        if (SchemaEquivalence.RefsValidationEquivalent(new DiffConfig(), new Ref<Schema>(new Schema()), new Ref<Schema>(schema))) return OneSided.NoContract;
        return OneSided.Regular;
    }

    private static Schema? RequestMediaTypeSchema(Operation? op, string mt) =>
        op?.RequestBody?.Value is { } rb && rb.Content.TryGetValue(mt, out var m) && m?.Schema is not null ? m.Schema.Value : null;

    private static Schema? ResponseMediaTypeSchema(Response? r, string mt) =>
        r is not null && r.Content.TryGetValue(mt, out var m) && m?.Schema is not null ? m.Schema.Value : null;

    private static bool ResponseMediaTypeHasSchema(Response? r, string mt) => r is not null && r.Content.TryGetValue(mt, out var m) && m?.Schema is not null;

    private static List<object?> MediaTypeSchemaArgs(string id, string mt, string status) =>
        id == Ids.RequestBodySchemaBecameFalse || id == Ids.ResponseBodySchemaBecameFalse ? [] : status == "" ? [mt] : [mt, status];

    public static List<Change> MediaTypeSchemaExistenceCheck(OpenApiDiff d, Sources s, CheckerConfig c)
    {
        var result = new List<Change>();
        foreach (var (path, _, m, md) in Walkers.ModifiedOperations(d))
        {
            var oi = OpInfo.FromDiff(c, md, s, m, path);
            if (md.RequestBodyDiff?.ContentDiff is { } cd)
                foreach (var (mt, mtd) in cd.MediaTypeModified)
                    foreach (var (sd, addedId, removedId) in new[] {
                        (mtd.SchemaDiff, Ids.RequestBodyMediaTypeSchemaAdded, Ids.RequestBodyMediaTypeSchemaRemoved),
                        (mtd.ItemSchemaDiff, Ids.RequestBodyMediaTypeItemSchemaAdded, Ids.RequestBodyMediaTypeItemSchemaRemoved) })
                    {
                        if (sd is null) continue;
                        bool regular = addedId == Ids.RequestBodyMediaTypeSchemaAdded;
                        if (sd.SchemaAdded)
                        {
                            var id = addedId;
                            if (regular)
                            {
                                var k = ClassifyOneSidedSchema(RequestMediaTypeSchema(md.Revision, mt));
                                if (k == OneSided.NoContract) continue;
                                if (k == OneSided.False) id = Ids.RequestBodySchemaBecameFalse;
                            }
                            result.Add(oi.NewApiChange(id, MediaTypeSchemaArgs(id, mt, ""), "").WithSources(null, Loc.RequestBodyMediaType(s, md.Revision, mt)));
                        }
                        else if (sd.SchemaDeleted)
                        {
                            if (regular && ClassifyOneSidedSchema(RequestMediaTypeSchema(md.Base, mt)) == OneSided.NoContract) continue;
                            result.Add(oi.NewApiChange(removedId, [mt], "").WithSources(Loc.RequestBodyMediaType(s, md.Base, mt), null));
                        }
                    }
            if (md.ResponsesDiff is null) continue;
            foreach (var (status, rd) in md.ResponsesDiff.Modified)
            {
                if (rd.ContentDiff is null) continue;
                foreach (var (mt, mtd) in rd.ContentDiff.MediaTypeModified)
                {
                    var addedSource = Loc.MediaType(s, md.Revision, rd.Revision, mt);
                    var removedSource = Loc.MediaType(s, md.Base, rd.Base, mt);
                    if (mtd.SchemaDiff is { SchemaAdded: true })
                    {
                        var id = Ids.ResponseBodyMediaTypeSchemaAdded;
                        var k = ClassifyOneSidedSchema(ResponseMediaTypeSchema(rd.Revision, mt));
                        if (k == OneSided.NoContract) continue;
                        if (k == OneSided.False) id = Ids.ResponseBodySchemaBecameFalse;
                        result.Add(oi.NewApiChange(id, MediaTypeSchemaArgs(id, mt, status), "").WithSources(null, addedSource));
                    }
                    else if (mtd.SchemaDiff is { SchemaDeleted: true })
                    {
                        if (ClassifyOneSidedSchema(ResponseMediaTypeSchema(rd.Base, mt)) == OneSided.NoContract) continue;
                        result.Add(oi.NewApiChange(Ids.ResponseBodyMediaTypeSchemaRemoved, [mt, status], "").WithSources(removedSource, null));
                    }
                    if (mtd.ItemSchemaDiff is { SchemaAdded: true })
                        result.Add(oi.NewApiChange(Ids.ResponseBodyMediaTypeItemSchemaAdded, [mt, status], "").WithSources(null, addedSource));
                    else if (mtd.ItemSchemaDiff is { SchemaDeleted: true })
                    {
                        var id = ResponseMediaTypeHasSchema(rd.Revision, mt) ? Ids.ResponseBodyMediaTypeItemSchemaRemoved : Ids.ResponseBodyMediaTypeItemSchemaRemovedUntyped;
                        result.Add(oi.NewApiChange(id, [mt, status], "").WithSources(removedSource, null));
                    }
                }
            }
        }
        return result;
    }

    // ---- response properties ----

    public static List<Change> ResponseOptionalPropertyUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
    {
        SchemaWalker.DeletedProperties(i.SchemaDiff, (path, name, prop, parent, underAllOf) =>
        {
            if (parent.Base!.Required.Contains(name)) return;
            if (parent.OneOfWrappingDiff is { } w && w.MovedProperties.Contains(name)) return;
            var id = prop.WriteOnly ? Ids.ResponseOptionalWriteOnlyPropertyRemoved : Ids.ResponseOptionalPropertyRemoved;
            var full = SchemaWalker.PropertyFullName(path, name);
            r.Add(i.NewChange(id, [full, i.ResponseStatus], "").WithSchema(i.SchemaDiff, parent, full).WithAllOfDisclaimers(underAllOf, null)
                .WithSources(Loc.Property(i.Sources, i.OperationItem.Base, prop), null));
        });
        SchemaWalker.AddedProperties(i.SchemaDiff, (path, name, prop, parent, underAllOf) =>
        {
            if (parent.Revision!.Required.Contains(name)) return;
            var id = prop.WriteOnly ? Ids.ResponseOptionalWriteOnlyPropertyAdded : Ids.ResponseOptionalPropertyAdded;
            var full = SchemaWalker.PropertyFullName(path, name);
            r.Add(i.NewChange(id, [full, i.ResponseStatus], "").WithSchema(i.SchemaDiff, parent, full).WithAllOfDisclaimers(underAllOf, null)
                .WithSources(null, Loc.Property(i.Sources, i.OperationItem.Revision, prop)));
        });
    });

    public static List<Change> ResponseRequiredPropertyUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
    {
        if (i.SchemaDiff.OneOfWrappingDiff is { Empty: false } w)
        {
            var (id, comment) = w.OriginalPreserved
                ? (Ids.ResponseBodyWrappedInOneOfOriginalPreserved, "wrapped-in-one-of-original-preserved-warning-comment")
                : (Ids.ResponseBodyWrappedInOneOf, "");
            r.Add(i.NewChange(id, [], comment).WithSources(null, null));
        }
        SchemaWalker.DeletedProperties(i.SchemaDiff, (path, name, prop, parent, underAllOf) =>
        {
            var id = prop.WriteOnly ? Ids.ResponseRequiredWriteOnlyPropertyRemoved : Ids.ResponseRequiredPropertyRemoved;
            if (!parent.Base!.Required.Contains(name)) return;
            if (parent.OneOfWrappingDiff is { } pw && pw.MovedProperties.Contains(name)) return;
            var full = SchemaWalker.PropertyFullName(path, name);
            List<object?> args = [full, i.ResponseStatus];
            if (!prop.WriteOnly)
            {
                var (sid, extra, ok) = Deprecation.Removal(prop.Deprecated, prop.Extensions, new Deprecation.RemovalIds(
                    Ids.ResponseRequiredPropertyRemoved, Ids.ResponseRequiredPropertyRemovedWithDeprecation, Ids.ResponseRequiredPropertyRemovedBeforeSunset, ""));
                if (!ok) return;
                id = sid; args.AddRange(extra);
            }
            r.Add(i.NewChange(id, args, "").WithSchema(i.SchemaDiff, parent, full).WithAllOfDisclaimers(underAllOf, null)
                .WithSources(Loc.Property(i.Sources, i.OperationItem.Base, prop), null));
        });
        SchemaWalker.AddedProperties(i.SchemaDiff, (path, name, prop, parent, underAllOf) =>
        {
            var id = prop.WriteOnly ? Ids.ResponseRequiredWriteOnlyPropertyAdded : Ids.ResponseRequiredPropertyAdded;
            if (!parent.Revision!.Required.Contains(name)) return;
            var full = SchemaWalker.PropertyFullName(path, name);
            r.Add(i.NewChange(id, [full, i.ResponseStatus], "").WithSchema(i.SchemaDiff, parent, full).WithAllOfDisclaimers(underAllOf, null)
                .WithSources(null, Loc.Property(i.Sources, i.OperationItem.Revision, prop)));
        });
    });

    private static bool HasProp(Schema? s, string name, out Schema? value)
    {
        value = null;
        if (s is null || !s.Properties.TryGetValue(name, out var r) || r is null) return false;
        value = r.Value;
        return true;
    }

    public static List<Change> ResponsePropertyBecameOptionalCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
    {
        if (i.SchemaDiff.RequiredDiff is { } rd)
            foreach (var name in rd.Deleted)
            {
                if (!HasProp(i.SchemaDiff.Revision, name, out var rp)) continue;
                var id = rp!.WriteOnly ? Ids.ResponseWriteOnlyPropertyBecameOptional : Ids.ResponsePropertyBecameOptional;
                var (b, rv) = Loc.SchemaDeletedItem(i.Sources, i.OperationItem, i.SchemaDiff, "required", name);
                r.Add(i.NewChange(id, [name, i.ResponseStatus], "").WithSources(b, rv));
            }
        i.WalkProperties(p =>
        {
            if (p.PropertyDiff.RequiredDiff is not { } prd) return;
            foreach (var name in prd.Deleted)
            {
                if (!HasProp(p.PropertyDiff.Base, name, out var bp) || !HasProp(p.PropertyDiff.Revision, name, out _)) continue;
                var id = bp!.WriteOnly ? Ids.ResponseWriteOnlyPropertyBecameOptional : Ids.ResponsePropertyBecameOptional;
                var (b, rv) = Loc.SchemaDeletedItem(i.Sources, i.OperationItem, p.PropertyDiff, "required", name);
                r.Add(p.NewChange(id, [SchemaWalker.PropertyFullName(p.PropertyPath, SchemaWalker.PropertyFullName(p.PropertyName, name)), i.ResponseStatus], "").WithSources(b, rv));
            }
        });
    });

    public static List<Change> ResponsePropertyBecameRequiredCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseBodies(d, s, c, (i, r) =>
    {
        if (i.SchemaDiff.RequiredDiff is { } rd)
            foreach (var name in rd.Added)
            {
                if (!HasProp(i.SchemaDiff.Base, name, out _) || !HasProp(i.SchemaDiff.Revision, name, out var rp)) continue;
                var id = rp!.WriteOnly ? Ids.ResponseWriteOnlyPropertyBecameRequired : Ids.ResponsePropertyBecameRequired;
                var (b, rv) = Loc.SchemaAddedItem(i.Sources, i.OperationItem, i.SchemaDiff, "required", name);
                r.Add(i.NewChange(id, [SchemaWalker.PropertyFullName("", name), i.ResponseStatus], "").WithSources(b, rv));
            }
        i.WalkProperties(p =>
        {
            if (p.PropertyDiff.RequiredDiff is not { } prd) return;
            foreach (var name in prd.Added)
            {
                if (!HasProp(p.PropertyDiff.Base, name, out var bp) || !HasProp(p.PropertyDiff.Revision, name, out _)) continue;
                var id = bp!.WriteOnly ? Ids.ResponseWriteOnlyPropertyBecameRequired : Ids.ResponsePropertyBecameRequired;
                var (b, rv) = Loc.SchemaAddedItem(i.Sources, i.OperationItem, p.PropertyDiff, "required", name);
                r.Add(p.NewChange(id, [SchemaWalker.PropertyFullName(p.PropertyPath, SchemaWalker.PropertyFullName(p.PropertyName, name)), i.ResponseStatus], "").WithSources(b, rv));
            }
        });
    });

    private static List<Change> ResponseWriteOnlyReadOnly(OpenApiDiff d, Sources s, CheckerConfig c, bool required) => ResponseBodies(d, s, c, (i, r) => i.WalkProperties(p =>
    {
        var parent = p.Parent!;
        if (!HasProp(parent.Revision, p.PropertyName, out _)) return;
        if (parent.Base!.Required.Contains(p.PropertyName) != required) return;
        if (p.PropertyDiff.WriteOnlyDiff is { } wo)
        {
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, "writeOnly");
            bool on = wo.To is true;
            var id = required
                ? on ? Ids.ResponseRequiredPropertyBecameWriteOnly : Ids.ResponseRequiredPropertyBecameNonWriteOnly
                : on ? Ids.ResponseOptionalPropertyBecameWriteOnly : Ids.ResponseOptionalPropertyBecameNonWriteOnly;
            var ch = p.NewChange(id, [p.FullName, i.ResponseStatus], "");
            if (required) ch = ch.WithoutGuard(Guard.WriteOnly);
            r.Add(ch.WithSources(b, rv));
        }
        if (p.PropertyDiff.ReadOnlyDiff is { } ro)
        {
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, "readOnly");
            bool on = ro.To is true;
            var id = required
                ? on ? Ids.ResponseRequiredPropertyBecameReadOnly : Ids.ResponseRequiredPropertyBecameNonReadOnly
                : on ? Ids.ResponseOptionalPropertyBecameReadOnly : Ids.ResponseOptionalPropertyBecameNonReadOnly;
            r.Add(p.NewChange(id, [p.FullName, i.ResponseStatus], "").WithSources(b, rv));
        }
    }));
    public static List<Change> ResponseOptionalPropertyWriteOnlyReadOnlyCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseWriteOnlyReadOnly(d, s, c, false);
    public static List<Change> ResponseRequiredPropertyWriteOnlyReadOnlyCheck(OpenApiDiff d, Sources s, CheckerConfig c) => ResponseWriteOnlyReadOnly(d, s, c, true);

    // ---- property deprecation ----

    private static readonly Deprecation.Ids RequestPropertyDeprecationIds = new(Ids.RequestPropertyReactivated, Ids.RequestPropertyDeprecated,
        Ids.RequestPropertyDeprecatedWithSunset, Ids.RequestPropertyDeprecatedSunsetMissing, Ids.RequestPropertyDeprecatedInvalid, Ids.RequestPropertySunsetDateTooSmall);
    private static readonly Deprecation.Ids ResponsePropertyDeprecationIds = new(Ids.ResponsePropertyReactivated, Ids.ResponsePropertyDeprecated,
        Ids.ResponsePropertyDeprecatedWithSunset, Ids.ResponsePropertyDeprecatedSunsetMissing, Ids.ResponsePropertyDeprecatedInvalid, Ids.ResponsePropertySunsetDateTooSmall);

    private static List<Change> PropertyDeprecation(bool resp, OpenApiDiff d, Sources s, CheckerConfig c) => Bodies(resp, d, s, c, (i, r) =>
    {
        var ids = resp ? ResponsePropertyDeprecationIds : RequestPropertyDeprecationIds;
        var op = i.OperationItem.Revision!;
        var (days, ok) = Deprecation.Days(c, op.Extensions);
        if (!ok) return;
        i.WalkProperties(p =>
        {
            if (p.PropertyDiff.DeprecatedDiff is not { } dd) return;
            var stage = Deprecation.Announce(dd, p.PropertyDiff.Revision?.Extensions, days, ids);
            var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, stage.AtSunset ? DiffConfig.SunsetExtension : "deprecated");
            List<object?> args = [p.FullName, .. stage.Args];
            var ch = p.NewChange(stage.Id, args, "").WithSources(b, rv);
            if (stage.Id == ids.Deprecated || stage.Id == ids.WithSunset)
                ch = ch.WithDetails(Walkers.CombineDetails(FormatDeprecationDetails(op.Extensions), i.MediaTypeDetails));
            r.Add(ch);
        });
    });
    public static List<Change> RequestPropertyDeprecationCheck(OpenApiDiff d, Sources s, CheckerConfig c) => PropertyDeprecation(false, d, s, c);
    public static List<Change> ResponsePropertyDeprecationCheck(OpenApiDiff d, Sources s, CheckerConfig c) => PropertyDeprecation(true, d, s, c);

    // ---- discriminators (the response variants append the status code) ----

    private static void DiscriminatorChanges(DiscriminatorDiff? dd, string propertyName, string prefix, string? status, Action<string, List<object?>> add)
    {
        if (dd is null) return;
        bool body = propertyName == "";
        List<object?> A(params object?[] a) { var l = new List<object?>(a); if (status is not null) l.Add(status); return l; }
        if (dd.Added) { add(prefix + "-added", body ? A() : A(propertyName)); return; }
        if (dd.Deleted) { add(prefix + "-removed", body ? A() : A(propertyName)); return; }
        if (dd.PropertyNameDiff is { } pn)
            add(prefix + "-property-name-changed", body ? A(pn.From, pn.To) : A(propertyName, pn.From, pn.To));
        if (dd.MappingDiff is { } md)
        {
            if (md.Added.Count > 0) add(prefix + "-mapping-added", body ? A(md.Added) : A(md.Added, propertyName));
            if (md.Deleted.Count > 0) add(prefix + "-mapping-deleted", body ? A(md.Deleted) : A(md.Deleted, propertyName));
            foreach (var (k, v) in md.Modified) add(prefix + "-mapping-changed", body ? A(k, v.From, v.To) : A(k, v.From, v.To, propertyName));
        }
    }

    private static List<Change> Discriminator(bool resp, OpenApiDiff d, Sources s, CheckerConfig c) => Bodies(resp, d, s, c, (i, r) =>
    {
        string? status = resp ? i.ResponseStatus : null;
        var (b, rv) = Loc.SchemaField(i.Sources, i.OperationItem, i.SchemaDiff, "discriminator");
        DiscriminatorChanges(i.SchemaDiff.DiscriminatorDiff, "", Side(resp, "body-discriminator"), status, (id, a) => r.Add(i.NewChange(id, a, "").WithSources(b, rv)));
        i.WalkProperties(p =>
        {
            var (pb, prv) = Loc.SchemaField(i.Sources, i.OperationItem, p.PropertyDiff, "discriminator");
            var name = p.FullName;
            DiscriminatorChanges(p.PropertyDiff.DiscriminatorDiff, name, Side(resp, name == "" ? "body-discriminator" : "property-discriminator"), status,
                (id, a) => r.Add(p.NewChange(id, a, "").WithSources(pb, prv)));
        });
    });
    public static List<Change> RequestDiscriminatorUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Discriminator(false, d, s, c);
    public static List<Change> ResponseDiscriminatorUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c) => Discriminator(true, d, s, c);

    // ---- webhooks ----

    public static List<Change> WebhookUpdatedCheck(OpenApiDiff d, Sources s, CheckerConfig c)
    {
        var result = new List<Change>();
        if (d.WebhooksDiff is not { } wd) return result;
        foreach (var name in wd.Added)
        {
            SourceLoc? rv = wd.Revision is not null && wd.Revision.TryGetValue(name, out var pi) && pi is not null ? Loc.FromOriginOnly(pi.Origin) : null;
            result.Add(ComponentChange(Ids.WebhookAdded, c.LevelOf(Ids.WebhookAdded), [name], "webhooks").WithSources(null, rv));
        }
        foreach (var name in wd.Deleted)
        {
            SourceLoc? b = wd.Base is not null && wd.Base.TryGetValue(name, out var pi) && pi is not null ? Loc.FromOriginOnly(pi.Origin) : null;
            result.Add(ComponentChange(Ids.WebhookRemoved, c.LevelOf(Ids.WebhookRemoved), [name], "webhooks").WithSources(b, null));
        }
        return result;
    }
}
