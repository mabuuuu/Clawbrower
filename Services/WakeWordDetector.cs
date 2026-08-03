using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Clawbrower.Services;

/// <summary>
/// 唤醒词检测器（openwakeword 0.6.0 对齐实现）。
/// 三段 ONNX 推理链：melspectrogram → embedding → wakeword_erqi。
/// 流式处理：每 1280 样本（80ms）产生 8 帧 mel、1 个 embedding、1 次打分。
/// 参考：D:\soldier9527\wakeword_venv\Lib\site-packages\openwakeword\utils.py (AudioFeatures) 与 model.py (Model.predict)
/// </summary>
public class WakeWordDetector : IDisposable
{
    // ── 模型常量（对齐 openwakeword 0.6.0）──
    private const int SampleRate = 16000;
    private const int FrameSamples = 1280;          // 80ms 帧
    private const int MelLookbackSamples = 160 * 3; // mel 模型需要 3 帧回看
    private const int MelBins = 32;
    private const int MelFrameCount = 8;            // 每 1280 样本产出的 mel 帧数：ceil(1760/160)-3 = 8
    private const int EmbeddingWindow = 76;         // embedding 窗口 mel 帧数
    private const int EmbeddingDim = 96;
    private const int FeatureBufferMax = 120;       // ~10 秒 embedding 历史
    private const int MelBufferMax = 10 * 97;       // 970 帧 mel 历史
    private const int RawBufferMaxSamples = SampleRate * 10; // 10 秒原始音频
    private const int WakeWordWindow = 27;          // 唤醒词窗口 embedding 数（2.16s）
    private const int InitPredictionFrames = 5;     // 前 5 帧抑制（对齐 model.py：prediction_buffer < 5 → 0）

    private readonly InferenceSession _melSession;
    private readonly InferenceSession _embeddingSession;
    private readonly InferenceSession _wakewordSession;

    // ── 流式缓冲（对齐 AudioFeatures）──
    private readonly List<float> _rawSamples = new();   // 最近 10s int16 原值（转 float）
    private float[] _melBuffer = new float[MelBufferMax * MelBins]; // 初始前 76 帧全 1，滚动
    private int _melBufferFrames;
    private float[] _featureBuffer = new float[FeatureBufferMax * EmbeddingDim]; // 滚动
    private int _featureBufferFrames;
    private int _accumulatedSamples;
    private int _predictionCount;

    // ── 检测状态 ──
    private DateTime _lastTrigger = DateTime.MinValue;
    private DateTime _startTime = DateTime.UtcNow;
    private bool _disposed;

    /// <summary>模型是否加载成功（模型文件缺失时为 false）</summary>
    public bool IsAvailable { get; }

    /// <summary>触发阈值（0~1，可配置，默认 0.5）</summary>
    public float Threshold { get; set; } = 0.5f;

    /// <summary>触发后防抖时间（秒），期间不重复触发</summary>
    public double DebounceSeconds { get; set; } = 1.5;

    /// <summary>启动/重置后的冷却时间（秒），期间不触发（覆盖随机初始化窗口期的分数噪声）</summary>
    public double CooldownSeconds { get; set; } = 2.5;

    /// <summary>检测到唤醒词时触发（在推理后台线程）</summary>
    public event Action? WakeWordDetected;

    /// <summary>每次打分后的分数（调试/状态指示用，0~1）</summary>
    public event Action<float>? ScoreUpdated;

    /// <summary>模型文件所在目录（默认程序目录下 wakeword/）</summary>
    public WakeWordDetector(string? modelDir = null)
    {
        var dir = modelDir ?? Path.Combine(AppContext.BaseDirectory, "wakeword");
        try
        {
            var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
            _melSession = new InferenceSession(Path.Combine(dir, "melspectrogram.onnx"), options);
            _embeddingSession = new InferenceSession(Path.Combine(dir, "embedding_model.onnx"), options);
            _wakewordSession = new InferenceSession(Path.Combine(dir, "wakeword_erqi.onnx"), options);

            // 对齐 Python：feature_buffer 初始化为 4 秒随机 int16 数据生成的 embedding
            InitializeFeatureBuffer();
            IsAvailable = true;
            Logger.Info($"WakeWordDetector loaded from {dir}");
        }
        catch (Exception ex)
        {
            Logger.Error($"WakeWordDetector init failed: {ex.Message}");
            _melSession?.Dispose();
            _embeddingSession?.Dispose();
            _wakewordSession?.Dispose();
            _melSession = null!;
            _embeddingSession = null!;
            _wakewordSession = null!;
        }
    }

    /// <summary>
    /// 重置所有缓冲与状态（重新待命时调用）。
    /// </summary>
    public void Reset()
    {
        _rawSamples.Clear();
        _melBuffer = new float[MelBufferMax * MelBins];
        for (var i = 0; i < EmbeddingWindow * MelBins; i++) _melBuffer[i] = 1.0f; // 全 1（对齐 np.ones((76,32))）
        _melBufferFrames = EmbeddingWindow; // 初始 76 帧已计入（对齐 Python vstack 保留初始帧）
        _featureBuffer = new float[FeatureBufferMax * EmbeddingDim];
        _featureBufferFrames = 0;
        _accumulatedSamples = 0;
        _predictionCount = 0;
        _lastTrigger = DateTime.MinValue;
        _startTime = DateTime.UtcNow; // 冷却期起点
        InitializeFeatureBuffer();
    }

    /// <summary>
    /// 喂入 16kHz/16bit 单声道 PCM（byte 数组，小端）。
    /// 采集线程可安全调用：内部同步处理，每满 1280 样本进行一次推理。
    /// </summary>
    public void Feed(byte[] pcm)
    {
        if (!IsAvailable || _disposed) return;

        var sampleCount = pcm.Length / 2;
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            _rawSamples.Add(sample); // int16 原值（对齐 Python：不归一化）
        }
        if (_rawSamples.Count > RawBufferMaxSamples)
            _rawSamples.RemoveRange(0, _rawSamples.Count - RawBufferMaxSamples);

        _accumulatedSamples += sampleCount;
        if (_accumulatedSamples < FrameSamples || _accumulatedSamples % FrameSamples != 0)
            return;

        ProcessFrame();
        _accumulatedSamples = 0;
    }

    /// <summary>处理一帧（1280 样本）：mel → embedding → 打分（对齐 _streaming_features + predict）</summary>
    private void ProcessFrame()
    {
        // 1) melspectrogram：最近 1280+480 样本 → [8, 32]，spec/10+2
        var ctxLen = Math.Min(_rawSamples.Count, FrameSamples + MelLookbackSamples);
        var ctx = new float[ctxLen];
        for (var i = 0; i < ctxLen; i++)
            ctx[i] = _rawSamples[_rawSamples.Count - ctxLen + i];

        var mel = RunMel(ctx);
        // 2) 追加 mel 帧（保留最近 MelBufferMax 帧）
        AppendMelFrames(mel);
        // 3) 最近 76 帧 mel → 1 个 embedding [96]
        if (_melBufferFrames >= EmbeddingWindow)
        {
            var embedding = RunEmbedding();
            AppendEmbedding(embedding);
        }
        // 4) 最近 27 帧 embedding → 唤醒分数
        if (_featureBufferFrames >= WakeWordWindow)
        {
        var score = RunWakeWord();
        _predictionCount++;
        // 前 5 帧抑制（对齐 model.py：prediction_buffer 长度 < 5 时强制 0，第 6 帧起真实分数）
        if (_predictionCount > InitPredictionFrames)
        {
                ScoreUpdated?.Invoke(score);
                var now = DateTime.UtcNow;
                var cooldownPassed = (now - _startTime).TotalSeconds >= CooldownSeconds;
                if (score >= Threshold && cooldownPassed && (now - _lastTrigger).TotalSeconds >= DebounceSeconds)
                {
                    _lastTrigger = now;
                    Logger.Info($"WakeWordDetector triggered, score={score:F3}");
                    WakeWordDetected?.Invoke();
                }
            }
        }
    }

    /// <summary>melspectrogram.onnx：输入 [1, samples] float32，输出 [1, 1, time, 32]（对齐 Python np.squeeze）</summary>
    private float[,] RunMel(float[] samples)
    {
        var tensor = new DenseTensor<float>(samples, new[] { 1, samples.Length });
        using var results = _melSession.Run(new[] { NamedOnnxValue.CreateFromTensor("input", tensor) });
        using var output = results.Single();
        var data = output.AsTensor<float>();
        var time = data.Dimensions[2]; // 实际 shape [1, 1, time, 32]，time 在第 3 维
        var mel = new float[time, MelBins];
        for (var t = 0; t < time; t++)
            for (var b = 0; b < MelBins; b++)
                mel[t, b] = data[0, 0, t, b] / 10.0f + 2.0f; // spec/10+2（必须）
        return mel;
    }

    /// <summary>追加 mel 帧并滚动（保留最近 MelBufferMax 帧）</summary>
    private void AppendMelFrames(float[,] mel)
    {
        var time = mel.GetLength(0);
        // 丢弃最旧的，为新增帧腾出空间（保留最近 MelBufferMax 帧）
        if (_melBufferFrames + time > MelBufferMax)
        {
            var drop = _melBufferFrames + time - MelBufferMax;
            Array.Copy(_melBuffer, drop * MelBins, _melBuffer, 0, (_melBufferFrames - drop) * MelBins);
            _melBufferFrames -= drop;
        }
        // 追加新帧（mel 输出帧数一般不超过 8，无需截断；保险起见限制）
        var start = time - Math.Min(time, MelBufferMax);
        for (var t = start; t < time; t++)
        {
            var dst = _melBufferFrames * MelBins;
            for (var b = 0; b < MelBins; b++)
                _melBuffer[dst + b] = mel[t, b];
            _melBufferFrames++;
        }
    }

    /// <summary>embedding_model.onnx：输入 [1, 76, 32, 1]，输出 [1, 1, 1, 96] → [96]</summary>
    private float[] RunEmbedding()
    {
        var input = new DenseTensor<float>(new[] { 1, EmbeddingWindow, MelBins, 1 });
        var start = (_melBufferFrames - EmbeddingWindow) * MelBins;
        for (var f = 0; f < EmbeddingWindow; f++)
            for (var b = 0; b < MelBins; b++)
                input[0, f, b, 0] = _melBuffer[start + f * MelBins + b];

        using var results = _embeddingSession.Run(new[] { NamedOnnxValue.CreateFromTensor("input_1", input) });
        using var output = results.Single();
        var data = output.AsTensor<float>();
        var embedding = new float[EmbeddingDim];
        for (var i = 0; i < EmbeddingDim; i++) embedding[i] = data[0, 0, 0, i];
        return embedding;
    }

    /// <summary>追加 embedding 并滚动（保留最近 FeatureBufferMax 帧）</summary>
    private void AppendEmbedding(float[] embedding)
    {
        if (_featureBufferFrames >= FeatureBufferMax)
        {
            Array.Copy(_featureBuffer, EmbeddingDim, _featureBuffer, 0, (FeatureBufferMax - 1) * EmbeddingDim);
            _featureBufferFrames = FeatureBufferMax - 1;
        }
        Array.Copy(embedding, 0, _featureBuffer, _featureBufferFrames * EmbeddingDim, EmbeddingDim);
        _featureBufferFrames++;
    }

    /// <summary>wakeword_erqi.onnx：输入 [1, 27, 96]，输出 [1, 1]（sigmoid 已含）</summary>
    private float RunWakeWord()
    {
        var input = new DenseTensor<float>(new[] { 1, WakeWordWindow, EmbeddingDim });
        var start = (_featureBufferFrames - WakeWordWindow) * EmbeddingDim;
        for (var f = 0; f < WakeWordWindow; f++)
            for (var e = 0; e < EmbeddingDim; e++)
                input[0, f, e] = _featureBuffer[start + f * EmbeddingDim + e];

        using var results = _wakewordSession.Run(new[] { NamedOnnxValue.CreateFromTensor(_wakewordSession.InputNames[0], input) });
        using var output = results.Single();
        return output.AsTensor<float>()[0];
    }

    /// <summary>对齐 Python：feature_buffer 初始化为 4 秒随机 int16 数据生成的 embedding（41 帧）</summary>
    private void InitializeFeatureBuffer()
    {
        if (!IsAvailable || _melSession == null) return;
        _rawSamples.Clear();
        _accumulatedSamples = 0;

        // 4 秒随机 int16（randint(-1000, 1000)）—— 每次随机（对齐 Python 无种子）
        var random = new Random();
        var samples = new float[SampleRate * 4];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = random.Next(-1000, 1000);

        // mel：ceil(64000/160)-3 = 397 帧
        var mel = RunMel(samples);
        // 窗口步进 8：i=0,8,...,320 → 41 个窗口（320+76=396 <= 397）
        var windowCount = 0;
        var windowStart = new int[41];
        for (var i = 0; i + EmbeddingWindow <= mel.GetLength(0); i += 8)
            windowStart[windowCount++] = i;
        windowCount = Math.Min(windowCount, 41);

        _melBuffer = new float[MelBufferMax * MelBins];
        _melBufferFrames = 0;
        _featureBuffer = new float[FeatureBufferMax * EmbeddingDim];
        _featureBufferFrames = 0;

        // 逐个窗口跑 embedding（对齐 Python batch 推理，单窗口逐次即可，结果一致）
        for (var w = 0; w < windowCount; w++)
        {
            var input = new DenseTensor<float>(new[] { 1, EmbeddingWindow, MelBins, 1 });
            for (var f = 0; f < EmbeddingWindow; f++)
                for (var b = 0; b < MelBins; b++)
                    input[0, f, b, 0] = mel[windowStart[w] + f, b];

            using var results = _embeddingSession.Run(new[] { NamedOnnxValue.CreateFromTensor("input_1", input) });
            using var output = results.Single();
            var data = output.AsTensor<float>();
            var embedding = new float[EmbeddingDim];
            for (var i = 0; i < EmbeddingDim; i++) embedding[i] = data[0, 0, 0, i];
            AppendEmbedding(embedding);
        }

        _melBuffer = new float[MelBufferMax * MelBins];
        for (var i = 0; i < EmbeddingWindow * MelBins; i++) _melBuffer[i] = 1.0f;
        _melBufferFrames = EmbeddingWindow; // 初始 76 帧全 1（对齐 Python np.ones((76,32))）
        // 清空原始音频缓冲：初始化用的随机样本不进入流式（Python raw_data_buffer 从空开始）
        _rawSamples.Clear();
        _accumulatedSamples = 0;
        Logger.Info($"WakeWordDetector feature buffer initialized with {_featureBufferFrames} frames");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _melSession?.Dispose();
        _embeddingSession?.Dispose();
        _wakewordSession?.Dispose();
        GC.SuppressFinalize(this);
    }
}
