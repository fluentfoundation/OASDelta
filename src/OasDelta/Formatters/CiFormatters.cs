using System.Text;
using OasDelta.Checker;

namespace OasDelta.Formatters;

public static class GitHubActions
{
    private static string Severity(Level l) => l switch { Level.ERR => "error", Level.WARN => "warning", Level.INFO => "notice", _ => "" };

    private static string EscapeData(string s) => s.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
    private static string EscapeProperty(string s) => EscapeData(s).Replace(":", "%3A").Replace(",", "%2C");
    private static bool IsHttp(string f) => f.StartsWith("http://", StringComparison.Ordinal) || f.StartsWith("https://", StringComparison.Ordinal);

    public static string Changelog(List<Change> changes, Localizer l)
    {
        WriteJobOutputs(changes);
        var sb = new StringBuilder();
        foreach (var c in changes)
        {
            var ps = new List<string> { "title=" + EscapeProperty(c.Id) };
            if (c.RevisionSource is { } rev && rev.File != "" && !IsHttp(rev.File))
            {
                ps.Add("file=" + EscapeProperty(rev.File));
                if (rev.Line != 0) ps.Add("line=" + rev.Line);
                if (rev.Column != 0) ps.Add("col=" + rev.Column);
            }
            else if (c.GetSourceFile() is { Length: > 0 } sf) ps.Add("file=" + EscapeProperty(sf));
            sb.Append("::").Append(Severity(c.Level)).Append(' ').Append(string.Join(",", ps)).Append("::")
              .Append(EscapeData($"in API {c.Operation} {c.Path} {c.GetUncolorizedText(l)}")).Append('\n');
        }
        return sb.ToString();
    }

    private static void WriteJobOutputs(List<Change> changes)
    {
        var file = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
        if (string.IsNullOrEmpty(file)) return;
        var text = $"error_count={changes.Count(c => c.Level == Level.ERR)}\nwarning_count={changes.Count(c => c.Level == Level.WARN)}\ninfo_count={changes.Count(c => c.Level == Level.INFO)}\n";
        File.AppendAllText(file, text);
    }
}

public static class JUnit
{
    private static string Attr(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&#34;").Replace("'", "&#39;")
        .Replace("\t", "&#x9;").Replace("\n", "&#xA;").Replace("\r", "&#xD;");

    public static string Changelog(List<Change> changes, Localizer l)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<testsuites>\n");
        sb.Append($"  <testsuite package=\"com.oasdiff\" time=\"0\" tests=\"{changes.Count}\" errors=\"0\" failures=\"{changes.Count}\" name=\"OASDiff\">\n");
        foreach (var c in changes)
        {
            sb.Append($"    <testcase name=\"{Attr(c.Id)}\" classname=\"OASDiff\" time=\"0\">\n");
            sb.Append($"      <failure message=\"Breaking change detected\">{c.GetUncolorizedText(l)}</failure>\n");
            sb.Append("    </testcase>\n");
        }
        if (changes.Count == 0) sb.Append("    <testcase name=\"no breaking changes detected\" classname=\"OASDiff\" time=\"0\"></testcase>\n");
        sb.Append("  </testsuite>\n</testsuites>");
        return sb.ToString();
    }
}
