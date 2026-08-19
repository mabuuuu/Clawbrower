using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Clawbrower.Models;
using Clawbrower.Services;
using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.SolidColorBrush;
using WpfFontStyle = System.Windows.FontStyle;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfBinding = System.Windows.Data.Binding;

namespace Clawbrower.Controls;

public partial class MarkdownBlock : System.Windows.Controls.UserControl
{
    // ── 静态缓存：渲染热路径中的重复对象（FontFamily 字符串解析、Brush 创建都很昂贵）──
    // 注意：文字颜色不在此硬编码——文字色应继承控件 Foreground（跟随用户设置），
    // 只有装饰性背景/边框色可在此缓存。
    private static readonly WpfFontFamily YaHeiFont = new("Microsoft YaHei UI");
    private static readonly WpfBrush HeaderBgBrush = new(WpfColor.FromRgb(0x2A, 0x2A, 0x3A));
    private static readonly WpfBrush CellBorderBrush = new(WpfColor.FromRgb(0x33, 0x33, 0x44));
    private static readonly WpfBrush GridLineBrush = new(WpfColor.FromRgb(0x44, 0x44, 0x55));
    private static readonly WpfBrush QuoteBorderBrush = new(WpfColor.FromRgb(0x55, 0x77, 0xAA));

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
    private bool _updateScheduled;

    public MarkdownBlock()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 兼容旧接口：窗口缩放时 TextBlock 会自动重排，无需手动重渲染。
    /// </summary>
    public void InvalidateLayout()
    {
    }

    private static void OnMarkdownTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // 延迟渲染：在 Dispatcher Background 优先级执行视觉树修改。
        // 直接同步渲染会在模板加载/Measure 期间修改视觉树（Clear/Add），
        // 导致 WPF 布局重入（模板反复加载），UI 线程卡死 + 内存暴涨。
        ((MarkdownBlock)d).ScheduleContentUpdate();
    }

    private static void OnIsStreamingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((MarkdownBlock)d).ScheduleContentUpdate();
    }

    /// <summary>
    /// 调度一次内容更新（合并快速连续更新，延迟到布局完成后的 Background 优先级）。
    /// </summary>
    private void ScheduleContentUpdate()
    {
        if (_updateScheduled) return;
        _updateScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _updateScheduled = false;
            if (IsStreaming)
                RenderStreaming(MarkdownText);
            else
                RenderMarkdown(MarkdownText);
        }));
    }

    private void RenderStreaming(string text)
    {
        if (_streamingBlock == null)
        {
            _streamingBlock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                FontFamily = YaHeiFont,
            };
            ContentRoot.Children.Clear();
            ContentRoot.Children.Add(_streamingBlock);
        }
        // 截断：流式时只显示最后 MaxStreamingChars 字符，避免超长 TextBlock 文字布局卡死 UI。
        // 流式结束（IsStreaming=false）后走 RenderMarkdown 分段渲染完整内容。
        const int MaxStreamingChars = 5000;
        if (text.Length > MaxStreamingChars)
        {
            text = "…（流式中，仅显示末尾内容）\n" + text[^MaxStreamingChars..];
        }
        _streamingBlock.Text = MarkdownParser.SanitizeSurrogates(text);
    }

    private void RenderMarkdown(string markdown)
    {
        ContentRoot.Children.Clear();
        _streamingBlock = null;

        // Sanitize BEFORE any text enters TextBlock — broken surrogates crash WPF
        markdown = MarkdownParser.SanitizeSurrogates(markdown);

        // 渲染上限：超过 MaxRenderChars 只渲染开头部分。
        // 防止超长消息（数十万行）的解析+UI 对象创建卡死 UI 线程并耗尽内存。
        const int MaxRenderChars = 30_000;
        if (markdown.Length > MaxRenderChars)
        {
            var omitted = markdown.Length - MaxRenderChars;
            markdown = markdown[..MaxRenderChars] +
                       $"\n\n…（消息过长，已截断显示，剩余 {omitted} 字符未渲染）";
        }

        // 不使用 FlowDocument/FlowDocumentScrollViewer：其 PTS 段落布局在有限高度
        // ScrollViewer 内多文档渲染时触发 UpdateViewport 无限递归（WPF 布局循环检测 →
        // Environment.FailFast 崩溃，加载历史消息时复现）。
        // 全部改用普通 UI 元素（TextBlock/Grid/StackPanel），走 UIElement 布局管线，
        // 宽度自适应 Border 约束，无 PTS、无布局循环风险。
        var blocks = MarkdownParser.ParseBlocks(markdown);
        foreach (var block in blocks)
        {
            try
            {
                switch (block)
                {
                    case MdParagraph p:
                        ContentRoot.Children.Add(CreateSelectableText(
                            MarkdownParser.StripInlineMarkdown(p.Text), 13,
                            FontWeights.Normal, FontStyles.Normal, new Thickness(0, 2, 0, 2)));
                        break;

                    case MdHeader h:
                        var hSize = h.Level switch { 1 => 18.0, 2 => 16.0, 3 => 14.0, _ => 13.0 };
                        // 标题用 TextBlock（性能好，Foreground 继承），不需要选中复制
                        ContentRoot.Children.Add(new TextBlock
                        {
                            Text = MarkdownParser.StripInlineMarkdown(h.Text),
                            FontFamily = YaHeiFont,
                            FontSize = hSize,
                            FontWeight = FontWeights.Bold,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 6, 0, 2)
                        });
                        break;

                    case MdTable t:
                        ContentRoot.Children.Add(CreateFlowTable(t));
                        break;

                    case MdList l:
                        ContentRoot.Children.Add(CreateFlowList(l));
                        break;

                    case MdQuote q:
                        ContentRoot.Children.Add(CreateFlowQuote(q));
                        break;

                    case MdDivider:
                        ContentRoot.Children.Add(new Border
                        {
                            Height = 1,
                            Margin = new Thickness(0, 6, 0, 6),
                            Background = GridLineBrush
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

    /// <summary>
    /// 创建只读 TextBox（替代 TextBlock），支持文本选择和 Ctrl+C 复制。
    /// WPF 的 TextBlock 不支持文本选择，用 TextBox（IsReadOnly + 透明样式）实现。
    /// TextBox 默认样式强制黑色前景，必须用 Binding 跟随本控件的 Foreground（用户文字颜色设置）。
    /// </summary>
    private WpfTextBox CreateSelectableText(
        string text, double fontSize, FontWeight weight, WpfFontStyle style, Thickness margin)
    {
        var tb = new WpfTextBox
        {
            Text = text,
            FontFamily = YaHeiFont,
            FontSize = fontSize,
            FontWeight = weight,
            FontStyle = style,
            Margin = margin,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = null,
            Padding = new Thickness(0),
            AcceptsReturn = false,
            CaretBrush = new WpfBrush(WpfColor.FromArgb(0x80, 0x67, 0x8C, 0xB3)),
            SelectionBrush = new WpfBrush(WpfColor.FromArgb(0x55, 0x67, 0x8C, 0xB3))
        };
        tb.SetBinding(WpfTextBox.ForegroundProperty,
            new WpfBinding(nameof(Foreground)) { Source = this });
        return tb;
    }

    private static Grid CreateFlowTable(MdTable mdTable)
    {
        // 表格用 Grid 自绘（不用 WPF Table：单元格走 PTS 段落布局，触发布局循环崩溃）
        var colCount = Math.Max(mdTable.Headers.Count, mdTable.Rows.Count > 0 ? mdTable.Rows.Max(r => r.Count) : 0);
        if (colCount <= 0) colCount = 1;

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        for (int c = 0; c < colCount; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int row = 0;

        // Header row
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < colCount; c++)
        {
            var headerText = c < mdTable.Headers.Count ? mdTable.Headers[c] : "";
            var cell = new Border
            {
                Background = HeaderBgBrush,
                BorderBrush = GridLineBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(6, 3, 6, 3),
                Child = new TextBlock
                {
                    Text = MarkdownParser.StripInlineMarkdown(headerText),
                    FontFamily = YaHeiFont,
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    TextWrapping = TextWrapping.Wrap
                    // Foreground 继承 MarkdownBlock.Foreground（跟随用户文字颜色设置）
                }
            };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, c);
            grid.Children.Add(cell);
        }
        row++;

        // Data rows
        foreach (var dataRow in mdTable.Rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < colCount; c++)
            {
                var text = c < dataRow.Count ? dataRow[c] : "";
                var cell = new Border
                {
                    BorderBrush = CellBorderBrush,
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(6, 3, 6, 3),
                    Child = new TextBlock
                    {
                        Text = MarkdownParser.StripInlineMarkdown(text),
                        FontFamily = YaHeiFont,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap
                        // Foreground 继承 MarkdownBlock.Foreground（跟随用户文字颜色设置）
                    }
                };
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
            row++;
        }

        return grid;
    }

    private StackPanel CreateFlowList(MdList l)
    {
        // 列表用 StackPanel 自绘（不用 WPF List：ListItem -> Paragraph 走 PTS 容器布局）
        var stack = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        var number = 1;
        foreach (var item in l.Items)
        {
            // 每行用 Grid（marker 列 Auto + content 列 Star），让 content 有约束宽度可换行。
            // 之前用水平 StackPanel：给子元素无限宽度，TextWrapping.Wrap 无效，长内容被截断。
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var marker = l.Ordered ? $"{number}. " : "•  ";
            var markerTb = new TextBlock
            {
                Text = marker,
                FontFamily = YaHeiFont,
                FontSize = 13,
                Opacity = 0.7, // 弱化标记（颜色继承 Foreground，跟随用户设置）
                Margin = new Thickness(0, 0, 2, 0)
            };
            Grid.SetColumn(markerTb, 0);
            row.Children.Add(markerTb);

            // 列表内容用 TextBlock（性能好，Foreground 继承）；复制可右键"复制"全文
            var content = new TextBlock
            {
                Text = MarkdownParser.StripInlineMarkdown(item),
                FontFamily = YaHeiFont,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(content, 1);
            row.Children.Add(content);

            stack.Children.Add(row);
            number++;
        }
        return stack;
    }

    private Border CreateFlowQuote(MdQuote q)
    {
        return new Border
        {
            BorderBrush = QuoteBorderBrush,
            BorderThickness = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(8, 0, 0, 0),
            Margin = new Thickness(0, 2, 0, 2),
            // 引用用 TextBlock（性能好，Foreground 继承）
            Child = new TextBlock
            {
                Text = MarkdownParser.StripInlineMarkdown(q.Text),
                FontFamily = YaHeiFont,
                FontSize = 13,
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap
            }
        };
    }
}
