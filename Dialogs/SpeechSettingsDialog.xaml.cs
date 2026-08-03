using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Clawbrower.Services;
using SpeechMode = Clawbrower.Services.SpeechMode;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace Clawbrower.Dialogs;

public partial class SpeechSettingsDialog : Window
{
    public SpeechConfig Config { get; private set; }

    private Key _capturedKey = Key.F12;

    public SpeechSettingsDialog(SpeechConfig? existing = null)
    {
        InitializeComponent();

        var cfg = existing ?? new SpeechConfig();
        Config = cfg;

        ModeCombo.SelectedIndex = (int)cfg.Mode;
        ThresholdSlider.Value = cfg.WakeWordThreshold;
        UpdateWakeWordVisibility((int)cfg.Mode);

        // 从 VK 转回 Key 显示
        _capturedKey = KeyInterop.KeyFromVirtualKey(cfg.PttVirtualKey);
        UpdateKeyDisplay();

        Loaded += (_, _) => ModeCombo.Focus();
    }

    public static SpeechConfig? Show(Window owner, SpeechConfig? existing = null)
    {
        var dlg = new SpeechSettingsDialog(existing) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Config : null;
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateWakeWordVisibility(ModeCombo.SelectedIndex);
    }

    private void UpdateWakeWordVisibility(int modeIndex)
    {
        WakeWordPanel.Visibility = modeIndex == (int)SpeechMode.WakeWord ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ThresholdValue.Text = e.NewValue.ToString("F2");
    }

    private void PttKeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        PttKeyBox.Text = "按下任意键...";
    }

    private void PttKeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.None) return;

        // 忽略单独的修饰键
        if (key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LWin || key == Key.RWin)
        {
            return;
        }

        _capturedKey = key;
        UpdateKeyDisplay();

        // 移走焦点避免闪烁
        ModeCombo.Focus();
    }

    private void UpdateKeyDisplay()
    {
        var keyName = _capturedKey switch
        {
            Key.F12 => "F12",
            Key.F11 => "F11",
            Key.F10 => "F10",
            Key.F9 => "F9",
            Key.F8 => "F8",
            Key.F7 => "F7",
            Key.F6 => "F6",
            Key.F5 => "F5",
            Key.F4 => "F4",
            Key.F3 => "F3",
            Key.F2 => "F2",
            Key.F1 => "F1",
            Key.Space => "空格键",
            Key.OemTilde => "~ 键",
            _ => _capturedKey.ToString()
        };
        PttKeyBox.Text = keyName;
        HintText.Text = $"当前按键: {keyName}（VK=0x{KeyInterop.VirtualKeyFromKey(_capturedKey):X2}）";
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        Config.Mode = (SpeechMode)ModeCombo.SelectedIndex;
        Config.PttVirtualKey = KeyInterop.VirtualKeyFromKey(_capturedKey);
        Config.WakeWordThreshold = Math.Round(ThresholdSlider.Value, 2);
        Config.IsConfigured = true;

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == this) DragMove();
    }
}
