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
    private static readonly MarkdownPipeline Pipeline = BuildPipeline();

    private static MarkdownPipeline BuildPipeline()
    {
        var builder = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseAutoLinks(new AutoLinkOptions { OpenInNewWindow = false })
            .UseSoftlineBreakAsHardlineBreak();
        // Emphasis as goldmark does it (GoldmarkEmphasisParser), with GFM strikethrough: a run of one or two tildes
        // (~a~ as well as ~~a~~), rendered as <del> whatever its length. Markdig's own strikethrough takes only two, and
        // a run of three or more, which goldmark leaves as text, would give it two: TildeRunParser keeps that literal.
        var emphasis = new GoldmarkEmphasisParser();
        emphasis.EmphasisDescriptors.Add(new Markdig.Parsers.Inlines.EmphasisDescriptor('~', 1, 2, true));
        builder.InlineParsers.Replace<Markdig.Parsers.Inlines.EmphasisInlineParser>(emphasis);
        builder.InlineParsers.InsertBefore<GoldmarkEmphasisParser>(new TildeRunParser());
        // GFM task lists as goldmark has them, not Markdig's (UseTaskLists), which turns a [ ] anywhere in a list item
        // into a checkbox and renders it differently
        builder.InlineParsers.InsertBefore<Markdig.Parsers.Inlines.LinkInlineParser>(new GoldmarkTaskCheckBoxParser());
        return builder.Build();
    }

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
        renderer.ObjectRenderers.Add(new GoldmarkTaskCheckBoxRenderer());
        if (renderer.ObjectRenderers.FindExact<Markdig.Renderers.Html.Inlines.EmphasisInlineRenderer>() is { } emphasis)
        {
            var tag = emphasis.GetTag;
            emphasis.GetTag = e => e.DelimiterChar == '~' ? "del" : tag(e);
        }
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

    /// <summary>Markdig's emphasis parser with goldmark's flanking rules (parser.ScanDelimiter, CommonMark's). Markdig
    /// does not count - + − † ‡ as punctuation here (CharHelper.IsPunctuationException), so it opens emphasis in
    /// x*-y* and x~-y~ where goldmark, and the CommonMark spec, do not.</summary>
    private sealed class GoldmarkEmphasisParser : Markdig.Parsers.Inlines.EmphasisInlineParser
    {
        public override bool Match(Markdig.Parsers.InlineProcessor processor, ref Markdig.Helpers.StringSlice slice)
        {
            int start = slice.Start;
            var previous = processor.Inline;
            // strikethroughParser: no delimiter right after a tilde, even an escaped one (\~~a~~ is text); Markdig
            // allows it after a backslash
            if (slice.CurrentChar == '~' && start > 0 && slice.Text[start - 1] == '~') return false;
            if (!base.Match(processor, ref slice)) return false; // goldmark is never more lenient than Markdig
            if (processor.Inline is not EmphasisDelimiterInline delimiter) return true;
            char before = start > 0 ? slice.Text[start - 1] : '\n', after = slice.Start <= slice.End ? slice.Text[slice.Start] : '\n';
            Markdig.Helpers.CharHelper.CheckUnicodeCategory(before, out bool beforeSpace, out bool beforePunct);
            Markdig.Helpers.CharHelper.CheckUnicodeCategory(after, out bool afterSpace, out bool afterPunct);
            bool left = !afterSpace && (!afterPunct || beforeSpace || beforePunct);
            bool right = !beforeSpace && (!beforePunct || afterSpace || afterPunct);
            bool canOpen = left, canClose = right;
            if (delimiter.DelimiterChar == '_')
            {
                canOpen = left && (!right || beforePunct);
                canClose = right && (!left || afterPunct);
            }
            if (!canOpen && !canClose)
            {
                slice.Start = start;
                processor.Inline = previous;
                return false; // plain text
            }
            delimiter.Type = (canOpen ? DelimiterType.Open : 0) | (canClose ? DelimiterType.Close : 0);
            return true;
        }
    }

    /// <summary>goldmark's strikethroughParser: a run of three or more tildes is no delimiter, so it stays text.</summary>
    private sealed class TildeRunParser : Markdig.Parsers.InlineParser
    {
        public TildeRunParser() => OpeningCharacters = ['~'];

        public override bool Match(Markdig.Parsers.InlineProcessor processor, ref Markdig.Helpers.StringSlice slice)
        {
            int start = slice.Start, end = start;
            while (end <= slice.End && slice.Text[end] == '~') end++;
            if (end - start < 3) return false; // one or two: the emphasis parser's
            processor.Inline = new LiteralInline(new Markdig.Helpers.StringSlice(slice.Text, start, end - 1))
            {
                Span = new SourceSpan(processor.GetSourcePosition(start, out int line, out int column), processor.GetSourcePosition(end - 1)),
                Line = line,
                Column = column,
            };
            slice.Start = end;
            return true;
        }
    }

    /// <summary>goldmark's taskCheckBoxParser: [ ], [x] or [X] (then any whitespace, regexp ^\[([\sxX])\]\s*) only as
    /// the very start of the first paragraph of a list item.</summary>
    private sealed class GoldmarkTaskCheckBoxParser : Markdig.Parsers.InlineParser
    {
        public GoldmarkTaskCheckBoxParser() => OpeningCharacters = ['['];

        private static bool IsGoSpace(char c) => c is ' ' or '\t' or '\n' or '\f' or '\r'; // RE2's \s

        public override bool Match(Markdig.Parsers.InlineProcessor processor, ref Markdig.Helpers.StringSlice slice)
        {
            if (processor.Block is not ParagraphBlock paragraph || paragraph.Parent is not ListItemBlock item || item[0] != paragraph) return false;
            if (processor.Root?.FirstChild is not null) return false; // not at the start
            int start = slice.Start;
            if (start + 2 > slice.End || slice.Text[start + 2] != ']') return false;
            char value = slice.Text[start + 1];
            if (!IsGoSpace(value) && value is not ('x' or 'X')) return false;
            int end = start + 3;
            while (end <= slice.End && IsGoSpace(slice.Text[end])) end++;
            processor.Inline = new GoldmarkTaskCheckBox { Checked = value is 'x' or 'X', Span = new SourceSpan(start, end - 1) };
            slice.Start = end;
            return true;
        }
    }

    private sealed class GoldmarkTaskCheckBox : LeafInline
    {
        public bool Checked { get; init; }
    }

    private sealed class GoldmarkTaskCheckBoxRenderer : HtmlObjectRenderer<GoldmarkTaskCheckBox>
    {
        protected override void Write(HtmlRenderer renderer, GoldmarkTaskCheckBox obj) =>
            renderer.Write(obj.Checked ? "<input checked=\"\" disabled=\"\" type=\"checkbox\"> " : "<input disabled=\"\" type=\"checkbox\"> ");
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
