using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32;

namespace WheelSimuServer;

/// <summary>
/// WinUHid 驱动零部署安装器。
/// 驱动文件（INF/DLL/CAT/CER）内嵌在 EXE 中，运行时可自动解压并安装：
///   1. 检测/开启测试签名模式（bcdedit /set testsigning on，需重启）
///   2. 检测/导入驱动签名证书（Root + TrustedPublisher）
///   3. 用 SetupAPI 创建 root 设备 + newdev 安装驱动
/// 仅 WinUHid（UMDF 用户态驱动）支持自动安装。
/// </summary>
public static class WinUHidDriverInstaller
{
    const string INF_NAME = "WinUHidDriver.inf";
    const string DLL_NAME = "WinUHidDriver.dll";
    const string CAT_NAME = "WinUHidDriver.cat";
    const string CER_NAME = "WinUHidDriver.cer";
    const string DRIVER_STORE_DIR = "WheelSimuServer\\Driver";
    const string HWID = "root\\WinUHid";
    const string INSTANCE_ID = "ROOT\\WINUHID\\0000";
    const string CERT_SUBJECT_PREFIX = "CN="; // 实际证书 CN 以 cer 文件为准

    public enum InstallState
    {
        Ready,          // 已就绪，无需操作
        RebootRequired, // 已开启测试签名，需重启后再次运行
        Installed,      // 本次安装成功
        Failed,         // 安装失败
    }

    // ==================== SetupAPI / newdev P/Invoke（与 winuhid-install 一致） ====================
    const int DICD_GENERATE_ID = 0x00000001;
    const int DIF_REGISTERDEVICE = 0x00000019;
    const uint SPDRP_HARDWAREID = 0x00000001;
    const uint INSTALLFLAG_FORCE = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid classGuid;
        public uint devInst;
        public IntPtr reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetINFClass(string infPath, out Guid classGuid, IntPtr className, uint classNameSize, uint flags);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiCreateDeviceInfo(IntPtr deviceInfoSet, string deviceName, ref Guid classGuid, string deviceDescription, IntPtr hwndParent, int creationFlags, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiSetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property, byte[] propertyBuffer, uint propertyBufferSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwndParent, string hardwareID, string fullInfPath, uint installFlags, out bool rebootNeeded);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    // ==================== 对外入口 ====================

    /// <summary>
    /// 完整自检+安装流程。返回是否需要重启等状态。
    /// </summary>
    public static (InstallState state, string detail) EnsureReady()
    {
        try
        {
            // 0. 解压内嵌驱动文件
            string? dir = ExtractDriverFiles(out string extractMsg);
            if (dir == null)
                return (InstallState.Failed, "驱动文件解压失败: " + extractMsg);

            // 1. 驱动已就绪则直接返回（覆盖已装好 / 证书已导入场景）
            if (IsDriverReady())
            {
                // 驱动能正常加载且签名证书已受信任 → 不再需要测试签名模式，主动关闭（需重启生效）
                if (IsCertInstalled(out _) && IsTestSigningEnabled())
                {
                    string? offMsg = DisableTestSigning();
                    if (offMsg != null)
                        return (InstallState.Ready, "WinUHid 驱动已就绪（提示：自动关闭测试签名模式失败: " + offMsg + "）");
                    return (InstallState.RebootRequired,
                        "WinUHid 驱动已就绪，已自动关闭测试签名模式。重启电脑后生效（右下角水印将消失）。");
                }
                return (InstallState.Ready, "WinUHid 驱动已就绪");
            }

            // 2. 签名证书
            if (!IsCertInstalled(out string certName))
            {
                string? certMsg = ImportCertificates();
                if (certMsg != null)
                    return (InstallState.Failed, "导入驱动证书失败: " + certMsg);
            }

            // 3. 测试签名模式（驱动未就绪才需要检查）
            if (!IsTestSigningEnabled())
            {
                string? enableMsg = EnableTestSigning();
                if (enableMsg != null)
                    return (InstallState.Failed, "开启测试签名模式失败: " + enableMsg);
                return (InstallState.RebootRequired,
                    "已开启 Windows 测试签名模式。需要重启电脑后再次运行本程序即可自动完成驱动安装。");
            }

            // 4. 安装驱动
            string? msg = InstallDriver(dir);
            if (msg != null)
                return (InstallState.Failed, msg);

            return IsDriverReady()
                ? (InstallState.Installed, "WinUHid 驱动安装成功，可直接使用")
                : (InstallState.RebootRequired, "驱动安装完成，可能需要重启电脑后才能生效。");
        }
        catch (Exception ex)
        {
            return (InstallState.Failed, "自动安装异常: " + ex.Message);
        }
    }

    /// <summary>驱动当前是否可用（以能否打开设备为准，程序已提权）。</summary>
    public static bool IsDriverReady()
    {
        try
        {
            // 优先用 WinUHid 自身 API 探测（DLL 已随程序输出）
            if (WinUHidDeviceManager.DriverAvailable()) return true;
            // 兜底：直接尝试打开设备符号链接
            IntPtr h = CreateFileW(@"\\.\WinUHid", 0, 0x3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h != IntPtr.Zero && h != new IntPtr(-1)) { CloseHandle(h); return true; }
            return false;
        }
        catch
        {
            return false;
        }
    }

    // ==================== 驱动文件解压 ====================

    /// <summary>从程序集内嵌资源解压 4 个驱动文件到 %ProgramData% 下。</summary>
    public static string? ExtractDriverFiles(out string message)
    {
        message = "";
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), DRIVER_STORE_DIR);
            Directory.CreateDirectory(dir);

            var names = Assembly.GetExecutingAssembly().GetManifestResourceNames();
            string? Find(string fileName)
            {
                foreach (var n in names)
                    if (n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase))
                        return n;
                return null;
            }

            foreach (string file in new[] { INF_NAME, DLL_NAME, CAT_NAME, CER_NAME })
            {
                string? res = Find(file);
                if (res == null) { message = $"缺少内嵌资源 {file}"; return null; }
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(res);
                if (stream == null) { message = $"内嵌资源不可读 {file}"; return null; }
                string target = Path.Combine(dir, file);
                using var fs = new FileStream(target, FileMode.Create, FileAccess.Write);
                stream.CopyTo(fs);
            }
            return dir;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return null;
        }
    }

    // ==================== 测试签名模式 ====================

    /// <summary>测试签名模式是否已开启。
    /// 优先读 CI\TestSigning 注册表（用户态可读、无需管理员），读不到再降级用 bcdedit 退出码。</summary>
    public static bool IsTestSigningEnabled()
    {
        try
        {
            using var ciKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI");
            var val = ciKey?.GetValue("TestSigning");
            if (val is int i) return i != 0;
            if (val is byte[] arr && arr.Length > 0) return (arr[0] & 1) == 1;
        }
        catch
        {
            // 注册表不可读（极少数环境），忽略走 bcdedit
        }
        return RunBcdeditExitCode("/enum {current}") == 0;
    }

    /// <summary>开启测试签名模式，成功返回 null，失败返回错误信息。</summary>
    public static string? EnableTestSigning()
    {
        try
        {
            int code = RunBcdeditExitCode("/set testsigning on");
            if (code != 0)
                return $"bcdedit 退出码 {code}（请以管理员身份运行本程序）";
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>关闭测试签名模式（恢复系统默认安全级别）。成功返回 null，失败返回错误信息。</summary>
    public static string? DisableTestSigning()
    {
        try
        {
            int code = RunBcdeditExitCode("/set testsigning off");
            if (code != 0)
                return $"bcdedit 退出码 {code}（请以管理员身份运行本程序）";
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>执行 bcdedit 并返回退出码（0=成功）。</summary>
    static int RunBcdeditExitCode(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("bcdedit.exe", args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi)!;
            string so = p.StandardOutput.ReadToEnd();
            string se = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    // ==================== 证书导入 ====================

    /// <summary>驱动签名证书是否已导入（Root + TrustedPublisher 两个 store）。</summary>
    public static bool IsCertInstalled(out string certName)
    {
        certName = "";
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), DRIVER_STORE_DIR);
            string cerPath = Path.Combine(dir, CER_NAME);
            if (!File.Exists(cerPath)) return false;

            using var cert = new X509Certificate2(cerPath);
            certName = cert.Subject;
            byte[] thumb = cert.GetCertHash();
            return StoreHasCert("Root", thumb) && StoreHasCert("TrustedPublisher", thumb);
        }
        catch
        {
            return false;
        }
    }

    static bool StoreHasCert(string storeName, byte[] thumbprint)
    {
        try
        {
            using var store = new X509Store(storeName, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, Convert.ToHexString(thumbprint), false);
            return found.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>导入驱动签名证书到 Root 与 TrustedPublisher。成功返回 null。</summary>
    public static string? ImportCertificates()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), DRIVER_STORE_DIR);
            string cerPath = Path.Combine(dir, CER_NAME);
            if (!File.Exists(cerPath)) return "证书文件不存在: " + cerPath;

            using var cert = new X509Certificate2(cerPath);
            foreach (string storeName in new[] { "Root", "TrustedPublisher" })
            {
                using var store = new X509Store(storeName, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                byte[] thumb = cert.GetCertHash();
                var existing = store.Certificates.Find(X509FindType.FindByThumbprint, Convert.ToHexString(thumb), false);
                if (existing.Count == 0)
                    store.Add(cert);
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // ==================== 驱动安装 ====================

    /// <summary>安装 WinUHid 驱动。成功返回 null，失败返回错误信息。</summary>
    public static string? InstallDriver(string driverDir)
    {
        string fullInf = Path.Combine(driverDir, INF_NAME);
        if (!File.Exists(fullInf)) return "INF 不存在: " + fullInf;

        // Step 0: stage INF+CAT 进驱动商店（仅当前驱动包，不 /install 避免重复）
        string pnpOut = RunPnpUtil("/add-driver \"" + fullInf + "\" /install");
        if (!pnpOut.Contains("published", StringComparison.OrdinalIgnoreCase)
            && !pnpOut.Contains("success", StringComparison.OrdinalIgnoreCase)
            && !pnpOut.Contains("已添加", StringComparison.OrdinalIgnoreCase))
            return "pnputil 添加驱动包失败: " + pnpOut.Trim();

        // Step 1: 移除旧设备节点（忽略错误）
        RunPnpUtil("/remove-device \"" + INSTANCE_ID + "\"");

        // Step 2: 创建 root 设备
        try
        {
            IntPtr className = Marshal.AllocHGlobal(512);
            try
            {
                Guid classGuid;
                if (!SetupDiGetINFClass(fullInf, out classGuid, className, 256, 0))
                    return "SetupDiGetINFClass 失败: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;

                IntPtr devInfoSet = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
                if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1))
                    return "SetupDiCreateDeviceInfoList 失败: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;

                try
                {
                    var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                    if (!SetupDiCreateDeviceInfo(devInfoSet, "WinUHid", ref classGuid, null!, IntPtr.Zero, DICD_GENERATE_ID, ref devInfo))
                        return "SetupDiCreateDeviceInfo 失败: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;

                    byte[] hwidBytes = Encoding.Unicode.GetBytes(HWID + "\0\0");
                    if (!SetupDiSetDeviceRegistryProperty(devInfoSet, ref devInfo, SPDRP_HARDWAREID, hwidBytes, (uint)hwidBytes.Length))
                        return "SetupDiSetDeviceRegistryProperty 失败: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;

                    if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, devInfoSet, ref devInfo))
                        return "DIF_REGISTERDEVICE 失败: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                }
                finally
                {
                    SetupDiDestroyDeviceInfoList(devInfoSet);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(className);
            }
        }
        catch (Exception ex)
        {
            return "创建设备异常: " + ex.Message;
        }

        // Step 3: newdev 绑定驱动
        try
        {
            bool reboot = false;
            if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, HWID, fullInf, INSTALLFLAG_FORCE, out reboot))
                return "UpdateDriverForPlugAndPlayDevices 失败: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
        }
        catch (Exception ex)
        {
            return "绑定驱动异常: " + ex.Message;
        }

        return null;
    }

    static string RunPnpUtil(string argsLine)
    {
        try
        {
            var psi = new ProcessStartInfo("pnputil.exe", argsLine)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi)!;
            string so = p.StandardOutput.ReadToEnd();
            string se = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (so + "\n" + se).Trim();
        }
        catch (Exception ex)
        {
            return "pnputil 执行失败: " + ex.Message;
        }
    }
}
