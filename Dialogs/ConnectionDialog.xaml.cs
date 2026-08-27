using System.Windows;
using System.Windows.Controls;
using Clawbrower.Services;
using KKey = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using ComboBox = System.Windows.Controls.ComboBox;

namespace Clawbrower.Dialogs;

public partial class ConnectionDialog : Window
{
    public string? GatewayUrl { get; private set; }
    public string? GatewayToken { get; private set; }
    public string? GatewayPassword { get; private set; }
    public bool UsePasswordAuth { get; private set; }

    /// <summary>Gateway 连接历史（下拉项，选中自动填充凭据）。</summary>
    public List<GatewayHistoryItem> HistoryItems { get; } = ConfigService.GetGatewayHistoryItems();

    public ConnectionDialog(string url, string? token, string? password, bool usePasswordAuth)
    {
        InitializeComponent();
        DataContext = this;
        UrlBox.Text = url;
        UsePasswordAuth = usePasswordAuth;
        AuthTypeCombo.SelectedIndex = usePasswordAuth ? 1 : 0;
        TokenBox.Text = token ?? "";
        PasswordBox.Password = password ?? "";
        UpdateAuthVisibility();

        Loaded += (_, _) => UrlBox.Focus();
    }

    public static ConnectionDialog? Show(Window owner, string url, string? token, string? password, bool usePasswordAuth)
    {
        var dlg = new ConnectionDialog(url, token, password, usePasswordAuth) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg : null;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        GatewayUrl = UrlBox.Text.Trim();
        UsePasswordAuth = AuthTypeCombo.SelectedIndex == 1;
        GatewayToken = UsePasswordAuth ? null : TokenBox.Text.Trim();
        GatewayPassword = UsePasswordAuth ? PasswordBox.Password : null;
        ConfigService.AddGatewayHistory(GatewayUrl, UsePasswordAuth, GatewayToken, GatewayPassword);
        DialogResult = true;
        Close();
    }

    // ════════ 选择历史地址 → 自动带出认证方式与凭据 ════════
    private void UrlBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender is not ComboBox cb || cb.SelectedItem is not GatewayHistoryItem item) return;

        // 编辑框只显示纯地址（下拉行才展示认证方式摘要）
        cb.Text = item.Url;
        AuthTypeCombo.SelectedIndex = item.UsePasswordAuth ? 1 : 0;
        TokenBox.Text = item.Token ?? "";
        PasswordBox.Password = item.Password ?? "";
        UpdateAuthVisibility();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == KKey.Enter)
        {
            GatewayUrl = UrlBox.Text.Trim();
            UsePasswordAuth = AuthTypeCombo.SelectedIndex == 1;
            GatewayToken = UsePasswordAuth ? null : TokenBox.Text.Trim();
            GatewayPassword = UsePasswordAuth ? PasswordBox.Password : null;
            ConfigService.AddGatewayHistory(GatewayUrl, UsePasswordAuth, GatewayToken, GatewayPassword);
            DialogResult = true;
            Close();
        }
        else if (e.Key == KKey.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == this) DragMove();
    }

    private void AuthTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UsePasswordAuth = AuthTypeCombo.SelectedIndex == 1;
        UpdateAuthVisibility();
    }

    private void UpdateAuthVisibility()
    {
        var isPassword = AuthTypeCombo.SelectedIndex == 1;
        TokenBox.Visibility = isPassword ? Visibility.Collapsed : Visibility.Visible;
        PasswordBox.Visibility = isPassword ? Visibility.Visible : Visibility.Collapsed;
    }
}
