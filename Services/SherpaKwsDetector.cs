// ============================================================================
// SherpaKwsDetector.cs — sherpa-onnx KWS 唤醒词检测器（替换自训 WakeWordDetector）
// 整词检测「二七二七」（èr q ī èr q ī），只说「二七」/普通语音/环境音不触发。
// 接口与 WakeWordDetector 完全兼容：IsAvailable / Threshold / CooldownSeconds /
//   Reset / Feed / WakeWordDetected / ScoreUpdated / Dispose
// 使用前提（模型与 DLL 放置）：
//   1) 在 cb 输出目录新建 kws/ 子目录，放入：
//        sherpa-onnx-c-api.dll / sherpa-onnx-cxx-api.dll / onnxruntime.dll
//        encoder.onnx / decoder.onnx / joiner.onnx / tokens.txt
//      （DLL 取自 kws_venv\Lib\site-packages\sherpa_onnx\lib\；
//        模型取自 kws_models\sherpa-onnx-kws-zipformer-zh-en-3M-2025-12-20\，
//        用 chunk-8 int8：encoder/joiner 用 int8，decoder 用 fp32）
//   2) SpeechService.cs 第 26 行：WakeWordDetector 改成 SherpaKwsDetector（仅此一处）
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Clawbrower.Services
{
    public class SherpaKwsDetector : IDisposable
    {
        private const int SampleRate = 16000;
        private const string Keyword = "èr q ī èr q ī @二七二七";

        // —— VAD 语音闸门（沿用原 WakeWordDetector 参数）——
        private const double VoiceGateRms = 0.015;      // 归一化 RMS 阈值（int16≈491）
        private const int VoiceGateKeepFrames = 128;
        private const int VoiceGateWindowFrames = 27;
        private const int VoiceGateMinVoiceFrames = 3;
        private readonly List<bool> _voiceFrames = new();

        private IntPtr _spotter = IntPtr.Zero;
        private IntPtr _stream = IntPtr.Zero;
        private bool _disposed;
        private DateTime _lastTrigger = DateTime.MinValue;
        private DateTime _startTime = DateTime.UtcNow;
        private readonly object _lock = new();
        private readonly List<byte> _pcmBuf = new();

        public bool IsAvailable { get; private set; }
        public float Threshold { get; set; } = 0.5f;      // 兼容保留（KWS 用内部 keywords_threshold=0.15）
        public double CooldownSeconds { get; set; } = 2.5;

        public event Action? WakeWordDetected;
        public event Action<float>? ScoreUpdated;

        public SherpaKwsDetector(string? modelDir = null)
        {
            var dir = modelDir ?? Path.Combine(AppContext.BaseDirectory, "kws");
            try
            {
                var enc = Path.Combine(dir, "encoder.onnx");
                var dec = Path.Combine(dir, "decoder.onnx");
                var joiner = Path.Combine(dir, "joiner.onnx");
                var tokens = Path.Combine(dir, "tokens.txt");
                if (!File.Exists(enc) || !File.Exists(dec) || !File.Exists(joiner) || !File.Exists(tokens))
                {
                    Logger.Error($"SherpaKwsDetector: model files missing in {dir}");
                    return;
                }

                // 显式按完整路径加载 sherpa-onnx 的 3 个 DLL（避免与进程内 ML.OnnxRuntime 的
                // onnxruntime.dll 版本冲突；P/Invoke 会自动匹配已加载模块）
                NativeMethods.LoadLibrary(Path.Combine(dir, "onnxruntime.dll"));
                NativeMethods.LoadLibrary(Path.Combine(dir, "sherpa-onnx-cxx-api.dll"));
                NativeMethods.LoadLibrary(Path.Combine(dir, "sherpa-onnx-c-api.dll"));

                var cfg = BuildConfig(enc, dec, joiner, tokens);
                _spotter = SherpaOnnxNative.SherpaOnnxCreateKeywordSpotter(ref cfg);
                FreeConfigStrings(cfg);
                if (_spotter == IntPtr.Zero)
                {
                    Logger.Error("SherpaKwsDetector: SherpaOnnxCreateKeywordSpotter failed");
                    return;
                }

                _stream = SherpaOnnxNative.SherpaOnnxCreateKeywordStream(_spotter);
                IsAvailable = _stream != IntPtr.Zero;
                Logger.Info(IsAvailable
                    ? $"SherpaKwsDetector loaded from {dir} (keyword=二七二七)"
                    : "SherpaKwsDetector: create keyword stream failed");
            }
            catch (Exception ex)
            {
                Logger.Error($"SherpaKwsDetector init failed: {ex.Message}");
                Cleanup();
            }
        }

        private static SherpaOnnxNative.SherpaOnnxKeywordSpotterConfig BuildConfig(
            string enc, string dec, string joiner, string tokens)
        {
            var m = new SherpaOnnxNative.SherpaOnnxOnlineModelConfig
            {
                transducer = new SherpaOnnxNative.SherpaOnnxOnlineTransducerModelConfig
                {
                    encoder = Marshal.StringToCoTaskMemAnsi(enc),
                    decoder = Marshal.StringToCoTaskMemAnsi(dec),
                    joiner = Marshal.StringToCoTaskMemAnsi(joiner),
                },
                tokens = Marshal.StringToCoTaskMemAnsi(tokens),
                num_threads = 2,
                provider = Marshal.StringToCoTaskMemAnsi("cpu"),
                debug = 0,
            };

            var cfg = new SherpaOnnxNative.SherpaOnnxKeywordSpotterConfig
            {
                feat_config = new SherpaOnnxNative.SherpaOnnxFeatureConfig { sample_rate = 16000, feature_dim = 80 },
                model_config = m,
                max_active_paths = 4,
                num_trailing_blanks = 0,
                keywords_score = 1.0f,
                keywords_threshold = 0.15f,
            };

            var kwBytes = Encoding.UTF8.GetBytes(Keyword + "\n");
            cfg.keywords_buf = Marshal.AllocHGlobal(kwBytes.Length + 1);
            Marshal.Copy(kwBytes, 0, cfg.keywords_buf, kwBytes.Length);
            Marshal.WriteByte(cfg.keywords_buf, kwBytes.Length, 0);
            cfg.keywords_buf_size = kwBytes.Length;
            return cfg;
        }

        private static void FreeConfigStrings(SherpaOnnxNative.SherpaOnnxKeywordSpotterConfig cfg)
        {
            var m = cfg.model_config;
            Marshal.FreeCoTaskMem(m.transducer.encoder);
            Marshal.FreeCoTaskMem(m.transducer.decoder);
            Marshal.FreeCoTaskMem(m.transducer.joiner);
            Marshal.FreeCoTaskMem(m.tokens);
            Marshal.FreeCoTaskMem(m.provider);
            Marshal.FreeHGlobal(cfg.keywords_buf);
        }

        public void Reset()
        {
            lock (_lock)
            {
                _voiceFrames.Clear();
                _pcmBuf.Clear();
                _lastTrigger = DateTime.MinValue;
                _startTime = DateTime.UtcNow;
                if (_stream != IntPtr.Zero && _spotter != IntPtr.Zero)
                    SherpaOnnxNative.SherpaOnnxResetKeywordStream(_spotter, _stream);
            }
        }

        /// <summary>喂入 16kHz/16bit 单声道 PCM（byte 数组）。采集线程可安全调用。</summary>
        public void Feed(byte[] pcm)
        {
            if (!IsAvailable || _disposed || pcm == null || pcm.Length == 0) return;
            lock (_lock)
            {
                _pcmBuf.AddRange(pcm);
                RecordVoiceFrame(pcm);

                var sampleCount = _pcmBuf.Count / 2;
                var samples = new float[sampleCount];
                for (var i = 0; i < sampleCount; i++)
                {
                    var s = (short)(_pcmBuf[i * 2] | (_pcmBuf[i * 2 + 1] << 8));
                    samples[i] = s / 32768f;
                }
                _pcmBuf.Clear();
                if (samples.Length == 0) return;

                SherpaOnnxNative.SherpaOnnxOnlineStreamAcceptWaveform(_stream, SampleRate, samples, samples.Length);
                while (SherpaOnnxNative.SherpaOnnxIsKeywordStreamReady(_spotter, _stream) == 1)
                {
                    SherpaOnnxNative.SherpaOnnxDecodeKeywordStream(_spotter, _stream);
                    CheckResult();
                }
            }
        }

        private void CheckResult()
        {
            var r = SherpaOnnxNative.SherpaOnnxGetKeywordResult(_spotter, _stream);
            if (r == IntPtr.Zero) return;
            try
            {
                var kr = Marshal.PtrToStructure<SherpaOnnxNative.SherpaOnnxKeywordResult>(r);
                var kw = kr.keyword == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(kr.keyword);
                ScoreUpdated?.Invoke(1f);

                if (string.IsNullOrEmpty(kw)) return;
                var now = DateTime.UtcNow;
                var cooldownPassed = (now - _startTime).TotalSeconds >= CooldownSeconds;
                if (VoiceGatePassed() && cooldownPassed &&
                    (now - _lastTrigger).TotalSeconds >= 1.5)
                {
                    _lastTrigger = now;
                    SherpaOnnxNative.SherpaOnnxResetKeywordStream(_spotter, _stream);
                    Logger.Info($"SherpaKwsDetector triggered, keyword={kw}");
                    WakeWordDetected?.Invoke();
                }
            }
            finally
            {
                SherpaOnnxNative.SherpaOnnxDestroyKeywordResult(r);
            }
        }

        // —— VAD 语音闸门（复制原 WakeWordDetector 逻辑）——
        private void RecordVoiceFrame(byte[] data)
        {
            var n = data.Length / 2;
            double sum = 0;
            for (var i = 0; i < n; i++)
            {
                var v = (short)(data[i * 2] | (data[i * 2 + 1] << 8)) / 32768.0;
                sum += v * v;
            }
            _voiceFrames.Add(n > 0 && Math.Sqrt(sum / n) >= VoiceGateRms);
            while (_voiceFrames.Count > VoiceGateKeepFrames)
                _voiceFrames.RemoveAt(0);
        }

        private bool VoiceGatePassed()
        {
            var take = Math.Min(VoiceGateWindowFrames, _voiceFrames.Count);
            if (take == 0) return false;
            var cnt = 0;
            for (var i = _voiceFrames.Count - take; i < _voiceFrames.Count; i++)
                if (_voiceFrames[i]) cnt++;
            return cnt >= VoiceGateMinVoiceFrames;
        }

        private void Cleanup()
        {
            if (_stream != IntPtr.Zero) { SherpaOnnxNative.SherpaOnnxDestroyOnlineStream(_stream); _stream = IntPtr.Zero; }
            if (_spotter != IntPtr.Zero) { SherpaOnnxNative.SherpaOnnxDestroyKeywordSpotter(_spotter); _spotter = IntPtr.Zero; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Cleanup();
            GC.SuppressFinalize(this);
        }

        ~SherpaKwsDetector() => Dispose();
    }

    // ======================= sherpa-onnx C API P/Invoke =======================
    internal static class SherpaOnnxNative
    {
        private const string Dll = "sherpa-onnx-c-api.dll";

        [StructLayout(LayoutKind.Sequential)]
        public struct SherpaOnnxFeatureConfig { public int sample_rate; public int feature_dim; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SherpaOnnxOnlineTransducerModelConfig { public IntPtr encoder; public IntPtr decoder; public IntPtr joiner; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SherpaOnnxOnlineParaformerModelConfig { public IntPtr encoder; public IntPtr decoder; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SherpaOnnxOnlineZipformer2CtcModelConfig { public IntPtr model; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SherpaOnnxOnlineNemoCtcModelConfig { public IntPtr model; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SherpaOnnxOnlineToneCtcModelConfig { public IntPtr model; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SherpaOnnxOnlineModelConfig
        {
            public SherpaOnnxOnlineTransducerModelConfig transducer;
            public SherpaOnnxOnlineParaformerModelConfig paraformer;
            public SherpaOnnxOnlineZipformer2CtcModelConfig zipformer2_ctc;
            public IntPtr tokens;
            public int num_threads;
            public IntPtr provider;
            public int debug;
            public IntPtr model_type;
            public IntPtr modeling_unit;
            public IntPtr bpe_vocab;
            public IntPtr tokens_buf;
            public int tokens_buf_size;
            public SherpaOnnxOnlineNemoCtcModelConfig nemo_ctc;
            public SherpaOnnxOnlineToneCtcModelConfig t_one_ctc;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SherpaOnnxKeywordSpotterConfig
        {
            public SherpaOnnxFeatureConfig feat_config;
            public SherpaOnnxOnlineModelConfig model_config;
            public int max_active_paths;
            public int num_trailing_blanks;
            public float keywords_score;
            public float keywords_threshold;
            public IntPtr keywords_file;
            public IntPtr keywords_buf;
            public int keywords_buf_size;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SherpaOnnxKeywordResult
        {
            public IntPtr keyword;
            public IntPtr tokens;
            public IntPtr tokens_arr;
            public int count;
            public IntPtr timestamps;
            public float start_time;
            public IntPtr json;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SherpaOnnxCreateKeywordSpotter(ref SherpaOnnxKeywordSpotterConfig config);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SherpaOnnxDestroyKeywordSpotter(IntPtr spotter);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SherpaOnnxCreateKeywordStream(IntPtr spotter);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern int SherpaOnnxIsKeywordStreamReady(IntPtr spotter, IntPtr stream);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SherpaOnnxDecodeKeywordStream(IntPtr spotter, IntPtr stream);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SherpaOnnxGetKeywordResult(IntPtr spotter, IntPtr stream);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SherpaOnnxResetKeywordStream(IntPtr spotter, IntPtr stream);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SherpaOnnxDestroyKeywordResult(IntPtr result);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SherpaOnnxOnlineStreamAcceptWaveform(IntPtr stream, int sample_rate, float[] samples, int n);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SherpaOnnxOnlineStreamInputFinished(IntPtr stream);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SherpaOnnxDestroyOnlineStream(IntPtr stream);
    }

    // kernel32 LoadLibrary（显式加载 sherpa-onnx DLL）
    internal static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadLibrary(string lpFileName);
    }
}
