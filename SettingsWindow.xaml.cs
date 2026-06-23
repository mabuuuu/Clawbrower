using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Clawbrower.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Media = System.Windows.Media;

namespace Clawbrower;

public partial class SettingsWindow : Window, INotifyPropertyChanged
{
    private readonly MainWindow _mainWindow;
    private bool _capturingHotkey;

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

    private string _hotkeyDisplay = "Alt + C";
    public string HotkeyDisplay
    {
        get => _hotkeyDisplay;
        set { _hotkeyDisplay = value; OnPropertyChanged(); }
    }

    private ModifierKeys _hotkeyMod = ModifierKeys.Alt;
    private Key _hotkeyKey = Key.C;

    public SettingsWindow(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
        var s = ConfigService.Load();
        _windowOpacity = s.Opacity;
        _textOpacity = s.TextOpacity;
        _textColor = s.TextColor;
        _hotkeyMod = s.HotkeyMod;
        _hotkeyKey = s.HotkeyKey;
        _hotkeyDisplay = HotkeyToString(_hotkeyMod, _hotkeyKey);

        InitializeComponent();
        DataContext = this;

        Closed += (_, _) =>
        {
            var c = ConfigService.Load();
            c.Opacity = _windowOpacity;
            c.TextOpacity = _textOpacity;
            c.TextColor = _textColor;
            c.HotkeyMod = _hotkeyMod;
            c.HotkeyKey = _hotkeyKey;
            ConfigService.Save();
        };
    }

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

        // Ignore modifier-only presses
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

    private void ColorButton_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.Tag is string color)
            TextColor = color;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
