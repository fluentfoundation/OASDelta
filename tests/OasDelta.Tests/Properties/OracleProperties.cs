using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using CsCheck;
using Xunit;
using Xunit.Abstractions;

namespace OasDelta.Tests.Properties;

/// <summary>Runs only when OASDIFF names an oasdiff binary (the version in tools/oasdiff-version.txt).</summary>
public sealed class OracleFactAttribute : FactAttribute
{
    public OracleFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OASDIFF") is not { Length: > 0 }) Skip = "set OASDIFF to an oasdiff binary to compare with it";
    }
}

/// <summary>
/// Differential property: for generated base/revision pairs, OasDelta prints what oasdiff prints, byte for byte, with
/// the same exit code. Two differences are allowed, both where oasdiff's own output follows Go's random map order:
/// the order of lines or JSON array entries, and which name of an aliased component schema a change shows as its
/// shared schema. oasdiff is run once per command line; any other run-to-run variation fails the property. Each case
/// runs three command lines drawn from tools/golden-commands.txt, so formats, languages and options vary too.
///
/// Also records which checker rules the generated pairs make fire. A rule that no golden makes fire, reached by a
/// pair on which both tools agree, is a promotion candidate: the pair is kept under properties/new-rules/&lt;rule&gt;
/// to be added to tests/fixtures (raising tests/rule-coverage-floor.txt).
///
/// Run by the Properties workflow (nightly, with a time budget in seconds: CsCheck_Time, sampled in one-minute rounds;
/// the first mismatch ends the run); locally:
/// OASDIFF=path/to/oasdiff CsCheck_Time=60 dotnet test --filter Category=Oracle
/// </summary>
[Trait("Category", "Oracle")]
public class OracleProperties(ITestOutputHelper output)
{
    private static readonly Lazy<string[][]> Commands = new(() => [.. GoldenTests.Commands().Values]);

    private static readonly Gen<int[]> CommandChoice = Gen.Int[0, 1000].Array[3];

    private const int RoundSeconds = 60;

    [OracleFact]
    public void MatchesOasdiff()
    {
        var oasdiff = new External(Environment.GetEnvironmentVariable("OASDIFF")!);
        var golden = Support.GoldenRuleIds();
        var fired = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var promoted = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        int cases = 0, reordered = 0, loadErrors = 0, oasdiffCrashes = 0, aliasNamed = 0;
        string? failure = null;
        var started = DateTime.UtcNow;
        try
        {
            void Check(SpecPair p, int[] choice)
            {
                using var f = CaseFiles.Write(p);
                var aliases = new Lazy<Dictionary<string, string>>(() => Support.SchemaAliases(p.Base, p.Revision));
                foreach (var i in choice)
                {
                    var cmd = Commands.Value[i % Commands.Value.Length];
                    string[] args = [.. cmd, f.Base, f.Revision];
                    var got = InProcess.Instance.Run(args); // throws if OasDelta crashes
                    var want = oasdiff.Run(args);
                    if (want.Crashed)
                    {
                        // oasdiff panicked: nothing to compare with; OasDelta did not crash (an error or a result)
                        if (Interlocked.Increment(ref oasdiffCrashes) <= 5) Support.Keep("oasdiff-crashes", p, $"oasdiff panics on: {string.Join(' ', cmd)} (oasdelta: exit {got.Code})");
                        continue;
                    }
                    var agreement = got.Code == want.Code ? Support.Compare(want.Out, got.Out) : Support.Agreement.Different;
                    // Which name of an aliased schema (Item: {$ref: Error}) oasdiff shows as a change's shared schema
                    // depends on Go's random map order, so that name alone may differ. Nothing else may: oasdiff is
                    // not run again, so any other run-to-run variation in its output fails the property and is looked at.
                    if (agreement == Support.Agreement.Different && got.Code == want.Code && aliases.Value.Count > 0 &&
                        Support.Compare(Support.WithCanonicalSharedSchemaNames(want.Out, aliases.Value),
                                        Support.WithCanonicalSharedSchemaNames(got.Out, aliases.Value)) is not Support.Agreement.Different and var named)
                    {
                        agreement = named;
                        Interlocked.Increment(ref aliasNamed);
                    }
                    if (agreement == Support.Agreement.Different)
                        throw new PropertyException($"{string.Join(' ', cmd)}: exit {got.Code}, oasdiff {want.Code}\n--- oasdiff\n{Support.Clip(want.Out)}\n--- oasdelta\n{Support.Clip(got.Out)}");
                    if (agreement == Support.Agreement.Reordered) Interlocked.Increment(ref reordered);
                }

                // which rules fire; keep the first pair that reaches a rule the goldens never do, once oasdiff agrees on it
                var changelog = InProcess.Instance.Run("changelog", "-f", "json", f.Base, f.Revision);
                Interlocked.Increment(ref cases);
                if (changelog.Code != 0) { Interlocked.Increment(ref loadErrors); return; }
                foreach (var id in Support.RuleIds(changelog.Out).Distinct())
                {
                    fired.AddOrUpdate(id, 1, (_, n) => n + 1);
                    if (golden.Contains(id) || promoted.ContainsKey(id)) continue;
                    var want = oasdiff.Run("changelog", "-f", "json", f.Base, f.Revision);
                    if (want.Code == 0 && Support.Compare(want.Out, changelog.Out) != Support.Agreement.Different && promoted.TryAdd(id, ""))
                        promoted[id] = Support.Keep(Path.Combine("new-rules", id), p, $"rule {id}: fires here and in no golden; oasdelta and oasdiff agree");
                }
            }

            string Print((SpecPair, int[]) t) =>
                $"{t.Item1}\ncommands: {string.Join(" | ", t.Item2.Select(i => string.Join(' ', Commands.Value[i % Commands.Value.Length])))}\nkept in {Support.Keep("mismatch", t.Item1, "oasdelta and oasdiff differ")}";

            var gen = Gen.Select(SpecPair.Gen, CommandChoice);
            if (Environment.GetEnvironmentVariable("CsCheck_Seed") is { Length: > 0 })
                gen.Sample(Check, print: Print); // replay a reported seed
            else
            {
                // rounds of at most a minute, so a mismatch ends the run (shrunk) soon after it is found instead of
                // CsCheck spending the rest of the budget looking for a smaller one
                int budget = int.TryParse(Environment.GetEnvironmentVariable("CsCheck_Time"), out var s) && s > 0 ? s : 60;
                double left;
                while ((left = budget - (DateTime.UtcNow - started).TotalSeconds) >= 1)
                    gen.Sample(Check, time: (int)Math.Min(RoundSeconds, left), print: Print);
            }
        }
        catch (Exception e)
        {
            failure = e.Message;
            throw;
        }
        finally
        {
            Report(DateTime.UtcNow - started, cases, reordered, aliasNamed, loadErrors, oasdiffCrashes, fired, promoted, failure);
        }
    }

    private void Report(TimeSpan took, int cases, int reordered, int aliasNamed, int loadErrors, int oasdiffCrashes, ConcurrentDictionary<string, int> fired,
        ConcurrentDictionary<string, string> promoted, string? failure)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("## Property-based comparison with oasdiff\n\n");
        sb.Append(inv, $"- Cases: **{cases}** in {took.TotalSeconds:F0} s, 3 command lines each; same output up to order (oasdiff's nondeterminism): {reordered}\n");
        sb.Append(inv, $"- Command lines that differ only in which name of an aliased schema (`Item: {{$ref: Error}}`) is the shared schema (Go map order): {aliasNamed}\n");
        sb.Append(inv, $"- Cases neither tool could load (same exit code): {loadErrors}\n");
        sb.Append(inv, $"- Command lines on which oasdiff crashed (OasDelta reported an error or a result instead): {oasdiffCrashes}\n");
        sb.Append(inv, $"- Rules fired by generated pairs: **{fired.Count}**; of those, not fired by any golden: **{promoted.Count}**\n");
        sb.Append(failure is null ? "- Mismatches: **none**\n" : "- Mismatch: **yes** (shrunk case below and in the artifact)\n");
        if (!promoted.IsEmpty)
        {
            sb.Append("\n| New rule | Times fired | Kept in |\n|---|---:|---|\n");
            foreach (var (id, dir) in promoted.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sb.Append(inv, $"| `{id}` | {fired.GetValueOrDefault(id)} | `{Path.GetRelativePath(Support.OutDir, dir).Replace('\\', '/')}` |\n");
        }
        if (failure is not null) sb.Append("\n<details><summary>Mismatch</summary>\n\n```\n").Append(Support.Clip(failure, 20000)).Append("\n```\n</details>\n");
        var text = sb.ToString();
        output.WriteLine(text);
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary) File.AppendAllText(summary, text);
        Directory.CreateDirectory(Support.OutDir);
        File.WriteAllText(Path.Combine(Support.OutDir, "summary.md"), text);
    }
}
