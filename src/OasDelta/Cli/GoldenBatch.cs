using System.Text;

namespace OasDelta.Cli;

/// <summary>
/// Test support, not a user feature: runs many golden comparisons inside one process. tools/check_golden.sh uses it to
/// check the NativeAOT binary against tests/golden without starting ~10k processes, which takes half an hour on some
/// CI runners. Active only when the OASDELTA_GOLDEN_BATCH environment variable names a case list; otherwise inert.
///
/// Case list: one case per line, tab-separated: expected exit code, golden file, kind ("-", "unordered" or "#N", see
/// tests/golden-known.txt), then the command-line arguments. Paths are relative to the working directory. Each case
/// runs through <see cref="Cli.Run"/>, exactly as Main does, and its stdout is compared with the golden file byte for
/// byte. Prints one line per failure and a final "batch: pass=N fail=M"; exits 0 only if nothing failed.
/// </summary>
public static class GoldenBatch
{
    public const string Variable = "OASDELTA_GOLDEN_BATCH";

    public static int Run(string caseList, TextWriter output)
    {
        var utf8 = new UTF8Encoding(false);
        int pass = 0, fail = 0;
        foreach (var line in File.ReadLines(caseList))
        {
            if (line.Length == 0) continue;
            var f = line.Split('\t');
            int expectedCode = int.Parse(f[0], System.Globalization.CultureInfo.InvariantCulture);
            string golden = f[1], kind = f[2];
            string[] args = f[3..];

            var stdout = new StringWriter { NewLine = "\n" };
            var stderr = new StringWriter { NewLine = "\n" };
            int code;
            try { code = Cli.Run(args, stdout, stderr); }
            catch (Exception e) { stdout.Flush(); stderr.WriteLine("Error: " + e.Message); code = 100; }

            byte[] actual = utf8.GetBytes(stdout.ToString()), expected = File.ReadAllBytes(golden);
            bool same = code == expectedCode && actual.AsSpan().SequenceEqual(expected);
            string what = string.Join(' ', args);
            if (same)
            {
                if (kind.StartsWith('#')) { fail++; output.WriteLine($"NOW MATCHES {what}: remove it from tests/golden-known.txt ({kind})"); }
                else pass++;
            }
            else if (kind == "unordered" && code == expectedCode && SortedLines(utf8.GetString(actual)) == SortedLines(utf8.GetString(expected))) pass++;
            else if (kind.StartsWith('#')) pass++; // a tracked bug: expected to differ
            else { fail++; output.WriteLine($"FAIL {what} (exit {code}, want {expectedCode}) vs {golden}"); }
        }
        output.WriteLine($"batch: pass={pass} fail={fail}");
        return fail == 0 ? 0 : 1;
    }

    private static string SortedLines(string s) => string.Join('\n', s.Split('\n').Order(StringComparer.Ordinal));
}
