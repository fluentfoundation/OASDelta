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
            // YAML has no negative zero: go-yaml reads "-0" as the integer 0 (so oasdiff, and the port, #26), while JSON
            // keeps -0. The same document in both encodings has 0 there.
            Map b = (Map)WithoutNegativeZero(p.Base)!, r = (Map)WithoutNegativeZero(p.Revision)!;
            using var yaml = CaseFiles.Write(b, false, r, false);
            using var json = CaseFiles.Write(b, true, r, true);
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
    /// A YAML round trip that fails is not counted in these cases; the JSON round trip is still checked:
    /// - go-yaml's own: a string that needs a block scalar with an indentation indicator ("|4-", for a leading space or
    ///   line break) inside a sequence item is written with its text indented from the sequence rather than from the
    ///   node that holds it. Where the two differ, go-yaml cannot read its own output back ("did not find expected '-'
    ///   indicator", "did not find expected key"): an item ("- |4-", "- a:\n    - |4-"), the key of a mapping that is
    ///   an item ("- ? |4-"), or any value of such a mapping ("- b: |4-", "- a: 1\n  b: |4-"). If the text starts
    ///   with spaces, it reads back a different string instead: two of them are lost.
    /// - go-yaml's own: a "&lt;&lt;" key is written plain, so it reads back as a merge key. That merges the value
    ///   into its mapping, or fails ("map merge requires map or sequence of maps as the value").
    /// - YamlDotNet's (#54): a plain key with a word that starts with "*" ("a *b: 1") runs on past its ": ".
    /// oasdiff rejects or misreads such a spec; for go-yaml's cases, OasDelta reproduces both the writing and the
    /// reading (checked against oasdiff 1.35.0, whose flatten -f yaml and diff -f yaml write the same bytes).</summary>
    [Fact]
    public void EmitParseEmitIsStable() =>
        Gen.Frequency<object?>((1, SpecGen.Spec.Select(m => (object?)m)), (2, AnyValue)).Select(AsParsed).Sample(v =>
        {
            var yaml = YamlEmitter.Emit(v);
            string? yamlFailure;
            try
            {
                var again = YamlEmitter.Emit(NodeReader.Parse(yaml)?.ToValue());
                yamlFailure = again == yaml ? null : $"YAML parsed and emitted again differs; {Support.FirstDifference(yaml, again)}";
            }
            catch (YamlParseException e) { yamlFailure = "YAML does not parse back: " + e.Message; }
            if (yamlFailure is not null && !GoYamlMisindents().IsMatch(yaml) && !MergeKey().IsMatch(yaml) && !PlainWordStartingWithStar().IsMatch(yaml))
                throw new PropertyException(yamlFailure);
            var json = JsonEmitter.Emit(v);
            // like the loader: the JSON fast path takes objects and arrays, anything else goes to the YAML parser. YAML
            // (go-yaml too) reads a raw NEL, LS or PS in a quoted string as a line break and folds it, so a top-level
            // string with one doesn't round-trip that way. No spec is a top-level string.
            if (v is string s && s.AsSpan().IndexOfAny('\u0085', '\u2028', '\u2029') >= 0) return;
            var node = JsonNodeReader.TryParse(json, out var n) ? n : NodeReader.Parse(json);
            var jsonAgain = JsonEmitter.Emit(node?.ToValue());
            if (jsonAgain != json) throw new PropertyException($"JSON parsed and emitted again differs; {Support.FirstDifference(json, jsonAgain)}");
        }, print: v => $"{Support.Clip(YamlEmitter.Emit(v), 4000)}\nas JSON: {Support.Clip(JsonEmitter.Emit(v), 4000)}");

    // ------------------------------------------------------------------ helpers

    /// <summary>A block scalar with an indentation indicator, on a line after a sequence item's "- " (which may follow
    /// a complex key's "? " or ": ").</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[ ?:]*- (?s:.*)(?<![^ \n])[|>][1-9][-+]?$", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex GoYamlMisindents();

    /// <summary>A plain "&lt;&lt;" key.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^ *(?:- |\? |: +)*<<:(?: |$)", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex MergeKey();

    /// <summary>A plain scalar with a word that starts with "*", followed by ": " (#54).</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^ *(?:- |\? |: +)*[^ \n""'][^\n]* \*[^\n]*:(?: |$)", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex PlainWordStartingWithStar();

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

    private static object? WithoutNegativeZero(object? v) => v switch
    {
        double d when d == 0 && double.IsNegative(d) => 0.0,
        Map m => m.Aggregate(new Map(), (acc, kv) => { acc[kv.Key] = WithoutNegativeZero(kv.Value); return acc; }),
        List<object?> l => l.Select(WithoutNegativeZero).ToList(),
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
