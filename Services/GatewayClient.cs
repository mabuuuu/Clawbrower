using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Clawbrower.Models;

namespace Clawbrower.Services;

public class GatewayClient : IDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly string _url;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private int _reqCounter;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>> _pending = new();
    private string? _activeStreamType;
    private bool _streamEnded;

    public bool IsConnected => _ws?.State == WebSocketState.Open && _handshakeDone && _handshakeError == null;
    private volatile bool _handshakeDone;
    private string? _handshakeError;
    private volatile bool _pairingPending;

    public event Action? OnConnected;
    public event Action<string>? OnDisconnected;
    public event Action<string, string>? OnDeltaText;
    public event Action<string, string>? OnToolEvent;
    public event Action<string, string, string, string, string>? OnToolResult;
    public event Action<string>? OnStreamComplete;
    public event Action<string>? OnError;
    public event Action? OnStreamReset;
    public event Action<string>? OnPairingPending;

    public GatewayClient(string url)
    {
        _url = url;
        Logger.Info($"GatewayClient created, url={url}");
    }

    public async Task ConnectAsync()
    {
        _cts = new CancellationTokenSource();
        _ws = new ClientWebSocket();
        _ws.Options.RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true;
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        _handshakeDone = false;
        _handshakeError = null;
        _pairingPending = false;
        _activeStreamType = null;

        Logger.Info($"Connecting to {_url}...");
        try
        {
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, connectCts.Token);
            await _ws.ConnectAsync(new Uri(_url), linked.Token);
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
        {
            Logger.Error("WebSocket connect timed out (10s)");
            throw new TimeoutException("连接超时 (10秒)");
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException != null ? $"{ex.Message} | {ex.InnerException.Message}" : ex.Message;
            Logger.Error($"WebSocket connect failed: {detail}");
            throw;
        }

        Logger.Info("WebSocket connected, waiting for server challenge...");
        _ = ReceiveLoopAsync(_ws, _cts.Token);

        // 等待握手完成
        var timeout = Task.Delay(30_000); // pair 等待时间更长（需要管理员批准）
        var waited = 0;
        while (!_handshakeDone && !timeout.IsCompleted)
        {
            await Task.Delay(1000);
            waited++;
            if (_pairingPending && waited % 5 == 0)
                Logger.Info($"Waiting for admin approval... ({waited}s)");
            else if (!_pairingPending && waited % 3 == 0)
                Logger.Info($"Waiting for handshake... ({waited}s)");
        }

        if (_handshakeError != null)
        {
            Logger.Error($"Handshake failed: {_handshakeError}");
            throw new Exception($"认证失败: {_handshakeError}");
        }

        if (_pairingPending)
        {
            Logger.Info("Pairing pending — waiting for admin approval");
            OnPairingPending?.Invoke("等待配对，请检查服务端日志获取 requestId");
            return; // 不抛异常，等待手动重连
        }

        if (!_handshakeDone)
        {
            Logger.Error($"Handshake timeout after {waited}s");
            throw new TimeoutException($"握手超时 ({waited}秒)");
        }
    }

    private async Task SendConnectRpc(string nonce)
    {
        var deviceId = ConfigService.GetDeviceId();
        var deviceToken = ConfigService.GetDeviceToken();
        var password = ConfigService.GetPassword();
        var publicKey = ConfigService.GetPublicKeyBase64Url();

        var clientId = "gateway-client";
        var clientMode = "ui";
        var role = "operator";
        var scopesStr = "operator.admin,operator.read,operator.write";  // 逗号分隔
        var signedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // token 字段：密码认证时为 空字符串
        var token = deviceToken ?? "";

        // 签名: v2|deviceId|clientId|clientMode|role|scopes|signedAt|nonce|token
        var signature = ConfigService.SignAuthPayload(deviceId, clientId, clientMode, role, scopesStr, signedAt, nonce, token);

        var deviceObj = new Dictionary<string, object>
        {
            ["id"] = deviceId,
            ["nonce"] = nonce,
            ["publicKey"] = publicKey,
            ["signedAt"] = signedAt,
            ["signature"] = signature
        };

        // 构建 auth 对象
        var authObj = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(deviceToken))
        {
            authObj["deviceToken"] = deviceToken;
        }
        else if (!string.IsNullOrWhiteSpace(password))
        {
            authObj["password"] = password;
        }

        var id = NextId();
        var connectParams = new Dictionary<string, object>
        {
            ["minProtocol"] = 4,
            ["maxProtocol"] = 4,
            ["client"] = new Dictionary<string, string>
            {
                ["id"] = clientId,
                ["version"] = "1.0.0",
                ["platform"] = "windows",
                ["mode"] = clientMode
            },
            ["role"] = role,
            ["scopes"] = new[] { "operator.admin", "operator.read", "operator.write" },
            ["device"] = deviceObj,
            ["auth"] = authObj,
            ["caps"] = new[] { "tool-events" }
        };

        var frame = new Dictionary<string, object> { ["type"] = "req", ["id"] = id, ["method"] = "connect", ["params"] = connectParams };
        var tcs = new TaskCompletionSource<JsonElement?>();
        _pending[id] = tcs;
        await SendJsonAsync(frame);
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
        _streamEnded = false;
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

    private string NextId() => (++_reqCounter).ToString();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private async Task SendJsonAsync(object obj)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(obj, _jsonOptions);
        Logger.Info($"TX: {json[..Math.Min(json.Length, 500)]}");
        await _ws.SendAsync(new ArraySegment<byte>(Utf8NoBom.GetBytes(json)), WebSocketMessageType.Text, true, _cts!.Token);
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
                    Logger.Info($"RX: {json[..Math.Min(json.Length, 500)]}");
                    try { ProcessFrame(json); } catch (Exception ex) { Logger.Error($"ProcessFrame: {ex.Message}"); }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { _handshakeDone = false; OnDisconnected?.Invoke(ex.Message); }
    }

    private void ProcessFrame(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        // 读取 type 字段
        var frameType = "";
        if (root.TryGetProperty("type", out var t))
            frameType = t.GetString() ?? "";

        // ── 握手阶段处理 ──
        if (!_handshakeDone)
        {
            // connect.challenge → 提取 nonce → 发送 connect RPC
            if (frameType == "event" && root.TryGetProperty("event", out var evt) && evt.GetString() == "connect.challenge")
            {
                var nonce = "";
                if (root.TryGetProperty("payload", out var pl) && pl.TryGetProperty("nonce", out var n))
                    nonce = n.GetString() ?? "";
                Logger.Info($"Received connect.challenge (nonce={nonce[..Math.Min(nonce.Length, 8)]}...), sending connect RPC");
                _ = SendConnectRpc(nonce);
                return;
            }

            // 握手阶段的 res 响应 — 检查是否是 hello-ok 或 PAIRING_REQUIRED
            if (frameType == "res")
            {
                // 检查 payload 中的 type 是否是 hello-ok
                if (root.TryGetProperty("payload", out var connectPayload))
                {
                    if (connectPayload.TryGetProperty("type", out var pt) && pt.GetString() == "hello-ok")
                    {
                        Logger.Info("hello-ok received, handshake complete!");
                        if (connectPayload.TryGetProperty("deviceToken", out var dt))
                        {
                            var token = dt.GetString();
                            if (!string.IsNullOrWhiteSpace(token))
                            {
                                var cfg = ConfigService.Load();
                                cfg.DeviceToken = token;
                                ConfigService.Save();
                                Logger.Info("DeviceToken saved");
                            }
                        }
                        _handshakeDone = true;
                        OnConnected?.Invoke();
                        return;
                    }
                }

                // 检查是否有 PAIRING_REQUIRED 错误（可能在 error.code 或 error.details.code）
                if (root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.False)
                {
                    var errCode = "";
                    var detailsCode = "";
                    var requestId = "";
                    if (root.TryGetProperty("error", out var err))
                    {
                        if (err.TryGetProperty("code", out var ec)) errCode = ec.GetString() ?? "";
                        if (err.TryGetProperty("details", out var details))
                        {
                            if (details.TryGetProperty("code", out var dc)) detailsCode = dc.GetString() ?? "";
                            if (details.TryGetProperty("requestId", out var rid))
                                requestId = rid.GetString() ?? "";
                        }
                    }

                    if (errCode == "PAIRING_REQUIRED" || detailsCode == "PAIRING_REQUIRED")
                    {
                        Logger.Info($"PAIRING_REQUIRED — needs admin approval (requestId={requestId})");
                        _pairingPending = true;
                        _handshakeDone = true;
                        OnPairingPending?.Invoke($"等待配对，服务端执行命令：openclaw devices approve {requestId}");
                        return;
                    }

                    // 其他错误
                    _handshakeError = errCode.Length > 0 ? errCode : "connect failed";
                    _handshakeDone = true;
                    return;
                }
            }
        }

        // ── 通用 RPC 响应处理（已握手后）──
        if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
        {
            var id = idEl.GetString() ?? "";
            if (_pending.TryRemove(id, out var tcs))
            {
                if (root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True)
                    tcs.TrySetResult(root.TryGetProperty("payload", out var pl) ? pl : null);
                else
                {
                    var errMsg = "RPC error";
                    if (root.TryGetProperty("error", out var rpcErr))
                        errMsg = rpcErr.ToString();
                    tcs.TrySetException(new Exception(errMsg));
                }
            }
            return;
        }

        // ── 事件处理 ──
        if (root.TryGetProperty("event", out var evtEl) && root.TryGetProperty("payload", out var payload))
        {
            var eventName = evtEl.GetString() ?? "";

            // chat event
            if (eventName == "chat")
            {
                if (_activeStreamType == "agent") return;
                var sessionKey = payload.TryGetProperty("sessionKey", out var sk) ? sk.GetString() ?? "" : "";

                if (payload.TryGetProperty("deltaText", out var dt) && dt.GetString() is string txt && txt.Length > 0)
                {
                    if (!_streamEnded)
                    {
                        if (_activeStreamType != "chat")
                            Logger.Info("Stream: chat (new stream)");
                        _activeStreamType = "chat";
                        OnDeltaText?.Invoke(sessionKey, txt);
                    }
                }
                if (payload.TryGetProperty("state", out var st) && st.GetString() == "final")
                {
                    Logger.Info("Stream: chat final — clearing");
                    _activeStreamType = null;
                    _streamEnded = false;
                    OnStreamComplete?.Invoke(sessionKey);
                }
                return;
            }

            // agent event
            if (eventName == "agent")
            {
                var stream = payload.TryGetProperty("stream", out var s) ? s.GetString() : "";
                var agSessionKey = payload.TryGetProperty("sessionKey", out var agSk) ? agSk.GetString() ?? "" : "";

                if (stream == "assistant" && payload.TryGetProperty("data", out var data))
                {
                    if (data.TryGetProperty("delta", out var d) && d.GetString() is string dtx && dtx.Length > 0)
                    {
                        if (_activeStreamType == "chat")
                            OnStreamReset?.Invoke();
                        _activeStreamType = "agent";
                        OnDeltaText?.Invoke(agSessionKey, dtx);
                    }
                }
                if (stream == "lifecycle" && payload.TryGetProperty("data", out var ld))
                {
                    if (ld.TryGetProperty("phase", out var ph) && ph.GetString() == "end")
                    {
                        Logger.Info("Stream: agent lifecycle end — clearing");
                        _activeStreamType = null;
                        _streamEnded = true;
                        OnStreamComplete?.Invoke(agSessionKey);
                    }
                }
                if (stream == "item" && payload.TryGetProperty("data", out var itemData))
                {
                    var kind = itemData.TryGetProperty("kind", out var kEl) ? kEl.GetString() ?? "" : "";
                    var phase = itemData.TryGetProperty("phase", out var pEl) ? pEl.GetString() ?? "" : "";
                    var toolCallId = itemData.TryGetProperty("toolCallId", out var tciEl) ? tciEl.GetString() ?? "" : "";
                    var name = itemData.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";

                    if (kind == "command" && phase == "end" && !string.IsNullOrEmpty(toolCallId))
                    {
                        var summary = itemData.TryGetProperty("summary", out var sumEl) ? sumEl.GetString() ?? "" : "";
                        var meta = itemData.TryGetProperty("meta", out var metaEl) ? metaEl.GetString() ?? "" : "";
                        OnToolResult?.Invoke(agSessionKey, toolCallId, name, meta, summary);
                    }
                    else if (kind == "tool" && phase == "end" && !string.IsNullOrEmpty(toolCallId))
                    {
                        var meta = itemData.TryGetProperty("meta", out var metaEl) ? metaEl.GetString() ?? "" : "";
                        OnToolResult?.Invoke(agSessionKey, toolCallId, name, meta, "");
                    }
                }
                return;
            }

            // session.tool
            if (eventName == "session.tool")
            {
                var tn = payload.TryGetProperty("toolName", out var tnEl) ? tnEl.GetString() ?? "" : "";
                var ts = payload.TryGetProperty("status", out var tsEl) ? tsEl.GetString() ?? "" : "";
                OnToolEvent?.Invoke(tn, ts);
            }
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
