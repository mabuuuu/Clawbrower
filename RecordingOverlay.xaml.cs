using System.Windows;

namespace Clawbrower;

/// <summary>
/// 录音中的浮层提示窗口。Topmost，即使主窗口最小化也能看到。
/// </summary>
public partial class RecordingOverlay : Window
{
    public RecordingOverlay()
    {
        InitializeComponent();
        PositionBottomCenter();
    }

    /// <summary>显示浮层</summary>
    public new void Show()
    {
        PositionBottomCenter();
        base.Show();
    }

    /// <summary>定位到屏幕底部居中</summary>
    private void PositionBottomCenter()
    {
        var area = SystemParameters.WorkArea;
        Left = (area.Right - Width) / 2;
        Top = area.Bottom - Height - 80;
    }
}
