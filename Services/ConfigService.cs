using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Clawbrower.Services;

public static class ConfigService
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
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
            if (double.IsNaN(_cache!.WindowLeft)) _cache.WindowLeft = 0;
            if (double.IsNaN(_cache.WindowTop)) _cache.WindowTop = 0;
            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch (Exception ex) { Logger.Error($"Failed to save settings: {ex.Message}"); }
    }

    public static string? GetToken() => Load().GatewayToken;
    public static void SetToken(string token) { var s = Load(); s.GatewayToken = token; Save(); }

    public static string GetGatewayUrl()
    {
        var s = Load();
        return s.GatewayUrl ?? "ws://127.0.0.1:18789";
    }
    public static void SetGatewayUrl(string url) { var s = Load(); s.GatewayUrl = url; Save(); }

    private static string ToBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string GetDeviceId()
    {
        EnsureKeyPair();
        return Load().DeviceId!;
    }

    public static string GetPublicKeyBase64Url()
    {
        EnsureKeyPair();
        return Load().DevicePublicKey!;
    }

    public static string SignAuthPayload(string deviceId, string clientId, string clientMode,
        string role, string scopes, long signedAt, string nonce, string token)
    {
        var message = $"v2|{deviceId}|{clientId}|{clientMode}|{role}|{scopes}|{signedAt}|{token}|{nonce}";
        Logger.Info($"Sign message: {message}");
        var messageBytes = Utf8NoBom.GetBytes(message);

        var seedBytes = Convert.FromBase64String(Load().DevicePrivateKey!);
        var privateKey = new Ed25519PrivateKeyParameters(seedBytes, 0);
        var publicKey = privateKey.GeneratePublicKey();

        var signer = new Ed25519Signer();
        signer.Init(true, privateKey);
        signer.BlockUpdate(messageBytes, 0, messageBytes.Length);
        var signature = signer.GenerateSignature(); // 64 bytes

        // 本地验签
        var verifier = new Ed25519Signer();
        verifier.Init(false, publicKey);
        verifier.BlockUpdate(messageBytes, 0, messageBytes.Length);
        var verified = verifier.VerifySignature(signature);

        Logger.Info($"Local verify: {(verified ? "PASS" : "FAIL")}");
        Logger.Info($"Signature base64url: {ToBase64Url(signature)}");
        Logger.Info($"PublicKey base64url: {Load().DevicePublicKey}");
        Logger.Info($"DeviceId: {deviceId}");
        return ToBase64Url(signature);
    }

    private static void EnsureKeyPair()
    {
        var s = Load();
        if (!string.IsNullOrWhiteSpace(s.DeviceId)) return;

        // BouncyCastle Ed25519 密钥对生成
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var keyPair = generator.GenerateKeyPair();
        var privateKey = (Ed25519PrivateKeyParameters)keyPair.Private;
        var publicKey = (Ed25519PublicKeyParameters)keyPair.Public;
        var seed = privateKey.GetEncoded();       // 32 bytes
        var rawPublicKey = publicKey.GetEncoded(); // 32 bytes

        // deviceId = SHA256(publicKey raw bytes) hex
        var deviceId = BitConverter.ToString(SHA256.HashData(rawPublicKey)).Replace("-", "").ToLowerInvariant();

        // 自校验
        var signer = new Ed25519Signer();
        signer.Init(true, privateKey);
        var testMessage = Utf8NoBom.GetBytes("test");
        signer.BlockUpdate(testMessage, 0, testMessage.Length);
        var testSig = signer.GenerateSignature();

        var verifier = new Ed25519Signer();
        verifier.Init(false, publicKey);
        verifier.BlockUpdate(testMessage, 0, testMessage.Length);
        var verified = verifier.VerifySignature(testSig);

        Logger.Info($"PublicKey hex: {BitConverter.ToString(rawPublicKey).Replace("-", "").ToLowerInvariant()}");
        Logger.Info($"Self-test sign/verify: {(verified ? "PASS" : "FAIL")}");

        s.DevicePublicKey = ToBase64Url(rawPublicKey);
        s.DevicePrivateKey = Convert.ToBase64String(seed);
        s.DeviceId = deviceId;
        Save();
        Logger.Info($"Ed25519 keypair generated, deviceId={deviceId}");
    }

    public static string? GetPassword() => Load().GatewayPassword;
    public static string? GetDeviceToken() => Load().DeviceToken;

    public static McpConfig GetMcpConfig() => Load().Mcp ?? new McpConfig();
    public static void SetMcpConfig(McpConfig cfg) { var s = Load(); s.Mcp = cfg; Save(); }

    public static SpeechConfig GetSpeechConfig() => Load().Speech ?? new SpeechConfig();
    public static void SetSpeechConfig(SpeechConfig cfg) { var s = Load(); s.Speech = cfg; Save(); }

    /// <summary>
    /// 返回用户配置的语音服务器地址。未配置时返回 null（不再从 GatewayUrl 自动推导）。
    /// </summary>
    public static string? GetSpeechServerUrl()
    {
        var cfg = GetSpeechConfig();
        return string.IsNullOrWhiteSpace(cfg.ServerUrl) ? null : cfg.ServerUrl.Trim();
    }
}

public class Settings
{
    public string? GatewayToken { get; set; }
    public string? GatewayUrl { get; set; } = "ws://127.0.0.1:18789";
    public string? GatewayPassword { get; set; }
    public bool UsePasswordAuth { get; set; } = false;
    public bool IsConfigured { get; set; } = false;
    public string? DeviceId { get; set; }
    public string? DevicePublicKey { get; set; }
    public string? DevicePrivateKey { get; set; }
    public string? DeviceToken { get; set; }
    public double Opacity { get; set; } = 0.80;
    public double TextOpacity { get; set; } = 1.0;
    public string TextColor { get; set; } = "#EEEEEE";
    public System.Windows.Input.ModifierKeys HotkeyMod { get; set; } = System.Windows.Input.ModifierKeys.Alt;
    public System.Windows.Input.Key HotkeyKey { get; set; } = System.Windows.Input.Key.Z;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 420;
    public double WindowHeight { get; set; } = 580;
    public McpConfig? Mcp { get; set; }
    public SpeechConfig? Speech { get; set; }
}

public class McpConfig
{
    public string DeviceName { get; set; } = "";
    public int LocalPort { get; set; } = 9527;
    public int RemotePort { get; set; } = 9527;
    public string FrpServerAddr { get; set; } = "124.222.90.15";
    public int FrpServerPort { get; set; } = 7000;
    public string FrpAuthToken { get; set; } = "cl@w2026";
    public bool IsConfigured { get; set; } = false;
}

public enum SpeechMode
{
    PTT = 0,           // 按住说话
    WakeWord = 1,      // 唤醒词（说"二七二七"自动开始对话）
}

public class SpeechConfig
{
    /// <summary>语音功能是否启用</summary>
    public bool IsEnabled { get; set; } = false;

    /// <summary>语音交互模式</summary>
    public SpeechMode Mode { get; set; } = SpeechMode.PTT;

    /// <summary>PTT 按键的 Windows 虚拟键码 (VK)。默认 F12 = 0x7B = 123</summary>
    public int PttVirtualKey { get; set; } = 0x7B;

    /// <summary>语音服务器 WebSocket 地址（如 ws://host:9529/speech）。默认为空，首次连接时弹窗填写。</summary>
    public string? ServerUrl { get; set; }

    /// <summary>唤醒词触发阈值（0~1，默认 0.5，可调 0.1~0.9）</summary>
    public double WakeWordThreshold { get; set; } = 0.5;

    /// <summary>唤醒词检测器启动/重置后的冷却时间（秒），期间不触发（覆盖初始化窗口期误报）</summary>
    public double WakeWordCooldown { get; set; } = 2.5;

    /// <summary>是否已完成首次配置</summary>
    public bool IsConfigured { get; set; } = false;
}
