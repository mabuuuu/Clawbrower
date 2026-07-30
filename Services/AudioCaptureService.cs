using NAudio.Wave;

namespace Clawbrower.Services;

/// <summary>
/// 使用 NAudio 采集 16kHz/16bit/单声道 PCM 音频。
/// 每 200ms 回调一次（约 6400 字节/次）。
/// </summary>
public class AudioCaptureService : IDisposable
{
    private const int SampleRate = 16000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;
    private const int BufferMs = 200;

    private WaveInEvent? _waveIn;
    private bool _capturing;

    /// <summary>采集到音频数据时触发（buffer 为有效数据，已裁剪到 BytesRecorded）</summary>
    public event Action<byte[]>? OnAudioData;

    /// <summary>采集出错时触发</summary>
    public event Action<string>? OnError;

    /// <summary>当前是否正在采集</summary>
    public bool IsCapturing => _capturing;

    /// <summary>开始采集</summary>
    public void Start()
    {
        if (_capturing) return;

        try
        {
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
                BufferMilliseconds = BufferMs
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn.StartRecording();
            _capturing = true;
            Logger.Info($"AudioCapture started: {SampleRate}Hz/{BitsPerSample}bit/{Channels}ch, buffer={BufferMs}ms");
        }
        catch (Exception ex)
        {
            Logger.Error($"AudioCapture start failed: {ex.Message}");
            OnError?.Invoke($"麦克风启动失败: {ex.Message}");
            _waveIn?.Dispose();
            _waveIn = null;
        }
    }

    /// <summary>停止采集</summary>
    public void Stop()
    {
        if (!_capturing) return;
        _capturing = false;
        try
        {
            _waveIn?.StopRecording();
        }
        catch (Exception ex)
        {
            Logger.Error($"AudioCapture stop error: {ex.Message}");
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_capturing || e.BytesRecorded == 0) return;

        // 裁剪到实际录制的字节数
        var data = new byte[e.BytesRecorded];
        Array.Copy(e.Buffer, data, e.BytesRecorded);
        OnAudioData?.Invoke(data);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            Logger.Error($"AudioCapture stopped with error: {e.Exception.Message}");
            OnError?.Invoke($"录音异常: {e.Exception.Message}");
        }
        _waveIn?.Dispose();
        _waveIn = null;
        _capturing = false;
        Logger.Info("AudioCapture fully stopped");
    }

    public void Dispose()
    {
        Stop();
        _waveIn?.Dispose();
        _waveIn = null;
        GC.SuppressFinalize(this);
    }

    ~AudioCaptureService() => Stop();
}
