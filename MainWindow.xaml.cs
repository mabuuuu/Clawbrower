using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Clawbrower.Controls;
using Clawbrower.Models;
using Clawbrower.Services;
using Clawbrower.Dialogs;
using Clawbrower.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;

namespace Clawbrower;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private HwndSource? _hwndSource;
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 9001;

    /// <summary>
    /// 标记是否应聚焦输入框（仅热键/托盘唤起时为 true，用户点击内容区域时不抢焦点）
    /// </summary>
    private bool _shouldFocusInput;

    private ModifierKeys _hotkeyMod;
    private Key _hotkeyKey;
    private System.Windows.Threading.DispatcherTimer _resizeTimer;
    private bool _suppressScrollToEnd;

    public MainWindow()
    {
        InitializeComponent();
        Logger.Info("MainWindow constructing");

        _vm = new MainViewModel();
        DataContext = _vm;

        Loaded += (_, _) =>
        {
            PositionWindow();
            Logger.Info($"Window loaded at {Left},{Top} size={ActualWidth}x{ActualHeight}");
        };
        SourceInitialized += OnSourceInitialized;

        _vm.Messages.CollectionChanged += (_, _) =>
            Dispatcher.InvokeAsync(() =>
            {
                if (!_suppressScrollToEnd)
                    MessageScroll.ScrollToEnd();
            }, System.Windows.Threading.DispatcherPriority.Background);

        _vm.OnMessageUpdated += () =>
            Dispatcher.InvokeAsync(() =>
            {
                if (!_suppressScrollToEnd)
                    MessageScroll.ScrollToEnd();
            }, System.Windows.Threading.DispatcherPriority.Background);

        // 窗口缩放时，通知所有 MarkdownBlock 按新可用宽度重新布局（处理放大时不恢复的 bug）
        // 用 DispatcherTimer 节流：拖动中只重置定时器，停止 ~120ms 后才遍历渲染一次，避免每帧全量重渲染卡顿
        // 注：MarkdownBlock 已改为 TextBlock 自绘（自适应宽度），不再需要手动重渲染，此逻辑已空转保留
        _resizeTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            foreach (var mb in FindVisualChildren<MarkdownBlock>(MessageList))
                mb.InvalidateLayout();
        };
        MessageScroll.SizeChanged += (_, _) =>
        {
            _resizeTimer.Stop();
            _resizeTimer.Start();
        };

        Activated += OnWindowActivated;
        KeyDown += Window_KeyDown;

        var s = ConfigService.Load();
        _hotkeyMod = s.HotkeyMod;
        _hotkeyKey = s.HotkeyKey;
        SetWindowOpacity(s.Opacity);
        SetTextOpacity(s.TextOpacity);
        SetTextColor(s.TextColor);
        Logger.Info($"Settings: winOpacity={s.Opacity}, textOpacity={s.TextOpacity}, textColor={s.TextColor}, hotkey={_hotkeyMod}+{_hotkeyKey}");

        // MCP 状态按钮更新
        App.McpService.OnStatusChanged += (status) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                McpButton.ToolTip = status switch
                {
                    McpStatus.Running => "MCP 运行中（点击停止）",
                    McpStatus.Starting => "MCP 启动中...",
                    McpStatus.Error => $"MCP 异常: {App.McpService.LastError}",
                    _ => "MCP 远程控制（点击启动）"
                };
                McpDot.Fill = status == McpStatus.Running
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0x66, 0x66));
            });
        };

        // 语音输入状态按钮更新
        App.SpeechService.OnEnabledChanged += (enabled) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                SpeechButton.ToolTip = enabled ? "语音输入已开启（点击关闭）" : "语音输入（点击开启）";
                SpeechDot.Fill = enabled
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0x66, 0x66));
            });
        };
        // 初始化语音按钮状态
        {
            var enabled = App.SpeechService.IsEnabled;
            SpeechButton.ToolTip = enabled ? "语音输入已开启（点击关闭）" : "语音输入（点击开启）";
            SpeechDot.Fill = enabled
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x66, 0x66, 0x66));
        }
    }

    // ── Global hotkey ──

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public void RegisterHotkey(ModifierKeys mod, Key key)
    {
        UnregisterHotkey();
        _hotkeyMod = mod;
        _hotkeyKey = key;
        if (_hwndSource == null) return;
        uint m = 0;
        if (mod.HasFlag(ModifierKeys.Alt)) m |= 0x0001;
        if (mod.HasFlag(ModifierKeys.Control)) m |= 0x0002;
        if (mod.HasFlag(ModifierKeys.Shift)) m |= 0x0004;
        if (mod.HasFlag(ModifierKeys.Windows)) m |= 0x0008;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        RegisterHotKey(_hwndSource.Handle, HOTKEY_ID, m, vk);
        Logger.Info($"Hotkey registered: {mod}+{key}");
    }

    private void UnregisterHotkey() { if (_hwndSource != null) UnregisterHotKey(_hwndSource.Handle, HOTKEY_ID); }

    private void OnWindowActivated(object sender, EventArgs e)
    {
        // 仅在热键/托盘唤起时聚焦输入框，用户点击消息区域选择文字时不抢焦点
        if (_shouldFocusInput)
        {
            InputBox.Focus();
            _shouldFocusInput = false;
        }
    }

    public void RequestFocusInput()
    {
        _shouldFocusInput = true;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);
        RegisterHotkey(_hotkeyMod, _hotkeyKey);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                if (IsActive && InputBox.IsFocused)
                    Hide();
                else
                {
                    if (WindowState == WindowState.Maximized)
                        WindowState = WindowState.Normal;
                    _shouldFocusInput = true;
                    Show(); Activate(); InputBox.Focus();
                }
            handled = true;
            return IntPtr.Zero;
        }

        const int WM_NCHITTEST = 0x0084;
        if (msg != WM_NCHITTEST) return IntPtr.Zero;

        var pt = PointFromScreen(new Point((int)lParam & 0xFFFF, ((int)lParam >> 16) & 0xFFFF));
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        const int border = 12;
        if (!rect.Contains(pt)) return IntPtr.Zero;
        if (pt.X >= border && pt.X <= ActualWidth - border && pt.Y >= border && pt.Y <= ActualHeight - border)
            return IntPtr.Zero;

        int htX = pt.X <= border ? 1 : pt.X >= ActualWidth - border ? 2 : 0;
        int htY = pt.Y <= border ? 1 : pt.Y >= ActualHeight - border ? 2 : 0;
        handled = true;
        return (IntPtr)((htY * 3 + htX) switch
        {
            11 => 13, 12 => 10, 13 => 16, 21 => 12, 22 => 2, 23 => 15,
            31 => 14, 32 => 11, 33 => 17, _ => 0
        });
    }

    // ── Copy support ──

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (System.Windows.Clipboard.ContainsText())
                System.Windows.Clipboard.SetText(System.Windows.Clipboard.GetText()); // already handled
            e.Handled = true;
        }
    }

    // ── Position ──

    private void PositionWindow()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Bottom - Height - 16;
    }

    // ── Drag ──

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == this) DragMove();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) return;
        DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => Hide();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    // ── Send ──

    private void SendButton_Click(object sender, RoutedEventArgs e) => _ = TrySendAsync();

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            _ = TrySendAsync();
        }
    }

    private async Task TrySendAsync() => await _vm.SendMessageAsync();

    private async void NewSession_Click(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Show(this, "新建会话", "请输入会话名称（可选）：", $"会话 {_vm.Sessions.Count + 1}");
        if (name != null)
            await _vm.CreateSessionAsync(name);
    }
    private async void StopButton_Click(object sender, RoutedEventArgs e) => await _vm.StopAsync();

    private async void SessionSelector_DropDownOpened(object sender, EventArgs e) => await _vm.LoadSessionsAsync();
    private void SessionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is SessionInfo si)
            _vm.CurrentSession = si;
    }

    private void CopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem menuItem &&
            menuItem.Parent is ContextMenu ctxMenu)
        {
            if (ctxMenu.PlacementTarget is Controls.MarkdownBlock mb)
                System.Windows.Clipboard.SetText(mb.MarkdownText);
            else if (ctxMenu.PlacementTarget is System.Windows.Controls.TextBlock tb)
                System.Windows.Clipboard.SetText(tb.Text);
        }
    }

    // ── History loading (scroll-to-top trigger) ──

    private void LoadMoreHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsConnected && !_vm.IsLoadingHistory)
        {
            _suppressScrollToEnd = true;
            _ = _vm.LoadHistoryAsync().ContinueWith(_ =>
                Dispatcher.InvokeAsync(() => { _suppressScrollToEnd = false; },
                    System.Windows.Threading.DispatcherPriority.Background));
        }
    }

    private void ClearMessages_Click(object sender, RoutedEventArgs e)
    {
        _suppressScrollToEnd = true;
        _vm.ClearCurrentSessionMessages();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _suppressScrollToEnd = false;
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void McpButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.McpService.Status == McpStatus.Running)
        {
            App.McpService.Stop();
        }
        else if (App.McpService.Status == McpStatus.Stopped || App.McpService.Status == McpStatus.Error)
        {
            var existing = ConfigService.GetMcpConfig();
            if (!existing.IsConfigured)
            {
                var result = Dialogs.McpConfigDialog.Show(this, existing);
                if (result == null) return;
                ConfigService.SetMcpConfig(result);
                existing = result;
            }
            _ = App.McpService.StartAsync(existing);
        }
    }

    private void SpeechButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current is App app)
            app.ToggleSpeech();
    }

    // ── Settings callbacks ──

    private double _textOpacity = 1.0;
    private string _textColor = "#EEEEEE";

    public void SetWindowOpacity(double opacity)
    {
        var cardAlpha = (byte)Math.Clamp(opacity * 255, 0, 255);
        var barAlpha = (byte)Math.Clamp(opacity * 255 * 0.93, 0, 255);
        ContentCard.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(cardAlpha, 0x1A, 0x1A, 0x2E));
        TitleBar.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(barAlpha, 0x12, 0x12, 0x1E));
        InputArea.Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(barAlpha, 0x12, 0x12, 0x1E));
    }

    public void SetTextOpacity(double opacity)
    {
        _textOpacity = Math.Clamp(opacity, 0, 1.0);
        ApplyTextStyle();
    }

    public void SetTextColor(string hex)
    {
        _textColor = hex;
        ApplyTextStyle();
    }

    public async Task Reconnect()
    {
        Logger.Info("Reconnect requested (gateway url may have changed)");
        await _vm.ReconnectAsync();
    }

    private void ApplyTextStyle()
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var baseColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_textColor);
                var alpha = (byte)Math.Clamp(_textOpacity * 255, 0, 255);
                var textColor = System.Windows.Media.Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
                var dimColor = System.Windows.Media.Color.FromArgb((byte)(alpha * 0.6), baseColor.R, baseColor.G, baseColor.B);
                var textBrush = new System.Windows.Media.SolidColorBrush(textColor);
                var dimBrush = new System.Windows.Media.SolidColorBrush(dimColor);

                Resources["TextBrush"] = textBrush;
                Resources["TextDimBrush"] = dimBrush;

                InputBox.Foreground = textBrush;
                InputBox.CaretBrush = textBrush;

                // Walk message bubbles (hardcoded Foreground, not DynamicResource)
                UpdateBubbleForeground(MessageScroll, textBrush);
            }
            catch (Exception ex) { Logger.Error($"ApplyTextStyle: {ex.Message}"); }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static void UpdateBubbleForeground(
        System.Windows.DependencyObject parent, System.Windows.Media.Brush brush)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            // MarkdownBlock 的父是 StackPanel（Bubble > StackPanel > MarkdownBlock），
            // 不能依赖 Parent is Border 判断——直接对所有 MarkdownBlock 设置 Foreground。
            if (child is Controls.MarkdownBlock mb)
            {
                mb.Foreground = brush;
                SyncFlowDocForeground(child, brush);
            }
            else if (child is TextBlock tb && tb.Parent is Border)
                tb.Foreground = brush;
            UpdateBubbleForeground(child, brush);
        }
    }

    private static void SyncFlowDocForeground(System.Windows.DependencyObject parent, System.Windows.Media.Brush brush)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is FlowDocumentScrollViewer viewer && viewer.Document != null)
                viewer.Document.Foreground = brush;
            SyncFlowDocForeground(child, brush);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var desc in FindVisualChildren<T>(child)) yield return desc;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        UnregisterHotkey();
        base.OnClosed(e);
    }
}
