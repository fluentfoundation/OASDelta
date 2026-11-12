using CsCheck;

namespace OasDelta.Tests.Properties;

internal enum Kind { Root, PathItem, Operation, Parameter, RequestBody, MediaType, Response, Header, Schema, SecurityScheme }

/// <summary>A node of a document an edit can apply to, with where it sits (for removal, renaming and reporting).</summary>
internal sealed record Loc(Kind Kind, Map Map, object? Parent, string? Key, int Index, string Pointer)
{
    public void Remove()
    {
        if (Parent is Map m && Key is not null) m.Remove(Key);
        else if (Parent is List<object?> l && Index >= 0 && Index < l.Count) l.RemoveAt(Index);
    }
}

/// <summary>One structural change: on the <see cref="Target"/>-th node of kind <see cref="Kind"/> (modulo how many
/// there are), apply the <see cref="Op"/>-th operation of that kind, parameterised by <see cref="Arg"/> and
/// <see cref="Text"/>. Plain data, so CsCheck can generate and shrink it; applying it is deterministic.</summary>
internal readonly record struct Edit(Kind Kind, int Target, int Op, int Arg, string Text);

/// <summary>
/// The edit catalogue: the changes a revision of an API makes, mirroring what oasdiff's checks look for (types,
/// formats, enums, required, bounds, patterns, nullable/readOnly/writeOnly, deprecation and stability, composition,
/// discriminators, parameters, headers, media types, response statuses, request bodies, security, paths).
/// </summary>
internal static class Edits
{
    private sealed record Ctx(Loc Loc, Map Doc, int A, string T, bool V31)
    {
        public Map M => Loc.Map;
        public string NameOr(string fallback) => T.Length == 0 ? fallback : T;
    }

    public static readonly Gen<Edit> One = Gen.Select(
        Gen.Frequency<Kind>(
            (8, Kind.Schema), (4, Kind.Operation), (3, Kind.Parameter), (2, Kind.Response), (2, Kind.MediaType),
            (2, Kind.PathItem), (1, Kind.RequestBody), (1, Kind.Header), (1, Kind.SecurityScheme), (2, Kind.Root)),
        Gen.Int[0, 999], Gen.Int[0, 99], Gen.Int[0, 999], SpecGen.Text,
        (k, t, o, a, s) => new Edit(k, t, o, a, s));

    public static readonly Gen<Edit[]> Some = One.Array[1, 4];

    /// <summary>Applies the edits in order to <paramref name="doc"/> (in place); returns what each one did.</summary>
    public static List<string> Apply(Map doc, IEnumerable<Edit> edits)
    {
        var done = new List<string>();
        foreach (var e in edits)
        {
            var locs = Walk(doc).Where(l => l.Kind == e.Kind).ToList();
            if (locs.Count == 0) { done.Add($"{e.Kind}: none to edit"); continue; }
            var loc = locs[e.Target % locs.Count];
            var ops = Ops[e.Kind];
            var (name, act) = ops[e.Op % ops.Length];
            bool v31 = doc.GetValueOrDefault("openapi") is string v && v.StartsWith("3.1", StringComparison.Ordinal);
            act(new Ctx(loc, doc, e.Arg, e.Text, v31));
            SpecGen.Dedupe(doc);
            done.Add($"{e.Kind} {(loc.Pointer.Length == 0 ? "/" : loc.Pointer)}: {name} (arg {e.Arg}, text {Show(e.Text)})");
        }
        return done;
    }

    public static string Show(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    // ------------------------------------------------------------------ walking

    public static List<Loc> Walk(Map doc)
    {
        var locs = new List<Loc> { new(Kind.Root, doc, null, null, -1, "") };

        void Schema(Map s, object parent, string? key, int index, string ptr)
        {
            if (s.ContainsKey("$ref")) return; // edited where it is defined
            locs.Add(new Loc(Kind.Schema, s, parent, key, index, ptr));
            if (s.GetValueOrDefault("properties") is Map props)
                foreach (var (k, v) in props) if (v is Map p) Schema(p, props, k, -1, ptr + "/properties/" + Esc(k));
            foreach (var k in new[] { "items", "additionalProperties", "not" })
                if (s.GetValueOrDefault(k) is Map sub) Schema(sub, s, k, -1, ptr + "/" + k);
            foreach (var k in new[] { "allOf", "anyOf", "oneOf" })
                if (s.GetValueOrDefault(k) is List<object?> l)
                    for (int i = 0; i < l.Count; i++) if (l[i] is Map b) Schema(b, l, null, i, $"{ptr}/{k}/{i}");
        }

        void Content(Map owner, string ptr)
        {
            if (owner.GetValueOrDefault("content") is not Map content) return;
            foreach (var (mt, v) in content)
                if (v is Map m)
                {
                    var p = ptr + "/content/" + Esc(mt);
                    locs.Add(new Loc(Kind.MediaType, m, content, mt, -1, p));
                    if (m.GetValueOrDefault("schema") is Map s) Schema(s, m, "schema", -1, p + "/schema");
                }
        }

        void Parameters(Map owner, string ptr)
        {
            if (owner.GetValueOrDefault("parameters") is not List<object?> ps) return;
            for (int i = 0; i < ps.Count; i++)
                if (ps[i] is Map p)
                {
                    var pp = $"{ptr}/parameters/{i}";
                    locs.Add(new Loc(Kind.Parameter, p, ps, null, i, pp));
                    if (p.GetValueOrDefault("schema") is Map s) Schema(s, p, "schema", -1, pp + "/schema");
                    Content(p, pp);
                }
        }

        if (doc.GetValueOrDefault("paths") is Map paths)
            foreach (var (path, pv) in paths)
            {
                if (pv is not Map item) continue;
                var ptr = "/paths/" + Esc(path);
                locs.Add(new Loc(Kind.PathItem, item, paths, path, -1, ptr));
                Parameters(item, ptr);
                foreach (var (method, ov) in item)
                {
                    if (ov is not Map op || !SpecGen.Methods.Contains(method)) continue;
                    var op_ = ptr + "/" + method;
                    locs.Add(new Loc(Kind.Operation, op, item, method, -1, op_));
                    Parameters(op, op_);
                    if (op.GetValueOrDefault("requestBody") is Map rb)
                    {
                        locs.Add(new Loc(Kind.RequestBody, rb, op, "requestBody", -1, op_ + "/requestBody"));
                        Content(rb, op_ + "/requestBody");
                    }
                    if (op.GetValueOrDefault("responses") is Map responses)
                        foreach (var (status, rv) in responses)
                        {
                            if (rv is not Map r) continue;
                            var rp = op_ + "/responses/" + Esc(status);
                            locs.Add(new Loc(Kind.Response, r, responses, status, -1, rp));
                            Content(r, rp);
                            if (r.GetValueOrDefault("headers") is Map headers)
                                foreach (var (h, hv) in headers)
                                    if (hv is Map hm)
                                    {
                                        locs.Add(new Loc(Kind.Header, hm, headers, h, -1, rp + "/headers/" + Esc(h)));
                                        if (hm.GetValueOrDefault("schema") is Map s) Schema(s, hm, "schema", -1, rp + "/headers/" + Esc(h) + "/schema");
                                    }
                        }
                }
            }

        if (doc.GetValueOrDefault("components") is Map components)
        {
            if (components.GetValueOrDefault("schemas") is Map schemas)
                foreach (var (n, v) in schemas) if (v is Map s) Schema(s, schemas, n, -1, "/components/schemas/" + Esc(n));
            if (components.GetValueOrDefault("securitySchemes") is Map schemes)
                foreach (var (n, v) in schemes)
                    if (v is Map s) locs.Add(new Loc(Kind.SecurityScheme, s, schemes, n, -1, "/components/securitySchemes/" + Esc(n)));
        }
        return locs;
    }

    private static string Esc(string s) => s.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    // ------------------------------------------------------------------ helpers

    private static List<object?> ListAt(Map m, string key)
    {
        if (m.GetValueOrDefault(key) is List<object?> l) return l;
        var n = new List<object?>();
        m[key] = n;
        return n;
    }

    private static Map MapAt(Map m, string key)
    {
        if (m.GetValueOrDefault(key) is Map x) return x;
        var n = new Map();
        m[key] = n;
        return n;
    }

    private static void Toggle(Map m, string key, object value)
    {
        if (!m.Remove(key)) m[key] = value;
    }

    private static void Flip(Map m, string key) => m[key] = m.GetValueOrDefault(key) is not true;

    private static void RemoveOne(Map m, int a)
    {
        if (m.Count == 0) return;
        m.Remove(m.Keys.ElementAt(a % m.Count));
    }

    private static Map Typed(string type) => type == "array"
        ? new Map { ["type"] = "array", ["items"] = new Map { ["type"] = "string" } }
        : new Map { ["type"] = type };

    private static string[] SchemeNames(Map doc) =>
        doc.GetValueOrDefault("components") is Map c && c.GetValueOrDefault("securitySchemes") is Map s ? [.. s.Keys] : [];

    private static readonly string[] XOf = ["oneOf", "anyOf", "allOf"];
    private static readonly string[] In = ["query", "header", "cookie", "path"];
    private static readonly string[] Styles = ["form", "simple", "spaceDelimited", "pipeDelimited", "deepObject", "matrix", "label"];
    private static readonly string[] Patterns = ["^[a-z]+$", "\\d{3}", "^.*$", "[A-Z]", "("];
    private static readonly string[] NumericBounds = ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf"];
    private static readonly string[] SizeBounds = ["minLength", "maxLength", "minItems", "maxItems", "minProperties", "maxProperties"];

    private static object EnumValue(Ctx c, int salt) => (c.M.GetValueOrDefault("type") as string) switch
    {
        "integer" => (long)(c.A + salt),
        "number" => (c.A + salt) / 2.0,
        "boolean" => (c.A + salt) % 2 == 0,
        _ => salt == 0 ? c.T : c.T + "-" + salt,
    };

    private static bool IsNullable(Map s, bool v31) =>
        v31 ? s.GetValueOrDefault("type") is List<object?> l && l.Contains("null") : s.GetValueOrDefault("nullable") is true;

    // ------------------------------------------------------------------ the catalogue

    private static readonly Dictionary<Kind, (string Name, Action<Ctx> Do)[]> Ops = new()
    {
        [Kind.Schema] =
        [
            ("set type", c =>
            {
                var t = SpecGen.Types[c.A % SpecGen.Types.Length];
                c.M["type"] = c.V31 && IsNullable(c.M, true) ? new List<object?> { t, "null" } : t;
                if (t == "array" && !c.M.ContainsKey("items")) c.M["items"] = new Map { ["type"] = "string" };
            }),
            ("remove type", c => c.M.Remove("type")),
            ("set format", c => c.M["format"] = SpecGen.Formats[c.A % SpecGen.Formats.Length]),
            ("remove format", c => c.M.Remove("format")),
            ("add enum value", c =>
            {
                var l = ListAt(c.M, "enum");
                if (l.Count == 0) l.Add(EnumValue(c, 1));
                l.Add(EnumValue(c, 0));
            }),
            ("remove enum value", c =>
            {
                if (c.M.GetValueOrDefault("enum") is not List<object?> l || l.Count == 0) return;
                l.RemoveAt(c.A % l.Count);
                if (l.Count == 0) c.M.Remove("enum");
            }),
            ("toggle a required property", c =>
            {
                if (c.M.GetValueOrDefault("properties") is not Map props || props.Count == 0) return;
                var name = props.Keys.ElementAt(c.A % props.Count);
                var req = ListAt(c.M, "required");
                if (!req.Remove(name)) req.Add(name);
                if (req.Count == 0) c.M.Remove("required");
            }),
            ("add property", c =>
            {
                var name = c.NameOr("added");
                MapAt(c.M, "properties")[name] = Typed(SpecGen.Types[c.A % 4]);
                if (c.A % 3 == 0 && !ListAt(c.M, "required").Contains(name)) ListAt(c.M, "required").Add(name);
            }),
            ("remove property", c =>
            {
                if (c.M.GetValueOrDefault("properties") is not Map props || props.Count == 0) return;
                var name = props.Keys.ElementAt(c.A % props.Count);
                props.Remove(name);
                if (props.Count == 0) c.M.Remove("properties");
                if (c.M.GetValueOrDefault("required") is List<object?> req) { req.Remove(name); if (req.Count == 0) c.M.Remove("required"); }
            }),
            ("set numeric bound", c =>
            {
                var k = NumericBounds[c.A % NumericBounds.Length];
                c.M[k] = k == "multipleOf" ? (long)(c.A % 9 + 1)
                    : !c.V31 && k.StartsWith("exclusive", StringComparison.Ordinal) ? c.A % 2 == 0
                    : (long)(c.A % 50 - 10);
            }),
            ("set size bound", c => c.M[SizeBounds[c.A % SizeBounds.Length]] = (long)(c.A % 20)),
            ("remove a bound", c =>
            {
                var ks = c.M.Keys.Where(k => NumericBounds.Contains(k) || SizeBounds.Contains(k)).ToList();
                if (ks.Count > 0) c.M.Remove(ks[c.A % ks.Count]);
            }),
            ("set pattern", c => c.M["pattern"] = Patterns[c.A % Patterns.Length]),
            ("remove pattern", c => c.M.Remove("pattern")),
            ("toggle nullable", c =>
            {
                if (!c.V31) { Toggle(c.M, "nullable", true); return; }
                var t = c.M.GetValueOrDefault("type");
                c.M["type"] = t switch
                {
                    List<object?> l when l.Contains("null") => l.Count == 2 ? l.First(x => !Equals(x, "null")) : l.Where(x => !Equals(x, "null")).ToList(),
                    List<object?> l => l.Append("null").ToList(),
                    string s => new List<object?> { s, "null" },
                    _ => "null",
                };
            }),
            ("toggle readOnly", c => Toggle(c.M, "readOnly", true)),
            ("toggle writeOnly", c => Toggle(c.M, "writeOnly", true)),
            ("toggle deprecated", c => Toggle(c.M, "deprecated", true)),
            ("set default", c => c.M["default"] = c.A % 2 == 0 ? c.T : (long)c.A),
            ("set example", c => c.M["example"] = c.T),
            ("wrap in a composition", c =>
            {
                var inner = SpecGen.Clone(c.M);
                c.M.Clear();
                c.M[XOf[c.A % 3]] = new List<object?> { inner, Typed(SpecGen.Types[c.A % 4]) };
            }),
            ("add a composition branch", c =>
            {
                var kw = XOf.FirstOrDefault(c.M.ContainsKey) ?? XOf[c.A % 3];
                ListAt(c.M, kw).Add(Typed(SpecGen.Types[c.A % 6]));
            }),
            ("remove a composition branch", c =>
            {
                var kw = XOf.FirstOrDefault(c.M.ContainsKey);
                if (kw is null || c.M[kw] is not List<object?> l || l.Count == 0) return;
                l.RemoveAt(c.A % l.Count);
                if (l.Count == 0) c.M.Remove(kw);
            }),
            ("toggle discriminator", c =>
            {
                if (c.M.Remove("discriminator")) return;
                var prop = c.M.GetValueOrDefault("properties") is Map p && p.Count > 0 ? p.Keys.First() : c.NameOr("kind");
                c.M["discriminator"] = new Map { ["propertyName"] = prop };
            }),
            ("cycle additionalProperties", c => c.M["additionalProperties"] = c.M.GetValueOrDefault("additionalProperties") switch
            {
                null => false,
                false => true,
                true => new Map { ["type"] = SpecGen.Types[c.A % 4] },
                _ => false,
            }),
            ("set description", c => c.M["description"] = c.T),
            ("set title", c => c.M["title"] = c.T),
            ("toggle uniqueItems", c => Toggle(c.M, "uniqueItems", true)),
            ("set an extension", c => c.M["x-" + SpecGen.Words[c.A % 8]] = c.T),
            ("toggle not", c => Toggle(c.M, "not", Typed(SpecGen.Types[c.A % 4]))),
            ("set const", c => c.M["const"] = c.T),
            ("change items", c =>
            {
                if (c.M.GetValueOrDefault("items") is Map items && !items.ContainsKey("$ref")) items["type"] = SpecGen.Types[c.A % 4];
                else c.M["items"] = Typed(SpecGen.Types[c.A % 4]);
            }),
        ],
        [Kind.Parameter] =
        [
            ("toggle required", c => Flip(c.M, "required")),
            ("change in", c => c.M["in"] = In[(Array.IndexOf(In, c.M.GetValueOrDefault("in") as string) + 1 + c.A % 3 + 4) % 4]),
            ("rename", c => c.M["name"] = c.NameOr("renamed")),
            ("set style", c => c.M["style"] = Styles[c.A % Styles.Length]),
            ("toggle explode", c => Flip(c.M, "explode")),
            ("toggle deprecated", c => Toggle(c.M, "deprecated", true)),
            ("toggle allowEmptyValue", c => Toggle(c.M, "allowEmptyValue", true)),
            ("toggle allowReserved", c => Toggle(c.M, "allowReserved", true)),
            ("set description", c => c.M["description"] = c.T),
            ("remove", c => c.Loc.Remove()),
            ("schema to content", c =>
            {
                if (c.M.Remove("schema", out var s)) c.M["content"] = new Map { [SpecGen.MediaTypes[c.A % 3]] = new Map { ["schema"] = s } };
            }),
            ("set example", c => c.M["example"] = c.T),
        ],
        [Kind.Operation] =
        [
            ("remove", c => c.Loc.Remove()),
            ("toggle deprecated", c => Toggle(c.M, "deprecated", true)),
            ("set operationId", c => c.M["operationId"] = c.NameOr("op" + c.A)),
            ("remove operationId", c => c.M.Remove("operationId")),
            ("add tag", c => ListAt(c.M, "tags").Add(SpecGen.Words[c.A % SpecGen.Words.Length])),
            ("remove tags", c => c.M.Remove("tags")),
            ("set stability level", c => c.M["x-stability-level"] = SpecGen.StabilityLevels[c.A % 4]),
            ("remove stability level", c => c.M.Remove("x-stability-level")),
            ("deprecate with sunset", c =>
            {
                c.M["deprecated"] = true;
                c.M["x-sunset"] = c.A % 2 == 0 ? "2025-01-01" : "2099-12-31";
            }),
            ("add parameter", c => ListAt(c.M, "parameters").Add(new Map
            {
                ["name"] = c.NameOr("p"), ["in"] = In[c.A % 3], ["required"] = c.A % 2 == 0, ["schema"] = Typed(SpecGen.Types[c.A % 4]),
            })),
            ("remove request body", c => c.M.Remove("requestBody")),
            ("add request body", c => c.M["requestBody"] = new Map
            {
                ["required"] = c.A % 2 == 0,
                ["content"] = new Map
                {
                    [SpecGen.MediaTypes[c.A % SpecGen.MediaTypes.Length]] = new Map
                    {
                        ["schema"] = new Map { ["type"] = "object", ["properties"] = new Map { [c.NameOr("field")] = Typed(SpecGen.Types[c.A % 4]) } },
                    },
                },
            }),
            ("add response", c =>
            {
                var r = MapAt(c.M, "responses");
                var s = SpecGen.Statuses[c.A % SpecGen.Statuses.Length];
                if (!r.ContainsKey(s)) r[s] = new Map { ["description"] = c.T };
            }),
            ("remove response", c => { if (c.M.GetValueOrDefault("responses") is Map r) RemoveOne(r, c.A); }),
            ("set security", c =>
            {
                var names = SchemeNames(c.Doc);
                c.M["security"] = names.Length == 0 || c.A % 4 == 0
                    ? new List<object?>()
                    : new List<object?> { new Map { [names[c.A % names.Length]] = new List<object?>() } };
            }),
            ("remove security", c => c.M.Remove("security")),
            ("set summary", c => c.M["summary"] = c.T),
            ("set description", c => c.M["description"] = c.T),
        ],
        [Kind.PathItem] =
        [
            ("remove", c => c.Loc.Remove()),
            ("rename", c =>
            {
                var paths = (Map)c.Loc.Parent!;
                var key = c.Loc.Key!;
                var param = SpecGen.PathParams(key).FirstOrDefault();
                string nk;
                if (param is not null && c.A % 2 == 0)
                {
                    nk = key.Replace("{" + param + "}", "{" + param + "2}", StringComparison.Ordinal);
                    var owners = SpecGen.Methods.Select(m => c.M.GetValueOrDefault(m)).OfType<Map>().Prepend(c.M);
                    foreach (var p in owners.SelectMany(o => (o.GetValueOrDefault("parameters") as List<object?> ?? []).OfType<Map>()))
                        if (p.GetValueOrDefault("in") is "path" && p.GetValueOrDefault("name") as string == param) p["name"] = param + "2";
                }
                else nk = key + (c.A % 3 == 0 ? "/" : "/v2");
                if (paths.ContainsKey(nk)) return;
                paths.Remove(key);
                paths[nk] = c.M;
            }),
            ("add parameter", c => ListAt(c.M, "parameters").Add(new Map
            {
                ["name"] = c.NameOr("q"), ["in"] = In[c.A % 3], ["schema"] = new Map { ["type"] = "string" },
            })),
            ("set summary", c => c.M["summary"] = c.T),
            ("add operation", c =>
            {
                var m = SpecGen.Methods.FirstOrDefault(x => !c.M.ContainsKey(x));
                if (m is not null) c.M[m] = new Map { ["responses"] = new Map { ["200"] = new Map { ["description"] = c.T } } };
            }),
        ],
        [Kind.RequestBody] =
        [
            ("toggle required", c => Flip(c.M, "required")),
            ("add media type", c => MapAt(c.M, "content").TryAdd(SpecGen.MediaTypes[c.A % SpecGen.MediaTypes.Length], new Map { ["schema"] = Typed(SpecGen.Types[c.A % 6]) })),
            ("remove media type", c => { if (c.M.GetValueOrDefault("content") is Map m && m.Count > 1) RemoveOne(m, c.A); }),
            ("set description", c => c.M["description"] = c.T),
        ],
        [Kind.MediaType] =
        [
            ("rename", c =>
            {
                var content = (Map)c.Loc.Parent!;
                var nk = SpecGen.MediaTypes[c.A % SpecGen.MediaTypes.Length];
                if (content.ContainsKey(nk)) return;
                content.Remove(c.Loc.Key!);
                content[nk] = c.M;
            }),
            ("remove", c => c.Loc.Remove()),
            ("set example", c => c.M["example"] = c.T),
            ("replace schema", c => c.M["schema"] = Typed(SpecGen.Types[c.A % 6])),
        ],
        [Kind.Response] =
        [
            ("set description", c => c.M["description"] = c.T),
            ("add header", c => MapAt(c.M, "headers")[c.NameOr("X-Added")] = new Map
            {
                ["schema"] = Typed(SpecGen.Types[c.A % 4]), ["required"] = c.A % 2 == 0,
            }),
            ("remove header", c => { if (c.M.GetValueOrDefault("headers") is Map h) { RemoveOne(h, c.A); if (h.Count == 0) c.M.Remove("headers"); } }),
            ("add content", c => MapAt(c.M, "content").TryAdd(SpecGen.MediaTypes[c.A % SpecGen.MediaTypes.Length], new Map { ["schema"] = Typed(SpecGen.Types[c.A % 6]) })),
            ("remove content", c => c.M.Remove("content")),
        ],
        [Kind.Header] =
        [
            ("toggle required", c => Flip(c.M, "required")),
            ("toggle deprecated", c => Toggle(c.M, "deprecated", true)),
            ("remove", c => c.Loc.Remove()),
            ("set description", c => c.M["description"] = c.T),
        ],
        [Kind.SecurityScheme] =
        [
            ("add scope", c =>
            {
                foreach (var flow in (c.M.GetValueOrDefault("flows") as Map)?.Values.OfType<Map>() ?? [])
                    MapAt(flow, "scopes")[c.NameOr("scope")] = c.T;
            }),
            ("remove scope", c =>
            {
                foreach (var flow in (c.M.GetValueOrDefault("flows") as Map)?.Values.OfType<Map>() ?? [])
                    if (flow.GetValueOrDefault("scopes") is Map s) RemoveOne(s, c.A);
            }),
            ("change in", c => { if (c.M.ContainsKey("in")) c.M["in"] = In[c.A % 3]; }),
            ("change name", c => { if (c.M.ContainsKey("name")) c.M["name"] = c.NameOr("key"); }),
            ("change http scheme", c => { if (c.M.ContainsKey("scheme")) c.M["scheme"] = c.A % 2 == 0 ? "bearer" : "basic"; }),
            ("set bearerFormat", c => c.M["bearerFormat"] = c.T),
            ("remove", c => c.Loc.Remove()),
        ],
        [Kind.Root] =
        [
            ("add path", c => MapAt(c.Doc, "paths").TryAdd("/added/" + Uri.EscapeDataString(c.NameOr("x")), new Map
            {
                ["get"] = new Map { ["responses"] = new Map { ["200"] = new Map { ["description"] = "ok" } } },
            })),
            ("set info version", c => MapAt(c.Doc, "info")["version"] = c.A % 2 == 0 ? "2.0.0" : c.T),
            ("set info title", c => MapAt(c.Doc, "info")["title"] = c.T),
            ("add server", c => ListAt(c.Doc, "servers").Add(new Map { ["url"] = "https://" + (c.A % 3) + ".example.com" })),
            ("remove servers", c => c.Doc.Remove("servers")),
            ("set security", c =>
            {
                var names = SchemeNames(c.Doc);
                c.Doc["security"] = names.Length == 0 ? new List<object?>() : new List<object?> { new Map { [names[c.A % names.Length]] = new List<object?>() } };
            }),
            ("remove security", c => c.Doc.Remove("security")),
            ("add tag", c => ListAt(c.Doc, "tags").Add(new Map { ["name"] = c.NameOr("tag"), ["description"] = c.T })),
            ("remove tags", c => c.Doc.Remove("tags")),
            ("switch OpenAPI version", c => c.Doc["openapi"] = c.V31 ? "3.0.3" : "3.1.0"),
            ("add component schema", c => MapAt(MapAt(c.Doc, "components"), "schemas").TryAdd("Added" + c.A % 10, Typed(SpecGen.Types[c.A % 6]))),
            ("set an extension", c => c.Doc["x-" + SpecGen.Words[c.A % 8]] = c.T),
            // kin-openapi rejects null in some places, keeps it as a nil it cannot compare in others, and ignores it in
            // the rest; and an empty mapping in a list has no origin. Both found parity bugs.
            ("set a node to null", c => ReplaceNode(c, null)),
            ("set a node to {}", c => ReplaceNode(c, new Map())),
            // kin-openapi follows a $ref through its typed document, and only to the kind the $ref stands for; when
            // that fails it drills the document holding the $ref instead. Any node pointing at any other one (another
            // kind, an ancestor, the root, through a $ref) exercises both.
            ("point a node at another with $ref", c =>
            {
                var all = Walk(c.Doc);
                ReplaceNode(c, new Map { ["$ref"] = "#" + all[(c.A / 7 + c.T.Length) % all.Count].Pointer });
            }),
        ],
    };

    /// <summary>Replaces one of the document's nodes (any kind but the root) where it sits.</summary>
    private static void ReplaceNode(Ctx c, object? value)
    {
        var locs = Walk(c.Doc).Where(l => l.Kind != Kind.Root).ToList();
        if (locs.Count == 0) return;
        var loc = locs[c.A % locs.Count];
        if (loc.Parent is Map m && loc.Key is not null) m[loc.Key] = value;
        else if (loc.Parent is List<object?> l && loc.Index >= 0 && loc.Index < l.Count) l[loc.Index] = value;
    }
}
