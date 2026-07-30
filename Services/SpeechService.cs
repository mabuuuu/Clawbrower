using System.IO;
using System.Windows;
using Application = System.Windows.Application;

namespace Clawbrower.Services;

/// <summary>
/// 语音协调器：整合键盘钩子、录音、WebSocket通信、播放的状态机。
/// 在 UI 线程上创建和调用。
/// </summary>
public class SpeechService : IDisposable
{
    public enum SpeechState
    {
        Disabled,    // 语音未开启
        Listening,   // 待命，等待 PTT 按键
        Recording,   // 正在录音 + 发送音频
        Waiting,     // 已发送 end 标记，等待服务器回复
        Playing,     // 正在播放回复语音
    }

    private readonly KeyboardHookService _keyboard = new();
    private readonly AudioCaptureService _capture = new();
    private readonly AudioPlayer _player = new();
    private SpeechClient? _client;
    private readonly MemoryStream _mp3Buffer = new();
    private SpeechState _state = SpeechState.Disabled;
    private bool _disposed;
    private bool _intentionalDisconnect;
    private RecordingOverlay? _overlay;

    /// <summary>语音状态变化</summary>
    public event Action<SpeechState>? OnStateChanged;

    /// <summary>状态提示消息（如"正在识别..."），用于显示在聊天窗口</summary>
    public event Action<string>? OnStatusMessage;

    /// <summary>ASR 识别结果（用户说的话），需显示为用户消息</summary>
    public event Action<string>? OnTranscript;

    /// <summary>助手回复文字，需显示为助手消息</summary>
    public event Action<string>? OnReply;

    /// <summary>语音开关状态变化</summary>
    public event Action<bool>? OnEnabledChanged;

    /// <summary>当前状态</summary>
    public SpeechState State => _state;

    /// <summary>语音是否已开启</summary>
    public bool IsEnabled => _state != SpeechState.Disabled;

    public SpeechService()
    {
        _keyboard.OnKeyDown += OnPttKeyDown;
        _keyboard.OnKeyUp += OnPttKeyUp;
        _capture.OnAudioData += OnAudioCaptured;
        _capture.OnError += OnCaptureError;
        _capture.OnVoiceActivityChanged += OnVoiceActivityChanged;
        _player.OnPlaybackCompleted += OnPlaybackCompleted;
        _player.OnError += OnPlaybackError;
    }

    /// <summary>声音活动变化 -> 切换录音浮层红/绿点（切到 UI 线程）</summary>
    private void OnVoiceActivityChanged(bool speaking)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_state == SpeechState.Recording)
                _overlay?.SetSpeaking(speaking);
        });
    }

    /// <summary>
    /// 启用语音功能（安装键盘钩子，进入 Listening 状态）。
    /// 必须在 UI 线程调用。
    /// </summary>
    public void Enable(int pttVirtualKey)
    {
        if (_state != SpeechState.Disabled) return;

        _overlay = new RecordingOverlay();
        _keyboard.Install(pttVirtualKey);
        SetState(SpeechState.Listening);
        OnEnabledChanged?.Invoke(true);
        OnStatusMessage?.Invoke("语音已开启，按住 PTT 键说话");
        Logger.Info($"SpeechService enabled, PTT VK=0x{pttVirtualKey:X2}");
    }

    /// <summary>
    /// 禁用语音功能（停止一切，卸载钩子）。
    /// </summary>
    public void Disable()
    {
        if (_state == SpeechState.Disabled) return;

        _keyboard.Uninstall();
        _capture.Stop();
        _player.Stop();
        _mp3Buffer.SetLength(0);
        DisconnectClient();
        _overlay?.Hide();
        _overlay?.Close();
        _overlay = null;

        SetState(SpeechState.Disabled);
        OnEnabledChanged?.Invoke(false);
        OnStatusMessage?.Invoke("语音已关闭");
        Logger.Info("SpeechService disabled");
    }

    /// <summary>更新 PTT 按键（语音开启时实时切换）</summary>
    public void UpdatePttKey(int virtualKey)
    {
        if (_state == SpeechState.Disabled)
        {
            // 未开启时只保存配置，下次 Enable 时生效
            return;
        }
        _keyboard.ChangeKey(virtualKey);
        Logger.Info($"SpeechService PTT key changed to VK=0x{virtualKey:X2}");
    }

    private void OnPttKeyDown()
    {
        // 只在 Listening 或 Playing 状态响应 PTT 按下
        if (_state != SpeechState.Listening && _state != SpeechState.Playing) return;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            // 如果正在播放语音，打断播放
            if (_state == SpeechState.Playing)
            {
                _intentionalDisconnect = true;
                _player.Stop();
                DisconnectClient();
                _mp3Buffer.SetLength(0);
                SetState(SpeechState.Listening);
            }

            if (_state != SpeechState.Listening) return;

            _mp3Buffer.SetLength(0);

            // 连接语音服务器
            var url = ConfigService.GetSpeechServerUrl();
            if (string.IsNullOrEmpty(url))
            {
                OnStatusMessage?.Invoke("未配置语音服务器地址，请在设置中填写");
                SetState(SpeechState.Listening);
                return;
            }
            _client = new SpeechClient();
            RegisterSpeechClientEvents(_client);

            // 获取当前会话 key
            var sessionKey = "agent:main:main";
            if (Application.Current?.MainWindow?.DataContext is ViewModels.MainViewModel vm)
                sessionKey = vm.SessionKey;

            SetState(SpeechState.Recording);
            _capture.ResetVoiceState();
            _capture.Start(); // 立即开始录音，不等连接（连接前的音频块在连接建立前会被 SendAudioAsync 丢弃）

            _ = Task.Run(async () =>
            {
                try
                {
                    await _client.ConnectAsync(url, sessionKey);
                    // 连接+session握手成功，OnAudioCaptured 会自动发送音频
                }
                catch (Exception ex)
                {
                    Logger.Error($"SpeechClient connect failed: {ex.Message}");
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        // 用户主动打断时不提示连接失败
                        if (!_intentionalDisconnect)
                            OnStatusMessage?.Invoke($"语音服务器连接失败: {ex.Message}");
                        _intentionalDisconnect = false;
                        _capture.Stop();
                        DisconnectClient();
                        if (_state == SpeechState.Recording || _state == SpeechState.Waiting)
                            SetState(SpeechState.Listening);
                    });
                }
            });
        });
    }

    private void OnPttKeyUp()
    {
        if (_state != SpeechState.Recording) return;

        _ = Task.Run(async () =>
        {
            // 等待录音完全停止：NAudio 的 RecordingStopped 触发时，
            // 所有缓冲音频已通过 OnAudioCaptured 发出（状态仍为 Recording，不会被丢弃）
            try
            {
                await _capture.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
                Logger.Error("AudioCapture stop timeout, sending end anyway");
            }
            catch (Exception ex)
            {
                Logger.Error($"AudioCapture stop failed: {ex.Message}");
            }

            await Application.Current!.Dispatcher.InvokeAsync(() =>
            {
                if (_state != SpeechState.Recording) return;

                // 发送结束标记（此时最后缓冲的音频已全部发出）
                if (_client?.IsConnected == true)
                {
                    SetState(SpeechState.Waiting);
                    _ = _client.SendEndAsync();
                }
                else
                {
                    // 连接未建立或已断开，回到 Listening
                    DisconnectClient();
                    SetState(SpeechState.Listening);
                    OnStatusMessage?.Invoke("语音连接未就绪，请重试");
                }
            });
        });
    }

    private void OnAudioCaptured(byte[] data)
    {
        // 在 NAudio 工作线程，直接发送（SpeechClient 内部有发送锁）
        if (_state != SpeechState.Recording) return;
        _ = _client?.SendAudioAsync(data);
    }

    private void OnCaptureError(string msg)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            OnStatusMessage?.Invoke(msg);
            if (_state == SpeechState.Recording)
            {
                _capture.Stop();
                DisconnectClient();
                SetState(SpeechState.Listening);
            }
        });
    }

    private void RegisterSpeechClientEvents(SpeechClient client)
    {
        client.OnStatus += stage =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                Logger.Info($"SpeechService status: {stage}");
            });
        };

        client.OnTranscript += text =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                OnTranscript?.Invoke(text);
            });
        };

        client.OnReply += text =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                OnReply?.Invoke(text);
            });
        };

        client.OnMp3Data += data =>
        {
            // 在 WebSocket 接收线程，写入缓冲区需要线程安全
            lock (_mp3Buffer)
            {
                _mp3Buffer.Write(data, 0, data.Length);
            }
            Logger.Info($"SpeechClient mp3 chunk received: {data.Length} bytes, total={_mp3Buffer.Length}");
        };

        client.OnAudioEnd += () =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                PlayMp3Reply();
            });
        };

        client.OnError += msg =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                OnStatusMessage?.Invoke($"语音错误: {msg}");
                if (_state == SpeechState.Waiting || _state == SpeechState.Recording)
                {
                    DisconnectClient();
                    SetState(SpeechState.Listening);
                }
            });
        };

        client.OnDisconnected += () =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (_state == SpeechState.Waiting)
                {
                    OnStatusMessage?.Invoke("语音连接已断开");
                    SetState(SpeechState.Listening);
                }
            });
        };
    }

    private void PlayMp3Reply()
    {
        byte[] mp3Data;
        lock (_mp3Buffer)
        {
            if (_mp3Buffer.Length == 0)
            {
                // 没有音频数据，直接回到 Listening
                DisconnectClient();
                SetState(SpeechState.Listening);
                return;
            }
            mp3Data = _mp3Buffer.ToArray();
            _mp3Buffer.SetLength(0);
        }

        SetState(SpeechState.Playing);
        _player.PlayMp3(mp3Data);
    }

    private void OnPlaybackCompleted()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            DisconnectClient();
            SetState(SpeechState.Listening);
            Logger.Info("SpeechService playback completed, back to listening");
        });
    }

    private void OnPlaybackError(string msg)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            OnStatusMessage?.Invoke(msg);
            DisconnectClient();
            SetState(SpeechState.Listening);
        });
    }

    private void DisconnectClient()
    {
        if (_client != null)
        {
            // Dispose 后 WebSocket 接收循环停止，事件不会再触发
            _client.Dispose();
            _client = null;
        }
    }

    private void SetState(SpeechState newState)
    {
        if (_state == newState) return;
        var old = _state;
        _state = newState;

        // 录音浮层：进入 Recording 显示，离开时隐藏
        if (newState == SpeechState.Recording)
            _overlay?.Show();
        else if (old == SpeechState.Recording)
            _overlay?.Hide();

        Logger.Info($"SpeechService state: {old} -> {newState}");
        OnStateChanged?.Invoke(newState);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disable();
        _keyboard.Dispose();
        _capture.Dispose();
        _player.Dispose();
        _mp3Buffer.Dispose();
        GC.SuppressFinalize(this);
    }

    ~SpeechService() => Disable();
}
