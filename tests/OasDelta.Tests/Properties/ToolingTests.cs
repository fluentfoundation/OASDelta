using Xunit;

namespace OasDelta.Tests.Properties;

/// <summary>The oracle's allowance for oasdiff's choice of name for an aliased shared schema (Support.SchemaAliases,
/// Support.WithCanonicalSharedSchemaNames). The outputs are oasdiff 1.35.0's for the nightly case of 2026-11-10
/// (seed fVvzMrYnLHU1), where it printed `Item` in 49 of 60 runs and `Error` in 11.</summary>
public class SharedSchemaAliasTests
{
    private static Map Doc(params (string Name, Map Schema)[] schemas)
    {
        var s = new Map();
        foreach (var (name, schema) in schemas) s[name] = schema;
        return new Map { ["components"] = new Map { ["schemas"] = s } };
    }

    private static Map Ref(string name) => new() { ["$ref"] = "#/components/schemas/" + name };

    private static readonly Dictionary<string, string> Aliases = Support.SchemaAliases(
        Doc(("Item", Ref("Error")), ("Error", new Map { ["type"] = "object" }), ("Other", new Map { ["type"] = "string" })),
        Doc(("Thing", Ref("Item"))));

    [Fact]
    public void AliasesMapEveryNameOfAGroupToItsFirstName()
    {
        Assert.Equal(new Dictionary<string, string> { ["Error"] = "Error", ["Item"] = "Error", ["Thing"] = "Error" }, Aliases);
        // a $ref with siblings is a schema of its own, not an alias
        Assert.Empty(Support.SchemaAliases(Doc(("A", new Map { ["$ref"] = "#/components/schemas/B", ["description"] = "d" }))));
    }

    [Theory]
    [InlineData("text", "\t\tthe request property `p/` became nullable (shared schema: `{0}`, also at `q/`)\n")]
    [InlineData("ru", "\t\tсвойство запроса `p/` стало nullable (общая схема: `{0}`, также в `q/`)\n")]
    [InlineData("yaml", "- id: request-property-became-nullable\n  text: 'the request property `p/` became nullable (shared schema: `{0}`, also at `q/`)'\n  sharedSchema:\n    name: {0}\n    properties:\n        - 'p/'\n")]
    [InlineData("json", "[{{\"id\":\"request-property-became-nullable\",\"text\":\"the request property `p/` became nullable (shared schema: `{0}`, also at `q/`)\",\"sharedSchema\":{{\"name\":\"{0}\",\"properties\":[\"p/\"],\"count\":2}}}}]")]
    public void TheNameOfAnAliasedSchemaMayDiffer(string format, string template)
    {
        _ = format;
        string oasdiff = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, "Item");
        string oasdelta = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, "Error");
        Assert.Equal(Support.Agreement.Different, Support.Compare(oasdiff, oasdelta));
        Assert.Equal(Support.Agreement.Same, Support.Compare(
            Support.WithCanonicalSharedSchemaNames(oasdiff, Aliases), Support.WithCanonicalSharedSchemaNames(oasdelta, Aliases)));
    }

    [Fact]
    public void AnyOtherDifferenceStillCounts()
    {
        string Text(string name, string path) => $"the request property `{path}` became nullable (shared schema: `{name}`, also at `q/`)\n";
        // a name that is no alias of the other
        Assert.NotEqual(Support.WithCanonicalSharedSchemaNames(Text("Other", "p/"), Aliases), Support.WithCanonicalSharedSchemaNames(Text("Error", "p/"), Aliases));
        // aliases, but the property differs
        Assert.NotEqual(Support.WithCanonicalSharedSchemaNames(Text("Item", "p/"), Aliases), Support.WithCanonicalSharedSchemaNames(Text("Error", "x/"), Aliases));
        // an alias name outside a shared-schema detail is left alone
        Assert.Equal("removed `#/components/schemas/Item`", Support.WithCanonicalSharedSchemaNames("removed `#/components/schemas/Item`", Aliases));
    }
}

/// <summary>How the oracle sorts mismatches into kinds (Support.MismatchKind), so a known one (#60: source locations
/// under a $ref) doesn't end the run before others are found. The outputs are the nightly's of 2026-11-16/17.</summary>
public class MismatchKindTests
{
    private static string Kind(string[] cmd, string want, string got, int wantCode = 0, int gotCode = 0) =>
        Support.MismatchKind(cmd, new Outcome(gotCode, got), new Outcome(wantCode, want), s => s);

    [Fact]
    public void JsonThatDiffersOnlyInSourcesIsSourceLocations()
    {
        const string oasdiff = """[{"id":"request-parameter-became-enum","text":"t","level":3,"operation":"POST","path":"/p","section":"paths","fingerprint":"20e29ae185d7"},{"id":"response-media-type-removed","level":3,"section":"paths","baseSource":{"file":"a.yaml","line":186,"column":25,"endLine":196,"endColumn":44},"fingerprint":"a67ab9919ac5"}]""";
        const string oasdelta = """[{"id":"request-parameter-became-enum","text":"t","level":3,"operation":"POST","path":"/p","section":"paths","revisionSource":{"file":"b.yaml","line":185,"column":33},"fingerprint":"20e29ae185d7"},{"id":"response-media-type-removed","level":3,"section":"paths","baseSource":{"file":"a.yaml","line":186,"column":25,"endLine":196,"endColumn":44},"fingerprint":"a67ab9919ac5"}]""";
        Assert.Equal("source locations only: changelog -f json", Kind(["changelog", "-f", "json", "--stability-level", "beta"], oasdiff, oasdelta));
    }

    [Fact]
    public void GithubActionsThatDiffersOnlyInSourcesIsSourceLocations()
    {
        const string oasdiff = "::error title=request-parameter-type-changed,file=/tmp/r.yaml,line=78,col=9::in API GET /pets the type changed\n::notice title=request-parameter-became-nullable,file=/tmp/r.yaml,line=78,col=9::in API GET /pets became nullable\n";
        const string oasdelta = "::error title=request-parameter-type-changed,file=/tmp/r.yaml,line=187,col=33::in API GET /pets the type changed\n::notice title=request-parameter-became-nullable,file=/tmp/r.yaml::in API GET /pets became nullable\n";
        Assert.Equal("source locations only: changelog -f githubactions", Kind(["changelog", "-f", "githubactions"], oasdiff, oasdelta));
    }

    [Fact]
    public void YamlThatDiffersOnlyInSourcesIsSourceLocations()
    {
        const string oasdiff = "- id: a\n  level: 3\n  baseSource:\n    file: ./b.yaml\n    line: 11\n    column: 11\n  fingerprint: x\n";
        const string oasdelta = "- id: a\n  level: 3\n  baseSource:\n    file: ./b.yaml\n    line: 12\n    column: 3\n  revisionSource:\n    file: ./r.yaml\n    line: 4\n    column: 3\n  fingerprint: x\n";
        Assert.Equal("source locations only: changelog -f yaml", Kind(["changelog", "-f", "yaml"], oasdiff, oasdelta));
    }

    [Fact]
    public void AnyOtherDifferenceIsOutputAndExitCodesComeFirst()
    {
        // the same sources, a different change: not a source-location mismatch
        Assert.Equal("output: changelog -f json", Kind(["changelog", "-f", "json"],
            """[{"id":"a","baseSource":{"file":"x","line":1,"column":1}}]""", """[{"id":"b","baseSource":{"file":"x","line":1,"column":1}}]"""));
        Assert.Equal("output: breaking", Kind(["breaking", "--fail-on", "WARN"], "a\n", "b\n"));
        Assert.Equal("exit code 0 where oasdiff exits 102: diff -f yaml", Kind(["diff", "-f", "yaml"], "", "x\n", wantCode: 102));
    }

    [Fact]
    public void TheSeedOfACsCheckFailure() =>
        Assert.Equal("b1fwLTMaeFL7", Support.SeedOf("Set seed: \"b1fwLTMaeFL7\" or -e CsCheck_Seed=b1fwLTMaeFL7 to reproduce (0 shrinks, 47 skipped, 1,557 total)."));

    [Fact]
    public void AnUnpairedSurrogateBecomesTheReplacementCharacter()
    {
        Assert.Equal("a\uFFFDb", Support.WellFormed("a\uD83Db"));
        Assert.Equal("\uD83D\uDE00\uFFFD", Support.WellFormed("\uD83D\uDE00\uDE00"));
        Assert.Equal("plain", Support.WellFormed("plain"));
    }

    [Fact]
    public void APathItemRefKinReadsDifferently()
    {
        static Map Item(params (string, object?)[] fields) { var m = new Map(); foreach (var (k, v) in fields) m[k] = v; return m; }
        var r = ("$ref", (object?)"#/paths/~1b");
        Assert.True(Support.HasPathItemRefKinMisreads(new Map { ["paths"] = new Map { ["/a"] = Item(r, ("summary", "s")) } }));
        Assert.True(Support.HasPathItemRefKinMisreads(new Map { ["webhooks"] = new Map { ["w"] = Item(r, ("get", new Map())) } }));
        Assert.True(Support.HasPathItemRefKinMisreads(new Map { ["components"] = new Map { ["pathItems"] = new Map { ["P"] = Item(r, ("parameters", new List<object?>())) } } }));
        var callback = new Map { ["callbacks"] = new Map { ["c"] = new Map { ["{$url}"] = Item(r, ("post", new Map())) } } };
        Assert.True(Support.HasPathItemRefKinMisreads(new Map { ["paths"] = new Map { ["/a"] = new Map { ["get"] = callback } } }));
        // a $ref alone, or with only extensions beside it, is what kin resolves
        Assert.False(Support.HasPathItemRefKinMisreads(new Map { ["paths"] = new Map { ["/a"] = Item(r, ("x-a", 1)) } }));
        // a chain: /a -> /b -> /c
        var chain = new Map { ["paths"] = new Map { ["/a"] = Item(r), ["/b"] = Item(("$ref", "#/paths/~1c")), ["/c"] = new Map { ["get"] = new Map() } } };
        Assert.True(Support.HasPathItemRefKinMisreads(chain));
        Assert.False(Support.HasPathItemRefKinMisreads(new Map { ["paths"] = new Map { ["/a"] = Item(r), ["/b"] = new Map { ["get"] = new Map() } } }));
        // a schema's $ref with siblings is no path item
        Assert.False(Support.HasPathItemRefKinMisreads(new Map { ["components"] = new Map { ["schemas"] = new Map { ["S"] = Item(r, ("description", "d")) } } }));
    }
}
