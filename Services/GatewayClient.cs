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
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _pending = new();
    private readonly object _pendingGate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private bool _acceptingRequests;
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
            lock (_pendingGate) _acceptingRequests = true;
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
            await Task.Delay(1000, _cts.Token);
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
        try
        {
            await SendJsonAsync(frame);
        }
        catch (Exception ex)
        {
            if (_cts?.IsCancellationRequested == true) return;
            _handshakeError = ex.Message;
            _handshakeDone = true;
            Logger.Error($"Connect RPC send failed: {ex.Message}");
        }
    }

    public async Task SendAbortAsync(string sessionKey)
    {
        await SendRpcAsync("chat.abort", new Dictionary<string, object> { ["sessionKey"] = sessionKey });
    }

    public async Task<string?> SendRpcAsync(string method, Dictionary<string, object>? ps = null)
    {
        var id = NextId();
        var frame = new Dictionary<string, object> { ["type"] = "req", ["id"] = id, ["method"] = method };
        if (ps != null) frame["params"] = ps;
        var tcs = RegisterPendingRequest(id);
        try
        {
            await SendJsonAsync(frame);
            return await tcs.Task;
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
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
        var tcs = RegisterPendingRequest(id);
        try
        {
            await SendJsonAsync(frame);
            await tcs.Task;
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        return id;
    }

    private TaskCompletionSource<string?> RegisterPendingRequest(string id)
    {
        lock (_pendingGate)
        {
            if (!_acceptingRequests || _ws?.State != WebSocketState.Open)
                throw new InvalidOperationException("WebSocket 未连接，无法发送请求");

            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, tcs))
                throw new InvalidOperationException($"重复的请求 ID: {id}");
            return tcs;
        }
    }

    private string NextId() => Interlocked.Increment(ref _reqCounter).ToString();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private async Task SendJsonAsync(object obj)
    {
        var ws = _ws;
        var cts = _cts;
        if (ws?.State != WebSocketState.Open || cts == null)
            throw new InvalidOperationException("WebSocket 未连接，无法发送消息");

        var json = JsonSerializer.Serialize(obj, _jsonOptions);
        await _sendGate.WaitAsync(cts.Token);
        try
        {
            if (!ReferenceEquals(ws, _ws) || ws.State != WebSocketState.Open)
                throw new InvalidOperationException("WebSocket 连接已变更，无法发送消息");

            Logger.Info($"TX: {json[..Math.Min(json.Length, 500)]}");
            await ws.SendAsync(new ArraySegment<byte>(Utf8NoBom.GetBytes(json)), WebSocketMessageType.Text, true, cts.Token);
        }
        finally
        {
            _sendGate.Release();
        }
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

            if (!ct.IsCancellationRequested)
                HandleReceiveFailure(new WebSocketException($"WebSocket receive loop ended with state {ws.State}"));
        }
        catch (OperationCanceledException) { CancelPendingRequests(ct); }
        catch (Exception) when (ct.IsCancellationRequested) { CancelPendingRequests(ct); }
        catch (Exception ex) { HandleReceiveFailure(ex); }
    }

    private void HandleReceiveFailure(Exception error)
    {
        _handshakeDone = false;
        FailPendingRequests(error);
        try
        {
            Logger.Error($"Receive loop failed: {error.Message}");
        }
        finally
        {
            OnDisconnected?.Invoke(error.Message);
        }
    }

    private void FailPendingRequests(Exception error)
    {
        lock (_pendingGate)
        {
            _acceptingRequests = false;
            foreach (var entry in _pending)
                if (_pending.TryRemove(entry.Key, out var tcs))
                    tcs.TrySetException(error);
        }
    }

    private void CancelPendingRequests(CancellationToken cancellationToken)
    {
        lock (_pendingGate)
        {
            _acceptingRequests = false;
            foreach (var entry in _pending)
                if (_pending.TryRemove(entry.Key, out var tcs))
                    tcs.TrySetCanceled(cancellationToken);
        }
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
                {
                    // 返回 payload 的 JSON 文本，避免 JsonElement 引用已 Dispose 的 JsonDocument
                    string? payloadJson = null;
                    if (root.TryGetProperty("payload", out var pl))
                        payloadJson = pl.GetRawText();
                    tcs.TrySetResult(payloadJson);
                }
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

            // 过滤心跳/确认等无用事件
            if (IsNoiseEvent(eventName, payload))
            {
                Logger.Info($"Filtered noise event: {eventName}");
                return;
            }

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
        CancelPendingRequests(_cts?.Token ?? new CancellationToken(canceled: true));
        if (_ws?.State == WebSocketState.Open)
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        _ws?.Dispose(); _ws = null; _handshakeDone = false;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        CancelPendingRequests(_cts?.Token ?? new CancellationToken(canceled: true));
        _cts?.Dispose();
        _ws?.Dispose();
    }

    /// <summary>
    /// 判断事件是否为心跳/确认等无用噪声事件，应静默丢弃。
    /// </summary>
    private static bool IsNoiseEvent(string eventName, JsonElement payload)
    {
        // 服务端明确标记的心跳事件（agent 事件中 isHeartbeat=true）
        if (payload.TryGetProperty("isHeartbeat", out var ihb)
            && ihb.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        // 心跳事件类型
        if (eventName.Equals("heartbeat", StringComparison.OrdinalIgnoreCase) ||
            eventName.Equals("ping", StringComparison.OrdinalIgnoreCase) ||
            eventName.Equals("pong", StringComparison.OrdinalIgnoreCase) ||
            eventName.Equals("noop", StringComparison.OrdinalIgnoreCase) ||
            eventName.Equals("keepalive", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 检查 payload 中的 deltaText 是否为噪声文本
        if (payload.TryGetProperty("deltaText", out var dt) && dt.GetString() is string txt && txt.Length > 0)
        {
            if (MessageFilter.IsNoiseText(txt))
                return true;
        }

        // 检查 payload 中的 content 是否为噪声文本
        if (payload.TryGetProperty("content", out var ct) && ct.ValueKind == JsonValueKind.String)
        {
            var content = ct.GetString() ?? "";
            if (MessageFilter.IsNoiseText(content))
                return true;
        }

        return false;
    }
}
