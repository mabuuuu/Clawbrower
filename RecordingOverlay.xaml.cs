using System.Windows;
using Media = System.Windows.Media;

namespace Clawbrower;

/// <summary>
/// 录音中的浮层提示窗口。Topmost，即使主窗口最小化也能看到。
/// </summary>
public partial class RecordingOverlay : Window
{
    private static readonly Media.Color SilentColor = Media.Color.FromRgb(0xEF, 0x44, 0x44);  // 红
    private static readonly Media.Color SpeakingColor = Media.Color.FromRgb(0x22, 0xC5, 0x5E); // 绿

    public RecordingOverlay()
    {
        InitializeComponent();
        PositionBottomCenter();
    }

    /// <summary>显示浮层</summary>
    public new void Show()
    {
        PositionBottomCenter();
        SetSpeaking(false); // 显示时默认静默状态（红点）
        base.Show();
    }

    /// <summary>设置是否检测到声音：true=绿点（说话中），false=红点（静默）</summary>
    public void SetSpeaking(bool speaking)
    {
        PulseBrush.Color = speaking ? SpeakingColor : SilentColor;
    }

    /// <summary>定位到屏幕底部居中</summary>
    private void PositionBottomCenter()
    {
        var area = SystemParameters.WorkArea;
        Left = (area.Right - Width) / 2;
        Top = area.Bottom - Height - 80;
    }
}
