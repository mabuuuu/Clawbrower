using System.Runtime.InteropServices;

namespace Clawbrower.Services;

/// <summary>
/// 低级键盘钩子（WH_KEYBOARD_LL），用于全局监听 PTT 按键的按下/松开。
/// 必须在 UI 线程（有消息循环的线程）上 Install/Uninstall。
/// </summary>
public class KeyboardHookService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _proc;  // 必须保持引用防止 GC 回收
    private int _targetVk;
    private bool _isDown;

    /// <summary>目标按键按下时触发（仅一次，按住不重复触发）</summary>
    public event Action? OnKeyDown;

    /// <summary>目标按键松开时触发</summary>
    public event Action? OnKeyUp;

    /// <summary>当前是否已安装钩子</summary>
    public bool IsInstalled => _hookId != IntPtr.Zero;

    /// <summary>
    /// 安装全局键盘钩子，监听指定虚拟键码。
    /// 必须在 UI 线程调用。
    /// </summary>
    public void Install(int virtualKey)
    {
        if (_hookId != IntPtr.Zero) Uninstall();

        _targetVk = virtualKey;
        _isDown = false;
        _proc = HookCallback;

        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName!), 0);

        if (_hookId == IntPtr.Zero)
            Logger.Error($"KeyboardHook SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
        else
            Logger.Info($"KeyboardHook installed for VK=0x{virtualKey:X2}");
    }

    /// <summary>卸载钩子</summary>
    public void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            _isDown = false;
            Logger.Info("KeyboardHook uninstalled");
        }
    }

    /// <summary>切换监听的按键（重新安装钩子）</summary>
    public void ChangeKey(int virtualKey)
    {
        if (_hookId != IntPtr.Zero && _targetVk == virtualKey) return;
        if (_hookId != IntPtr.Zero)
        {
            Uninstall();
            Install(virtualKey);
        }
        else
        {
            _targetVk = virtualKey;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (k.vkCode == _targetVk)
            {
                int msg = wParam.ToInt32();
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                if (isDown && !_isDown)
                {
                    _isDown = true;
                    Logger.Info($"PTT KeyDown: VK=0x{_targetVk:X2}");
                    OnKeyDown?.Invoke();
                }
                else if (isUp && _isDown)
                {
                    _isDown = false;
                    Logger.Info($"PTT KeyUp: VK=0x{_targetVk:X2}");
                    OnKeyUp?.Invoke();
                }
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Uninstall();
        GC.SuppressFinalize(this);
    }

    ~KeyboardHookService() => Uninstall();
}
