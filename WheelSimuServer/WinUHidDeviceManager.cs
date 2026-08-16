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
    static extern bool WinUHidXOneReportInput(IntPtr gamepad, ref XOneInputReport report);

    [DllImport("WinUHidDevs.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void WinUHidXOneDestroy(IntPtr gamepad);

    [DllImport("WinUHid.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern uint WinUHidGetDriverInterfaceVersion();

    // ============ 映射常量 ============
    const int STICK_CENTER = 0x8000;
    const int TRIGGER_MAX = 1023;               // 10bit
    const double ANGLE_RATIO = 32767.0 / 900.0; // ±450° → 摇杆满行程
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
    /// 提交输入报告。映射（方向盘标准布局，三踏板独立通道）：
    ///   角度 → 左摇杆 X；油门 → 右扳机；刹车 → 左扳机；离合 → 右摇杆 Y（向下踩）；
    ///   升档 → RB（右拨片）；降档 → LB（左拨片）；手刹 → B；
    ///   自动挡：D→Y（满油门）、R→X（满油门）；
    ///   手动挡：R→Back、1→A、2→X、3→Y、4→LB、5→RB、6→Menu。
    ///   升/降档为瞬时脉冲，自动锁存约 60ms，避免游戏 60Hz 轮询漏检。
    /// </summary>
    public void Report(double angle, int throttle, int brake, int clutch,
                       int handbrake, int gearUp, int gearDown,
                       int gearMode, int autoDr, int gearValue)
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero) return;

            // 左摇杆 X：角度 → 摇杆（0x8000 居中）
            int stickX = (int)Math.Round(STICK_CENTER + angle * ANGLE_RATIO);
            stickX = Math.Clamp(stickX, 0, 0xFFFF);

            // 三个踏板独立通道（互不干扰，允许油离/跟趾配合）
            // 油门 → 右扳机，刹车 → 左扳机
            int targetThrottle = throttle * TRIGGER_MAX / 100;
            // 自动挡：按住 D/R 时持续满油门（松开即回踏板值）
            if (autoDr == 1 || autoDr == -1) targetThrottle = TRIGGER_MAX;
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
                LeftStickX = (ushort)stickX,
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

            if (_gearUpTimer > 0) report.ButtonsMain |= 0x20;   // RB = 升档
            if (_gearDownTimer > 0) report.ButtonsMain |= 0x10; // LB = 降档
            if (handbrake > 0) report.ButtonsMain |= 0x02;      // B = 手刹

            // 自动挡 D/R：Y = D（前进）、X = R（倒车）
            if (autoDr == 1) report.ButtonsMain |= 0x08;        // Y = D
            else if (autoDr == -1) report.ButtonsMain |= 0x04;  // X = R

            // 手动挡挡位（-1=R, 1..6），5档(3)/6档(4) 共用（Xbox 按钮位有限，最多映射到 6 档）
            if (gearMode == 3 || gearMode == 4)
            {
                switch (gearValue)
                {
                    case -1: report.ButtonsMain |= 0x40; break; // Back = R
                    case 1: report.ButtonsMain |= 0x01; break;  // A = 1档
                    case 2: report.ButtonsMain |= 0x04; break;  // X = 2档
                    case 3: report.ButtonsMain |= 0x08; break;  // Y = 3档
                    case 4: report.ButtonsMain |= 0x10; break;  // LB = 4档
                    case 5: report.ButtonsMain |= 0x20; break;  // RB = 5档
                    case 6: report.ButtonsMain |= 0x80; break;  // Menu = 6档
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
