using OasDelta.Diff;
using OasDelta.Model;

namespace OasDelta.Checker;

public static partial class Checks
{
    private const string ComponentSecuritySchemes = "securitySchemes", ComponentSchemas = "schemas";

    // ------------------------------------------------------------------ anonymous_access.go
    private readonly record struct AnonymousAccess(bool Known, bool Base, bool Revision)
    {
        public bool Changed => Known && Base != Revision;
        public bool RevisionAcceptsAll => Known && Revision;
    }

    private static bool AllowsAnonymous(List<SecurityRequirement>? reqs)
    {
        if (reqs is null || reqs.Count == 0) return true;
        return reqs.Any(r => r.Count == 0);
    }

    private static List<string> SecurityAlternatives(List<SecurityRequirement> reqs) => reqs.Select((r, i) => new SecurityAlternative(i, r).ToString()).ToList();

    private static Change SecurityChange(string id, Level level, List<object?> args, string comment = "") =>
        new() { Kind = ChangeSection.Security, Id = id, Level = level, Args = args, Comment = comment };

    private static Change ComponentChange(string id, Level level, List<object?> args, string component, string comment = "") =>
        new() { Kind = ChangeSection.Component, Id = id, Level = level, Args = args, Component = component, Comment = comment };

    private static List<Change> GlobalSecurity(OpenApiDiff d, CheckerConfig config)
    {
        var result = new List<Change>();
        if (d.SecurityDiff is null) return result;
        var ctx = d.SecurityContext;
        if (ctx is not null && ctx.Operations.Any(p => p.Base.Security is null && p.Revision.Security is null)) return result;
        var b = Loc.FromFieldOnly(d.SecurityDiff.BaseOrigin, "security");
        var r = Loc.FromFieldOnly(d.SecurityDiff.RevisionOrigin, "security");
        Change New(string id, List<object?> args) => ctx is null ? SecurityChange(id, config.LevelOf(id), args) : SecurityChange(id, Level.INFO, args, Ids.APIGlobalSecurityNotInheritedComment);
        foreach (var a in d.SecurityDiff.Added) if (a.Schemes.Count > 0) result.Add(New(Ids.APIGlobalSecurityAddedCheck, [a.ToString()]).WithSources(null, r));
        foreach (var a in d.SecurityDiff.Deleted) if (a.Schemes.Count > 0) result.Add(New(Ids.APIGlobalSecurityRemovedCheck, [a.ToString()]).WithSources(b, null));
        foreach (var m in d.SecurityDiff.Modified)
            foreach (var (scheme, scopes) in m.Scopes)
            {
                foreach (var s in scopes.Added) result.Add(New(Ids.APIGlobalSecurityScopeAdded, [s, scheme]).WithSources(null, r));
                foreach (var s in scopes.Deleted) result.Add(New(Ids.APIGlobalSecurityScopeRemoved, [s, scheme]).WithSources(b, null));
            }
        return result;
    }

    public static List<Change> APISecurityUpdatedCheck(OpenApiDiff d, Sources src, CheckerConfig config)
    {
        var result = GlobalSecurity(d, config);
        var (inherited, handled) = InheritedSecurity(d, src, config);
        result.AddRange(inherited);
        foreach (var (path, _, m, md) in Walkers.ModifiedOperations(d))
        {
            if (md.SecurityDiff is null || handled.Contains(new Endpoint(m, path))) continue;
            var oi = OpInfo.FromDiff(config, md, src, m, path);
            var access = new AnonymousAccess(md.Base!.Security is not null && md.Revision!.Security is not null, AllowsAnonymous(md.Base.Security), AllowsAnonymous(md.Revision!.Security));
            result.AddRange(OperationSecurityChanges(oi, Loc.Security(src, md.Base), Loc.Security(src, md.Revision), md.Revision.Security, md.SecurityDiff, access, ""));
        }
        return result;
    }

    private static (List<Change>, HashSet<Endpoint>) InheritedSecurity(OpenApiDiff d, Sources src, CheckerConfig config)
    {
        var result = new List<Change>();
        var handled = new HashSet<Endpoint>();
        var ctx = d.SecurityContext;
        if (ctx is null) return (result, handled);
        foreach (var pair in ctx.Operations)
        {
            if (pair.Base.Security is not null && pair.Revision.Security is not null) continue;
            var bs = pair.Base.Security ?? ctx.Base;
            var rs = pair.Revision.Security ?? ctx.Revision;
            var access = new AnonymousAccess(true, AllowsAnonymous(bs), AllowsAnonymous(rs));
            var comment = pair.Base.Security is null && pair.Revision.Security is null ? Ids.APISecurityInheritedComment : "";
            handled.Add(new Endpoint(pair.Method, pair.Path));
            var oi = OpInfo.Of(config, pair.Revision, src, pair.Method, pair.Path);
            var b = InheritedSecuritySource(src, pair.Base, d.SecurityDiff, true);
            var r = InheritedSecuritySource(src, pair.Revision, d.SecurityDiff, false);
            result.AddRange(OperationSecurityChanges(oi, b, r, rs, SecurityRequirementsDiff.Get(bs, rs), access, comment));
        }
        return (result, handled);
    }

    private static SourceLoc? InheritedSecuritySource(Sources src, Operation op, SecurityRequirementsDiff? root, bool isBase)
    {
        if (op.Security is not null || root is null) return Loc.Security(src, op);
        return Loc.FromFieldOnly(isBase ? root.BaseOrigin : root.RevisionOrigin, "security");
    }

    private static List<Change> OperationSecurityChanges(OpInfo oi, SourceLoc? b, SourceLoc? r, List<SecurityRequirement>? revisionSecurity, SecurityRequirementsDiff? sd, AnonymousAccess access, string comment)
    {
        var result = new List<Change>();
        if (access.Changed)
        {
            if (access.Base) { result.Add(oi.NewApiChange(Ids.APISecurityAnonymousAccessRemoved, [SecurityAlternatives(revisionSecurity!)], comment).WithSources(b, r)); return result; }
            result.Add(oi.NewApiChange(Ids.APISecurityAnonymousAccessAdded, [], comment).WithSources(b, r));
            return result;
        }
        if (sd is null) return result;
        foreach (var a in sd.Added) if (a.Schemes.Count > 0) result.Add(oi.NewApiChange(Ids.APISecurityAddedCheck, [a.ToString()], comment).WithSources(null, r));
        if (!access.RevisionAcceptsAll)
            foreach (var a in sd.Deleted) if (a.Schemes.Count > 0) result.Add(oi.NewApiChange(Ids.APISecurityRemovedCheck, [a.ToString()], comment).WithSources(b, null));
        foreach (var m in sd.Modified)
            foreach (var (scheme, scopes) in m.Scopes)
            {
                if (!access.RevisionAcceptsAll)
                    foreach (var s in scopes.Added) result.Add(oi.NewApiChange(Ids.APISecurityScopeAdded, [s, scheme], comment).WithSources(null, r));
                foreach (var s in scopes.Deleted) result.Add(oi.NewApiChange(Ids.APISecurityScopeRemoved, [s, scheme], comment).WithSources(b, null));
            }
        return result;
    }

    // ------------------------------------------------------------------ components security

    private static List<Change> OAuthFlowUpdates(OAuthFlowDiff fd, string name, string flow, SourceLoc? b, SourceLoc? r)
    {
        var result = new List<Change>();
        void Url(string id, ValueDiff? ud)
        {
            if (ud is null || Walkers.SameURL(ud.From, ud.To)) return;
            result.Add(ComponentChange(id, Level.WARN, [name, flow, ud.From, ud.To], ComponentSecuritySchemes, Ids.SecuritySchemeURLChangedComment).WithSources(b, r));
        }
        Url(Ids.APIComponentsSecurityComponentOauthUrlUpdated, fd.AuthorizationURLDiff);
        Url(Ids.APIComponentsSecurityOauthTokenUrlUpdated, fd.TokenURLDiff);
        Url(Ids.APIComponentsSecurityOauthRefreshUrlUpdated, fd.RefreshURLDiff);
        if (fd.ScopesDiff is null) return result;
        foreach (var s in fd.ScopesDiff.Added) result.Add(ComponentChange(Ids.APIComponentSecurityOauthScopeAdded, Level.INFO, [name, flow, s], ComponentSecuritySchemes).WithSources(null, r));
        foreach (var s in fd.ScopesDiff.Deleted) result.Add(ComponentChange(Ids.APIComponentSecurityOauthScopeRemoved, Level.INFO, [name, flow, s], ComponentSecuritySchemes).WithSources(b, null));
        foreach (var (s, v) in fd.ScopesDiff.Modified) result.Add(ComponentChange(Ids.APIComponentSecurityOauthScopeUpdated, Level.INFO, [name, flow, s, v.From, v.To], ComponentSecuritySchemes).WithSources(b, r));
        return result;
    }

    public static List<Change> APIComponentsSecurityUpdatedCheck(OpenApiDiff d, Sources src, CheckerConfig config)
    {
        var result = new List<Change>();
        var ss = d.ComponentsDiff?.SecuritySchemesDiff;
        if (ss is null) return result;
        SourceLoc? Of(Dictionary<string, Ref<SecurityScheme>>? m, string n) => m is not null && m.TryGetValue(n, out var r) && r?.Value is not null ? Loc.FromOriginOnly(r.Value.Origin) : null;
        foreach (var n in ss.Added) result.Add(ComponentChange(Ids.APIComponentsSecurityAdded, Level.INFO, [n], ComponentSecuritySchemes).WithSources(null, Of(ss.Revision, n)));
        foreach (var n in ss.Deleted) result.Add(ComponentChange(Ids.APIComponentsSecurityRemoved, Level.INFO, [n], ComponentSecuritySchemes).WithSources(Of(ss.Base, n), null));
        foreach (var (n, sd) in ss.Modified)
        {
            var b = Of(ss.Base, n); var r = Of(ss.Revision, n);
            if (sd.OAuthFlowsDiff is { } fd)
                foreach (var (flow, fdd) in new[] { ("implicit", fd.ImplicitDiff), ("password", fd.PasswordDiff), ("clientCredentials", fd.ClientCredentialsDiff), ("authorizationCode", fd.AuthorizationCodeDiff) })
                    if (fdd is not null) result.AddRange(OAuthFlowUpdates(fdd, n, flow, b, r));
            if (sd.TypeDiff is not null)
            {
                result.Add(ComponentChange(Ids.APIComponentsSecurityTypeUpdated, Level.ERR, [n, sd.TypeDiff.From, sd.TypeDiff.To], ComponentSecuritySchemes).WithSources(b, r));
                continue;
            }
            void Report(string id, Level level, ValueDiff? vd) { if (vd is not null) result.Add(ComponentChange(id, level, [n, vd.From, vd.To], ComponentSecuritySchemes).WithSources(b, r)); }
            Report(Ids.APIComponentsSecurityApiKeyNameUpdated, Level.ERR, sd.NameDiff);
            Report(Ids.APIComponentsSecurityApiKeyInUpdated, Level.ERR, sd.InDiff);
            if (sd.SchemeDiff is { } sch && !string.Equals(Fmt.InterfaceToString(sch.From), Fmt.InterfaceToString(sch.To), StringComparison.OrdinalIgnoreCase))
                Report(Ids.APIComponentsSecurityHttpSchemeUpdated, Level.ERR, sch);
            Report(Ids.APIComponentsSecurityBearerFormatUpdated, Level.INFO, sd.BearerFormatDiff);
            if (sd.OpenIDConnectURLDiff is { } ud && !Walkers.SameURL(ud.From, ud.To))
                result.Add(ComponentChange(Ids.APIComponentsSecurityOpenIdConnectUrlUpdated, Level.WARN, [n, ud.From, ud.To], ComponentSecuritySchemes, Ids.SecuritySchemeURLChangedComment).WithSources(b, r));
        }
        return result;
    }

    public static List<Change> APIComponentsSchemaRemovedCheck(OpenApiDiff d, Sources src, CheckerConfig config)
    {
        var result = new List<Change>();
        var sd = d.ComponentsDiff?.SchemasDiff;
        if (sd is null) return result;
        foreach (var n in sd.Deleted)
        {
            SourceLoc? b = sd.Base is not null && sd.Base.TryGetValue(n, out var r) && r?.Value is not null ? Loc.FromOriginOnly(r.Value.Origin) : null;
            result.Add(ComponentChange(Ids.APISchemasRemoved, config.LevelOf(Ids.APISchemasRemoved), [n], ComponentSchemas).WithSources(b, null));
        }
        return result;
    }
}
