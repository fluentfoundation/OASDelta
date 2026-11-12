using OasDelta.Yaml;

namespace OasDelta.Load;

/// <summary>
/// kin-openapi's resolve pass (Loader.ResolveRefsIn) fails a document that has <c>null</c> where it expects a header,
/// parameter, request body, response, schema, security scheme, example, callback, link or path item, with
/// "invalid schema: value MUST be an object" and the like (oasdiff exit 102). Only some places are checked: a null in
/// a list or map of them (oneOf: [null], properties: {a: null}, responses: {'200': null}, components.schemas.A: null)
/// fails, while a single optional one (items: null, requestBody: null, a path item or operation that is null) is
/// skipped. This walks the parsed document in the order kin resolves it, so that when there are several the first
/// error is kin's, and ref errors come out in the same order too.
/// </summary>
public sealed partial class SpecLoader
{
    private enum Must { Header, Parameter, RequestBody, Response, Schema, SecurityScheme, Example, Callback, Link, PathItem }

    private static string Message(Must m) => "invalid " + m switch
    {
        Must.Header => "header",
        Must.Parameter => "parameter",
        Must.RequestBody => "requestBody",
        Must.Response => "response",
        Must.Schema => "schema",
        Must.SecurityScheme => "securityScheme",
        Must.Example => "example",
        Must.Callback => "callback",
        Must.Link => "link",
        _ => "path item",
    } + ": value MUST be an object";

    // kin's visitedRefs: the refs being resolved right now (a cycle returns at once); keyed by the ref as written
    private readonly HashSet<string> _resolving = new(StringComparer.Ordinal);
    private readonly HashSet<(Node, Must)> _checked = new(NodeMustComparer.Instance);
    private readonly HashSet<Document> _checkedDocs = new(ReferenceEqualityComparer.Instance);

    private sealed class NodeMustComparer : IEqualityComparer<(Node, Must)>
    {
        public static readonly NodeMustComparer Instance = new();
        public bool Equals((Node, Must) a, (Node, Must) b) => ReferenceEquals(a.Item1, b.Item1) && a.Item2 == b.Item2;
        public int GetHashCode((Node, Must) k) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.Item1), k.Item2);
    }

    private static readonly (string Name, Must Kind)[] ComponentOrder =
    [
        ("headers", Must.Header), ("parameters", Must.Parameter), ("requestBodies", Must.RequestBody), ("responses", Must.Response),
        ("schemas", Must.Schema), ("securitySchemes", Must.SecurityScheme), ("examples", Must.Example), ("callbacks", Must.Callback),
    ];

    /// <summary>Throws kin's error for the first null it would meet in this document.</summary>
    private void CheckForNulls(Document doc)
    {
        if (CheckDocument(doc) is { } m) throw new LoadException(Message(m));
    }

    // Loader.ResolveRefsIn: components (these kinds, in this order; links and pathItems are not resolved), then paths,
    // then webhooks, each by name in Go's sort.Strings order
    private Must? CheckDocument(Document doc)
    {
        if (!_checkedDocs.Add(doc) || doc.Root is not { IsMapping: true } root) return null;
        if (root["components"] is { IsMapping: true } comps)
            foreach (var (name, kind) in ComponentOrder)
                foreach (var (_, v) in Sorted(comps[name]))
                    if (Check(doc, v, kind) is { } m) return m;
        foreach (var (k, v) in Sorted(root["paths"]))
            if (!k.StartsWith("x-", StringComparison.Ordinal) && v.IsMapping && CheckPathItem(doc, v) is { } m) return m;
        foreach (var (_, v) in Sorted(root["webhooks"]))
            if (v.IsMapping && CheckPathItem(doc, v) is { } m) return m;
        return null;
    }

    private static IEnumerable<(string Key, Node Value)> Sorted(Node? map)
    {
        if (map is not { IsMapping: true }) return [];
        return map.Pairs!.Select(p => (p.Key.KeyString, p.Value)).OrderBy(p => p.KeyString, Utf8OrdinalComparer.Instance);
    }

    /// <summary>resolveXxxRef: a null is the error; a $ref is followed (an error of the same kind in what it points to
    /// is dropped, as kin does); anything else has its own parts checked.</summary>
    private Must? Check(Document doc, Node n, Must kind)
    {
        if (n.IsNull) return kind;
        if (!n.IsMapping) return null; // not an object at all: kin fails earlier, when it unmarshals
        if (RefOf(n) is { } reference)
        {
            if (!_resolving.Add(reference)) return null;
            try
            {
                var (tdoc, tnode) = ResolveRef(doc, reference);
                if (CheckDocument(tdoc) is { } dm) return dm; // a newly loaded document is resolved first
                if (!reference.Contains('#')) return tnode.IsMapping ? CheckParts(tdoc, tnode, kind) : null; // a whole file
                var m = Check(tdoc, tnode, kind);
                return m == kind ? null : m;
            }
            finally { _resolving.Remove(reference); }
        }
        return CheckParts(doc, n, kind);
    }

    private Must? CheckParts(Document doc, Node n, Must kind)
    {
        if (_checked.Contains((n, kind))) return null;
        var m = kind switch
        {
            Must.Schema => CheckSchema(doc, n),
            Must.Header => CheckOne(doc, n["schema"], Must.Schema) ?? CheckEach(doc, n["examples"], Must.Example),
            Must.Parameter => CheckContent(doc, n["content"]) ?? CheckOne(doc, n["schema"], Must.Schema) ?? CheckEach(doc, n["examples"], Must.Example),
            Must.RequestBody => CheckContent(doc, n["content"]),
            Must.Response => CheckEach(doc, n["headers"], Must.Header) ?? CheckContent(doc, n["content"]) ?? CheckEach(doc, n["links"], Must.Link),
            Must.Callback => CheckCallback(doc, n),
            _ => null, // security schemes, examples and links have nothing inside to resolve
        };
        if (m is null) _checked.Add((n, kind));
        return m;
    }

    /// <summary>An optional single one: skipped when null.</summary>
    private Must? CheckOne(Document doc, Node? n, Must kind) => n is { IsMapping: true } ? Check(doc, n, kind) : null;

    /// <summary>Each entry of a map, by name: a null entry is an error.</summary>
    private Must? CheckEach(Document doc, Node? map, Must kind)
    {
        foreach (var (_, v) in Sorted(map))
            if (Check(doc, v, kind) is { } m) return m;
        return null;
    }

    private Must? CheckList(Document doc, Node? list, Must kind)
    {
        if (list is not { IsSequence: true }) return null;
        foreach (var it in list.Items!)
            if (Check(doc, it, kind) is { } m) return m;
        return null;
    }

    // resolveMediaTypeRefs: a null media type is skipped
    private Must? CheckContent(Document doc, Node? content)
    {
        foreach (var (_, mt) in Sorted(content))
        {
            if (!mt.IsMapping) continue;
            if ((CheckOne(doc, mt["schema"], Must.Schema) ?? CheckOne(doc, mt["itemSchema"], Must.Schema) ?? CheckEach(doc, mt["examples"], Must.Example)) is { } m) return m;
        }
        return null;
    }

    // resolveSchemaRef, in its order
    private Must? CheckSchema(Document doc, Node n) =>
        CheckOne(doc, n["items"], Must.Schema)
        ?? CheckEach(doc, n["properties"], Must.Schema)
        ?? CheckOne(doc, n["additionalProperties"], Must.Schema)
        ?? CheckOne(doc, n["not"], Must.Schema)
        ?? CheckList(doc, n["allOf"], Must.Schema)
        ?? CheckList(doc, n["anyOf"], Must.Schema)
        ?? CheckList(doc, n["oneOf"], Must.Schema)
        ?? CheckList(doc, n["prefixItems"], Must.Schema)
        ?? CheckOne(doc, n["contains"], Must.Schema)
        ?? CheckEach(doc, n["patternProperties"], Must.Schema)
        ?? CheckEach(doc, n["dependentSchemas"], Must.Schema)
        ?? CheckEach(doc, n["$defs"], Must.Schema)
        ?? CheckOne(doc, n["propertyNames"], Must.Schema)
        ?? CheckOne(doc, n["unevaluatedItems"], Must.Schema)
        ?? CheckOne(doc, n["unevaluatedProperties"], Must.Schema)
        ?? CheckOne(doc, n["if"], Must.Schema)
        ?? CheckOne(doc, n["then"], Must.Schema)
        ?? CheckOne(doc, n["else"], Must.Schema)
        ?? CheckOne(doc, n["contentSchema"], Must.Schema);

    // resolveCallbackRef: every path item of the callback, a null one included
    private Must? CheckCallback(Document doc, Node n)
    {
        foreach (var (k, v) in Sorted(n))
        {
            if (k.StartsWith("x-", StringComparison.Ordinal)) continue;
            if (v.IsNull) return Must.PathItem;
            if (v.IsMapping && CheckPathItem(doc, v) is { } m) return m;
        }
        return null;
    }

    private static readonly string[] FixedMethods = ["connect", "delete", "get", "head", "options", "patch", "post", "put", "trace", "query"];

    // resolvePathItemRef
    private Must? CheckPathItem(Document doc, Node n)
    {
        if (RefOf(n) is { } reference)
        {
            // a $ref next to anything else is left alone (PathItem.isEmpty is false)
            if (!PathItemIsEmpty(n) || !_resolving.Add(reference)) return null;
            try
            {
                var (tdoc, tnode) = ResolveRef(doc, reference);
                if (CheckDocument(tdoc) is { } dm) return dm == Must.PathItem ? null : dm;
                // what it points to is read as a path item; a $ref in there is not followed again
                return tnode.IsMapping ? CheckPathItemParts(tdoc, tnode) : null;
            }
            finally { _resolving.Remove(reference); }
        }
        return CheckPathItemParts(doc, n);
    }

    private static bool PathItemIsEmpty(Node n) =>
        Str(n["summary"]).Length == 0 && Str(n["description"]).Length == 0 &&
        FixedMethods.All(m => n[m] is not { IsMapping: true }) &&
        n["additionalOperations"] is not { IsMapping: true, Pairs.Count: > 0 } &&
        n["servers"] is not { IsSequence: true, Items.Count: > 0 } &&
        n["parameters"] is not { IsSequence: true, Items.Count: > 0 };

    private Must? CheckPathItemParts(Document doc, Node n)
    {
        if (_checked.Contains((n, Must.PathItem))) return null;
        if (CheckList(doc, n["parameters"], Must.Parameter) is { } pm) return pm;
        // PathItem.Operations(): the fixed methods (upper case) and additionalOperations, sorted by method
        var ops = new List<(string Method, Node Op)>();
        foreach (var m in FixedMethods)
            if (n[m] is { IsMapping: true } op) ops.Add((m.ToUpperInvariant(), op));
        if (n["additionalOperations"] is { IsMapping: true } add)
            foreach (var (k, v) in add.Pairs!)
                if (v.IsMapping && !ops.Any(o => o.Method == k.KeyString)) ops.Add((k.KeyString, v));
        foreach (var (_, op) in ops.OrderBy(o => o.Method, Utf8OrdinalComparer.Instance))
        {
            if ((CheckList(doc, op["parameters"], Must.Parameter)
                ?? CheckOne(doc, op["requestBody"], Must.RequestBody)
                ?? CheckEachExceptExtensions(doc, op["responses"], Must.Response)
                ?? CheckEach(doc, op["callbacks"], Must.Callback)) is { } m) return m;
        }
        _checked.Add((n, Must.PathItem));
        return null;
    }

    private Must? CheckEachExceptExtensions(Document doc, Node? map, Must kind)
    {
        foreach (var (k, v) in Sorted(map))
            if (!k.StartsWith("x-", StringComparison.Ordinal) && Check(doc, v, kind) is { } m) return m;
        return null;
    }
}
