using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Clawbrower.Models;
using Clawbrower.Services;
using WpfApp = System.Windows.Application;

namespace Clawbrower.ViewModels;

public enum AppState { WaitingForToken, Connecting, Connected, Disconnected }

public class MainViewModel : INotifyPropertyChanged
{
    private readonly string _gatewayUrl = "ws://127.0.0.1:18789";
    private string _sessionKey = "agent:main:main";
    private GatewayClient? _client;
    private AppState _state;
    private string _currentAiMessage = "";
    private string _lastDeltaText = "";
    private CancellationTokenSource? _streamTimeoutCts;

    // 历史记录加载相关状态
    private bool _isLoadingHistory;

    public ObservableCollection<ChatMessage> Messages { get; } = new();
    public ObservableCollection<SessionInfo> Sessions { get; } = new();
    public event Action? OnMessageUpdated;

    public bool IsLoadingHistory
    {
        get => _isLoadingHistory;
        private set { _isLoadingHistory = value; OnPropertyChanged(); }
    }

    private SessionInfo? _currentSession;
    public SessionInfo? CurrentSession
    {
        get => _currentSession;
        set
        {
            if (value == null || value.Key == _currentSession?.Key) return;
            _currentSession = value;
            _sessionKey = value.Key;
            OnPropertyChanged();
            Logger.Info($"Session switched to: {value.Key}");
            // 清空本地消息；历史由右键菜单主动加载
            Messages.Clear();
            AddSystemMessage($"已切换到会话: {_currentSession?.Label}");
        }
    }

    public AppState State
    {
        get => _state;
        set { _state = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(IsConnected)); OnPropertyChanged(nameof(IsStreaming)); }
    }

    private bool _isStreaming;
    public bool IsStreaming
    {
        get => _isStreaming;
        set { _isStreaming = value; OnPropertyChanged(); OnPropertyChanged(nameof(ThinkingVisible)); }
    }

    private string _thinkingText = "";
    public string ThinkingText
    {
        get => _thinkingText;
        set { _thinkingText = value; OnPropertyChanged(); }
    }

    public bool ThinkingVisible => IsStreaming;

    public bool IsConnected => State == AppState.Connected;
    public string StatusText => State switch
    {
        AppState.WaitingForToken => "等待输入 Token",
        AppState.Connecting => "连接中...",
        AppState.Connected => "已连接",
        AppState.Disconnected => "未连接",
        _ => ""
    };

    private string _inputText = "";
    public string InputText
    {
        get => _inputText;
        set { _inputText = value; OnPropertyChanged(); }
    }

    public MainViewModel()
    {
        Logger.Info("MainViewModel created");
        // Always have default session
        var def = new SessionInfo { Key = "agent:main:main", Label = "主会话", AgentId = "main" };
        Sessions.Add(def);
        _currentSession = def;
        OnPropertyChanged(nameof(CurrentSession));

        var savedToken = ConfigService.GetToken();
        if (string.IsNullOrWhiteSpace(savedToken))
        {
            State = AppState.WaitingForToken;
            AddSystemMessage("欢迎使用 Clawbrower！");
            AddSystemMessage("请直接粘贴 OpenClaw Gateway Token 并发送，Token 将安全保存在本地。");
            AddSystemMessage("获取 Token：运行 openclaw status 或在配置文件中查看 gateway.auth.token");
        }
        else
        {
            _ = ConnectAsync(savedToken);
        }
    }

    public async Task ConnectAsync(string token)
    {
        State = AppState.Connecting;
        AddSystemMessage("正在连接 OpenClaw Gateway...");
        Logger.Info("ConnectAsync with token");

        try
        {
            _client?.Dispose();
            _client = new GatewayClient(_gatewayUrl, token);
            _client.OnConnected += () => SafeInvoke(async () =>
            {
                State = AppState.Connected;
                AddSystemMessage("已连接到 OpenClaw Gateway");
                await LoadSessionsAsync();
            });
            _client.OnDisconnected += (reason) => SafeInvoke(() =>
            {
                State = AppState.Disconnected;
                StopStreaming();
                AddSystemMessage($"连接断开: {reason}");
                _ = Task.Run(async () => { await Task.Delay(3000); await SafeInvokeAsync(() => _ = ConnectAsync(token)); });
            });
            _client.OnDeltaText += (sessionKey, text) => SafeInvoke(async () =>
            {
                // 只处理当前选中 session 的事件
                if (sessionKey != _sessionKey) return;

                // Web 端触发（非本地发送）：自动加载用户消息并开始流式
                if (!IsStreaming)
                {
                    Logger.Info($"External stream detected for session {sessionKey}, loading recent history");
                    await LoadRecentHistoryAsync();
                    _currentAiMessage = "";
                    _lastDeltaText = "";
                    var aiMsg = new ChatMessage { Role = ChatRole.Assistant, Content = "", IsStreaming = true };
                    Messages.Add(aiMsg);
                    IsStreaming = true;
                    ThinkingText = "思考中...";
                    ResetStreamTimeout();
                    OnMessageUpdated?.Invoke();
                }

                if (!IsStreaming) return;

                // Skip consecutive identical substantial deltas (covers remaining duplication paths)
                if (text.Length > 3 && text == _lastDeltaText)
                {
                    Logger.Info($"Delta dedup: skipped duplicate '{text[..Math.Min(text.Length, 40)]}'");
                    return;
                }
                _lastDeltaText = text;

                ThinkingText = "";
                var preview = text.Length > 40 ? text[..40] + "..." : text;
                Logger.Info($"Delta[{text.Length}]: {preview.Replace("\n", "\\n")}");
                _currentAiMessage += text;
                var ai = FindLastAssistantMessage();
                if (ai != null) ai.Content = _currentAiMessage;
                OnMessageUpdated?.Invoke();
                ResetStreamTimeout();
            });
            _client.OnStreamReset += () => SafeInvoke(() =>
            {
                // Agent took priority over chat — clear any chat-sourced duplicate content
                Logger.Info("Stream reset — clearing accumulated text");
                _lastDeltaText = "";
                _currentAiMessage = "";
                var ai = FindLastAssistantMessage();
                if (ai != null) ai.Content = "";
                OnMessageUpdated?.Invoke();
            });
            _client.OnToolEvent += (name, status) => SafeInvoke(() =>
            {
                if (status == "started") ThinkingText = $"正在使用工具: {name}...";
                else if (status == "completed") { ThinkingText = "思考中..."; ResetStreamTimeout(); }
            });
            _client.OnToolResult += (sessionKey, toolCallId, toolName, toolInput, output) => SafeInvoke(() =>
            {
                if (sessionKey != _sessionKey) return;
                if (string.IsNullOrEmpty(toolInput) && string.IsNullOrEmpty(output)) return;

                var content = string.IsNullOrEmpty(output)
                    ? $"\n**TOOL INPUT:**\n{toolInput}"
                    : $"\n**TOOL INPUT:**\n{toolInput}\n\n---\n**TOOL OUTPUT:**\n{output}";

                // 同 toolCallId 去重：已存在则在新 output 更长时更新（command 的 summary 优于 tool 的 meta）
                for (int i = 0; i < Messages.Count; i++)
                {
                    if (Messages[i].Id == toolCallId)
                    {
                        if (output.Length > 0 && (string.IsNullOrEmpty(Messages[i].ToolInput) || output.Length > Messages[i].Content.Length))
                        {
                            Messages[i].ToolInput = toolInput;
                            Messages[i].Content = content;
                            OnMessageUpdated?.Invoke();
                        }
                        return;
                    }
                }

                var msg = new ChatMessage
                {
                    Id = toolCallId,
                    Role = ChatRole.System,
                    ToolName = toolName,
                    ToolInput = toolInput,
                    Content = content,
                    IsStreaming = false
                };

                // 插入到最后一条 Assistant 消息之前（工具结果应在助手回复之前）
                int insertAt = Messages.Count;
                for (int i = Messages.Count - 1; i >= 0; i--)
                {
                    if (Messages[i].Role == ChatRole.Assistant)
                    {
                        insertAt = i;
                        break;
                    }
                }
                if (insertAt == Messages.Count) Messages.Add(msg);
                else Messages.Insert(insertAt, msg);

                Logger.Info($"Tool result inserted: toolCallId={toolCallId}, name={toolName}, inputLen={toolInput.Length}, outputLen={output.Length}, at={insertAt}");
                OnMessageUpdated?.Invoke();
            });
            _client.OnStreamComplete += (sessionKey) => SafeInvoke(() =>
            {
                if (sessionKey != _sessionKey) return;
                StopStreaming();
            });
            _client.OnError += (msg) => SafeInvoke(() => AddSystemMessage($"错误: {msg}"));

            await _client.ConnectAsync();
        }
        catch (Exception ex)
        {
            Logger.Error($"Connect failed: {ex.Message}");
            State = AppState.Disconnected;
            AddSystemMessage($"连接失败: {ex.Message}");
            _ = Task.Run(async () => { await Task.Delay(5000); await SafeInvokeAsync(() => _ = ConnectAsync(token)); });
        }
    }

    private void StopStreaming()
    {
        _streamTimeoutCts?.Cancel();
        IsStreaming = false;
        ThinkingText = "";
        var ai = FindLastAssistantMessage();
        if (ai != null) ai.IsStreaming = false;
        OnMessageUpdated?.Invoke();
    }

    private void ResetStreamTimeout()
    {
        _streamTimeoutCts?.Cancel();
        _streamTimeoutCts = new CancellationTokenSource();
        var ct = _streamTimeoutCts.Token;
        _ = Task.Run(async () =>
        {
            await Task.Delay(120_000, ct);
            if (!ct.IsCancellationRequested)
                WpfApp.Current.Dispatcher.Invoke(() =>
                {
                    Logger.Info("Stream timeout — forcing stop");
                    StopStreaming();
                });
        }, ct);
    }

    public async Task SendMessageAsync()
    {
        var text = InputText?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        if (State == AppState.WaitingForToken)
        {
            InputText = "";
            AddSystemMessage("Token 已收到，正在保存...");
            ConfigService.SetToken(text);
            AddSystemMessage("Token 已保存，开始连接...");
            await ConnectAsync(text);
            return;
        }

        if (!IsConnected) { AddSystemMessage("未连接到 Gateway"); return; }
        if (IsStreaming) return;

        InputText = "";

        Messages.Add(new ChatMessage { Role = ChatRole.User, Content = text });
        var aiMsg = new ChatMessage { Role = ChatRole.Assistant, Content = "", IsStreaming = true };
        Messages.Add(aiMsg);
        _currentAiMessage = "";
        _lastDeltaText = "";
        IsStreaming = true;
        ThinkingText = "思考中...";
        ResetStreamTimeout();
        OnMessageUpdated?.Invoke();

        try
        {
            await _client!.SendChatAsync(_sessionKey, text);
        }
        catch (Exception ex)
        {
            Logger.Error($"Send failed: {ex.Message}");
            aiMsg.Content = $"发送失败: {ex.Message}";
            aiMsg.IsStreaming = false;
            StopStreaming();
            OnMessageUpdated?.Invoke();
        }
    }

    private ChatMessage? FindLastAssistantMessage()
    {
        for (int i = Messages.Count - 1; i >= 0; i--)
            if (Messages[i].Role == ChatRole.Assistant)
                return Messages[i];
        return null;
    }

    public void AddSystemMessage(string text)
    {
        Logger.Info($"System: {text}");
        Messages.Add(new ChatMessage { Role = ChatRole.System, Content = text });
    }

    public async Task StopAsync()
    {
        if (_client == null || !IsStreaming) return;
        try
        {
            await _client.SendAbortAsync(_sessionKey);
            StopStreaming();
            AddSystemMessage("已停止生成");
        }
        catch (Exception ex) { Logger.Error($"Stop failed: {ex.Message}"); }
    }

    public async Task LoadSessionsAsync()
    {
        if (_client == null) return;
        try
        {
            Logger.Info("Loading sessions...");
            // Try without params first (simpler API)
            var result = await _client.SendRpcAsync("sessions.list", new Dictionary<string, object>
            {
                ["limit"] = 20
            });
            Logger.Info($"sessions.list returned: {result != null}");
            if (result != null) Logger.Info($"sessions.list raw: {result.Value.GetRawText()}");

            if (result != null && result.Value.TryGetProperty("sessions", out var arr))
            {
                var existing = new HashSet<string>(Sessions.Select(s => s.Key));
                foreach (var s in arr.EnumerateArray())
                {
                    var key = s.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(key) || existing.Contains(key)) continue;

                    // 排除非聊天会话：cron、subagent
                    if (key.Contains(":cron") || key.Contains(":subagent:")) continue;

                    var label = s.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(label)) label = key[..Math.Min(key.Length, 20)];
                    Sessions.Add(new SessionInfo { Key = key, Label = label });
                    existing.Add(key);
                }
            }
        }
        catch (Exception ex) { Logger.Error($"LoadSessions failed: {ex.Message}"); }
    }

    public async Task CreateSessionAsync(string? label = null)
    {
        if (_client == null) return;
        try
        {
            var ps = new Dictionary<string, object> { ["agentId"] = "main" };
            if (!string.IsNullOrWhiteSpace(label))
                ps["label"] = label;

            var result = await _client.SendRpcAsync("sessions.create", ps);
            if (result != null)
            {
                var payload = result.Value;
                if (payload.TryGetProperty("key", out var key) && key.GetString() is string k && k.Length > 0)
                {
                    var sessionLabel = payload.TryGetProperty("label", out var l) && l.GetString() is string lb && lb.Length > 0 ? lb : (label ?? $"会话 {Sessions.Count}");
                    var si = new SessionInfo { Key = k, Label = sessionLabel };
                    Sessions.Add(si);
                    CurrentSession = si;
                    AddSystemMessage($"已创建新会话: {sessionLabel}");
                    return;
                }
                Logger.Error($"sessions.create unexpected payload: {payload.GetRawText()}");
                AddSystemMessage($"创建会话失败: 返回格式异常");
            }
            else
            {
                AddSystemMessage($"创建会话失败: 无响应");
            }
        }
        catch (Exception ex) { AddSystemMessage($"创建会话失败: {ex.Message}"); }
    }

    /// <summary>
    /// <summary>
    /// 从服务端加载当前会话最近30条历史消息（右键菜单触发）。
    /// </summary>
    public async Task LoadHistoryAsync()
    {
        if (_client == null || !IsConnected) return;
        if (IsLoadingHistory) return;

        IsLoadingHistory = true;
        try
        {
            var ps = new Dictionary<string, object>
            {
                ["sessionKey"] = _sessionKey,
                ["limit"] = 30
            };

            Logger.Info($"Loading history: method=chat.history, sessionKey={_sessionKey}, limit=30");
            var result = await _client.SendRpcAsync("chat.history", ps);

            if (result == null)
            {
                Logger.Info("chat.history returned null");
                return;
            }

            var payload = result.Value;
            // 兼容返回格式：{ messages: [...] } 或直接数组
            JsonElement msgsEl = default;
            bool found = false;
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("messages", out msgsEl))
            {
                found = true;
            }
            else if (payload.ValueKind == JsonValueKind.Array)
            {
                msgsEl = payload;
                found = true;
            }

            if (!found)
            {
                Logger.Info($"chat.history unexpected payload: {payload.GetRawText()}");
                return;
            }

            var historyMessages = new List<ChatMessage>();
            foreach (var m in msgsEl.EnumerateArray())
            {
                if (m.TryGetProperty("role", out var rEl) && rEl.GetString() == "tool_use")
                    continue;
                var msg = ParseHistoryMessage(m);
                if (string.IsNullOrEmpty(msg.Content) && msg.Role == ChatRole.Assistant)
                    continue;
                historyMessages.Add(msg);
            }

            if (historyMessages.Count == 0)
            {
                Logger.Info("History empty");
                return;
            }

            // 历史消息按时间正序排列（最早在前）
            historyMessages.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

            // tool_use → toolResult 关联，统一拼接 TOOL INPUT / TOOL OUTPUT 格式
            var inputMap = BuildToolUseInputMap(msgsEl);
            Logger.Info($"BuildToolUseInputMap: {inputMap.Count} entries");
            CorrelateToolMessages(historyMessages, inputMap);

            // 客户端去重
            if (Messages.Count > 0)
            {
                var existingKeys = new HashSet<string>(
                    Messages.Select(m => $"{m.Role}|{m.Content}|{m.Timestamp:O}"));
                var before = historyMessages.Count;
                historyMessages = historyMessages
                    .Where(m => !existingKeys.Contains($"{m.Role}|{m.Content}|{m.Timestamp:O}"))
                    .ToList();
                if (historyMessages.Count < before)
                    Logger.Info($"Dedup: removed {before - historyMessages.Count} duplicate messages");
            }

            if (historyMessages.Count == 0)
            {
                Logger.Info("All loaded messages are duplicates");
                return;
            }

            Messages.Clear();

            // 插入到消息列表开头
            for (int i = 0; i < historyMessages.Count; i++)
                Messages.Insert(i, historyMessages[i]);

            Logger.Info($"History loaded: {historyMessages.Count} messages");
            OnMessageUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.Error($"LoadHistory failed: {ex.Message}");
            AddSystemMessage($"加载历史消息失败: {ex.Message}");
        }
        finally
        {
            IsLoadingHistory = false;
        }
    }

    /// <summary>
    /// 从原始 JSON 中扫描所有 tool_use 块，建立 toolCallId → input 映射。
    /// 覆盖两种形式：顶层 role=tool_use 消息，以及 assistant 消息 content 中的 toolCall 块。
    /// </summary>
    private static Dictionary<string, string> BuildToolUseInputMap(JsonElement msgsEl)
    {
        var map = new Dictionary<string, string>();
        foreach (var m in msgsEl.EnumerateArray())
        {
            var role = "";
            if (m.TryGetProperty("role", out var rEl)
                && rEl.ValueKind == JsonValueKind.String)
                role = rEl.GetString() ?? "";

            // 形式 1：顶层 role=tool_use 消息
            if (role.Equals("tool_use", StringComparison.OrdinalIgnoreCase))
            {
                var tci = m.TryGetProperty("toolCallId", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "";
                var input = "";
                if (m.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.String)
                    input = meta.GetString() ?? "";
                else if (m.TryGetProperty("input", out var inp) && inp.ValueKind == JsonValueKind.String)
                    input = inp.GetString() ?? "";
                else if (m.TryGetProperty("args", out var arg) && arg.ValueKind == JsonValueKind.String)
                    input = arg.GetString() ?? "";
                if (!string.IsNullOrEmpty(tci) && !string.IsNullOrEmpty(input))
                    map[tci] = input;
            }

            // 形式 2：assistant 消息 content 数组中嵌入的 toolCall 块
            if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
                && m.TryGetProperty("content", out var c)
                && c.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in c.EnumerateArray())
                {
                    if (!block.TryGetProperty("type", out var bt)
                        || bt.GetString() != "toolCall")
                        continue;

                    var tci = block.TryGetProperty("id", out var idEl)
                        && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? "" : "";
                    // exec 类工具：取 arguments.command 作为简洁命令；其他工具取 arguments 的 JSON
                    var input = "";
                    if (block.TryGetProperty("arguments", out var args)
                        && args.TryGetProperty("command", out var cmd)
                        && cmd.ValueKind == JsonValueKind.String)
                        input = cmd.GetString() ?? "";
                    if (string.IsNullOrEmpty(input)
                        && block.TryGetProperty("arguments", out var args2))
                        input = args2.GetRawText();
                    if (string.IsNullOrEmpty(input)
                        && block.TryGetProperty("partialArgs", out var pa)
                        && pa.ValueKind == JsonValueKind.String)
                        input = pa.GetString() ?? "";
                    if (!string.IsNullOrEmpty(tci) && !string.IsNullOrEmpty(input))
                        map[tci] = input;
                }
            }
        }
        return map;
    }

    /// <summary>
    /// 关联 tool_use → toolResult：将外部构建的 inputMap 中同 toolCallId 的输入赋值给 toolResult，
    /// 并统一拼接 TOOL INPUT / TOOL OUTPUT 格式。
    /// </summary>
    private static void CorrelateToolMessages(List<ChatMessage> historyMessages, Dictionary<string, string> inputMap)
    {
        var updatedCount = 0;
        foreach (var msg in historyMessages)
        {
            if (msg.Role != ChatRole.System || string.IsNullOrEmpty(msg.ToolName))
                continue;

            if (!string.IsNullOrEmpty(msg.ToolCallId)
                && inputMap.TryGetValue(msg.ToolCallId, out var input))
            {
                msg.ToolInput = input;
                msg.Content = $"\n**TOOL INPUT:**\n{input}\n\n---\n**TOOL OUTPUT:**\n{msg.Content}";
                updatedCount++;
            }
            else if (!string.IsNullOrEmpty(msg.Content))
            {
                msg.Content = $"\n\n**TOOL OUTPUT:**\n{msg.Content}";
            }
        }

        Logger.Info($"CorrelateToolMessages: {inputMap.Count} tool_use inputs → {updatedCount} toolResults updated");
    }

    /// <summary>
    /// 从 chat.history 返回的单个 JsonElement 解析为 ChatMessage。
    /// </summary>
    private static ChatMessage ParseHistoryMessage(JsonElement m)
    {
        var role = "assistant";
        if (m.TryGetProperty("role", out var r))
        {
            if (r.ValueKind == JsonValueKind.String)
                role = r.GetString() ?? "assistant";
            else if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0
                && r[0].ValueKind == JsonValueKind.String)
                role = r[0].GetString() ?? "assistant";
        }

        var content = "";
        if (m.TryGetProperty("content", out var c))
        {
            if (c.ValueKind == JsonValueKind.String)
            {
                content = c.GetString() ?? "";
            }
            else if (c.ValueKind == JsonValueKind.Array)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var block in c.EnumerateArray())
                {
                    if (block.TryGetProperty("text", out var bt)
                        && bt.ValueKind == JsonValueKind.String)
                        sb.AppendLine(bt.GetString());
                    else if (block.TryGetProperty("content", out var bc)
                        && bc.ValueKind == JsonValueKind.String)
                        sb.AppendLine(bc.GetString());
                }
                content = sb.ToString().TrimEnd('\r', '\n');
            }
        }
        if (string.IsNullOrEmpty(content)
            && m.TryGetProperty("text", out var txt)
            && txt.ValueKind == JsonValueKind.String)
            content = txt.GetString() ?? "";

        var id = "";
        if (m.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            id = idEl.GetString() ?? "";

        var chatRole = role.ToLowerInvariant() switch
        {
            "user" => ChatRole.User,
            "system" => ChatRole.System,
            "toolresult" => ChatRole.System,
            "tool_use" => ChatRole.Assistant,
            _ => ChatRole.Assistant
        };

        // 提取 toolCallId（所有消息类型通用）
        var toolCallId = "";
        if (m.TryGetProperty("toolCallId", out var tciEl)
            && tciEl.ValueKind == JsonValueKind.String)
            toolCallId = tciEl.GetString() ?? "";

        var toolName = "";
        var toolInput = "";

        // tool_use：提取命令
        if (role.Equals("tool_use", StringComparison.OrdinalIgnoreCase))
        {
            if (m.TryGetProperty("meta", out var metaEl) && metaEl.ValueKind == JsonValueKind.String)
                toolInput = metaEl.GetString() ?? "";
            else if (m.TryGetProperty("input", out var inEl) && inEl.ValueKind == JsonValueKind.String)
                toolInput = inEl.GetString() ?? "";
            else if (m.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.String)
                toolInput = argsEl.GetString() ?? "";
        }

        // toolResult：提取工具名（内容格式由后续关联步骤统一拼接）
        if (role.Equals("toolResult", StringComparison.OrdinalIgnoreCase)
            && m.TryGetProperty("toolName", out var tnEl)
            && tnEl.ValueKind == JsonValueKind.String)
        {
            toolName = tnEl.GetString() ?? "";
        }

        var ts = DateTime.Now;
        if (m.TryGetProperty("timestamp", out var tsEl))
        {
            if (tsEl.ValueKind == JsonValueKind.Number)
            {
                var ms = tsEl.TryGetInt64(out var v) ? v : (long)tsEl.GetDouble();
                try { ts = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime; } catch { }
            }
            else if (tsEl.ValueKind == JsonValueKind.String
                && DateTime.TryParse(tsEl.GetString(), out var parsed))
            {
                ts = parsed;
            }
        }

        return new ChatMessage
        {
            Id = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N")[..8] : id,
            Role = chatRole,
            ToolCallId = toolCallId,
            ToolName = toolName,
            ToolInput = toolInput,
            Content = content,
            Timestamp = ts,
            IsStreaming = false
        };
    }

    /// <summary>
    /// 加载当前会话最近历史消息（由 Web 端外部事件触发，轻量版）。
    /// </summary>
    private async Task LoadRecentHistoryAsync()
    {
        if (_client == null || !IsConnected) return;
        try
        {
            var ps = new Dictionary<string, object>
            {
                ["sessionKey"] = _sessionKey,
                ["limit"] = 10
            };
            var result = await _client.SendRpcAsync("chat.history", ps);
            if (result == null) return;

            var payload = result.Value;
            JsonElement msgsEl = default;
            bool found = false;
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("messages", out msgsEl))
                found = true;
            else if (payload.ValueKind == JsonValueKind.Array)
            { msgsEl = payload; found = true; }
            if (!found) return;

            // 保留系统消息，替换用户/助手消息为最新历史
            var systemMessages = Messages.Where(m => m.Role == ChatRole.System).ToList();
            Messages.Clear();
            foreach (var msg in systemMessages) Messages.Add(msg);

            var historyMessages = new List<ChatMessage>();
            foreach (var m in msgsEl.EnumerateArray())
            {
                if (m.TryGetProperty("role", out var rEl) && rEl.GetString() == "tool_use")
                    continue;
                var msg = ParseHistoryMessage(m);
                if (string.IsNullOrEmpty(msg.Content) && msg.Role == ChatRole.Assistant)
                    continue;
                historyMessages.Add(msg);
            }

            historyMessages.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

            // tool_use → toolResult 关联，统一拼接 TOOL INPUT / TOOL OUTPUT 格式
            var inputMap = BuildToolUseInputMap(msgsEl);
            Logger.Info($"BuildToolUseInputMap: {inputMap.Count} entries");
            CorrelateToolMessages(historyMessages, inputMap);

            foreach (var hm in historyMessages)
                Messages.Add(hm);

            Logger.Info($"Recent history loaded: {historyMessages.Count} messages");
        }
        catch (Exception ex) { Logger.Error($"LoadRecentHistory failed: {ex.Message}"); }
    }

    /// <summary>
    /// 清除当前会话的本地聊天记录（仅清空内存中的Messages，不影响服务端数据）。
    /// </summary>
    public void ClearCurrentSessionMessages()
    {
        Logger.Info($"Clearing local messages for session: {_sessionKey}");
        Messages.Clear();
        AddSystemMessage("已清除本地聊天记录（服务端历史不受影响）");
        OnMessageUpdated?.Invoke();
    }

    // ── Safe dispatcher helpers ──

    private static void SafeInvoke(Action action)
    {
        try { WpfApp.Current.Dispatcher.Invoke(action); }
        catch (Exception ex) { Logger.Error($"SafeInvoke exception: {ex}"); }
    }

    private static void SafeInvoke(Func<Task> asyncAction)
    {
        try { WpfApp.Current.Dispatcher.Invoke(async () => { try { await asyncAction(); } catch (Exception ex) { Logger.Error($"SafeInvoke async exception: {ex}"); } }); }
        catch (Exception ex) { Logger.Error($"SafeInvoke exception: {ex}"); }
    }

    private static async Task SafeInvokeAsync(Func<Task> func)
    {
        try { await WpfApp.Current.Dispatcher.InvokeAsync(func); }
        catch (Exception ex) { Logger.Error($"SafeInvokeAsync exception: {ex}"); }
    }

    // ── INotifyPropertyChanged ──

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
