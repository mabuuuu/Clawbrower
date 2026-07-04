using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace Clawbrower.Services;

public static class Logger
{
    private static readonly string _dir;
    private static string _path;
    private static string _currentDate = "";

    static Logger()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clawbrower");
        Directory.CreateDirectory(_dir);
        RefreshPath();
    }

    private static void RefreshPath()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        _path = Path.Combine(_dir, $"app-{today}.log");
        _currentDate = today;
    }

    [Conditional("DEBUG")]
    public static void Debug(string msg, [CallerMemberName] string? caller = null)
        => Write("DEBUG", msg, caller);

    public static void Info(string msg, [CallerMemberName] string? caller = null)
        => Write("INFO", msg, caller);

    public static void Error(string msg, [CallerMemberName] string? caller = null)
        => Write("ERROR", msg, caller);

    private static readonly object _lock = new();
    private static void Write(string level, string msg, string? caller)
    {
        var now = DateTime.Now;
        var today = now.ToString("yyyy-MM-dd");
        if (today != _currentDate) RefreshPath();
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{caller}] {msg}";
        lock (_lock) File.AppendAllText(_path, line + Environment.NewLine);
    }

    public static string LogPath => _path;
}
