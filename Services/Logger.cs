using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace Clawbrower.Services;

public static class Logger
{
    private static readonly string _path;

    static Logger()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clawbrower");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "app.log");
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
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{caller}] {msg}";
        lock (_lock) File.AppendAllText(_path, line + Environment.NewLine);
    }

    public static string LogPath => _path;
}
