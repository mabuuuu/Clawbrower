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
    private readonly WakeWordDetector _wakeWord = new();
    private SpeechClient? _client;
    private readonly Queue<byte[]> _playQueue = new();
    private bool _audioEnded;
    private bool _isPlayingChunk;
    private SpeechState _state = SpeechState.Disabled;
    private SpeechMode _mode = SpeechMode.PTT;
    private System.Windows.Threading.DispatcherTimer? _silenceTimer;
    private System.Windows.Threading.DispatcherTimer? _deadlineTimer;
    private DateTime _voiceDeadline = DateTime.MaxValue;

    /// <summary>唤醒词模式：静默结束录音的等待时间（毫秒）</summary>
    private const int WakeWordSilenceEndMs = 1800;

    /// <summary>唤醒词模式：唤醒后无语音的总超时（秒），防止无限等待</summary>
    private const int WakeWordNoVoiceTimeoutSec = 15;
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

    /// <summary>声音活动变化 -> 切换录音浮层红/绿点 + 唤醒词模式静默计时（切到 UI 线程）</summary>
    private void OnVoiceActivityChanged(bool speaking)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_state != SpeechState.Recording) return;
            _overlay?.SetSpeaking(speaking);

            if (_mode != SpeechMode.WakeWord) return;
            if (speaking)
            {
                // 用户说话了：停止静默计时，并延长总超时
                _silenceTimer?.Stop();
                _voiceDeadline = DateTime.UtcNow.AddSeconds(WakeWordNoVoiceTimeoutSec);
            }
            else
            {
                // 转为静默：启动静默结束计时
                _silenceTimer?.Start();
            }
        });
    }

    /// <summary>
    /// 启用语音功能（安装键盘钩子，进入 Listening 状态）。
    /// 唤醒词模式会持续采集音频并喂给本地检测器。
    /// 必须在 UI 线程调用。
    /// </summary>
    public void Enable(int pttVirtualKey, SpeechMode mode = SpeechMode.PTT,
        double wakeThreshold = 0.5, double wakeCooldown = 2.5)
    {
        if (_state != SpeechState.Disabled) return;

        _mode = mode;
        _overlay = new RecordingOverlay();
        _keyboard.Install(pttVirtualKey);

        if (mode == SpeechMode.WakeWord)
        {
            _wakeWord.Threshold = (float)wakeThreshold;
            _wakeWord.CooldownSeconds = wakeCooldown;
            _wakeWord.Reset();
            _wakeWord.WakeWordDetected += OnWakeWordDetected;
            if (!_wakeWord.IsAvailable)
            {
                OnStatusMessage?.Invoke("唤醒词模型加载失败，请检查程序目录 wakeword/ 文件夹");
                Logger.Error("WakeWordDetector unavailable, wake word mode degraded");
            }
            // 常驻采集：Listening 时喂检测器，Recording 时发服务器
            _capture.Start();
            OnStatusMessage?.Invoke("语音已开启（唤醒词模式），说\"二七二七\"开始对话");
            Logger.Info($"SpeechService enabled in WakeWord mode, threshold={wakeThreshold}, cooldown={wakeCooldown}s");
        }
        else
        {
            OnStatusMessage?.Invoke("语音已开启，按住 PTT 键说话");
            Logger.Info($"SpeechService enabled, PTT VK=0x{pttVirtualKey:X2}");
        }

        SetState(SpeechState.Listening);
        OnEnabledChanged?.Invoke(true);
    }

    /// <summary>
    /// 禁用语音功能（停止一切，卸载钩子）。
    /// </summary>
    public void Disable()
    {
        if (_state == SpeechState.Disabled) return;

        _silenceTimer?.Stop();
        _deadlineTimer?.Stop();
        _wakeWord.WakeWordDetected -= OnWakeWordDetected;
        _keyboard.Uninstall();
        _capture.Stop();
        _player.Stop();
        ClearPlayQueue();
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
                ClearPlayQueue();
                SetState(SpeechState.Listening);
            }

            if (_state != SpeechState.Listening) return;

            StartConversation("按住 PTT 说话");
        });
    }

    /// <summary>唤醒词触发（检测器后台线程）→ 自动开始对话</summary>
    private void OnWakeWordDetected()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_mode != SpeechMode.WakeWord || _state != SpeechState.Listening) return;
            OnStatusMessage?.Invoke("已唤醒，请说话...");
            StartConversation("已唤醒，请说话...");
        });
    }

    /// <summary>开始一轮对话：连接服务器 + 进入录音状态（PTT 按下与唤醒词共用）</summary>
    private void StartConversation(string statusMessage)
    {
        if (_state != SpeechState.Listening) return;

        ClearPlayQueue();

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
        if (!_capture.IsCapturing)
            _capture.Start(); // PTT 模式：立即开始录音（唤醒词模式已常驻采集）

        if (_mode == SpeechMode.WakeWord)
        {
            // 唤醒词模式：启动静默结束 + 无语音总超时
            _silenceTimer ??= new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(WakeWordSilenceEndMs)
            };
            _silenceTimer.Tick -= OnSilenceTimerTick;
            _silenceTimer.Tick += OnSilenceTimerTick;
            _silenceTimer.Stop();

            _deadlineTimer ??= new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _deadlineTimer.Tick -= OnDeadlineTimerTick;
            _deadlineTimer.Tick += OnDeadlineTimerTick;
            _voiceDeadline = DateTime.UtcNow.AddSeconds(WakeWordNoVoiceTimeoutSec);
            _deadlineTimer.Start();
        }
        OnStatusMessage?.Invoke(statusMessage);

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
                    if (_mode != SpeechMode.WakeWord)
                        _capture.Stop();
                    DisconnectClient();
                    if (_state == SpeechState.Recording || _state == SpeechState.Waiting)
                        SetState(SpeechState.Listening);
                });
            }
        });
    }

    /// <summary>唤醒词模式：静默超时 → 自动结束录音并发送</summary>
    private void OnSilenceTimerTick(object? sender, EventArgs e)
    {
        _silenceTimer?.Stop();
        Logger.Info("WakeWord silence timeout, ending recording");
        FinishRecording();
    }

    /// <summary>唤醒词模式：无语音总超时（唤醒后一直没人说话）→ 结束</summary>
    private void OnDeadlineTimerTick(object? sender, EventArgs e)
    {
        if (_state != SpeechState.Recording) { _deadlineTimer?.Stop(); return; }
        if (DateTime.UtcNow < _voiceDeadline) return;
        _deadlineTimer?.Stop();
        Logger.Info("WakeWord no-voice timeout, ending recording");
        FinishRecording();
    }

    private void OnPttKeyUp()
    {
        if (_state != SpeechState.Recording) return;

        _silenceTimer?.Stop();
        _deadlineTimer?.Stop();
        FinishRecording();
    }

    /// <summary>
    /// 结束录音并发送 end 标记（PTT 松开 / 唤醒词静默超时共用）。
    /// PTT 模式先停采集等缓冲音频发出；唤醒词模式采集常驻，直接发送。
    /// </summary>
    private void FinishRecording()
    {
        if (_state != SpeechState.Recording) return;

        var stopCapture = _mode != SpeechMode.WakeWord;

        _ = Task.Run(async () =>
        {
            // 等待录音完全停止：NAudio 的 RecordingStopped 触发时，
            // 所有缓冲音频已通过 OnAudioCaptured 发出（状态仍为 Recording，不会被丢弃）
            if (stopCapture)
            {
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
        // 在 NAudio 工作线程：Recording 发服务器；唤醒词模式 Listening 时喂检测器
        if (_state == SpeechState.Recording)
        {
            _ = _client?.SendAudioAsync(data);
        }
        else if (_state == SpeechState.Listening && _mode == SpeechMode.WakeWord && _wakeWord.IsAvailable)
        {
            _wakeWord.Feed(data);
        }
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
                if (stage == "thinking")
                    OnStatusMessage?.Invoke("正在思考...");
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
            // 在 WebSocket 接收线程，切到 UI 线程入队并调度播放（边收边播，不等 audio_end）
            Application.Current?.Dispatcher.Invoke(() =>
            {
                _playQueue.Enqueue(data);
                Logger.Info($"SpeechClient mp3 chunk queued: {data.Length} bytes, queue={_playQueue.Count}");
                // 空闲则立即开始播
                if ((_state == SpeechState.Waiting || _state == SpeechState.Playing) && !_player.IsPlaying)
                    PlayNextChunk();
            });
        };

        client.OnAudioEnd += () =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                // audio_end 语义：所有分句已下发完毕（不是音频已全部到达）
                _audioEnded = true;
                Logger.Info($"SpeechService audio_end received, queue={_playQueue.Count}");
                // 若队列已空且没有块在播，直接结束本轮
                if (_playQueue.Count == 0 && !_isPlayingChunk)
                {
                    DisconnectClient();
                    SetState(SpeechState.Listening);
                }
            });
        };

        client.OnError += msg =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                OnStatusMessage?.Invoke($"语音错误: {msg}");
                if (_state == SpeechState.Waiting || _state == SpeechState.Recording || _state == SpeechState.Playing)
                {
                    _player.Stop();
                    ClearPlayQueue();
                    DisconnectClient();
                    SetState(SpeechState.Listening);
                }
            });
        };

        client.OnDisconnected += () =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (_state == SpeechState.Waiting || _state == SpeechState.Playing)
                {
                    _player.Stop();
                    ClearPlayQueue();
                    OnStatusMessage?.Invoke("语音连接已断开");
                    DisconnectClient();
                    SetState(SpeechState.Listening);
                }
            });
        };
    }

    /// <summary>播放下一个音频块（队列空且收到 audio_end 则结束本轮）</summary>
    private void PlayNextChunk()
    {
        // 防重入：已有块在播放时，忽略重复调度（OnMp3Data 与 OnPlaybackCompleted 可能竞态）
        if (_isPlayingChunk) return;

        if (_playQueue.Count == 0)
        {
            // 队列空：若已收到 audio_end（所有分句已下发），结束本轮
            if (_audioEnded)
            {
                DisconnectClient();
                SetState(SpeechState.Listening);
                Logger.Info("SpeechService playback queue drained, back to listening");
            }
            return;
        }

        _isPlayingChunk = true;
        var chunk = _playQueue.Dequeue();
        if (_state != SpeechState.Playing)
            SetState(SpeechState.Playing);
        _player.PlayMp3(chunk);
        Logger.Info($"SpeechService playing chunk, remaining={_playQueue.Count}");
    }

    /// <summary>清空播放队列并重置 audio_end/播放中标记</summary>
    private void ClearPlayQueue()
    {
        _playQueue.Clear();
        _audioEnded = false;
        _isPlayingChunk = false;
    }

    private void OnPlaybackCompleted()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            // 当前块已自然播完，重置标记并播队列中的下一块（边收边播）
            _isPlayingChunk = false;
            PlayNextChunk();
            Logger.Info("SpeechService chunk playback completed");
        });
    }

    private void OnPlaybackError(string msg)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            OnStatusMessage?.Invoke(msg);
            ClearPlayQueue();
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

        // 录音浮层：Recording/Waiting/Playing 显示并更新状态文字，离开时隐藏
        switch (newState)
        {
            case SpeechState.Recording:
                _overlay?.Show();
                _overlay?.SetState(newState);
                break;
            case SpeechState.Waiting:
            case SpeechState.Playing:
                _overlay?.Show();
                _overlay?.SetState(newState);
                break;
            default:
                if (old == SpeechState.Recording || old == SpeechState.Waiting || old == SpeechState.Playing)
                {
                    _overlay?.Hide();
                    // 唤醒词模式：对话/播放结束后重置检测器（清窗口，避免 TTS 回声/环境音误触发）
                    if (_mode == SpeechMode.WakeWord && _wakeWord.IsAvailable)
                        _wakeWord.Reset();
                }
                break;
        }

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
        _wakeWord.Dispose();
        _playQueue.Clear();
        GC.SuppressFinalize(this);
    }

    ~SpeechService() => Disable();
}
