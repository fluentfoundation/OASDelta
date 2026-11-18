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
/// A mismatch doesn't end the run. Each has a kind (<see cref="Support.MismatchKind"/>: a different exit code, only
/// source locations, or other output; per command and format). The first mismatch of a kind fails its round, so
/// CsCheck shrinks it for the rest of that round. After that, that kind is only counted and kept, and sampling goes
/// on to look for other kinds: a known open mismatch can't hide a new one. At the end, each kind is shrunk further
/// from its seed (OASDELTA_SHRINK_TIME seconds, default 120, for up to <see cref="MaxShrunk"/> kinds), and the
/// property fails if anything mismatched.
///
/// Also records which checker rules the generated pairs make fire. A rule that no golden makes fire, reached by a
/// pair on which both tools agree, is a promotion candidate: the pair is kept under properties/new-rules/&lt;rule&gt;
/// to be added to tests/fixtures (raising tests/rule-coverage-floor.txt).
///
/// Run by the Properties workflow (nightly, with a time budget in seconds: CsCheck_Time, sampled in one-minute
/// rounds); locally:
/// OASDIFF=path/to/oasdiff CsCheck_Time=60 dotnet test --filter Category=Oracle
/// With CsCheck_Seed set, it replays that seed and fails on any mismatch.
/// </summary>
[Trait("Category", "Oracle")]
public class OracleProperties(ITestOutputHelper output)
{
    private static readonly Lazy<string[][]> Commands = new(() => [.. GoldenTests.Commands().Values]);

    private static readonly Gen<int[]> CommandChoice = Gen.Int[0, 1000].Array[3];

    private const int RoundSeconds = 60;
    private const int MaxShrunk = 5;
    private const int KeptPerKind = 3;

    /// <summary>One kind of mismatch: how often it was seen after it was found, and its smallest case so far. Shrunk:
    /// shrinking from its seed found a smaller case.</summary>
    private sealed class Mismatch(string kind)
    {
        public string Kind { get; } = kind;
        public int SeenAgain;
        public int Kept;
        public string? Seed { get; set; }
        public string? Case { get; set; }
        public bool Shrunk { get; set; }
    }

    private sealed class MismatchException(string kind, string message) : Exception(message)
    {
        public string Kind { get; } = kind;
    }

    [OracleFact]
    public void MatchesOasdiff()
    {
        var oasdiff = new External(Environment.GetEnvironmentVariable("OASDIFF")!);
        var golden = Support.GoldenRuleIds();
        var fired = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var promoted = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var mismatches = new ConcurrentDictionary<string, Mismatch>(StringComparer.Ordinal);
        int cases = 0, reordered = 0, loadErrors = 0, oasdiffCrashes = 0, aliasNamed = 0, danglingMappings = 0;
        string? failure = null;
        var started = DateTime.UtcNow;
        // which kinds of mismatch fail the current Sample (and so are shrunk); the others are counted and kept
        Func<string, bool> fails = _ => true;
        try
        {
            void Check(SpecPair p, int[] choice)
            {
                // a known difference (compatibility.md): oasdiff fails to load a revision whose discriminator mapping
                // points at nothing, as its loader takes the revision for an external document
                if (Support.HasDanglingMapping(p.Revision)) { Interlocked.Increment(ref danglingMappings); return; }
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
                    {
                        var kind = Support.MismatchKind(cmd, got, want, s => Support.WithCanonicalSharedSchemaNames(s, aliases.Value));
                        var what = $"{kind}\n{string.Join(' ', cmd)}: exit {got.Code}, oasdiff {want.Code}\n--- oasdiff\n{Support.Clip(want.Out)}\n--- oasdelta\n{Support.Clip(got.Out)}";
                        if (fails(kind)) throw new MismatchException(kind, what);
                        var m = mismatches.GetOrAdd(kind, k => new Mismatch(k));
                        Interlocked.Increment(ref m.SeenAgain);
                        if (Interlocked.Increment(ref m.Kept) <= KeptPerKind)
                        {
                            var kept = Support.Keep("mismatch-again", p, what);
                            // first seen while another kind was being shrunk: reported as it is, with no seed
                            lock (m) m.Case ??= $"(found while another kind was being shrunk; not shrunk)\n{what}\n{p}\nkept in {kept}";
                        }
                        continue;
                    }
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

            // a failed Sample: its (shrunk) case, recorded under its kind
            void Found(CsCheckException e, MismatchException m)
            {
                var r = mismatches.GetOrAdd(m.Kind, k => new Mismatch(k));
                r.Seed = Support.SeedOf(e.Message) ?? r.Seed;
                r.Case = e.Message;
            }

            var gen = Gen.Select(SpecPair.Gen, CommandChoice);
            if (Environment.GetEnvironmentVariable("CsCheck_Seed") is { Length: > 0 })
                gen.Sample(Check, print: Print); // replay a reported seed: any mismatch fails
            else
            {
                // Rounds of at most a minute. A new kind of mismatch fails its round, shrunk for the rest of it; from
                // then on that kind is counted, not failed, so the following rounds can find other kinds. Anything
                // but a mismatch (OasDelta crashed) ends the run.
                fails = kind => !mismatches.TryGetValue(kind, out var m) || m.Case is null;
                int budget = int.TryParse(Environment.GetEnvironmentVariable("CsCheck_Time"), out var s) && s > 0 ? s : 60;
                double left;
                while ((left = budget - (DateTime.UtcNow - started).TotalSeconds) >= 1)
                {
                    try { gen.Sample(Check, time: (int)Math.Min(RoundSeconds, left), print: Print); }
                    catch (CsCheckException e) when (e.InnerException is MismatchException m) { Found(e, m); }
                }

                // shrink each kind further, starting from its seed; only that kind fails meanwhile
                int shrink = int.TryParse(Environment.GetEnvironmentVariable("OASDELTA_SHRINK_TIME"), out var t) && t >= 0 ? t : 120;
                foreach (var m in mismatches.Values.Where(m => m.Seed is not null).OrderBy(m => m.Kind, StringComparer.Ordinal).Take(shrink > 0 ? MaxShrunk : 0).ToList())
                {
                    fails = kind => kind == m.Kind;
                    try { gen.Sample(Check, seed: m.Seed, time: shrink, print: Print); }
                    catch (CsCheckException e) when (e.InnerException is MismatchException x && x.Kind == m.Kind)
                    {
                        var before = m.Seed;
                        Found(e, x);
                        m.Shrunk = m.Seed != before; // CsCheck reports the seed's own case again when it finds no smaller one
                    }
                }
                fails = _ => false;

                if (!mismatches.IsEmpty)
                    throw new PropertyException($"oasdelta and oasdiff differ: {mismatches.Count} kind(s) of mismatch\n" +
                        string.Join('\n', mismatches.Keys.Order(StringComparer.Ordinal).Select(k => "- " + k)));
            }
        }
        catch (Exception e)
        {
            failure = e.Message;
            throw;
        }
        finally
        {
            Report(DateTime.UtcNow - started, cases, reordered, aliasNamed, loadErrors, oasdiffCrashes, danglingMappings, fired, promoted, mismatches, failure);
        }
    }

    private void Report(TimeSpan took, int cases, int reordered, int aliasNamed, int loadErrors, int oasdiffCrashes, int danglingMappings, ConcurrentDictionary<string, int> fired,
        ConcurrentDictionary<string, string> promoted, ConcurrentDictionary<string, Mismatch> mismatches, string? failure)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("## Property-based comparison with oasdiff\n\n");
        sb.Append(inv, $"- Cases: **{cases}** in {took.TotalSeconds:F0} s, 3 command lines each; same output up to order (oasdiff's nondeterminism): {reordered}\n");
        sb.Append(inv, $"- Command lines that differ only in which name of an aliased schema (`Item: {{$ref: Error}}`) is the shared schema (Go map order): {aliasNamed}\n");
        sb.Append(inv, $"- Cases neither tool could load (same exit code): {loadErrors}\n");
        sb.Append(inv, $"- Command lines on which oasdiff crashed (OasDelta reported an error or a result instead): {oasdiffCrashes}\n");
        sb.Append(inv, $"- Cases skipped because the revision's discriminator mapping points at nothing (a known difference): {danglingMappings}\n");
        sb.Append(inv, $"- Rules fired by generated pairs: **{fired.Count}**; of those, not fired by any golden: **{promoted.Count}**\n");
        var found = mismatches.Values.Where(m => m.Case is not null).OrderBy(m => m.Kind, StringComparer.Ordinal).ToList();
        sb.Append(found.Count == 0 && failure is null ? "- Mismatches: **none**\n"
            : found.Count == 0 ? "- Mismatch: **yes** (below)\n"
            : $"- Mismatches: **{found.Count} kind(s)** (each shrunk case below and in the artifact)\n");
        if (found.Count > 0)
        {
            sb.Append("\n| Kind of mismatch | Command lines after it was found | Seed | Shrunk from its seed |\n|---|---:|---|---|\n");
            foreach (var m in found)
                sb.Append(inv, $"| {m.Kind} | {m.SeenAgain} | {(m.Seed is null ? "none" : $"`{m.Seed}`")} | {(m.Shrunk ? "yes" : "no")} |\n");
        }
        if (!promoted.IsEmpty)
        {
            sb.Append("\n| New rule | Times fired | Kept in |\n|---|---:|---|\n");
            foreach (var (id, dir) in promoted.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sb.Append(inv, $"| `{id}` | {fired.GetValueOrDefault(id)} | `{Path.GetRelativePath(Support.OutDir, dir).Replace('\\', '/')}` |\n");
        }
        foreach (var m in found)
            sb.Append("\n<details><summary>").Append(m.Kind).Append("</summary>\n\n```\n").Append(Support.Clip(m.Case!, 12000)).Append("\n```\n</details>\n");
        if (found.Count == 0 && failure is not null)
            sb.Append("\n<details><summary>Mismatch</summary>\n\n```\n").Append(Support.Clip(failure, 20000)).Append("\n```\n</details>\n");
        var text = sb.ToString();
        output.WriteLine(text);
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary) File.AppendAllText(summary, text);
        Directory.CreateDirectory(Support.OutDir);
        File.WriteAllText(Path.Combine(Support.OutDir, "summary.md"), text);
    }
}
