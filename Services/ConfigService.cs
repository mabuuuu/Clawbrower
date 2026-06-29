using System.IO;
using System.Text.Json;

namespace Clawbrower.Services;

public static class ConfigService
{
    private static readonly string _dir;
    private static readonly string _path;
    private static Settings? _cache;

    static ConfigService()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clawbrower");
        _path = Path.Combine(_dir, "settings.json");
    }

    public static Settings Load()
    {
        if (_cache != null) return _cache;
        try
        {
            Directory.CreateDirectory(_dir);
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                _cache = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
                return _cache;
            }
        }
        catch (Exception ex) { Logger.Error($"Failed to load settings: {ex.Message}"); }
        _cache = new Settings();
        return _cache;
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            // Sanitize NaN values before serializing
            if (double.IsNaN(_cache!.WindowLeft)) _cache.WindowLeft = 0;
            if (double.IsNaN(_cache.WindowTop)) _cache.WindowTop = 0;
            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch (Exception ex) { Logger.Error($"Failed to save settings: {ex.Message}"); }
    }

    public static string? GetToken() => Load().GatewayToken;
    public static void SetToken(string token) { var s = Load(); s.GatewayToken = token; Save(); }

    public static string GetGatewayUrl() => Load().GatewayUrl ?? "ws://127.0.0.1:18789";
    public static void SetGatewayUrl(string url) { var s = Load(); s.GatewayUrl = url; Save(); }

}

public class Settings
{
    public string? GatewayToken { get; set; }
    public string? GatewayUrl { get; set; } = "ws://127.0.0.1:18789";
    public string? DeviceToken { get; set; }
    public double Opacity { get; set; } = 0.80;
    public double TextOpacity { get; set; } = 1.0;
    public string TextColor { get; set; } = "#EEEEEE";
    public System.Windows.Input.ModifierKeys HotkeyMod { get; set; } = System.Windows.Input.ModifierKeys.Alt;
    public System.Windows.Input.Key HotkeyKey { get; set; } = System.Windows.Input.Key.C;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 420;
    public double WindowHeight { get; set; } = 580;
}
