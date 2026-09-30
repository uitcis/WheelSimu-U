using System.Runtime.InteropServices;

namespace WheelSimuServer;

/// <summary>
/// WinUHid (lurebat/WinUHid) Xbox One 虚拟手柄输出。
/// 通过 P/Invoke 调用 WinUHidDevs.dll 的预设 Xbox One 手柄接口，
/// 驱动层为 WinUHidDriver.sys（UMDF + VHF），游戏会识别为 XInput 手柄。
/// </summary>
public sealed class WinUHidDeviceManager : IDisposable
{
    // ============ 原生结构（WinUHidXOne.h，pack=1，共 17 字节） ============
    // 注意：两个 10bit 扳机是 USHORT 位域，MSVC 位域不跨单元（10+10=20>16），
    //       因此每个扳机各占一个 USHORT（低 10 位有效），不能合并到一个 ushort！
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct XOneInputReport
    {
        public ushort LeftStickX;   // 0x8000 居中
        public ushort LeftStickY;   // 0x8000 居中
        public ushort RightStickX;  // 0x8000 居中
        public ushort RightStickY;  // 0x8000 居中
        public ushort LeftTrigger;  // 10bit，0-1023
        public ushort RightTrigger; // 10bit，0-1023
        public byte ButtonsMain;    // bit0=A bit1=B bit2=X bit3=Y bit4=LB bit5=RB bit6=Back bit7=Menu
        public byte ButtonsStick;   // bit0=LS bit1=RS
        public byte Hat;            // [3:0]=十字键
        public byte Misc;           // bit0=Home
        public byte BatteryLevel;   // 0x00-0xFF
    }

    // ============ P/Invoke ============
    [DllImport("WinUHidDevs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr WinUHidXOneCreate(IntPtr info, IntPtr ffCallback, IntPtr callbackContext);

    [DllImport("WinUHidDevs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void WinUHidXOneInitializeInputReport(ref XOneInputReport report);

    [DllImport("WinUHidDevs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void WinUHidXOneSetHatState(ref XOneInputReport report, int hatX, int hatY);

    [DllImport("WinUHidDevs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern bool WinUHidXOneReportInput(IntPtr gamepad, ref XOneInputReport report);

    [DllImport("WinUHidDevs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void WinUHidXOneDestroy(IntPtr gamepad);

    [DllImport("WinUHid.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern uint WinUHidGetDriverInterfaceVersion();

    // ============ 映射常量 ============
    const int STICK_CENTER = 0x8000;
    const int TRIGGER_MAX = 1023;               // 10bit
    const double ANGLE_RATIO = 32767.0 / 900.0; // ±450° → 摇杆满行程
    // 转向死区（度）：静止噪声小于该值时输出精确中心，避免轴微抖被游戏绑定界面捕获
    const double STEER_DEADZONE = 1.0;
    const int SMOOTH_STEP = TRIGGER_MAX / 30;
    const int CLUTCH_SMOOTH_STEP = 1000;        // 离合轴（右摇杆 Y）平滑步长
    const int GEAR_HOLD_TICKS = 6;              // 升/降档脉冲保持 6 帧（约 60ms），确保游戏捕捉到点击

    IntPtr _handle;
    readonly object _lock = new();

    int _lastThrottle, _lastBrake, _lastClutch;
    int _gearUpTimer, _gearDownTimer;

    public bool IsReady => _handle != IntPtr.Zero;
    public string LastError { get; private set; } = "";

    /// <summary>检测 WinUHid 驱动是否可用（驱动未安装时返回 0）。</summary>
    public static bool DriverAvailable()
    {
        try
        {
            return WinUHidGetDriverInterfaceVersion() != 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("WinUHid 驱动检测失败: " + ex.Message);
            return false;
        }
    }

    public bool Create(out string message)
    {
        lock (_lock)
        {
            if (_handle != IntPtr.Zero)
            {
                message = "WinUHid Xbox One 手柄已存在";
                return true;
            }

            if (!DriverAvailable())
            {
                LastError = "WinUHid 驱动未安装/不可用（需安装 WinUHidDriver.sys）";
                message = LastError;
                return false;
            }

            _handle = WinUHidXOneCreate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_handle == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                LastError = $"WinUHidXOneCreate 失败 (Win32 错误码 {err})";
                message = LastError;
                return false;
            }

            _lastThrottle = 0;
            _lastBrake = 0;
            _lastClutch = STICK_CENTER;
            _gearUpTimer = 0;
            _gearDownTimer = 0;
            LastError = "";
            message = "WinUHid Xbox One 虚拟手柄已创建";
            return true;
        }
    }

    /// <summary>
    /// 提交输入报告。映射（行业标准布局，Logitech/Fanatec H 挡通用）：
    ///   角度 → 左摇杆 X；油门 → 右扳机；刹车 → 左扳机；离合 → 右摇杆 Y；
    ///   升降档（序列挡）：RB=升档，LB=降档；
    ///   手刹 → Menu（独立按钮）；
    ///   自动挡（gearMode=1）：D=A（前进），R=X（倒车）；
    ///   手动挡（gearMode=3/4）：R=Back, 1=LB, 2=RB, 3=A, 4=B, 5=X, 6=Y（行业 H 挡标准顺序）。
    ///   油门由右扳机独立控制，不与挡位耦合。
    /// <para>
    /// <paramref name="btnMask"/> ≠ 0 时为"手柄直通"模式（手机布局2 Xbox 手柄）：
    ///   按钮/十字键按位掩码原样输出，跳过上述赛车挡位映射；
    ///   角度→左摇杆 X、油门→右扳机、刹车→左扳机仍然生效（传感器转向照常用）。
    ///   掩码位定义：bit0..7 = A B X Y LB RB Back Menu；bit8..9 = LS RS；
    ///   bit10..13 = 十字 上 下 左 右；bit14 = Home。
    /// </para>
    /// <para>
    /// <paramref name="sticks"/> ≠ null 时（手机布局2 摇杆被拖动，J=lx,ly,rx,ry，-100..100）：
    ///   四个摇杆轴全部由手机摇杆接管——左摇杆 X 覆盖传感器转向，右摇杆 Y 覆盖离合；
    ///   松手后手机停止上报 J=（sticks=null），自动恢复传感器转向/离合。
    /// </para>
    /// </summary>
    public void Report(double angle, int throttle, int brake, int clutch,
                       int handbrake, int gearUp, int gearDown,
                       int gearMode, int autoDr, int gearValue, int btnMask = 0, int[] sticks = null)
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero) return;

            // 防 NaN / Infinity：非法角度会被 (int)Math.Round 钳成 0 → 轴跳到最左并被游戏捕获
            if (double.IsNaN(angle) || double.IsInfinity(angle)) angle = 0;

            // 左摇杆 X：角度 → 摇杆（0x8000 居中）；静止噪声（<1°）归中心
            double steerAngle = Math.Abs(angle) < STEER_DEADZONE ? 0.0 : angle;
            int stickX = (int)Math.Round(STICK_CENTER + steerAngle * ANGLE_RATIO);
            stickX = Math.Clamp(stickX, 0, 0xFFFF);

            // 三个踏板独立通道（互不干扰，允许油离/跟趾配合）
            // 油门 → 右扳机，刹车 → 左扳机
            int targetThrottle = throttle * TRIGGER_MAX / 100;
            int targetBrake = brake * TRIGGER_MAX / 100;
            _lastThrottle = Smooth(_lastThrottle, targetThrottle);
            _lastBrake = Smooth(_lastBrake, targetBrake);

            // 离合 → 右摇杆 Y（0x8000 中心，向下递增 = 踩下）
            int targetClutch = STICK_CENTER + clutch * (0xFFFF - STICK_CENTER) / 100;
            _lastClutch = Smooth(_lastClutch, targetClutch, CLUTCH_SMOOTH_STEP);

            // 升/降档脉冲锁存：按下保持数帧，松开后递减到 0
            if (gearUp > 0) _gearUpTimer = GEAR_HOLD_TICKS;
            else if (_gearUpTimer > 0) _gearUpTimer--;
            if (gearDown > 0) _gearDownTimer = GEAR_HOLD_TICKS;
            else if (_gearDownTimer > 0) _gearDownTimer--;

            var report = new XOneInputReport
            {
                LeftStickX = (ushort)Math.Clamp(stickX, 0, 0xFFFF),
                LeftStickY = STICK_CENTER,
                RightStickX = STICK_CENTER,
                RightStickY = (ushort)_lastClutch,
                LeftTrigger = (ushort)(_lastBrake & 0x3FF),     // 左扳机 = 刹车
                RightTrigger = (ushort)(_lastThrottle & 0x3FF), // 右扳机 = 油门
                ButtonsMain = 0,
                ButtonsStick = 0,
                Hat = 0,
                Misc = 0,
                BatteryLevel = 0xFF,
            };

            // 布局2 摇杆直通：J= 存在时四个摇杆轴全部由手机摇杆接管
            // （左摇杆 X 覆盖传感器转向，右摇杆 Y 覆盖离合；松手手机停报 J= → 自动恢复）
            if (sticks != null)
            {
                report.LeftStickX = (ushort)Math.Clamp(STICK_CENTER + Math.Clamp(sticks[0], -100, 100) * 32767 / 100, 0, 0xFFFF);
                report.LeftStickY = (ushort)Math.Clamp(STICK_CENTER + Math.Clamp(sticks[1], -100, 100) * 32767 / 100, 0, 0xFFFF);
                report.RightStickX = (ushort)Math.Clamp(STICK_CENTER + Math.Clamp(sticks[2], -100, 100) * 32767 / 100, 0, 0xFFFF);
                report.RightStickY = (ushort)Math.Clamp(STICK_CENTER + Math.Clamp(sticks[3], -100, 100) * 32767 / 100, 0, 0xFFFF);
            }

            if (btnMask != 0)
            {
                // ===== 手柄直通（布局2 Xbox 手柄）：按钮位原样输出 =====
                report.ButtonsMain = (byte)(btnMask & 0xFF);              // A B X Y LB RB Back Menu
                report.ButtonsStick = (byte)((btnMask >> 8) & 0x03);      // LS RS
                int hatX = ((btnMask & (1 << 13)) != 0 ? 1 : 0)           // 十字右 → +
                         - ((btnMask & (1 << 12)) != 0 ? 1 : 0);          // 十字左 → -
                int hatY = ((btnMask & (1 << 11)) != 0 ? 1 : 0)           // 十字下 → +
                         - ((btnMask & (1 << 10)) != 0 ? 1 : 0);          // 十字上 → -
                WinUHidXOneSetHatState(ref report, hatX, hatY);
                report.Misc = (byte)((btnMask >> 14) & 0x01);             // Home
            }
            else
            {
                // ===== 赛车映射（布局0/1） =====

                // 升降档（序列挡）：RB=升档，LB=降档（行业标准拨片布局）
                if (_gearUpTimer > 0) report.ButtonsMain |= 0x20;   // RB = 升档
                if (_gearDownTimer > 0) report.ButtonsMain |= 0x10; // LB = 降档

                // 手刹（独立按钮，不与挡位冲突）
                if (handbrake > 0) report.ButtonsMain |= 0x80;      // Menu = 手刹

                // 自动挡（保持式；仅 gearMode==1 真实自动挡）：D=A（前进），R=X（倒车）
                // 加 gearMode 判断避免其它模式下误发保持式按钮导致游戏侧持续触发
                if (gearMode == 1)
                {
                    if (autoDr == 1) report.ButtonsMain |= 0x01;        // A = D
                    else if (autoDr == -1) report.ButtonsMain |= 0x04;  // X = R
                }

                // 手动挡（gearMode=3/4）：行业 H 挡标准顺序
                // R=Back, 1=LB, 2=RB, 3=A, 4=B, 5=X, 6=Y
                if (gearMode == 3 || gearMode == 4)
                {
                    switch (gearValue)
                    {
                        case -1: report.ButtonsMain |= 0x40; break; // Back = R
                        case 1: report.ButtonsMain |= 0x10; break;  // LB = 1档
                        case 2: report.ButtonsMain |= 0x20; break;  // RB = 2档
                        case 3: report.ButtonsMain |= 0x01; break;  // A = 3档
                        case 4: report.ButtonsMain |= 0x02; break;  // B = 4档
                        case 5: report.ButtonsMain |= 0x04; break;  // X = 5档
                        case 6: report.ButtonsMain |= 0x08; break;  // Y = 6档
                    }
                }
            }

            WinUHidXOneReportInput(_handle, ref report);
        }
    }

    static int Smooth(int current, int target) => Smooth(current, target, SMOOTH_STEP);

    static int Smooth(int current, int target, int step)
    {
        if (current < target) return Math.Min(current + step, target);
        if (current > target) return Math.Max(current - step, target);
        return target;
    }

    public void Destroy()
    {
        lock (_lock)
        {
            if (_handle != IntPtr.Zero)
            {
                WinUHidXOneDestroy(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    public void Dispose() => Destroy();
}
