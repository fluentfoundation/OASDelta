using OasDelta.Yaml;

namespace OasDelta.Model;

public sealed class Location
{
    public string File = "";
    public int Line, Column;
    public string Name = "";
    public int EndLine, EndColumn;
    public Location Clone() => (Location)MemberwiseClone();
}

/// <summary>Source position of an element (kin-openapi Origin).</summary>
public sealed class Origin
{
    public Location? Key;
    public List<Location>? Fields;
    public Dictionary<string, List<Location>>? Sequences;

    public Location? Field(string name)
    {
        if (Fields is null) return null;
        foreach (var f in Fields) if (f.Name == name) return f;
        return null;
    }
}

public abstract class Element
{
    public Dictionary<string, object?>? Extensions;
    public Origin? Origin;

    public object? Ext(string name) => Extensions is not null && Extensions.TryGetValue(name, out var v) ? v : null;
    public bool HasExt(string name) => Extensions is not null && Extensions.ContainsKey(name);
}

/// <summary>A reference-or-value holder (kin-openapi XxxRef).</summary>
public sealed class Ref<T> where T : class
{
    public string RefPath = "";
    public T? Value;
    public Dictionary<string, object?>? Extensions;
    public Origin? Origin;
    public Ref() { }
    public Ref(T value) { Value = value; }
    public bool IsRef => RefPath.Length > 0;
}

public sealed class OpenApiDoc : Element
{
    public string OpenAPI = "";
    public Components? Components;
    public Info? Info;
    public Paths Paths = new();
    public List<SecurityRequirement> Security = [];
    public bool HasSecurity;
    public List<Server> Servers = [];
    public List<Tag> Tags = [];
    public ExternalDocs? ExternalDocs;
    public Dictionary<string, PathItem>? Webhooks;
    public string JSONSchemaDialect = "";

    /// <summary>kin-openapi OpenAPIMajorMinor: only the exact versions it knows.</summary>
    public static string MajorMinor(string v) => v switch
    {
        "3" or "3.0" or "3.0.0" or "3.0.1" or "3.0.2" or "3.0.3" or "3.0.4" => "3.0",
        "3.1" or "3.1.0" or "3.1.1" or "3.1.2" => "3.1",
        "3.2" or "3.2.0" or "3.2.1" => "3.2",
        _ => "",
    };
    public static bool Is31OrLater(string v) => MajorMinor(v) is "3.1" or "3.2";
    public bool IsOpenAPI31OrLater() => Is31OrLater(OpenAPI);
}

public sealed class Components : Element
{
    public Dictionary<string, Ref<Schema>> Schemas = new(StringComparer.Ordinal);
    public Dictionary<string, Ref<Parameter>> Parameters = new(StringComparer.Ordinal);
    public Dictionary<string, Ref<Header>> Headers = new(StringComparer.Ordinal);
    public Dictionary<string, Ref<RequestBody>> RequestBodies = new(StringComparer.Ordinal);
    public Dictionary<string, Ref<Response>> Responses = new(StringComparer.Ordinal);
    public Dictionary<string, Ref<SecurityScheme>> SecuritySchemes = new(StringComparer.Ordinal);
    public Dictionary<string, Ref<Example>> Examples = new(StringComparer.Ordinal);
    public Dictionary<string, Ref<Link>> Links = new(StringComparer.Ordinal);
    public Dictionary<string, Ref<Callback>> Callbacks = new(StringComparer.Ordinal);
}

public sealed class Info : Element
{
    public string Title = "", Summary = "", Description = "", TermsOfService = "", Version = "";
    public Contact? Contact;
    public License? License;
}

public sealed class Contact : Element { public string Name = "", URL = "", Email = ""; }
public sealed class License : Element { public string Name = "", URL = "", Identifier = ""; }

public sealed class Server : Element
{
    public string URL = "", Description = "";
    public Dictionary<string, ServerVariable> Variables = new(StringComparer.Ordinal);
}

public sealed class ServerVariable : Element
{
    public List<string> Enum = [];
    public string Default = "", Description = "";
}

public sealed class Tag : Element
{
    public string Name = "", Description = "";
    public ExternalDocs? ExternalDocs;
}

public sealed class ExternalDocs : Element { public string Description = "", URL = ""; }

public sealed class Paths : Element
{
    public Dictionary<string, PathItem> Map = new(StringComparer.Ordinal);
    public PathItem? Value(string path) => Map.TryGetValue(path, out var p) ? p : null;
    public void Set(string path, PathItem item) => Map[path] = item;
    public void Delete(string path) => Map.Remove(path);
    public Paths ShallowClone() { var p = new Paths { Extensions = Extensions, Origin = Origin }; foreach (var (k, v) in Map) p.Map[k] = v; return p; }
}

public sealed class PathItem : Element
{
    public static readonly string[] FixedMethods = ["CONNECT", "DELETE", "GET", "HEAD", "OPTIONS", "PATCH", "POST", "PUT", "QUERY", "TRACE"];

    public string Ref = "", Summary = "", Description = "";
    public Operation? Connect, Delete, Get, Head, Options, Patch, Post, Put, Trace, Query;
    public List<Server> Servers = [];
    public List<Ref<Parameter>> Parameters = [];
    public Dictionary<string, Operation>? AdditionalOperations;

    public Operation? GetOperation(string method) => method switch
    {
        "CONNECT" => Connect,
        "DELETE" => Delete,
        "GET" => Get,
        "HEAD" => Head,
        "OPTIONS" => Options,
        "PATCH" => Patch,
        "POST" => Post,
        "PUT" => Put,
        "TRACE" => Trace,
        "QUERY" => Query,
        _ => AdditionalOperations is not null && AdditionalOperations.TryGetValue(method, out var op) ? op : null,
    };

    public void SetOperation(string method, Operation? op)
    {
        switch (method)
        {
            case "CONNECT": Connect = op; break;
            case "DELETE": Delete = op; break;
            case "GET": Get = op; break;
            case "HEAD": Head = op; break;
            case "OPTIONS": Options = op; break;
            case "PATCH": Patch = op; break;
            case "POST": Post = op; break;
            case "PUT": Put = op; break;
            case "TRACE": Trace = op; break;
            case "QUERY": Query = op; break;
            default:
                if (op is null) AdditionalOperations?.Remove(method);
                else (AdditionalOperations ??= new(StringComparer.Ordinal))[method] = op;
                break;
        }
    }

    /// <summary>Operations keyed by uppercase method.</summary>
    public Dictionary<string, Operation> Operations()
    {
        var r = new Dictionary<string, Operation>(StringComparer.Ordinal);
        foreach (var m in FixedMethods) { var op = GetOperation(m); if (op is not null) r[m] = op; }
        if (AdditionalOperations is not null)
            foreach (var (m, op) in AdditionalOperations) if (op is not null && !r.ContainsKey(m)) r[m] = op;
        return r;
    }

    public PathItem ShallowClone() => (PathItem)MemberwiseClone();
}

public sealed class Operation : Element
{
    public List<string> Tags = [];
    public string Summary = "", Description = "", OperationID = "";
    public List<Ref<Parameter>> Parameters = [];
    public Ref<RequestBody>? RequestBody;
    public Responses? Responses;
    public Dictionary<string, Ref<Callback>> Callbacks = new(StringComparer.Ordinal);
    public bool Deprecated;
    public List<SecurityRequirement>? Security;
    public List<Server>? Servers;
    public ExternalDocs? ExternalDocs;
}

public class Parameter : Element
{
    public string Name = "", In = "", Description = "", Style = "";
    public bool? Explode;
    public bool AllowEmptyValue, AllowReserved, Deprecated, Required;
    public Ref<Schema>? Schema;
    public object? Example;
    public Dictionary<string, Ref<Example>> Examples = new(StringComparer.Ordinal);
    public Dictionary<string, MediaType> Content = new(StringComparer.Ordinal);

    public const string InPath = "path", InQuery = "query", InHeader = "header", InCookie = "cookie", InQueryString = "querystring";
}

public sealed class Header : Parameter { }

public sealed class RequestBody : Element
{
    public string Description = "";
    public bool Required;
    public Dictionary<string, MediaType> Content = new(StringComparer.Ordinal);
}

public sealed class Responses : Element
{
    public Dictionary<string, Ref<Response>> Map = new(StringComparer.Ordinal);
    public Ref<Response>? Value(string key) => Map.TryGetValue(key, out var r) ? r : null;
}

public sealed class Response : Element
{
    public string? Description;
    public Dictionary<string, Ref<Header>> Headers = new(StringComparer.Ordinal);
    public Dictionary<string, MediaType> Content = new(StringComparer.Ordinal);
    public Dictionary<string, Ref<Link>> Links = new(StringComparer.Ordinal);
}

public sealed class MediaType : Element
{
    public Ref<Schema>? Schema;
    public Ref<Schema>? ItemSchema;
    public object? Example;
    public Dictionary<string, Ref<Example>> Examples = new(StringComparer.Ordinal);
    public Dictionary<string, Encoding> Encoding = new(StringComparer.Ordinal);
}

public sealed class Encoding : Element
{
    public string ContentType = "", Style = "";
    public Dictionary<string, Ref<Header>> Headers = new(StringComparer.Ordinal);
    public bool? Explode;
    public bool AllowReserved;
}

public sealed class Example : Element
{
    public string Summary = "", Description = "", ExternalValue = "";
    public object? Value;
}

public sealed class Link : Element
{
    public string OperationRef = "", OperationID = "", Description = "";
    public Dictionary<string, object?> Parameters = new(StringComparer.Ordinal);
    public Server? Server;
    public object? RequestBody;
}

public sealed class Callback : Element
{
    public Dictionary<string, PathItem> Map = new(StringComparer.Ordinal);
}

public sealed class SecurityScheme : Element
{
    public string Type = "", Description = "", Name = "", In = "", Scheme = "", BearerFormat = "", OpenIdConnectUrl = "";
    public OAuthFlows? Flows;
}

public sealed class OAuthFlows : Element
{
    public OAuthFlow? Implicit, Password, ClientCredentials, AuthorizationCode;
}

public sealed class OAuthFlow : Element
{
    public string AuthorizationURL = "", TokenURL = "", RefreshURL = "";
    public Dictionary<string, string> Scopes = new(StringComparer.Ordinal);
}

/// <summary>A security requirement: scheme name → scopes.</summary>
public sealed class SecurityRequirement : Dictionary<string, List<string>>
{
    public SecurityRequirement() : base(StringComparer.Ordinal) { }
}

public sealed class Discriminator : Element
{
    public string PropertyName = "";
    public Dictionary<string, string> Mapping = new(StringComparer.Ordinal);
}

public sealed class Xml : Element, IDeepEquatable, IOutput
{
    public string Name = "", Namespace = "", Prefix = "";
    public bool Attribute, Wrapped;

    public bool DeepEquals(object? other) => other is Xml o && o.Name == Name && o.Namespace == Namespace && o.Prefix == Prefix && o.Attribute == Attribute && o.Wrapped == Wrapped
        && Values.DeepEqual(Extensions ?? EmptyExt, o.Extensions ?? EmptyExt);

    private static readonly Dictionary<string, object?> EmptyExt = new();

    public object? ToOutput()
    {
        var m = new OMap();
        m.OptStr("name", Name); m.OptStr("namespace", Namespace); m.OptStr("prefix", Prefix);
        m.OptTrue("attribute", Attribute); m.OptTrue("wrapped", Wrapped);
        return m;
    }
}

public struct ExclusiveBound
{
    public bool? Bool;
    public double? Value;
    public readonly bool IsSet => Bool is not null || Value is not null;
}

public struct BoolSchema
{
    public bool? Has;
    public Ref<Schema>? Schema;
}

public sealed class Schema : Element
{
    public List<Ref<Schema>> OneOf = [], AnyOf = [], AllOf = [];
    public Ref<Schema>? Not;
    public List<string>? Type;
    public string Title = "", Format = "", Description = "";
    public List<object?>? Enum;
    public object? Default, Example;
    public ExternalDocs? ExternalDocs;
    public bool UniqueItems;
    public ExclusiveBound ExclusiveMin, ExclusiveMax;
    public bool Nullable, ReadOnly, WriteOnly, AllowEmptyValue, Deprecated;
    public Xml? XML;
    public double? Min, Max, MultipleOf;
    public ulong MinLength;
    public ulong? MaxLength;
    public string Pattern = "";
    public ulong MinItems;
    public ulong? MaxItems;
    public Ref<Schema>? Items;
    public List<string> Required = [];
    public Dictionary<string, Ref<Schema>> Properties = new(StringComparer.Ordinal);
    public ulong MinProps;
    public ulong? MaxProps;
    public BoolSchema AdditionalProperties;
    public Discriminator? Discriminator;
    public object? Const;
    public bool HasConst;
    public List<object?>? Examples;
    public List<Ref<Schema>> PrefixItems = [];
    public Ref<Schema>? Contains;
    public ulong? MinContains, MaxContains;
    public Dictionary<string, Ref<Schema>> PatternProperties = new(StringComparer.Ordinal);
    public Dictionary<string, Ref<Schema>> DependentSchemas = new(StringComparer.Ordinal);
    public Ref<Schema>? PropertyNames;
    public BoolSchema UnevaluatedItems, UnevaluatedProperties;
    public Ref<Schema>? If, Then, Else;
    public Dictionary<string, List<string>>? DependentRequired;
    public bool? Always;
    public Dictionary<string, Ref<Schema>> Defs = new(StringComparer.Ordinal);
    public string SchemaDialect = "", Comment = "";
    public string SchemaID = "", Anchor = "", DynamicRef = "", DynamicAnchor = "";
    public string ContentMediaType = "", ContentEncoding = "";
    public Ref<Schema>? ContentSchema;

    public Schema ShallowClone() => (Schema)MemberwiseClone();

    public bool TypeIs(string t) => Type is { Count: 1 } && Type[0] == t;
    public bool TypeIncludes(string t) => Type is not null && Type.Contains(t);

    /// <summary>True when no keyword is set (kin Schema.IsEmpty).</summary>
    public bool IsEmpty()
    {
        if (Always is not null) return false;
        if (Type is { Count: > 0 } || Format != "" || Enum is { Count: > 0 } || UniqueItems || ExclusiveMin.IsSet || ExclusiveMax.IsSet) return false;
        if (Nullable || ReadOnly || WriteOnly || AllowEmptyValue || Deprecated) return false;
        if (Min is not null || Max is not null || MultipleOf is not null) return false;
        if (MinLength != 0 || MaxLength is not null || Pattern != "") return false;
        if (MinItems != 0 || MaxItems is not null) return false;
        if (Required.Count != 0 || MinProps != 0 || MaxProps is not null) return false;
        if (Items is not null && Items.Value is not null && !Items.Value.IsEmpty()) return false;
        foreach (var p in Properties.Values) if (p.Value is not null && !p.Value.IsEmpty()) return false;
        if (AdditionalProperties.Has is false) return false;
        if (AdditionalProperties.Schema?.Value is { } ap && !ap.IsEmpty()) return false;
        if (Not?.Value is { } n && !n.IsEmpty()) return false;
        foreach (var l in new[] { OneOf, AnyOf, AllOf }) foreach (var s in l) if (s.Value is not null && !s.Value.IsEmpty()) return false;
        if (HasConst || Examples is { Count: > 0 } || PrefixItems.Count > 0 || Contains is not null || MinContains is not null || MaxContains is not null) return false;
        if (PatternProperties.Count > 0 || DependentSchemas.Count > 0 || PropertyNames is not null) return false;
        if (UnevaluatedItems.Has is not null || UnevaluatedItems.Schema is not null || UnevaluatedProperties.Has is not null || UnevaluatedProperties.Schema is not null) return false;
        if (If is not null || Then is not null || Else is not null || DependentRequired is { Count: > 0 }) return false;
        if (Defs.Count > 0 || SchemaDialect != "" || Comment != "" || SchemaID != "" || Anchor != "" || DynamicRef != "" || DynamicAnchor != "") return false;
        if (ContentMediaType != "" || ContentEncoding != "" || ContentSchema is not null) return false;
        if (Discriminator is not null) return false;
        return true;
    }
}
