using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WheelSimuServer;

public partial class MainForm : Form
{
    // ==================== 配置 ====================
    const int LISTEN_PORT = 5050;
    const int DISCOVERY_PORT = 5051;
    const string DISCOVERY_MAGIC = "WHEELSIMU_SERVER";
    const int MAX_LOG_LINES = 1000;

    // ==================== 状态 ====================
    enum OutputMode { WinUHid = 0, WinUHidWheel = 1 }
    OutputMode _outputMode = OutputMode.WinUHidWheel;
    bool _uiReady;          // UI 初始化完成标志（防止 Load 前触发切换）
    bool xoneReady;
    bool wheelReady;
    readonly WinUHidDeviceManager xoneMgr = new();
    readonly WinUHidWheelDeviceManager wheelMgr = new();

    CancellationTokenSource? _cts;
    bool _isExiting;
    bool _restoringFromTray;    // 从托盘恢复窗口期间，跳过 Resize 自动隐藏

    // 统计
    int msgCount;
    int clientCount;
    DateTime lastDataLog = DateTime.MinValue;

    // ==================== UI 控件 ====================
    RichTextBox rtbLogs = null!;
    StatusStrip statusBar = null!;
    ToolStripStatusLabel lblIP = null!;
    ToolStripStatusLabel lblClient = null!;
    ToolStripStatusLabel lblMsgCount = null!;
    NotifyIcon trayIcon = null!;
    Label lblData = null!;  // 固定行显示实时数据
    ComboBox cmbOutput = null!; // 输出方式选择

    // ==================== 构造函数 ====================
    public MainForm(string[] args)
    {
        InitializeComponent();
        SetupTray();

        // --tray 参数：启动后自动隐藏
        if (args.Length > 0 && args[0] == "--tray")
        {
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
        }
    }

    void InitializeComponent()
    {
        Text = "WheelSimu Server v2";
        Size = new Size(650, 430);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = false;
        Icon = CreateAppIcon();

        // === 顶部标题栏 ===
        var pnlTop = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40,
            BackColor = Color.FromArgb(45, 45, 48)
        };
        var lblTitle = new Label
        {
            Text = "  WheelSimu Server v2 — 方向盘手机模拟器  ",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(12, 9)
        };
        pnlTop.Controls.Add(lblTitle);

        // === 输出方式选择（WinUHid） ===
        var lblOut = new Label
        {
            Text = "输出方式:",
            ForeColor = Color.White,
            AutoSize = true,
            Location = new Point(pnlTop.Width - 235, 11),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        cmbOutput = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 175,
            Location = new Point(pnlTop.Width - 165, 8),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        cmbOutput.Items.Add("WinUHid (Xbox One)");
        cmbOutput.Items.Add("WinUHid (方向盘)");
        cmbOutput.SelectedIndexChanged += CmbOutput_SelectedIndexChanged;
        pnlTop.Controls.Add(lblOut);
        pnlTop.Controls.Add(cmbOutput);

        Controls.Add(pnlTop);

        // === 日志区域 ===
        var pnlMain = new Panel { Dock = DockStyle.Fill };

        rtbLogs = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(200, 200, 200),
            Font = new Font("Consolas", 9f),
            BorderStyle = BorderStyle.None,
            WordWrap = true,
            DetectUrls = false,
            ShortcutsEnabled = true,   // 允许 Ctrl+C / Ctrl+A 复制日志
        };
        pnlMain.Controls.Add(rtbLogs);

        // 日志右键菜单：方便复制
        var logMenu = new ContextMenuStrip();
        logMenu.Items.Add("复制选中", null, (s, e) => rtbLogs.Copy());
        logMenu.Items.Add("复制全部", null, (s, e) =>
        {
            rtbLogs.SelectAll();
            rtbLogs.Copy();
            rtbLogs.DeselectAll();
        });
        logMenu.Items.Add(new ToolStripSeparator());
        logMenu.Items.Add("清空日志", null, (s, e) => rtbLogs.Clear());
        rtbLogs.ContextMenuStrip = logMenu;

        // === 底部固定数据行 ===
        lblData = new Label
        {
            Text = "等待客户端数据...",
            Dock = DockStyle.Bottom,
            BackColor = Color.FromArgb(40, 40, 40),
            ForeColor = Color.FromArgb(100, 200, 255),
            Font = new Font("Consolas", 10f, FontStyle.Bold),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            Height = 24,
        };
        pnlMain.Controls.Add(lblData);

        Controls.Add(pnlMain);

        // === 底部状态栏 ===
        statusBar = new StatusStrip
        {
            BackColor = Color.FromArgb(45, 45, 48),
            ForeColor = Color.FromArgb(200, 200, 200),
            SizingGrip = false
        };
        lblIP = new ToolStripStatusLabel { Text = "IP: ---", Padding = new Padding(6, 0, 12, 0) };
        lblClient = new ToolStripStatusLabel { Text = "客户端: 0", Padding = new Padding(0, 0, 12, 0) };
        lblMsgCount = new ToolStripStatusLabel { Text = "消息: 0" };
        statusBar.Items.Add(lblIP);
        statusBar.Items.Add(lblClient);
        statusBar.Items.Add(lblMsgCount);
        Controls.Add(statusBar);

        // 事件
        Load += MainForm_Load;
        FormClosing += MainForm_FormClosing;
        Resize += MainForm_Resize;
    }

    // ==================== 图标生成 ====================
    static Icon CreateAppIcon()
    {
        var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(Color.FromArgb(0, 120, 212));
        g.FillEllipse(brush, 2, 2, 28, 28);
        using var white = new SolidBrush(Color.White);
        g.FillEllipse(white, 10, 10, 12, 12);
        return Icon.FromHandle(bmp.GetHicon());
    }

    // ==================== 系统托盘 ====================
    void SetupTray()
    {
        var menu = new ContextMenuStrip();

        var titleItem = new ToolStripMenuItem("WheelSimu Server v2")
        {
            Font = new Font(menu.Font, FontStyle.Bold),
            Enabled = false
        };
        menu.Items.Add(titleItem);
        menu.Items.Add(new ToolStripSeparator());

        var showItem = new ToolStripMenuItem("显示窗口", null, (_, _) => ShowWindow());
        menu.Items.Add(showItem);

        var hideItem = new ToolStripMenuItem("隐藏到托盘", null, (_, _) => HideToTray());
        menu.Items.Add(hideItem);
        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("退出程序", null, (_, _) =>
        {
            _isExiting = true;
            trayIcon.Visible = false;
            Application.Exit();
        });
        menu.Items.Add(exitItem);

        trayIcon = new NotifyIcon
        {
            Icon = CreateAppIcon(),
            Text = "WheelSimu Server",
            ContextMenuStrip = menu,
            Visible = true
        };
        trayIcon.DoubleClick += (_, _) => ShowWindow();
    }

    void ShowWindow()
    {
        _restoringFromTray = true;
        try
        {
            Show();
            WindowState = FormWindowState.Normal;
            ShowInTaskbar = true;
            BringToFront();
            TopMost = true;
            TopMost = false;
            Activate();
        }
        finally
        {
            _restoringFromTray = false;
        }
    }

    void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    // ==================== 窗口事件 ====================
    void MainForm_Load(object? sender, EventArgs e)
    {
        Log("=================================");
        Log("  WheelSimu Server v2");
        Log("  方向盘手机模拟器 - PC 服务端");
        Log("=================================");
        Log("");

        // UI 初始化完成，输出方式默认 WinUHid 方向盘
        // 必须放在最前：保证窗口立即可用，不等待耗时的驱动初始化
        _uiReady = true;
        cmbOutput.SelectedIndex = (int)OutputMode.WinUHidWheel;

        string ip = GetLocalIP();
        Log($"本机 IP: {ip}");
        Log($"监听端口: {LISTEN_PORT}");
        UpdateStatusUI($"IP: {ip}:{LISTEN_PORT}");

        // 启动服务器（不依赖驱动初始化，立即启动）
        _cts = new CancellationTokenSource();
        _ = RunServer(_cts.Token);

        if (ShowInTaskbar)
        {
            Log("关闭窗口将最小化到托盘，右键托盘图标可退出");
        }
        Log("");

        // 耗时初始化（WinUHid 驱动检测与安装）放到后台线程。
        // EnsureReady 会跑 pnputil /add-driver，可能阻塞数秒，
        // 期间托盘双击/右键消息全部排队 -> 表现为"点了很久才有反应"。
        _ = Task.Run(InitOutputsInBackground);
    }

    // ==================== 后台驱动初始化（不阻塞 UI） ====================
    void InitOutputsInBackground()
    {
        // WinUHid 驱动检测 + 零部署自动安装
        try
        {
            if (WinUHidDeviceManager.DriverAvailable())
            {
                Log("WinUHid 驱动已就绪（可在顶部切换 Xbox One 手柄 / 方向盘输出）");
            }
            else
            {
                Log("WinUHid 驱动未安装，尝试自动安装…");
                var (state, detail) = WinUHidDriverInstaller.EnsureReady();
                switch (state)
                {
                    case WinUHidDriverInstaller.InstallState.Ready:
                    case WinUHidDriverInstaller.InstallState.Installed:
                        Log($"WinUHid: {detail}");
                        break;
                    case WinUHidDriverInstaller.InstallState.RebootRequired:
                        Log($"WinUHid: {detail}");
                        BeginInvoke(() => MessageBox.Show(
                            detail + "\n\n重启完成后请再次运行本程序。",
                            "需要重启电脑",
                            MessageBoxButtons.OK, MessageBoxIcon.Information));
                        break;
                    default:
                        Log($"WinUHid: {detail}");
                        BeginInvoke(() => MessageBox.Show(
                            "WinUHid 驱动自动安装失败：\n" + detail + "\n\n可尝试手动安装（见 README）。",
                            "驱动安装失败",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning));
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"WinUHid 检测异常: {ex.Message}");
        }
    }

    void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_isExiting)
        {
            // 关闭窗口 → 隐藏到托盘
            e.Cancel = true;
            HideToTray();
            return;
        }

        // 真正退出
        _cts?.Cancel();

        // 释放 WinUHid 虚拟设备
        try
        {
            xoneMgr.Dispose();
            Log("WinUHid Xbox One 手柄已释放");
        }
        catch (Exception ex)
        {
            Log($"释放 WinUHid Xbox One 异常: {ex.Message}");
        }
        try
        {
            wheelMgr.Dispose();
            Log("WinUHid 方向盘设备已释放");
        }
        catch (Exception ex)
        {
            Log($"释放 WinUHid 方向盘异常: {ex.Message}");
        }

        trayIcon.Visible = false;
        trayIcon.Dispose();
    }

    void MainForm_Resize(object? sender, EventArgs e)
    {
        if (_restoringFromTray) return;
        if (WindowState == FormWindowState.Minimized)
        {
            HideToTray();
        }
    }

    // ==================== 安全 UI 更新 ====================
    void Log(string msg)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(msg));
            return;
        }

        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        rtbLogs.AppendText(line + Environment.NewLine);

        // 限制行数
        if (rtbLogs.Lines.Length > MAX_LOG_LINES)
        {
            int excess = rtbLogs.Lines.Length - MAX_LOG_LINES;
            int pos = 0;
            for (int i = 0; i < excess; i++)
                pos = rtbLogs.Text.IndexOf('\n', pos) + 1;
            if (pos > 0) rtbLogs.Select(0, pos);
            rtbLogs.SelectedText = "";
        }

        rtbLogs.SelectionStart = rtbLogs.TextLength;
        rtbLogs.ScrollToCaret();
    }

    void UpdateStatusUI(string? trayText = null, string? ipText = null)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateStatusUI(trayText, ipText));
            return;
        }

        if (ipText != null) lblIP.Text = ipText;
        if (trayText != null) trayIcon.Text = "WheelSimu Server - " + trayText;

        lblClient.Text = $"客户端: {clientCount}";
        lblMsgCount.Text = $"消息: {msgCount}";
    }

    // ==================== UDP 广播发现 ====================
    async Task BroadcastDiscovery(CancellationToken ct)
    {
        string localIp = GetLocalIP();
        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        var endpoint = new IPEndPoint(IPAddress.Broadcast, DISCOVERY_PORT);
        var payload = $"{DISCOVERY_MAGIC}:{localIp}:{LISTEN_PORT}";
        var data = Encoding.UTF8.GetBytes(payload);

        while (!ct.IsCancellationRequested)
        {
            try { await udp.SendAsync(data, data.Length, endpoint); }
            catch { }
            try { await Task.Delay(2000, ct); } catch { break; }
        }
    }

    // ==================== TCP 服务器 ====================
    async Task RunServer(CancellationToken ct)
    {
        _ = BroadcastDiscovery(ct);

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Any, LISTEN_PORT);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Start();
            Log($"TCP 服务已启动 -> 0.0.0.0:{LISTEN_PORT} (等待手机连接...)");
            Log($"UDP 广播发现 -> 端口 {DISCOVERY_PORT}");

            while (!ct.IsCancellationRequested)
            {
                var acceptTask = listener.AcceptTcpClientAsync(ct);
                try { await acceptTask; }
                catch (OperationCanceledException) { break; }

                Interlocked.Increment(ref clientCount);
                UpdateStatusUI();
                _ = HandleClient(acceptTask.Result, ct);
            }
        }
        finally
        {
            listener?.Stop();
            Log("TCP 服务已停止");
        }
    }

    // ==================== 客户端处理 ====================
    async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        Log($"[连接] {remote}");

        try
        {
            client.NoDelay = true; // 禁用 Nagle，降低延迟
            using var stream = client.GetStream();
            stream.ReadTimeout = 3000;
            var buffer = new byte[512];
            var leftover = "";

            while (!ct.IsCancellationRequested && client.Connected)
            {
                int count;
                try
                {
                    count = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (count == 0) break;
                }
                catch (IOException) { break; }
                catch (OperationCanceledException) { break; }

                string raw = Encoding.UTF8.GetString(buffer, 0, count);
                leftover += raw;

                while (true)
                {
                    int atIdx = leftover.IndexOf('@');
                    if (atIdx < 0) break;

                    string msg = leftover.Substring(0, atIdx);
                    leftover = leftover.Substring(atIdx + 1);
                    ProcessMessage(msg);
                    // 客户端不处理 ACK，已移除（节省 100 次/秒无用的网络写入）
                }

                if (leftover.Length > 4096) leftover = "";
            }
        }
        catch (Exception ex)
        {
            Log($"[错误] {remote}: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref clientCount);
            UpdateStatusUI();
            Log($"[断开] {remote}");
        }
    }

    // ==================== 消息解析（手动单遍解析，零分配） ====================
    void ProcessMessage(string msg)
    {
        Interlocked.Increment(ref msgCount);

        double angle = 0;
        int throttle = 0, brake = 0, clutch = 0, handbrake = 0;
        int gearUp = 0, gearDown = 0;
        int gearMode = 0, autoDr = 0, gearValue = 0;   // M=档位模式, DR=自动挡D/R, GV=手动挡位

        int len = msg.Length, pos = 0;
        while (pos < len)
        {
            // 找 '='
            int eq = pos;
            while (eq < len && msg[eq] != '=') eq++;
            if (eq >= len) break;

            // 找值结束（',' 或字符串末尾）
            int valEnd = eq + 1;
            while (valEnd < len && msg[valEnd] != ',') valEnd++;

            // 根据首字符+长度匹配键名（避免 Substring 分配）
            int keyLen = eq - pos;
            switch (msg[pos])
            {
                case 'A': if (keyLen == 1) ParseDouble(msg, eq + 1, valEnd, out angle); break;
                case 'T': if (keyLen == 1) ParseInt(msg, eq + 1, valEnd, out throttle); break;
                case 'B': if (keyLen == 1) ParseInt(msg, eq + 1, valEnd, out brake); break;
                case 'C': if (keyLen == 1) ParseInt(msg, eq + 1, valEnd, out clutch); break;
                case 'H': if (keyLen == 1) ParseInt(msg, eq + 1, valEnd, out handbrake); break;
                case 'M': if (keyLen == 1) ParseInt(msg, eq + 1, valEnd, out gearMode); break;
                case 'D':
                    if (keyLen == 2 && msg[pos + 1] == 'R') ParseInt(msg, eq + 1, valEnd, out autoDr);
                    break;
                case 'G':
                    if (keyLen == 1) { /* G=xxx 跳过 */ }
                    else if (keyLen == 2)
                    {
                        char ch2 = msg[pos + 1];
                        if (ch2 == 'u') ParseInt(msg, eq + 1, valEnd, out gearUp);
                        else if (ch2 == 'd') ParseInt(msg, eq + 1, valEnd, out gearDown);
                        else if (ch2 == 'v') ParseInt(msg, eq + 1, valEnd, out gearValue);
                    }
                    break;
            }

            pos = valEnd + 1; // 跳过 ','
        }

        // 日志节流 + 状态栏更新 + 数据行刷新（每 ~1s）
        var now = DateTime.Now;
        if ((now - lastDataLog).TotalSeconds >= 1.0)
        {
            lastDataLog = now;
            string tag = _outputMode == OutputMode.WinUHidWheel ? "方向盘" : "Xbox";

            // 计算实际输出按钮位（与设备映射一致），供电脑端直观看到按钮消息
            uint btnBits = 0;
            if (gearUp > 0) btnBits |= 1u;           // Button1 升挡
            if (gearDown > 0) btnBits |= 2u;         // Button2 降挡
            if (handbrake > 0) btnBits |= 4u;        // Button3 手刹
            if (autoDr == 1) btnBits |= 8u;          // Button4 自动D
            else if (autoDr == -1) btnBits |= 16u;   // Button5 自动R
            if (gearMode == 3 || gearMode == 4)      // 手动挡
            {
                if (gearValue == -1) btnBits |= 32u;      // Button6 挡R
                else if (gearValue >= 1 && gearValue <= 12) btnBits |= 64u << (gearValue - 1); // Button7..18 挡1..12
            }
            string btnName = "";
            if ((btnBits & 1u) != 0) btnName += " 升挡";
            if ((btnBits & 2u) != 0) btnName += " 降挡";
            if ((btnBits & 4u) != 0) btnName += " 手刹";
            if ((btnBits & 8u) != 0) btnName += " D";
            if ((btnBits & 16u) != 0) btnName += " 倒R";
            if ((btnBits & 32u) != 0) btnName += " 挡R";
            for (int i = 0; i < 12; i++)
                if ((btnBits & (64u << i)) != 0) btnName += $" 挡{i + 1}";

            string dataLine = $"[{tag} #{msgCount}] A={angle:F1} T={throttle} B={brake} C={clutch} HB={handbrake} M={gearMode} DR={autoDr} GV={gearValue} BTN=0x{btnBits:X8}{btnName}";
            BeginInvoke(() => lblData.Text = dataLine);
            UpdateStatusUI();
        }

        // 按输出方式分流
        if (_outputMode == OutputMode.WinUHid)
        {
            if (xoneReady) xoneMgr.Report(angle, throttle, brake, clutch, handbrake, gearUp, gearDown, gearMode, autoDr, gearValue);
        }
        else if (_outputMode == OutputMode.WinUHidWheel)
        {
            if (wheelReady) wheelMgr.Report(angle, throttle, brake, clutch, handbrake, gearUp, gearDown, gearMode, autoDr, gearValue);
        }
    }

    // ==================== 输出方式切换 ====================
    void CmbOutput_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (!_uiReady || cmbOutput.SelectedIndex < 0) return;

        var mode = (OutputMode)cmbOutput.SelectedIndex;
        if (mode == _outputMode) return;

        try
        {
            // 先销毁当前模式占用的设备
            if (_outputMode == OutputMode.WinUHid)
            {
                xoneMgr.Destroy();
                xoneReady = false;
            }
            else if (_outputMode == OutputMode.WinUHidWheel)
            {
                wheelMgr.Destroy();
                wheelReady = false;
            }

            // 创建新模式需要的设备
            if (mode == OutputMode.WinUHid)
            {
                if (xoneMgr.Create(out var msg))
                {
                    Log(msg);
                    xoneReady = true;
                }
                else
                {
                    Log("切换失败: " + msg);
                    cmbOutput.SelectedIndex = (int)OutputMode.WinUHidWheel; // 回退（不再触发递归）
                    return;
                }
            }
            else
            {
                if (wheelMgr.Create(out var msg))
                {
                    Log(msg);
                    wheelReady = true;
                }
                else
                {
                    Log("切换失败: " + msg);
                    cmbOutput.SelectedIndex = (int)OutputMode.WinUHid; // 回退（不再触发递归）
                    return;
                }
            }

            _outputMode = mode;
            UpdateStatusUI($"{cmbOutput.Text} 模式");
        }
        catch (Exception ex)
        {
            Log($"切换输出方式异常: {ex.Message}");
        }
    }

    /// <summary>从字符串的子区间直接解析 int，不创建 Substring</summary>
    static void ParseInt(string s, int start, int end, out int result)
    {
        result = 0;
        bool neg = false;
        if (start < end && s[start] == '-') { neg = true; start++; }
        while (start < end) { result = result * 10 + (s[start++] - '0'); }
        if (neg) result = -result;
    }

    /// <summary>从字符串的子区间直接解析 double，不创建 Substring</summary>
    static void ParseDouble(string s, int start, int end, out double result)
    {
        result = 0;
        bool neg = false;
        if (start < end && s[start] == '-') { neg = true; start++; }
        // 整数部分
        while (start < end && s[start] != '.')
        {
            result = result * 10 + (s[start++] - '0');
        }
        // 小数部分
        if (start < end && s[start] == '.')
        {
            start++;
            double frac = 0, div = 0.1;
            while (start < end)
            {
                frac += (s[start++] - '0') * div;
                div *= 0.1;
            }
            result += frac;
        }
        if (neg) result = -result;
    }

    // ==================== 辅助 ====================
    static string GetLocalIP()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            var ip = host.AddressList
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork
                                  && !a.ToString().StartsWith("127."));
            return ip?.ToString() ?? "?.?.?.?";
        }
        catch { return "?.?.?.?"; }
    }
}
