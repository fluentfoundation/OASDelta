using System.Globalization;
using OasDelta.Checker;
using OasDelta.Yaml;

namespace OasDelta.Load;

/// <summary>
/// kin-openapi's resolveComponent (loader.go, v0.151.0), which oasdiff uses to follow every <c>$ref</c> with a fragment.
/// The port keeps what kin means to do and leaves out its bugs:
/// <list type="bullet">
/// <item>Kept: it first drills into the typed document (T, Components, Schema, ...). A ref wrapper is drilled through
/// its resolved value, so <c>#/components/schemas/R/properties/x</c> works when R is itself a $ref. What it reaches
/// must be of the kind the $ref stands for, or a map from an extension; anything else is "bad data in ... (expecting
/// ref to parameter object)". <c>#</c> is the document's unknown top-level keys (kin's issue 856: a file that is
/// itself a schema). When the typed drill fails, the pointer is followed through the plain data, which reaches
/// extensions and fields the typed document does not have.</item>
/// <item>Not kept: <c>#</c> on an OpenAPI document (one with an openapi key) gives kin an object made of its x- keys,
/// so a schema $ref to it is silently "any". The port reports bad data.</item>
/// <item>Not kept: kin follows that second pointer through the document that holds the $ref (path), not the one the
/// $ref points to (componentPath). With <c>./defs.yaml#/components/schemas/Pet</c> and no Pet in defs.yaml, oasdiff
/// silently uses the referencing file's own Pet. The port follows it through the target.</item>
/// <item>Not kept: kin's drill closure assigns the enclosing function's err, so the error that comes out is the
/// second drill's bare one ("map key \"Z\" not found", naming neither the $ref nor the file), and a pointer that
/// passes through a null comes out as no error at all. The port reports the error kin builds before losing it
/// (<c>failed to resolve "Z" in fragment in URI: "#/x-defs/Z": map key "Z" not found</c>), also for a pointer
/// through a null.</item>
/// <item>Not kept: a pointer to a typed field that is absent (<c>#/components/schemas/A/not</c>) makes kin panic
/// (it copies a nil pointer); the port reports it as not found.</item>
/// </list>
/// A $ref to a null (present in the document) resolves to nothing, as in kin; oasdiff then reports what it does for
/// such a value (for a schema, "schema value is nil").
/// </summary>
public sealed partial class SpecLoader
{
    private static readonly Node NullNode = new() { Kind = NodeKind.Scalar, Text = "null", Plain = true };

    // what each $ref resolved to, by the document holding it and the $ref as written; NullNode: resolved to nothing
    private readonly Dictionary<(Document, string), (Document Doc, Node Node)> _kinResolved = new(DocRefComparer.Instance);
    private readonly HashSet<(Document, string, Must)> _kinKinds = [];

    private sealed class DocRefComparer : IEqualityComparer<(Document, string)>
    {
        public static readonly DocRefComparer Instance = new();
        public bool Equals((Document, string) a, (Document, string) b) => ReferenceEquals(a.Item1, b.Item1) && string.Equals(a.Item2, b.Item2, StringComparison.Ordinal);
        public int GetHashCode((Document, string) k) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.Item1), StringComparer.Ordinal.GetHashCode(k.Item2));
    }

    private static string Readable(Must kind) => kind switch
    {
        Must.PathItem => "pathItem object",
        Must.Callback => "ref to callback object",
        Must.Example => "ref to example object",
        Must.Header => "ref to header object",
        Must.Link => "ref to link object",
        Must.Parameter => "ref to parameter object",
        Must.RequestBody => "ref to requestBody object",
        Must.Response => "ref to response object",
        Must.SecurityScheme => "ref to securityScheme object",
        _ => "ref to schema object",
    };

    /// <summary>Resolves a $ref that has a fragment as kin does for one standing for <paramref name="kind"/>. Returns
    /// <see cref="NullNode"/> when kin resolves it to nothing; throws kin's error.</summary>
    private (Document Doc, Node Node) KinResolve(Document from, string reference, Must kind)
    {
        // the same $ref can stand for another kind elsewhere (a parameter $ref to a schema), which is checked again
        if (_kinResolved.TryGetValue((from, reference), out var done) && _kinKinds.Contains((from, reference, kind))) return done;
        var target = from;
        var fragmentRef = reference;
        if (!reference.StartsWith('#'))
        {
            // resolveRefAndDocument: the other document is loaded and resolved first, and its errors are wrapped
            int hash = reference.IndexOf('#');
            CheckRefPolicy(from, reference, reference[..hash]);
            try
            {
                target = LoadExternal(from, reference[..hash]);
                if (!_checkedDocs.Contains(target) && CheckDocument(target) is { } m) throw new LoadException(Message(m));
            }
            catch (LoadException e) { throw new LoadException($"error resolving reference {Fmt.GoQuote(reference)}: {e.Message}"); }
            fragmentRef = "#" + Uri.UnescapeDataString(reference[(hash + 1)..]);
        }
        var fragment = Uri.UnescapeDataString(fragmentRef[1..]);
        if (fragment.Length == 0) fragment = "/";
        if (fragment[0] != '/') throw new LoadException($"expected fragment prefix '#/' in URI {Fmt.GoQuote(fragmentRef)}");
        var tokens = fragment[1..].Split('/').Select(t => t.Replace("~1", "/").Replace("~0", "~")).ToArray();
        // "#" and "#/": kin reads the target as T and takes its unknown top-level keys, for a file that is itself the
        // object (its issue 856: a file that is a schema). The root of an OpenAPI document is no such object: kin
        // makes one of its x- keys (an "any" schema) or of nothing. The port rejects it.
        if (tokens is [""] && target.Root is { IsMapping: true } root && root["openapi"] is not null)
            throw new LoadException($"bad data in {Fmt.GoQuote(fragmentRef)} (expecting {Readable(kind)})");

        (Document, Node) result;
        if (TypedDrill(target, tokens) is { } t)
        {
            if (t.Kind == KindOf(kind))
            {
                // a typed nil: kin panics copying it. Absent: not found; a null: resolves to nothing
                if (t.Node is null) throw new LoadException(FailedToResolve(reference, tokens[^1], null));
                result = (t.Doc, t.Node.IsNull ? NullNode : t.Node);
            }
            else if (t.Kind == KT.Generic && t.Node!.IsMapping) result = (t.Doc, t.Node);
            else if (t.Kind == KT.Generic && ReferenceEquals(t.Node, NullNode)) result = (t.Doc, NullNode); // nil map: decodes to nothing
            else throw new LoadException($"bad data in {Fmt.GoQuote(fragmentRef)} (expecting {Readable(kind)})");
        }
        else
        {
            // kin reads the referencing document (path) here; the pointer is the target's
            var node = RawDrill(target.Root, tokens, reference);
            if (node is null) result = (target, NullNode);
            else if (node.IsMapping) result = (target, node);
            else throw new LoadException($"bad data in {Fmt.GoQuote(fragmentRef)} (expecting {Readable(kind)})");
        }
        _kinResolved[(from, reference)] = result;
        _kinKinds.Add((from, reference, kind));
        return result;
    }

    /// <summary>kin's failedToResolveRefFragmentPart, with drillIntoField's error; the reference as written, so that
    /// the file is named too.</summary>
    private static string FailedToResolve(string reference, string token, string? error) =>
        $"failed to resolve {Fmt.GoQuote(token)} in fragment in URI: {Fmt.GoQuote(reference)}" + (error is null ? "" : ": " + error);

    /// <summary>drillIntoField over the plain data of a document. Null: the pointer ends on a null.</summary>
    private static Node? RawDrill(Node? root, string[] tokens, string reference)
    {
        var cur = root;
        string? previous = null;
        foreach (var token in tokens)
        {
            string? error = null;
            if (cur is null || cur.IsNull) error = previous is null ? "the document is null" : $"{Fmt.GoQuote(previous)} is null";
            else if (cur.IsMapping) { if (cur[token] is { } next) cur = next; else error = $"map key {Fmt.GoQuote(token)} not found"; }
            else if (cur.IsSequence) { if (SliceIndex(token, cur.Items!.Count, out error) is { } i) cur = cur.Items[i]; }
            else error = "not a map, slice nor struct";
            if (error is not null) throw new LoadException(FailedToResolve(reference, token, error));
            previous = token;
        }
        return cur is null || cur.IsNull ? null : cur;
    }

    /// <summary>drillIntoField's slice index, or null and its error.</summary>
    private static int? SliceIndex(string token, int count, out string? error)
    {
        error = null;
        int index = 0;
        if (token.StartsWith('+') || token.StartsWith('-')) error = $"invalid slice index {Fmt.GoQuote(token)}";
        else if (token.Length == 0 || !token.All(char.IsAsciiDigit)) error = $"strconv.Atoi: parsing {Fmt.GoQuote(token)}: invalid syntax";
        else if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out index)) error = $"strconv.Atoi: parsing {Fmt.GoQuote(token)}: value out of range";
        else if (index >= count) error = "slice index out of bounds";
        return error is null ? index : null;
    }

    // ------------------------------------------------------------------ the typed document

    /// <summary>kin's types, as far as a drill tells them apart. Generic: an `any` value (extensions, examples,
    /// defaults); Other: a string, bool, number or a struct nothing points into.</summary>
    private enum KT
    {
        Doc, Components, Info, Contact, License, ExternalDocs, Tag, Server, ServerVariable, Paths, PathItem, Operation,
        Parameter, RequestBody, Responses, Response, MediaType, Encoding, Schema, Header, Example, Link, Callback,
        SecurityScheme, OAuthFlows, OAuthFlow, Discriminator, XML,
        SchemaRef, ParameterRef, HeaderRef, RequestBodyRef, ResponseRef, SecuritySchemeRef, ExampleRef, LinkRef, CallbackRef,
        Schemas, ParametersMap, Headers, RequestBodies, ResponseBodies, SecuritySchemes, Examples, Links, Callbacks,
        Content, Encodings, ServerVariables, PathItemMap, OperationMap,
        SchemaRefs, Parameters, Servers, Tags,
        Generic, Other,
    }

    private static KT KindOf(Must kind) => kind switch
    {
        Must.Header => KT.HeaderRef,
        Must.Parameter => KT.ParameterRef,
        Must.RequestBody => KT.RequestBodyRef,
        Must.Response => KT.ResponseRef,
        Must.SecurityScheme => KT.SecuritySchemeRef,
        Must.Example => KT.ExampleRef,
        Must.Callback => KT.CallbackRef,
        Must.Link => KT.LinkRef,
        Must.PathItem => KT.PathItem,
        _ => KT.SchemaRef,
    };

    private static Dictionary<string, KT> Fields(params (string, KT)[] fields) => fields.ToDictionary(f => f.Item1, f => f.Item2, StringComparer.Ordinal);

    private static Dictionary<string, KT> Plain(params string[] names) => names.ToDictionary(n => n, _ => KT.Other, StringComparer.Ordinal);

    // the yaml tags of each struct's fields; any other key is looked up in its Extensions, which hold every key the
    // struct does not know (UnmarshalJSON deletes only its own fields)
    private static readonly Dictionary<KT, Dictionary<string, KT>> StructFields = new()
    {
        [KT.Doc] = Fields(("openapi", KT.Other), ("components", KT.Components), ("info", KT.Info), ("paths", KT.Paths),
            ("security", KT.Other), ("servers", KT.Servers), ("tags", KT.Tags), ("externalDocs", KT.ExternalDocs),
            ("webhooks", KT.PathItemMap), ("jsonSchemaDialect", KT.Other)),
        [KT.Components] = Fields(("schemas", KT.Schemas), ("parameters", KT.ParametersMap), ("headers", KT.Headers),
            ("requestBodies", KT.RequestBodies), ("responses", KT.ResponseBodies), ("securitySchemes", KT.SecuritySchemes),
            ("examples", KT.Examples), ("links", KT.Links), ("callbacks", KT.Callbacks)),
        [KT.Info] = new(Plain("title", "summary", "description", "termsOfService", "version"), StringComparer.Ordinal)
        { ["contact"] = KT.Contact, ["license"] = KT.License },
        [KT.Contact] = Plain("name", "url", "email"),
        [KT.License] = Plain("name", "url", "identifier"),
        [KT.ExternalDocs] = Plain("description", "url"),
        [KT.Tag] = new(Plain("name", "description"), StringComparer.Ordinal) { ["externalDocs"] = KT.ExternalDocs },
        [KT.Server] = new(Plain("url", "description"), StringComparer.Ordinal) { ["variables"] = KT.ServerVariables },
        [KT.ServerVariable] = Plain("enum", "default", "description"),
        [KT.PathItem] = Fields(("$ref", KT.Other), ("summary", KT.Other), ("description", KT.Other),
            ("connect", KT.Operation), ("delete", KT.Operation), ("get", KT.Operation), ("head", KT.Operation),
            ("options", KT.Operation), ("patch", KT.Operation), ("post", KT.Operation), ("put", KT.Operation),
            ("trace", KT.Operation), ("query", KT.Operation), ("servers", KT.Servers), ("parameters", KT.Parameters),
            ("additionalOperations", KT.OperationMap)),
        [KT.Operation] = Fields(("tags", KT.Other), ("summary", KT.Other), ("description", KT.Other), ("operationId", KT.Other),
            ("parameters", KT.Parameters), ("requestBody", KT.RequestBodyRef), ("responses", KT.Responses),
            ("callbacks", KT.Callbacks), ("deprecated", KT.Other), ("security", KT.Other), ("servers", KT.Servers),
            ("externalDocs", KT.ExternalDocs)),
        [KT.Parameter] = new(Plain("name", "in", "description", "style", "explode", "allowEmptyValue", "allowReserved", "deprecated", "required"), StringComparer.Ordinal)
        { ["schema"] = KT.SchemaRef, ["example"] = KT.Generic, ["examples"] = KT.Examples, ["content"] = KT.Content },
        [KT.RequestBody] = new(Plain("description", "required"), StringComparer.Ordinal) { ["content"] = KT.Content },
        [KT.Response] = Fields(("description", KT.Other), ("headers", KT.Headers), ("content", KT.Content), ("links", KT.Links)),
        [KT.MediaType] = Fields(("schema", KT.SchemaRef), ("itemSchema", KT.SchemaRef), ("example", KT.Generic),
            ("examples", KT.Examples), ("encoding", KT.Encodings)),
        [KT.Encoding] = new(Plain("contentType", "style", "explode", "allowReserved"), StringComparer.Ordinal) { ["headers"] = KT.Headers },
        [KT.Example] = new(Plain("summary", "description", "externalValue"), StringComparer.Ordinal) { ["value"] = KT.Generic },
        [KT.Link] = new(Plain("operationRef", "operationId", "description"), StringComparer.Ordinal)
        { ["parameters"] = KT.Generic, ["server"] = KT.Server, ["requestBody"] = KT.Generic },
        [KT.SecurityScheme] = new(Plain("type", "description", "name", "in", "scheme", "bearerFormat", "openIdConnectUrl"), StringComparer.Ordinal)
        { ["flows"] = KT.OAuthFlows },
        [KT.OAuthFlows] = Fields(("implicit", KT.OAuthFlow), ("password", KT.OAuthFlow), ("clientCredentials", KT.OAuthFlow), ("authorizationCode", KT.OAuthFlow)),
        [KT.OAuthFlow] = Plain("authorizationUrl", "tokenUrl", "refreshUrl", "scopes"),
        [KT.Discriminator] = Plain("propertyName", "mapping"),
        [KT.XML] = Plain("name", "namespace", "prefix", "attribute", "wrapped"),
        [KT.Schema] = new(Plain("type", "title", "format", "description", "uniqueItems", "exclusiveMinimum", "exclusiveMaximum",
            "nullable", "readOnly", "writeOnly", "allowEmptyValue", "deprecated", "minimum", "maximum", "multipleOf", "minLength",
            "maxLength", "pattern", "minItems", "maxItems", "required", "minProperties", "maxProperties", "additionalProperties",
            "minContains", "maxContains", "unevaluatedItems", "unevaluatedProperties", "dependentRequired", "$schema", "$comment",
            "$id", "$anchor", "$dynamicRef", "$dynamicAnchor", "contentMediaType", "contentEncoding"), StringComparer.Ordinal)
        {
            ["oneOf"] = KT.SchemaRefs, ["anyOf"] = KT.SchemaRefs, ["allOf"] = KT.SchemaRefs, ["prefixItems"] = KT.SchemaRefs,
            ["not"] = KT.SchemaRef, ["items"] = KT.SchemaRef, ["contains"] = KT.SchemaRef, ["propertyNames"] = KT.SchemaRef,
            ["if"] = KT.SchemaRef, ["then"] = KT.SchemaRef, ["else"] = KT.SchemaRef, ["contentSchema"] = KT.SchemaRef,
            ["properties"] = KT.Schemas, ["patternProperties"] = KT.Schemas, ["dependentSchemas"] = KT.Schemas, ["$defs"] = KT.Schemas,
            ["enum"] = KT.Generic, ["default"] = KT.Generic, ["example"] = KT.Generic, ["const"] = KT.Generic, ["examples"] = KT.Generic,
            ["externalDocs"] = KT.ExternalDocs, ["xml"] = KT.XML, ["discriminator"] = KT.Discriminator,
        },
    };

    private static readonly Dictionary<KT, KT> RefValue = new()
    {
        [KT.SchemaRef] = KT.Schema, [KT.ParameterRef] = KT.Parameter, [KT.HeaderRef] = KT.Header, [KT.RequestBodyRef] = KT.RequestBody,
        [KT.ResponseRef] = KT.Response, [KT.SecuritySchemeRef] = KT.SecurityScheme, [KT.ExampleRef] = KT.Example,
        [KT.LinkRef] = KT.Link, [KT.CallbackRef] = KT.Callback,
    };

    private static readonly Dictionary<KT, KT> MapValue = new()
    {
        [KT.Schemas] = KT.SchemaRef, [KT.ParametersMap] = KT.ParameterRef, [KT.Headers] = KT.HeaderRef,
        [KT.RequestBodies] = KT.RequestBodyRef, [KT.ResponseBodies] = KT.ResponseRef, [KT.SecuritySchemes] = KT.SecuritySchemeRef,
        [KT.Examples] = KT.ExampleRef, [KT.Links] = KT.LinkRef, [KT.Callbacks] = KT.CallbackRef, [KT.Content] = KT.MediaType,
        [KT.Encodings] = KT.Encoding, [KT.ServerVariables] = KT.ServerVariable, [KT.PathItemMap] = KT.PathItem,
        [KT.OperationMap] = KT.Operation,
        // Paths, Responses and Callback keep their extensions apart from the map the drill looks in
        [KT.Paths] = KT.PathItem, [KT.Responses] = KT.ResponseRef, [KT.Callback] = KT.PathItem,
    };

    private static readonly Dictionary<KT, KT> SliceValue = new()
    {
        [KT.SchemaRefs] = KT.SchemaRef, [KT.Parameters] = KT.ParameterRef, [KT.Servers] = KT.Server, [KT.Tags] = KT.Tag,
    };

    /// <summary>A cursor of the typed drill: a kind, the node it was read from (null: absent, a nil pointer; a null
    /// node: null in the document, also a nil pointer) and the document that node is in.</summary>
    private readonly record struct Typed(KT Kind, Node? Node, Document Doc);

    /// <summary>The drill over the typed document. Null: it fails, and kin falls back to the plain data.</summary>
    private Typed? TypedDrill(Document doc, string[] tokens)
    {
        Typed? cur = new Typed(KT.Doc, doc.Root, doc);
        foreach (var token in tokens)
        {
            cur = Step(cur.Value, token);
            if (cur is null) return null;
        }
        return cur;
    }

    private Typed? Step(Typed cur, string token)
    {
        var (kind, node, doc) = cur;
        if (kind == KT.Generic)
        {
            // plain data; a null on the way is the drill's "nil cursor" failure
            Node? next = null;
            if (node!.IsMapping) next = node[token];
            else if (node.IsSequence && SliceIndex(token, node.Items!.Count, out _) is { } i) next = node.Items![i];
            return next is null || next.IsNull ? null : new Typed(KT.Generic, next, doc);
        }
        if (node is null || node.IsNull) return null; // drilling into a nil pointer, map or slice
        if (kind == KT.Doc && token.Length == 0)
        {
            // "#" and "#/": T.Extensions as a whole, every top-level key T does not know (nil when there are none)
            var known = StructFields[KT.Doc];
            var pairs = node.Pairs?.Where(p => !known.ContainsKey(p.Key.KeyString)).ToList() ?? [];
            return pairs.Count == 0 ? new Typed(KT.Generic, NullNode, doc) : new Typed(KT.Generic, new Node { Kind = NodeKind.Mapping, Pairs = pairs }, doc);
        }

        if (kind == KT.SchemaRef && token == "additionalProperties")
        {
            // the special case for multijson: a boolean or the schema; with no value the cursor stays where it is
            if (Value(cur) is not { } value) return cur;
            var ap = value.Node!["additionalProperties"];
            if (ap is { IsScalar: true } && !ap.IsNull && ap.ToValue() is bool) return new Typed(KT.Other, ap, value.Doc);
            return new Typed(KT.SchemaRef, ap, value.Doc);
        }
        if (RefValue.ContainsKey(kind))
        {
            if (kind != KT.SchemaRef && token is "summary" or "description") return new Typed(KT.Other, node[token], doc);
            return Value(cur) is { } v ? Step(v, token) : null;
        }
        if (MapValue.TryGetValue(kind, out var valueKind))
        {
            if (!node.IsMapping) return null;
            if (kind is KT.Paths or KT.Responses or KT.Callback && token.StartsWith("x-", StringComparison.Ordinal)) return null;
            var next = node[token];
            return next is null ? null : new Typed(valueKind, next, doc);
        }
        if (SliceValue.TryGetValue(kind, out var itemKind))
        {
            if (!node.IsSequence || SliceIndex(token, node.Items!.Count, out _) is not { } i) return null;
            var item = node.Items![i];
            return new Typed(itemKind, item, doc);
        }
        if (StructFields.TryGetValue(kind, out var fields) && node.IsMapping)
        {
            if (fields.TryGetValue(token, out var fieldKind))
            {
                var field = node[token];
                if (fieldKind == KT.Generic) return field is null || field.IsNull ? null : new Typed(KT.Generic, field, doc);
                return new Typed(fieldKind, field, doc);
            }
            var ext = node[token];
            return ext is null || ext.IsNull ? null : new Typed(KT.Generic, ext, doc);
        }
        return null; // Header (an embedded Parameter, no Extensions first), a scalar, anything else
    }

    /// <summary>A ref wrapper's Value: what its $ref resolves to, or the object written in its place.</summary>
    private Typed? Value(Typed cur)
    {
        var valueKind = RefValue[cur.Kind];
        var (doc, node) = (cur.Doc, cur.Node!);
        for (int depth = 0; RefOf(node) is { } reference; depth++)
        {
            if (depth > 32) return null;
            try { (doc, node) = ResolveRef(doc, reference); }
            catch (LoadException) { return null; }
            if (node.IsNull) return null;
        }
        return node.IsMapping ? new Typed(valueKind, node, doc) : null;
    }
}
