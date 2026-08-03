using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Clawbrower.Services;
using SpeechMode = Clawbrower.Services.SpeechMode;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Media = System.Windows.Media;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace Clawbrower;

public partial class SettingsWindow : Window, INotifyPropertyChanged
{
    private readonly MainWindow _mainWindow;
    private bool _capturingHotkey;
    private bool _capturingPttKey;

    // ── 外观（实时预览，取消时恢复）──
    private readonly double _origWindowOpacity;
    private readonly double _origTextOpacity;
    private readonly string _origTextColor;
    private readonly ModifierKeys _origHotkeyMod;
    private readonly Key _origHotkeyKey;

    private string _textColor;
    public string TextColor
    {
        get => _textColor;
        set { _textColor = value; OnPropertyChanged(); OnPropertyChanged(nameof(PreviewColor)); _mainWindow.SetTextColor(value); }
    }

    public Media.Brush PreviewColor
    {
        get
        {
            try { return new Media.SolidColorBrush((Media.Color)Media.ColorConverter.ConvertFromString(TextColor)); }
            catch { return new Media.SolidColorBrush(Media.Colors.Gray); }
        }
    }

    private double _windowOpacity;
    public double WindowOpacity
    {
        get => _windowOpacity;
        set { _windowOpacity = value; OnPropertyChanged(); _mainWindow.SetWindowOpacity(value); }
    }

    private double _textOpacity;
    public double TextOpacity
    {
        get => _textOpacity;
        set { _textOpacity = value; OnPropertyChanged(); _mainWindow.SetTextOpacity(value); }
    }

    private string _hotkeyDisplay = "Alt + Z";
    public string HotkeyDisplay
    {
        get => _hotkeyDisplay;
        set { _hotkeyDisplay = value; OnPropertyChanged(); }
    }

    private ModifierKeys _hotkeyMod = ModifierKeys.Alt;
    private Key _hotkeyKey = Key.Z;

    // ── 语音 PTT 按键 ──
    private Key _capturedPttKey = Key.F12;

    // ── 语音原始值（模式/阈值变更需重启语音服务）──
    private readonly SpeechMode _origSpeechMode;
    private readonly double _origSpeechThreshold;

    public SettingsWindow(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
        var s = ConfigService.Load();

        // 外观
        _origWindowOpacity = s.Opacity;
        _origTextOpacity = s.TextOpacity;
        _origTextColor = s.TextColor;
        _origHotkeyMod = s.HotkeyMod;
        _origHotkeyKey = s.HotkeyKey;

        // 语音原始值
        var origSpeech = ConfigService.GetSpeechConfig();
        _origSpeechMode = origSpeech.Mode;
        _origSpeechThreshold = origSpeech.WakeWordThreshold;

        _windowOpacity = s.Opacity;
        _textOpacity = s.TextOpacity;
        _textColor = s.TextColor;
        _hotkeyMod = s.HotkeyMod;
        _hotkeyKey = s.HotkeyKey;
        _hotkeyDisplay = HotkeyToString(_hotkeyMod, _hotkeyKey);

        InitializeComponent();
        DataContext = this;

        // 连接
        UrlBox.Text = s.GatewayUrl ?? "ws://127.0.0.1:18789";
        AuthTypeCombo.SelectedIndex = s.UsePasswordAuth ? 1 : 0;
        TokenBox.Text = s.GatewayToken ?? "";
        PasswordBox.Password = s.GatewayPassword ?? "";
        UpdateAuthVisibility();

        // 语音
        var speechCfg = ConfigService.GetSpeechConfig();
        ModeCombo.SelectedIndex = (int)speechCfg.Mode;
        ThresholdSlider.Value = speechCfg.WakeWordThreshold;
        UpdateWakeWordVisibility((int)speechCfg.Mode);
        _capturedPttKey = KeyInterop.KeyFromVirtualKey(speechCfg.PttVirtualKey);
        UpdatePttKeyDisplay();
        SpeechServerUrl.Text = speechCfg.ServerUrl ?? "";

        Loaded += (_, _) => { UrlBox.SelectAll(); UrlBox.Focus(); };
    }

    // ════════ 外观：颜色选择 ════════
    private void ColorButton_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is string color)
            TextColor = color;
    }

    // ════════ 外观：全局快捷键捕获 ════════
    private void HotkeyBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _capturingHotkey = true;
        HotkeyDisplay = "按下新快捷键...";
        HotkeyBox.Focus();
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin ||
            key == Key.Escape)
        {
            if (key == Key.Escape) { _capturingHotkey = false; HotkeyDisplay = HotkeyToString(_hotkeyMod, _hotkeyKey); }
            return;
        }

        var mod = Keyboard.Modifiers;
        _hotkeyMod = mod;
        _hotkeyKey = key;
        _capturingHotkey = false;
        HotkeyDisplay = HotkeyToString(mod, key);
        _mainWindow.RegisterHotkey(mod, key);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    // ════════ 连接：认证方式切换 ════════
    private void AuthTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateAuthVisibility();
    }

    private void UpdateAuthVisibility()
    {
        var isPassword = AuthTypeCombo.SelectedIndex == 1;
        TokenLabel.Visibility = isPassword ? Visibility.Collapsed : Visibility.Visible;
        TokenBox.Visibility = isPassword ? Visibility.Collapsed : Visibility.Visible;
        PasswordLabel.Visibility = isPassword ? Visibility.Visible : Visibility.Collapsed;
        PasswordBox.Visibility = isPassword ? Visibility.Visible : Visibility.Collapsed;
    }

    // ════════ 语音：PTT 按键捕获 ════════
    private void PttKeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _capturingPttKey = true;
        PttKeyBox.Text = "按下任意键...";
    }

    private void PttKeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingPttKey) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.None) return;
        if (key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LWin || key == Key.RWin)
            return;

        _capturedPttKey = key;
        UpdatePttKeyDisplay();
        ModeCombo.Focus();
    }

    private void UpdatePttKeyDisplay()
    {
        var keyName = _capturedPttKey switch
        {
            Key.F12 => "F12", Key.F11 => "F11", Key.F10 => "F10", Key.F9 => "F9",
            Key.F8 => "F8", Key.F7 => "F7", Key.F6 => "F6", Key.F5 => "F5",
            Key.F4 => "F4", Key.F3 => "F3", Key.F2 => "F2", Key.F1 => "F1",
            Key.Space => "空格键", _ => _capturedPttKey.ToString()
        };
        PttKeyBox.Text = keyName;
        PttHint.Text = $"当前按键: {keyName}（VK=0x{KeyInterop.VirtualKeyFromKey(_capturedPttKey):X2}）";
    }

    // ════════ 语音：唤醒词配置区 ════════
    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateWakeWordVisibility(ModeCombo.SelectedIndex);
    }

    private void ThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAML 加载期间 Minimum/Maximum 应用会触发 ValueChanged，ThresholdValue 尚未创建（null 防护）
        if (ThresholdValue == null) return;
        ThresholdValue.Text = e.NewValue.ToString("F2");
    }

    private void UpdateWakeWordVisibility(int modeIndex)
    {
        // XAML 加载期间 SelectionChanged 会在 WakeWordPanel 创建前触发（null 防护）
        if (WakeWordPanel == null) return;
        WakeWordPanel.Visibility = modeIndex == (int)SpeechMode.WakeWord ? Visibility.Visible : Visibility.Collapsed;
    }

    // ════════ 确认：保存所有设置 ════════
    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        // ── 外观 ──
        var c = ConfigService.Load();
        c.Opacity = _windowOpacity;
        c.TextOpacity = _textOpacity;
        c.TextColor = _textColor;
        c.HotkeyMod = _hotkeyMod;
        c.HotkeyKey = _hotkeyKey;

        // ── 连接 ──
        var newUrl = string.IsNullOrWhiteSpace(UrlBox.Text) ? "ws://127.0.0.1:18789" : UrlBox.Text.Trim();
        var usePassword = AuthTypeCombo.SelectedIndex == 1;
        var newToken = usePassword ? null : TokenBox.Text?.Trim();
        var newPassword = usePassword ? PasswordBox.Password : null;

        var connectionChanged = c.GatewayUrl != newUrl ||
                                c.UsePasswordAuth != usePassword ||
                                c.GatewayToken != newToken ||
                                c.GatewayPassword != newPassword;

        c.GatewayUrl = newUrl;
        c.UsePasswordAuth = usePassword;
        c.GatewayToken = newToken;
        c.GatewayPassword = newPassword;
        c.IsConfigured = true;

        // ── 语音 ──
        var speechCfg = ConfigService.GetSpeechConfig();
        var newPttVk = KeyInterop.VirtualKeyFromKey(_capturedPttKey);
        var newServerUrl = string.IsNullOrWhiteSpace(SpeechServerUrl.Text) ? null : SpeechServerUrl.Text.Trim();
        var newMode = (SpeechMode)ModeCombo.SelectedIndex;
        var newThreshold = Math.Round(ThresholdSlider.Value, 2);
        var speechChanged = speechCfg.PttVirtualKey != newPttVk
                            || speechCfg.Mode != newMode
                            || speechCfg.WakeWordThreshold != newThreshold
                            || speechCfg.ServerUrl != newServerUrl;
        speechCfg.Mode = newMode;
        speechCfg.PttVirtualKey = newPttVk;
        speechCfg.WakeWordThreshold = newThreshold;
        speechCfg.ServerUrl = newServerUrl;
        speechCfg.IsConfigured = true;
        c.Speech = speechCfg;

        ConfigService.Save();

        // 连接变更 -> 重连
        if (connectionChanged)
            _ = _mainWindow.Reconnect();

        // 语音变更：模式/阈值变化需要重启语音服务应用；仅 PTT 键变化则热更新
        if (speechChanged && App.SpeechService.IsEnabled)
        {
            if (speechCfg.Mode != _origSpeechMode || speechCfg.WakeWordThreshold != _origSpeechThreshold)
            {
                App.SpeechService.Disable();
                App.SpeechService.Enable(newPttVk, newMode, newThreshold, speechCfg.WakeWordCooldown);
            }
            else
            {
                App.SpeechService.UpdatePttKey(newPttVk);
            }
        }

        DialogResult = true;
        Close();
    }

    // ════════ 取消：恢复外观原始值 ════════
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.SetWindowOpacity(_origWindowOpacity);
        _mainWindow.SetTextOpacity(_origTextOpacity);
        _mainWindow.SetTextColor(_origTextColor);
        _mainWindow.RegisterHotkey(_origHotkeyMod, _origHotkeyKey);
        DialogResult = false;
        Close();
    }

    // ════════ 窗口交互 ════════
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) CancelButton_Click(sender, e);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == this) DragMove();
    }

    // ════════ Helpers ════════
    private static string HotkeyToString(ModifierKeys mod, Key key)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (mod.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mod.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mod.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mod.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join(" + ", parts);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
