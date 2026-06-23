using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Clawbrower.Models;
using Clawbrower.Services;
using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.SolidColorBrush;

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
            switch (block)
            {
                case MdParagraph p:
                    var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 2, 0, 2) };
                    foreach (var inline in p.Inlines)
                        tb.Inlines.Add(inline);
                    ContentRoot.Children.Add(tb);
                    break;

                case MdHeader h:
                    var ht = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 2) };
                    ht.FontSize = h.Level switch { 1 => 18.0, 2 => 16.0, 3 => 14.0, _ => 13.0 };
                    foreach (var inline in MarkdownParser.ParseInlineLine(h.Text))
                        ht.Inlines.Add(inline);
                    ContentRoot.Children.Add(ht);
                    break;

                case MdTable t:
                    ContentRoot.Children.Add(RenderTable(t));
                    break;

                case MdList l:
                    var lt = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 2, 0, 2) };
                    var dimBrush = new WpfBrush(WpfColor.FromRgb(0x88, 0x88, 0xAA));
                    int num = 1;
                    foreach (var item in l.Items)
                    {
                        if (l.Ordered)
                            lt.Inlines.Add(new Run($"{num++}. ") { Foreground = dimBrush });
                        else
                        {
                            var prefix = l.IndentLevel switch { 0 => "• ", <= 2 => "◦ ", _ => "▪ " };
                            lt.Inlines.Add(new Run(prefix) { Foreground = dimBrush });
                        }
                        foreach (var inline in MarkdownParser.ParseInlineLine(item))
                            lt.Inlines.Add(inline);
                        lt.Inlines.Add(new LineBreak());
                    }
                    ContentRoot.Children.Add(lt);
                    break;

                case MdQuote q:
                    var qt = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap, FontSize = 13, FontStyle = FontStyles.Italic,
                        Foreground = new WpfBrush(WpfColor.FromRgb(0xBB, 0xBB, 0xCC)),
                        Margin = new Thickness(8, 2, 0, 2)
                    };
                    qt.Inlines.Add(new Run("▎ ") { Foreground = new WpfBrush(WpfColor.FromRgb(0x55, 0x77, 0xAA)) });
                    foreach (var inline in MarkdownParser.ParseInlineLine(q.Text))
                        qt.Inlines.Add(inline);
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
    }

    private static Grid RenderTable(MdTable table)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        var colCount = table.Headers.Count;

        for (int c = 0; c < colCount; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 50, MaxWidth = 300 });

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
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        foreach (var inline in MarkdownParser.ParseInlineLine(text))
            tb.Inlines.Add(inline);

        if (weight == FontWeights.Bold) tb.FontWeight = weight;

        tb.TextAlignment = alignment switch
        {
            "center" => TextAlignment.Center,
            "right" => TextAlignment.Right,
            _ => TextAlignment.Left
        };

        return new Border
        {
            Child = tb,
            Padding = new Thickness(6, 3, 6, 3)
        };
    }
}
