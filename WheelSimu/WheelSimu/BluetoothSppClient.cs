using System;
using System.Collections.Generic;
using Android.Bluetooth;
using Android.Content;
using Android.OS;
using Java.Util;

namespace WheelSimu
{
    /// <summary>已配对设备条目</summary>
    public sealed class PairedDevice
    {
        public string Name { get; }
        public string Address { get; }

        public PairedDevice(string name, string address)
        {
            Name = name;
            Address = address;
        }

        public override string ToString() => string.IsNullOrEmpty(Name) ? Address : $"{Name} ({Address})";
    }

    /// <summary>
    /// 经典蓝牙 SPP (RFCOMM) 客户端：手机主动连接 PC 端广播的「串行端口」服务。
    /// 数据格式与 TCP/UDP 完全一致（'@' 结尾的文本帧），PC 端无需区分来源。
    /// </summary>
    public sealed class BluetoothSppClient : IDisposable
    {
        /// <summary>
        /// 与 PC 端 BluetoothSppServer.ServiceUuid 完全一致（必须保持一致，否则 SDP 查不到服务）。
        /// 不用标准串口 UUID，避免与 Windows 已有的蓝牙串口 COM 冲突。
        /// </summary>
        public static readonly UUID SppUuid = UUID.FromString("8D358FC4-B4F8-422C-9FE6-BD384843220A");

        BluetoothSocket _socket;
        System.IO.Stream _output;

        public bool IsConnected
        {
            get
            {
                try { return _socket != null && _socket.IsConnected; }
                catch { return false; }
            }
        }

        /// <summary>获取蓝牙适配器（BluetoothManager 在 API 18+ 必然可用，故无需降级到已废弃的 DefaultAdapter）</summary>
        public static BluetoothAdapter GetAdapter(Context context)
        {
            try
            {
                if (context?.GetSystemService(Context.BluetoothService) is BluetoothManager mgr)
                    return mgr.Adapter;
            }
            catch { }

            return null;
        }

        public static bool IsAvailable(Context context) => GetAdapter(context) != null;

        public static bool IsEnabled(Context context)
        {
            try
            {
                var adapter = GetAdapter(context);
                return adapter != null && adapter.IsEnabled;
            }
            catch { return false; }
        }

        /// <summary>列出已配对设备（需要 BLUETOOTH_CONNECT 权限）</summary>
        public static List<PairedDevice> GetPairedDevices(Context context)
        {
            var result = new List<PairedDevice>();
            try
            {
                var adapter = GetAdapter(context);
                if (adapter == null) return result;

                var bonded = adapter.BondedDevices;
                if (bonded == null) return result;

                foreach (var device in bonded)
                {
                    if (device == null) continue;
                    string address = device.Address ?? "";
                    if (string.IsNullOrEmpty(address)) continue;

                    string name;
                    try { name = device.Name ?? address; }
                    catch { name = address; }   // 部分机型未授权时读名字会抛异常

                    result.Add(new PairedDevice(name, address));
                }
            }
            catch { }

            return result;
        }

        /// <summary>
        /// 连接指定 MAC 地址的 SPP 服务。阻塞调用，必须在后台线程执行。
        /// 前置条件：设备已与本机配对，且 PC 端已启动 WheelSimu 蓝牙服务。
        /// </summary>
        public void Connect(Context context, string address)
        {
            var adapter = GetAdapter(context);
            if (adapter == null) throw new InvalidOperationException("本机无蓝牙适配器");
            if (!adapter.IsEnabled) throw new InvalidOperationException("蓝牙未开启");

            Close();

            var device = adapter.GetRemoteDevice(address);
            if (device == null) throw new InvalidOperationException("找不到设备 " + address);

            // 扫描会明显拖慢 RFCOMM 连接，连接前先停掉
            try { adapter.CancelDiscovery(); } catch { }

            var socket = device.CreateRfcommSocketToServiceRecord(SppUuid);
            if (socket == null) throw new InvalidOperationException("创建 RFCOMM 通道失败");

            socket.Connect();

            _socket = socket;
            _output = socket.OutputStream;
            if (_output == null)
            {
                socket.Close();
                _socket = null;
                throw new InvalidOperationException("获取输出流失败");
            }
        }

        /// <summary>发送一帧数据（调用方已保证缓冲区内容有效）</summary>
        public void Send(byte[] buffer, int length)
        {
            var output = _output;
            if (output == null || length <= 0) return;

            try
            {
                output.Write(buffer, 0, length);
                output.Flush();   // SPP 不刷可能攒包，导致 PC 端延迟抖动
            }
            catch (Java.IO.IOException)
            {
                throw;            // 交给上层判定为掉线
            }
        }

        public void Close()
        {
            try { _output?.Flush(); } catch { }
            try { _output?.Close(); } catch { }
            try { _socket?.Close(); } catch { }
            _output = null;
            _socket = null;
        }

        public void Dispose() => Close();
    }
}
