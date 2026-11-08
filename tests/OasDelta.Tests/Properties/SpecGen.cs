using CsCheck;

namespace OasDelta.Tests.Properties;

/// <summary>A JSON-like map in the value model the emitters take (Go map semantics: keys are emitted sorted).</summary>
internal sealed class Map : Dictionary<string, object?>
{
    public Map() : base(StringComparer.Ordinal) { }
}

/// <summary>
/// CsCheck generators for OpenAPI 3.0 and 3.1 documents. The documents are structurally valid (paths, operations,
/// parameters, request bodies, responses, headers, schemas, security schemes, references to component schemas, which
/// may be cyclic) and hostile by default: names, descriptions, enum values and defaults are drawn partly from strings
/// that are awkward for YAML, JSON, JSON pointers, Markdown and HTML (see <see cref="Hostile"/>).
/// Generated values shrink: a failing case is reduced to fewer paths, operations, properties and edits.
/// </summary>
internal static class SpecGen
{
    public static readonly string[] Words = ["pet", "name", "id", "owner", "status", "tag", "created", "price", "kind", "Ünïcode", "ключ", "名前"];

    /// <summary>Strings that YAML would read as something else unquoted, JSON-pointer and path separators, Markdown and
    /// HTML syntax, line breaks, tabs, bidi controls, astral characters.</summary>
    public static readonly string[] Hostile =
    [
        "", " ", "yes", "no", "null", "~", "0x1F", "1e3", "-", "a: b", "#hash", "x\ty", "line1\nline2", "trailing ",
        " leading", "\"quoted\"", "'single'", "😀", "a/b", "a~b", "{brace}", "[bracket]", "%20", "<b>&amp;", "\u202Eabc",
        "\u00A0nbsp", "C:\\path", "@at", "`tick`", "*star", "&anchor", "!tag", "|", ">", "---", "...", "a\r\nb", "\u0007",
        "| table |", "_under_", "**bold**",
    ];

    public static readonly string[] Types = ["string", "integer", "number", "boolean", "array", "object"];
    public static readonly string[] Formats = ["int32", "int64", "float", "double", "date", "date-time", "uuid", "email", "byte", "binary", "password", "uri"];
    public static readonly string[] MediaTypes = ["application/json", "application/xml", "text/plain", "application/json; charset=utf-8", "application/vnd.api+json", "multipart/form-data", "*/*"];
    public static readonly string[] Statuses = ["200", "201", "204", "400", "404", "500", "default", "2XX"];
    public static readonly string[] Methods = ["get", "post", "put", "delete", "patch"];
    public static readonly string[] StabilityLevels = ["draft", "alpha", "beta", "stable"];
    public static readonly string[] ComponentNames = ["Pet", "Owner", "Error", "Item"];
    public static readonly string[] SchemeNames = ["api_key", "bearer", "oauth"];

    private static readonly string[] PathTemplates =
        ["/pets", "/pets/{petId}", "/owners/{ownerId}/pets", "/store/order", "/ünïcode/ключ", "/v1/items", "/items/{id}.json", "/search"];

    // ------------------------------------------------------------------ scalars

    private static readonly Gen<string> Rune = Gen.Int[0x20, 0xD7FF].Select(i => ((char)i).ToString());

    /// <summary>Mostly words, sometimes hostile strings, sometimes random BMP text.</summary>
    public static readonly Gen<string> Text = Gen.Frequency(
        (6, (IGen<string>)Gen.OneOf(Words)),
        (3, Gen.OneOf(Hostile)),
        (1, Rune.Array[1, 8].Select(rs => string.Concat(rs))));

    /// <summary>Names (properties, parameters, headers): never empty.</summary>
    public static readonly Gen<string> Name = Text.Select(t => t.Length == 0 ? "empty" : t);

    public static readonly Gen<object?> Number = Gen.Frequency(
        (4, (IGen<object?>)Gen.Int[-5, 100].Select(i => (object?)(long)i)),
        (2, Gen.Int[-800, 800].Select(i => (object?)(i / 8.0))),
        (1, Gen.OneOf<object?>(9007199254740993L, long.MaxValue, long.MinValue, 1e300, 0.1, -0.0, 1e-7, 123456789.125)));

    /// <summary>"No value": a field generated this way is left out.</summary>
    public static readonly Gen<object?> Absent = Gen.Const(() => (object?)null);

    private static readonly string[] IntegerFormats = ["int32", "int64"], NumberFormats = ["float", "double"],
        StringFormats = ["date", "date-time", "uuid", "email", "byte", "binary", "password"], OtherFormats = ["custom"],
        Scopes = ["read", "write", "admin"], ServerUrls = ["https://api.example.com", "https://api.example.com/v2", "/relative"];

    private static Gen<object?> Opt<T>(Gen<T> g, int percent) =>
        Gen.Int[0, 99].SelectMany(i => i < percent ? g.Select(x => (object?)x) : Absent);

    /// <summary>Builds a map from fields whose generators yield null for "absent".</summary>
    private static Gen<Map> MapOf(params (string Key, Gen<object?> Value)[] fields)
    {
        Gen<List<KeyValuePair<string, object?>>> acc = Gen.Const(() => new List<KeyValuePair<string, object?>>());
        foreach (var (key, value) in fields)
            acc = acc.Select(value, (l, v) => v is null ? l : [.. l, new KeyValuePair<string, object?>(key, v)]);
        return acc.Select(l =>
        {
            var m = new Map();
            foreach (var (k, v) in l) m[k] = v;
            return m;
        });
    }

    /// <summary>A map with distinct keys from <paramref name="keys"/>, each with its own generated value.</summary>
    private static Gen<Map> Keyed(Gen<string[]> keys, Func<string, Gen<object?>> value) =>
        keys.SelectMany(ks =>
        {
            Gen<Map> acc = Gen.Const(() => new Map());
            foreach (var k in ks.Distinct(StringComparer.Ordinal))
                acc = acc.Select(value(k), (m, v) => { var c = Clone(m); c[k] = v; return c; });
            return acc;
        });

    // ------------------------------------------------------------------ schemas

    private static Gen<object?> EnumValues(string type) => type switch
    {
        "integer" => Gen.Int[-3, 20].Select(i => (object?)(long)i).List[1, 4].Select(l => (object?)l.Distinct().ToList()),
        "number" => Number.List[1, 3].Select(l => (object?)l),
        "boolean" => Gen.Const<object?>(new List<object?> { true, false }),
        _ => Text.List[1, 4].Select(l => (object?)l.Distinct(StringComparer.Ordinal).Select(s => (object?)s).ToList()),
    };

    private static Gen<Map> Primitive(bool v31) =>
        Gen.OneOf("string", "integer", "number", "boolean").SelectMany(type =>
            Gen.Bool.SelectMany(nullable => MapOf(
                ("type", Gen.Const<object?>(v31 && nullable ? new List<object?> { type, "null" } : type)),
                ("nullable", !v31 && nullable ? Gen.Const<object?>(true) : Absent),
                ("format", Opt(Gen.OneOf(type switch
                {
                    "integer" => IntegerFormats,
                    "number" => NumberFormats,
                    "string" => StringFormats,
                    _ => OtherFormats,
                }), 30)),
                ("enum", Opt(EnumValues(type), 20)),
                ("minimum", type is "integer" or "number" ? Opt(Gen.Int[-10, 10].Select(i => (long)i), 20) : Absent),
                ("maximum", type is "integer" or "number" ? Opt(Gen.Int[11, 1000].Select(i => (long)i), 20) : Absent),
                ("minLength", type == "string" ? Opt(Gen.Int[0, 5].Select(i => (long)i), 15) : Absent),
                ("maxLength", type == "string" ? Opt(Gen.Int[6, 255].Select(i => (long)i), 15) : Absent),
                ("pattern", type == "string" ? Opt(Gen.OneOf("^[a-z]+$", "\\d{3}", "^.*$", "(", "[^\\n]"), 10) : Absent),
                ("default", type == "string" ? Opt(Text, 10) : type == "boolean" ? Opt(Gen.Bool, 10) : Opt(Number, 10)),
                ("description", Opt(Text, 25)),
                ("readOnly", Opt(Gen.Const(true), 8)),
                ("writeOnly", Opt(Gen.Const(true), 8)),
                ("deprecated", Opt(Gen.Const(true), 8)),
                ("example", Opt(Text, 5)))));

    private static Gen<Map> Ref(string[] refs) =>
        Gen.OneOf(refs).Select(n => new Map { ["$ref"] = "#/components/schemas/" + n });

    public static Gen<Map> Schema(bool v31, string[] refs, int depth)
    {
        var leaf = refs.Length > 0
            ? Gen.Frequency((5, (IGen<Map>)Primitive(v31)), (1, Ref(refs)))
            : Primitive(v31);
        if (depth <= 0) return leaf;
        var inner = Schema(v31, refs, depth - 1);
        var obj = Name.Array[0, 4].SelectMany(names =>
        {
            var distinct = names.Distinct(StringComparer.Ordinal).ToArray();
            return Gen.Select(
                Keyed(Gen.Const(distinct), _ => inner.Select(s => (object?)s)),
                Gen.Bool.Array[distinct.Length],
                MapOf(
                    ("additionalProperties", Opt(Gen.OneOf<object>(false, true, new Map { ["type"] = "string" }), 15)),
                    ("description", Opt(Text, 20)),
                    ("deprecated", Opt(Gen.Const(true), 5)),
                    ("x-extensible-enum", Opt(Text.List[1, 2], 3))),
                (props, req, rest) =>
                {
                    var m = new Map { ["type"] = "object" };
                    if (props.Count > 0) m["properties"] = props;
                    var required = distinct.Where((_, i) => req[i]).Select(n => (object?)n).ToList();
                    if (required.Count > 0) m["required"] = required;
                    foreach (var (k, v) in rest) m[k] = v;
                    return m;
                });
        });
        var array = Gen.Select(inner, MapOf(
                ("minItems", Opt(Gen.Int[0, 3].Select(i => (long)i), 15)),
                ("maxItems", Opt(Gen.Int[4, 100].Select(i => (long)i), 15)),
                ("uniqueItems", Opt(Gen.Const(true), 10))),
            (items, rest) =>
            {
                var m = new Map { ["type"] = "array", ["items"] = items };
                foreach (var (k, v) in rest) m[k] = v;
                return m;
            });
        var xOf = Gen.Select(Gen.OneOf("oneOf", "anyOf", "allOf"), inner.Array[2, 3], Opt(Name, 15),
            (kw, branches, disc) =>
            {
                var m = new Map { [kw] = branches.Select(b => (object?)b).ToList() };
                if (disc is string d && kw != "allOf") m["discriminator"] = new Map { ["propertyName"] = d };
                return m;
            });
        return Gen.Frequency((4, (IGen<Map>)leaf), (3, obj), (2, array), (1, xOf));
    }

    // ------------------------------------------------------------------ operations

    private static Gen<Map> Content(bool v31, string[] refs) =>
        Keyed(Gen.Shuffle(MediaTypes, 1, 2), _ => Schema(v31, refs, 2).Select(s => (object?)new Map { ["schema"] = s }));

    private static Gen<Map> Parameter(bool v31, string[] refs) =>
        MapOf(
            ("name", Name.Select(n => (object?)n)),
            ("in", Gen.OneOf<object?>("query", "header", "cookie")),
            ("required", Opt(Gen.Bool, 40)),
            ("schema", Schema(v31, refs, 1).Select(s => (object?)s)),
            ("style", Opt(Gen.OneOf("form", "simple", "spaceDelimited", "pipeDelimited", "deepObject"), 10)),
            ("explode", Opt(Gen.Bool, 10)),
            ("deprecated", Opt(Gen.Const(true), 8)),
            ("description", Opt(Text, 20)));

    private static Gen<Map> Response(bool v31, string[] refs) =>
        MapOf(
            ("description", Text.Select(t => (object?)t)),
            ("content", Opt(Content(v31, refs), 60)),
            ("headers", Opt(Keyed(Name.Array[1, 2], _ => MapOf(
                ("schema", Primitive(v31).Select(s => (object?)s)),
                ("required", Opt(Gen.Bool, 30)),
                ("deprecated", Opt(Gen.Const(true), 10))).Select(h => (object?)h)), 20)));

    private static Gen<Map> Operation(bool v31, string[] refs, string method, string path, string[] schemes)
    {
        var pathParams = PathParams(path).Select(p => (object?)new Map
        {
            ["name"] = p, ["in"] = "path", ["required"] = true, ["schema"] = new Map { ["type"] = "string" },
        }).ToList();
        var security = schemes.Length == 0
            ? Absent
            : Opt(Gen.Shuffle(schemes, 0, schemes.Length).Select(ss => ss.Select(s => (object?)new Map { [s] = new List<object?>() }).ToList()), 20);
        return MapOf(
            ("operationId", Opt(Gen.Frequency((4, (IGen<string>)Gen.Const(method + path.Replace("/", "_", StringComparison.Ordinal))), (1, Text)), 60)),
            ("summary", Opt(Text, 30)),
            ("description", Opt(Text, 15)),
            ("tags", Opt(Gen.OneOf(Words).List[1, 2], 25)),
            ("deprecated", Opt(Gen.Const(true), 10)),
            ("x-stability-level", Opt(Gen.OneOf(StabilityLevels), 10)),
            ("parameters", Parameter(v31, refs).List[0, 3].Select(ps =>
            {
                var all = pathParams.Concat(ps.Select(p => (object?)p)).ToList();
                return all.Count == 0 ? null : (object?)all;
            })),
            ("requestBody", method is "post" or "put" or "patch"
                ? Opt(MapOf(("required", Opt(Gen.Bool, 50)), ("content", Content(v31, refs).Select(c => (object?)c)), ("description", Opt(Text, 10))), 70)
                : Absent),
            ("responses", Keyed(Gen.Shuffle(Statuses, 1, 3), _ => Response(v31, refs).Select(r => (object?)r)).Select(r => (object?)r)),
            ("security", security));
    }

    public static IEnumerable<string> PathParams(string path)
    {
        for (int i = path.IndexOf('{', StringComparison.Ordinal); i >= 0; i = path.IndexOf('{', i + 1))
        {
            int j = path.IndexOf('}', i);
            if (j < 0) yield break;
            yield return path[(i + 1)..j];
        }
    }

    private static Gen<Map> SecurityScheme(string name) => name switch
    {
        "api_key" => MapOf(("type", Gen.Const<object?>("apiKey")), ("in", Gen.OneOf<object?>("header", "query", "cookie")), ("name", Name.Select(n => (object?)n))),
        "bearer" => MapOf(("type", Gen.Const<object?>("http")), ("scheme", Gen.OneOf<object?>("bearer", "basic")), ("bearerFormat", Opt(Gen.Const("JWT"), 30))),
        _ => Gen.Shuffle(Scopes, 1, 3).Select(scopes => new Map
        {
            ["type"] = "oauth2",
            ["flows"] = new Map
            {
                ["clientCredentials"] = new Map
                {
                    ["tokenUrl"] = "https://auth.example.com/token",
                    ["scopes"] = ToMap(scopes.Select(s => new KeyValuePair<string, object?>(s, s + " access"))),
                },
            },
        }),
    };

    private static Map ToMap(IEnumerable<KeyValuePair<string, object?>> kvs)
    {
        var m = new Map();
        foreach (var (k, v) in kvs) m[k] = v;
        return m;
    }

    /// <summary>A whole document.</summary>
    public static readonly Gen<Map> Spec =
        Gen.Select(Gen.Bool, Gen.Shuffle(ComponentNames, 0, 3), Gen.Shuffle(SchemeNames, 0, 2)).SelectMany((v31, refs, schemes) =>
        {
            var paths = Keyed(Gen.Shuffle(PathTemplates, 1, 4), path =>
                Keyed(Gen.Shuffle(Methods, 1, 3), method => Operation(v31, refs, method, path, schemes).Select(o => (object?)o)).Select(p => (object?)p));
            var components = Gen.Select(
                // a component may be just a $ref, so cycles of bare references occur too (no value: the diff fails
                // when both sides have one)
                Keyed(Gen.Const(refs), _ => Schema(v31, refs, 2).Select(s => (object?)s)),
                Keyed(Gen.Const(schemes), n => SecurityScheme(n).Select(s => (object?)s)),
                (schemas, sec) =>
                {
                    var c = new Map();
                    if (schemas.Count > 0) c["schemas"] = schemas;
                    if (sec.Count > 0) c["securitySchemes"] = sec;
                    return c.Count == 0 ? null : (object?)c;
                });
            return MapOf(
                ("openapi", Gen.Const<object?>(v31 ? "3.1.0" : "3.0.3")),
                ("info", MapOf(("title", Text.Select(t => (object?)t)), ("version", Gen.OneOf<object?>("1.0.0", "2.0", "v1"))).Select(i => (object?)i)),
                ("servers", Opt(Gen.Shuffle(ServerUrls, 1, 2)
                    .Select(us => us.Select(u => (object?)new Map { ["url"] = u }).ToList()), 30)),
                ("paths", paths.Select(p => (object?)p)),
                ("components", components),
                ("security", schemes.Length == 0 ? Absent
                    : Opt(Gen.Const(schemes.Take(1).Select(s => (object?)new Map { [s] = new List<object?>() }).ToList()), 30)),
                ("tags", Opt(Gen.Shuffle(Words, 1, 2).Select(ws => ws.Select(w => (object?)new Map { ["name"] = w }).ToList()), 15)))
                .Select(Dedupe);
        });

    // ------------------------------------------------------------------ helpers

    /// <summary>Drops all but the first of the parameters of a path or operation that share location and name
    /// (header names case-insensitively). OpenAPI requires them to be unique, and oasdiff pairs duplicates up in an
    /// order that changes from run to run, so they would only make noise.</summary>
    public static Map Dedupe(Map doc)
    {
        if (doc.GetValueOrDefault("paths") is not Map paths) return doc;
        foreach (var item in paths.Values.OfType<Map>())
            foreach (var owner in Methods.Select(m => item.GetValueOrDefault(m)).OfType<Map>().Prepend(item))
                if (owner.GetValueOrDefault("parameters") is List<object?> ps)
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    ps.RemoveAll(p => p is Map m && !seen.Add(ParameterKey(m)));
                    ExplodedNames(doc, ps, "query");
                    ExplodedNames(doc, ps, "cookie");
                }
        return doc;
    }

    /// <summary>oasdiff expands a query or cookie parameter with an object schema (form style, the default there) into
    /// one parameter per property, so a property named like another parameter in the same place (or like a property of
    /// another such parameter) is a duplicate too. Drops those properties; a referenced schema with such a property is
    /// replaced by a string.</summary>
    private static void ExplodedNames(Map doc, List<object?> ps, string at)
    {
        var query = ps.OfType<Map>().Where(p => p.GetValueOrDefault("in") as string == at).ToList();
        var taken = new HashSet<string>(query.Select(p => p.GetValueOrDefault("name") as string ?? ""), StringComparer.Ordinal);
        foreach (var p in query)
        {
            if (p.GetValueOrDefault("schema") is not Map s) continue;
            if (s.GetValueOrDefault("$ref") is string r)
            {
                var target = doc.GetValueOrDefault("components") is Map c && c.GetValueOrDefault("schemas") is Map ss
                    ? ss.GetValueOrDefault(r[(r.LastIndexOf('/') + 1)..]) as Map : null;
                if (target?.GetValueOrDefault("properties") is Map tp && tp.Keys.Any(k => !taken.Add(k))) p["schema"] = new Map { ["type"] = "string" };
                continue;
            }
            if (s.GetValueOrDefault("properties") is not Map props) continue;
            foreach (var k in props.Keys.ToList())
                if (!taken.Add(k))
                {
                    props.Remove(k);
                    if (s.GetValueOrDefault("required") is List<object?> req) { req.Remove(k); if (req.Count == 0) s.Remove("required"); }
                }
        }
    }

    private static string ParameterKey(Map p)
    {
        var at = p.GetValueOrDefault("in") as string ?? "";
        var name = p.GetValueOrDefault("name") as string ?? "";
        return at + "\0" + (at == "header" ? name.ToLowerInvariant() : name);
    }

    public static Map Clone(Map m)
    {
        var c = new Map();
        foreach (var (k, v) in m) c[k] = CloneValue(v);
        return c;
    }

    public static object? CloneValue(object? v) => v switch
    {
        Map m => Clone(m),
        List<object?> l => l.Select(CloneValue).ToList(),
        _ => v,
    };
}
