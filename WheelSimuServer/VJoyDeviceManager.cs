using Microsoft.Win32;
using System.ServiceProcess;

namespace WheelSimuServer;

/// <summary>
/// 自动管理 vJoy 设备的“存在性”，解决 vJoy 设备常驻系统、
/// 干扰 Xbox 手柄等真实设备的问题。
///
/// 原理（与 vJoyConf 一致）：
///   - vJoy 设备是否在系统中出现，由注册表
///     HKLM\SYSTEM\CurrentControlSet\services\vjoy\Parameters\Device0
///     的配置决定；修改后需重启 vJoy 驱动服务才能生效。
///   - 启动时写入设备 1 所需的轴/按钮配置并重启驱动 -> 设备出现，手机可当方向盘；
///   - 退出时删除该配置并重启驱动 -> 设备消失，Xbox 手柄自动恢复正常。
/// 全程无需用户手动操作 vJoyConf。需要管理员权限（改 HKLM + 重启驱动）。
/// </summary>
public static class VJoyDeviceManager
{
    const string REG_BASE = @"SYSTEM\CurrentControlSet\services\vjoy\Parameters";
    const string REG_DEVICE0 = REG_BASE + @"\Device0";
    const uint DEVICE_ID = 1;

    // 本程序需要的轴（与 MainForm.UpdateVJoy 映射一致）：
    // X(方向盘), Y(油门), Z(刹车), XRot(手刹), YRot(离合)
    static readonly string[] AxisKeys =
    {
        "AxisX", "AxisY", "AxisZ", "AxisXRot", "AxisYRot"
    };

    /// <summary>启用设备 1：写入注册表并重启驱动。</summary>
    public static bool EnableDevice(out string message)
    {
        try
        {
            if (!IsDriverInstalled())
            {
                message = "vJoy 驱动未安装，无法自动配置设备";
                return false;
            }

            using var key = Registry.LocalMachine.CreateSubKey(REG_DEVICE0);
            if (key == null)
            {
                message = "无法打开 vJoy 注册表项（权限不足？需管理员）";
                return false;
            }

            foreach (var axis in AxisKeys)
                key.SetValue(axis, 1, RegistryValueKind.DWord);

            key.SetValue("Button", 8, RegistryValueKind.DWord); // 至少 8 个按钮
            key.SetValue("DiscPov", 0, RegistryValueKind.DWord);
            key.SetValue("ContPov", 0, RegistryValueKind.DWord);
            key.Close();

            if (!RestartDriver(out var err))
            {
                message = $"已写入配置但重启驱动失败: {err}";
                return false;
            }

            message = "vJoy 设备已自动启用";
            return true;
        }
        catch (Exception ex)
        {
            message = $"启用 vJoy 设备异常: {ex.Message}";
            return false;
        }
    }

    /// <summary>禁用设备 1：删除配置并重启驱动，使其从系统中消失。</summary>
    public static bool DisableDevice(out string message)
    {
        try
        {
            if (!IsDriverInstalled())
            {
                message = "vJoy 驱动未安装，无需禁用";
                return true;
            }

            // 删除 Device0 整项 -> 设备不存在
            using (var baseKey = Registry.LocalMachine.OpenSubKey(REG_BASE, writable: true))
            {
                baseKey?.DeleteSubKeyTree("Device0", throwOnMissingSubKey: false);
            }

            if (!RestartDriver(out var err))
            {
                message = $"已删除配置但重启驱动失败: {err}";
                return false;
            }

            message = "vJoy 设备已自动禁用";
            return true;
        }
        catch (Exception ex)
        {
            message = $"禁用 vJoy 设备异常: {ex.Message}";
            return false;
        }
    }

    static bool IsDriverInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(REG_BASE);
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>重启 vJoy 驱动服务，使注册表配置生效。服务名通常为 vjoy / vjoyctl。</summary>
    static bool RestartDriver(out string error)
    {
        error = "";
        foreach (var svcName in new[] { "vjoy", "vjoyctl" })
        {
            try
            {
                using var svc = new ServiceController(svcName);
                var status = svc.Status;
                if (status == ServiceControllerStatus.Running)
                {
                    svc.Stop();
                    svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                }
                svc.Start();
                svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                error = $"{svcName}: {ex.Message}";
            }
        }
        return false;
    }
}
