using CsCheck;
using OasDelta.Checker;
using Xunit;
using Xunit.Abstractions;

namespace OasDelta.Tests.Properties;

/// <summary>Runs only when OASDIFF is an oasdiff binary and OASDELTA_PROMOTE_RULES is the directory to write fixtures
/// to (tests/fixtures/properties/rules).</summary>
public sealed class PromoteFactAttribute : FactAttribute
{
    public PromoteFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OASDIFF") is not { Length: > 0 } || Environment.GetEnvironmentVariable("OASDELTA_PROMOTE_RULES") is not { Length: > 0 })
            Skip = "set OASDIFF to an oasdiff binary and OASDELTA_PROMOTE_RULES to the fixture directory to promote rules";
    }
}

/// <summary>
/// Writes a minimal fixture pair for each checker rule that no golden fires (#40). For the rules still missing, CsCheck
/// samples generated pairs, each also reversed, until `changelog -f json` reports one of them, then shrinks that pair
/// for the rest of the search's time (OASDELTA_PROMOTE_SECONDS, default 120). If oasdiff prints the same changelog, the smallest pair
/// found becomes `&lt;rule&gt;_base` / `&lt;rule&gt;_revision` in the fixture directory, and every rule it fires counts
/// as covered; otherwise it is kept under TestResults/properties/promotion-mismatch. This repeats until a search finds
/// no rule. Then generate the goldens of the new pairs with oasdiff and raise tests/rule-coverage-floor.txt.
///
/// promoted.txt in the fixture directory lists the rules done, so a later run continues where this one stopped.
/// </summary>
public class RulePromotion(ITestOutputHelper output)
{
    [PromoteFact]
    public void PromoteRules()
    {
        var oasdiff = new External(Environment.GetEnvironmentVariable("OASDIFF")!);
        var dir = Path.GetFullPath(Environment.GetEnvironmentVariable("OASDELTA_PROMOTE_RULES")!, Support.Root); // relative: to the repository
        Directory.CreateDirectory(dir);
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("OASDELTA_PROMOTE_SECONDS"), out var s) ? s : 120;
        var log = Path.Combine(dir, "promoted.txt");
        Directory.CreateDirectory(Support.OutDir);
        void Say(string line)
        {
            output.WriteLine(line);
            File.AppendAllText(Path.Combine(Support.OutDir, "promotion.log"), $"{DateTime.Now:HH:mm:ss} {line}\n");
        }

        var targets = Rules.All().Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        targets.ExceptWith(Support.GoldenRuleIds());
        if (File.Exists(log)) targets.ExceptWith(File.ReadAllLines(log).SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)));
        Say($"{targets.Count} rules to promote");

        while (targets.Count > 0)
        {
            SpecPair? best = null;
            HashSet<string>? bestFired = null;
            int bestSize = int.MaxValue;
            var gate = new Lock();
            try
            {
                SpecPair.Gen.Sample(generated =>
                {
                    // also the pair reversed: the rule for undoing an edit (unset, removed, became not ...) fires
                    // there, so the generator need not write every starting state
                    // tools/pairs.sh pairs <name>_base.<ext> with <name>_revision.<ext>: both files in the base's encoding
                    foreach (var p in new[] { generated.SameEncoding(), generated.Reversed().SameEncoding() })
                        Check(p);
                }, time: seconds);

                void Check(SpecPair p)
                {
                    if (Support.HasDanglingMapping(p.Revision)) return; // a known difference (OracleProperties)
                    using var f = CaseFiles.Write(p);
                    var got = InProcess.Instance.Run("changelog", "-f", "json", f.Base, f.Revision);
                    if (got.Code != 0) return;
                    var fired = Support.RuleIds(got.Out).Where(targets.Contains).ToHashSet(StringComparer.Ordinal);
                    if (fired.Count == 0) return;
                    int size = SpecPair.Serialize(p.Base, false).Length + SpecPair.Serialize(p.Revision, false).Length;
                    lock (gate)
                        if (size < bestSize) (best, bestFired, bestSize) = (p, fired, size);
                    throw new PropertyException("fires " + string.Join(", ", fired));
                }
            }
            catch (CsCheckException) { } // a pair fires a rule: the smallest one CsCheck tried is in best
            if (best is null || bestFired is null) break;

            using var files = CaseFiles.Write(best);
            var mine = InProcess.Instance.Run("changelog", "-f", "json", files.Base, files.Revision);
            var theirs = oasdiff.Run("changelog", "-f", "json", files.Base, files.Revision);
            var all = Support.RuleIds(mine.Out).Where(targets.Contains).ToHashSet(StringComparer.Ordinal);
            if (theirs.Crashed || theirs.Code != mine.Code || Support.Compare(theirs.Out, mine.Out) == Support.Agreement.Different)
            {
                var kept = Support.Keep("promotion-mismatch", best, $"fires {string.Join(", ", all)}; oasdiff's changelog differs");
                Say($"MISMATCH {string.Join(" ", all)}: kept in {kept}");
                targets.ExceptWith(all); // looked at by hand, not promoted
                continue;
            }
            var name = all.Order(StringComparer.Ordinal).First();
            string Ext(bool json) => json ? ".json" : ".yaml";
            string Text(Map doc, bool json) => json ? SpecPair.Serialize(doc, true)
                : $"# Fires {string.Join(", ", all.Order(StringComparer.Ordinal))} (tests/OasDelta.Tests/Properties/RulePromotion.cs)\n" + SpecPair.Serialize(doc, false);
            File.WriteAllText(Path.Combine(dir, name + "_base" + Ext(best.BaseJson)), Text(best.Base, best.BaseJson));
            File.WriteAllText(Path.Combine(dir, name + "_revision" + Ext(best.BaseJson)), Text(best.Revision, best.BaseJson));
            File.AppendAllText(log, $"{name} {string.Join(" ", all.Order(StringComparer.Ordinal))}\n");
            targets.ExceptWith(all);
            Say($"{name}: {all.Count} rules, {bestSize} bytes; {targets.Count} left");
        }
        Say($"not reached ({targets.Count}): {string.Join(" ", targets.Order(StringComparer.Ordinal))}");
    }
}
