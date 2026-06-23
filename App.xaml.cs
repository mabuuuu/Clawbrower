using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using Clawbrower.Services;
using WF = System.Windows.Forms;
using FontStyle = System.Drawing.FontStyle;

namespace Clawbrower;

public partial class App : System.Windows.Application
{
    private WF.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private static Bitmap? _trayBitmap;

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

        // Self-test: verify MarkdownParser table support
        VerifyMarkdownParser();

        _mainWindow = new MainWindow();

        _trayIcon = new WF.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Visible = true,
            Text = "Clawbrower — OpenClaw 悬浮窗"
        };
        _trayIcon.DoubleClick += (_, _) => ShowWindow();

        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("显示/隐藏", null, (_, _) => ToggleWindow());
        menu.Items.Add("设置", null, (_, _) => OpenSettings());
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

    private void ShutdownApp()
    {
        Logger.Info("App shutting down");
        _trayIcon?.Dispose();
        _mainWindow?.Dispatcher.Invoke(() => _mainWindow?.Close());
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
