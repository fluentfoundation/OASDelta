using System.Text.Json;
using CsCheck;
using OasDelta.Yaml;
using Xunit;

namespace OasDelta.Tests.Properties;

/// <summary>
/// Property-based tests (CsCheck) over generated OpenAPI documents and edits (<see cref="SpecGen"/>,
/// <see cref="Edits"/>). These need no oracle: each property also holds for oasdiff, which you can check by pointing
/// OASDELTA_PROPERTIES_TOOL at an oasdiff binary. Differential testing against oasdiff is in
/// <see cref="OracleProperties"/>.
///
/// Each run samples new cases (CsCheck_Iter, default 100; CsCheck_Time for a time budget). A failure is shrunk to a
/// small case, printed with its seed (replay with CsCheck_Seed), and kept under TestResults/properties.
///
/// A stack overflow ends the test process with no seed and no shrinking. The files of the cases being run at the time
/// stay behind in the temp directory (oasdelta-properties/*): run the CLI on each to find the one that overflows.
/// </summary>
public partial class SpecProperties
{
    private static readonly ITool Tool =
        Environment.GetEnvironmentVariable("OASDELTA_PROPERTIES_TOOL") is { Length: > 0 } exe ? new External(exe) : InProcess.Instance;

    private static readonly string[][] NoChangeCommands =
        [["diff"], ["diff", "-f", "json"], ["changelog", "-f", "json"], ["changelog"], ["breaking"], ["summary", "-f", "json"]];

    private static readonly string[][] EncodingCommands = [["diff", "-f", "json"], ["changelog", "-f", "json"]];

    /// <summary>What each command prints for a minimal document compared with itself.</summary>
    private static readonly Lazy<Dictionary<string, Outcome>> NoChange = new(() =>
    {
        var doc = new Map { ["openapi"] = "3.0.3", ["info"] = new Map { ["title"] = "t", ["version"] = "1" }, ["paths"] = new Map() };
        using var f = CaseFiles.Write(doc, false, doc, false);
        return NoChangeCommands.ToDictionary(c => string.Join(' ', c), c => Tool.Run([.. c, f.Base, f.Revision]));
    });

    /// <summary>A document has no changes against itself, also when one side is JSON and the other YAML.</summary>
    [Fact]
    public void ADocumentHasNoChangesAgainstItself() =>
        Gen.Select(SpecGen.Spec, Gen.Bool, Gen.Bool).Sample((doc, baseJson, revisionJson) =>
        {
            using var f = CaseFiles.Write(doc, baseJson, doc, revisionJson);
            foreach (var c in NoChangeCommands)
            {
                var got = Tool.Run([.. c, f.Base, f.Revision]);
                if (got.Code >= 100 || got.Crashed) return; // could not load or diff it at all: OracleProperties compares those
                var want = NoChange.Value[string.Join(' ', c)];
                if (got != want)
                    throw new PropertyException($"{string.Join(' ', c)} reports changes for identical documents (exit {got.Code}, want {want.Code}):\n{Support.Clip(got.Out)}");
            }
        }, print: t => Keep("self", new SpecPair(t.Item1, [], t.Item2, t.Item3)));

    /// <summary>YAML or JSON is only the encoding: the same pair reports the same changes either way (except for where
    /// in the file each change is).</summary>
    [Fact]
    public void YamlAndJsonInputsGiveTheSameChanges() =>
        SpecPair.Gen.Sample(p =>
        {
            using var yaml = CaseFiles.Write(p.Base, false, p.Revision, false);
            using var json = CaseFiles.Write(p.Base, true, p.Revision, true);
            foreach (var c in EncodingCommands)
            {
                var y = Tool.Run([.. c, yaml.Base, yaml.Revision]);
                var j = Tool.Run([.. c, json.Base, json.Revision]);
                if (y.Crashed || j.Crashed) return; // only an external oasdiff reports crashes; OasDelta throws
                if (y.Code != j.Code || WithoutSources(y.Out) != WithoutSources(j.Out))
                    throw new PropertyException($"{string.Join(' ', c)}: YAML input (exit {y.Code}) and JSON input (exit {j.Code}) differ:\n--- YAML\n{Support.Clip(y.Out)}\n--- JSON\n{Support.Clip(j.Out)}");
            }
        }, print: p => Keep("yaml-json", p));

    /// <summary>breaking reports exactly the changelog's errors and warnings.</summary>
    [Fact]
    public void BreakingIsTheChangelogsErrorsAndWarnings() =>
        SpecPair.Gen.Sample(p =>
        {
            using var f = CaseFiles.Write(p);
            var breaking = Tool.Run("breaking", "-f", "json", f.Base, f.Revision);
            var changelog = Tool.Run("changelog", "-f", "json", f.Base, f.Revision);
            if (breaking.Crashed || changelog.Crashed) return;
            if (breaking.Code != changelog.Code) throw new PropertyException($"exit codes differ: breaking {breaking.Code}, changelog {changelog.Code}");
            if (breaking.Code != 0) return; // a load error, reported the same way
            var want = Entries(changelog.Out).Where(e => e.Level >= 2).Select(e => e.Raw).Order(StringComparer.Ordinal);
            var got = Entries(breaking.Out).Select(e => e.Raw).Order(StringComparer.Ordinal);
            if (!want.SequenceEqual(got))
                throw new PropertyException($"breaking:\n{Support.Clip(breaking.Out)}\nchangelog:\n{Support.Clip(changelog.Out)}");
        }, print: p => Keep("breaking-changelog", p));

    /// <summary>Two documents either differ in both directions or in neither.</summary>
    [Fact]
    public void NoDifferenceIsSymmetric() =>
        SpecPair.Gen.Sample(p =>
        {
            using var f = CaseFiles.Write(p);
            var forward = Tool.Run("diff", "-f", "json", f.Base, f.Revision);
            var backward = Tool.Run("diff", "-f", "json", f.Revision, f.Base);
            if (forward.Code != 0 || backward.Code != 0) return;
            bool none = forward.Out.Trim() == "{}", noneBack = backward.Out.Trim() == "{}";
            if (none != noneBack)
                throw new PropertyException($"base→revision:\n{Support.Clip(forward.Out)}\nrevision→base:\n{Support.Clip(backward.Out)}");
        }, print: p => Keep("symmetry", p));

    /// <summary>Parsing what the emitters write and emitting it again gives the same text (YAML and JSON), for
    /// documents and for arbitrary values with hostile strings.
    ///
    /// One exception, go-yaml's own: a string that needs a block scalar with an indentation indicator ("|4-", for a
    /// leading space or line break), as a sequence item ("- |4-", "- - |4-"), is written with its text indented from
    /// the enclosing node rather than from the dash, and go-yaml cannot read that back ("did not find expected '-'
    /// indicator"). oasdiff rejects such a spec (exit 102), and OasDelta reproduces both the writing and the reading.</summary>
    [Fact]
    public void EmitParseEmitIsStable() =>
        Gen.Frequency<object?>((1, SpecGen.Spec.Select(m => (object?)m)), (2, AnyValue)).Select(AsParsed).Sample(v =>
        {
            var yaml = YamlEmitter.Emit(v);
            Node? parsed;
            try { parsed = NodeReader.Parse(yaml); }
            catch (YamlParseException) when (GoYamlCannotReadBack().IsMatch(yaml)) { return; }
            var again = YamlEmitter.Emit(parsed?.ToValue());
            if (again != yaml) throw new PropertyException($"YAML parsed and emitted again differs; {Support.FirstDifference(yaml, again)}");
            var json = JsonEmitter.Emit(v);
            // like the loader: the JSON fast path takes objects and arrays, anything else goes to the YAML parser
            var node = JsonNodeReader.TryParse(json, out var n) ? n : NodeReader.Parse(json);
            var jsonAgain = JsonEmitter.Emit(node?.ToValue());
            if (jsonAgain != json) throw new PropertyException($"JSON parsed and emitted again differs; {Support.FirstDifference(json, jsonAgain)}");
        }, print: v => $"{Support.Clip(YamlEmitter.Emit(v), 4000)}\nas JSON: {Support.Clip(JsonEmitter.Emit(v), 4000)}");

    // ------------------------------------------------------------------ helpers

    [System.Text.RegularExpressions.GeneratedRegex(@"^ *(- )+[|>][1-9][-+]?$", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex GoYamlCannotReadBack();

    private static readonly Gen<string> AnyString = Gen.Frequency<string>(
        (3, SpecGen.Text),
        (2, Gen.Frequency<int>((6, Gen.Int[0x20, 0x7E]), (2, Gen.Int[0x00, 0x1F]), (2, Gen.Int[0x7F, 0xD7FF]), (1, Gen.Int[0xE000, 0xFFFD]), (1, Gen.Int[0x10000, 0x10FFFF]))
            .Array[0, 12].Select(cs => string.Concat(cs.Select(char.ConvertFromUtf32)))));

    private static readonly Gen<object?> AnyValue = Gen.Recursive<object?>((depth, self) =>
    {
        var scalar = Gen.Frequency<object?>(
            (4, AnyString.Select(s => (object?)s)), (2, SpecGen.Number), (1, Gen.Bool.Select(b => (object?)b)), (1, SpecGen.Absent));
        if (depth > 3) return scalar;
        return Gen.Frequency<object?>(
            (3, scalar),
            (1, self.List[0, 4].Select(l => (object?)l)),
            (1, Gen.Select(AnyString, self).Array[0, 4].Select(kvs =>
            {
                var m = new Map();
                foreach (var (k, v) in kvs) m[k] = v;
                return (object?)m;
            })));
    });

    /// <summary>The value as a parser represents it: numbers are doubles (as in Go, where kin-openapi decodes into
    /// float64), so an integer beyond 2^53 cannot round-trip in either tool.</summary>
    private static object? AsParsed(object? v) => v switch
    {
        long l => (double)l,
        // go-yaml writes negative zero as "-0" and reads that back as the integer 0
        double d when d == 0 && double.IsNegative(d) => 0.0,
        Map m => m.Aggregate(new Map(), (acc, kv) => { acc[kv.Key] = AsParsed(kv.Value); return acc; }),
        List<object?> l => l.Select(AsParsed).ToList(),
        _ => v,
    };

    private sealed record Entry(int Level, string Raw);

    private static List<Entry> Entries(string json)
    {
        using var d = JsonDocument.Parse(json);
        return [.. d.RootElement.EnumerateArray().Select(e => new Entry(e.GetProperty("level").GetInt32(), e.GetRawText()))];
    }

    /// <summary>A changelog without baseSource/revisionSource (file, line and column of each change).</summary>
    private static string WithoutSources(string output)
    {
        if (!output.StartsWith('[')) return output;
        using var d = JsonDocument.Parse(output);
        return string.Join('\n', d.RootElement.EnumerateArray().Select(e =>
            string.Join(',', e.EnumerateObject().Where(p => p.Name is not ("baseSource" or "revisionSource")).Select(p => p.ToString()))));
    }

    private static string Keep(string kind, SpecPair p) => $"{p}\nkept in {Support.Keep(kind, p, "property: " + kind)}";
}

internal sealed class PropertyException(string message) : Exception(message);
