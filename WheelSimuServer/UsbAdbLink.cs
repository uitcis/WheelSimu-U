using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WheelSimuServer;

/// <summary>
/// USB 有线链路：调用 adb 把 PC 的 5050 端口"反向"映射到手机本机端口
/// （<c>adb reverse tcp:5050 tcp:5050</c>）。
/// 手机端 App 连 <c>127.0.0.1:5050</c> 即可经 USB 数据线直达本机服务端，完全不依赖 WiFi。
///
/// 前置条件：手机开启「USB 调试」并在弹窗中授权本机；PC 上能找到 adb.exe。
/// 任一条件不满足都只是记录状态，绝不影响 TCP / UDP / 蓝牙 通道。
///
/// 说明：只在"USB 接入"的设备（序列号不含冒号）上建立转发。
/// 经 <c>adb connect</c> 接入的无线调试设备不走这里 —— 那种情况下走普通 TCP 即可。
/// </summary>
sealed class UsbAdbLink : IDisposable
{
    /// <summary>转发端口，与 TCP 服务端口保持一致</summary>
    public const int TunnelPort = 5050;

    /// <summary>轮询间隔：插线/授权后几秒内自动建立转发</summary>
    const int PollIntervalMs = 5000;

    /// <summary>找不到 adb 时的重试间隔（避免频繁翻磁盘）</summary>
    const int NoAdbRetryMs = 30000;

    /// <summary>设备一直在位时也定期重申转发，防止被其他工具清掉</summary>
    const int ReassertMs = 30000;

    /// <summary>单条 adb 命令超时。首次调用 adb 会启动守护进程，需要留足时间</summary>
    const int AdbTimeoutMs = 10000;

    CancellationTokenSource? _cts;

    /// <summary>状态栏显示用的一句话状态</summary>
    public string StatusText { get; private set; } = "未启动";

    /// <summary>转发是否已建立（手机端此刻应能连上 127.0.0.1:TunnelPort）</summary>
    public bool IsReady { get; private set; }

    /// <summary>当前承载转发的 USB 设备序列号</summary>
    public string? DeviceSerial { get; private set; }

    /// <summary>定位到的 adb.exe 完整路径；null = 没找到</summary>
    public string? AdbPath { get; private set; }

    /// <summary>每次状态文案真的变了才触发，避免轮询刷屏</summary>
    public event Action? StatusChanged;

    /// <summary>需要写进日志的事件（建立、断开、异常），不随轮询触发</summary>
    public event Action<string>? Log;

    string _lastAppliedSerial = "";   // 上次成功建立转发的设备
    long _lastApplyTicks;             // 上次成功建立转发的时间戳

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => MonitorLoop(_cts.Token));
    }

    // ==================== 状态变更（去重后通知） ====================
    void SetStatus(string text, bool ready, string? serial = null)
    {
        bool changed = text != StatusText || ready != IsReady;
        StatusText = text;
        IsReady = ready;
        DeviceSerial = ready ? serial : null;
        if (changed) StatusChanged?.Invoke();
    }

    // ==================== 监控循环 ====================
    async Task MonitorLoop(CancellationToken ct)
    {
        // 第一轮先定位 adb：找不到就降频重试，别每 5 秒翻一次磁盘
        while (!ct.IsCancellationRequested)
        {
            int delay = PollIntervalMs;
            try
            {
                AdbPath ??= FindAdb();
                if (AdbPath == null)
                {
                    SetStatus("未找到 adb.exe", false);
                    delay = NoAdbRetryMs;
                }
                else
                {
                    delay = await PollOnceAsync(ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                SetStatus("异常: " + ex.Message, false);
                delay = NoAdbRetryMs;
            }

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>做一轮探测，返回下一次轮询的间隔</summary>
    async Task<int> PollOnceAsync(CancellationToken ct)
    {
        var (code, output) = await RunAdbAsync("devices");
        if (code != 0 && string.IsNullOrWhiteSpace(output))
        {
            SetStatus("adb 调用失败", false);
            return NoAdbRetryMs;
        }

        string? usb = FirstUsbSerial(output);
        bool unauthorized = output.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
                            && usb == null;

        if (usb == null)
        {
            if (unauthorized)
            {
                if (StatusText != UnauthorizedStatus) Log?.Invoke("USB 设备未授权：请在手机弹窗中勾选「一律允许」并点允许");
                SetStatus(UnauthorizedStatus, false);
            }
            else
            {
                // 没有 USB 设备。若之前建过转发，说明线被拔了
                if (IsReady) Log?.Invoke("USB 设备已断开，转发失效（插回后会自动重建）");
                SetStatus("未检测到 USB 设备", false);
            }
            _lastAppliedSerial = "";
            return PollIntervalMs;
        }

        bool needApply = usb != _lastAppliedSerial
                         || !IsReady
                         || Environment.TickCount64 - _lastApplyTicks > ReassertMs;

        if (needApply && await EnsureReverseAsync(usb, ct))
        {
            bool wasReady = IsReady;
            SetStatus($"已转发 {usb}", true, usb);
            _lastAppliedSerial = usb;
            _lastApplyTicks = Environment.TickCount64;
            if (!wasReady)
                Log?.Invoke($"USB 链路就绪: {usb} -> 手机连 127.0.0.1:{TunnelPort} 即可");
        }

        return PollIntervalMs;
    }

    const string UnauthorizedStatus = "设备未授权（手机需点允许）";

    /// <summary>建立（或重申）端口转发。adb reverse 对同一端口重复执行是幂等的。</summary>
    async Task<bool> EnsureReverseAsync(string serial, CancellationToken ct)
    {
        var (code, output) = await RunAdbAsync($"-s {serial} reverse tcp:{TunnelPort} tcp:{TunnelPort}");
        if (ct.IsCancellationRequested) return false;

        if (code == 0 && !output.Contains("error", StringComparison.OrdinalIgnoreCase)
                      && !output.Contains("failed", StringComparison.OrdinalIgnoreCase))
            return true;

        string detail = output.Trim();
        if (detail.Length > 120) detail = detail[..120];
        SetStatus($"转发失败: {detail}", false);
        // 只报一次，避免每 5 秒刷一条
        if (_lastAppliedSerial != "!" + detail)
        {
            _lastAppliedSerial = "!" + detail;
            Log?.Invoke($"USB 转发失败 ({serial}): {detail}");
        }
        return false;
    }

    // ==================== 设备解析 ====================
    /// <summary>
    /// 从 `adb devices` 输出里挑出第一台 USB 接入且已授权的设备。
    /// 排除：经 TCP/IP 接入的（序列号含冒号，属于无线调试）、emulator-* 、状态非 device。
    /// </summary>
    static string? FirstUsbSerial(string adbDevicesOutput)
    {
        foreach (string rawLine in adbDevicesOutput.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                continue;

            string[] parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            string serial = parts[0];
            string state = parts[1];
            if (!state.Equals("device", StringComparison.OrdinalIgnoreCase)) continue;   // offline / unauthorized
            if (serial.Contains(':')) continue;                                          // 无线调试接入
            if (serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase)) continue;
            return serial;
        }
        return null;
    }

    // ==================== adb 调用 ====================
    async Task<(int Code, string Output)> RunAdbAsync(string arguments)
    {
        string adb = AdbPath ?? throw new InvalidOperationException("adb 路径未初始化");

        var psi = new ProcessStartInfo(adb, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start())
            return (-1, "无法启动 adb");

        // stdout / stderr 必须同时读，否则 adb 写满管道就会卡住
        Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> errTask = proc.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(AdbTimeoutMs);
        try
        {
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (-1, "adb 超时");
        }

        string stdout = await outTask;
        string stderr = await errTask;
        string all = stdout + stderr;

        // adb 启动守护进程的提示写在 stderr，不算错误；有真实设备行时以 stdout 为准
        int code = proc.ExitCode;
        if (code != 0 && all.Length == 0) all = $"exit={code}";
        return (code, all);
    }

    // ==================== adb 定位 ====================
    /// <summary>
    /// 依次尝试：环境变量 WHEELSIMU_ADB → 随程序分发的 platform-tools → 常见 Android SDK 位置 → PATH。
    /// </summary>
    static string? FindAdb()
    {
        foreach (string? candidate in Candidates())
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try { if (File.Exists(candidate)) return Path.GetFullPath(candidate); }
            catch { }
        }
        return null;
    }

    static System.Collections.Generic.IEnumerable<string?> Candidates()
    {
        // 1. 显式指定
        yield return Environment.GetEnvironmentVariable("WHEELSIMU_ADB");

        // 2. 随 EXE 一起分发（把 platform-tools 文件夹放在 exe 旁边即可零依赖）
        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "platform-tools", "adb.exe");
        yield return Path.Combine(baseDir, "adb.exe");

        // 3. Android SDK 的常见安装位置
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        yield return Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe");
        yield return Path.Combine(programFilesX86, "Android", "android-sdk", "platform-tools", "adb.exe");
        yield return Path.Combine(programFiles, "Android", "android-sdk", "platform-tools", "adb.exe");

        foreach (string drive in new[] { "C:", "D:", "E:", "F:", "G:" })
        {
            yield return $@"{drive}\Android\Sdk\platform-tools\adb.exe";
            yield return $@"{drive}\Android\platform-tools\adb.exe";
            yield return $@"{drive}\platform-tools\adb.exe";
            yield return $@"{drive}\Program Files\Android\Sdk\platform-tools\adb.exe";
        }

        // 4. PATH 里找
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                // 注意：迭代器方法里不能在带 catch 的 try 中 yield（CS1626），
                // 所以先算好候选路径再 yield。
                string candidate;
                try { candidate = Path.Combine(dir.Trim(), "adb.exe"); }
                catch { continue; }
                yield return candidate;
            }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        // 故意不执行 adb reverse --remove：留着映射后，服务端重启时手机能立刻重连，
        // 且映射本身不会占用端口资源。
    }
}
