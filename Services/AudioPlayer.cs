using System.IO;
using NAudio.Wave;

namespace Clawbrower.Services;

/// <summary>
/// 使用 NAudio 播放内存中的 mp3 字节流。
/// 异步播放，播放完成后触发 OnPlaybackCompleted。
/// </summary>
public class AudioPlayer : IDisposable
{
    private WaveOutEvent? _waveOut;
    private bool _disposed;

    /// <summary>播放完成时触发</summary>
    public event Action? OnPlaybackCompleted;

    /// <summary>播放出错时触发</summary>
    public event Action<string>? OnError;

    /// <summary>当前是否正在播放</summary>
    public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;

    /// <summary>
    /// 异步播放 mp3 数据。如果当前正在播放会先停止。
    /// </summary>
    public void PlayMp3(byte[] mp3Data)
    {
        if (_disposed) return;

        Stop();

        try
        {
            var ms = new MemoryStream(mp3Data);
            var reader = new Mp3FileReader(ms);
            _waveOut = new WaveOutEvent();
            _waveOut.Init(reader);
            _waveOut.PlaybackStopped += (s, e) =>
            {
                Logger.Info($"AudioPlayer playback stopped, error={e.Exception?.Message ?? "none"}");
                if (e.Exception != null)
                    OnError?.Invoke($"播放异常: {e.Exception.Message}");
                else
                    OnPlaybackCompleted?.Invoke();

                // 清理资源
                reader.Dispose();
                ms.Dispose();
                _waveOut?.Dispose();
                _waveOut = null;
            };
            _waveOut.Play();
            Logger.Info($"AudioPlayer playing mp3, size={mp3Data.Length} bytes");
        }
        catch (Exception ex)
        {
            Logger.Error($"AudioPlayer play failed: {ex.Message}");
            OnError?.Invoke($"播放失败: {ex.Message}");
        }
    }

    /// <summary>停止当前播放</summary>
    public void Stop()
    {
        if (_waveOut?.PlaybackState == PlaybackState.Playing)
        {
            _waveOut.Stop();
        }
        _waveOut?.Dispose();
        _waveOut = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }

    ~AudioPlayer() => Stop();
}
