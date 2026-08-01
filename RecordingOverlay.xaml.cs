using System.Windows;
using Clawbrower.Services;
using Media = System.Windows.Media;

namespace Clawbrower;

/// <summary>
/// 录音中的浮层提示窗口。Topmost，即使主窗口最小化也能看到。
/// </summary>
public partial class RecordingOverlay : Window
{
    private static readonly Media.Color SilentColor = Media.Color.FromRgb(0xEF, 0x44, 0x44);   // 红（录音静默）
    private static readonly Media.Color SpeakingColor = Media.Color.FromRgb(0x22, 0xC5, 0x5E); // 绿（说话中）
    private static readonly Media.Color ThinkingColor = Media.Color.FromRgb(0x3B, 0x82, 0xF6); // 蓝（思考中）

    public RecordingOverlay()
    {
        InitializeComponent();
        PositionBottomCenter();
    }

    /// <summary>显示浮层</summary>
    public new void Show()
    {
        PositionBottomCenter();
        SetState(SpeechService.SpeechState.Listening); // 默认静默状态（红点）
        base.Show();
    }

    /// <summary>按语音状态更新浮层文字与颜色</summary>
    public void SetState(SpeechService.SpeechState state)
    {
        switch (state)
        {
            case SpeechService.SpeechState.Recording:
                StateText.Text = "说话中...";
                PulseBrush.Color = SilentColor; // 声音检测到后由 SetSpeaking 变绿
                break;
            case SpeechService.SpeechState.Waiting:
                StateText.Text = "思考中...";
                PulseBrush.Color = ThinkingColor;
                break;
            case SpeechService.SpeechState.Playing:
                StateText.Text = "说话中...";
                PulseBrush.Color = SpeakingColor;
                break;
            default:
                StateText.Text = "说话中...";
                PulseBrush.Color = SilentColor;
                break;
        }
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
