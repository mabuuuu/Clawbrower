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
        set { _content = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Content))); }
    }

    public DateTime Timestamp { get; set; } = DateTime.Now;

    private bool _isStreaming;
    public bool IsStreaming
    {
        get => _isStreaming;
        set { _isStreaming = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsStreaming))); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public enum ChatRole { User, Assistant, System }

public class SessionInfo
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string AgentId { get; set; } = "main";

    public override string ToString() => Label;

    public override bool Equals(object? obj) => obj is SessionInfo s && s.Key == Key;
    public override int GetHashCode() => Key.GetHashCode();
}
