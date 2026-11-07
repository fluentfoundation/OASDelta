using System.Text;
using OasDelta.Checker;
using OasDelta.Diff;
using OasDelta.Yaml;

namespace OasDelta.Formatters;

public sealed class RenderOpts
{
    public ColorMode ColorMode = ColorMode.Auto;
    public bool DiffEmpty, IsBreaking, WrapInObject;
    public string TemplatePath = "";
    public string BaseVersion = "", RevisionVersion = "";
}

/// <summary>formatters.Change: the serialized form of a change.</summary>
public sealed class ChangeOut(Change c, Localizer l) : IOutput
{
    public object? ToOutput()
    {
        var m = new OMap();
        m.OptStr("id", c.Id);
        m.OptStr("text", c.GetUncolorizedText(l));
        m.OptStr("comment", c.GetComment(l));
        if (c.Disclaimers.Count > 0) m.Add("disclaimers", c.Disclaimers.Select(d => d.Name()).ToList());
        m.Opt("sharedSchema", c.Kind == ChangeSection.Api ? c.GetSharedSchema() : null);
        m.Add("level", (long)c.Level);
        m.OptStr("operation", c.Operation);
        m.OptStr("operationId", c.OperationId);
        m.OptStr("path", c.Path);
        m.OptStr("section", c.Section);
        if (c.Attributes is { Count: > 0 }) m.Add("attributes", c.Attributes);
        m.Opt("baseSource", c.BaseSource);
        m.Opt("revisionSource", c.RevisionSource);
        m.OptStr("fingerprint", c.Fingerprint());
        return m;
    }
}

public static class ChangelogRenderer
{
    public static readonly string[] Formats = ["yaml", "json", "text", "markup", "markdown", "singleline", "html", "githubactions", "junit"];

    public static string Title(List<Change> changes, Localizer l, ColorMode mode)
    {
        int err = changes.Count(c => c.Level == Level.ERR), warn = changes.Count(c => c.Level == Level.WARN), info = changes.Count(c => c.Level == Level.INFO);
        bool color = Color.Enabled(mode);
        string N(Level lv) => color ? lv.Pretty() : lv.Name();
        return l.Get("total-changes", [changes.Count, err, N(Level.ERR), warn, N(Level.WARN), info, N(Level.INFO)]);
    }

    private static object? Structured(List<Change> changes, Localizer l, RenderOpts opts)
    {
        var list = changes.Select(c => (object?)new ChangeOut(c, l)).ToList();
        if (!opts.WrapInObject) return list;
        return new OMap { { "changes", list }, { "diff_empty", opts.DiffEmpty } };
    }

    public static string Render(string format, List<Change> changes, Localizer l, RenderOpts opts) => format switch
    {
        "json" => changes.Count == 0 && !opts.WrapInObject ? "[]" : JsonEmitter.Emit(Structured(changes, l, opts)),
        "yaml" => changes.Count == 0 && !opts.WrapInObject ? "[]\n" : YamlEmitter.Emit(Structured(changes, l, opts)).TrimEnd('\n') + "\n",
        "text" => Text(changes, l, opts, false),
        "singleline" => Text(changes, l, opts, true),
        "markup" or "markdown" => Markup.Changelog(changes, l, opts),
        "html" => Html.Changelog(changes, l, opts),
        "githubactions" => GitHubActions.Changelog(changes, l),
        "junit" => JUnit.Changelog(changes, l),
        _ => throw new ArgumentException("unsupported format: " + format),
    };

    private static string Text(List<Change> changes, Localizer l, RenderOpts opts, bool singleLine)
    {
        if (changes.Count == 0)
        {
            if (opts.DiffEmpty) return "No changes detected";
            var sep = singleLine ? " " : "\n";
            return opts.IsBreaking
                ? "No breaking changes to report, but the specs are different." + sep + "Run 'oasdiff diff' to see structural differences."
                : "No changes to report, but the specs are different." + sep + "Run 'oasdiff diff' to see structural differences.";
        }
        var sb = new StringBuilder(Title(changes, l, opts.ColorMode));
        foreach (var c in changes) sb.Append(singleLine ? c.SingleLineError(l, opts.ColorMode) : c.MultiLineError(l, opts.ColorMode)).Append("\n\n");
        return sb.ToString();
    }
}
