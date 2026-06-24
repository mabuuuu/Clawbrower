using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Clawbrower.Models;
using Clawbrower.Services;
using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.SolidColorBrush;
using WpfFontStyle = System.Windows.FontStyle;

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

    public MarkdownBlock()
    {
        InitializeComponent();
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

        var blocks = MarkdownParser.ParseBlocks(markdown);
        foreach (var block in blocks)
        {
            try
            {
                switch (block)
                {
                    case MdParagraph p:
                        ContentRoot.Children.Add(CreateTextBlock(p.Inlines, 13, FontWeights.Normal, FontStyles.Normal,
                            new Thickness(0, 2, 0, 2)));
                        break;

                    case MdHeader h:
                        var hSize = h.Level switch { 1 => 18.0, 2 => 16.0, 3 => 14.0, _ => 13.0 };
                        ContentRoot.Children.Add(CreateTextBlock(
                            MarkdownParser.ParseInlineLine(h.Text), hSize, FontWeights.Bold, FontStyles.Normal,
                            new Thickness(0, 6, 0, 2)));
                        break;

                    case MdTable t:
                        ContentRoot.Children.Add(RenderTable(t));
                        break;

                    case MdList l:
                        var listInlines = new List<Inline>();
                        var dimBrush = new WpfBrush(WpfColor.FromRgb(0x88, 0x88, 0xAA));
                        int num = 1;
                        foreach (var item in l.Items)
                        {
                            if (l.Ordered)
                                listInlines.Add(new Run($"{num++}. ") { Foreground = dimBrush });
                            else
                            {
                                var prefix = l.IndentLevel switch { 0 => "• ", <= 2 => "◦ ", _ => "▪ " };
                                listInlines.Add(new Run(prefix) { Foreground = dimBrush });
                            }
                            foreach (var inline in MarkdownParser.ParseInlineLine(item))
                                listInlines.Add(inline);
                            listInlines.Add(new LineBreak());
                        }
                        ContentRoot.Children.Add(CreateTextBlock(listInlines, 13, FontWeights.Normal, FontStyles.Normal,
                            new Thickness(0, 2, 0, 2)));
                        break;

                    case MdQuote q:
                        var quoteInlines = new List<Inline>
                        {
                            new Run("▎ ") { Foreground = new WpfBrush(WpfColor.FromRgb(0x55, 0x77, 0xAA)) }
                        };
                        foreach (var inline in MarkdownParser.ParseInlineLine(q.Text))
                            quoteInlines.Add(inline);
                        var qt = CreateTextBlock(quoteInlines, 13, FontWeights.Normal, FontStyles.Italic,
                            new Thickness(8, 2, 0, 2));
                        qt.Foreground = new WpfBrush(WpfColor.FromRgb(0xBB, 0xBB, 0xCC));
                        ContentRoot.Children.Add(qt);
                        break;

                    case MdDivider:
                        ContentRoot.Children.Add(new Border
                        {
                            Height = 1, Margin = new Thickness(0, 6, 0, 6),
                            Background = new WpfBrush(WpfColor.FromRgb(0x44, 0x44, 0x55))
                        });
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"RenderMarkdown block failed: {ex.Message}");
            }
        }
    }

    private static TextBlock CreateTextBlock(
        IEnumerable<Inline> inlines, double fontSize, FontWeight weight, WpfFontStyle style, Thickness margin)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            FontWeight = weight,
            FontStyle = style,
            Margin = margin
        };
        foreach (var inline in inlines)
            tb.Inlines.Add(inline);
        return tb;
    }

    // ── Table rendering ──

    private static Grid RenderTable(MdTable table)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        var colCount = table.Headers.Count;

        for (int c = 0; c < colCount; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 40 });

        // Header row
        grid.RowDefinitions.Add(new RowDefinition());
        for (int c = 0; c < colCount; c++)
        {
            var cell = CreateTableCell(table.Headers[c], FontWeights.Bold, table.Alignments.Count > c ? table.Alignments[c] : null);
            cell.Background = new WpfBrush(WpfColor.FromRgb(0x2A, 0x2A, 0x3A));
            cell.BorderThickness = new Thickness(0, 0, 0, 1);
            cell.BorderBrush = new WpfBrush(WpfColor.FromRgb(0x44, 0x44, 0x55));
            Grid.SetRow(cell, 0);
            Grid.SetColumn(cell, c);
            grid.Children.Add(cell);
        }

        // Data rows
        int rowIdx = 1;
        foreach (var row in table.Rows)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            for (int c = 0; c < colCount; c++)
            {
                var text = c < row.Count ? row[c] : "";
                var align = table.Alignments.Count > c ? table.Alignments[c] : null;
                var cell = CreateTableCell(text, FontWeights.Normal, align);
                cell.BorderBrush = new WpfBrush(WpfColor.FromRgb(0x33, 0x33, 0x44));
                cell.BorderThickness = new Thickness(0.5);
                Grid.SetRow(cell, rowIdx);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
            rowIdx++;
        }

        return grid;
    }

    private static Border CreateTableCell(string text, FontWeight weight, string? alignment)
    {
        // Use TextBox for selectable text, strip inline markdown for readability
        var tb = new System.Windows.Controls.TextBox
        {
            Text = MarkdownParser.StripInlineMarkdown(text),
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            FontWeight = weight,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            Padding = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.IBeam,
            TextAlignment = alignment switch
            {
                "center" => TextAlignment.Center,
                "right" => TextAlignment.Right,
                _ => TextAlignment.Left
            }
        };

        return new Border
        {
            Child = tb,
            Padding = new Thickness(6, 3, 6, 3)
        };
    }
}
