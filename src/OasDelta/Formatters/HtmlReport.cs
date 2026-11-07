using System.Text;
using Markdig;
using Markdig.Extensions.AutoLinks;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace OasDelta.Formatters;

/// <summary>report.GetHTMLReportAsString: the text report rendered as GitHub-flavoured markdown
/// (goldmark with GFM, auto heading ids and hard wraps). Markdig is configured to match that output.</summary>
public static class HtmlReport
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .UseTaskLists()
        .UseAutoLinks(new AutoLinkOptions { OpenInNewWindow = false })
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    public static string Render(string markdown)
    {
        var doc = Markdown.Parse(markdown, Pipeline);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in doc.Descendants<HeadingBlock>())
            h.GetAttributes().Id = HeadingId(LastLineSource(markdown, h), ids, used);
        var sw = new StringWriter { NewLine = "\n" };
        var renderer = new HtmlRenderer(sw);
        Pipeline.Setup(renderer);
        renderer.ObjectRenderers.ReplaceOrAdd<Markdig.Renderers.Html.Inlines.HtmlInlineRenderer>(new OmittedHtmlInline());
        renderer.ObjectRenderers.ReplaceOrAdd<Markdig.Renderers.Html.HtmlBlockRenderer>(new OmittedHtmlBlock());
        renderer.ObjectRenderers.ReplaceOrAdd<Markdig.Renderers.Html.Inlines.LineBreakInlineRenderer>(new GoldmarkLineBreak());
        renderer.ObjectRenderers.ReplaceOrAdd<Markdig.Renderers.Html.ThematicBreakRenderer>(new GoldmarkThematicBreak());
        renderer.Render(doc);
        return sw.ToString();
    }

    /// <summary>goldmark takes the id from the raw source of the heading's last content line.</summary>
    private static string LastLineSource(string md, HeadingBlock h)
    {
        var raw = md.Substring(h.Span.Start, Math.Min(h.Span.Length, md.Length - h.Span.Start));
        var lines = raw.Split('\n');
        if (h.IsSetext)
        {
            // drop the underline; the last remaining line is the last content line
            int end = lines.Length - 1;
            while (end > 0 && lines[end].Trim().Length == 0) end--;
            return end > 0 ? lines[end - 1] : lines[0];
        }
        var line = lines[0].TrimStart();
        line = line.TrimStart('#');
        var trimmed = line.TrimEnd();
        int k = trimmed.Length;
        while (k > 0 && trimmed[k - 1] == '#') k--;
        if (k < trimmed.Length && (k == 0 || trimmed[k - 1] is ' ' or '\t')) line = trimmed[..k];
        return line;
    }

    private static string InlineText(ContainerInline? c)
    {
        var sb = new StringBuilder();
        void Walk(Inline? i)
        {
            for (; i is not null; i = i.NextSibling)
            {
                switch (i)
                {
                    case LiteralInline l: sb.Append(l.Content.ToString()); break;
                    case CodeInline code: sb.Append(code.Content); break;
                    case ContainerInline ci: Walk(ci.FirstChild); break;
                }
            }
        }
        Walk(c?.FirstChild);
        return sb.ToString();
    }

    /// <summary>goldmark parser ids.Generate: ASCII alphanumerics lowercased, space/-/_ become '-', other bytes dropped.</summary>
    private static string HeadingId(string text, Dictionary<string, int> n, HashSet<string> used)
    {
        var sb = new StringBuilder();
        foreach (char ch in text.Trim())
        {
            if (ch > 0x7f) continue;
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch is ' ' or '\t' or '\n' or '\r' or '\f' or '\v' or '-' or '_') sb.Append('-');
        }
        var key = sb.Length == 0 ? "heading" : sb.ToString();
        if (used.Add(key)) return key;
        for (int i = n.GetValueOrDefault(key) + 1; ; i++)
        {
            var candidate = key + "-" + i;
            if (used.Add(candidate)) { n[key] = i; return candidate; }
        }
    }

    // goldmark renders raw HTML only in "unsafe" mode; otherwise it leaves a marker comment
    private sealed class OmittedHtmlInline : HtmlObjectRenderer<HtmlInline>
    {
        protected override void Write(HtmlRenderer renderer, HtmlInline obj) => renderer.Write("<!-- raw HTML omitted -->");
    }

    private sealed class OmittedHtmlBlock : HtmlObjectRenderer<HtmlBlock>
    {
        protected override void Write(HtmlRenderer renderer, HtmlBlock obj) => renderer.EnsureLine().WriteLine("<!-- raw HTML omitted -->");
    }

    private sealed class GoldmarkThematicBreak : HtmlObjectRenderer<ThematicBreakBlock>
    {
        protected override void Write(HtmlRenderer renderer, ThematicBreakBlock obj) => renderer.EnsureLine().WriteLine("<hr>");
    }

    private sealed class GoldmarkLineBreak : HtmlObjectRenderer<LineBreakInline>
    {
        protected override void Write(HtmlRenderer renderer, LineBreakInline obj)
        {
            if (renderer.IsLastInContainer) return;
            renderer.WriteLine("<br>");
        }
    }
}
