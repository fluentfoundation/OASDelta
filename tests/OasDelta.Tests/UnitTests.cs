using OasDelta.Cli;
using OasDelta.Load;
using OasDelta.Yaml;
using Xunit;

namespace OasDelta.Tests;

public class FlagTests
{
    private static FlagValues Parse(params string[] args)
    {
        var f = new FlagValues(
        [
            new FlagDef("format", "f", FlagKind.Enum, "text", "fmt") { Allowed = ["json", "text", "yaml"] },
            new FlagDef("composed", "c", FlagKind.Bool, false, "c"),
            new FlagDef("exclude", "e", FlagKind.EnumSlice, null, "e") { Allowed = ["a", "b", "c"] },
            new FlagDef("days", "", FlagKind.Uint, 0UL, "d"),
        ]);
        f.Parse(args);
        return f;
    }

    [Fact]
    public void InterspersedFlagsAndArgs()
    {
        var f = Parse("base.yaml", "-f", "JSON", "rev.yaml", "--composed");
        Assert.Equal(["base.yaml", "rev.yaml"], f.Args);
        Assert.Equal("json", f.Str("format"));
        Assert.True(f.Bool("composed"));
    }

    [Fact]
    public void ShorthandForms()
    {
        Assert.Equal("yaml", Parse("-fyaml").Str("format"));
        Assert.Equal("yaml", Parse("-f=yaml").Str("format"));
        var f = Parse("-cf", "json");
        Assert.True(f.Bool("composed"));
        Assert.Equal("json", f.Str("format"));
    }

    [Fact]
    public void SlicesAppendAndValidate()
    {
        Assert.Equal(["a", "b", "c"], Parse("-e", "a,b", "--exclude=c").Strings("exclude"));
        var e = Assert.Throws<FlagException>(() => Parse("-e", "x"));
        Assert.Equal("invalid argument \"x\" for \"-e, --exclude\" flag: x is not one of the allowed values: a, b, or c", e.Message);
    }

    [Fact]
    public void Errors()
    {
        Assert.Equal("unknown flag: --nope", Assert.Throws<FlagException>(() => Parse("--nope")).Message);
        Assert.Equal("unknown shorthand flag: 'x' in -x", Assert.Throws<FlagException>(() => Parse("-x")).Message);
        Assert.Equal("invalid argument \"-1\" for \"--days\" flag: must be a non-negative integer", Assert.Throws<FlagException>(() => Parse("--days", "-1")).Message);
        Assert.Equal("invalid argument \"maybe\" for \"--composed\" flag: must be true or false", Assert.Throws<FlagException>(() => Parse("--composed=maybe")).Message);
    }

    [Fact]
    public void DoubleDashEndsFlags() => Assert.Equal(["-f", "x"], Parse("--", "-f", "x").Args);
}

public class YamlTests
{
    [Fact]
    public void KeysSortNaturallyLikeYamlV3()
    {
        var keys = new List<string> { "/v1/resource10", "/v1/resource2", "/v1/resource1", "b", "a10", "a9", "200", "1000" };
        keys.Sort(YamlKeyComparer.Instance);
        Assert.Equal(["/v1/resource1", "/v1/resource2", "/v1/resource10", "200", "1000", "a9", "a10", "b"], keys);
    }

    [Theory]
    [InlineData("12", 12.0)]
    [InlineData("0x1F", 31.0)]
    [InlineData("0o17", 15.0)]
    [InlineData("0777", 511.0)]   // YAML 1.1 octal, still decoded by yaml.v3
    [InlineData("1_000", 1000.0)]
    [InlineData("-.5", -0.5)]
    [InlineData("1e3", 1000.0)]
    public void PlainScalarsResolveAsNumbers(string text, double expected) =>
        Assert.Equal(expected, Assert.IsType<double>(Node.ResolveScalar(text, true, null)));

    [Theory]
    [InlineData("17113002e812")]          // overflows float64: stays a string, as in Go
    [InlineData("0xFFFFFFFFFFFFFFFFFF")]  // overflows uint64
    [InlineData("1.2.3")]
    [InlineData(".5_0")]
    public void NonNumbersStayStrings(string text) => Assert.IsType<string>(Node.ResolveScalar(text, true, null));

    [Fact]
    public void QuotesStringsThatWouldResolveAsOtherTypes()
    {
        var yaml = YamlEmitter.Emit(new OMap { { "a", "true" }, { "b", "17113002e812" }, { "c", "12" }, { "d", "plain" } });
        Assert.Equal("a: \"true\"\nb: 17113002e812\nc: \"12\"\nd: plain\n", yaml);
    }

    [Theory] // go-yaml v3 is_printable: NEL and everything beyond the BMP force double quotes and are escaped
    [InlineData("fun size \U0001F36B", "a: \"fun size \\U0001F36B\"\n")]
    [InlineData("next\u0085line", "a: \"next\\Nline\"\n")]
    [InlineData("tab\there", "a: \"tab\\there\"\n")]
    [InlineData("caf\u00e9 \u00a0", "a: caf\u00e9 \u00a0\n")] // is_space is U+0020 only: a trailing NBSP keeps the plain style
    public void QuotesAndEscapesLikeGoYaml(string value, string expected) =>
        Assert.Equal(expected, YamlEmitter.Emit(new OMap { { "a", value } }));

    [Fact] // go-yaml v3.0.5: a tab rules out plain and single quotes, but not a literal block
    public void TabsStayInLiteralBlocks() =>
        Assert.Equal("a: |-\n    Go:\n    \ttype T struct{}\n", YamlEmitter.Emit(new OMap { { "a", "Go:\n\ttype T struct{}" } }));

    [Fact]
    public void StreamingMatchesBufferedOutput()
    {
        // several MB, so EmitTo flushes many times; the last value ends in newlines that EmitTo must still trim
        var doc = new OMap();
        for (int i = 0; i < 40_000; i++)
            doc.Add($"key{i}", new OMap { { "from", $"value {i}" }, { "to", new List<object?> { i, $"line\n{i}", true } } });
        doc.Add("zz", "trailing\n\n\n");
        var yaml = new StringWriter { NewLine = "\n" };
        YamlEmitter.EmitTo(yaml, doc);
        Assert.Equal(YamlEmitter.Emit(doc).TrimEnd('\n') + "\n", yaml.ToString());
        var json = new StringWriter { NewLine = "\n" };
        JsonEmitter.EmitTo(json, doc);
        Assert.Equal(JsonEmitter.Emit(doc), json.ToString());
        Assert.True(json.ToString().Length > 2 << 20, "the document should be large enough to flush several times");
    }
}

public class SourceTests
{
    [Theory]
    [InlineData("main:openapi.yaml", SourceType.GitRevision)]
    [InlineData("HEAD~1:api/spec.yaml", SourceType.GitRevision)]
    [InlineData("C:spec.yaml", SourceType.File)]
    [InlineData("https://example.com/spec.yaml", SourceType.Url)]
    [InlineData("spec.yaml", SourceType.File)]
    [InlineData("-", SourceType.Stdin)]
    public void Classifies(string path, SourceType type) => Assert.Equal(type, SpecSource.Of(path).Type);

    [Fact]
    public void GlobMatchesLikeFilepathx()
    {
        var root = Path.Combine(Path.GetTempPath(), "oasdelta-glob-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));
        foreach (var f in new[] { "a/x.yaml", "a/b/y.yaml", "a/b/z.json", "top.yaml" }) File.WriteAllText(Path.Combine(root, f), "");
        try
        {
            var star = Glob.Expand(root + "/a/*.yaml");
            Assert.Equal([root + "/a/x.yaml"], star);
            var deep = Glob.Expand(root + "/**/*.yaml");
            Assert.Contains(root + "/a/b/y.yaml", deep);
            Assert.Contains(root + "/top.yaml", deep);
            Assert.DoesNotContain(root + "/a/b/z.json", deep);
        }
        finally { Directory.Delete(root, true); }
    }
}

/// <summary>The file names changelog sources show: kin-openapi's url.URL{Path: filepath.ToSlash(location)}.String(),
/// then oasdiff's displayFilePath (checked against oasdiff 1.35.0 on Windows; the Unix cases follow the same code).</summary>
public class SourceFileTests
{
    [Theory]
    [InlineData("openapi.yaml", "openapi.yaml")]
    [InlineData("./checker/base.yaml", "./checker/base.yaml")]
    [InlineData("a b.yaml", "a%20b.yaml")]
    [InlineData("ü(1)!.yaml", "%C3%BC%281%29%21.yaml")]
    [InlineData("a%20b+c,d;e=f@g$h&i~j.yaml", "a%2520b+c,d;e=f@g$h&i~j.yaml")]
    [InlineData("-", "stdin")]
    [InlineData("https://example.com/a%20b.yaml", "https://example.com/a%20b.yaml")]
    public void SameOnEveryPlatform(string location, string shown) =>
        Assert.Equal(shown, Checker.Loc.DisplayFilePath(location));

    [Fact]
    public void WindowsPathsAreUrlPathsWithoutTheDrive()
    {
        // "C:\specs\api.yaml" -> "./C:/specs/api.yaml" (RFC 3986 §4.2), which displayFilePath reads as revision "./C"
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("./C:/specs/api.yaml", SpecLoader.OriginFile(@"C:\specs\api.yaml"));
            Assert.Equal("/specs/my%20api.yaml", Checker.Loc.DisplayFilePath(@"C:\specs\my api.yaml"));
            Assert.Equal("sub/x.yaml", Checker.Loc.DisplayFilePath(@"sub\x.yaml"));
        }
        else
        {
            // filepath.ToSlash does nothing on Unix: a backslash is part of the name, and escaped
            Assert.Equal("./C:%5Cspecs%5Capi.yaml", SpecLoader.OriginFile(@"C:\specs\api.yaml"));
            Assert.Equal("sub%5Cx.yaml", Checker.Loc.DisplayFilePath(@"sub\x.yaml"));
        }
    }
}

/// <summary>An array whose items are the array itself has no end. oasdiff 1.35.0 recurses describing its type until
/// Go's stack limit (fatal error: stack overflow, exit 2) on changelog and breaking; the port shows the repeated
/// schema as "…". Found by the property tests, where the overflow took down the test process.</summary>
public class CyclicArrayTests
{
    private const string Spec = """
        openapi: 3.0.3
        info: {title: t, version: '1'}
        paths:
          /p:
            post:
              requestBody:
                content:
                  application/json:
                    schema: SCHEMA
              responses:
                '200': {description: ok}
        components:
          schemas:
            Item: {type: array, items: {$ref: '#/components/schemas/Item'}}
            A: {type: array, items: {$ref: '#/components/schemas/B'}}
            B: {type: array, format: pair, items: {$ref: '#/components/schemas/A'}}
        """;

    private static (int Code, string Out) Changelog(string baseSchema, string revisionSchema)
    {
        var dir = Directory.CreateTempSubdirectory("oasdelta-cyclic-").FullName;
        try
        {
            string b = Path.Combine(dir, "base.yaml"), r = Path.Combine(dir, "revision.yaml");
            File.WriteAllText(b, Spec.Replace("SCHEMA", baseSchema));
            File.WriteAllText(r, Spec.Replace("SCHEMA", revisionSchema));
            var stdout = new StringWriter { NewLine = "\n" };
            int code = Cli.Cli.Run(["changelog", b, r], stdout, new StringWriter());
            return (code, stdout.ToString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("{$ref: '#/components/schemas/Item'}", "{type: string}", "array<…>", "string")]
    [InlineData("{type: string}", "{$ref: '#/components/schemas/Item'}", "string", "array<…>")]
    [InlineData("{$ref: '#/components/schemas/A'}", "{type: string}", "array<array<…>/pair>", "string")]
    public void TheRepeatedSchemaIsShownAsAnEllipsis(string baseSchema, string revisionSchema, string from, string to)
    {
        var (code, output) = Changelog(baseSchema, revisionSchema);
        Assert.Equal(0, code);
        Assert.Contains($"changed from `{from}` to `{to}`", output);
    }
}

/// <summary>kin-openapi's resolve pass rejects null where it expects an object in some places and not others, and
/// reports the first one in its own order (components by kind, then paths, then webhooks, each sorted by name).
/// Each expectation is what oasdiff 1.35.0 prints (exit 102).</summary>
public class NullObjectTests
{
    private static string? LoadError(string body)
    {
        try
        {
            new SpecLoader().LoadFromText("openapi: 3.1.0\ninfo: {title: t, version: '1'}\n" + body, "spec.yaml");
            return null;
        }
        catch (LoadException e) { return e.Message; }
    }

    private const string Ok = "responses: {'200': {description: ok}}";

    [Theory]
    [InlineData("paths: {}\ncomponents: {schemas: {A: {oneOf: [{type: string}, null]}}}", "schema")]
    [InlineData("paths: {}\ncomponents: {schemas: {A: {properties: {a: null}}}}", "schema")]
    [InlineData("paths: {}\ncomponents: {schemas: {A: {prefixItems: [null]}}}", "schema")]
    [InlineData("paths: {}\ncomponents: {schemas: {A: {$defs: {x: null}}}}", "schema")]
    [InlineData("paths: {}\ncomponents: {schemas: {A: null}}", "schema")]
    [InlineData("paths: {/p: {post: {parameters: [null], " + Ok + "}}}", "parameter")]
    [InlineData("paths: {/p: {post: {responses: {'200': null}}}}", "response")]
    [InlineData("paths: {/p: {post: {responses: {'200': {description: ok, headers: {X: null}}}}}}", "header")]
    [InlineData("paths: {/p: {post: {responses: {'200': {description: ok, links: {l: null}}}}}}", "link")]
    [InlineData("paths: {/p: {post: {callbacks: {c: null}, " + Ok + "}}}", "callback")]
    [InlineData("paths: {/p: {post: {callbacks: {c: {'{$url}': null}}, " + Ok + "}}}", "path item")]
    [InlineData("paths: {}\ncomponents: {requestBodies: {B: null}}", "requestBody")]
    [InlineData("paths: {}\ncomponents: {securitySchemes: {S: null}}", "securityScheme")]
    [InlineData("paths: {}\ncomponents: {examples: {E: null}}", "example")]
    // the schema components come before the paths, whatever their names
    [InlineData("paths: {/p: {post: {parameters: [null], " + Ok + "}}}\ncomponents: {schemas: {A: null}}", "schema")]
    // headers come before schemas
    [InlineData("paths: {}\ncomponents: {schemas: {A: null}, headers: {H: null}}", "header")]
    // paths by name, operations by method
    [InlineData("paths: {/b: {post: {parameters: [null]}}, /a: {post: {responses: {'200': null}}}}", "response")]
    [InlineData("paths: {/a: {put: {parameters: [null]}, get: {responses: {'200': null}}}}", "response")]
    public void NullIsRejectedWhereKinRejectsIt(string body, string kind) =>
        Assert.Equal($"invalid {kind}: value MUST be an object", LoadError(body));

    [Theory]
    [InlineData("paths: {/p: {post: {requestBody: {content: {a/b: {schema: {items: null, not: null, additionalProperties: null}}}}, " + Ok + "}}}")]
    [InlineData("paths: {/p: {post: {requestBody: null, " + Ok + "}}}")]
    [InlineData("paths: {/p: {post: {requestBody: {content: {a/b: null}}, " + Ok + "}}}")]
    [InlineData("paths: {/p: null, /q: {post: null}}")]
    [InlineData("paths: {}\ncomponents: {links: {L: null}, pathItems: {P: null}}")]
    [InlineData("paths: {}\nwebhooks: {w: null}")]
    public void NullIsAcceptedWhereKinSkipsIt(string body) => Assert.Null(LoadError(body));

    /// <summary>An empty mapping that is a list item has no origin (oasdiff/yaml3 locates it by its first key).</summary>
    [Fact]
    public void AnEmptyListItemHasNoOrigin()
    {
        var spec = new SpecLoader().LoadFromText(
            "openapi: 3.1.0\ninfo: {title: t, version: '1'}\npaths: {}\ncomponents:\n  schemas:\n    A:\n      oneOf:\n        - {}\n        - {type: string}\n    B: {}\n",
            "spec.yaml");
        var a = spec.Components!.Schemas["A"].Value!;
        Assert.Null(a.OneOf[0].Value!.Origin);
        Assert.NotNull(a.OneOf[1].Value!.Origin);
        Assert.NotNull(spec.Components.Schemas["B"].Value!.Origin); // a map value is located by its key, empty or not
    }
}

/// <summary>--allow-external-refs=false (the default of the GitHub actions): which $refs are refused, with which
/// message and exit code. Checked against oasdiff 1.35.0 (load/git.go, load/errors.go, kin-openapi's loader).</summary>
public class ExternalRefPolicyTests
{
    private const string Dir = "tests/fixtures/external-refs/";

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        int code = Cli.Cli.Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>The repository root, or null outside a git checkout (git revision paths are relative to it).</summary>
    private static readonly string? Root = FindRoot();

    private static string? FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
                return d.FullName;
        return null;
    }

    [Fact]
    public void AFileSpecMayNotReadASiblingFile()
    {
        if (Root is null) return;
        var spec = Path.Combine(Root, Dir, "split.yaml");
        var (code, _, err) = Run("breaking", spec, spec, "--allow-external-refs=false");
        Assert.Equal(123, code);
        Assert.Contains("encountered disallowed external reference: \"./schemas.yaml#/Pet\"", err, StringComparison.Ordinal);
    }

    [Fact]
    public void AGitRevisionSpecReadsItsRelativeRefsFromTheRevision()
    {
        if (Root is null || !Committed("split.yaml")) return;
        var (code, output, err) = Run("breaking", $"HEAD:{Dir}split.yaml", $"HEAD:{Dir}split.yaml", "--allow-external-refs=false");
        Assert.True(code == 0, err);
        Assert.Equal("No changes detected\n", output);
    }

    [Fact]
    public void AGitRevisionSpecMayNotReadAUrl()
    {
        if (Root is null || !Committed("url.yaml")) return;
        var (code, _, err) = Run("breaking", $"HEAD:{Dir}url.yaml", $"HEAD:{Dir}url.yaml", "--allow-external-refs=false");
        Assert.Equal(123, code);
        Assert.Contains($"failed to load base spec from \"HEAD:{Dir}url.yaml\": external $ref not allowed (enable --allow-external-refs to permit): https://example.invalid/schemas.yaml", err, StringComparison.Ordinal);
    }

    /// <summary>Whether HEAD has the fixture (not yet, before it is first committed).</summary>
    private static bool Committed(string name)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", ["cat-file", "-e", $"HEAD:{Dir}{name}"])
        { WorkingDirectory = Root, RedirectStandardError = true, RedirectStandardOutput = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode == 0;
    }
}

/// <summary>kin-openapi's resolveComponent: what a $ref with a fragment may point at, and its errors. It drills into the
/// typed document first (through a $ref'd component's value, and only to the kind the $ref stands for); when that fails
/// it drills the plain data of the document holding the $ref, and that drill's error is the one reported. Each
/// expectation is what oasdiff 1.35.0 prints (exit 102).</summary>
public class KinRefTests
{
    private const string Components = """
        components:
          schemas:
            A: {type: object, properties: {x: {type: string}}}
            R: {$ref: '#/components/schemas/A'}
          parameters:
            P: {name: q, in: query, schema: {type: string}}
          headers:
            H: {schema: {type: string}}
          examples:
            E: {value: {k: v}}
          x-defs:
            D: {type: string}
        x-defs: {B: null}
        x-l: [{type: string}]
        """;

    private static string? LoadError(string operation)
    {
        try
        {
            new SpecLoader().LoadFromText("openapi: 3.0.3\ninfo: {title: t, version: '1'}\npaths:\n  /p:\n    x-p: {type: string}\n    get:\n" + operation + "\n" + Components, "spec.yaml");
            return null;
        }
        catch (LoadException e) { return e.Message; }
    }

    private static string SchemaRef(string reference) =>
        $"      responses: {{'200': {{description: ok, content: {{a/b: {{schema: {{$ref: '{reference}'}}}}}}}}}}";

    [Theory]
    [InlineData("#/components/parameters/P", "bad data in \"#/components/parameters/P\" (expecting ref to schema object)")]
    [InlineData("#/info/title", "bad data in \"#/info/title\" (expecting ref to schema object)")]
    [InlineData("#/x-l", "bad data in \"#/x-l\" (expecting ref to schema object)")]
    [InlineData("#/x-l/abc", "failed to resolve \"abc\" in fragment in URI: \"#/x-l/abc\": strconv.Atoi: parsing \"abc\": invalid syntax")]
    [InlineData("#/x-l/-1", "failed to resolve \"-1\" in fragment in URI: \"#/x-l/-1\": invalid slice index \"-1\"")]
    [InlineData("#/x-l/+0", "failed to resolve \"+0\" in fragment in URI: \"#/x-l/+0\": invalid slice index \"+0\"")]
    [InlineData("#/x-l/5", "failed to resolve \"5\" in fragment in URI: \"#/x-l/5\": slice index out of bounds")]
    [InlineData("#/x-defs/Z", "failed to resolve \"Z\" in fragment in URI: \"#/x-defs/Z\": map key \"Z\" not found")]
    [InlineData("#/info/title/x", "failed to resolve \"x\" in fragment in URI: \"#/info/title/x\": not a map, slice nor struct")]
    [InlineData("#components/schemas/A", "expected fragment prefix '#/' in URI \"#components/schemas/A\"")]
    // kin: the root's x- keys as a schema, or nothing
    [InlineData("#", "bad data in \"#\" (expecting ref to schema object)")]
    [InlineData("#/", "bad data in \"#/\" (expecting ref to schema object)")]
    // kin: no error, the $ref resolves to nothing (its drill closure overwrites the error with nil)
    [InlineData("#/x-defs/B/C", "failed to resolve \"C\" in fragment in URI: \"#/x-defs/B/C\": \"B\" is null")]
    // kin: a panic (a nil *SchemaRef copied)
    [InlineData("#/components/schemas/A/not", "failed to resolve \"not\" in fragment in URI: \"#/components/schemas/A/not\"")]
    [InlineData("#/components/schemas/A/additionalProperties", "failed to resolve \"additionalProperties\" in fragment in URI: \"#/components/schemas/A/additionalProperties\"")]
    public void ASchemaRefIsRejected(string reference, string error) => Assert.Equal(error, LoadError(SchemaRef(reference)));

    /// <summary>The fragment of a $ref to another file is looked up in that file. kin 0.151.0 looks it up in the
    /// file holding the $ref when the other file doesn't have it, so oasdiff 1.35.0 silently uses this file's Pet.</summary>
    [Fact]
    public void AFragmentIsLookedUpInTheFileItNames() =>
        Assert.Equal("failed to resolve \"Pet\" in fragment in URI: \"./defs.yaml#/components/schemas/Pet\": map key \"Pet\" not found",
            LoadErrorWith("./defs.yaml#/components/schemas/Pet", "openapi: 3.0.3\ninfo: {title: d, version: '1'}\npaths: {}\ncomponents: {schemas: {Cat: {type: string}}}\n"));

    /// <summary>"#" is the whole file when that file is the object itself (kin's issue 856).</summary>
    [Fact]
    public void ARefToAFileThatIsASchemaIsAccepted() => Assert.Null(LoadErrorWith("./defs.yaml#", "type: object\nproperties: {id: {type: string}}\n"));

    /// <summary>Loads a spec, which also has a Pet, whose response schema is <paramref name="reference"/>, beside
    /// defs.yaml.</summary>
    private static string? LoadErrorWith(string reference, string defs)
    {
        var dir = Directory.CreateTempSubdirectory("oasdelta-kinref-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "defs.yaml"), defs);
            var spec = Path.Combine(dir.FullName, "spec.yaml");
            File.WriteAllText(spec, "openapi: 3.0.3\ninfo: {title: t, version: '1'}\npaths:\n  /p:\n    get:\n" + SchemaRef(reference) +
                "\ncomponents: {schemas: {Pet: {type: integer}}}\n");
            new SpecLoader().LoadFile(spec);
            return null;
        }
        catch (LoadException e) { return e.Message; }
        finally { dir.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("#/components/schemas/R/properties/x")] // through R, a $ref itself
    [InlineData("#/components/headers/H/schema")]       // not in the typed Header; found in the plain data
    [InlineData("#/paths/~1p/x-p")]
    [InlineData("#/components/x-defs/D")]
    [InlineData("#/x-defs/B")]                          // null: resolves to nothing
    public void ASchemaRefIsAccepted(string reference) => Assert.Null(LoadError(SchemaRef(reference)));

    [Theory]
    [InlineData("      parameters: [{$ref: '#/components/schemas/A'}]\n      responses: {'200': {description: ok}}", "parameter")]
    [InlineData("      parameters: [{$ref: '#/components/headers/H'}]\n      responses: {'200': {description: ok}}", "parameter")]
    [InlineData("      responses: {'200': {$ref: '#/components/schemas/A'}}", "response")]
    [InlineData("      requestBody: {$ref: '#/components/parameters/P'}\n      responses: {'200': {description: ok}}", "requestBody")]
    [InlineData("      responses: {'200': {description: ok, headers: {Y: {$ref: '#/components/parameters/P'}}}}", "header")]
    [InlineData("      responses: {'200': {description: ok, content: {a/b: {examples: {e: {$ref: '#/components/schemas/A'}}}}}}", "example")]
    public void ARefToAnotherKindIsRejected(string operation, string kind) =>
        Assert.EndsWith($"(expecting ref to {kind} object)", LoadError(operation));

    [Fact]
    public void AParameterRefToAnExtensionMapIsAccepted() =>
        Assert.Null(LoadError("      parameters: [{$ref: '#/components/x-defs/D'}]\n      responses: {'200': {description: ok}}"));
}
