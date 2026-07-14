using System.Windows;
using System.Windows.Input;
using Clawbrower.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace Clawbrower.Dialogs;

public partial class McpConfigDialog : Window
{
    public McpConfig Config { get; private set; }

    public McpConfigDialog(McpConfig? existing = null)
    {
        InitializeComponent();

        var cfg = existing ?? new McpConfig();
        DeviceNameBox.Text = cfg.DeviceName;
        LocalPortBox.Text = cfg.LocalPort.ToString();
        RemotePortBox.Text = cfg.RemotePort.ToString();
        FrpServerBox.Text = cfg.FrpServerAddr;
        FrpServerPortBox.Text = cfg.FrpServerPort.ToString();
        FrpAuthTokenBox.Password = cfg.FrpAuthToken;

        Config = cfg;

        Loaded += (_, _) => DeviceNameBox.Focus();
    }

    public static McpConfig? Show(Window owner, McpConfig? existing = null)
    {
        var dlg = new McpConfigDialog(existing) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Config : null;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DeviceNameBox.Text))
        {
            System.Windows.MessageBox.Show("请输入设备名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            DeviceNameBox.Focus();
            return;
        }

        if (!int.TryParse(LocalPortBox.Text, out var localPort) || localPort <= 0 || localPort > 65535)
        {
            System.Windows.MessageBox.Show("本地端口必须为 1-65535 的整数", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(RemotePortBox.Text, out var remotePort) || remotePort <= 0 || remotePort > 65535)
        {
            System.Windows.MessageBox.Show("远程端口必须为 1-65535 的整数", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(FrpServerPortBox.Text, out var frpServerPort) || frpServerPort <= 0 || frpServerPort > 65535)
        {
            System.Windows.MessageBox.Show("FRP 服务器端口必须为 1-65535 的整数", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Config.DeviceName = DeviceNameBox.Text.Trim();
        Config.LocalPort = localPort;
        Config.RemotePort = remotePort;
        Config.FrpServerAddr = FrpServerBox.Text.Trim();
        Config.FrpServerPort = frpServerPort;
        Config.FrpAuthToken = FrpAuthTokenBox.Password;
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
        if (e.Key == Key.Escape) { DialogResult = false; Close(); }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == this) DragMove();
    }
}
