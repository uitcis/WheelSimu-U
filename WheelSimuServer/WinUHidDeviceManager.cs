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

    IntPtr _handle;
    readonly object _lock = new();

    int _lastThrottle, _lastBrake;

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
            LastError = "";
            message = "WinUHid Xbox One 虚拟手柄已创建";
            return true;
        }
    }

    /// <summary>
    /// 提交输入报告。映射（与 vJoy 模式保持一致）：
    ///   角度 → 左摇杆 X；油门 → 左扳机；刹车 → 右扳机；
    ///   升档 → A；降档 → B；手刹 → LB。
    /// </summary>
    public void Report(double angle, int throttle, int brake, int clutch,
                       int handbrake, int gearUp, int gearDown)
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero) return;

            // 左摇杆 X：角度 → 摇杆（0x8000 居中）
            int stickX = (int)Math.Round(STICK_CENTER + angle * ANGLE_RATIO);
            stickX = Math.Clamp(stickX, 0, 0xFFFF);

            // 扳机 10bit
            int targetThrottle = throttle * TRIGGER_MAX / 100;
            int targetBrake = brake * TRIGGER_MAX / 100;
            if (targetThrottle > 0) targetBrake = 0;
            if (targetBrake > 0) targetThrottle = 0;
            _lastThrottle = Smooth(_lastThrottle, targetThrottle);
            _lastBrake = Smooth(_lastBrake, targetBrake);

            var report = new XOneInputReport
            {
                LeftStickX = (ushort)stickX,
                LeftStickY = STICK_CENTER,
                RightStickX = STICK_CENTER,
                RightStickY = STICK_CENTER,
                LeftTrigger = (ushort)(_lastThrottle & 0x3FF),
                RightTrigger = (ushort)(_lastBrake & 0x3FF),
                ButtonsMain = 0,
                ButtonsStick = 0,
                Hat = 0,
                Misc = 0,
                BatteryLevel = 0xFF,
            };

            if (gearUp > 0) report.ButtonsMain |= 0x01;   // A
            if (gearDown > 0) report.ButtonsMain |= 0x02; // B
            if (handbrake > 0) report.ButtonsMain |= 0x10; // LB

            WinUHidXOneReportInput(_handle, ref report);
        }
    }

    static int Smooth(int current, int target)
    {
        if (current < target) return Math.Min(current + SMOOTH_STEP, target);
        if (current > target) return Math.Max(current - SMOOTH_STEP, target);
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
