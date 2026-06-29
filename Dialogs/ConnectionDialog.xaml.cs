using System.Windows;
using KKey = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace Clawbrower.Dialogs;

public partial class ConnectionDialog : Window
{
    public string? Ip { get; private set; }
    public string? Port { get; private set; }

    public ConnectionDialog(string ip, string port)
    {
        InitializeComponent();
        IpBox.Text = ip;
        IpBox.SelectAll();
        PortBox.Text = port;
        Loaded += (_, _) => IpBox.Focus();
    }

    public static (string? ip, string? port) Show(Window owner, string ip, string port)
    {
        var dlg = new ConnectionDialog(ip, port) { Owner = owner };
        dlg.ShowDialog();
        return (dlg.Ip, dlg.Port);
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        Ip = IpBox.Text.Trim();
        Port = PortBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Ip = null;
        Port = null;
        DialogResult = false;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == KKey.Enter)
        {
            Ip = IpBox.Text.Trim();
            Port = PortBox.Text.Trim();
            DialogResult = true;
            Close();
        }
        else if (e.Key == KKey.Escape)
        {
            Ip = null;
            Port = null;
            DialogResult = false;
            Close();
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == this) DragMove();
    }
}
