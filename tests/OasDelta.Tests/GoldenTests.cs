using System.Text;
using System.Text.RegularExpressions;
using OasDelta.Checker;
using Xunit;

namespace OasDelta.Tests;

/// <summary>
/// Byte-for-byte comparison with the upstream Go oasdiff. tools/gen_golden.sh runs the Go binary over every fixture
/// pair in tests/fixtures, for every command/flag combination in tools/golden-commands.txt, into tests/golden.
/// tests/golden-known.txt lists the cases that are expected to differ (see the comments in that file).
/// </summary>
public partial class GoldenTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Fixtures = Path.Combine(Root, "tests", "fixtures");
    private static readonly string Golden = Path.Combine(Root, "tests", "golden");

    private static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "tests", "golden"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("tests/golden not found");
    }

    /// <summary>tools/golden-commands.txt: "slug|args", arguments split on spaces.</summary>
    public static Dictionary<string, string[]> Commands() =>
        File.ReadAllLines(Path.Combine(Root, "tools", "golden-commands.txt"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('|', 2))
            .ToDictionary(p => p[0], p => p[1].Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>tests/golden-known.txt: "slug pair-name kind reason...". Kind is "#N" (a tracked OasDelta bug, expected
    /// to differ until issue N is fixed) or "unordered" (oasdiff's own line order is nondeterministic; compare as a
    /// multiset of lines).</summary>
    public static Dictionary<(string Slug, string Name), string> Known() =>
        File.ReadAllLines(Path.Combine(Root, "tests", "golden-known.txt"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(p => (p[0], p[1]), p => p[2]);

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
        // pairs that upstream's own tests compare (tools/mine_upstream_pairs.py)
        foreach (var line in File.ReadAllLines(Path.Combine(Root, "tests", "extra-pairs.txt")))
            if (line.Length > 0 && !line.StartsWith('#') && line.Split(' ') is [var b, var r]) result.Add((b, r));
        return result;
    }

    public static IEnumerable<object[]> Cases() => Commands().Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesUpstream(string slug)
    {
        Directory.SetCurrentDirectory(Fixtures);
        var args = Commands()[slug];
        var known = Known();
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
            int code = OasDelta.Cli.Cli.Run([.. args, b, r], stdout, stderr);
            var expected = File.ReadAllText(goldenFile, Encoding.UTF8);
            var actual = stdout.ToString();
            checkedCount++;
            bool same = code == codes[name] && actual == expected;
            switch (known.GetValueOrDefault((slug, name)))
            {
                case null when !same:
                    failures.Add($"{b} {r} (exit {code}, want {codes[name]}){FirstDifference(expected, actual)}");
                    break;
                case "unordered" when !same && !(code == codes[name] && SortedLines(actual) == SortedLines(expected)):
                    failures.Add($"{b} {r}: differs even as unordered lines (exit {code}, want {codes[name]})");
                    break;
                case { } issue when issue.StartsWith('#') && same:
                    failures.Add($"{b} {r} now matches: remove it from tests/golden-known.txt (issue {issue})");
                    break;
            }
        }
        Assert.True(checkedCount >= 170, $"only {checkedCount} golden cases found for {slug}");
        Assert.True(failures.Count == 0, $"{slug}: {failures.Count} mismatches:\n" + string.Join("\n", failures.Take(20)));
    }

    [Fact]
    public void KnownListOnlyNamesExistingCases()
    {
        var commands = Commands();
        foreach (var ((slug, name), kind) in Known())
        {
            Assert.True(commands.ContainsKey(slug), $"tests/golden-known.txt: unknown slug {slug}");
            Assert.True(File.Exists(Path.Combine(Golden, slug, name + ".out")), $"tests/golden-known.txt: no golden {slug}/{name}");
            Assert.True(kind == "unordered" || IssueRef().IsMatch(kind), $"tests/golden-known.txt: kind must be #N or unordered, got {kind}");
        }
    }

    /// <summary>The share of checker rules that fire somewhere in the goldens may only go up: raise
    /// tests/rule-coverage-floor.txt when it does.</summary>
    [Fact]
    public void RuleCoverageDoesNotDrop()
    {
        // every rule id the checker defines (the constants of Ids), the same set tools/check_golden.sh counts
        var all = typeof(Ids).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Golden, "*.out", SearchOption.AllDirectories))
            foreach (Match m in RuleIdInJson().Matches(File.ReadAllText(file)))
                if (all.Contains(m.Groups[1].Value)) seen.Add(m.Groups[1].Value);
        int floor = int.Parse(File.ReadAllText(Path.Combine(Root, "tests", "rule-coverage-floor.txt")).Trim());
        Assert.True(seen.Count >= floor, $"rule coverage dropped: {seen.Count} of {all.Count} rules fire in the goldens, floor is {floor}");
    }

    /// <summary>The first differing line, shortened, so a failure says where it is without dumping whole outputs.</summary>
    private static string FirstDifference(string expected, string actual)
    {
        if (expected == actual) return "";
        int i = 0;
        while (i < expected.Length && i < actual.Length && expected[i] == actual[i]) i++;
        int line = expected.AsSpan(0, i).Count('\n') + 1;
        string Around(string s) => s.Length <= i ? "<end>" : s.Substring(Math.Max(0, i - 40), Math.Min(120, s.Length - Math.Max(0, i - 40))).ReplaceLineEndings("⏎");
        return $"\n    line {line}, char {i}:\n      want: {Around(expected)}\n      got:  {Around(actual)}";
    }

    private static string SortedLines(string s) => string.Join("\n", s.Split('\n').Order(StringComparer.Ordinal));

    [GeneratedRegex("\"id\":\\s*\"([a-z0-9-]+)\"")]
    private static partial Regex RuleIdInJson();

    [GeneratedRegex(@"^#\d+$")]
    private static partial Regex IssueRef();
}
