using OasDelta.Checker;

namespace OasDelta.Formatters;

internal sealed class ChangeEntry(bool isBreaking, string text, string comment) : ITemplateObject
{
    public bool TryGet(string name, out object? value)
    {
        value = name switch { "IsBreaking" => isBreaking, "Text" => text, "Comment" => comment, _ => null };
        return name is "IsBreaking" or "Text" or "Comment";
    }
}

internal sealed class ChangeGroup(string section, string path, string operation) : ITemplateObject
{
    public readonly string Section = section, Path = path, Operation = operation;
    public bool TryGet(string name, out object? value)
    {
        value = name switch { "Section" => Section, "Path" => Path, "Operation" => Operation, _ => null };
        return name is "Section" or "Path" or "Operation";
    }
}

internal sealed class GroupEntry(ChangeGroup group, List<ChangeEntry> changes) : ITemplateObject
{
    public readonly ChangeGroup Group = group;
    public readonly List<ChangeEntry> Changes = changes;
    public bool TryGet(string name, out object? value)
    {
        value = name switch { "Group" => Group, "Changes" => Changes, _ => null };
        return name is "Group" or "Changes";
    }
}

internal sealed class TemplateData(List<GroupEntry> groups, string baseVersion, string revisionVersion, bool diffEmpty, bool isBreaking) : ITemplateObject
{
    public bool TryGet(string name, out object? value)
    {
        switch (name)
        {
            case "GroupedChanges" or "APIChanges": value = groups; return true;
            case "BaseVersion": value = baseVersion; return true;
            case "RevisionVersion": value = revisionVersion; return true;
            case "DiffEmpty": value = diffEmpty; return true;
            case "IsBreaking": value = isBreaking; return true;
            case "GetVersionTitle": value = baseVersion == "" || revisionVersion == "" ? "" : $"{baseVersion} vs. {revisionVersion}"; return true;
        }
        value = null;
        return false;
    }
}

internal static class TemplateRenderer
{
    public static List<GroupEntry> Group(List<Change> changes, Localizer l)
    {
        var groups = new List<GroupEntry>();
        var index = new Dictionary<(string, string, string), GroupEntry>();
        foreach (var c in changes)
        {
            var key = c.Kind == ChangeSection.Api ? (c.Section, c.Path, c.Operation) : (c.Section, "", "");
            if (!index.TryGetValue(key, out var g)) { index[key] = g = new GroupEntry(new ChangeGroup(key.Item1, key.Item2, key.Item3), []); groups.Add(g); }
            g.Changes.Add(new ChangeEntry(c.IsBreaking, c.GetUncolorizedText(l), c.GetComment(l)));
        }
        return groups;
    }

    private static Dictionary<string, Func<object?[], object?>> Funcs() => new(StringComparer.Ordinal)
    {
        ["pathGroups"] = a => ((List<GroupEntry>)a[0]!).Where(g => g.Group.Path != "")
            .OrderBy(g => g.Group.Path, StringComparer.Ordinal).ThenBy(g => g.Group.Operation, StringComparer.Ordinal).ToList(),
        ["sectionGroups"] = a => ((List<GroupEntry>)a[0]!).Where(g => g.Group.Path == "").OrderBy(g => g.Group.Section, StringComparer.Ordinal).ToList(),
        ["capitalize"] = a => a[0] is string { Length: > 0 } s ? char.ToUpperInvariant(s[0]) + s[1..] : a[0],
    };

    public static string Render(string template, bool html, List<Change> changes, Localizer l, RenderOpts opts)
    {
        var t = new GoTemplate(html ? HtmlTemplateText.ElideComments(template) : template, Funcs(), html);
        return t.Execute(new TemplateData(Group(changes, l), opts.BaseVersion, opts.RevisionVersion, opts.DiffEmpty, opts.IsBreaking));
    }
}

public static class Markup
{
    public static string Changelog(List<Change> changes, Localizer l, RenderOpts opts) =>
        TemplateRenderer.Render(opts.TemplatePath != "" ? File.ReadAllText(opts.TemplatePath) : Templates.ChangelogMarkdown, false, changes, l, opts);
}

public static class Html
{
    public static string Changelog(List<Change> changes, Localizer l, RenderOpts opts) =>
        TemplateRenderer.Render(opts.TemplatePath != "" ? File.ReadAllText(opts.TemplatePath) : Templates.ChangelogHtml, true, changes, l, opts);
}

/// <summary>html/template drops comments from template text: HTML comments vanish, a CSS block comment becomes one space,
/// a JS block comment becomes a space (or a newline if it spans lines) and a JS line comment vanishes. Actions are left alone.</summary>
internal static class HtmlTemplateText
{
    public static string ElideComments(string t)
    {
        var sb = new System.Text.StringBuilder(t.Length);
        string raw = "";   // "style" / "script" while inside such an element
        int i = 0;
        while (i < t.Length)
        {
            if (t.AsSpan(i).StartsWith("{{"))
            {
                int end = t.IndexOf("}}", i + 2, StringComparison.Ordinal);
                end = end < 0 ? t.Length : end + 2;
                sb.Append(t, i, end - i);
                i = end;
                continue;
            }
            if (raw.Length > 0 && t.AsSpan(i).StartsWith("</" + raw, StringComparison.OrdinalIgnoreCase)) { raw = ""; sb.Append(t[i++]); continue; }
            if (raw.Length == 0)
            {
                if (t.AsSpan(i).StartsWith("<!--"))
                {
                    int end = t.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    i = end < 0 ? t.Length : end + 3;
                    continue;
                }
                foreach (var tag in new[] { "style", "script" })
                    if (t.AsSpan(i).StartsWith("<" + tag, StringComparison.OrdinalIgnoreCase) && i + tag.Length + 1 < t.Length && t[i + tag.Length + 1] is '>' or ' ' or '\t' or '\n' or '\r' or '/')
                    {
                        int gt = t.IndexOf('>', i);
                        if (gt < 0) break;
                        sb.Append(t, i, gt + 1 - i);
                        i = gt + 1;
                        raw = tag;
                        goto next;
                    }
                sb.Append(t[i++]);
                continue;
            }
            char c = t[i];
            if (c is '"' or '\'')
            {
                int j = i + 1;
                while (j < t.Length && t[j] != c && t[j] != '\n') { if (t[j] == '\\') j++; j++; }
                j = Math.Min(j + 1, t.Length);
                sb.Append(t, i, j - i);
                i = j;
                continue;
            }
            if (t.AsSpan(i).StartsWith("/*"))
            {
                int end = t.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? t.Length : end + 2;
                bool multiLine = t.AsSpan(i, end - i).IndexOfAny('\n', '\r') >= 0;
                sb.Append(raw == "script" && multiLine ? '\n' : ' ');
                i = end;
                continue;
            }
            if (raw == "script" && t.AsSpan(i).StartsWith("//"))
            {
                int end = t.IndexOfAny(['\n', '\r'], i);
                i = end < 0 ? t.Length : end;
                continue;
            }
            sb.Append(t[i++]);
            next:;
        }
        return sb.ToString();
    }
}
