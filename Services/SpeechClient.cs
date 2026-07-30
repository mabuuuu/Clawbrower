using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Clawbrower.Services;

/// <summary>
/// 连接语音服务器 ws://host:9529/speech 的 WebSocket 客户端。
/// 负责：发送 PCM 音频分片、发送结束标记、接收 status/transcript/reply/mp3/audio_end。
/// </summary>
public class SpeechClient : IDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private bool _disposed;

    private const int ReceiveBufferSize = 16 * 1024;

    /// <summary>状态更新：stage = "asr" | "thinking" | "tts"</summary>
    public event Action<string>? OnStatus;

    /// <summary>ASR 识别结果（用户说的话）</summary>
    public event Action<string>? OnTranscript;

    /// <summary>助手回复文字</summary>
    public event Action<string>? OnReply;

    /// <summary>收到 mp3 语音数据（可能分多次）</summary>
    public event Action<byte[]>? OnMp3Data;

    /// <summary>语音数据发送完毕，mp3 已完整接收</summary>
    public event Action? OnAudioEnd;

    /// <summary>错误</summary>
    public event Action<string>? OnError;

    /// <summary>连接关闭</summary>
    public event Action? OnDisconnected;

    /// <summary>是否已连接</summary>
    public bool IsConnected => _ws?.State == WebSocketState.Open;

    /// <summary>连接语音服务器</summary>
    public async Task ConnectAsync(string url)
    {
        _cts = new CancellationTokenSource();
        _ws = new ClientWebSocket();
        _ws.Options.RemoteCertificateValidationCallback = (s, c, ch, e) => true;

        Logger.Info($"SpeechClient connecting to {url}");
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, connectCts.Token);
        await _ws.ConnectAsync(new Uri(url), linked.Token);

        Logger.Info("SpeechClient connected");
        _ = ReceiveLoopAsync(_ws, _cts.Token);
    }

    /// <summary>发送音频分片（二进制 PCM）</summary>
    public async Task SendAudioAsync(byte[] chunk)
    {
        var ws = _ws;
        var ct = _cts?.Token ?? default;
        if (ws?.State != WebSocketState.Open) return;

        await _sendGate.WaitAsync(ct);
        try
        {
            if (ws.State != WebSocketState.Open) return;
            await ws.SendAsync(new ArraySegment<byte>(chunk), WebSocketMessageType.Binary, true, ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>发送结束标记 {"type":"end"}</summary>
    public async Task SendEndAsync()
    {
        var ws = _ws;
        var ct = _cts?.Token ?? default;
        if (ws?.State != WebSocketState.Open) return;

        var json = """{"type":"end"}""";
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendGate.WaitAsync(ct);
        try
        {
            if (ws.State != WebSocketState.Open) return;
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            Logger.Info("SpeechClient sent end marker");
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>主动断开</summary>
    public void Disconnect()
    {
        try
        {
            _cts?.Cancel();
            var ws = _ws;
            if (ws?.State == WebSocketState.Open)
            {
                try
                {
                    ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client closing", CancellationToken.None).Wait(TimeSpan.FromSeconds(2));
                }
                catch { /* 忽略关闭错误 */ }
            }
        }
        catch { /* 忽略 */ }
        finally
        {
            _ws?.Dispose();
            _ws = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];
        var sb = new StringBuilder();

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Logger.Info("SpeechClient server closed connection");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // 二进制 = mp3 数据
                    var data = new byte[result.Count];
                    Array.Copy(buffer, data, result.Count);

                    // 如果一条消息分多帧，需要累积
                    if (!result.EndOfMessage)
                    {
                        using var ms = new MemoryStream();
                        ms.Write(data, 0, data.Length);
                        while (!result.EndOfMessage)
                        {
                            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                            ms.Write(buffer, 0, result.Count);
                        }
                        data = ms.ToArray();
                    }

                    OnMp3Data?.Invoke(data);
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (!result.EndOfMessage) continue;

                    var json = sb.ToString();
                    sb.Clear();
                    ProcessTextMessage(json);
                }
            }

            if (!ct.IsCancellationRequested)
                OnDisconnected?.Invoke();
        }
        catch (OperationCanceledException) { /* 正常取消 */ }
        catch (Exception ex)
        {
            Logger.Error($"SpeechClient receive loop error: {ex.Message}");
            if (!ct.IsCancellationRequested)
                OnError?.Invoke($"连接异常: {ex.Message}");
        }
    }

    private void ProcessTextMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeEl))
            {
                Logger.Info($"SpeechClient unknown message: {json[..Math.Min(json.Length, 200)]}");
                return;
            }

            var type = typeEl.GetString() ?? "";

            switch (type)
            {
                case "status":
                    var stage = root.TryGetProperty("stage", out var s) ? s.GetString() ?? "" : "";
                    Logger.Info($"SpeechClient status: {stage}");
                    OnStatus?.Invoke(stage);
                    break;

                case "transcript":
                    var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    Logger.Info($"SpeechClient transcript: {text[..Math.Min(text.Length, 60)]}");
                    OnTranscript?.Invoke(text);
                    break;

            case "reply":
                var replyRaw = root.TryGetProperty("text", out var r) ? r.GetString() ?? "" : "";
                var replyText = TryParseReplyPayload(replyRaw);
                Logger.Info($"SpeechClient reply: {replyText[..Math.Min(replyText.Length, 60)]}");
                OnReply?.Invoke(replyText);
                break;

                case "audio_end":
                    Logger.Info("SpeechClient audio_end received");
                    OnAudioEnd?.Invoke();
                    break;

                case "error":
                    var msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "未知错误" : "未知错误";
                    Logger.Error($"SpeechClient server error: {msg}");
                    OnError?.Invoke(msg);
                    break;

                default:
                    Logger.Info($"SpeechClient unhandled type={type}: {json[..Math.Min(json.Length, 200)]}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"SpeechClient process message failed: {ex.Message}, raw={json[..Math.Min(json.Length, 200)]}");
        }
    }

    /// <summary>尝试从嵌套 JSON（含 runId/status/result/payloads）中提取 result.payloads[0].text；格式不匹配则原样返回</summary>
    private static string TryParseReplyPayload(string text)
    {
        if (!text.StartsWith("{") || !text.Contains("\"payloads\"")) return text;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("result", out var result) &&
                result.TryGetProperty("payloads", out var payloads) &&
                payloads.GetArrayLength() > 0)
            {
                var firstPayload = payloads[0];
                if (firstPayload.TryGetProperty("text", out var textEl))
                    return textEl.GetString() ?? text;
            }
        }
        catch { /* 解析失败，回退到原始文本 */ }
        return text;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        _sendGate.Dispose();
        GC.SuppressFinalize(this);
    }

    ~SpeechClient() => Disconnect();
}
