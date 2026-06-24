using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Clawbrower.Models;
using Clawbrower.Services;
using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.SolidColorBrush;
using WpfFontStyle = System.Windows.FontStyle;
using WpfFontFamily = System.Windows.Media.FontFamily;
using FlowDocumentScrollViewer = System.Windows.Controls.FlowDocumentScrollViewer;

namespace Clawbrower.Controls;

public partial class MarkdownBlock : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty MarkdownTextProperty =
        DependencyProperty.Register(nameof(MarkdownText), typeof(string), typeof(MarkdownBlock),
            new PropertyMetadata("", OnMarkdownTextChanged));

    public static readonly DependencyProperty IsStreamingProperty =
        DependencyProperty.Register(nameof(IsStreaming), typeof(bool), typeof(MarkdownBlock),
            new PropertyMetadata(false, OnIsStreamingChanged));

    public string MarkdownText
    {
        get => (string)GetValue(MarkdownTextProperty);
        set => SetValue(MarkdownTextProperty, value);
    }

    public bool IsStreaming
    {
        get => (bool)GetValue(IsStreamingProperty);
        set => SetValue(IsStreamingProperty, value);
    }

    private TextBlock? _streamingBlock;
    private double _lastPageWidth;

    public MarkdownBlock()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Only re-render on width shrink — flow document needs tighter PageWidth
        if (e.NewSize.Width < e.PreviousSize.Width - 0.5 && _streamingBlock == null)
            Dispatcher.BeginInvoke(() => RenderMarkdown(MarkdownText),
                System.Windows.Threading.DispatcherPriority.Background);
    }

    private static void OnMarkdownTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (MarkdownBlock)d;
        if (control.IsStreaming)
        {
            if (control._streamingBlock == null)
            {
                control._streamingBlock = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
                control.ContentRoot.Children.Clear();
                control.ContentRoot.Children.Add(control._streamingBlock);
            }
            control._streamingBlock.Text = e.NewValue as string ?? "";
        }
        else
        {
            control.RenderMarkdown(e.NewValue as string ?? "");
        }
    }

    private static void OnIsStreamingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (MarkdownBlock)d;
        if ((bool)e.OldValue && !(bool)e.NewValue)
            control.RenderMarkdown(control.MarkdownText);
    }

    private void RenderMarkdown(string markdown)
    {
        ContentRoot.Children.Clear();
        _streamingBlock = null;

        // Calculate page width from content: tight for short text, capped at available space
        var availableWidth = ActualWidth > 1 ? ActualWidth : 320;
        var contentWidth = EstimateContentWidth(markdown);
        var pageWidth = contentWidth > 0
            ? Math.Min(contentWidth + 24, availableWidth)
            : availableWidth;
        _lastPageWidth = pageWidth;

        var doc = new FlowDocument
        {
            FontFamily = new WpfFontFamily("Microsoft YaHei UI"),
            FontSize = 13,
            PagePadding = new Thickness(0),
            PageWidth = pageWidth,
            ColumnWidth = double.PositiveInfinity
        };

        var blocks = MarkdownParser.ParseBlocks(markdown);
        foreach (var block in blocks)
        {
            try
            {
                switch (block)
                {
                    case MdParagraph p:
                        doc.Blocks.Add(CreateFlowParagraph(p.Inlines, 13, FontWeights.Normal, FontStyles.Normal,
                            new Thickness(0, 2, 0, 2)));
                        break;

                    case MdHeader h:
                        var hSize = h.Level switch { 1 => 18.0, 2 => 16.0, 3 => 14.0, _ => 13.0 };
                        doc.Blocks.Add(CreateFlowParagraph(
                            MarkdownParser.ParseInlineLine(h.Text), hSize, FontWeights.Bold, FontStyles.Normal,
                            new Thickness(0, 6, 0, 2)));
                        break;

                    case MdTable t:
                        doc.Blocks.Add(CreateFlowTable(t));
                        break;

                    case MdList l:
                        doc.Blocks.Add(CreateFlowList(l));
                        break;

                    case MdQuote q:
                        doc.Blocks.Add(CreateFlowQuote(q));
                        break;

                    case MdDivider:
                        doc.Blocks.Add(new BlockUIContainer(new Border
                        {
                            Height = 1, Margin = new Thickness(0, 6, 0, 6),
                            Background = new WpfBrush(WpfColor.FromRgb(0x44, 0x44, 0x55))
                        }));
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"RenderMarkdown block failed: {ex.Message}");
            }
        }

        var viewer = new FlowDocumentScrollViewer
        {
            Document = doc,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsToolBarVisible = false,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };

        ContentRoot.Children.Add(viewer);
    }

    private static double EstimateContentWidth(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return 0;
        double maxWidth = 0;
        var typeface = new Typeface("Microsoft YaHei UI");
        const double dpi = 1.0;

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            // Skip table separators
            if (line.All(c => c == '|' || c == '-' || c == ':' || c == ' ')) continue;

            var match = System.Text.RegularExpressions.Regex.Match(line, @"^(#{1,6})\s+");
            double fontSize = match.Success ? match.Groups[1].Length switch
            {
                1 => 18.0, 2 => 16.0, 3 => 14.0, _ => 13.0
            } : 13.0;

            var text = MarkdownParser.StripInlineMarkdown(line);
            if (string.IsNullOrEmpty(text)) continue;

            var ft = new FormattedText(text, CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight, typeface, fontSize, System.Windows.Media.Brushes.Black, dpi);
            if (ft.Width > maxWidth) maxWidth = ft.Width;
        }
        return Math.Min(maxWidth, 320);
    }

    // ── FlowDocument helpers ──

    private static Paragraph CreateFlowParagraph(
        IEnumerable<Inline> inlines, double fontSize, FontWeight weight, WpfFontStyle style, Thickness margin)
    {
        var p = new Paragraph
        {
            FontSize = fontSize,
            FontWeight = weight,
            FontStyle = style,
            Margin = margin
        };
        foreach (var inline in inlines)
            p.Inlines.Add(inline);
        return p;
    }

    private static Table CreateFlowTable(MdTable mdTable)
    {
        var table = new Table { Margin = new Thickness(0, 4, 0, 4) };
        var colCount = mdTable.Headers.Count;

        for (int c = 0; c < colCount; c++)
            table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });

        var rowGroup = new TableRowGroup();

        // Header row
        var headerRow = new TableRow();
        for (int c = 0; c < colCount; c++)
        {
            var cell = new TableCell(new Paragraph(new Run(MarkdownParser.StripInlineMarkdown(mdTable.Headers[c])))
            {
                FontSize = 12,
                FontWeight = FontWeights.Bold
            })
            {
                Background = new WpfBrush(WpfColor.FromRgb(0x2A, 0x2A, 0x3A)),
                BorderBrush = new WpfBrush(WpfColor.FromRgb(0x44, 0x44, 0x55)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(6, 3, 6, 3)
            };
            ApplyCellAlign(cell, mdTable.Alignments.Count > c ? mdTable.Alignments[c] : null);
            headerRow.Cells.Add(cell);
        }
        rowGroup.Rows.Add(headerRow);

        // Data rows
        foreach (var row in mdTable.Rows)
        {
            var tableRow = new TableRow();
            for (int c = 0; c < colCount; c++)
            {
                var text = c < row.Count ? row[c] : "";
                var align = mdTable.Alignments.Count > c ? mdTable.Alignments[c] : null;
                var cell = new TableCell(new Paragraph(new Run(MarkdownParser.StripInlineMarkdown(text)))
                {
                    FontSize = 12
                })
                {
                    BorderBrush = new WpfBrush(WpfColor.FromRgb(0x33, 0x33, 0x44)),
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(6, 3, 6, 3)
                };
                ApplyCellAlign(cell, align);
                tableRow.Cells.Add(cell);
            }
            rowGroup.Rows.Add(tableRow);
        }

        table.RowGroups.Add(rowGroup);
        return table;
    }

    private static void ApplyCellAlign(TableCell cell, string? alignment)
    {
        cell.TextAlignment = alignment switch
        {
            "center" => TextAlignment.Center,
            "right" => TextAlignment.Right,
            _ => TextAlignment.Left
        };
    }

    private static List CreateFlowList(MdList l)
    {
        var list = new List
        {
            MarkerStyle = l.Ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 2, 0, 2)
        };
        foreach (var item in l.Items)
        {
            var listItem = new ListItem();
            var para = new Paragraph { Margin = new Thickness(0) };
            foreach (var inline in MarkdownParser.ParseInlineLine(item))
                para.Inlines.Add(inline);
            listItem.Blocks.Add(para);
            list.ListItems.Add(listItem);
        }
        return list;
    }

    private static Paragraph CreateFlowQuote(MdQuote q)
    {
        var p = new Paragraph
        {
            FontStyle = FontStyles.Italic,
            Foreground = new WpfBrush(WpfColor.FromRgb(0xBB, 0xBB, 0xCC)),
            BorderBrush = new WpfBrush(WpfColor.FromRgb(0x55, 0x77, 0xAA)),
            BorderThickness = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(8, 0, 0, 0),
            Margin = new Thickness(0, 2, 0, 2)
        };
        foreach (var inline in MarkdownParser.ParseInlineLine(q.Text))
            p.Inlines.Add(inline);
        return p;
    }
}
