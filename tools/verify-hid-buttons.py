"""读取 WheelSimu 虚拟方向盘（VID_046D&PID_C262）的 HID 输入报告，
按时间线打印 X/Y/Z/Rx 轴与按钮位，用于验证「数据传输开关」改动是否生效
（开关关掉后按钮/踏板数据是否仍在上传）。

报告布局（Windows 侧读出 13 字节，byte0 = Report ID = 0）:
    buf[1:3]   X  转向（居中 32768）    buf[3:5]   Y  油门
    buf[5:7]   Z  刹车                  buf[7:9]   Rx 离合
    buf[9:13]  buttons (uint32 LE)      bit0=升挡 bit1=降挡 bit2=手刹

用法: python verify-hid-buttons.py [采集秒数，默认 8]
"""
import ctypes
import ctypes.wintypes as wt
import struct
import sys
import threading
import time

setupapi = ctypes.WinDLL('setupapi', use_last_error=True)
kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)

setupapi.SetupDiGetClassDevsW.restype = ctypes.c_ssize_t
setupapi.SetupDiGetClassDevsW.argtypes = [ctypes.c_void_p, wt.LPCWSTR, wt.HWND, wt.DWORD]
setupapi.SetupDiEnumDeviceInterfaces.restype = wt.BOOL
setupapi.SetupDiEnumDeviceInterfaces.argtypes = [ctypes.c_void_p, ctypes.c_void_p,
                                                 ctypes.c_void_p, wt.DWORD, ctypes.c_void_p]
setupapi.SetupDiGetDeviceInterfaceDetailW.restype = wt.BOOL
setupapi.SetupDiGetDeviceInterfaceDetailW.argtypes = [ctypes.c_void_p, ctypes.c_void_p,
                                                      ctypes.c_void_p, wt.DWORD,
                                                      ctypes.POINTER(wt.DWORD), ctypes.c_void_p]
setupapi.SetupDiDestroyDeviceInfoList.argtypes = [ctypes.c_void_p]
kernel32.CreateFileW.restype = ctypes.c_ssize_t
kernel32.CreateFileW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.c_void_p,
                                 wt.DWORD, wt.DWORD, ctypes.c_void_p]
kernel32.ReadFile.restype = wt.BOOL
kernel32.ReadFile.argtypes = [ctypes.c_void_p, ctypes.c_void_p, wt.DWORD,
                              ctypes.POINTER(wt.DWORD), ctypes.c_void_p]
kernel32.CreateEventW.restype = ctypes.c_ssize_t
kernel32.CreateEventW.argtypes = [ctypes.c_void_p, wt.BOOL, wt.BOOL, wt.LPCWSTR]
kernel32.WaitForSingleObject.restype = wt.DWORD
kernel32.WaitForSingleObject.argtypes = [ctypes.c_void_p, wt.DWORD]
kernel32.GetOverlappedResult.restype = wt.BOOL
kernel32.GetOverlappedResult.argtypes = [ctypes.c_void_p, ctypes.c_void_p,
                                         ctypes.POINTER(wt.DWORD), wt.BOOL]

DIGCF_PRESENT = 0x02
DIGCF_DEVICEINTERFACE = 0x10
GENERIC_READ = 0x80000000
FILE_FLAG_OVERLAPPED = 0x40000000
FILE_SHARE_READ = 0x01
FILE_SHARE_WRITE = 0x02
OPEN_EXISTING = 3
ERROR_IO_PENDING = 997
WAIT_TIMEOUT = 0x102


class GUID(ctypes.Structure):
    _fields_ = [("Data1", wt.DWORD), ("Data2", wt.WORD), ("Data3", wt.WORD),
                ("Data4", ctypes.c_ubyte * 8)]


class SP_DEVICE_INTERFACE_DATA(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("InterfaceClassGuid", GUID),
                ("Flags", wt.DWORD), ("Reserved", ctypes.c_void_p)]


class OVERLAPPED(ctypes.Structure):
    _fields_ = [("Internal", ctypes.c_ssize_t), ("InternalHigh", ctypes.c_ssize_t),
                ("Offset", wt.DWORD), ("OffsetHigh", wt.DWORD), ("hEvent", ctypes.c_ssize_t)]


GUID_DEVINTERFACE_HID = GUID(0x4D1E55B2, 0xF16F, 0x11CF,
                             (ctypes.c_ubyte * 8)(0x88, 0xCB, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30))


def find_device_paths():
    hdev = setupapi.SetupDiGetClassDevsW(ctypes.byref(GUID_DEVINTERFACE_HID), None, None,
                                         DIGCF_PRESENT | DIGCF_DEVICEINTERFACE)
    if hdev == -1:
        print("SetupDiGetClassDevs 失败", ctypes.get_last_error())
        return []
    paths = []
    did = SP_DEVICE_INTERFACE_DATA()
    did.cbSize = ctypes.sizeof(SP_DEVICE_INTERFACE_DATA)
    i = 0
    while setupapi.SetupDiEnumDeviceInterfaces(hdev, None, ctypes.byref(GUID_DEVINTERFACE_HID), i,
                                               ctypes.byref(did)):
        req = wt.DWORD(0)
        setupapi.SetupDiGetDeviceInterfaceDetailW(hdev, ctypes.byref(did), None, 0,
                                                  ctypes.byref(req), None)
        buf = ctypes.create_string_buffer(req.value)
        ctypes.cast(buf, ctypes.POINTER(wt.DWORD))[0] = 8
        if setupapi.SetupDiGetDeviceInterfaceDetailW(hdev, ctypes.byref(did), buf, req.value,
                                                     ctypes.byref(req), None):
            paths.append(ctypes.wstring_at(ctypes.addressof(buf) + 4))
        i += 1
    setupapi.SetupDiDestroyDeviceInfoList(hdev)
    return paths


def open_hid(path):
    h = kernel32.CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                             None, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, None)
    if h == -1:
        return None
    return h


samples = []
stop_flag = threading.Event()
events = []


def reader(handle):
    ev = kernel32.CreateEventW(None, True, False, None)
    buf = ctypes.create_string_buffer(64)
    nread = wt.DWORD(0)
    n_ok = n_timeout = n_fail = 0
    while not stop_flag.is_set():
        ov = OVERLAPPED()
        ov.hEvent = ev
        # 请求 64 字节：报告含 Report ID 共 13 字节，请求长度小于该值会报 1784
        ok = kernel32.ReadFile(handle, buf, 64, ctypes.byref(nread), ctypes.byref(ov))
        if not ok:
            err = ctypes.get_last_error()
            if err != ERROR_IO_PENDING:
                n_fail += 1
                if n_fail <= 3:
                    events.append(f"ReadFile 立即失败 err={err} (0x{err:X})")
                continue
            r = kernel32.WaitForSingleObject(ev, 500)
            if r == WAIT_TIMEOUT:
                n_timeout += 1
                if n_timeout <= 3:
                    events.append("等待报告超时 500ms（设备无输入报告）")
                kernel32.CancelIo(handle)
                continue
            got = wt.DWORD(0)
            if not kernel32.GetOverlappedResult(handle, ctypes.byref(ov), ctypes.byref(got), False):
                n_fail += 1
                continue
            nread = got
        if nread.value:
            samples.append((time.time(), bytes(buf.raw[:nread.value])))
            n_ok += 1
    events.append(f"读取统计: 成功={n_ok} 超时={n_timeout} 失败={n_fail}")


def parse(b):
    x = struct.unpack_from("<H", b, 1)[0]
    y = struct.unpack_from("<H", b, 3)[0]
    z = struct.unpack_from("<H", b, 5)[0]
    rx = struct.unpack_from("<H", b, 7)[0]
    btn = struct.unpack_from("<I", b, 9)[0]
    return x, y, z, rx, btn


def main():
    seconds = float(sys.argv[1]) if len(sys.argv) > 1 else 8.0

    paths = find_device_paths()
    target = [p for p in paths if "vid_046d&pid_c262" in p.lower()]
    print(f"枚举到 HID 接口 {len(paths)} 个，匹配 G920/C262 的 {len(target)} 个")
    if not target:
        print("!! 找不到虚拟方向盘设备接口（服务端是否在跑？设备是否为方向盘模式？）")
        sys.stdout.flush()
        import os
        os._exit(1)

    handle = open_hid(target[0])
    if handle is None:
        print("!! 打开设备失败 err=", ctypes.get_last_error())
        sys.stdout.flush()
        import os
        os._exit(1)
    print(f"设备已打开，采集 {seconds:.1f} 秒...\n")

    th = threading.Thread(target=reader, args=(handle,), daemon=True)
    th.start()

    t0 = time.time()
    last_slot = -1
    while time.time() - t0 < seconds:
        time.sleep(0.05)
        slot = int((time.time() - t0) * 2)          # 每 0.5s 输出一次
        if slot != last_slot:
            last_slot = slot
            seg = [b for (t, b) in samples if t >= t0 + slot / 2.0]
            if seg:
                x, y, z, rx, btn = parse(seg[-1])
                marks = []
                if btn & 0x01: marks.append("升挡")
                if btn & 0x02: marks.append("降挡")
                if btn & 0x04: marks.append("手刹")
                if btn & 0x08: marks.append("D挡")
                if btn & 0x10: marks.append("R挡")
                if btn & 0x20: marks.append("N挡")
                print(f"  t={slot/2.0:4.1f}s  X={x:5d} Y={y:5d} Z={z:5d} Rx={rx:5d} "
                      f"buttons=0x{btn:08X} [{'+'.join(marks) if marks else '无'}]")

    stop_flag.set()
    time.sleep(0.6)

    print("\n=== 汇总 ===")
    if not samples:
        print("  未读到任何报告")
    else:
        btns = [parse(b)[4] for (t, b) in samples]
        xs = [parse(b)[0] for (t, b) in samples]
        print(f"  报告数={len(samples)}  X范围=[{min(xs)},{max(xs)}]")
        print(f"  按钮值出现过的集合: {sorted(set(btns))}  (0=全部松开)")
        print(f"  手刹位(bit2)为1的报告数: {sum(1 for v in btns if v & 0x04)}")
    print("\n诊断事件:")
    for e in events:
        print("   -", e)
    sys.stdout.flush()
    import os
    os._exit(0)


if __name__ == "__main__":
    main()
