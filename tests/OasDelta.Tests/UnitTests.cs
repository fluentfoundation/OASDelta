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
