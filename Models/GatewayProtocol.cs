using System.Text.Json.Serialization;

namespace Clawbrower.Models;

// ── Wire-level frames (per guide §2.1) ──

public class GatewayFrame
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("params")]
    public System.Text.Json.JsonElement? Params { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("ok")]
    public bool? Ok { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("payload")]
    public System.Text.Json.JsonElement? Payload { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("error")]
    public object? Error { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("event")]
    public string? EventName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("seq")]
    public int? Seq { get; set; }
}

// ── Chat message model ──

public class ChatMessage : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public ChatRole Role { get; set; }

    private string _content = "";
    public string Content
    {
        get => _content;
        set
        {
            // Sanitize BEFORE storing — orphaned UTF-16 surrogates crash WPF FlowDocument with FailFast.
            // This is the single chokepoint where ALL message text enters the UI binding pipeline.
            _content = MessageFilter.SanitizeSurrogates(value);
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Content)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSystemCollapsible)));
        }
    }

    public DateTime Timestamp { get; set; } = DateTime.Now;

    private bool _isStreaming;
    public bool IsStreaming
    {
        get => _isStreaming;
        set { _isStreaming = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsStreaming))); }
    }

    public string ToolCallId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string ToolInput { get; set; } = "";

    public string CollapseTitle => string.IsNullOrEmpty(ToolName)
        ? "系统消息"
        : string.IsNullOrEmpty(ToolInput)
            ? $"tool {ToolName}"
            : $"tool {ToolName}: {ToolInput}";

    public bool IsSystemCollapsible =>
        Role == ChatRole.System && (
            !string.IsNullOrEmpty(ToolName) ||
            Content.Split('\n', StringSplitOptions.None).Length > 10
        );

    private bool _isSystemExpanded;
    public bool IsSystemExpanded
    {
        get => _isSystemExpanded;
        set { _isSystemExpanded = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSystemExpanded))); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public enum ChatRole { User, Assistant, System }

public static class MessageFilter
{
    /// <summary>
    /// 已知的无用消息内容模式（服务端心跳/确认/空操作等）。
    /// 匹配时忽略大小写和首尾空白。
    /// </summary>
    private static readonly string[] NoisePatterns =
    {
        "HEARTBEAT_OK",
        "HEARTBEAT_ACK",
        "ARTBEAT_OK",      // HEARTBEAT_OK 被截断首字母的变体
        "PING",
        "PONG",
        "ACK_OK",
        "NOOP",
        "KEEPALIVE",
        "KEEPALIVE_OK",
    };

    /// <summary>
    /// 判断一条消息是否为无用噪声消息（心跳、确认等），应从 UI 中过滤掉。
    /// 注意：心跳消息的 role 可能是 assistant（服务端将心跳流存储为 assistant 消息），
    /// 因此不能仅按 role=system 过滤，必须检查内容。
    /// </summary>
    public static bool IsNoiseMessage(ChatMessage msg)
    {
        var content = msg.Content?.Trim();
        if (string.IsNullOrEmpty(content)) return false;

        // 精确匹配已知噪声模式
        foreach (var pattern in NoisePatterns)
        {
            if (content.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // 匹配 HEARTBEAT* / ARTBEAT* / PING* / PONG* / ACK* / NOOP* / KEEPALIVE* 前缀模式
        if (content.StartsWith("HEARTBEAT", StringComparison.OrdinalIgnoreCase) ||
            content.StartsWith("ARTBEAT", StringComparison.OrdinalIgnoreCase) ||
            content.StartsWith("PING", StringComparison.OrdinalIgnoreCase) ||
            content.StartsWith("PONG", StringComparison.OrdinalIgnoreCase) ||
            content.StartsWith("ACK_", StringComparison.OrdinalIgnoreCase) ||
            content.StartsWith("NOOP", StringComparison.OrdinalIgnoreCase) ||
            content.StartsWith("KEEPALIVE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 判断实时推送的文本内容是否为无用噪声（如心跳确认文本）。
    /// </summary>
    public static bool IsNoiseText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var trimmed = text.Trim();

        foreach (var pattern in NoisePatterns)
        {
            if (trimmed.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (trimmed.StartsWith("HEARTBEAT", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ARTBEAT", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("PING", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("PONG", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ACK_", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("NOOP", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("KEEPALIVE", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Remove orphaned UTF-16 surrogate characters that would crash WPF FlowDocument.
    /// WPF's TextParaClient.CreateLineVisual calls FailFast on broken surrogate pairs,
    /// which terminates the process immediately — no try/catch can intercept it.
    /// </summary>
    public static string SanitizeSurrogates(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Fast path: scan for any surrogate characters first
        bool hasSurrogate = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsSurrogate(text[i])) { hasSurrogate = true; break; }
        }
        if (!hasSurrogate) return text;

        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    sb.Append(c);
                    sb.Append(text[i + 1]);
                    i++;
                }
                // else: orphaned high surrogate — skip
            }
            else if (char.IsLowSurrogate(c))
            {
                // orphaned low surrogate — skip
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}

public class SessionInfo
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string AgentId { get; set; } = "main";

    public override string ToString() => Label;

    public override bool Equals(object? obj) => obj is SessionInfo s && s.Key == Key;
    public override int GetHashCode() => Key.GetHashCode();
}
