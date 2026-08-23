using System.Runtime.InteropServices;

namespace WheelSimuServer;

/// <summary>
/// WinUHid (lurebat/WinUHid) 自定义方向盘虚拟设备输出。
/// 直接调用 WinUHid.dll 核心 API，传入自定义 HID 报告描述符，
/// 将虚拟设备枚举为"方向盘/模拟控制器"（DirectInput 设备），
/// 供 AC/ACC/iRacing/欧卡/地平线等支持方向盘的赛车游戏识别。
/// </summary>
public sealed class WinUHidWheelDeviceManager : IDisposable
{
    // ============ 原生结构（对应 WinUHid.h WINUHID_DEVICE_CONFIG） ============
    // ⚠️ 头文件在 #include <pshpack1.h>/<poppack.h> 之间定义该结构体，必须 Pack=1！
    //    （否则 C 侧在 offset 12 读 ReportDescriptor 时拿到错位指针 → 错误码 998）
    // 64 位 pack=1 布局（共 56 字节）：
    //   0-3   SupportedEvents        (uint)
    //   4-5   VendorID               (ushort)
    //   6-7   ProductID              (ushort)
    //   8-9   VersionNumber          (ushort)
    //   10-11 ReportDescriptorLength (ushort)
    //   12-19 ReportDescriptor       (IntPtr)
    //   20-35 ContainerId            (Guid)
    //   36-43 InstanceID             (IntPtr)
    //   44-51 HardwareIDs            (IntPtr)
    //   52-55 ReadReportPeriodUs     (uint)
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct WINUHID_DEVICE_CONFIG
    {
        public uint SupportedEvents;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
        public ushort ReportDescriptorLength;
        public IntPtr ReportDescriptor;
        public Guid ContainerId;
        public IntPtr InstanceID;
        public IntPtr HardwareIDs;
        public uint ReadReportPeriodUs;
    }

    // ============ P/Invoke（WinUHid.dll 核心 API） ============
    [DllImport("WinUHid.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    static extern IntPtr WinUHidCreateDevice(ref WINUHID_DEVICE_CONFIG config);

    [DllImport("WinUHid.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    static extern bool WinUHidStartDevice(IntPtr device, IntPtr eventCallback, IntPtr callbackContext);

    [DllImport("WinUHid.dll", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    static extern bool WinUHidSubmitInputReport(IntPtr device, byte[] report, int reportSize);

    [DllImport("WinUHid.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern void WinUHidDestroyDevice(IntPtr device);

    [DllImport("WinUHid.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern uint WinUHidGetDriverInterfaceVersion();

    // ============ 方向盘 HID 报告描述符 ============
    // 布局：4 轴 × 16bit（X=转向, Rx=油门, Ry=刹车, Rz=离合）+ 32 按钮
    // 报告 12 字节：[X_lo X_hi Rx_lo Rx_hi Ry_lo Ry_hi Rz_lo Rz_hi BTN0 BTN1 BTN2 BTN3]
    // 轴命名对齐游戏里的：摇杆 X 轴 / X 旋转 / Y 旋转 / Z 旋转。
    // 按钮 1~32 供序列挡/手动挡/自动挡按需映射（见 Report 注释）。
    static readonly byte[] k_WheelReportDescriptor =
    {
        0x05, 0x01,        // Usage Page (Generic Desktop)
        // ⚠️ 不能用 Wheel(0x08)！Windows 的 hidgame.inf 只把 Joystick(0x04)/Gamepad(0x05)
        //    识别为"游戏控制器"并创建 DirectInput 接口；Wheel 只被识别为普通 HID 设备，
        //    游戏里根本看不到。模拟方向盘必须用 Joystick（真实 G27 靠厂商驱动才显示 Wheel）。
        0x09, 0x04,        // Usage (Joystick)
        0xA1, 0x01,        // Collection (Application)
        0xA1, 0x00,        //   Collection (Physical)
        0x09, 0x30,        //     Usage (X)      — 转向（摇杆 X 轴）
        0x09, 0x33,        //     Usage (Rx)     — 油门（摇杆 X 旋转）
        0x09, 0x34,        //     Usage (Ry)     — 刹车（摇杆 Y 旋转）
        0x09, 0x35,        //     Usage (Rz)     — 离合（摇杆 Z 旋转）
        0x15, 0x00,        //     Logical Minimum (0)
        0x26, 0xFF, 0xFF,  //     Logical Maximum (65535)
        0x35, 0x00,        //     Physical Minimum (0)
        0x46, 0xFF, 0xFF,  //     Physical Maximum (65535)
        0x75, 0x10,        //     Report Size (16)
        0x95, 0x04,        //     Report Count (4)
        0x81, 0x02,        //     Input (Data,Var,Abs)
        0xC0,              //   End Collection
        0x05, 0x09,        //   Usage Page (Button)
        0x19, 0x01,        //   Usage Minimum (1)
        0x29, 0x20,        //   Usage Maximum (32)
        0x15, 0x00,        //   Logical Minimum (0)
        0x25, 0x01,        //   Logical Maximum (1)
        0x75, 0x01,        //   Report Size (1)
        0x95, 0x20,        //   Report Count (32)
        0x81, 0x02,        //   Input (Data,Var,Abs)
        0xC0,              // End Collection
    };
    const int REPORT_SIZE = 4 * 2 + 4;  // 4 轴 × 16bit + 32 按钮 bit = 12 字节

    // ============ 按钮位映射（32 位，对应 Button 1~32） ============
    // 欧卡2 换挡：真实自动挡=D/R 按钮驱动；序列挡=升/降挡按钮；手动挡(H档)=每个挡位一个按钮；简易档无挡位按钮
    const uint BTN_GEAR_UP   = 0x00000001; // Button 1  = 升挡（序列挡）
    const uint BTN_GEAR_DN   = 0x00000002; // Button 2  = 降挡（序列挡）
    const uint BTN_HANDBRAKE = 0x00000004; // Button 3  = 手刹
    const uint BTN_AUTO_D    = 0x00000008; // Button 4  = 自动挡 D（按住前进）
    const uint BTN_AUTO_R    = 0x00000010; // Button 5  = 自动挡 R（按住倒车）
    const uint BTN_GEAR_R    = 0x00000020; // Button 6  = 手动挡 R
    const uint BTN_GEAR_1    = 0x00000040; // Button 7  = 手动挡 1（1..6 挡 → Button 7..12）

    // ============ 映射常量 ============
    const int STICK_CENTER = 0x8000;
    const int AXIS_MAX = 0xFFFF;
    // 满行程 ±225°（等效比例 angle/450，保证手机角度与游戏方向盘 1:1）
    // 说明：中心 32768/半程 32767，若用 /900 则手机转 90° 游戏只转 45°，因此必须用 /450。
    const double ANGLE_RATIO = 32767.0 / 450.0;
    const int SMOOTH_STEP = AXIS_MAX / 30;
    const int CLUTCH_SMOOTH_STEP = AXIS_MAX / 60;
    const int GEAR_HOLD_TICKS = 6;   // 升降档脉冲保持 ~60ms

    IntPtr _handle;
    readonly object _lock = new();
    GCHandle _descPinned;
    IntPtr _hardwareIds;

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
                message = "WinUHid 方向盘设备已存在";
                return true;
            }

            if (!DriverAvailable())
            {
                LastError = "WinUHid 驱动未安装/不可用（需安装 WinUHidDriver.sys）";
                message = LastError;
                return false;
            }

            // 固定报告描述符，供 CreateDevice 期间引用
            _descPinned = GCHandle.Alloc(k_WheelReportDescriptor, GCHandleType.Pinned);

            // HardwareIDs（REG_MULTI_SZ：字符串 + 双 null 结尾）
            const string hwIds = "HID\\VID_1234&PID_5678";
            _hardwareIds = Marshal.StringToHGlobalUni(hwIds + "\0\0");

            try
            {
                var config = new WINUHID_DEVICE_CONFIG
                {
                    SupportedEvents = 0,
                    VendorID = 0x1234,
                    ProductID = 0x5678,
                    VersionNumber = 1,
                    ReportDescriptorLength = (ushort)k_WheelReportDescriptor.Length,
                    ReportDescriptor = _descPinned.AddrOfPinnedObject(),
                    ContainerId = Guid.Empty,
                    InstanceID = IntPtr.Zero,
                    HardwareIDs = _hardwareIds,
                    ReadReportPeriodUs = 0,
                };

                _handle = WinUHidCreateDevice(ref config);
                if (_handle == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    LastError = $"WinUHidCreateDevice 失败 (Win32 错误码 {err})";
                    message = LastError;
                    return false;
                }

                if (!WinUHidStartDevice(_handle, IntPtr.Zero, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    WinUHidDestroyDevice(_handle);
                    _handle = IntPtr.Zero;
                    LastError = $"WinUHidStartDevice 失败 (Win32 错误码 {err})";
                    message = LastError;
                    return false;
                }

                // 提交一个中立初始报告
                _lastThrottle = 0;
                _lastBrake = 0;
                _lastClutch = 0;
                _gearUpTimer = 0;
                _gearDownTimer = 0;
                SubmitRaw(0x8000, 0, 0, 0, 0);

                LastError = "";
                message = "WinUHid 方向盘设备已创建（DirectInput 设备）";
                return true;
            }
            catch (Exception ex)
            {
                if (_handle != IntPtr.Zero) { WinUHidDestroyDevice(_handle); _handle = IntPtr.Zero; }
                LastError = "WinUHid 方向盘创建异常: " + ex.Message;
                message = LastError;
                return false;
            }
            finally
            {
                // config 只需在调用期间有效，创建完成后释放
                if (_hardwareIds != IntPtr.Zero) { Marshal.FreeHGlobal(_hardwareIds); _hardwareIds = IntPtr.Zero; }
                if (_descPinned.IsAllocated) _descPinned.Free();
            }
        }
    }

    /// <summary>
    /// 提交输入报告。映射（对齐游戏截图中的"摇杆"命名）：
    ///   角度 → X 轴（转向 / 摇杆 X 轴）；
    ///   油门 → Rx 轴（摇杆 X 旋转）；刹车 → Ry 轴（摇杆 Y 旋转）；离合 → Rz 轴（摇杆 Z 旋转）。
    /// 档位按 gearMode 切换（欧卡2 换挡方式）：
    ///   0 简易档：仅油门/刹车，无任何挡位按钮输出。
    ///   1 真实自动挡：按住 D(autoDr=1) 油门持续→满，并输出 Button 4；按住 R(autoDr=-1) 同样满油门 + Button 5。
    ///   2 序列挡：升/降挡为瞬时脉冲，锁存约 60ms 输出 Button 1/2（避免 60Hz 轮询漏检）。
    ///   3 5档手动挡：gearValue(-1=R, 0=N, 1..6) 点按保持，映射 Button 6=R、Button 7..12=1..6。
    ///   4 6档手动挡：同 3，仅档位数不同（同样 1..6）。
    /// 手刹始终为 Button 3。
    /// </summary>
    public void Report(double angle, int throttle, int brake, int clutch,
                       int handbrake, int gearUp, int gearDown,
                       int gearMode, int autoDr, int gearValue)
    {
        lock (_lock)
        {
            if (_handle == IntPtr.Zero) return;

            // 转向：±225° → 0..65535（32768 居中），保证手机角度与游戏方向盘 1:1
            int x = (int)Math.Round(STICK_CENTER + angle * ANGLE_RATIO);
            x = Math.Clamp(x, 0, AXIS_MAX);

            // 三个踏板独立通道（对应 Rx/Ry/Rz，即游戏里常显示的 X/Y/Z 旋转）
            int targetThrottle = throttle * AXIS_MAX / 100;
            // 自动挡：按住 D/R 时持续满油门（松开即回踏板值）
            if (autoDr == 1 || autoDr == -1) targetThrottle = AXIS_MAX;
            int targetBrake = brake * AXIS_MAX / 100;
            int targetClutch = clutch * AXIS_MAX / 100;
            _lastThrottle = Smooth(_lastThrottle, targetThrottle, SMOOTH_STEP);
            _lastBrake = Smooth(_lastBrake, targetBrake, SMOOTH_STEP);
            _lastClutch = Smooth(_lastClutch, targetClutch, CLUTCH_SMOOTH_STEP);

            // 升降档脉冲锁存（序列挡）
            if (gearUp > 0) _gearUpTimer = GEAR_HOLD_TICKS;
            else if (_gearUpTimer > 0) _gearUpTimer--;
            if (gearDown > 0) _gearDownTimer = GEAR_HOLD_TICKS;
            else if (_gearDownTimer > 0) _gearDownTimer--;

            uint buttons = 0;
            if (_gearUpTimer > 0) buttons |= BTN_GEAR_UP;
            if (_gearDownTimer > 0) buttons |= BTN_GEAR_DN;
            if (handbrake > 0) buttons |= BTN_HANDBRAKE;

            // 自动挡 D/R
            if (autoDr == 1) buttons |= BTN_AUTO_D;
            else if (autoDr == -1) buttons |= BTN_AUTO_R;

            // 手动挡挡位（-1=R, 1..6），5档(3)/6档(4) 共用（手机端最大仅 6 挡）
            if (gearMode == 3 || gearMode == 4)
            {
                if (gearValue == -1) buttons |= BTN_GEAR_R;
                else if (gearValue >= 1 && gearValue <= 6) buttons |= BTN_GEAR_1 << (gearValue - 1);
            }

            SubmitRaw(x, _lastThrottle, _lastBrake, _lastClutch, buttons);
        }
    }

    void SubmitRaw(int x, int y, int z, int rz, uint buttons)
    {
        var report = new byte[REPORT_SIZE];
        report[0] = (byte)(x & 0xFF);
        report[1] = (byte)(x >> 8);
        report[2] = (byte)(y & 0xFF);
        report[3] = (byte)(y >> 8);
        report[4] = (byte)(z & 0xFF);
        report[5] = (byte)(z >> 8);
        report[6] = (byte)(rz & 0xFF);
        report[7] = (byte)(rz >> 8);
        report[8] = (byte)(buttons & 0xFF);
        report[9] = (byte)((buttons >> 8) & 0xFF);
        report[10] = (byte)((buttons >> 16) & 0xFF);
        report[11] = (byte)((buttons >> 24) & 0xFF);
        WinUHidSubmitInputReport(_handle, report, report.Length);
    }

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
                WinUHidDestroyDevice(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    public void Dispose() => Destroy();
}
