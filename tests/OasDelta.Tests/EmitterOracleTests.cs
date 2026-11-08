using System.Globalization;
using System.Text;
using System.Text.Json;
using OasDelta.Yaml;
using Xunit;

namespace OasDelta.Tests;

/// <summary>
/// Differential tests of <see cref="YamlEmitter"/> and <see cref="JsonEmitter"/> against the Go libraries oasdiff
/// uses: go.yaml.in/yaml/v3 v3.0.5 (yaml.Marshal) and encoding/json (json.Marshal). tests/emitter-oracle/corpus.json
/// holds the values (hand-picked edge cases plus a fixed-seed pseudo-random part); expected.json holds what the Go
/// program in tools/go-oracle printed for each of them. The emitter-oracle workflow regenerates both with Go and fails
/// if they change, so these files are the Go output, and the comparison here is byte for byte.
/// </summary>
public class EmitterOracleTests
{
    private static readonly Lazy<List<Case>> Cases = new(Load);

    private sealed record Case(string Id, object? Value, string[] Yaml, string[] Json);

    [Fact]
    public void YamlMatchesGoYamlV3() => Compare(c => c.Yaml, YamlEmitter.Emit);

    [Fact]
    public void JsonMatchesEncodingJson() => Compare(c => c.Json, v => JsonEmitter.Emit(v));

    /// <summary>The streaming entry points the CLI uses for diff output write what Emit returns (YAML: with exactly
    /// one final newline).</summary>
    [Fact]
    public void StreamingMatchesEmit()
    {
        foreach (var c in Cases.Value)
            foreach (var d in Docs(c.Value))
            {
                var yaml = new StringWriter();
                YamlEmitter.EmitTo(yaml, d);
                Assert.True(YamlEmitter.Emit(d).TrimEnd('\n') + "\n" == yaml.ToString(), $"{c.Id}: YamlEmitter.EmitTo");
                var json = new StringWriter();
                JsonEmitter.EmitTo(json, d);
                Assert.True(JsonEmitter.Emit(d) == json.ToString(), $"{c.Id}: JsonEmitter.EmitTo");
            }
    }

    [Fact]
    public void CorpusAndExpectedOutputsLineUp()
    {
        Assert.True(Cases.Value.Count > 3000, $"only {Cases.Value.Count} corpus entries");
        foreach (var c in Cases.Value)
        {
            int n = Docs(c.Value).Count;
            Assert.True(c.Yaml.Length == n && c.Json.Length == n, $"{c.Id}: {n} documents, {c.Yaml.Length} YAML and {c.Json.Length} JSON outputs");
        }
    }

    /// <summary>The documents each corpus value is wrapped in; must mirror docs() in tools/go-oracle/main.go.</summary>
    private static List<object?> Docs(object? v)
    {
        var d = new List<object?>
        {
            Map(("m", Map(("l", Seq(v, Map(("k", v), ("x", v)), Seq(v))), ("w", v))), ("v", v)),
            Seq(v, Map(("k", v))),
        };
        if (v is string s) d.Add(Map((s, Map((s, Seq(Map((s, s))))))));
        return d;
    }

    // Go maps: the emitters sort the keys (Normalize), so insertion order must not matter.
    private static Dictionary<string, object?> Map(params (string k, object? v)[] items)
    {
        var m = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in items) m[k] = v;
        return m;
    }

    private static List<object?> Seq(params object?[] items) => [.. items];

    private static void Compare(Func<Case, string[]> expected, Func<object?, string> emit)
    {
        var failures = new List<string>();
        var byCategory = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in Cases.Value)
        {
            var docs = Docs(c.Value);
            var want = expected(c);
            for (int i = 0; i < docs.Count; i++)
            {
                string got;
                try { got = emit(docs[i]); }
                catch (Exception e) { got = $"<{e.GetType().Name}: {e.Message}>"; }
                if (got == want[i]) continue;
                string cat = c.Id[..c.Id.IndexOf('/')];
                byCategory[cat] = byCategory.GetValueOrDefault(cat) + 1;
                failures.Add($"{c.Id} document {i} (value {Show(c.Value)}):\n    go:     {Escape(want[i])}\n    oasdelta: {Escape(got)}");
            }
        }
        if (failures.Count == 0) return;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{failures.Count} documents differ from the Go output (");
        sb.AppendJoin(", ", byCategory.Select(kv => $"{kv.Key}: {kv.Value}"));
        sb.Append(")\n");
        foreach (var f in failures.Take(60)) sb.Append(f).Append('\n');
        if (failures.Count > 60) sb.Append(CultureInfo.InvariantCulture, $"... and {failures.Count - 60} more\n");
        Assert.Fail(sb.ToString());
    }

    private static string Show(object? v) => v switch
    {
        string s => "\"" + Escape(s) + "\"",
        null => "null",
        double d => d.ToString("R", CultureInfo.InvariantCulture) + " (float)",
        IEnumerable<KeyValuePair<string, object?>> m => "{" + string.Join(", ", m.Select(kv => Show(kv.Key) + ": " + Show(kv.Value))) + "}",
        List<object?> l => "[" + string.Join(", ", l.Select(Show)) + "]",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var r in s.EnumerateRunes())
        {
            int c = r.Value;
            if (c == '\\') sb.Append("\\\\");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\t') sb.Append("\\t");
            else if (c >= 0x20 && c < 0x7F) sb.Append((char)c);
            else if (c <= 0xFFFF) sb.Append(CultureInfo.InvariantCulture, $"\\u{c:X4}");
            else sb.Append(CultureInfo.InvariantCulture, $"\\U{c:X8}");
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- loading

    private static List<Case> Load()
    {
        var dir = FindDir();
        using var corpus = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "corpus.json")));
        using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "expected.json")));
        var outputs = new Dictionary<string, (string[] yaml, string[] json)>(StringComparer.Ordinal);
        foreach (var e in expected.RootElement.EnumerateArray())
            outputs.Add(e.GetProperty("id").GetString()!, (Strings(e.GetProperty("yaml")), Strings(e.GetProperty("json"))));
        var cases = new List<Case>();
        foreach (var e in corpus.RootElement.EnumerateArray())
        {
            var id = e.GetProperty("id").GetString()!;
            var (yaml, json) = outputs[id];
            cases.Add(new Case(id, Typed(e.GetProperty("v")), yaml, json));
        }
        Assert.Equal(outputs.Count, cases.Count);
        return cases;
    }

    private static string[] Strings(JsonElement a) => [.. a.EnumerateArray().Select(x => x.GetString()!)];

    /// <summary>Decodes a corpus value; see appendTyped in tools/go-oracle/main.go.</summary>
    private static object? Typed(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: return e.GetString();
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Null: return null;
            case JsonValueKind.Object:
                var p = e.EnumerateObject().Single();
                switch (p.Name)
                {
                    case "int": return long.Parse(p.Value.GetString()!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                    case "uint": return ulong.Parse(p.Value.GetString()!, NumberStyles.None, CultureInfo.InvariantCulture);
                    case "float": return double.Parse(p.Value.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
                    case "seq": return p.Value.EnumerateArray().Select(Typed).ToList();
                    case "map":
                        var m = new Dictionary<string, object?>(StringComparer.Ordinal);
                        foreach (var kv in p.Value.EnumerateArray()) m.Add(kv[0].GetString()!, Typed(kv[1]));
                        return m;
                }
                break;
        }
        throw new InvalidDataException($"bad corpus value {e}");
    }

    private static string FindDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "tests", "emitter-oracle"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? throw new InvalidOperationException("tests/emitter-oracle not found"), "tests", "emitter-oracle");
    }
}
