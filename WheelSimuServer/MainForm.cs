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
    const int BASE_PORT = 25050;
    const int PORT_PROBE_COUNT = 10;
    /// <summary>实际监听端口：启动时从 BASE_PORT 起探测，被占用则自动后移</summary>
    int ListenPort = BASE_PORT;
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
    BluetoothSppServer? _btServer;   // 蓝牙 SPP (RFCOMM) 服务端
    UsbAdbLink? _usbLink;            // USB 有线链路（adb 端口转发）
    string _ipText = "";             // 状态栏左半段（局域网信息）
    string _btStatusText = "未启动";  // 状态栏中段（蓝牙状态）
    string _usbStatusText = "未启动"; // 状态栏右段（USB 状态）
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

    // 虚拟键位监视器
    readonly Dictionary<int, Button> _keyBitToBtn = new();
    DateTime _lastKeyRefresh = DateTime.MinValue;

    // 方向盘角度指示器
    double _currentAngle;  // 当前角度（度）
    PictureBox picWheel = null!;  // 角度指示器画布
    DateTime _lastAngleRefresh = DateTime.MinValue;

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
        Size = new Size(650, 720);
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

        // 虚拟键位监视器（图形化按键显示，含角度指示器）
        BuildKeyMonitor();

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

        // 消息栏
        lblData = new Label
        {
            Text = "等待客户端数据...",
            Dock = DockStyle.Bottom,
            BackColor = Color.FromArgb(40, 40, 40),
            ForeColor = Color.FromArgb(100, 200, 255),
            Font = new Font("Consolas", 9f, FontStyle.Bold),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            Height = 20,
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
        ResolvePort();
        Log($"本机 IP: {ip}");
        Log($"监听端口: {ListenPort}");
        UpdateStatusUI($"IP: {ip}:{ListenPort}");

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
            bool driverReady = WinUHidDeviceManager.DriverAvailable();
            if (!driverReady)
            {
                Log("WinUHid 驱动未安装，尝试自动安装…");
                var (state, detail) = WinUHidDriverInstaller.EnsureReady();
                switch (state)
                {
                    case WinUHidDriverInstaller.InstallState.Ready:
                    case WinUHidDriverInstaller.InstallState.Installed:
                        Log($"WinUHid: {detail}");
                        driverReady = true;
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
            else
            {
                Log("WinUHid 驱动已就绪（可在顶部切换 Xbox One 手柄 / 方向盘输出）");
            }

            // 驱动就绪后，自动创建默认的虚拟方向盘设备
            if (driverReady)
            {
                BeginInvoke(() => CreateDefaultDevice());
            }
        }
        catch (Exception ex)
        {
            Log($"WinUHid 检测异常: {ex.Message}");
        }
    }

    /// <summary>创建默认的虚拟设备（方向盘模式）</summary>
    void CreateDefaultDevice()
    {
        if (wheelMgr.Create(out var msg))
        {
            Log(msg);
            wheelReady = true;
        }
        else
        {
            Log("虚拟方向盘创建失败: " + msg);
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

        // 停止蓝牙广播
        try
        {
            _btServer?.Dispose();
            _btServer = null;
            Log("蓝牙 SPP 服务已停止");
        }
        catch (Exception ex)
        {
            Log($"停止蓝牙服务异常: {ex.Message}");
        }

        // 停止 USB 链路监控（已建立的转发保留，服务端重启后手机可立即重连）
        try
        {
            _usbLink?.Dispose();
            _usbLink = null;
        }
        catch (Exception ex)
        {
            Log($"停止 USB 链路异常: {ex.Message}");
        }

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

        if (ipText != null) _ipText = ipText;
        string btPart = $"蓝牙: {_btStatusText}";
        string usbPart = $"USB: {_usbStatusText}";
        lblIP.Text = string.IsNullOrEmpty(_ipText)
            ? $"{btPart} | {usbPart}"
            : $"{_ipText} | {btPart} | {usbPart}";
        if (trayText != null) trayIcon.Text = "WheelSimu Server - " + trayText;

        lblClient.Text = $"客户端: {clientCount}";
        lblMsgCount.Text = $"消息: {msgCount}";

        // 蓝色状态行：只显示连接状态（已连接/未连接），不显示任何具体数据
        lblData.Text = clientCount > 0 ? "已连接" : "未连接";
    }

    // ==================== UDP 广播发现 ====================
    async Task BroadcastDiscovery(CancellationToken ct)
    {
        string localIp = GetLocalIP();
        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        var endpoint = new IPEndPoint(IPAddress.Broadcast, DISCOVERY_PORT);
        var payload = $"{DISCOVERY_MAGIC}:{localIp}:{ListenPort}";
        var data = Encoding.UTF8.GetBytes(payload);

        while (!ct.IsCancellationRequested)
        {
            try { await udp.SendAsync(data, data.Length, endpoint); }
            catch { }
            try { await Task.Delay(2000, ct); } catch { break; }
        }
    }

    // ==================== 蓝牙 SPP (RFCOMM) 服务器 ====================
    /// <summary>
    /// 启动蓝牙串口服务。手机需先与本机配对，再由手机端主动连接；
    /// 无蓝牙适配器或系统拒绝时不致命，仅记录一行日志，TCP 通道照常工作。
    /// </summary>
    async Task StartBluetoothServer(CancellationToken ct)
    {
        var bt = new BluetoothSppServer();
        bt.ClientConnected += conn =>
        {
            Interlocked.Increment(ref clientCount);
            UpdateStatusUI();
            _ = HandleBluetoothClient(conn, ct);
        };

        if (await bt.StartAsync())
        {
            _btServer = bt;
            _btStatusText = bt.StatusText;
            Log($"蓝牙 SPP 服务已启动: {BluetoothSppServer.ServiceDisplayName} (手机配对后即可连接)");
        }
        else
        {
            _btStatusText = bt.StatusText;
            Log($"蓝牙未启用: {bt.StatusText}（不影响 TCP/UDP 模式）");
            bt.Dispose();
        }

        UpdateStatusUI();
    }

    // ==================== USB 有线链路（adb 端口转发） ====================
    /// <summary>
    /// 启动 USB 链路监控：找到 adb 且手机已插线授权时，自动执行
    /// <c>adb reverse tcp:25050 tcp:25050</c>，手机端选 USB 模式连 127.0.0.1:25050 即可。
    /// 没 adb / 没插线 / 没授权都只记状态，不影响 TCP/UDP/蓝牙。
    /// </summary>
    void StartUsbLink(CancellationToken ct)
    {
        var usb = new UsbAdbLink();
        usb.StatusChanged += () =>
        {
            _usbStatusText = usb.StatusText;
            UpdateStatusUI();
        };
        usb.Log += msg => Log(msg);
        usb.Start();
        _usbLink = usb;

        _usbStatusText = usb.StatusText;
        UpdateStatusUI();
        Log("USB 链路监控已启动（手机开 USB 调试并插线后自动建立转发）");
    }

    // ==================== TCP 服务器 ====================
    async Task RunServer(CancellationToken ct)
    {
        _ = BroadcastDiscovery(ct);
        _ = StartBluetoothServer(ct);
        UsbAdbLink.PcTunnelPort = ListenPort; // 端口占用回落时，adb reverse 的 PC 侧跟随实际端口
        StartUsbLink(ct);

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Any, ListenPort);
            listener.Start();
            Log($"TCP 服务已启动 -> 0.0.0.0:{ListenPort} (等待手机连接...)");
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
            await ProcessStream(stream, remote, ct);
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

    async Task HandleBluetoothClient(BluetoothSppConnection conn, CancellationToken ct)
    {
        var remote = "BT " + conn.RemoteName;
        Log($"[蓝牙连接] {remote}");

        try
        {
            await ProcessStream(conn.ReadStream, remote, ct);
        }
        catch (Exception ex)
        {
            Log($"[蓝牙错误] {remote}: {ex.Message}");
        }
        finally
        {
            conn.Dispose();
            Interlocked.Decrement(ref clientCount);
            UpdateStatusUI();
            Log($"[蓝牙断开] {remote}");
        }
    }

    /// <summary>通用接收循环：TCP 与蓝牙共用，按 '@' 切帧后交给 ProcessMessage</summary>
    async Task ProcessStream(Stream stream, string remote, CancellationToken ct)
    {
        var buffer = new byte[512];
        var leftover = "";

        while (!ct.IsCancellationRequested)
        {
            int count;
            try
            {
                count = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (count == 0) break;
            }
            catch (IOException) { break; }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }

            leftover += Encoding.UTF8.GetString(buffer, 0, count);

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
                        if (ch2 == 'u' || ch2 == 'U') ParseInt(msg, eq + 1, valEnd, out gearUp);
                        else if (ch2 == 'd' || ch2 == 'D') ParseInt(msg, eq + 1, valEnd, out gearDown);
                        else if (ch2 == 'v' || ch2 == 'V') ParseInt(msg, eq + 1, valEnd, out gearValue);
                    }
                    break;
            }

            pos = valEnd + 1; // 跳过 ','
        }

        // 日志节流 + 状态栏刷新（每 ~1s）
        var now = DateTime.Now;
        if ((now - lastDataLog).TotalSeconds >= 1.0)
        {
            lastDataLog = now;
            UpdateStatusUI();
        }

        // 图形化键位监视器刷新（约 30ms 一次）
        if ((now - _lastKeyRefresh).TotalMilliseconds >= 30)
        {
            _lastKeyRefresh = now;
            uint liveBits = ComputeBtnBits(gearUp, gearDown, handbrake, autoDr, gearMode, gearValue);
            BeginInvoke(() => RefreshKeyPanel(liveBits));
        }

        // 方向盘角度指示器刷新（约 50ms 一次）
        _currentAngle = angle;
        if ((now - _lastAngleRefresh).TotalMilliseconds >= 50)
        {
            _lastAngleRefresh = now;
            BeginInvoke(() => RefreshAngleIndicator());
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

    // ==================== 虚拟键位监视器 + 角度指示器 ====================
    void BuildKeyMonitor()
    {
        // 使用 TableLayoutPanel 实现左右布局
        var tableLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 150,
            BackColor = Color.FromArgb(37, 37, 38),
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(5, 5, 5, 5)
        };
        tableLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));  // 左60%：按键区
        tableLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));  // 右40%：角度指示器

        // 左侧：按键区域
        var flp = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.FromArgb(37, 37, 38),
            Dock = DockStyle.Fill
        };

        void AddGroup(string title, params (int bit, string text)[] keys)
        {
            flp.Controls.Add(new Label
            {
                Text = title,
                ForeColor = Color.FromArgb(150, 150, 155),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 3, 0, 2)
            });
            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = true,
                BackColor = Color.FromArgb(37, 37, 38),
                Margin = new Padding(0, 0, 0, 2)
            };
            foreach (var k in keys)
            {
                var b = new Button
                {
                    Text = k.text,
                    Size = new Size(50, 26),
                    Margin = new Padding(2),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(58, 58, 62),
                    ForeColor = Color.Gray,
                    Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                    TabStop = false,
                    Tag = k.bit
                };
                b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 95);
                b.FlatAppearance.BorderSize = 1;
                _keyBitToBtn[k.bit] = b;
                row.Controls.Add(b);
            }
            flp.Controls.Add(row);
        }

        AddGroup("控制键",
            (1, "升挡"), (2, "降挡"), (4, "手刹"), (8, "自动D"), (16, "自动R"), (32, "挡R"));
        AddGroup("手动挡 1-6",
            (64, "1挡"), (128, "2挡"), (256, "3挡"), (512, "4挡"), (1024, "5挡"), (2048, "6挡"));

        tableLayout.Controls.Add(flp, 0, 0);

        // 右侧：圆形角度指示器
        var pnlAngle = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(37, 37, 38)
        };

        picWheel = new PictureBox
        {
            Size = new Size(120, 120),
            BackColor = Color.FromArgb(45, 45, 48),
            SizeMode = PictureBoxSizeMode.Normal
        };
        pnlAngle.Controls.Add(picWheel);
        picWheel.Paint += (s, e) => DrawWheelIndicator(e.Graphics, picWheel.Width, picWheel.Height);
        pnlAngle.Resize += (s, e) => PositionAnglePanel(pnlAngle);

        tableLayout.Controls.Add(pnlAngle, 1, 0);

        Controls.Add(tableLayout);
    }

    void PositionAnglePanel(Panel parent)
    {
        if (picWheel == null || picWheel.IsDisposed) return;
        picWheel.Left = (parent.ClientSize.Width - picWheel.Width) / 2;
        picWheel.Top = (parent.ClientSize.Height - picWheel.Height) / 2;
    }

    void PositionControls(Panel parent)
    {
        if (picWheel == null || picWheel.IsDisposed) return;

        // picWheel 居中
        picWheel.Left = (parent.ClientSize.Width - picWheel.Width) / 2;
        picWheel.Top = (parent.ClientSize.Height - picWheel.Height) / 2;
    }

    // ==================== 方向盘角度指示器 ====================
    void DrawWheelIndicator(Graphics g, int w, int h)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        int cx = w / 2, cy = h / 2;
        int radius = Math.Min(w, h) / 2 - 5;

        // 外圈（刻度盘背景）
        using (var bgBrush = new SolidBrush(Color.FromArgb(50, 50, 55)))
        {
            g.FillEllipse(bgBrush, cx - radius, cy - radius, radius * 2, radius * 2);
        }

        // 刻度线（每 45 度一个主刻度，每 15 度一个副刻度）
        using (var tickPen = new Pen(Color.FromArgb(100, 100, 110), 1))
        using (var majorTickPen = new Pen(Color.FromArgb(180, 180, 190), 2))
        {
            for (int deg = -180; deg <= 180; deg += 15)
            {
                bool isMajor = deg % 45 == 0;
                var pen = isMajor ? majorTickPen : tickPen;
                double rad = deg * Math.PI / 180.0;
                float innerR = radius - (isMajor ? 12 : 8);
                float outerR = radius - 3;
                float x1 = (float)(cx + Math.Sin(rad) * innerR);
                float y1 = (float)(cy - Math.Cos(rad) * innerR);
                float x2 = (float)(cx + Math.Sin(rad) * outerR);
                float y2 = (float)(cy - Math.Cos(rad) * outerR);
                g.DrawLine(pen, x1, y1, x2, y2);

                // 主刻度数字
                if (isMajor && deg != 0)
                {
                    float textR = radius - 22;
                    float tx = (float)(cx + Math.Sin(rad) * textR);
                    float ty = (float)(cy - Math.Cos(rad) * textR);
                    using var font = new Font("Segoe UI", 7f);
                    using var brush = new SolidBrush(Color.FromArgb(150, 150, 160));
                    var size = g.MeasureString(Math.Abs(deg).ToString(), font);
                    g.DrawString(Math.Abs(deg).ToString(), font, brush, tx - size.Width / 2, ty - size.Height / 2);
                }
            }
        }

        // 中心点
        using (var centerBrush = new SolidBrush(Color.FromArgb(60, 60, 65)))
        {
            g.FillEllipse(centerBrush, cx - 6, cy - 6, 12, 12);
        }

        // 方向盘指针（根据当前角度旋转）
        double angleRad = _currentAngle * Math.PI / 180.0;
        float pointerLen = radius - 25;
        float px = (float)(cx + Math.Sin(angleRad) * pointerLen);
        float py = (float)(cy - Math.Cos(angleRad) * pointerLen);

        // 指针颜色：居中附近偏绿，极端位置偏红
        double absAngle = Math.Abs(_currentAngle);
        byte r = (byte)Math.Min(255, absAngle * 2);
        byte g2 = (byte)Math.Max(50, 200 - absAngle * 1.5);

        using (var pointerPen = new Pen(Color.FromArgb(r, g2, 50), 4))
        {
            pointerPen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
            pointerPen.EndCap = System.Drawing.Drawing2D.LineCap.Triangle;
            g.DrawLine(pointerPen, cx, cy, px, py);
        }

        // 指针端点圆点
        using (var tipBrush = new SolidBrush(Color.FromArgb(r, g2, 50)))
        {
            g.FillEllipse(tipBrush, px - 5, py - 5, 10, 10);
        }

        // 更新角度数字
        var lblAngle = picWheel.Tag as Label;
        if (lblAngle != null)
        {
            string angleText = $"{_currentAngle:F0}°";
            if (lblAngle.Text != angleText)
            {
                lblAngle.Text = angleText;
                // 颜色同步指针
                lblAngle.ForeColor = Color.FromArgb(r, g2, 50);
            }
        }
    }

    void RefreshAngleIndicator()
    {
        if (picWheel != null && !picWheel.IsDisposed)
        {
            picWheel.Invalidate();
        }
    }

    void RefreshKeyPanel(uint bits)
    {
        foreach (var kv in _keyBitToBtn)
        {
            bool on = (bits & kv.Key) != 0;
            kv.Value.BackColor = on ? Color.FromArgb(255, 82, 82) : Color.FromArgb(58, 58, 62);
            kv.Value.ForeColor = on ? Color.White : Color.Gray;
            kv.Value.FlatAppearance.BorderColor = on ? Color.FromArgb(255, 138, 138) : Color.FromArgb(90, 90, 95);
        }
    }

    static uint ComputeBtnBits(int gearUp, int gearDown, int handbrake, int autoDr, int gearMode, int gearValue)
    {
        uint btnBits = 0;
        if (gearUp > 0) btnBits |= 1u;
        if (gearDown > 0) btnBits |= 2u;
        if (handbrake > 0) btnBits |= 4u;
        if (autoDr == 1) btnBits |= 8u;
        else if (autoDr == -1) btnBits |= 16u;
        if (gearMode == 3 || gearMode == 4)
        {
            if (gearValue == -1) btnBits |= 32u;
            else if (gearValue >= 1 && gearValue <= 6) btnBits |= 64u << (gearValue - 1);
        }
        return btnBits;
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
    /// <summary>
    /// 从 BASE_PORT 起探测第一个可用端口（被占用则依次后移，最多试 PORT_PROBE_COUNT 个）。
    /// 注意：探测绑定不能带 ReuseAddress——Windows 上它会掩盖端口真实占用，导致检测失效。
    /// </summary>
    void ResolvePort()
    {
        for (int p = BASE_PORT; p < BASE_PORT + PORT_PROBE_COUNT; p++)
        {
            TcpListener probe;
            try
            {
                probe = new TcpListener(IPAddress.Any, p);
                probe.Start();
            }
            catch (SocketException)
            {
                continue; // 端口被占用，试下一个
            }
            probe.Stop();
            if (p != BASE_PORT)
                Log($"端口 {BASE_PORT} 被占用，自动改用 {p}（发现广播会告知手机新端口）");
            ListenPort = p;
            return;
        }
        Log($"警告：{BASE_PORT}~{BASE_PORT + PORT_PROBE_COUNT - 1} 全被占用，仍尝试 {BASE_PORT}");
        ListenPort = BASE_PORT;
    }

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
