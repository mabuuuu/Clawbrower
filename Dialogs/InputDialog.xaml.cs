using System.Windows;
using KKey = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace Clawbrower.Dialogs;

public partial class InputDialog : Window
{
    public string? Result { get; private set; }

    public InputDialog(string title, string prompt, string placeholder = "")
    {
        InitializeComponent();
        TitleText.Text = title;
        PromptText.Text = prompt;
        if (!string.IsNullOrEmpty(placeholder))
        {
            InputBox.Text = placeholder;
            InputBox.SelectAll();
        }
        Loaded += (_, _) => InputBox.Focus();
    }

    public static string? Show(Window owner, string title, string prompt, string placeholder = "")
    {
        var dlg = new InputDialog(title, prompt, placeholder) { Owner = owner };
        dlg.ShowDialog();
        return dlg.Result;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        Result = InputBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == KKey.Enter)
        {
            Result = InputBox.Text.Trim();
            DialogResult = true;
            Close();
        }
        else if (e.Key == KKey.Escape)
        {
            Result = null;
            DialogResult = false;
            Close();
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == this) DragMove();
    }
}
