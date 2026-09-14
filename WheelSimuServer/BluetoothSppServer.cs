using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace WheelSimuServer;

/// <summary>
/// 经典蓝牙 SPP (RFCOMM) 服务端：PC 广播标准「串行端口」服务，手机先与 PC 配对，
/// 再由手机端主动连接过来。收到的数据与 TCP 通道完全同构（以 '@' 结尾的文本帧），
/// 因此上层解析与 WinUHid 输出链路无需区分数据来源。
/// </summary>
public sealed class BluetoothSppServer : IDisposable
{
    /// <summary>
    /// 项目专用 RFCOMM 服务 UUID，必须与手机端 BluetoothSppClient.SppUuid 完全一致。
    /// 刻意不用标准串口 UUID(00001101-...)：Windows 上若已存在蓝牙串口 COM，
    /// 用标准 UUID 广播会与系统服务冲突导致注册失败。
    /// </summary>
    public static readonly Guid ServiceUuid =
        Guid.Parse("8D358FC4-B4F8-422C-9FE6-BD384843220A");

    /// <summary>广播出去的服务名（手机端配对列表里可见）</summary>
    public const string ServiceDisplayName = "WheelSimu";

    // SDP 属性：0x0100 = ServiceName，类型 (4&lt;&lt;3)|5 = 文本 + 8 位长度前缀
    const ushort SdpServiceNameAttributeId = 0x0100;
    const byte SdpServiceNameAttributeType = (4 << 3) | 5;

    RfcommServiceProvider? _provider;
    StreamSocketListener? _listener;
    bool _started;

    /// <summary>是否已成功开始广播</summary>
    public bool IsRunning => _started;

    /// <summary>启动结果说明（未启用时给出原因）</summary>
    public string StatusText { get; private set; } = "未启动";

    /// <summary>手机通过蓝牙连入时触发（参数为连接对象，使用后需 Dispose）</summary>
    public event Action<BluetoothSppConnection>? ClientConnected;

    /// <summary>
    /// 启动 SPP 服务。失败时不抛异常，仅把原因写入 <see cref="StatusText"/>。
    /// </summary>
    public async Task<bool> StartAsync()
    {
        if (_started) return true;

        try
        {
            // 1) 蓝牙适配器检测
            BluetoothAdapter? adapter;
            try { adapter = await BluetoothAdapter.GetDefaultAsync(); }
            catch (Exception ex)
            {
                StatusText = "蓝牙不可用: " + ex.Message;
                return false;
            }

            if (adapter == null)
            {
                StatusText = "未检测到蓝牙适配器";
                return false;
            }
            if (!adapter.IsClassicSupported)
            {
                StatusText = "适配器不支持经典蓝牙 (SPP)";
                return false;
            }

            // 2) 创建 RFCOMM 服务并绑定监听（ServiceId 由系统分配，含通道号）
            _provider = await RfcommServiceProvider.CreateAsync(
                RfcommServiceId.FromUuid(ServiceUuid));
            if (_provider == null)
            {
                StatusText = "RFCOMM 服务创建失败";
                return false;
            }

            _listener = new StreamSocketListener();
            _listener.ConnectionReceived += OnConnectionReceived;

            await _listener.BindServiceNameAsync(
                _provider.ServiceId.AsString(),
                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);

            // 3) 写入 SDP 服务名并开始广播（SDP 记录由系统生成）
            SetSdpServiceName(_provider);
            _provider.StartAdvertising(_listener);

            _started = true;
            StatusText = $"已广播 {ServiceDisplayName} (SPP)";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = "蓝牙启动失败: " + ex.Message;
            _started = false;
            return false;
        }
    }

    static void SetSdpServiceName(RfcommServiceProvider provider)
    {
        try
        {
            using var writer = new DataWriter();
            writer.WriteByte(SdpServiceNameAttributeType);
            writer.WriteByte((byte)ServiceDisplayName.Length);
            writer.UnicodeEncoding = UnicodeEncoding.Utf8;
            writer.WriteString(ServiceDisplayName);
            provider.SdpRawAttributes[SdpServiceNameAttributeId] = writer.DetachBuffer();
        }
        catch
        {
            // 服务名写不进去不影响连接，仅影响手机端显示的名称
        }
    }

    void OnConnectionReceived(StreamSocketListener sender,
                              StreamSocketListenerConnectionReceivedEventArgs args)
    {
        BluetoothSppConnection? conn = null;
        try
        {
            conn = new BluetoothSppConnection(args.Socket);
            ClientConnected?.Invoke(conn);
        }
        catch
        {
            conn?.Dispose();
        }
    }

    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        try { _provider?.StopAdvertising(); } catch { }
        try { _listener?.Dispose(); } catch { }
        _provider = null;
        _listener = null;
        StatusText = "已停止";
    }
}

/// <summary>
/// 一条蓝牙 SPP 连接：对外暴露只读数据流（协议为单向遥测，PC 不需要回发数据），
/// Dispose 时释放底层 <see cref="StreamSocket"/>。
/// </summary>
public sealed class BluetoothSppConnection : IDisposable
{
    public StreamSocket Socket { get; }
    public Stream ReadStream { get; }
    public string RemoteName { get; }

    public BluetoothSppConnection(StreamSocket socket)
    {
        Socket = socket;
        ReadStream = socket.InputStream.AsStreamForRead();
        RemoteName = TryGetRemoteName(socket);
    }

    static string TryGetRemoteName(StreamSocket socket)
    {
        try
        {
            var host = socket.Information?.RemoteHostName;
            var service = socket.Information?.RemoteServiceName;
            if (host == null) return "蓝牙设备";
            return string.IsNullOrEmpty(service)
                ? host.DisplayName
                : $"{host.DisplayName}:{service}";
        }
        catch
        {
            return "蓝牙设备";
        }
    }

    public void Dispose()
    {
        try { ReadStream.Dispose(); } catch { }
        try { Socket.Dispose(); } catch { }
    }
}
