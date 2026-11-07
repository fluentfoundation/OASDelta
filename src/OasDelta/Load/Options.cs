using OasDelta.Model;

namespace OasDelta.Load;

/// <summary>Post-load spec transformations (oasdiff load options / flatten packages).</summary>
public static class SpecOptions
{
    /// <summary>flatten/headers.Lowercase: header param names, response header keys and apiKey header scheme names.</summary>
    public static void LowercaseHeaders(OpenApiDoc spec)
    {
        foreach (var path in spec.Paths.Map.Values)
        {
            foreach (var p in path.Parameters) LowerParam(p.Value);
            foreach (var op in path.Operations().Values)
            {
                foreach (var p in op.Parameters) LowerParam(p.Value);
                if (op.Responses is null) continue;
                foreach (var r in op.Responses.Map.Values)
                {
                    if (r.Value is null) continue;
                    var lowered = new Dictionary<string, Ref<Header>>(StringComparer.Ordinal);
                    foreach (var (k, v) in r.Value.Headers) lowered[k.ToLowerInvariant()] = v;
                    r.Value.Headers = lowered;
                }
            }
        }
        if (spec.Components is null) return;
        foreach (var s in spec.Components.SecuritySchemes.Values)
            if (s.Value is { Type: "apiKey", In: "header" } scheme) scheme.Name = scheme.Name.ToLowerInvariant();
    }

    private static void LowerParam(Parameter? p)
    {
        if (p is not null && p.In == Parameter.InHeader) p.Name = p.Name.ToLowerInvariant();
    }

    /// <summary>flatten/commonparams.Move: copy path-level params into each operation.</summary>
    public static void FlattenParams(OpenApiDoc spec)
    {
        foreach (var path in spec.Paths.Map.Values)
        {
            foreach (var op in path.Operations().Values)
                foreach (var pp in path.Parameters)
                {
                    var v = pp.Value!;
                    bool exists = op.Parameters.Any(x => x.Value is not null && x.Value.In == v.In && x.Value.Name == v.Name);
                    if (!exists) op.Parameters = [.. op.Parameters, new Ref<Parameter>(v)];
                }
            path.Parameters = [];
        }
    }
}
