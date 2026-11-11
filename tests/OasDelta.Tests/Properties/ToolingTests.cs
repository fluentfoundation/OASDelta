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
