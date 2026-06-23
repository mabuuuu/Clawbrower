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
    private CancellationTokenSource? _streamTimeoutCts;

    public ObservableCollection<ChatMessage> Messages { get; } = new();
    public ObservableCollection<SessionInfo> Sessions { get; } = new();
    public event Action? OnMessageUpdated;

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
            _client.OnDeltaText += (text) => SafeInvoke(() =>
            {
                ThinkingText = "";
                _currentAiMessage += text;
                var ai = FindLastAssistantMessage();
                if (ai != null) ai.Content = _currentAiMessage;
                OnMessageUpdated?.Invoke();
                ResetStreamTimeout();
            });
            _client.OnToolEvent += (name, status) => SafeInvoke(() =>
            {
                if (status == "started") ThinkingText = $"正在使用工具: {name}...";
                else if (status == "completed") { ThinkingText = "思考中..."; ResetStreamTimeout(); }
            });
            _client.OnStreamComplete += () => SafeInvoke(StopStreaming);
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

            if (result != null && result.Value.TryGetProperty("sessions", out var arr))
            {
                var existing = new HashSet<string>(Sessions.Select(s => s.Key));
                foreach (var s in arr.EnumerateArray())
                {
                    var key = s.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(key) || existing.Contains(key)) continue;
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
