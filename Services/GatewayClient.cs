using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Clawbrower.Models;

namespace Clawbrower.Services;

public class GatewayClient : IDisposable
{
    private readonly string _url;
    private readonly string _token;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private int _reqCounter;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>> _pending = new();
    private string? _deviceToken;

    public bool IsConnected => _ws?.State == WebSocketState.Open && _handshakeDone;
    private volatile bool _handshakeDone;

    public event Action? OnConnected;
    public event Action<string>? OnDisconnected;
    public event Action<string>? OnDeltaText;
    public event Action<string, string>? OnToolEvent;
    public event Action? OnStreamComplete;
    public event Action<string>? OnError;

    public GatewayClient(string url, string token)
    {
        _url = url; _token = token;
        Logger.Info($"GatewayClient created, url={url}");
    }

    public async Task ConnectAsync()
    {
        _cts = new CancellationTokenSource();
        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        _handshakeDone = false;
        _deviceToken = ConfigService.Load().DeviceToken;

        Logger.Info($"Connecting to {_url}...");
        await _ws.ConnectAsync(new Uri(_url), _cts.Token);
        Logger.Info("WebSocket connected, waiting for challenge...");
        _ = ReceiveLoopAsync(_ws, _cts.Token);

        var timeout = Task.Delay(15_000);
        while (!_handshakeDone && !timeout.IsCompleted)
            await Task.Delay(100);

        if (!_handshakeDone)
        {
            Logger.Error("Handshake timeout");
            throw new TimeoutException("握手超时");
        }
    }

    public async Task SendAbortAsync(string sessionKey)
    {
        await SendRpcAsync("chat.abort", new Dictionary<string, object> { ["sessionKey"] = sessionKey });
    }

    public async Task<JsonElement?> SendRpcAsync(string method, Dictionary<string, object>? ps = null)
    {
        var id = NextId();
        var frame = new Dictionary<string, object> { ["type"] = "req", ["id"] = id, ["method"] = method };
        if (ps != null) frame["params"] = ps;
        var tcs = new TaskCompletionSource<JsonElement?>();
        _pending[id] = tcs;
        await SendJsonAsync(frame);
        return await tcs.Task;
    }

    public async Task<string> SendChatAsync(string sessionKey, string content)
    {
        var id = NextId();
        var frame = new Dictionary<string, object>
        {
            ["type"] = "req", ["id"] = id, ["method"] = "chat.send",
            ["params"] = new Dictionary<string, object>
            {
                ["sessionKey"] = sessionKey,
                ["idempotencyKey"] = Guid.NewGuid().ToString(),
                ["message"] = content
            }
        };
        var tcs = new TaskCompletionSource<JsonElement?>();
        _pending[id] = tcs;
        await SendJsonAsync(frame);
        await tcs.Task;
        return id;
    }

    private async Task SendConnectAsync()
    {
        var id = NextId();
        var connectParams = new Dictionary<string, object>
        {
            ["minProtocol"] = 4, ["maxProtocol"] = 4,
            ["client"] = new Dictionary<string, string> { ["id"] = "gateway-client", ["version"] = "1.0.0", ["platform"] = "windows", ["mode"] = "backend" },
            ["role"] = "operator",
            ["scopes"] = new[] { "operator.read", "operator.write" },
            ["caps"] = Array.Empty<object>(), ["commands"] = Array.Empty<object>(),
            ["permissions"] = new Dictionary<string, object>(),
            ["locale"] = "zh-CN", ["userAgent"] = "clawbrower/1.0"
        };
        connectParams["auth"] = new Dictionary<string, string> { ["token"] = _deviceToken ?? _token };

        var frame = new Dictionary<string, object> { ["type"] = "req", ["id"] = id, ["method"] = "connect", ["params"] = connectParams };
        var tcs = new TaskCompletionSource<JsonElement?>();
        _pending[id] = tcs;
        await SendJsonAsync(frame);
    }

    private string NextId() => (++_reqCounter).ToString();

    private async Task SendJsonAsync(object obj)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(obj);
        Logger.Debug($"TX: {json[..Math.Min(json.Length, 300)]}");
        await _ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, _cts!.Token);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[128 * 1024];
        var sb = new StringBuilder();
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (result.EndOfMessage)
                {
                    var json = sb.ToString(); sb.Clear();
                    if (string.IsNullOrEmpty(json)) continue;
                    Logger.Debug($"RX: {json[..Math.Min(json.Length, 500)]}");
                    try { ProcessFrame(JsonSerializer.Deserialize<GatewayFrame>(json)!); } catch (Exception ex) { Logger.Error($"ProcessFrame: {ex.Message}"); }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { _handshakeDone = false; OnDisconnected?.Invoke(ex.Message); }
    }

    private void ProcessFrame(GatewayFrame frame)
    {
        // Challenge
        if (frame.Type == "event" && frame.EventName == "connect.challenge") { _ = SendConnectAsync(); return; }

        // hello-ok
        if (frame.Type == "res" && frame.Ok == true && frame.Payload != null)
        {
            var p = frame.Payload.Value;
            if (p.TryGetProperty("type", out var t) && t.GetString() == "hello-ok")
            {
                _handshakeDone = true;
                if (p.TryGetProperty("auth", out var a) && a.TryGetProperty("deviceToken", out var dt))
                {
                    _deviceToken = dt.GetString();
                    ConfigService.Load().DeviceToken = _deviceToken; ConfigService.Save();
                }
                OnConnected?.Invoke();
                if (frame.Id != null && _pending.TryRemove(frame.Id, out var tcs)) tcs.TrySetResult(frame.Payload);
                return;
            }
        }

        // Generic RPC response
        if (frame.Type == "res" && frame.Id != null)
        {
            if (_pending.TryRemove(frame.Id, out var tcs))
            {
                if (frame.Ok == true) tcs.TrySetResult(frame.Payload);
                else tcs.TrySetException(new Exception($"RPC error: {frame.Error}"));
            }
            return;
        }

        // ── Events ──
        if (frame.Type != "event" || frame.Payload == null) return;
        var payload = frame.Payload.Value;

        // chat event — streaming + completion
        if (frame.EventName == "chat")
        {
            if (payload.TryGetProperty("deltaText", out var dt) && dt.GetString() is string txt && txt.Length > 0)
                OnDeltaText?.Invoke(txt);
            if (payload.TryGetProperty("state", out var st) && st.GetString() == "final")
                OnStreamComplete?.Invoke();
            return;
        }

        // agent event — text deltas + lifecycle
        if (frame.EventName == "agent")
        {
            var stream = payload.TryGetProperty("stream", out var s) ? s.GetString() : "";
            if (stream == "assistant" && payload.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("delta", out var d) && d.GetString() is string dtx && dtx.Length > 0)
                    OnDeltaText?.Invoke(dtx);
            }
            if (stream == "lifecycle" && payload.TryGetProperty("data", out var ld))
            {
                if (ld.TryGetProperty("phase", out var ph) && ph.GetString() == "end")
                    OnStreamComplete?.Invoke();
            }
            return;
        }

        // session.tool
        if (frame.EventName == "session.tool")
        {
            var tn = payload.TryGetProperty("toolName", out var tnEl) ? tnEl.GetString() ?? "" : "";
            var ts = payload.TryGetProperty("status", out var tsEl) ? tsEl.GetString() ?? "" : "";
            OnToolEvent?.Invoke(tn, ts);
        }
    }

    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        if (_ws?.State == WebSocketState.Open)
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        _ws?.Dispose(); _ws = null; _handshakeDone = false;
    }

    public void Dispose() { _cts?.Cancel(); _cts?.Dispose(); _ws?.Dispose(); }
}
