# WheelSimu — Turn Your Phone into a Steering Wheel

WheelSimu lets you use your **Android phone as a steering wheel** for PC racing games:

- **Android app** senses the wheel angle via gravity/gyroscope sensors and simulates throttle/brake/gear pedals on screen;
- **PC server** receives the data and outputs it to games as a **WinUHid virtual device** (Xbox One wheel or gamepad);
- No physical wheel needed — just mount your phone on a rig and play.

```
Phone (WheelSimu App)               PC (WheelSimuServer)                 Game
┌────────────────┐  TCP/WiFi  ┌──────────────────────────────┐  HID   ┌────────┐
│ Gyro angle      │ ──────────→ │ angle/throttle/brake/gears   │ ─────→ │ sees a  │
│ Touch pedals    │            │ Output: WinUHid Wheel/Gamepad│        │ wheel/  │
│ Shift buttons   │            │ (switchable from dropdown)   │        │ pad     │
└────────────────┘            └──────────────────────────────┘        └────────┘
```

---

## Features

- **Wheel simulation**: gyroscope/accelerometer detects 540°–900° steering angle, mapped in real time
- **Three-pedal system**: independent throttle, brake and clutch pedals with touch control
- **Gear control**: upshift / downshift buttons (mapped to paddle RB/LB or wheel buttons 1/2)
- **Handbrake**: one-touch switch (mapped to B)
- **Two output modes** (switchable from the top dropdown on the PC):
  - **WinUHid (Wheel)**: virtual steering wheel (XInput Xbox One wheel layout), X-axis steering + three independent pedal channels, **recommended default**
  - **WinUHid (Xbox One)**: virtual Xbox One gamepad, XInput, works with modern games (Forza / WRC / F1 / GTA / ETS2, etc.)
- **Zero-deployment**: WinUHid driver is embedded in the EXE and auto-installed on first run
- **Network**: TCP direct connection + UDP LAN auto-discovery + auto-reconnect
- **System tray**: minimize to tray and keep running in the background

---

## System Requirements

### Android
- Android 4.3 (API 18) or newer
- Accelerometer / gyroscope sensor support
- Same LAN as the PC (WiFi or hotspot)

### PC (Windows)
- Windows 10 / 11 (64-bit)
- `WheelSimuServer.exe` is a self-contained build, no .NET runtime required
- **Admin rights required** (UAC prompt appears automatically, since WinUHid virtual devices are admin-only)

### Driver
| Output mode | Driver | Notes |
|---|---|---|
| **WinUHid (Wheel / Xbox One)** | `WinUHidDriver.dll` (UMDF 2.23 + VHF) | **Embedded in EXE, auto-installed**. On first run it enables test-signing and installs the driver (one reboot may be needed). Success = `WinUHid Virtual HID Enumerator` appears in Device Manager |

> No manual driver installation needed: INF/DLL/CAT/CER are packaged inside `WheelSimuServer.exe`. Both output modes share the same driver and can be switched at runtime freely.

---

## Installation

### Step 1: Install the PC server (WinUHid driver is fully automatic)

`Release/WheelSimuServer.exe` (self-contained, driver embedded):
1. Double-click and accept the UAC prompt;
2. If the driver is not installed, the program will: enable Windows test-signing mode (`bcdedit /set testsigning on`), ask you to **reboot once**, then auto-import the certificate and install the driver;
3. After that, just double-click and play.

> The single manual step is the one-time reboot required to activate test-signing.

### Step 2: Install the Android app

Install `Release/WheelSimu.apk` on your phone (allow "install from unknown sources").

---

## Usage

### 1. Start the PC server
1. Double-click `WheelSimuServer.exe` (must run as administrator);
2. The server auto-installs/checks the WinUHid driver and listens on **TCP 5050** (data) with UDP **5051** broadcast (discovery);
3. Bottom status bar shows: `IP: 192.168.x.x:5050`, `客户端/Client: 0`, `消息/Messages: 0`.

### 2. Connect the phone
- **Auto-discovery**: keep the phone on the same LAN, open the app, and it will auto-fill the server IP found via broadcast, then tap Connect.
- **Manual**: enter `192.168.x.x:5050` in the app and tap Connect.

### 3. Choose the output mode
Use the dropdown at the top-right of the server window:

| Option | Meaning | Use case |
|---|---|---|
| `WinUHid (Wheel)` | Virtual steering wheel (XInput) | **Default**, three pedals + full gear buttons |
| `WinUHid (Xbox One)` | Virtual Xbox One gamepad (XInput) | Games that only recognize a standard gamepad |

### 4. Play
1. Mount the phone on a rig, open the app and connect;
2. In game settings you should see an **Xbox One wheel** or **Xbox One gamepad**;
3. Rotate the phone — the in-game wheel follows.

---

## Control Mapping

| Game action | Phone action | WinUHid (Wheel) | WinUHid (Xbox One) |
|---|---|---|---|
| Steering | tilt/rotate phone | Wheel X-axis (1:1 angle) | Left stick X |
| Throttle | hold throttle pedal | Throttle channel | Right trigger (RT) |
| Brake | hold brake pedal | Brake channel | Left trigger (LT) |
| Clutch | hold clutch pedal (manual) | Clutch channel | Right stick Y |
| Upshift | tap upshift button | Button 1 | RB (right paddle) |
| Downshift | tap downshift button | Button 2 | LB (left paddle) |
| Handbrake | handbrake switch | Button 3 | B |
| Auto D | gear switch D | Button 4 (throttle held full) | Y (throttle held full) |
| Auto R | gear switch R | Button 5 (throttle held full) | X (throttle held full) |
| Manual R | manual reverse | Button 6 | Back |
| Manual 1–6 | manual gears 1–6 | Buttons 7–12 | A/X/Y/LB/RB/Menu |

---

## Communication Protocol

Server listens on **TCP 5050**; data is single-line JSON ending with `\n`:

```json
{"type":"control","angle":45.5,"throttle":75,"brake":0,"clutch":0,"handbrake":0,"gearUp":0,"gearDown":0}
```

| Field | Meaning | Range |
|---|---|---|
| `angle` | steering angle (deg) | approx. -450 ~ +450 |
| `throttle` | throttle | 0–100 |
| `brake` | brake | 0–100 |
| `clutch` | clutch | 0–100 |
| `handbrake` | handbrake | 0 / 1 |
| `gearUp` / `gearDown` | shift pulses | 0 / 1 |

LAN discovery: the server broadcasts `WHEELSIMU_SERVER:<ip>:<port>` over UDP **5051** every second.

---

## Build

### PC Server

```bash
cd WheelSimuServer
dotnet publish -c Release
```

### Android

Open `WheelSimu.sln` in Visual Studio and build with the Release configuration.

---

## FAQ

**Q1: UAC prompt on startup?**
A: Normal — WinUHid virtual devices are admin-only. Click "Yes".

**Q2: "Driver unavailable / switch failed"?**
A: Driver not installed or test-signing off. Normally auto-installed on first run; if not, check admin rights, reboot once, or reinstall per Step 1.

**Q3: Game doesn't see the device / no response?**
A: Check the server log for "WinUHid ... created", rescan devices in game settings, and check `BTN=0x…` in the status line for button output.

**Q4: Phone can't connect?**
A: Same LAN? Allow TCP 5050 and UDP 5051 through Windows Firewall (accept the first-run prompt), or enter the IP manually.

**Q5: Steering direction reversed?**
A: Mount the phone the other way, or invert the sensor direction in the app.

**Q6: Phone rotates 90° but game only 45°?**
A: Fixed in the new version (equivalent ratio `angle/450`, wheel mode maps 1:1). Update `Release/WheelSimuServer.exe`.

---

## License

MIT License.

## Acknowledgements

- [WinUHid (lurebat)](https://github.com/lurebat/WinUHid) — virtual HID framework + Xbox One preset driver
- [Xamarin.Android](https://dotnet.microsoft.com/en-us/apps/xamarin/android) — Android app framework
