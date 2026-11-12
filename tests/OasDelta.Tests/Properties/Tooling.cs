using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsCheck;
using OasDelta.Yaml;

namespace OasDelta.Tests.Properties;

/// <summary>Something that runs oasdiff-style command lines: OasDelta in process, or an external binary.</summary>
internal interface ITool
{
    string Name { get; }
    Outcome Run(params string[] args);
}

/// <summary>Exit code and stdout of a run; <see cref="Crashed"/>: the Go binary panicked or hit a fatal runtime error
/// (exit 2, "panic:" or "fatal error:" on stderr).
/// OasDelta never reports a crash this way: an exception escaping the CLI fails the test instead.</summary>
internal readonly record struct Outcome(int Code, string Out, bool Crashed = false);

/// <summary>OasDelta through <see cref="Cli.Cli.Run"/>, exactly as Main runs it, one call at a time (the CLI was written
/// for one run per process). An exception escaping it is a crash: Main would report it with exit code 100.</summary>
internal sealed class InProcess : ITool
{
    public static readonly InProcess Instance = new();
    private static readonly Lock Gate = new();
    public string Name => "oasdelta";

    public Outcome Run(params string[] args)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        lock (Gate)
        {
            try { return new Outcome(Cli.Cli.Run(args, stdout, stderr), stdout.ToString()); }
            catch (Exception e) { throw new InvalidOperationException($"oasdelta {string.Join(' ', args)} crashed: {e}", e); }
        }
    }
}

/// <summary>An external binary, e.g. the oasdiff release: one process per run.</summary>
internal sealed class External(string exe) : ITool
{
    private static readonly UTF8Encoding Utf8 = new(false);
    public string Name => Path.GetFileNameWithoutExtension(exe);

    public Outcome Run(params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000))
        {
            p.Kill(true);
            throw new TimeoutException($"{Name} {string.Join(' ', args)} did not finish in 120 s");
        }
        p.WaitForExit();
        // a panic, or a fatal runtime error such as "fatal error: stack overflow" (both exit 2)
        return new Outcome(p.ExitCode, stdout.Result,
            p.ExitCode == 2 && (stderr.Result.Contains("panic:", StringComparison.Ordinal) || stderr.Result.Contains("fatal error:", StringComparison.Ordinal)));
    }
}

/// <summary>A generated base document, the edits that turn it into the revision, and the file format of each.</summary>
internal sealed class SpecPair
{
    public Map Base { get; }
    public Map Revision { get; }
    public Edit[] Edits { get; }
    public List<string> Applied { get; }
    public bool BaseJson { get; }
    public bool RevisionJson { get; }

    /// <summary>Start of both file names: changelog sources show file names URL-escaped, so some need escaping.</summary>
    public string Stem { get; }

    public SpecPair(Map @base, Edit[] edits, bool baseJson, bool revisionJson, string stem = "spec")
    {
        Base = @base;
        Edits = edits;
        BaseJson = baseJson;
        RevisionJson = revisionJson;
        Stem = stem;
        Revision = SpecGen.Clone(@base);
        Applied = Properties.Edits.Apply(Revision, edits);
    }

    private static readonly string[] Stems = ["spec", "my spec", "ü(1)!", "a%20b", "[x] y+z"];

    public static readonly Gen<SpecPair> Gen = CsCheck.Gen.Select(SpecGen.Spec, Properties.Edits.Some, CsCheck.Gen.Int[0, 9], CsCheck.Gen.Int[0, 9],
        CsCheck.Gen.Frequency<string>((4, CsCheck.Gen.Const("spec")), (1, CsCheck.Gen.OneOf(Stems))),
        (b, e, bj, rj, stem) => new SpecPair(b, e, bj == 0, rj == 0, stem)); // mostly YAML, like most real specs

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append("edits:\n");
        foreach (var a in Applied) sb.Append("  ").Append(a).Append('\n');
        sb.Append(BaseJson ? "base (JSON):\n" : "base (YAML):\n").Append(Serialize(Base, BaseJson));
        sb.Append(RevisionJson ? "revision (JSON):\n" : "revision (YAML):\n").Append(Serialize(Revision, RevisionJson));
        return sb.ToString();
    }

    public static string Serialize(Map doc, bool json) => json ? JsonEmitter.Emit(doc, indent: true) + "\n" : YamlEmitter.Emit(doc);
}

/// <summary>A pair written to files (absolute paths, so the CLI's working directory does not matter).</summary>
internal sealed class CaseFiles : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);
    public string Dir { get; }
    public string Base { get; }
    public string Revision { get; }


    private CaseFiles(string dir, string b, string r) { Dir = dir; Base = b; Revision = r; }

    public static CaseFiles Write(Map @base, bool baseJson, Map revision, bool revisionJson, string? dir = null, string stem = "spec")
    {
        dir ??= Path.Combine(Path.GetTempPath(), "oasdelta-properties", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        var b = Path.Combine(dir, stem + (baseJson ? "-base.json" : "-base.yaml"));
        var r = Path.Combine(dir, stem + (revisionJson ? "-revision.json" : "-revision.yaml"));
        File.WriteAllText(b, SpecPair.Serialize(@base, baseJson), Utf8);
        File.WriteAllText(r, SpecPair.Serialize(revision, revisionJson), Utf8);
        return new CaseFiles(dir, b, r);
    }

    public static CaseFiles Write(SpecPair p, string? dir = null) => Write(p.Base, p.BaseJson, p.Revision, p.RevisionJson, dir, p.Stem);

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal static partial class Support
{
    public static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "tests", "golden"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("tests/golden not found");
    }

    /// <summary>Where failing cases and promotion candidates are written: $OASDELTA_PROPERTIES_OUT, else
    /// TestResults/properties (uploaded by CI with the test results).</summary>
    public static string OutDir =>
        Environment.GetEnvironmentVariable("OASDELTA_PROPERTIES_OUT") is { Length: > 0 } d ? d : Path.Combine(Root, "TestResults", "properties");

    /// <summary>Keeps a failing case: the pair, the edits, and what went wrong. Returns where.</summary>
    public static string Keep(string kind, SpecPair p, string what)
    {
        var dir = Path.Combine(OutDir, kind, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6]);
        CaseFiles.Write(p, dir);
        File.WriteAllText(Path.Combine(dir, "README.txt"), what + "\n\n" + string.Join('\n', p.Applied) + "\n");
        return dir;
    }

    private const string SchemaRefPrefix = "#/components/schemas/";

    /// <summary>Component schemas that are only a $ref to another component (<c>Item: {$ref: '#/components/schemas/Error'}</c>),
    /// in either document, mapped to one name of their group (the first in ordinal order). Every name of a group
    /// belongs to one schema, and which of them oasdiff shows as a change's shared schema depends on Go map order
    /// (checker/shared_schema.go), so it changes from run to run. Names that are no alias are not in the map.</summary>
    public static Dictionary<string, string> SchemaAliases(params Map[] docs)
    {
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        string Find(string x)
        {
            while (parent.TryGetValue(x, out var p) && p != x) x = p;
            return x;
        }
        foreach (var doc in docs)
        {
            if (doc.GetValueOrDefault("components") is not Map c || c.GetValueOrDefault("schemas") is not Map schemas) continue;
            foreach (var (name, value) in schemas)
            {
                if (value is not Map m || m.Count != 1 || m.GetValueOrDefault("$ref") is not string r || !r.StartsWith(SchemaRefPrefix, StringComparison.Ordinal)) continue;
                var target = r[SchemaRefPrefix.Length..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                string a = Find(name), b = Find(target);
                if (a == b) continue;
                // the root of a group is its first name, so it is the same whatever order the aliases come in
                if (string.CompareOrdinal(a, b) < 0) parent[b] = a; else parent[a] = b;
                parent.TryAdd(a, a);
                parent.TryAdd(b, b);
            }
        }
        return parent.Keys.ToDictionary(n => n, Find, StringComparer.Ordinal);
    }

    /// <summary>The output with every shared-schema name that is an alias (<see cref="SchemaAliases"/>) replaced by the
    /// first name of its group: in the change's text ("shared schema: `Item`", in each language oasdiff has) and in
    /// the sharedSchema.name field of JSON and YAML. Nothing else changes, so any other difference still counts.</summary>
    public static string WithCanonicalSharedSchemaNames(string output, IReadOnlyDictionary<string, string> aliases)
    {
        if (aliases.Count == 0) return output;
        string Canonical(string name) => aliases.TryGetValue(name, out var c) ? c : name;
        output = SharedSchemaLabel().Replace(output, m => m.Groups[1].Value + Canonical(m.Groups[2].Value) + "`");
        output = SharedSchemaJsonName().Replace(output, m =>
        {
            string name;
            try { name = JsonSerializer.Deserialize<string>("\"" + m.Groups[2].Value + "\"")!; }
            catch (JsonException) { return m.Value; }
            return aliases.ContainsKey(name) ? m.Groups[1].Value + JsonEncodedText.Encode(Canonical(name)) + "\"" : m.Value;
        });
        return SharedSchemaYamlName().Replace(output, m =>
        {
            var raw = m.Groups[2].Value;
            var name = raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'' ? raw[1..^1].Replace("''", "'", StringComparison.Ordinal)
                : raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : raw;
            return aliases.ContainsKey(name) ? m.Groups[1].Value + Canonical(name) : m.Value;
        });
    }

    // checker/localizations_src/*/messages.yaml: shared-schema-detail-name
    [GeneratedRegex("((?:shared schema|esquema compartido|schema compartilhado|общая схема): `)([^`]*)`")]
    private static partial Regex SharedSchemaLabel();

    [GeneratedRegex("(\"sharedSchema\":\\s*\\{\\s*\"name\":\\s*\")((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex SharedSchemaJsonName();

    [GeneratedRegex(@"(sharedSchema:\r?\n[ \t]+name: )(.*?)\r?$", RegexOptions.Multiline)]
    private static partial Regex SharedSchemaYamlName();

    /// <summary>The same output, or the same up to order: lines in a different order (oasdiff's text, Markdown and
    /// HTML output for composed specs) or entries of a JSON array in a different order.</summary>
    public static Agreement Compare(string expected, string actual)
    {
        if (expected == actual) return Agreement.Same;
        if (SortedLines(expected) == SortedLines(actual)) return Agreement.Reordered;
        if (JsonArrayItems(expected) is { } e && JsonArrayItems(actual) is { } a && e.SequenceEqual(a)) return Agreement.Reordered;
        return Agreement.Different;
    }

    public enum Agreement { Same, Reordered, Different }

    private static string SortedLines(string s) => string.Join('\n', s.Split('\n').Order(StringComparer.Ordinal));

    private static List<string>? JsonArrayItems(string s)
    {
        if (!s.StartsWith('[')) return null;
        try
        {
            using var d = JsonDocument.Parse(s);
            return d.RootElement.ValueKind == JsonValueKind.Array
                ? [.. d.RootElement.EnumerateArray().Select(x => x.GetRawText()).Order(StringComparer.Ordinal)]
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The first line where two texts differ, with the lines before it.</summary>
    public static string FirstDifference(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        int i = 0;
        while (i < e.Length && i < a.Length && e[i] == a[i]) i++;
        var sb = new StringBuilder();
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"first difference at line {i + 1}:\n");
        for (int j = Math.Max(0, i - 6); j < i; j++) sb.Append("    ").Append(e[j]).Append('\n');
        sb.Append("  - ").Append(i < e.Length ? Edits.Show(e[i]) : "(end)").Append('\n');
        sb.Append("  + ").Append(i < a.Length ? Edits.Show(a[i]) : "(end)").Append('\n');
        return sb.ToString();
    }

    public static string Clip(string s, int max = 1500) => s.Length <= max ? s : s[..max] + $"\n... ({s.Length - max} more characters)";

    /// <summary>Rule ids in a changelog/breaking JSON array.</summary>
    public static IEnumerable<string> RuleIds(string json)
    {
        if (!json.StartsWith('[')) yield break;
        using var d = JsonDocument.Parse(json);
        foreach (var e in d.RootElement.EnumerateArray())
            if (e.TryGetProperty("id", out var id) && id.GetString() is { } s) yield return s;
    }

    /// <summary>Rule ids that fire somewhere in tests/golden (as RuleCoverageDoesNotDrop counts them).</summary>
    public static HashSet<string> GoldenRuleIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Root, "tests", "golden"), "*.out", SearchOption.AllDirectories))
            foreach (Match m in IdPattern().Matches(File.ReadAllText(f))) ids.Add(m.Groups[1].Value);
        return ids;
    }

    [GeneratedRegex("\"id\": ?\"([a-z0-9-]+)\"")]
    private static partial Regex IdPattern();
}
