using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Clawbrower.Services;

public enum McpStatus { Stopped, Starting, Running, Error }

public class McpService : IDisposable
{
    private Process? _mcpProcess;
    private Process? _frpcProcess;
    private volatile McpStatus _status = McpStatus.Stopped;
    private string? _lastError;

    // Job Object：将子进程绑定到 Job，父进程退出/崩溃时 OS 自动终止子进程
    private IntPtr _jobHandle = IntPtr.Zero;

    // ── Job Object P/Invoke ──
    private const int JobObjectBasicLimitInformation = 2;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoType,
        ref JOBOBJECT_BASIC_LIMIT_INFORMATION info, uint cbInfo);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    public McpStatus Status => _status;
    public string? LastError => _lastError;

    /// <summary>
    /// 状态变更（Starting / Running / Stopped / Error）
    /// </summary>
    public event Action<McpStatus>? OnStatusChanged;

    /// <summary>
    /// 详细操作消息（启动/关闭具体服务的提示）
    /// </summary>
    public event Action<string>? OnMessage;

    private void SetStatus(McpStatus s)
    {
        _status = s;
        OnStatusChanged?.Invoke(s);
    }

    private void SendMessage(string msg)
    {
        OnMessage?.Invoke(msg);
    }

    /// <summary>
    /// 获取 mcp 目录路径（输出目录下的 mcp/）
    /// </summary>
    public static string GetMcpDir()
    {
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mcp");
        if (!Directory.Exists(dir))
            dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "mcp");
        return Path.GetFullPath(dir);
    }

    public static string GetWindowsMcpExe() => Path.Combine(GetMcpDir(), "windows-mcp.exe");
    public static string GetFrpcExe() => Path.Combine(GetMcpDir(), "frpc.exe");

    /// <summary>
    /// 将 frpc.exe 添加到 Windows Defender 排除项（需要管理员权限）
    /// </summary>
    public static bool AddDefenderExclusion()
    {
        var frpcPath = GetFrpcExe();
        if (!File.Exists(frpcPath))
        {
            Logger.Error($"AddDefenderExclusion: frpc.exe not found at {frpcPath}");
            return false;
        }

        try
        {
            var checkPsi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"(Get-MpPreference).ExclusionPath\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var checkProc = Process.Start(checkPsi);
            if (checkProc == null) return false;
            var existing = checkProc.StandardOutput.ReadToEnd();
            checkProc.WaitForExit();

            if (existing.Contains(frpcPath, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info("Defender exclusion already exists for frpc.exe");
                return true;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -Command Add-MpPreference -ExclusionPath \\\"{frpcPath}\\\"'\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
            Logger.Info($"Defender exclusion added for {frpcPath}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"AddDefenderExclusion failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 生成 frpc.toml 到临时目录
    /// </summary>
    private static string GenerateFrpcToml(McpConfig cfg)
    {
        var dir = Path.Combine(Path.GetTempPath(), "clawbrower-mcp");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "frpc.toml");

        var sb = new StringBuilder();
        sb.AppendLine($"serverAddr = \"{cfg.FrpServerAddr}\"");
        sb.AppendLine($"serverPort = {cfg.FrpServerPort}");
        sb.AppendLine($"auth.token = \"{cfg.FrpAuthToken}\"");
        sb.AppendLine();
        sb.AppendLine("[[proxies]]");
        var proxyName = string.IsNullOrWhiteSpace(cfg.DeviceName) ? "mcp" : cfg.DeviceName;
        sb.AppendLine($"name = \"{proxyName}\"");
        sb.AppendLine("type = \"tcp\"");
        sb.AppendLine("localIP = \"127.0.0.1\"");
        sb.AppendLine($"localPort = {cfg.LocalPort}");
        sb.AppendLine($"remotePort = {cfg.RemotePort}");

        File.WriteAllText(path, sb.ToString());
        return path;
    }

    /// <summary>
    /// 清理可能残留的孤儿进程（上次崩溃时未正常退出的 windows-mcp.exe / frpc.exe）。
    /// </summary>
    public static void CleanupOrphanedProcesses()
    {
        foreach (var name in new[] { "windows-mcp", "frpc" })
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    Logger.Info($"Killing orphaned process: {proc.ProcessName} (PID {proc.Id})");
                    proc.Kill(true);
                    proc.WaitForExit(3000);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to kill orphaned {proc.ProcessName}: {ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// 创建 Job Object（KILL_ON_JOB_CLOSE），确保子进程随父进程退出而终止。
    /// </summary>
    private void EnsureJobObject()
    {
        if (_jobHandle != IntPtr.Zero) return;
        _jobHandle = CreateJobObject(IntPtr.Zero, null);
        if (_jobHandle == IntPtr.Zero)
        {
            Logger.Error("CreateJobObject failed");
            return;
        }
        var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        };
        SetInformationJobObject(_jobHandle, JobObjectBasicLimitInformation,
            ref info, (uint)Marshal.SizeOf<JOBOBJECT_BASIC_LIMIT_INFORMATION>());
    }

    /// <summary>
    /// 将进程加入 Job Object，使其随父进程退出而自动终止。
    /// </summary>
    private void AssignToJob(Process proc)
    {
        if (_jobHandle == IntPtr.Zero) return;
        try
        {
            AssignProcessToJobObject(_jobHandle, proc.Handle);
        }
        catch (Exception ex)
        {
            Logger.Error($"AssignProcessToJobObject failed for {proc.ProcessName}: {ex.Message}");
        }
    }

    public async Task StartAsync(McpConfig cfg)
    {
        if (_status == McpStatus.Running || _status == McpStatus.Starting) return;

        SetStatus(McpStatus.Starting);
        _lastError = null;

        try
        {
            // 清理上次崩溃可能残留的孤儿进程（端口占用）
            CleanupOrphanedProcesses();

            // 创建 Job Object，子进程崩溃/父进程退出时自动终止
            EnsureJobObject();

            var mcpExe = GetWindowsMcpExe();
            var frpcExe = GetFrpcExe();

            if (!File.Exists(mcpExe))
            {
                _lastError = $"windows-mcp.exe 不存在: {mcpExe}";
                Logger.Error(_lastError);
                SetStatus(McpStatus.Error);
                return;
            }

            // 1. 添加 Defender 排除项
            AddDefenderExclusion();

            // 2. 启动 windows-mcp
            SendMessage("正在启动 windows-mcp...");
            var mcpArgs = $"serve --transport sse --host 0.0.0.0 --port {cfg.LocalPort} --allow-insecure-remote";
            Logger.Info($"Starting windows-mcp: {mcpExe} {mcpArgs}");

            _mcpProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = mcpExe,
                    Arguments = mcpArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }
            };
            _mcpProcess.OutputDataReceived += (_, e) => { if (e.Data != null) Logger.Info($"[MCP] {e.Data}"); };
            _mcpProcess.ErrorDataReceived += (_, e) => { if (e.Data != null) Logger.Error($"[MCP] {e.Data}"); };
            _mcpProcess.Start();
            _mcpProcess.BeginOutputReadLine();
            _mcpProcess.BeginErrorReadLine();
            AssignToJob(_mcpProcess);

            // 3. 等待 MCP 端口就绪
            await Task.Delay(3000);
            SendMessage("windows-mcp 已启动");

            // 4. 启动 frpc（如果存在）
            if (File.Exists(frpcExe))
            {
                var tomlPath = GenerateFrpcToml(cfg);
                Logger.Info($"Starting frpc: {frpcExe} -c {tomlPath}");
                SendMessage("正在启动 frpc 隧道...");

                _frpcProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = frpcExe,
                        Arguments = $"-c \"{tomlPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    }
                };
                _frpcProcess.OutputDataReceived += (_, e) => { if (e.Data != null) Logger.Info($"[FRPC] {e.Data}"); };
                _frpcProcess.ErrorDataReceived += (_, e) => { if (e.Data != null) Logger.Error($"[FRPC] {e.Data}"); };
                _frpcProcess.Start();
                _frpcProcess.BeginOutputReadLine();
                _frpcProcess.BeginErrorReadLine();
                AssignToJob(_frpcProcess);
                SendMessage("frpc 隧道已启动");
            }
            else
            {
                Logger.Info($"frpc.exe not found at {frpcExe}, skipping tunnel");
            }

            SetStatus(McpStatus.Running);
            Logger.Info("MCP service started successfully");

            // 后台监控进程存活
            _ = MonitorProcessesAsync();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Logger.Error($"McpService start failed: {ex.Message}");
            SetStatus(McpStatus.Error);
        }
    }

    public void Stop()
    {
        // 防止重复调用（MonitorProcessesAsync 竞态）
        if (_status != McpStatus.Running && _status != McpStatus.Starting && _status != McpStatus.Error)
            return;

        // 立即标记状态，阻止 MonitorProcessesAsync 再次进入 Stop
        _status = McpStatus.Stopped;

        bool stoppedFrpc = false;
        bool stoppedMcp = false;

        try
        {
            if (_frpcProcess != null && !_frpcProcess.HasExited)
            {
                SendMessage("正在关闭 frpc 隧道...");
                _frpcProcess.Kill(true);
                stoppedFrpc = true;
                Logger.Info("frpc stopped");
            }
            _frpcProcess?.Dispose();
            _frpcProcess = null;
        }
        catch (Exception ex) { Logger.Error($"Stop frpc error: {ex.Message}"); }

        try
        {
            if (_mcpProcess != null && !_mcpProcess.HasExited)
            {
                SendMessage("正在关闭 windows-mcp...");
                _mcpProcess.Kill(true);
                stoppedMcp = true;
                Logger.Info("windows-mcp stopped");
            }
            _mcpProcess?.Dispose();
            _mcpProcess = null;
        }
        catch (Exception ex) { Logger.Error($"Stop mcp error: {ex.Message}"); }

        // 构建关闭结果消息
        var parts = new List<string>();
        if (stoppedMcp) parts.Add("windows-mcp 已关闭");
        if (stoppedFrpc) parts.Add("frpc 已关闭");
        if (parts.Count > 0)
            SendMessage(string.Join("，", parts));
        else
            SendMessage("MCP 远程控制已关闭");

        // 通知外部状态变更
        OnStatusChanged?.Invoke(McpStatus.Stopped);

        // 关闭 Job Object 句柄（子进程已被 Kill，这里仅释放资源）
        if (_jobHandle != IntPtr.Zero)
        {
            CloseHandle(_jobHandle);
            _jobHandle = IntPtr.Zero;
        }
    }

    private async Task MonitorProcessesAsync()
    {
        while (_status == McpStatus.Running)
        {
            await Task.Delay(5000);

            if (_status != McpStatus.Running) return;

            var mcpDead = _mcpProcess == null || _mcpProcess.HasExited;
            var frpcDead = _frpcProcess == null || _frpcProcess.HasExited;

            if (mcpDead && frpcDead)
            {
                _lastError = "windows-mcp 和 frpc 均已意外退出";
                Logger.Error(_lastError);
                Stop();
                return;
            }

            if (mcpDead)
            {
                _lastError = "windows-mcp 进程意外退出";
                Logger.Error(_lastError);
                Stop();
                return;
            }

            // frpc 挂了只记录，不重启
            if (frpcDead && _frpcProcess != null)
            {
                SendMessage("frpc 隧道已意外断开（windows-mcp 仍在运行）");
                Logger.Error("frpc process exited unexpectedly (MCP still running)");
                _frpcProcess.Dispose();
                _frpcProcess = null;
            }
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
