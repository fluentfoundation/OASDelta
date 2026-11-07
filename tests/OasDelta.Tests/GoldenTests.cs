using System.Text;
using Xunit;

namespace OasDelta.Tests;

/// <summary>
/// Byte-for-byte comparison with the upstream Go oasdiff. The golden files under tests/golden were produced by
/// tools/gen_golden.sh with the Go binary over every fixture pair in tests/fixtures (the upstream test data).
/// </summary>
public class GoldenTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Fixtures = Path.Combine(Root, "tests", "fixtures");
    private static readonly string Golden = Path.Combine(Root, "tests", "golden");

    private static readonly Dictionary<string, string[]> Commands = new()
    {
        ["diff-yaml"] = ["diff"],
        ["diff-json"] = ["diff", "-f", "json"],
        ["diff-text"] = ["diff", "-f", "text"],
        ["summary"] = ["summary"],
        ["changelog-json"] = ["changelog", "-f", "json"],
        ["changelog-markdown"] = ["changelog", "-f", "markdown"],
        ["breaking-text"] = ["breaking"],
    };

    private static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "tests", "golden"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("tests/golden not found");
    }

    private static List<(string b, string r)> Pairs()
    {
        // same enumeration as tools/pairs.sh
        var result = new List<(string, string)>();
        var all = Directory.EnumerateFiles(Fixtures, "*", SearchOption.AllDirectories)
            .Select(f => "./" + Path.GetRelativePath(Fixtures, f).Replace('\\', '/')).ToList();
        var set = new HashSet<string>(all);
        foreach (var b in all.Where(f => Path.GetFileName(f).Contains("_base") && (f.EndsWith(".yaml") || f.EndsWith(".json"))).Order(StringComparer.Ordinal))
        {
            var r = b.Replace("_base", "_revision");
            if (set.Contains(r)) result.Add((b, r));
        }
        foreach (var b in all.Where(f => f.EndsWith("1.yaml")).Order(StringComparer.Ordinal))
        {
            var r = b[..^"1.yaml".Length] + "2.yaml";
            if (set.Contains(r)) result.Add((b, r));
        }
        var tests = Directory.EnumerateFiles(Fixtures, "openapi-test*.yaml").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        for (int i = 1; i < tests.Count; i++) result.Add((tests[i - 1]!, tests[i]!));
        result.Add(("openapi-test1.yaml", "openapi-test3.yaml"));
        result.Add(("home-iot-api-1.yaml", "home-iot-api-2.yaml"));
        return result;
    }

    public static IEnumerable<object[]> Cases() => Commands.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesUpstream(string slug)
    {
        Directory.SetCurrentDirectory(Fixtures);
        var codes = File.ReadAllLines(Path.Combine(Golden, slug, "exit-codes.txt"))
            .Select(l => l.Split(' ')).ToDictionary(p => p[0], p => int.Parse(p[1]));
        var failures = new List<string>();
        int checkedCount = 0;
        var seen = new HashSet<string>();
        foreach (var (b, r) in Pairs())
        {
            var name = (b + "--" + r).Replace("./", "").Replace('/', '_').Replace('.', '_');
            if (!seen.Add(name)) continue;
            var goldenFile = Path.Combine(Golden, slug, name + ".out");
            if (!File.Exists(goldenFile)) continue;
            var stdout = new StringWriter { NewLine = "\n" };
            var stderr = new StringWriter { NewLine = "\n" };
            int code = OasDelta.Cli.Cli.Run([.. Commands[slug], b, r], stdout, stderr);
            var expected = File.ReadAllText(goldenFile, Encoding.UTF8);
            checkedCount++;
            if (code != codes[name] || stdout.ToString() != expected) failures.Add($"{b} {r} (exit {code}, want {codes[name]})");
        }
        Assert.True(checkedCount >= 170, $"only {checkedCount} golden cases found");
        Assert.True(failures.Count == 0, $"{failures.Count} mismatches:\n" + string.Join("\n", failures.Take(20)));
    }
}
