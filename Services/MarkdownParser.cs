using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Clawbrower.Models;
using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.SolidColorBrush;

namespace Clawbrower.Services;

/// <summary>
/// Converts Markdown text to structured blocks (paragraph, header, table, list, etc.)
/// and provides helpers to build WPF Inline elements.
/// </summary>
public static class MarkdownParser
{
    private static readonly WpfBrush CodeFg = new(WpfColor.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly WpfColor DimColor = WpfColor.FromRgb(0x88, 0x88, 0xAA);

    // ── Public API ──

    /// <summary>Parse markdown into structured blocks.</summary>
    public static List<MdBlock> ParseBlocks(string markdown)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrEmpty(markdown))
            return blocks;

        var lines = markdown.Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            // Skip empty lines
            if (string.IsNullOrEmpty(line))
            {
                i++;
                continue;
            }

            // Table: consecutive lines starting with |
            if (line.TrimStart().StartsWith('|'))
            {
                var table = ParseTable(lines, ref i);
                if (table != null) blocks.Add(table);
                continue;
            }

            // Header
            var headerMatch = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
            if (headerMatch.Success)
            {
                blocks.Add(new MdHeader
                {
                    Level = headerMatch.Groups[1].Length,
                    Text = StripInlineMarkdown(headerMatch.Groups[2].Value)
                });
                i++;
                continue;
            }

            // Horizontal rule
            if (Regex.IsMatch(line, @"^[-*_]{3,}\s*$"))
            {
                blocks.Add(new MdDivider());
                i++;
                continue;
            }

            // Unordered list — collect consecutive items
            if (Regex.IsMatch(line, @"^\s*[-*]\s+"))
            {
                blocks.Add(ParseList(lines, ref i, ordered: false));
                continue;
            }

            // Ordered list
            if (Regex.IsMatch(line, @"^\s*\d+\.\s+"))
            {
                blocks.Add(ParseList(lines, ref i, ordered: true));
                continue;
            }

            // Blockquote — collect consecutive quotes
            if (Regex.IsMatch(line, @"^>\s?"))
            {
                blocks.Add(ParseQuote(lines, ref i));
                continue;
            }

            // Regular paragraph
            blocks.Add(new MdParagraph { Inlines = ParseInlineLine(line) });
            i++;
        }

        return blocks;
    }

    // ── Legacy flat-Inline API (for streaming) ──

    /// <summary>Quick parse for streaming preview — returns flat Inlines.</summary>
    public static List<Inline> Parse(string markdown)
    {
        var inlines = new List<Inline>();
        if (string.IsNullOrEmpty(markdown))
            return inlines;

        var lines = markdown.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (string.IsNullOrEmpty(line))
            {
                if (inlines.Count > 0 && inlines[^1] is not LineBreak)
                    inlines.Add(new LineBreak());
                continue;
            }

            if (i > 0 && !string.IsNullOrEmpty(lines[i - 1]) && inlines.Count > 0)
                inlines.Add(new LineBreak());

            // Header
            var hm = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
            if (hm.Success)
            {
                var size = hm.Groups[1].Length switch { 1 => 18.0, 2 => 16.0, 3 => 14.0, _ => 13.0 };
                inlines.Add(new Run(StripInlineMarkdown(hm.Groups[2].Value)) { FontSize = size, FontWeight = FontWeights.Bold });
                continue;
            }

            // Table separator → skip
            if (Regex.IsMatch(line, @"\|?\s*[-:]{3,}\s*\|"))
                continue;

            // Table row
            if (line.TrimStart().StartsWith('|'))
            {
                var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                for (int c = 0; c < cells.Length; c++)
                {
                    AddFormattedText(inlines, cells[c]);
                    if (c < cells.Length - 1)
                        inlines.Add(new Run(" │ ") { Foreground = new WpfBrush(DimColor) });
                }
                continue;
            }

            // Horizontal rule
            if (Regex.IsMatch(line, @"^[-*_]{3,}\s*$"))
            {
                inlines.Add(new Run("─ ─ ─") { Foreground = new WpfBrush(DimColor), FontSize = 10 });
                continue;
            }

            // List
            var lm = Regex.Match(line, @"^(\s*)[-*]\s+(.+)$");
            if (lm.Success)
            {
                var indent = lm.Groups[1].Length;
                var prefix = indent switch { 0 => "  •  ", <= 2 => "    ◦  ", _ => "      ▪  " };
                inlines.Add(new Run(prefix) { Foreground = new WpfBrush(DimColor) });
                AddFormattedText(inlines, lm.Groups[2].Value);
                continue;
            }

            var om = Regex.Match(line, @"^\s*\d+\.\s+(.+)$");
            if (om.Success)
            {
                var num = Regex.Match(line, @"\d+\.").Value;
                inlines.Add(new Run($"{num} ") { Foreground = new WpfBrush(DimColor) });
                AddFormattedText(inlines, om.Groups[1].Value);
                continue;
            }

            // Blockquote
            var qm = Regex.Match(line, @"^>\s?(.+)$");
            if (qm.Success)
            {
                inlines.Add(new Run("▎ ") { Foreground = new WpfBrush(WpfColor.FromRgb(0x55, 0x77, 0xAA)) });
                inlines.Add(new Run(StripInlineMarkdown(qm.Groups[1].Value))
                {
                    FontStyle = FontStyles.Italic,
                    Foreground = new WpfBrush(WpfColor.FromRgb(0xBB, 0xBB, 0xCC))
                });
                continue;
            }

            AddFormattedText(inlines, line);
        }

        return inlines;
    }

    // ── Block parsers ──

    private static MdTable? ParseTable(string[] lines, ref int i)
    {
        // Collect all consecutive lines that look like table rows
        var rawRows = new List<string>();
        while (i < lines.Length)
        {
            var line = lines[i];
            if (string.IsNullOrEmpty(line)) break;

            // Stop collecting if this doesn't look like a table row
            if (!line.TrimStart().StartsWith('|') && !Regex.IsMatch(line, @"^\|?\s*[-:]{3,}\s*\|"))
                break;

            rawRows.Add(line);
            i++;
        }

        if (rawRows.Count < 2) return null; // need at least header + separator

        // Find separator row (contains |---|)
        int sepIdx = -1;
        for (int r = 0; r < rawRows.Count; r++)
        {
            if (Regex.IsMatch(rawRows[r], @"\|?\s*[-:]{3,}\s*\|"))
            {
                sepIdx = r;
                break;
            }
        }

        if (sepIdx <= 0) return null; // no valid separator found

        // Parse header row (before separator)
        var headers = ParseTableCells(rawRows[sepIdx - 1]);

        // Parse alignments from separator
        var alignments = ParseAlignments(rawRows[sepIdx]);

        // Parse data rows (after separator)
        var rows = new List<List<string>>();
        for (int r = sepIdx + 1; r < rawRows.Count; r++)
        {
            var cells = ParseTableCells(rawRows[r]);
            if (cells.Count > 0) rows.Add(cells);
        }

        if (headers.Count == 0) return null;

        return new MdTable
        {
            Headers = headers,
            Rows = rows,
            Alignments = alignments
        };
    }

    private static List<string> ParseTableCells(string line)
    {
        return line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();
    }

    private static List<string?> ParseAlignments(string sepLine)
    {
        var result = new List<string?>();
        foreach (var cell in ParseTableCells(sepLine))
        {
            var trimmed = cell.Trim();
            bool left = trimmed.StartsWith(':');
            bool right = trimmed.EndsWith(':');
            if (left && right) result.Add("center");
            else if (right) result.Add("right");
            else if (left) result.Add("left");
            else result.Add(null);
        }
        return result;
    }

    private static MdList ParseList(string[] lines, ref int i, bool ordered)
    {
        var pattern = ordered ? @"^\s*(\d+\.)\s+(.+)$" : @"^(\s*)[-*]\s+(.+)$";
        var list = new MdList { Ordered = ordered };
        var indentLevel = 0;

        while (i < lines.Length)
        {
            var line = lines[i];
            if (string.IsNullOrEmpty(line)) break;

            var match = Regex.Match(line, pattern);
            if (!match.Success) break;

            var indent = ordered ? 0 : match.Groups[1].Length;
            var text = ordered ? match.Groups[2].Value : match.Groups[2].Value;

            if (list.Items.Count == 0)
                indentLevel = indent;
            else if (indent != indentLevel)
                break; // different indent level → new list

            list.Items.Add(text);
            i++;
        }

        list.IndentLevel = indentLevel;
        return list;
    }

    private static MdQuote ParseQuote(string[] lines, ref int i)
    {
        var texts = new List<string>();
        while (i < lines.Length)
        {
            var line = lines[i];
            if (string.IsNullOrEmpty(line)) break;
            var m = Regex.Match(line, @"^>\s?(.*)$");
            if (!m.Success) break;
            texts.Add(m.Groups[1].Value);
            i++;
        }
        return new MdQuote { Text = string.Join(" ", texts) };
    }

    // ── Inline formatting ──

    /// <summary>Parse one line into Inline elements with formatting.</summary>
    public static List<Inline> ParseInlineLine(string text)
    {
        var inlines = new List<Inline>();
        AddFormattedText(inlines, text);
        return inlines;
    }

    private static void AddFormattedText(List<Inline> inlines, string text)
    {
        var pattern = @"(\*\*(.+?)\*\*)|(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)|(`(.+?)`)|(~~(.+?)~~)";
        var matches = Regex.Matches(text, pattern);
        var lastIndex = 0;

        foreach (Match match in matches)
        {
            if (match.Index > lastIndex)
                inlines.Add(new Run(text[lastIndex..match.Index]));

            if (match.Groups[1].Success)
            {
                inlines.Add(new Bold(new Run(match.Groups[2].Value)));
            }
            else if (match.Groups[3].Success)
            {
                inlines.Add(new Italic(new Run(match.Groups[3].Value)));
            }
            else if (match.Groups[4].Success)
            {
                inlines.Add(new Run(match.Groups[5].Value)
                {
                    FontFamily = new System.Windows.Media.FontFamily("Consolas, Courier New"),
                    Foreground = CodeFg
                });
            }
            else if (match.Groups[6].Success)
            {
                inlines.Add(new Run(match.Groups[7].Value) { TextDecorations = TextDecorations.Strikethrough });
            }

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
            inlines.Add(new Run(text[lastIndex..]));
    }

    private static string StripInlineMarkdown(string text)
    {
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        text = Regex.Replace(text, @"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", "$1");
        text = Regex.Replace(text, @"`(.+?)`", "$1");
        text = Regex.Replace(text, @"~~(.+?)~~", "$1");
        return text;
    }
}
