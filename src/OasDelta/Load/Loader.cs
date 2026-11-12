using System.Runtime.CompilerServices;
using System.Text.Json;
using OasDelta.Model;
using OasDelta.Yaml;

namespace OasDelta.Load;

public sealed class LoadException(string message) : Exception(message);

/// <summary>One parsed source document (root spec or an externally referenced file).</summary>
internal sealed class Document
{
    public required string Location;   // display / origin file name
    public required string Key;        // canonical cache key (absolute path or URL)
    public required Node? Root;
    public bool TrackOrigin;
    public OpenApiDoc? Spec;           // for documents that are OpenAPI roots
    private Dictionary<Node, Node>? _keyOf;

    public Node? KeyOf(Node value)
    {
        if (!TrackOrigin) return null;   // keys only feed origins
        if (_keyOf is null)
        {
            _keyOf = new Dictionary<Node, Node>(ReferenceEqualityComparer.Instance);
            if (Root is not null) IndexKeys(Root);
        }
        return _keyOf.TryGetValue(value, out var k) ? k : null;
    }

    private void IndexKeys(Node n)
    {
        var stack = new Stack<Node>();
        var seen = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        stack.Push(n);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!seen.Add(cur)) continue;
            if (cur.Pairs is not null)
                foreach (var (k, v) in cur.Pairs) { _keyOf!.TryAdd(v, k); if (v.Kind != NodeKind.Scalar) stack.Push(v); }
            else if (cur.Items is not null)
                foreach (var it in cur.Items)
                {
                    if (it.Pairs is { Count: > 0 }) _keyOf!.TryAdd(it, it.Pairs[0].Key);
                    if (it.Kind != NodeKind.Scalar) stack.Push(it);
                }
        }
    }
}

public sealed class LoaderOptions
{
    public bool IncludeOrigin = true;
    public bool AllowExternalRefs = true;
}

/// <summary>Loads OpenAPI documents into the model, resolving $refs (kin-openapi Loader equivalent).</summary>
public sealed partial class SpecLoader(LoaderOptions? options = null)
{
    private readonly LoaderOptions _opt = options ?? new LoaderOptions();
    private readonly Dictionary<string, Document> _docs = new(StringComparer.Ordinal);
    private readonly Dictionary<(Node, Type), object> _cache = new(NodeTypeComparer.Instance);
    private Document _root = null!;
    private bool _is31;

    private sealed class NodeTypeComparer : IEqualityComparer<(Node, Type)>
    {
        public static readonly NodeTypeComparer Instance = new();
        public bool Equals((Node, Type) a, (Node, Type) b) => ReferenceEquals(a.Item1, b.Item1) && a.Item2 == b.Item2;
        public int GetHashCode((Node, Type) k) => HashCode.Combine(RuntimeHelpers.GetHashCode(k.Item1), k.Item2);
    }

    public OpenApiDoc LoadFile(string path)
    {
        string text = ReadSource(path, out var key);
        return LoadFromText(text, path, key);
    }

    internal const string GitKeyPrefix = "git::";

    /// <summary>load.loadFromGitRevision: "ref:path" read with git show; relative $refs stay within the revision.</summary>
    public OpenApiDoc LoadGitRevision(string gitRef, bool fetch)
    {
        string text;
        try { text = GitSource.ReadRefContent(gitRef, fetch); }
        catch (LoadException e) { throw new LoadException($"failed to load spec from git revision {Checker.Fmt.GoQuote(gitRef)}: {e.Message}"); }
        return LoadFromText(text, gitRef[(gitRef.IndexOf(':') + 1)..], GitKeyPrefix + gitRef);
    }

    private static string GoPathDir(string p)
    {
        int i = p.LastIndexOf('/');
        return i < 0 ? "." : CleanPath(p[..(i + 1)]);
    }

    public OpenApiDoc LoadFromText(string text, string location, string? key = null)
    {
        key ??= IsUrl(location) ? location : Path.GetFullPath(location == "-" ? "stdin" : location);
        var doc = ParseDocument(text, location, key);
        _root = doc;
        var spec = new OpenApiDoc();
        doc.Spec = spec;
        if (doc.Root is null || !doc.Root.IsMapping)
            throw new LoadException($"failed to load {location}: not an OpenAPI document");
        // version first: 3.1 sibling handling depends on it
        spec.OpenAPI = Str(doc.Root["openapi"]);
        _is31 = spec.IsOpenAPI31OrLater();
        CheckForNulls(doc);
        BuildDoc(doc, doc.Root, spec);
        return spec;
    }

    /// <summary>
    /// The file name kin-openapi records in an element's origin, which oasdiff shows in changelog sources:
    /// <c>url.URL{Path: filepath.ToSlash(location)}.String()</c>. Go escapes the path (net/url, encodePath) and, per
    /// RFC 3986 §4.2, puts "./" in front when the first segment has a colon. So "a b.yaml" shows as "a%20b.yaml", and
    /// on Windows "C:\specs\api.yaml" becomes "./C:/specs/api.yaml", which oasdiff's display then reads as a git
    /// revision ("./C" : "/specs/api.yaml"). Stdin and http(s) locations are left alone.
    /// </summary>
    public static string OriginFile(string location)
    {
        if (location.Length == 0 || location == "-" || location.Contains("://", StringComparison.Ordinal)) return location;
        var escaped = GoEscapePath(ToSlash(location));
        int slash = escaped.IndexOf('/');
        var first = slash < 0 ? escaped : escaped[..slash];
        return first.Contains(':', StringComparison.Ordinal) ? "./" + escaped : escaped;
    }

    /// <summary>Go filepath.ToSlash: only Windows has another separator.</summary>
    internal static string ToSlash(string p) => OperatingSystem.IsWindows() ? p.Replace('\\', '/') : p;

    /// <summary>Go net/url escape(s, encodePath): keeps letters, digits, "-_.~" and "$&amp;+,/:;=@"; escapes every
    /// other UTF-8 byte as %XX.</summary>
    private static string GoEscapePath(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        var sb = new System.Text.StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            char c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || "-_.~$&+,/:;=@".Contains(c, StringComparison.Ordinal)) sb.Append(c);
            else sb.Append('%').Append("0123456789ABCDEF"[b >> 4]).Append("0123456789ABCDEF"[b & 15]);
        }
        return sb.ToString();
    }

    private static bool IsUrl(string s) => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static readonly HttpClient Http = new();

    private static string ReadSource(string location, out string key)
    {
        if (location == "-")
        {
            key = "stdin";
            return Console.In.ReadToEnd();
        }
        if (IsUrl(location))
        {
            key = location;
            try { return Http.GetStringAsync(location).GetAwaiter().GetResult(); }
            catch (Exception e) { throw new LoadException($"failed to load {location}: {e.Message}"); }
        }
        key = Path.GetFullPath(location);
        try { return File.ReadAllText(location); }
        catch (FileNotFoundException) { throw new LoadException($"open {location}: no such file or directory"); }
        catch (DirectoryNotFoundException) { throw new LoadException($"open {location}: no such file or directory"); }
    }

    private Document ParseDocument(string text, string location, string key)
    {
        if (_docs.TryGetValue(key, out var existing)) return existing;
        // JSON documents are read directly (fast path, no origins); anything else goes through the YAML parser
        bool isJson = JsonNodeReader.TryParse(text, out var root);
        if (!isJson)
        {
            try { root = NodeReader.Parse(text); }
            catch (YamlParseException e) { throw new LoadException($"failed to load {location}: {e.Message}"); }
        }
        var doc = new Document { Location = location, Key = key, Root = root, TrackOrigin = _opt.IncludeOrigin && !isJson };
        _docs[key] = doc;
        return doc;
    }


    // ------------------------------------------------------------------ ref resolution

    private const string KinRefusalMessage = "encountered disallowed external reference";
    private const string ExternalRefNotAllowed = "external $ref not allowed (enable --allow-external-refs to permit)";

    /// <summary>load.IsExternalRefDisallowed: whether a load failed because --allow-external-refs=false refused a $ref
    /// (exit code 123), from kin-openapi or from oasdiff's git-revision reader.</summary>
    internal static bool IsExternalRefDisallowed(string message) =>
        message.Contains("disallowed external reference", StringComparison.Ordinal) ||
        message.Contains(ExternalRefNotAllowed, StringComparison.Ordinal);

    private (Document doc, Node node) ResolveRef(Document from, string reference)
    {
        string filePart = reference, fragment = "";
        int hash = reference.IndexOf('#');
        if (hash >= 0) { filePart = reference[..hash]; fragment = reference[(hash + 1)..]; }
        var target = from;
        if (filePart.Length > 0)
        {
            if (!_opt.AllowExternalRefs)
            {
                // load/git.go: a spec read from a git revision reads its relative refs from the same revision, which
                // the policy allows; any other ref (a URL) is refused by oasdiff's reader, with its own message.
                // Elsewhere kin-openapi refuses every ref to another document, a sibling file included.
                if (!from.Key.StartsWith(GitKeyPrefix, StringComparison.Ordinal))
                    throw new LoadException($"{KinRefusalMessage}: {Checker.Fmt.GoQuote(reference)}");
                if (IsUrl(filePart))
                    throw new LoadException($"{ExternalRefNotAllowed}: {filePart}");
            }
            target = LoadExternal(from, filePart);
        }
        var node = target.Root ?? throw new LoadException($"failed to resolve {reference}: empty document");
        if (fragment.Length > 0 && fragment != "/")
        {
            if (fragment[0] != '/') throw new LoadException($"failed to resolve {reference}: expected a JSON pointer");
            foreach (var raw in fragment[1..].Split('/'))
            {
                var token = Uri.UnescapeDataString(raw).Replace("~1", "/").Replace("~0", "~");
                Node? next = null;
                if (node.IsMapping) next = node[token];
                else if (node.IsSequence && int.TryParse(token, out var idx) && idx >= 0 && idx < node.Items!.Count) next = node.Items[idx];
                node = next ?? throw new LoadException($"failed to resolve {reference} in {from.Location}: path not found");
            }
        }
        return (target, node);
    }

    private Document LoadExternal(Document from, string file)
    {
        string location, key;
        if (IsUrl(file)) { location = file; key = file; }
        else if (IsUrl(from.Key))
        {
            var u = new Uri(new Uri(from.Key), file);
            location = key = u.ToString();
        }
        else if (from.Key.StartsWith(GitKeyPrefix, StringComparison.Ordinal))
        {
            // a spec read from a git revision: relative refs resolve inside the same revision ("ref:dir/file")
            var gitBase = from.Key[GitKeyPrefix.Length..];
            int colon = gitBase.IndexOf(':');
            var rel = Uri.UnescapeDataString(file);
            var joined = gitBase[..(colon + 1)] + CleanPath(GoPathDir(gitBase[(colon + 1)..]) + "/" + rel);
            key = GitKeyPrefix + joined;
            var locDir = GoPathDir(from.Location);
            location = CleanPath(locDir == "." ? rel : locDir + "/" + rel);
            if (_docs.TryGetValue(key, out var gd)) return gd;
            string gitText;
            try { gitText = GitSource.Show(joined); }
            catch (LoadException e) { throw new LoadException($"failed to load external reference {file} from {from.Location}: {e.Message}"); }
            return ParseDocument(gitText, location, key);
        }
        else
        {
            var baseDir = Path.GetDirectoryName(from.Key) ?? "";
            var rel = Uri.UnescapeDataString(file);
            key = Path.GetFullPath(Path.Combine(baseDir, rel));
            // kin's defaultJoin: path.Join(path.Dir(base.Path), ref.Path) on the slash form, unescaped (absolute: as is)
            location = rel.StartsWith('/') ? rel : CleanPath(GoPathDir(ToSlash(from.Location)) + "/" + rel);
        }
        if (_docs.TryGetValue(key, out var d)) return d;
        string text;
        try { text = IsUrl(key) ? Http.GetStringAsync(key).GetAwaiter().GetResult() : File.ReadAllText(key); }
        catch (Exception e) when (e is IOException or HttpRequestException or UnauthorizedAccessException)
        { throw new LoadException($"failed to load external reference {file} from {from.Location}: {e.Message}"); }
        return ParseDocument(text, location, key);
    }

    /// <summary>Go path.Clean.</summary>
    internal static string CleanPath(string p)
    {
        if (p.Length == 0) return ".";
        bool rooted = p[0] == '/';
        var parts = new List<string>();
        foreach (var seg in p.Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..")
            {
                if (parts.Count > 0 && parts[^1] != "..") parts.RemoveAt(parts.Count - 1);
                else if (!rooted) parts.Add("..");
                continue;
            }
            parts.Add(seg);
        }
        var r = string.Join('/', parts);
        if (rooted) r = "/" + r;
        return r.Length == 0 ? "." : r;
    }

    private static string? RefOf(Node n)
    {
        if (!n.IsMapping) return null;
        var r = n["$ref"];
        return r is { IsScalar: true } ? r.Text : null;
    }

    private delegate T Builder<T>(Document doc, Node node, Node? key) where T : class;

    private Ref<T> ReadRef<T>(Document doc, Node n, Node? key, Builder<T> build, int depth = 0) where T : class
    {
        var reference = RefOf(n);
        if (reference is null)
            return new Ref<T>(Cached(doc, n, key, build)) { Origin = MakeOrigin(doc, n, key) };

        var result = new Ref<T> { RefPath = reference, Origin = MakeOrigin(doc, n, key) };
        foreach (var (k, v) in n.Pairs!)
            if (k.KeyString.StartsWith("x-", StringComparison.Ordinal))
                (result.Extensions ??= new(StringComparer.Ordinal))[k.KeyString] = v.ToValue();
        // a cycle of bare references ("A: {$ref: B}, B: {$ref: A}") has no value: kin-openapi loads it and leaves Value
        // nil, and the diff fails only if it compares such a schema on both sides ("base schema value is nil", 104)
        if (depth > 64) return result;
        var noOrigin = _noOrigin;
        _noOrigin = 0; // the target is located where it is defined
        try
        {
            var (tdoc, tnode) = ResolveRef(doc, reference);
            if (RefOf(tnode) is not null)
            {
                // ref to a ref: share the final value
                var inner = ReadRef(tdoc, tnode, tdoc.KeyOf(tnode), build, depth + 1);
                result.Value = inner.Value;
            }
            else result.Value = Cached(tdoc, tnode, tdoc.KeyOf(tnode), build);
        }
        finally { _noOrigin = noOrigin; }
        return result;
    }

    private T Cached<T>(Document doc, Node n, Node? key, Builder<T> build) where T : class
    {
        if (_cache.TryGetValue((n, typeof(T)), out var v)) return (T)v;
        return build(doc, n, key);
    }

    private void Register(Node n, object value) => _cache[(n, value.GetType())] = value;

    /// <summary>Above zero while building what is written inline under a header: kin-openapi's Header embeds Parameter,
    /// and its origin walk skips the embedded struct, so the header's schema, content and examples get no origin
    /// (changes in them are located at the operation). Whatever they reach through $ref has its own origin.</summary>
    private int _noOrigin;

    private Origin? MakeOrigin(Document doc, Node n, Node? key)
    {
        if (_noOrigin > 0 || !doc.TrackOrigin || n.Kind != NodeKind.Mapping) return null;
        key ??= doc.KeyOf(n);
        if (key is null)
        {
            // oasdiff/yaml3 locates a mapping that is not a map value (a sequence item, the document root) by its
            // first key, and gives an empty one no origin at all (addOriginInSeq, decoder.document)
            if (n.Pairs is not { Count: > 0 }) return null;
            key = n.Pairs[0].Key;
        }
        var o = new Origin();
        if (key is not null)
            o.Key = new Location { File = doc.Location, Line = key.Line, Column = key.Column, Name = key.Text ?? "", EndLine = n.EndLine, EndColumn = n.EndColumn };
        if (n.Pairs is { Count: > 0 })
        {
            o.Fields = new List<Location>(n.Pairs.Count);
            foreach (var (k, v) in n.Pairs)
            {
                o.Fields.Add(new Location { File = doc.Location, Line = k.Line, Column = k.Column, Name = k.Text ?? "" });
                if (v.IsSequence && v.Items!.Count > 0)
                {
                    List<Location>? items = null;
                    foreach (var it in v.Items)
                        if (it.IsScalar) (items ??= []).Add(new Location { File = doc.Location, Line = it.Line, Column = it.Column, Name = it.Text ?? "" });
                    if (items is not null) (o.Sequences ??= new(StringComparer.Ordinal))[k.Text ?? ""] = items;
                }
            }
        }
        return o;
    }

    // ------------------------------------------------------------------ helpers

    private static string Str(Node? n) => n is { IsScalar: true } ? (n.ToValue() is string s ? s : n.Text ?? "") : "";
    private static bool Bool(Node? n) => n is { IsScalar: true } && n.ToValue() is true;
    private static bool? BoolPtr(Node? n) => n is { IsScalar: true } && n.ToValue() is bool b ? b : null;
    private static double? Num(Node? n) => n is { IsScalar: true } && n.ToValue() is double d ? d : null;
    private static ulong U64(Node? n) => Num(n) is double d && d >= 0 ? (ulong)d : 0;
    private static ulong? U64Ptr(Node? n) => Num(n) is double d && d >= 0 ? (ulong)d : null;

    private static List<string> StrList(Node? n)
    {
        var r = new List<string>();
        if (n is null) return r;
        if (n.IsSequence) foreach (var it in n.Items!) r.Add(Str(it));
        return r;
    }

    private static void SetExtensions(Element e, Node n, HashSet<string> known, bool onlyX = false)
    {
        if (n.Pairs is null) return;
        foreach (var (k, v) in n.Pairs)
        {
            var name = k.KeyString;
            if (known.Contains(name)) continue;
            if (onlyX && !name.StartsWith("x-", StringComparison.Ordinal)) continue;
            (e.Extensions ??= new(StringComparer.Ordinal))[name] = v.ToValue();
        }
    }

    private static HashSet<string> Known(params string[] names) => new(names, StringComparer.Ordinal);

    private Dictionary<string, Ref<T>> RefMap<T>(Document doc, Node? n, Builder<T> build) where T : class
    {
        var r = new Dictionary<string, Ref<T>>(StringComparer.Ordinal);
        if (n is null || !n.IsMapping) return r;
        foreach (var (k, v) in n.Pairs!)
            if (v.IsMapping || (typeof(T) == typeof(Schema) && v.IsScalar && v.ToValue() is bool))
                r[k.KeyString] = ReadRef(doc, v, k, build);
            // null where kin-openapi does not reject it (components.links, an encoding's headers): a nil entry, which
            // the diff cannot compare ("link reference is nil"); everywhere else CheckForNulls has failed the load
            else if (v.IsNull) r[k.KeyString] = new Ref<T>();
        return r;
    }

    // ------------------------------------------------------------------ builders

    private static readonly HashSet<string> DocKnown = Known("openapi", "components", "info", "paths", "security", "servers", "tags", "externalDocs", "webhooks", "jsonSchemaDialect");

    private void BuildDoc(Document doc, Node n, OpenApiDoc spec)
    {
        spec.Origin = MakeOrigin(doc, n, null);
        SetExtensions(spec, n, DocKnown);
        if (n["info"] is { IsMapping: true } info) spec.Info = BuildInfo(doc, info, null);
        if (n["components"] is { IsMapping: true } comps) spec.Components = BuildComponents(doc, comps);
        if (n["paths"] is { IsMapping: true } paths) spec.Paths = BuildPaths(doc, paths);
        if (n["security"] is { IsSequence: true } sec) { spec.Security = BuildSecurity(sec); spec.HasSecurity = true; }
        if (n["servers"] is { IsSequence: true } servers) spec.Servers = BuildServers(doc, servers);
        if (n["tags"] is { IsSequence: true } tags) foreach (var t in tags.Items!) if (t.IsMapping) spec.Tags.Add(BuildTag(doc, t));
        if (n["externalDocs"] is { IsMapping: true } ed) spec.ExternalDocs = BuildExternalDocs(doc, ed);
        if (n["webhooks"] is { IsMapping: true } wh)
        {
            spec.Webhooks = new(StringComparer.Ordinal);
            foreach (var (k, v) in wh.Pairs!)
            {
                if (v.IsMapping) spec.Webhooks[k.KeyString] = BuildPathItemOrRef(doc, v, k);
                else if (v.IsNull) spec.Webhooks[k.KeyString] = new PathItem { IsNil = true }; // still a webhook to kin
            }
        }
        spec.JSONSchemaDialect = Str(n["jsonSchemaDialect"]);
    }

    private static readonly HashSet<string> InfoKnown = Known("title", "summary", "description", "termsOfService", "contact", "license", "version");
    private Info BuildInfo(Document doc, Node n, Node? key)
    {
        var i = new Info
        {
            Origin = MakeOrigin(doc, n, key),
            Title = Str(n["title"]), Summary = Str(n["summary"]), Description = Str(n["description"]),
            TermsOfService = Str(n["termsOfService"]), Version = Str(n["version"]),
        };
        SetExtensions(i, n, InfoKnown);
        if (n["contact"] is { IsMapping: true } c)
        {
            i.Contact = new Contact { Origin = MakeOrigin(doc, c, null), Name = Str(c["name"]), URL = Str(c["url"]), Email = Str(c["email"]) };
            SetExtensions(i.Contact, c, Known("name", "url", "email"));
        }
        if (n["license"] is { IsMapping: true } l)
        {
            i.License = new License { Origin = MakeOrigin(doc, l, null), Name = Str(l["name"]), URL = Str(l["url"]), Identifier = Str(l["identifier"]) };
            SetExtensions(i.License, l, Known("name", "url", "identifier"));
        }
        return i;
    }

    private ExternalDocs BuildExternalDocs(Document doc, Node n)
    {
        var e = new ExternalDocs { Origin = MakeOrigin(doc, n, null), Description = Str(n["description"]), URL = Str(n["url"]) };
        SetExtensions(e, n, Known("description", "url"));
        return e;
    }

    private Tag BuildTag(Document doc, Node n)
    {
        var t = new Tag { Origin = MakeOrigin(doc, n, null), Name = Str(n["name"]), Description = Str(n["description"]) };
        if (n["externalDocs"] is { IsMapping: true } ed) t.ExternalDocs = BuildExternalDocs(doc, ed);
        SetExtensions(t, n, Known("name", "description", "externalDocs"));
        return t;
    }

    private List<Server> BuildServers(Document doc, Node n)
    {
        var r = new List<Server>();
        foreach (var s in n.Items!)
        {
            if (!s.IsMapping) continue;
            var srv = new Server { Origin = MakeOrigin(doc, s, null), URL = Str(s["url"]), Description = Str(s["description"]) };
            SetExtensions(srv, s, Known("url", "description", "variables"));
            if (s["variables"] is { IsMapping: true } vars)
                foreach (var (k, v) in vars.Pairs!)
                {
                    if (!v.IsMapping) continue;
                    var sv = new ServerVariable { Origin = MakeOrigin(doc, v, k), Default = Str(v["default"]), Description = Str(v["description"]), Enum = StrList(v["enum"]) };
                    SetExtensions(sv, v, Known("enum", "default", "description"));
                    srv.Variables[k.KeyString] = sv;
                }
            r.Add(srv);
        }
        return r;
    }

    private static List<SecurityRequirement> BuildSecurity(Node n)
    {
        var r = new List<SecurityRequirement>();
        foreach (var item in n.Items!)
        {
            var req = new SecurityRequirement { IsNil = item.IsNull };
            if (item.IsMapping) foreach (var (k, v) in item.Pairs!) req[k.KeyString] = StrList(v);
            r.Add(req);
        }
        return r;
    }

    private static readonly HashSet<string> ComponentsKnown = Known("schemas", "parameters", "headers", "requestBodies", "responses", "securitySchemes", "examples", "links", "callbacks", "pathItems");
    private Components BuildComponents(Document doc, Node n)
    {
        var c = new Components { Origin = MakeOrigin(doc, n, null) };
        SetExtensions(c, n, ComponentsKnown);
        c.Schemas = SchemaMap(doc, n["schemas"]);
        c.Parameters = RefMap<Parameter>(doc, n["parameters"], BuildParameter);
        c.Headers = RefMap<Header>(doc, n["headers"], BuildHeader);
        c.RequestBodies = RefMap<RequestBody>(doc, n["requestBodies"], BuildRequestBody);
        c.Responses = RefMap<Response>(doc, n["responses"], BuildResponse);
        c.SecuritySchemes = RefMap<SecurityScheme>(doc, n["securitySchemes"], BuildSecurityScheme);
        c.Examples = RefMap<Example>(doc, n["examples"], BuildExample);
        c.Links = RefMap<Link>(doc, n["links"], BuildLink);
        c.Callbacks = RefMap<Callback>(doc, n["callbacks"], BuildCallback);
        return c;
    }

    private Paths BuildPaths(Document doc, Node n)
    {
        var p = new Paths { Origin = MakeOrigin(doc, n, null) };
        foreach (var (k, v) in n.Pairs!)
        {
            var name = k.KeyString;
            if (name.StartsWith("x-", StringComparison.Ordinal)) { (p.Extensions ??= new(StringComparer.Ordinal))[name] = v.ToValue(); continue; }
            if (v.IsMapping) p.Map[name] = BuildPathItemOrRef(doc, v, k);
            else if (v.IsNull) p.Map[name] = new PathItem();
        }
        return p;
    }

    private PathItem BuildPathItemOrRef(Document doc, Node n, Node? key)
    {
        var reference = RefOf(n);
        if (!n.IsMapping) return new PathItem(); // a $ref to null (components.pathItems.P: null): kin reads an empty path item
        if (reference is null) return Cached<PathItem>(doc, n, key, BuildPathItem);
        var (tdoc, tnode) = ResolveRef(doc, reference);
        var target = BuildPathItemOrRef(tdoc, tnode, tdoc.KeyOf(tnode));
        var copy = target.ShallowClone();
        copy.Ref = reference;
        // siblings of $ref (summary/description) override
        if (n["summary"] is { } s) copy.Summary = Str(s);
        if (n["description"] is { } d) copy.Description = Str(d);
        return copy;
    }

    private static readonly HashSet<string> PathItemKnown = Known("$ref", "summary", "description", "connect", "delete", "get", "head", "options", "patch", "post", "put", "trace", "query", "servers", "parameters", "additionalOperations");
    private PathItem BuildPathItem(Document doc, Node n, Node? key)
    {
        var p = new PathItem { Origin = MakeOrigin(doc, n, key) };
        Register(n, p);
        p.Summary = Str(n["summary"]);
        p.Description = Str(n["description"]);
        SetExtensions(p, n, PathItemKnown);
        foreach (var (k, v) in n.Pairs!)
        {
            var method = k.KeyString;
            if (!v.IsMapping) continue;
            switch (method)
            {
                case "connect" or "delete" or "get" or "head" or "options" or "patch" or "post" or "put" or "trace" or "query":
                    p.SetOperation(method.ToUpperInvariant(), BuildOperation(doc, v, k));
                    break;
            }
        }
        if (n["additionalOperations"] is { IsMapping: true } add)
            foreach (var (k, v) in add.Pairs!)
                if (v.IsMapping) (p.AdditionalOperations ??= new(StringComparer.Ordinal))[k.KeyString] = BuildOperation(doc, v, k);
        if (n["servers"] is { IsSequence: true } servers) p.Servers = BuildServers(doc, servers);
        if (n["parameters"] is { IsSequence: true } ps) p.Parameters = ParameterList(doc, ps);
        return p;
    }

    private List<Ref<Parameter>> ParameterList(Document doc, Node n)
    {
        var r = new List<Ref<Parameter>>();
        foreach (var it in n.Items!) if (it.IsMapping) r.Add(ReadRef<Parameter>(doc, it, null, BuildParameter));
        return r;
    }

    private static readonly HashSet<string> OperationKnown = Known("tags", "summary", "description", "operationId", "parameters", "requestBody", "responses", "callbacks", "deprecated", "security", "servers", "externalDocs");
    private Operation BuildOperation(Document doc, Node n, Node? key)
    {
        var op = new Operation
        {
            Origin = MakeOrigin(doc, n, key),
            Tags = StrList(n["tags"]),
            Summary = Str(n["summary"]), Description = Str(n["description"]), OperationID = Str(n["operationId"]),
            Deprecated = Bool(n["deprecated"]),
        };
        SetExtensions(op, n, OperationKnown);
        if (n["parameters"] is { IsSequence: true } ps) op.Parameters = ParameterList(doc, ps);
        if (n["requestBody"] is { IsMapping: true } rb) op.RequestBody = ReadRef<RequestBody>(doc, rb, null, BuildRequestBody);
        if (n["responses"] is { IsMapping: true } resp) op.Responses = BuildResponses(doc, resp);
        op.Callbacks = RefMap<Callback>(doc, n["callbacks"], BuildCallback);
        if (n["security"] is { IsSequence: true } sec) op.Security = BuildSecurity(sec);
        if (n["servers"] is { IsSequence: true } servers) op.Servers = BuildServers(doc, servers);
        if (n["externalDocs"] is { IsMapping: true } ed) op.ExternalDocs = BuildExternalDocs(doc, ed);
        return op;
    }

    private Responses BuildResponses(Document doc, Node n)
    {
        var r = new Responses { Origin = MakeOrigin(doc, n, null), Extensions = new(StringComparer.Ordinal) };
        foreach (var (k, v) in n.Pairs!)
        {
            var name = k.KeyString;
            if (name.StartsWith("x-", StringComparison.Ordinal)) { r.Extensions[name] = v.ToValue(); continue; }
            if (v.IsMapping) r.Map[name] = ReadRef<Response>(doc, v, k, BuildResponse);
        }
        return r;
    }

    private static readonly HashSet<string> ResponseKnown = Known("description", "headers", "content", "links");
    private Response BuildResponse(Document doc, Node n, Node? key)
    {
        var r = new Response { Origin = MakeOrigin(doc, n, key) };
        Register(n, r);
        if (n["description"] is { IsScalar: true } d) r.Description = Str(d);
        SetExtensions(r, n, ResponseKnown);
        r.Headers = RefMap<Header>(doc, n["headers"], BuildHeader);
        r.Content = BuildContent(doc, n["content"]);
        r.Links = RefMap<Link>(doc, n["links"], BuildLink);
        return r;
    }

    private static readonly HashSet<string> RequestBodyKnown = Known("description", "required", "content");
    private RequestBody BuildRequestBody(Document doc, Node n, Node? key)
    {
        var r = new RequestBody { Origin = MakeOrigin(doc, n, key), Description = Str(n["description"]), Required = Bool(n["required"]) };
        Register(n, r);
        SetExtensions(r, n, RequestBodyKnown);
        r.Content = BuildContent(doc, n["content"]);
        return r;
    }

    private Dictionary<string, MediaType> BuildContent(Document doc, Node? n)
    {
        var r = new Dictionary<string, MediaType>(StringComparer.Ordinal);
        if (n is null || !n.IsMapping) return r;
        foreach (var (k, v) in n.Pairs!)
            if (v.IsMapping) r[k.KeyString] = BuildMediaType(doc, v, k);
            else if (v.IsNull) r[k.KeyString] = new MediaType { IsNil = true };
        return r;
    }

    private static readonly HashSet<string> MediaTypeKnown = Known("schema", "itemSchema", "example", "examples", "encoding");
    private MediaType BuildMediaType(Document doc, Node n, Node? key)
    {
        var m = new MediaType { Origin = MakeOrigin(doc, n, key), Example = n["example"]?.ToValue() };
        SetExtensions(m, n, MediaTypeKnown);
        if (n["schema"] is { } s && IsSchemaNode(s)) m.Schema = ReadSchemaRef(doc, s, null);
        if (n["itemSchema"] is { } i && IsSchemaNode(i)) m.ItemSchema = ReadSchemaRef(doc, i, null);
        m.Examples = RefMap<Example>(doc, n["examples"], BuildExample);
        if (n["encoding"] is { IsMapping: true } enc)
            foreach (var (k, v) in enc.Pairs!)
            {
                if (v.IsNull) { m.Encoding[k.KeyString] = new Encoding { IsNil = true }; continue; }
                if (!v.IsMapping) continue;
                var e = new Encoding { Origin = MakeOrigin(doc, v, k), ContentType = Str(v["contentType"]), Style = Str(v["style"]), Explode = BoolPtr(v["explode"]), AllowReserved = Bool(v["allowReserved"]) };
                SetExtensions(e, v, Known("contentType", "headers", "style", "explode", "allowReserved"));
                e.Headers = RefMap<Header>(doc, v["headers"], BuildHeader);
                m.Encoding[k.KeyString] = e;
            }
        return m;
    }

    private static readonly HashSet<string> ParameterKnown = Known("name", "in", "description", "style", "explode", "allowEmptyValue", "allowReserved", "deprecated", "required", "schema", "example", "examples", "content");
    private Parameter BuildParameter(Document doc, Node n, Node? key)
    {
        var p = new Parameter();
        Register(n, p);
        FillParameter(doc, n, key, p);
        return p;
    }

    private Header BuildHeader(Document doc, Node n, Node? key)
    {
        var h = new Header();
        Register(n, h);
        FillParameter(doc, n, key, h);
        return h;
    }

    private void FillParameter(Document doc, Node n, Node? key, Parameter p)
    {
        p.Origin = MakeOrigin(doc, n, key);
        p.Name = Str(n["name"]); p.In = Str(n["in"]); p.Description = Str(n["description"]); p.Style = Str(n["style"]);
        p.Explode = BoolPtr(n["explode"]);
        p.AllowEmptyValue = Bool(n["allowEmptyValue"]); p.AllowReserved = Bool(n["allowReserved"]);
        p.Deprecated = Bool(n["deprecated"]); p.Required = Bool(n["required"]);
        p.Example = n["example"]?.ToValue();
        SetExtensions(p, n, ParameterKnown);
        if (p is Header) _noOrigin++;
        try
        {
            if (n["schema"] is { } s && IsSchemaNode(s)) p.Schema = ReadSchemaRef(doc, s, null);
            p.Examples = RefMap<Example>(doc, n["examples"], BuildExample);
            p.Content = BuildContent(doc, n["content"]);
        }
        finally { if (p is Header) _noOrigin--; }
    }

    private Example BuildExample(Document doc, Node n, Node? key)
    {
        var e = new Example { Origin = MakeOrigin(doc, n, key), Summary = Str(n["summary"]), Description = Str(n["description"]), ExternalValue = Str(n["externalValue"]), Value = n["value"]?.ToValue() };
        Register(n, e);
        SetExtensions(e, n, Known("summary", "description", "value", "externalValue"));
        return e;
    }

    private Link BuildLink(Document doc, Node n, Node? key)
    {
        var l = new Link { Origin = MakeOrigin(doc, n, key), OperationRef = Str(n["operationRef"]), OperationID = Str(n["operationId"]), Description = Str(n["description"]), RequestBody = n["requestBody"]?.ToValue() };
        Register(n, l);
        SetExtensions(l, n, Known("operationRef", "operationId", "description", "parameters", "server", "requestBody"));
        if (n["parameters"] is { IsMapping: true } ps) foreach (var (k, v) in ps.Pairs!) l.Parameters[k.KeyString] = v.ToValue();
        if (n["server"] is { IsMapping: true } srv)
        {
            var seq = new Node { Kind = NodeKind.Sequence, Items = [srv] };
            l.Server = BuildServers(doc, seq)[0];
        }
        return l;
    }

    private Callback BuildCallback(Document doc, Node n, Node? key)
    {
        var c = new Callback { Origin = MakeOrigin(doc, n, key) };
        Register(n, c);
        foreach (var (k, v) in n.Pairs!)
        {
            var name = k.KeyString;
            if (name.StartsWith("x-", StringComparison.Ordinal)) { (c.Extensions ??= new(StringComparer.Ordinal))[name] = v.ToValue(); continue; }
            if (v.IsMapping) c.Map[name] = BuildPathItemOrRef(doc, v, k);
        }
        return c;
    }

    private SecurityScheme BuildSecurityScheme(Document doc, Node n, Node? key)
    {
        var s = new SecurityScheme
        {
            Origin = MakeOrigin(doc, n, key), Type = Str(n["type"]), Description = Str(n["description"]), Name = Str(n["name"]), In = Str(n["in"]),
            Scheme = Str(n["scheme"]), BearerFormat = Str(n["bearerFormat"]), OpenIdConnectUrl = Str(n["openIdConnectUrl"]),
        };
        Register(n, s);
        SetExtensions(s, n, Known("type", "description", "name", "in", "scheme", "bearerFormat", "flows", "openIdConnectUrl"));
        if (n["flows"] is { IsMapping: true } f)
        {
            var flows = new OAuthFlows { Origin = MakeOrigin(doc, f, null) };
            SetExtensions(flows, f, Known("implicit", "password", "clientCredentials", "authorizationCode"));
            flows.Implicit = Flow(doc, f["implicit"]);
            flows.Password = Flow(doc, f["password"]);
            flows.ClientCredentials = Flow(doc, f["clientCredentials"]);
            flows.AuthorizationCode = Flow(doc, f["authorizationCode"]);
            s.Flows = flows;
        }
        return s;
    }

    private OAuthFlow? Flow(Document doc, Node? n)
    {
        if (n is null || !n.IsMapping) return null;
        var f = new OAuthFlow { Origin = MakeOrigin(doc, n, null), AuthorizationURL = Str(n["authorizationUrl"]), TokenURL = Str(n["tokenUrl"]), RefreshURL = Str(n["refreshUrl"]) };
        SetExtensions(f, n, Known("authorizationUrl", "tokenUrl", "refreshUrl", "scopes"));
        if (n["scopes"] is { IsMapping: true } sc) foreach (var (k, v) in sc.Pairs!) f.Scopes[k.KeyString] = Str(v);
        return f;
    }

    // ------------------------------------------------------------------ schemas

    private static bool IsSchemaNode(Node n) => n.IsMapping || (n.IsScalar && n.ToValue() is bool);

    private Ref<Schema> ReadSchemaRef(Document doc, Node n, Node? key)
    {
        if (n.IsScalar)
        {
            // boolean schema
            var b = n.ToValue() is true;
            return new Ref<Schema>(new Schema { Always = b });
        }
        var reference = RefOf(n);
        if (reference is null || !_is31)
            return ReadRef<Schema>(doc, n, key, BuildSchema);
        // OAS 3.1: sibling keywords next to $ref apply alongside it
        var r = ReadRef<Schema>(doc, n, key, BuildSchema);
        var siblingKeys = new List<string>();
        foreach (var (k, _) in n.Pairs!)
        {
            var name = k.KeyString;
            if (name != "$ref" && !name.StartsWith("x-", StringComparison.Ordinal)) siblingKeys.Add(name);
        }
        if (siblingKeys.Count == 0 || r.Value is null) return r;
        // build sibling schema from the same mapping without $ref
        var siblingNode = new Node { Kind = NodeKind.Mapping, Pairs = [], Line = n.Line, Column = n.Column, EndLine = n.EndLine, EndColumn = n.EndColumn };
        foreach (var p in n.Pairs!) if (p.Key.KeyString != "$ref") siblingNode.Pairs.Add(p);
        var sibling = BuildSchema(doc, siblingNode, key);
        siblingKeys.Sort(StringComparer.Ordinal);
        var annotations = new List<string>();
        var assertions = new List<string>();
        foreach (var f in siblingKeys) (RefSiblingAnnotations.Contains(f) ? annotations : assertions).Add(f);
        var copy = r.Value.ShallowClone();
        ApplySiblingFields(copy, sibling, annotations);
        if (assertions.Count > 0)
        {
            var conjunct = new Schema { Origin = r.Origin };
            ApplySiblingFields(conjunct, sibling, assertions);
            if (!conjunct.IsEmpty())
            {
                copy.AllOf = [.. copy.AllOf, new Ref<Schema>(conjunct)];
            }
        }
        r.Value = copy;
        return r;
    }

    private static readonly HashSet<string> RefSiblingAnnotations = Known("$comment", "allowEmptyValue", "default", "deprecated", "description", "discriminator", "example", "examples", "externalDocs", "nullable", "readOnly", "title", "writeOnly", "xml");

    private static void ApplySiblingFields(Schema dst, Schema s, List<string> fields)
    {
        foreach (var f in fields)
        {
            switch (f)
            {
                case "oneOf": dst.OneOf = s.OneOf; break;
                case "anyOf": dst.AnyOf = s.AnyOf; break;
                case "allOf": dst.AllOf = s.AllOf; break;
                case "not": dst.Not = s.Not; break;
                case "type": dst.Type = s.Type; break;
                case "title": dst.Title = s.Title; break;
                case "format": dst.Format = s.Format; break;
                case "description": dst.Description = s.Description; break;
                case "enum": dst.Enum = s.Enum; break;
                case "default": dst.Default = s.Default; break;
                case "example": dst.Example = s.Example; break;
                case "externalDocs": dst.ExternalDocs = s.ExternalDocs; break;
                case "uniqueItems": dst.UniqueItems = s.UniqueItems; break;
                case "exclusiveMinimum": dst.ExclusiveMin = s.ExclusiveMin; break;
                case "exclusiveMaximum": dst.ExclusiveMax = s.ExclusiveMax; break;
                case "nullable": dst.Nullable = s.Nullable; break;
                case "readOnly": dst.ReadOnly = s.ReadOnly; break;
                case "writeOnly": dst.WriteOnly = s.WriteOnly; break;
                case "allowEmptyValue": dst.AllowEmptyValue = s.AllowEmptyValue; break;
                case "deprecated": dst.Deprecated = s.Deprecated; break;
                case "xml": dst.XML = s.XML; break;
                case "minimum": dst.Min = s.Min; break;
                case "maximum": dst.Max = s.Max; break;
                case "multipleOf": dst.MultipleOf = s.MultipleOf; break;
                case "minLength": dst.MinLength = s.MinLength; break;
                case "maxLength": dst.MaxLength = s.MaxLength; break;
                case "pattern": dst.Pattern = s.Pattern; break;
                case "minItems": dst.MinItems = s.MinItems; break;
                case "maxItems": dst.MaxItems = s.MaxItems; break;
                case "items": dst.Items = s.Items; break;
                case "required": dst.Required = s.Required; break;
                case "properties": dst.Properties = s.Properties; break;
                case "minProperties": dst.MinProps = s.MinProps; break;
                case "maxProperties": dst.MaxProps = s.MaxProps; break;
                case "additionalProperties": dst.AdditionalProperties = s.AdditionalProperties; break;
                case "discriminator": dst.Discriminator = s.Discriminator; break;
                case "const": dst.Const = s.Const; dst.HasConst = s.HasConst; break;
                case "examples": dst.Examples = s.Examples; break;
                case "prefixItems": dst.PrefixItems = s.PrefixItems; break;
                case "contains": dst.Contains = s.Contains; break;
                case "minContains": dst.MinContains = s.MinContains; break;
                case "maxContains": dst.MaxContains = s.MaxContains; break;
                case "patternProperties": dst.PatternProperties = s.PatternProperties; break;
                case "dependentSchemas": dst.DependentSchemas = s.DependentSchemas; break;
                case "propertyNames": dst.PropertyNames = s.PropertyNames; break;
                case "unevaluatedItems": dst.UnevaluatedItems = s.UnevaluatedItems; break;
                case "unevaluatedProperties": dst.UnevaluatedProperties = s.UnevaluatedProperties; break;
                case "if": dst.If = s.If; break;
                case "then": dst.Then = s.Then; break;
                case "else": dst.Else = s.Else; break;
                case "dependentRequired": dst.DependentRequired = s.DependentRequired; break;
                case "$defs": dst.Defs = s.Defs; break;
                case "$schema": dst.SchemaDialect = s.SchemaDialect; break;
                case "$comment": dst.Comment = s.Comment; break;
                case "$id": dst.SchemaID = s.SchemaID; break;
                case "$anchor": dst.Anchor = s.Anchor; break;
                case "$dynamicRef": dst.DynamicRef = s.DynamicRef; break;
                case "$dynamicAnchor": dst.DynamicAnchor = s.DynamicAnchor; break;
                case "contentMediaType": dst.ContentMediaType = s.ContentMediaType; break;
                case "contentEncoding": dst.ContentEncoding = s.ContentEncoding; break;
                case "contentSchema": dst.ContentSchema = s.ContentSchema; break;
                default:
                    if (s.Extensions is not null && s.Extensions.TryGetValue(f, out var ev))
                        (dst.Extensions = dst.Extensions is null ? new(StringComparer.Ordinal) : new(dst.Extensions, StringComparer.Ordinal))[f] = ev;
                    break;
            }
        }
    }

    private static readonly HashSet<string> SchemaKnown = Known(
        "oneOf", "anyOf", "allOf", "not", "type", "title", "format", "description", "enum", "default", "example", "externalDocs",
        "uniqueItems", "exclusiveMinimum", "exclusiveMaximum", "nullable", "readOnly", "writeOnly", "allowEmptyValue", "deprecated", "xml",
        "minimum", "maximum", "multipleOf", "minLength", "maxLength", "pattern", "minItems", "maxItems", "items",
        "required", "properties", "minProperties", "maxProperties", "additionalProperties", "discriminator",
        "const", "examples", "prefixItems", "contains", "minContains", "maxContains", "patternProperties", "dependentSchemas", "propertyNames",
        "unevaluatedItems", "unevaluatedProperties", "if", "then", "else", "dependentRequired", "$defs", "$schema", "$comment",
        "$id", "$anchor", "$dynamicRef", "$dynamicAnchor", "contentMediaType", "contentEncoding", "contentSchema");

    private Ref<Schema>? SchemaField(Document doc, Node n, string name)
    {
        var v = n[name];
        return v is not null && IsSchemaNode(v) ? ReadSchemaRef(doc, v, null) : null;
    }

    private List<Ref<Schema>> SchemaList(Document doc, Node? n)
    {
        var r = new List<Ref<Schema>>();
        if (n is null || !n.IsSequence) return r;
        foreach (var it in n.Items!) if (IsSchemaNode(it)) r.Add(ReadSchemaRef(doc, it, null));
        return r;
    }

    private Dictionary<string, Ref<Schema>> SchemaMap(Document doc, Node? n)
    {
        var r = new Dictionary<string, Ref<Schema>>(StringComparer.Ordinal);
        if (n is null || !n.IsMapping) return r;
        foreach (var (k, v) in n.Pairs!) if (IsSchemaNode(v)) r[k.KeyString] = ReadSchemaRef(doc, v, k);
        return r;
    }

    private BoolSchema BoolOrSchema(Document doc, Node? n)
    {
        var r = new BoolSchema();
        if (n is null) return r;
        if (n.IsScalar && n.ToValue() is bool b) r.Has = b;
        else if (n.IsMapping) r.Schema = ReadSchemaRef(doc, n, null);
        return r;
    }

    private static ExclusiveBound Bound(Node? n)
    {
        var r = new ExclusiveBound();
        if (n is null || !n.IsScalar) return r;
        switch (n.ToValue())
        {
            case bool b: r.Bool = b; break;
            case double d: r.Value = d; break;
        }
        return r;
    }

    private Schema BuildSchema(Document doc, Node n, Node? key)
    {
        if (n.IsScalar) return new Schema { Always = n.ToValue() is true };
        var s = new Schema { Origin = MakeOrigin(doc, n, key) };
        Register(n, s);
        SetExtensions(s, n, SchemaKnown);
        foreach (var (k, v) in n.Pairs!)
        {
            switch (k.KeyString)
            {
                case "oneOf": s.OneOf = SchemaList(doc, v); break;
                case "anyOf": s.AnyOf = SchemaList(doc, v); break;
                case "allOf": s.AllOf = SchemaList(doc, v); break;
                case "not": if (IsSchemaNode(v)) s.Not = ReadSchemaRef(doc, v, null); break;
                case "type":
                    if (v.IsSequence) s.Type = StrList(v);
                    else if (v.IsScalar && v.ToValue() is not null) s.Type = [Str(v)];
                    break;
                case "title": s.Title = Str(v); break;
                case "format": s.Format = Str(v); break;
                case "description": s.Description = Str(v); break;
                case "enum": if (v.IsSequence) s.Enum = (List<object?>)v.ToValue()!; break;
                case "default": s.Default = v.ToValue(); break;
                case "example": s.Example = v.ToValue(); break;
                case "externalDocs": if (v.IsMapping) s.ExternalDocs = BuildExternalDocs(doc, v); break;
                case "uniqueItems": s.UniqueItems = Bool(v); break;
                case "exclusiveMinimum": s.ExclusiveMin = Bound(v); break;
                case "exclusiveMaximum": s.ExclusiveMax = Bound(v); break;
                case "nullable": s.Nullable = Bool(v); break;
                case "readOnly": s.ReadOnly = Bool(v); break;
                case "writeOnly": s.WriteOnly = Bool(v); break;
                case "allowEmptyValue": s.AllowEmptyValue = Bool(v); break;
                case "deprecated": s.Deprecated = Bool(v); break;
                case "xml":
                    if (v.IsMapping)
                    {
                        var x = new Xml { Origin = MakeOrigin(doc, v, k), Name = Str(v["name"]), Namespace = Str(v["namespace"]), Prefix = Str(v["prefix"]), Attribute = Bool(v["attribute"]), Wrapped = Bool(v["wrapped"]) };
                        SetExtensions(x, v, Known("name", "namespace", "prefix", "attribute", "wrapped"));
                        s.XML = x;
                    }
                    break;
                case "minimum": s.Min = Num(v); break;
                case "maximum": s.Max = Num(v); break;
                case "multipleOf": s.MultipleOf = Num(v); break;
                case "minLength": s.MinLength = U64(v); break;
                case "maxLength": s.MaxLength = U64Ptr(v); break;
                case "pattern": s.Pattern = Str(v); break;
                case "minItems": s.MinItems = U64(v); break;
                case "maxItems": s.MaxItems = U64Ptr(v); break;
                case "items": if (IsSchemaNode(v)) s.Items = ReadSchemaRef(doc, v, null); break;
                case "required": s.Required = StrList(v); break;
                case "properties": s.Properties = SchemaMap(doc, v); break;
                case "minProperties": s.MinProps = U64(v); break;
                case "maxProperties": s.MaxProps = U64Ptr(v); break;
                case "additionalProperties": s.AdditionalProperties = BoolOrSchema(doc, v); break;
                case "discriminator":
                    if (v.IsMapping)
                    {
                        var d = new Discriminator { Origin = MakeOrigin(doc, v, k), PropertyName = Str(v["propertyName"]) };
                        SetExtensions(d, v, Known("propertyName", "mapping"));
                        if (v["mapping"] is { IsMapping: true } map) foreach (var (mk, mv) in map.Pairs!) d.Mapping[mk.KeyString] = Str(mv);
                        s.Discriminator = d;
                    }
                    break;
                case "const": s.Const = v.ToValue(); s.HasConst = s.Const is not null; break;
                case "examples": if (v.IsSequence) s.Examples = (List<object?>)v.ToValue()!; break;
                case "prefixItems": s.PrefixItems = SchemaList(doc, v); break;
                case "contains": if (IsSchemaNode(v)) s.Contains = ReadSchemaRef(doc, v, null); break;
                case "minContains": s.MinContains = U64Ptr(v); break;
                case "maxContains": s.MaxContains = U64Ptr(v); break;
                case "patternProperties": s.PatternProperties = SchemaMap(doc, v); break;
                case "dependentSchemas": s.DependentSchemas = SchemaMap(doc, v); break;
                case "propertyNames": if (IsSchemaNode(v)) s.PropertyNames = ReadSchemaRef(doc, v, null); break;
                case "unevaluatedItems": s.UnevaluatedItems = BoolOrSchema(doc, v); break;
                case "unevaluatedProperties": s.UnevaluatedProperties = BoolOrSchema(doc, v); break;
                case "if": if (IsSchemaNode(v)) s.If = ReadSchemaRef(doc, v, null); break;
                case "then": if (IsSchemaNode(v)) s.Then = ReadSchemaRef(doc, v, null); break;
                case "else": if (IsSchemaNode(v)) s.Else = ReadSchemaRef(doc, v, null); break;
                case "dependentRequired":
                    if (v.IsMapping)
                    {
                        s.DependentRequired = new(StringComparer.Ordinal);
                        foreach (var (dk, dv) in v.Pairs!) s.DependentRequired[dk.KeyString] = StrList(dv);
                    }
                    break;
                case "$defs": s.Defs = SchemaMap(doc, v); break;
                case "$schema": s.SchemaDialect = Str(v); break;
                case "$comment": s.Comment = Str(v); break;
                case "$id": s.SchemaID = Str(v); break;
                case "$anchor": s.Anchor = Str(v); break;
                case "$dynamicRef": s.DynamicRef = Str(v); break;
                case "$dynamicAnchor": s.DynamicAnchor = Str(v); break;
                case "contentMediaType": s.ContentMediaType = Str(v); break;
                case "contentEncoding": s.ContentEncoding = Str(v); break;
                case "contentSchema": if (IsSchemaNode(v)) s.ContentSchema = ReadSchemaRef(doc, v, null); break;
            }
        }
        return s;
    }
}
