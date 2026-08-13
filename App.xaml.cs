using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using Clawbrower.Services;
using Clawbrower.Dialogs;
using WF = System.Windows.Forms;
using FontStyle = System.Drawing.FontStyle;

namespace Clawbrower;

public partial class App : System.Windows.Application
{
    private WF.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private static Bitmap? _trayBitmap;
    public static McpService McpService { get; } = new();
    public static SpeechService SpeechService { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global exception handlers
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error($"UI exception: {args.Exception}");
            args.Handled = true; // prevent crash
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Logger.Error($"Unhandled exception: {ex}");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error($"Unobserved task exception: {args.Exception}");
            args.SetObserved();
        };

        Logger.Info("App starting");

        // 清理上次崩溃可能残留的 MCP/frpc 孤儿进程（端口占用）
        McpService.CleanupOrphanedProcesses();

        // Self-test: verify MarkdownParser table support
        VerifyMarkdownParser();

        _mainWindow = new MainWindow();

        // 首次启动弹出连接设置
        if (!ConfigService.Load().IsConfigured)
        {
            _mainWindow.Show();
            OpenConnectionSettings();
            // 设置完成后触发连接
            _ = _mainWindow.Reconnect();
        }

        _trayIcon = new WF.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Visible = true,
            Text = "Clawbrower — OpenClaw 悬浮窗"
        };
        _trayIcon.DoubleClick += (_, _) => ShowWindow();

        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("显示/隐藏", null, (_, _) => ToggleWindow());
        menu.Items.Add("设置...", null, (_, _) => OpenSettings());
        menu.Items.Add("重连", null, (_, _) => _ = _mainWindow!.Reconnect());
        menu.Items.Add(new WF.ToolStripSeparator());

        // MCP 远程控制菜单
        var mcpStartItem = new WF.ToolStripMenuItem("启动 MCP 远程控制", null, (_, _) => StartMcp());
        var mcpStopItem = new WF.ToolStripMenuItem("停止 MCP 远程控制", null, (_, _) => StopMcp());
        var mcpSettingsItem = new WF.ToolStripMenuItem("MCP 设置...", null, (_, _) => OpenMcpSettings());
        mcpStopItem.Enabled = false;
        menu.Items.Add(mcpStartItem);
        menu.Items.Add(mcpStopItem);
        menu.Items.Add(mcpSettingsItem);

        // MCP 状态变更时更新菜单
        McpService.OnStatusChanged += (status) =>
        {
            _mainWindow?.Dispatcher.Invoke(() =>
            {
                mcpStartItem.Enabled = status != McpStatus.Running && status != McpStatus.Starting;
                mcpStopItem.Enabled = status == McpStatus.Running;
                mcpStartItem.Text = status switch
                {
                    McpStatus.Running => "MCP 运行中",
                    McpStatus.Starting => "MCP 启动中...",
                    McpStatus.Error => "MCP 异常",
                    _ => "MCP 远程控制"
                };
            });
        };

        // MCP 详细操作消息 → 显示在聊天窗口
        McpService.OnMessage += (msg) =>
        {
            _mainWindow?.Dispatcher.Invoke(() =>
            {
                if (_mainWindow.DataContext is ViewModels.MainViewModel vm)
                    vm.AddSystemMessage(msg);
            });
        };


        // 语音输入菜单
        var speechToggleItem = new WF.ToolStripMenuItem("开启语音输入", null, (_, _) => ToggleSpeech());
        menu.Items.Add(speechToggleItem);

        // SpeechService 事件 -> MainViewModel 桥接
        SpeechService.OnStatusMessage += (msg) =>
        {
            _mainWindow?.Dispatcher.Invoke(() =>
            {
                if (_mainWindow.DataContext is ViewModels.MainViewModel vm)
                    vm.AddSystemMessage(msg);
            });
        };
        SpeechService.OnTranscript += (text) =>
        {
            _mainWindow?.Dispatcher.Invoke(() =>
            {
                if (_mainWindow.DataContext is ViewModels.MainViewModel vm)
                    vm.AddSpeechTranscript(text);
            });
        };
        SpeechService.OnReply += (text) =>
        {
            _mainWindow?.Dispatcher.Invoke(() =>
            {
                if (_mainWindow.DataContext is ViewModels.MainViewModel vm)
                    vm.AddSpeechReply(text);
            });
        };
        SpeechService.OnEnabledChanged += (enabled) =>
        {
            _mainWindow?.Dispatcher.Invoke(() =>
            {
                speechToggleItem.Text = enabled ? "关闭语音输入" : "开启语音输入";
                speechToggleItem.Checked = enabled;
            });
        };
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ShutdownApp());
        _trayIcon.ContextMenuStrip = menu;

        _mainWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            _mainWindow.Hide();
            Logger.Info("Window hidden to tray");
        };

        _mainWindow.Show();
        Logger.Info("MainWindow shown");
    }

    private void ShowWindow()
    {
        _mainWindow?.Dispatcher.Invoke(() =>
        {
            if (_mainWindow.WindowState == WindowState.Maximized)
                _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.RequestFocusInput();
            _mainWindow?.Show();
            _mainWindow?.Activate();
            Logger.Info("Window shown from tray");
        });
    }

    private void ToggleWindow()
    {
        if (_mainWindow == null) return;
        if (_mainWindow.Visibility == Visibility.Visible)
            _mainWindow.Hide();
        else
            ShowWindow();
    }

    private void OpenSettings()
    {
        _mainWindow?.Dispatcher.Invoke(() =>
        {
            var win = new SettingsWindow(_mainWindow);
            win.Owner = _mainWindow;
            win.ShowDialog();
        });
    }

    private void OpenConnectionSettings()
    {
        _mainWindow?.Dispatcher.Invoke(() =>
        {
            var cfg = ConfigService.Load();
            // 获取不含密码的原始 URL
            var baseUrl = cfg.GatewayUrl ?? "ws://127.0.0.1:18789";
            var result = ConnectionDialog.Show(_mainWindow, baseUrl, cfg.GatewayToken, cfg.GatewayPassword, cfg.UsePasswordAuth);
            if (result == null) return; // cancelled

            var c = ConfigService.Load();
            c.GatewayUrl = string.IsNullOrWhiteSpace(result.GatewayUrl) ? "ws://127.0.0.1:18789" : result.GatewayUrl.Trim();
            c.UsePasswordAuth = result.UsePasswordAuth;
            c.GatewayToken = result.UsePasswordAuth ? null : result.GatewayToken?.Trim();
            c.GatewayPassword = result.UsePasswordAuth ? result.GatewayPassword : null;
            c.IsConfigured = true;
            ConfigService.Save();
            _ = _mainWindow!.Reconnect();
        });
    }

    private void StartMcp()
    {
        _mainWindow?.Dispatcher.Invoke(() =>
        {
            var existing = ConfigService.GetMcpConfig();
            if (!existing.IsConfigured)
            {
                var result = McpConfigDialog.Show(_mainWindow, existing);
                if (result == null) return; // cancelled
                ConfigService.SetMcpConfig(result);
                existing = result;
            }
            _ = McpService.StartAsync(existing);
        });
    }

    private void StopMcp()
    {
        McpService.Stop();
    }

    private void OpenMcpSettings()
    {
        _mainWindow?.Dispatcher.Invoke(() =>
        {
            var existing = ConfigService.GetMcpConfig();
            var result = McpConfigDialog.Show(_mainWindow, existing);
            if (result == null) return; // cancelled

            ConfigService.SetMcpConfig(result);

            // 如果正在运行，提示需要重启
            if (McpService.Status == McpStatus.Running)
            {
                if (_mainWindow.DataContext is ViewModels.MainViewModel vm)
                    vm.AddSystemMessage("MCP 配置已更新，将自动重启以应用新配置...");
                McpService.Stop();
                // 等待停止后重新启动
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1500);
                    _mainWindow.Dispatcher.Invoke(() => _ = McpService.StartAsync(result));
                });
            }
        });
    }

    public void ToggleSpeech()
    {
        _mainWindow?.Dispatcher.Invoke(() =>
        {
            if (SpeechService.IsEnabled)
            {
                SpeechService.Disable();
                return;
            }

            var cfg = ConfigService.GetSpeechConfig();

            // 语音服务器地址为空 -> 首次连接弹窗填写
            if (string.IsNullOrWhiteSpace(cfg.ServerUrl))
            {
                var input = InputDialog.Show(_mainWindow!,
                    "语音服务器地址",
                    "首次使用语音，请输入语音服务器地址（ws://主机:端口/speech）：",
                    "ws://127.0.0.1:9529/speech");
                if (input == null) return; // 用户取消，不开启语音
                cfg.ServerUrl = input;
            }

            // PTT 按键首次默认
            if (!cfg.IsConfigured)
            {
                cfg.PttVirtualKey = 0x7B; // F12
                cfg.Mode = SpeechMode.PTT;
                cfg.IsConfigured = true;
            }
            ConfigService.SetSpeechConfig(cfg);
            SpeechService.Enable(cfg.PttVirtualKey, cfg.Mode, cfg.WakeWordThreshold, cfg.WakeWordCooldown);
        });
    }

    private void ShutdownApp()
    {
        Logger.Info("App shutting down");
        SpeechService.Dispose();
        McpService.Stop();
        _trayIcon?.Dispose();
        _mainWindow?.Dispatcher.Invoke(() => _mainWindow?.Close());
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SpeechService.Dispose();
        McpService.Stop();
        _trayIcon?.Dispose();
        _trayBitmap?.Dispose();
        Logger.Info("App exited");
        base.OnExit(e);
    }

    private static Icon CreateTrayIcon()
    {
        _trayBitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(_trayBitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var brush = new SolidBrush(Color.FromArgb(103, 140, 179));
        g.FillEllipse(brush, 2, 2, 28, 28);

        using var font = new Font("Segoe UI", 16, FontStyle.Bold);
        using var textBrush = new SolidBrush(Color.White);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("C", font, textBrush, new RectangleF(2, 2, 28, 28), fmt);

        return Icon.FromHandle(_trayBitmap.GetHicon());
    }

    private static void VerifyMarkdownParser()
    {
        try
        {
            var testTable = "| 项目 | 旧 | 新 |\n|------|----|----|\n| 名称 | 士兵3254 | **士兵9527** |";
            var blocks = Services.MarkdownParser.ParseBlocks(testTable);

            var hasTable = false;
            foreach (var block in blocks)
            {
                if (block is Models.MdTable t
                    && t.Headers.Count == 3
                    && t.Headers[0] == "项目" && t.Headers[1] == "旧" && t.Headers[2] == "新"
                    && t.Rows.Count == 1
                    && t.Rows[0][0] == "名称" && t.Rows[0][1] == "士兵3254" && t.Rows[0][2] == "**士兵9527**")
                {
                    hasTable = true;
                    break;
                }
            }
            Logger.Info($"MarkdownParser ParseBlocks self-test: {(hasTable ? "PASS" : "FAIL")}");

            var cellInlines = Services.MarkdownParser.ParseInlineLine("**士兵9527**");
            var hasBold = cellInlines.Count == 1 && cellInlines[0] is System.Windows.Documents.Bold;
            Logger.Info($"MarkdownParser Inline self-test: {(hasBold ? "PASS" : "FAIL")}");
        }
        catch (Exception ex)
        {
            Logger.Error($"MarkdownParser self-test FAILED: {ex.Message}");
        }
    }
}
