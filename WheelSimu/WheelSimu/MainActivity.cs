using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Android.App;
using Android.OS;
using Android.Runtime;
using Android.Content;
using AndroidX.AppCompat.App;
using Android.Views;
using Android.Widget;
using Android.Hardware;
using Android.Net.Wifi;

namespace WheelSimu
{


    [Activity(Label = "@string/app_name", Theme = "@style/AppTheme.NoActionBar", MainLauncher = true,
        ScreenOrientation = Android.Content.PM.ScreenOrientation.Landscape,
        ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation | Android.Content.PM.ConfigChanges.ScreenSize | Android.Content.PM.ConfigChanges.KeyboardHidden)]



    public class MainActivity : AppCompatActivity, ISensorEventListener
    {
        //^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^全局参数声明^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
        TextView textView1;
        TextView textView2;
        TextView textView3;
        TextView textView4;
        TextView textView5;
        TextView textView_Ver;
        //TextView textView6;

        EditText IPText;
        Button btnConnect;
        Button btnSet;
        Button btnReset;
        Button btnSetSrd;
        Button btnSetSru;
        Button btnGearUp;
        Button btnGearDown;
        Button btnNetMode;
        Button btnLayoutSwitch;  // 布局切换按钮
        Button btnGearMode;      // 档位模式切换按钮
        Button btnAutoP;         // 真实自动挡 P(驻车) 拨动开关
        Button btnAutoD;         // 真实自动挡 D(前进) 拨动开关
        Button btnAutoN;         // 真实自动挡 N(空挡) 拨动开关
        Button btnAutoR;         // 真实自动挡 R(倒车) 拨动开关
        Button btnGearR;         // 5档手动挡 R
        Button btnGearN;         // 5档手动挡 N(空挡)
        Button[] btnManualGears; // 5档手动挡 1..5
        Button btn6GearR;        // 6档手动挡 R
        Button btn6GearN;        // 6档手动挡 N(空挡)
        Button[] btn6ManualGears; // 6档手动挡 1..6

        // 布局2: Xbox 手柄触屏按钮（其它布局下为 null，均判空保护）
        Button btnPadA, btnPadB, btnPadX, btnPadY;           // 面键
        Button btnPadLB, btnPadRB, btnPadLT, btnPadRT;       // 肩键/扳机（LT=刹车 RT=油门）
        Button btnPadBack, btnPadMenu, btnPadLS, btnPadRS;   // 中部功能键
        Button btnPadUp, btnPadDown, btnPadLeft, btnPadRight; // 十字键

        // 手动挡已改为“点选锁定”（模拟真实 H 挡硬件：拨杆卡入挡槽即保持），
        // 当前挡位由 _manualGearSelected 统一维护，不再需要按下布尔数组
        Switch SteerEnableSwitch;
        Button HandbrakeSwitch;
        SteeringWheelView steeringWheel;

        /// <summary>连接模式: 0=TCP, 1=UDP, 2=蓝牙, 3=USB(adb 端口转发)</summary>
        private int mConnectMode = 0;

        private const int MODE_TCP = 0;
        private const int MODE_UDP = 1;
        private const int MODE_BT = 2;
        private const int MODE_USB = 3;
        private const int MODE_COUNT = 4;

        /// <summary>各模式按钮/状态栏显示名，顺序必须与 MODE_* 一致</summary>
        private static readonly string[] ModeLabels = { "TCP", "UDP", "蓝牙", "USB" };

        /// <summary>
        /// USB 有线模式的固定端点。PC 端服务端会自动执行
        /// <c>adb reverse tcp:25050 tcp:25050</c>，把 PC 的 25050 反向映射到手机本机，
        /// 所以手机端连 127.0.0.1 就等于连到 PC。
        /// </summary>
        private const string USB_ENDPOINT = "127.0.0.1:25050";

        /// <summary>切进 USB 模式前的 IP 输入内容，切出时还原</summary>
        private string _ipBeforeUsb;

        /// <summary>布局模式: 0=赛车HUD, 1=模拟方向盘, 2=Xbox手柄</summary>
        private int _layoutMode = 0;

        /// <summary>档位模式: 0=简易档(仅油门刹车), 1=真实自动挡(D/R拨动开关), 2=序列挡, 3=5档手动挡, 4=6档手动挡</summary>
        private int _gearMode = 0;

        /// <summary>真实自动挡拨动开关: 0=N(空挡), 1=D(前进), -1=R(倒车), 2=P(驻车)</summary>
        private int _autoDrSelected = 0;

        /// <summary>手动挡当前挂挡: -1=R, 0=N, 1..6</summary>
        private int _manualGearSelected = 0;

        static readonly string[] kGearModeNames = { "简易档", "真实自动挡", "序列挡", "5档手动挡", "6档手动挡" };

        // 踏板垂直进度条
        PedalGaugeView gaugeThrottle;
        PedalGaugeView gaugeBrake;
        PedalGaugeView gaugeClutch;

        //ImageView iviewCoordinate;

        public Socket[] Sct = new Socket[2];
        public Thread[] Trd = new Thread[1];
        public struct IPFormat
        {
            public string IP;
            public int Port;
        };
        public IPFormat[] IPData = new IPFormat[2];
        public int TryTimes = 1;
        //int sClearAngle = 0;  改成在手机端清零
        bool IsConnected = false;

        //Sensor
        string AccelerometerData1;
        string AccelerometerData2;
        double AcX1, AcY1, AcZ1;
        double AcX2, AcY2, AcZ2;
        double TmpX = 0;
        double Hp = 0; //Hemisphere 方向盘大于+-90度的情况
        readonly double gAngle = 90 / 9.8; //一单位g值对应角度
        private readonly object sensorLock = new object();

        private SensorManager mSensorManager;
        //SensorMode = 0 Xamarin ; 1 android.Hardware ; 2,3 混合模式
        readonly int SensorMode = 1;

        // 定时器替代忙等轮询
        private System.Threading.Timer sendTimer;
        private readonly int sendIntervalMs = 10; // 100Hz 发送频率
        private volatile bool steerEnabled = false;

        // === 性能优化：UI 刷新节流 + 后台发送 ===
        private int _tickCounter;
        private const int UI_REFRESH_EVERY = 8;  // 每 8 tick (~80ms) 刷新一次文字 UI
        private readonly byte[] _sendBuf = new byte[256];  // 预分配发送缓冲区，避免 GC
        private volatile int _latestThrottle, _latestBrake, _latestClutch, _latestHb;
        private volatile int _latestGearUp, _latestGearDn, _latestGear, _latestSet, _latestSetSR;
        private volatile int _latestGearMode, _latestDr, _latestGearValue;
        private volatile float _latestAngle;

        // UDP 服务自动发现
        const int DISCOVERY_PORT = 5051;
        const string DISCOVERY_MAGIC = "WHEELSIMU_SERVER";
        private CancellationTokenSource mDiscoverCts;
        private volatile string mDiscoveredServer = null;

        // 自动重连
        private volatile bool mAutoReconnect = true;
        private volatile bool mConnecting;   // 连接防重入（连接在后台线程执行，不阻塞 UI）

        // ==================== 蓝牙模式 (SPP) ====================
        private BluetoothSppClient _btClient;
        private string _btDeviceAddress;     // 上次连接的 PC 蓝牙 MAC
        private const int REQ_BT_PERMISSION = 1001;

        //vvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvv全局参数声明vvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvv


        protected override void OnCreate(Bundle savedInstanceState)
        {
            // 全局未处理异常捕获
            AndroidEnvironment.UnhandledExceptionRaiser += (s, e) =>
            {
                try
                {
                    var log = $"[{DateTime.Now}] Crash: {e.Exception}";
                    File.WriteAllText(Path.Combine(CacheDir.AbsolutePath, "crash.log"), log);
                }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    var log = $"[{DateTime.Now}] AppDomain Crash: {((Exception)e.ExceptionObject)}";
                    File.WriteAllText(Path.Combine(CacheDir.AbsolutePath, "crash.log"), log);
                }
                catch { }
            };

            try
            {
                base.OnCreate(savedInstanceState);

                // 读取布局偏好：0=赛车HUD(content_main), 1=模拟方向盘(content_wheel), 2=Xbox手柄(content_gamepad)
                var prefs = GetSharedPreferences("WheelSimuPrefs", FileCreationMode.Private);
                _layoutMode = prefs.GetInt("LayoutMode", 0);
                if (_layoutMode < 0 || _layoutMode > 2) _layoutMode = 0;
                _gearMode = prefs.GetInt("GearMode", 0);
                if (_gearMode < 0 || _gearMode > 4) _gearMode = 0;
                _btDeviceAddress = prefs.GetString("LastBT", "");   // 蓝牙模式上次连接的 PC
                SetContentView(_layoutMode switch
                {
                    1 => Resource.Layout.activity_wheel,
                    2 => Resource.Layout.activity_gamepad,
                    _ => Resource.Layout.activity_main,
                });


            //保持屏幕常亮
            Window.AddFlags(WindowManagerFlags.KeepScreenOn);

            //^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^控件实例化^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
            // Get our UI controls from the loaded layout
            textView1 = FindViewById<TextView>(Resource.Id.textView1);
            textView2 = FindViewById<TextView>(Resource.Id.textView2);
            textView3 = FindViewById<TextView>(Resource.Id.textView3);
            textView4 = FindViewById<TextView>(Resource.Id.textView4);
            textView5 = FindViewById<TextView>(Resource.Id.textView5);
            textView_Ver = FindViewById<TextView>(Resource.Id.textView_Ver);
            //textView6 = FindViewById<TextView>(Resource.Id.textView6);
            IPText = FindViewById<EditText>(Resource.Id.IPText1);

            // 读取已保存的IP地址
            IPText.Text = prefs.GetString("LastIP", "192.168.1.100:25050");

            btnConnect = FindViewById<Button>(Resource.Id.Connect);
            btnConnect.Text = "重连: 开";  // 初始状态：自动重连开启
            btnNetMode = FindViewById<Button>(Resource.Id.btnNetMode);

            // 恢复上次的连接模式（0=TCP 1=UDP 2=蓝牙 3=USB）
            mConnectMode = prefs.GetInt("ConnectMode", MODE_TCP);
            if (mConnectMode < 0 || mConnectMode >= MODE_COUNT) mConnectMode = MODE_TCP;
            btnNetMode.Text = ModeLabels[mConnectMode];
            if (mConnectMode == MODE_USB)
            {
                _ipBeforeUsb = IPText.Text;
                IPText.Text = USB_ENDPOINT;   // USB 端点固定，界面直接显示实际连接目标
            }

            btnLayoutSwitch = FindViewById<Button>(Resource.Id.btnLayoutSwitch);
            btnSet = FindViewById<Button>(Resource.Id.btnSet);
            btnReset = FindViewById<Button>(Resource.Id.btnReset);
            btnSetSrd = FindViewById<Button>(Resource.Id.btnSetSrd);
            btnSetSru = FindViewById<Button>(Resource.Id.btnSetSru);


            btnGearUp = FindViewById<Button>(Resource.Id.btnGearUp);
            btnGearDown = FindViewById<Button>(Resource.Id.btnGearDown);

            // 布局2: Xbox 手柄按钮（其它布局下为 null，发送循环判空）
            btnPadA = FindViewById<Button>(Resource.Id.btnPadA);
            btnPadB = FindViewById<Button>(Resource.Id.btnPadB);
            btnPadX = FindViewById<Button>(Resource.Id.btnPadX);
            btnPadY = FindViewById<Button>(Resource.Id.btnPadY);
            btnPadLB = FindViewById<Button>(Resource.Id.btnPadLB);
            btnPadRB = FindViewById<Button>(Resource.Id.btnPadRB);
            btnPadLT = FindViewById<Button>(Resource.Id.btnPadLT);
            btnPadRT = FindViewById<Button>(Resource.Id.btnPadRT);
            btnPadBack = FindViewById<Button>(Resource.Id.btnPadBack);
            btnPadMenu = FindViewById<Button>(Resource.Id.btnPadMenu);
            btnPadLS = FindViewById<Button>(Resource.Id.btnPadLS);
            btnPadRS = FindViewById<Button>(Resource.Id.btnPadRS);
            btnPadUp = FindViewById<Button>(Resource.Id.btnPadUp);
            btnPadDown = FindViewById<Button>(Resource.Id.btnPadDown);
            btnPadLeft = FindViewById<Button>(Resource.Id.btnPadLeft);
            btnPadRight = FindViewById<Button>(Resource.Id.btnPadRight);
            SteerEnableSwitch = FindViewById<Switch>(Resource.Id.SteerEnableSwitch);
            HandbrakeSwitch = FindViewById<Button>(Resource.Id.HandbrakeSwitch);
            InitGearControls();

            // 程序化创建方向盘视图
            var container = FindViewById<FrameLayout>(Resource.Id.steeringWheelContainer);
            steeringWheel = new SteeringWheelView(this);
            container.AddView(steeringWheel, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MatchParent, FrameLayout.LayoutParams.MatchParent));

            // 创建踏板垂直进度条 (油门=绿, 刹车=红, 离合=紫)
            var gaugeThrottleContainer = FindViewById<FrameLayout>(Resource.Id.gaugeThrottle);
            gaugeThrottle = new PedalGaugeView(this);
            gaugeThrottle.SetColors(
                Android.Graphics.Color.Rgb(56, 142, 60).ToArgb(),   // 绿
                Android.Graphics.Color.Rgb(27, 94, 32).ToArgb(),
                Android.Graphics.Color.Rgb(76, 175, 80).ToArgb()
            );
            gaugeThrottle.SetLabel("油门");
            gaugeThrottleContainer.AddView(gaugeThrottle, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MatchParent, FrameLayout.LayoutParams.MatchParent));

            var gaugeBrakeContainer = FindViewById<FrameLayout>(Resource.Id.gaugeBrake);
            gaugeBrake = new PedalGaugeView(this);
            gaugeBrake.SetColors(
                Android.Graphics.Color.Rgb(214, 47, 47).ToArgb(),   // 红
                Android.Graphics.Color.Rgb(142, 0, 0).ToArgb(),
                Android.Graphics.Color.Rgb(244, 67, 54).ToArgb()
            );
            gaugeBrake.SetLabel("刹车");
            gaugeBrakeContainer.AddView(gaugeBrake, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MatchParent, FrameLayout.LayoutParams.MatchParent));

            var gaugeClutchContainer = FindViewById<FrameLayout>(Resource.Id.gaugeClutch);
            gaugeClutch = new PedalGaugeView(this);
            gaugeClutch.SetColors(
                Android.Graphics.Color.Rgb(156, 39, 176).ToArgb(),   // 紫
                Android.Graphics.Color.Rgb(74, 20, 140).ToArgb(),
                Android.Graphics.Color.Rgb(171, 71, 188).ToArgb()
            );
            gaugeClutch.SetLabel("离合");
            gaugeClutchContainer.AddView(gaugeClutch, new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MatchParent, FrameLayout.LayoutParams.MatchParent));

            // 油门↔刹车互斥：上调一个自动归零另一个
            gaugeThrottle.LinkedPedal = gaugeBrake;
            gaugeBrake.LinkedPedal = gaugeThrottle;

            RunOnUiThread(() => textView1.Text = "");
            RunOnUiThread(() => textView2.Text = "");
            //RunOnUiThread(() => textView3.Text = "");
            RunOnUiThread(() => textView4.Text = "");
            RunOnUiThread(() => textView5.Text = "");

            // 使用 XML 中定义的中文文字
            textView_Ver.Text = PackageManager.GetPackageInfo(this.PackageName, 0).VersionName;
            //vvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvv控件实例化vvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvv



            //^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^事件接口设置^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
            btnConnect.Click += delegate
            {
                ThreadPool.QueueUserWorkItem(o => BtnConnect_OnClick());
            };

            btnNetMode.Click += delegate
            {
                BtnNetMode_OnClick();
            };

            btnLayoutSwitch.Click += delegate
            {
                // 切换布局模式并重启 Activity（0=赛车HUD → 1=方向盘 → 2=Xbox手柄 → 循环）
                _layoutMode = (_layoutMode + 1) % 3;
                var p = GetSharedPreferences("WheelSimuPrefs", FileCreationMode.Private);
                p.Edit().PutInt("LayoutMode", _layoutMode).Commit();
                // 保存当前IP（Recreate 会重新读取）；USB 模式下 IPText 是固定回环地址，不覆盖
                if (mConnectMode != MODE_USB)
                    p.Edit().PutString("LastIP", IPText.Text).Commit();
                Recreate();
            };

            if (btnGearMode != null)
            {
                btnGearMode.Click += delegate
                {
                    _gearMode = (_gearMode + 1) % 5;
                    GetSharedPreferences("WheelSimuPrefs", FileCreationMode.Private)
                        .Edit().PutInt("GearMode", _gearMode).Commit();
                    ApplyGearMode();
                };
            }

            SteerEnableSwitch.Click += delegate
            {
                ThreadPool.QueueUserWorkItem(o => SteerEnableSwitch_OnClick());
            };

            // 数据传输开关默认打开：启动即启用方向盘传感器并开始发送
            SteerEnableSwitch.Checked = true;
            SteerEnableSwitch_OnClick();

            //vvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvv事件接口设置vvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvv



            //^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^其他事件委托^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

            //vvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvv其他事件委托vvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvv

            // 启动 UDP 服务发现（后台监听广播）
            StartDiscovery();

            // 启动后自动尝试连接（延迟 1.5s 等 WiFi 就绪，优先用发现的服务器）
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(1500);

                // USB 模式：端点固定，不需要等 UDP 发现，直接连本机转发端口
                if (mConnectMode == MODE_USB)
                {
                    mDiscoveryInitialDone = true;
                    LogToUI($"USB 模式：连接 {USB_ENDPOINT} ...（需数据线连接 + 手机开 USB 调试）");
                    ConnectNow();
                    return;
                }

                // 等待 UDP 发现 3 秒
                var waited = 0;
                while (mDiscoveredServer == null && waited < 3000)
                {
                    Thread.Sleep(500);
                    waited += 500;
                }

                // 优先使用发现的服务器
                if (mDiscoveredServer != null)
                {
                    RunOnUiThread(() => IPText.Text = mDiscoveredServer);
                    LogToUI($"发现服务器: {mDiscoveredServer}");
                }

                // 蓝牙模式：不走 IP/UDP 发现，需先选已配对设备
                if (mConnectMode == MODE_BT)
                {
                    LogToUI("蓝牙模式：请先与 PC 配对，再点连接选择设备");
                    mDiscoveryInitialDone = true;
                    return;
                }

                // 只要有已保存的 IP 或者发现了服务器就自动连接
                string ip = IPText.Text?.Trim();
                if (!string.IsNullOrEmpty(ip))
                {
                    LogToUI($"自动连接 {ip} ...");
                    ConnectNow(ip);
                }
                else
                {
                    LogToUI("等待服务器广播... (请确保 PC 端已启动)");
                }

                // 标记初始发现阶段完成，之后发现的服务器可自动连接
                mDiscoveryInitialDone = true;
            });
            }
            catch (Exception ex)
            {
                try
                {
                    var log = $"[{DateTime.Now}] OnCreate Crash: {ex}";
                    File.WriteAllText(Path.Combine(CacheDir.AbsolutePath, "crash.log"), log);
                    RunOnUiThread(() =>
                        new Android.App.AlertDialog.Builder(this)
                            .SetTitle("崩溃")
                            .SetMessage(ex.ToString())
                            .SetPositiveButton("OK", (s, ev) => Finish())
                            .Show());
                }
                catch
                {
                    // 二次崩溃无法恢复
                }
                throw; // 重新抛出以触发全局处理器
            }
        }


        //启动传感器

        private void StartSensor(SensorType EnableSensorType)
        {

            try
            {

                mSensorManager = (SensorManager)this.GetSystemService(SensorService);
                if (mSensorManager == null)
                {
                    RunOnUiThread(() => textView3.Text = "UnsupportedOperationException");
                }

                Sensor mSensor = mSensorManager.GetDefaultSensor(EnableSensorType);

                if (mSensor == null)
                {
                    RunOnUiThread(() => textView3.Text = "设备" + EnableSensorType + "不支持");
                }

                bool isRegister = mSensorManager.RegisterListener(this, mSensor, SensorDelay.Ui);
                if (!isRegister)
                {
                    RunOnUiThread(() => textView3.Text = "Listener开启失败");
                }

            }
            catch (Exception ex)
            {

                RunOnUiThread(() => textView2.Text = ex.Message);

            }


        }


        public void OnAccuracyChanged(Sensor sensor, SensorStatus accuracy)
        {
            //RunOnUiThread(() => textView2.Text = "AccuracyChange=" + accuracy);
        }
        public void OnSensorChanged(SensorEvent e)
        {
            // Process Acceleration X, Y, and Z
            if (e.Sensor.StringType == Android.Hardware.Sensor.StringTypeAccelerometer || e.Sensor.StringType == Android.Hardware.Sensor.StringTypeGravity)
            {
                AcX1 = e.Values[0];
                AcY1 = e.Values[1];
                AcZ1 = e.Values[2];
                AccelerometerData1 = $" AcX: {AcX1.ToString("0.000")} \r\n AcY: {AcY1.ToString("0.000")} \r\n AcZ: {AcZ1.ToString("0.000")} ";
            }
            else if (e.Sensor.StringType == Android.Hardware.Sensor.StringTypeLinearAcceleration)
            {
                AcX2 = e.Values[0];
                AcY2 = e.Values[1];
                AcZ2 = e.Values[2];
                AccelerometerData2 = $" AcX: {AcX2.ToString("0.000")} \r\n AcY: {AcY2.ToString("0.000")} \r\n AcZ: {AcZ2.ToString("0.000")} ";
            }

            else
            {
                AccelerometerData2 = "UnDefined Type!";
            }
        }



        private void SteerEnableSwitch_OnClick()
        {
            try
            {
                if (SteerEnableSwitch.Checked)
                {
                    steerEnabled = true;
                    StartSensors();

                    // 启动定时发送
                    if (sendTimer == null)
                    {
                        sendTimer = new System.Threading.Timer(_ => SendControlData(), null, sendIntervalMs, sendIntervalMs);
                    }
                    else
                    {
                        sendTimer.Change(0, sendIntervalMs);
                    }
                }
                else
                {
                    steerEnabled = false;
                    StopSensors();
                    sendTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }
            catch (Exception ex)
            {
                RunOnUiThread(() => textView2.Text = ex.Message);
            }
        }

        private void StartSensors()
        {
            if (SensorMode == 1)
                StartSensor(Android.Hardware.SensorType.Gravity);
            if (SensorMode == 3)
            {
                StartSensor(Android.Hardware.SensorType.Accelerometer);
                StartSensor(Android.Hardware.SensorType.LinearAcceleration);
            }
        }

        private void StopSensors()
        {
            if (SensorMode == 1 || SensorMode == 3)
            {
                mSensorManager?.UnregisterListener(this);
            }
        }

        /// <summary>
        /// 在 UI 线程一次性读取所有控件状态，构建发送字节，后台发送。
        /// UI 文字更新节流到 ~12.5Hz（每 8 tick），减少 87.5% 的 UI 开销。
        /// 网络 Send 移到 ThreadPool 避免阻塞 UI 线程。
        /// </summary>
        private void SendControlData()
        {
            if (!steerEnabled) return;
            try
            {
                double angle;
                lock (sensorLock) { angle = GetWheelData(); }

                // 在 UI 线程读取控件状态 & 构建发送数据（一次性完成）
                RunOnUiThread(() =>
                {
                    // --- 读取所有控件状态 ---
                    _latestThrottle = (int)gaugeThrottle.Progress;
                    _latestBrake    = (int)gaugeBrake.Progress;
                    _latestClutch   = (int)gaugeClutch.Progress;
                    _latestHb       = HandbrakeSwitch.Pressed ? 1 : 0;
                    _latestGearUp   = btnGearUp.Pressed ? 1 : 0;
                    _latestGearDn   = btnGearDown.Pressed ? 1 : 0;
                    _latestGear     = btnGearUp.Pressed ? 1 : (btnGearDown.Pressed ? -1 : 0);
                    _latestSet      = btnSet.Pressed ? -1 : (btnReset.Pressed ? 1 : 0);
                    _latestSetSR    = btnSetSrd.Pressed ? -1 : (btnSetSru.Pressed ? 1 : 0);
                    // 档位模式相关状态
                    _latestGearMode = _gearMode;
                    if (_gearMode == 0)
                    {
                        // 简易档：仅油门/刹车，无任何挡位控制（游戏自己自动换挡）
                        _latestDr = 0;
                        _latestGearValue = 0;
                    }
                    else if (_gearMode == 1)
                    {
                        // 真实自动挡：拨动开关选 D 前进 / R 倒车（点按切换保持），换挡由游戏自动完成
                        _latestDr = _autoDrSelected;
                        _latestGearValue = 0;
                    }
                    else if (_gearMode == 3 || _gearMode == 4)
                    {
                        // 手动挡（5档/6档）：点选锁定，模拟真实 H 挡硬件
                        // “拨杆卡入挡槽即保持，直到拨走”——点一下选中并保持，点其它挡切换，点空挡回 N。
                        // 挡位由 Click 事件直接写入 _manualGearSelected，这里只负责上报。
                        _latestDr = 0;
                        _latestGearValue = _manualGearSelected;
                    }
                    else
                    {
                        _latestDr = 0;
                        _latestGearValue = 0;
                    }

                    if (_layoutMode == 2)
                    {
                        // Xbox 手柄布局：LT/RT = 扳机（按住=满量程），赛车挡位/手刹映射停用，
                        // 全部按钮走 K 位掩码直通（服务端按原始 Xbox 按钮输出）
                        _latestThrottle  = btnPadRT?.Pressed == true ? 100 : 0;
                        _latestBrake     = btnPadLT?.Pressed == true ? 100 : 0;
                        _latestClutch    = 0;
                        _latestHb        = 0;
                        _latestGearUp    = 0;
                        _latestGearDn    = 0;
                        _latestGear      = 0;
                        _latestSet       = 0;
                        _latestSetSR     = 0;
                        _latestGearMode  = 0;
                        _latestDr        = 0;
                        _latestGearValue = 0;
                    }
                    // 布局0/1 恒为 0 → 服务端走赛车映射；布局2 为触屏按钮位掩码
                    int padMask = _layoutMode == 2 ? ReadPadMask() : 0;
                    _latestAngle    = (float)angle;

                    // 方向盘角度每帧更新（动画平滑）
                    steeringWheel.Angle = (float)angle;

                    // --- UI 文字更新节流：每 8 tick (~80ms) 才刷新一次 ---
                    if (++_tickCounter >= UI_REFRESH_EVERY)
                    {
                        _tickCounter = 0;
                        textView5.Text = AccelerometerData1;
                        textView1.Text = AccelerometerData2;
                        textView4.Text = $"A={angle:0.0}  B={_latestBrake} T={_latestThrottle} C={_latestClutch} HB={_latestHb}";
                    }

                    // --- 构建发送数据到预分配缓冲区 + 发送 ---
                    if (IsConnected)
                    {
                        int len = BuildSendDataToBuffer(angle, _latestThrottle, _latestBrake, _latestClutch,
                            _latestGearUp, _latestGearDn, _latestGear, _latestSet, _latestSetSR, _latestHb,
                            _latestGearMode, _latestDr, _latestGearValue, padMask);
                        try
                        {
                            if (mConnectMode == MODE_BT)
                                _btClient?.Send(_sendBuf, len);   // 蓝牙 SPP
                            else
                                Sct[1]?.Send(_sendBuf, len, SocketFlags.None);
                        }
                        catch { IsConnected = false; OnConnectionLost(); }
                    }
                });
            }
            catch (Exception ex)
            {
                RunOnUiThread(() => textView2.Text = ex.Message);
            }
        }

        /// <summary>在 _sendBuf 中构建发送数据，返回有效字节数</summary>
        private int BuildSendDataToBuffer(double angle, int t, int b, int c, int gu, int gd, int g, int s, int sr, int h,
                                          int m, int dr, int gv, int k)
        {
            // K=按钮位掩码（布局2 Xbox 手柄直通）；旧版服务端会忽略未知键，向前向后兼容
            string data = $"A={angle:0.0},T={t},B={b},C={c},Gu={gu},Gd={gd},G={g},S={s},SR={sr},H={h},M={m},DR={dr},GV={gv},K={k}@";
            return Encoding.UTF8.GetBytes(data, 0, data.Length, _sendBuf, 0);
        }

        /// <summary>
        /// 布局2：读取 Xbox 手柄触屏按钮状态 → 按钮位掩码。
        /// bit0..7 = A B X Y LB RB Back Menu；bit8..9 = LS RS；
        /// bit10..13 = 十字 上 下 左 右；bit14 = Home。
        /// </summary>
        private int ReadPadMask()
        {
            int m = 0;
            if (btnPadA?.Pressed == true) m |= 1 << 0;
            if (btnPadB?.Pressed == true) m |= 1 << 1;
            if (btnPadX?.Pressed == true) m |= 1 << 2;
            if (btnPadY?.Pressed == true) m |= 1 << 3;
            if (btnPadLB?.Pressed == true) m |= 1 << 4;
            if (btnPadRB?.Pressed == true) m |= 1 << 5;
            if (btnPadBack?.Pressed == true) m |= 1 << 6;
            if (btnPadMenu?.Pressed == true) m |= 1 << 7;
            if (btnPadLS?.Pressed == true) m |= 1 << 8;
            if (btnPadRS?.Pressed == true) m |= 1 << 9;
            if (btnPadUp?.Pressed == true) m |= 1 << 10;
            if (btnPadDown?.Pressed == true) m |= 1 << 11;
            if (btnPadLeft?.Pressed == true) m |= 1 << 12;
            if (btnPadRight?.Pressed == true) m |= 1 << 13;
            return m;
        }

        private double GetWheelData()
        {
            double data, y;

            switch (SensorMode)
            {
                case 0:
                    {
                        //x = AcX1 * 100;
                        y = AcY1 * gAngle * 10;
                        break;
                    } // gY / 0.98 * 90;

                case 1:
                    {
                        //x = AcX1 * 10;
                        y = AcY1 * gAngle;
                        break;
                    }

                case 2:
                    {
                        //x = (AcX1 - AcX2) * 100;
                        y = (AcY1 - AcY2) * gAngle * 10;
                        break;
                    } //总加速度分量 - 运动加速度分量 = 重力加速度分量 

                case 3:
                    {
                        //x = (AcX1 - AcX2) * 10;
                        y = (AcY1 - AcY2) * gAngle;
                        break;
                    } //总加速度分量 - 运动加速度分量 = 重力加速度分量 

                default:
                    {
                        //x = AcX1 * 10;
                        y = AcY1 * gAngle;
                        break;
                    }
            }
            if (TmpX > 0 && AcX1 < 0) //朝向由上变为下

            {
                if (y < 0) //左转
                {
                    Hp -= 1;
                }
                else       //右转
                {
                    Hp += 1;
                }
            }
            else if (TmpX < 0 && AcX1 > 0) //朝向由下变为上

            {
                if (y < 0) //右转
                {
                    Hp += 1;
                }
                else       //左转
                {
                    Hp -= 1;
                }
            }

            //限制转向范围为900度

            //if (Hp > 2) Hp = 2;
            //if (Hp < -2) Hp = -2;

            //else if ((TmpX > 0 && AcX1 > 0) || (TmpX == 0 || AcX1 == 0) || (TmpX < 0 && AcX1 < 0)) //朝向未变
            //{
            //    //不变
            //}

            //AcX1 > 0 手机朝上 /  AcX1 < 0 手机朝下
            // -90 ~  90   = y                             Hp=0        手机朝上    
            //  90 ~ 270   = 90 + (90 - y) = 180 - y       Hp=1        手机朝下
            //-270 ~ -90   = -90 + (-90 - y) = -180 - y    Hp=-1       手机朝下
            // 270 ~ 450   = 360 + y                       Hp=2        手机朝上
            //-270 ~-450   = -360 + y                      Hp=-2       手机朝上
            data = 180 * Hp + y * (AcX1 / Math.Abs(AcX1));
            TmpX = AcX1;
            return data;
        }

        private void BtnNetMode_OnClick()
        {
            // 循环切换: TCP → UDP → 蓝牙 → USB → TCP
            int prev = mConnectMode;
            mConnectMode = (mConnectMode + 1) % MODE_COUNT;
            btnNetMode.Text = ModeLabels[mConnectMode];

            // USB 模式端点固定为 127.0.0.1，切进/切出时同步 IP 输入框内容
            if (prev == MODE_USB)
                IPText.Text = _ipBeforeUsb ?? IPText.Text;
            if (mConnectMode == MODE_USB)
            {
                _ipBeforeUsb = IPText.Text;
                IPText.Text = USB_ENDPOINT;
            }

            // 切换通道：关掉旧通道的连接，避免两条通道同时发数据
            // （Sct[1] 是 TCP/UDP 的 socket，_btClient 是蓝牙的，两者都要关，
            //   否则旧 socket 会在 ConnectNow 建新连接时被丢弃成泄漏）
            IsConnected = false;
            try { Sct[1]?.Close(); } catch { }
            try { _btClient?.Close(); } catch { }
            try { steeringWheel.Connected = false; steeringWheel.CenterText = ""; } catch { }

            if (mConnectMode == MODE_BT)
            {
                if (!BluetoothSppClient.IsAvailable(this))
                    LogToUI("本机无蓝牙适配器，蓝牙模式不可用");
                else if (!BluetoothSppClient.IsEnabled(this))
                    LogToUI("请先开启蓝牙，并在系统设置中与 PC 配对");
                else
                    LogToUI("蓝牙模式：点「重连」选择已配对的 PC");
            }
            else if (mConnectMode == MODE_USB)
            {
                LogToUI("USB 模式：数据线连 PC + 手机开「USB 调试」，PC 端自动建立转发");
            }

            // 记住模式，下次启动直接沿用
            try
            {
                GetSharedPreferences("WheelSimuPrefs", FileCreationMode.Private)
                    .Edit().PutInt("ConnectMode", mConnectMode).Commit();
            }
            catch { }

            // 切换模式后立刻按新通道重连。
            // 否则用户切完模式界面毫无反应，还得去点「重连」按钮 —— 而那个按钮此时是"开"状态，
            // 点一下反而把自动重连关掉了，体验很反直觉。
            // 蓝牙例外：连它需要先选设备，交给用户点「重连」决定，避免每次切模式都弹选择框。
            if (mAutoReconnect && mConnectMode != MODE_BT)
                ConnectNow();
        }

        // ==================== 蓝牙模式 (SPP) ====================
        private bool HasBluetoothPermission()
        {
            // Android 12 (API 31) 起连接/读取已配对设备需要 BLUETOOTH_CONNECT
            // 用 OperatingSystem 判断（而非 Build.VERSION.SdkInt），平台分析器才能识别为守卫
            if (!OperatingSystem.IsAndroidVersionAtLeast(31)) return true;
            try { return CheckSelfPermission(Android.Manifest.Permission.BluetoothConnect) == Android.Content.PM.Permission.Granted; }
            catch { return false; }
        }

        private void RequestBluetoothPermission()
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(31)) { ShowBluetoothDevicePicker(); return; }
            RequestPermissions(new[] { Android.Manifest.Permission.BluetoothConnect }, REQ_BT_PERMISSION);
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Android.Content.PM.Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

            if (requestCode == REQ_BT_PERMISSION)
            {
                if (grantResults != null && grantResults.Length > 0 &&
                    grantResults[0] == Android.Content.PM.Permission.Granted)
                    ShowBluetoothDevicePicker();
                else
                    LogToUI("未授予蓝牙权限，无法使用蓝牙模式");
            }
        }

        /// <summary>蓝牙连接入口：权限 → 选设备 → 后台连接</summary>
        private void ConnectBluetoothFlow()
        {
            if (!BluetoothSppClient.IsAvailable(this)) { LogToUI("本机无蓝牙适配器"); RunOnUiThread(() => btnConnect.Enabled = true); return; }
            if (!BluetoothSppClient.IsEnabled(this)) { LogToUI("请先开启蓝牙并与 PC 配对"); RunOnUiThread(() => btnConnect.Enabled = true); return; }

            if (!HasBluetoothPermission()) { RunOnUiThread(RequestBluetoothPermission); return; }

            RunOnUiThread(ShowBluetoothDevicePicker);
        }

        /// <summary>自动重连：已有上次设备地址时直接连，否则弹选择器</summary>
        private void ConnectBluetoothAuto()
        {
            if (string.IsNullOrEmpty(_btDeviceAddress)) { ConnectBluetoothFlow(); return; }
            DoBluetoothConnect(_btDeviceAddress);
        }

        private void ShowBluetoothDevicePicker()
        {
            var devices = BluetoothSppClient.GetPairedDevices(this);
            if (devices.Count == 0)
            {
                LogToUI("没有已配对设备，请先在系统蓝牙设置中与 PC 配对");
                RunOnUiThread(() => btnConnect.Enabled = true);
                return;
            }

            var names = new string[devices.Count];
            for (int i = 0; i < devices.Count; i++) names[i] = devices[i].ToString();

            try
            {
                new Android.App.AlertDialog.Builder(this)
                    .SetTitle("选择 PC（已配对）")
                    .SetItems(names, (s, e) =>
                    {
                        var dev = devices[e.Which];
                        _btDeviceAddress = dev.Address;
                        try
                        {
                            GetSharedPreferences("WheelSimuPrefs", FileCreationMode.Private)
                                .Edit().PutString("LastBT", dev.Address).Commit();
                        }
                        catch { }

                        LogToUI($"正在连接 {dev.Name} ...");
                        ThreadPool.QueueUserWorkItem(_ => DoBluetoothConnect(dev.Address));
                    })
                    .SetNegativeButton("取消", (s, e) => { try { btnConnect.Enabled = true; } catch { } })
                    .Show();
            }
            catch (Exception ex)
            {
                LogToUI("设备选择失败: " + ex.Message);
            }
        }

        private void DoBluetoothConnect(string address)
        {
            if (mConnecting) return;
            mConnecting = true;
            RunOnUiThread(() => btnConnect.Enabled = false);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    try { _btClient?.Close(); } catch { }
                    var client = new BluetoothSppClient();
                    client.Connect(this, address);
                    _btClient = client;

                    IsConnected = true;
                    CancelReconnect();
                    RunOnUiThread(() =>
                    {
                        textView3.Text = "蓝牙已连接";
                        textView2.Text = "→ BT " + address;
                        steeringWheel.Connected = true;
                        steeringWheel.CenterText = "蓝牙";
                        btnConnect.Text = "重连: 开";
                        btnConnect.Enabled = true;
                    });
                }
                catch (Exception ex)
                {
                    IsConnected = false;
                    RunOnUiThread(() =>
                    {
                        textView3.Text = "蓝牙连接失败: " + ex.Message;
                        steeringWheel.Connected = false;
                        steeringWheel.CenterText = "";
                        btnConnect.Enabled = true;
                    });

                    if (mAutoReconnect) ScheduleReconnect();
                }
                finally
                {
                    mConnecting = false;
                }
            });
        }

        /// <summary>发起连接（不切换自动重连开关），失败时按自动重连策略处理。
        /// 连接在后台线程执行，避免阻塞 UI 线程（否则自动重连期间滑块/触摸会卡死）</summary>
        private void ConnectNow(string ipOverride = null)
        {
            // USB 模式端点固定为 127.0.0.1：PC 端的 adb reverse 已把 PC:25050 映射到手机本机，
            // 这里必须忽略 IP 输入框与 UDP 发现到的局域网地址，否则会绕开 USB 走 WiFi。
            string ip = mConnectMode == MODE_USB
                ? USB_ENDPOINT
                : (ipOverride ?? IPText.Text?.Trim());
            if (string.IsNullOrEmpty(ip))
            {
                RunOnUiThread(() => textView3.Text = "等待服务器广播...");
                RunOnUiThread(() => btnConnect.Enabled = true);
                return;
            }

            if (mConnecting) return;    // 防重入（正在连接/重连中）
            mConnecting = true;
            RunOnUiThread(() => btnConnect.Enabled = false);

            // 后台线程执行 DoConnect（内部有 connectTask.Wait(5000) 等阻塞调用），不阻塞 UI
            Task.Run(() =>
            {
                try
                {
                    DoConnect(ip);
                }
                catch (Exception ex)
                {
                    RunOnUiThread(() => textView2.Text = ex.Message);
                    RunOnUiThread(() => textView3.Text =
                    $"Remote={IPData[0].IP}:{IPData[0].Port}  Local={IPData[1].IP}:{IPData[1].Port}");
                    RunOnUiThread(() => btnConnect.Enabled = true);
                    TryTimes += 1;

                    if (mAutoReconnect)
                        ScheduleReconnect();
                }
                finally
                {
                    mConnecting = false;
                }
            });
        }

        private void BtnConnect_OnClick()
        {
            if (!mAutoReconnect)
            {
                // === 打开自动重连：连接并启用断线自动重连 ===
                mAutoReconnect = true;
                RunOnUiThread(() => btnConnect.Text = "重连: 开");
                RunOnUiThread(() => textView2.Text = "自动重连: 开");

                if (mConnectMode == MODE_BT)
                    ConnectBluetoothAuto();   // 蓝牙：选设备 / 重连上次设备
                else
                    ConnectNow();
            }
            else
            {
                // === 关闭自动重连：断开连接并停止重连 ===
                mAutoReconnect = false;
                CancelReconnect();
                try { Sct[1]?.Close(); } catch { }
                try { _btClient?.Close(); } catch { }
                IsConnected = false;
                RunOnUiThread(() => btnConnect.Text = "重连: 关");
                RunOnUiThread(() => textView3.Text = "已断开 (自动重连: 关)");
                RunOnUiThread(() => steeringWheel.Connected = false);
                RunOnUiThread(() => steeringWheel.CenterText = "");
                RunOnUiThread(() => btnConnect.Enabled = true);
            }
        }

        private void DoConnect(string rawText)
        {
            // 蓝牙模式不走 TCP/UDP，连接由 DoBluetoothConnect 负责
            if (mConnectMode == MODE_BT) return;

            // 获取手机本机WiFi IP (多种方式兜底)
            string localIp = null;
            try
            {
                WifiManager wifi = (WifiManager)GetSystemService(WifiService);
                WifiInfo info = wifi.ConnectionInfo;
                int ipInt = info.IpAddress;
                localIp = $"{(ipInt & 0xFF)}.{((ipInt >> 8) & 0xFF)}.{((ipInt >> 16) & 0xFF)}.{((ipInt >> 24) & 0xFF)}";
            }
            catch { }

            if (string.IsNullOrEmpty(localIp) || localIp.StartsWith("0."))
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                localIp = host.AddressList
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("127."))
                    ?.ToString() ?? "0.0.0.0";
            }
            IPData[1].IP = localIp;
            IPData[1].Port = 25050;

            int colonIdx = rawText.LastIndexOf(':');
            if (colonIdx > 0)
            {
                IPData[0].IP = rawText.Substring(0, colonIdx);
                if (int.TryParse(rawText.Substring(colonIdx + 1), out int parsedPort) && parsedPort > 0 && parsedPort <= 65535)
                    IPData[0].Port = parsedPort;
                else
                    IPData[0].Port = Core.CommonCode.GetPort(TryTimes);
            }
            else
            {
                IPData[0].IP = rawText;
                IPData[0].Port = Core.CommonCode.GetPort(TryTimes);
            }

            // 保存IP（USB 模式的 IPText 是固定回环地址，不该覆盖掉局域网 IP）
            var prefs = GetSharedPreferences("WheelSimuPrefs", FileCreationMode.Private);
            var editor = prefs.Edit();
            if (mConnectMode != MODE_USB)
                editor.PutString("LastIP", IPText.Text);
            editor.Commit();

            // 保存模式，下次启动直接沿用
            editor.PutInt("ConnectMode", mConnectMode);
            editor.Commit();

            RunOnUiThread(() => textView4.Text = "Connecting ...");
            RunOnUiThread(() => textView5.Text = ModeLabels[mConnectMode]);

            if (mConnectMode == MODE_BT)
                Sct[1] = new Socket(AddressFamily.InterNetwork, SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            else if (mConnectMode == MODE_UDP)
                Sct[1] = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            else
                Sct[1] = new Socket(AddressFamily.InterNetwork, SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);

            Sct[1].SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            Sct[1].NoDelay = true; // 禁用 Nagle 算法，降低延迟

            RunOnUiThread(() => textView4.Text = "Connecting ......");
            IPEndPoint RemoteEndPoint = new IPEndPoint(IPAddress.Parse(IPData[0].IP), IPData[0].Port);
            RunOnUiThread(() => textView2.Text = $"→ {IPData[0].IP}:{IPData[0].Port}");

            if (mConnectMode == MODE_UDP)
            {
                Sct[1].Bind(new IPEndPoint(IPAddress.Any, 25050));
                Sct[1].Connect(RemoteEndPoint);
            }
            else
            {
                var connectTask = Task.Run(() => Sct[1].Connect(RemoteEndPoint));
                if (!connectTask.Wait(5000))
                {
                    Sct[1].Close();
                    Sct[1].Dispose();
                    throw new TimeoutException("连接超时 (" + IPData[0].IP + ":" + IPData[0].Port + ")");
                }
            }

            RunOnUiThread(() => textView3.Text = "Connected");
            RunOnUiThread(() => steeringWheel.Connected = true);
            IsConnected = true;
            CancelReconnect();
            RunOnUiThread(() => btnConnect.Text = "重连: 开");
            RunOnUiThread(() => btnConnect.Enabled = true);
        }

        /// <summary>初始化档位模式相关控件（两种布局都有，部分控件可能不存在时置空安全处理）</summary>
        private void InitGearControls()
        {
            btnGearMode = FindViewById<Button>(Resource.Id.btnGearMode);
            btnAutoP = FindViewById<Button>(Resource.Id.btnAutoP);
            btnAutoD = FindViewById<Button>(Resource.Id.btnAutoD);
            btnAutoN = FindViewById<Button>(Resource.Id.btnAutoN);
            btnAutoR = FindViewById<Button>(Resource.Id.btnAutoR);
            btnGearR = FindViewById<Button>(Resource.Id.btnGearR);
            btnGearN = FindViewById<Button>(Resource.Id.btnGearN);
            btnManualGears = new Button[6];
            int[] gearIds =
            {
                Resource.Id.btnGear1, Resource.Id.btnGear2, Resource.Id.btnGear3, Resource.Id.btnGear4,
                Resource.Id.btnGear5
            };
            for (int i = 0; i < gearIds.Length; i++) btnManualGears[i] = FindViewById<Button>(gearIds[i]);
            // btnManualGears[5] 保持 null（5 档面板无 6 号按钮，判空保护）

            // 6档手动挡面板：H 型 R 1 3 5 / _ N _ / _ 2 4 6
            btn6GearR = FindViewById<Button>(Resource.Id.btn6GearR);
            btn6GearN = FindViewById<Button>(Resource.Id.btn6GearN);
            btn6ManualGears = new Button[6];
            int[] gear6Ids =
            {
                Resource.Id.btn6Gear1, Resource.Id.btn6Gear2, Resource.Id.btn6Gear3, Resource.Id.btn6Gear4,
                Resource.Id.btn6Gear5, Resource.Id.btn6Gear6
            };
            for (int i = 0; i < 6; i++) btn6ManualGears[i] = FindViewById<Button>(gear6Ids[i]);

            // 手动挡（5档/6档）：点选锁定（tap-to-select），模拟真实 H 挡硬件
            // “拨杆卡入挡槽即保持，直到拨走”：点一下选中并保持，再点其它挡切换，点空挡回 N。
            // 用 Click（完整“按下-抬起”手势）而非 Touch 按住，松开手指挡位依然保持。
            void BindManualSelect(Button btn, int gear)
            {
                if (btn == null) return;
                btn.Click += (s, e) =>
                {
                    _manualGearSelected = gear;
                    UpdateManualGearHighlight();
                };
            }
            BindManualSelect(btnGearR, -1);
            BindManualSelect(btnGearN, 0);
            for (int i = 0; i < 5; i++) { int idx = i; BindManualSelect(btnManualGears[i], idx + 1); }
            BindManualSelect(btn6GearR, -1);
            BindManualSelect(btn6GearN, 0);
            for (int i = 0; i < 6; i++) { int idx = i; BindManualSelect(btn6ManualGears[i], idx + 1); }

            // 真实自动挡拨动开关：点按切换并保持（再点同挡回空挡）
            if (btnAutoP != null) btnAutoP.Click += (s, e) => ToggleAutoDr(2);  // P=驻车
            if (btnAutoD != null) btnAutoD.Click += (s, e) => ToggleAutoDr(1);  // D=前进
            if (btnAutoN != null) btnAutoN.Click += (s, e) => ToggleAutoDr(0);  // N=空挡
            if (btnAutoR != null) btnAutoR.Click += (s, e) => ToggleAutoDr(-1); // R=倒车

            ApplyGearMode();
        }

        /// <summary>根据当前档位模式显示对应的档位面板并更新按钮文字</summary>
        private void ApplyGearMode()
        {
            if (btnGearMode != null)
                btnGearMode.Text = kGearModeNames[_gearMode];

            var simplePanel = FindViewById<Android.Views.View>(Resource.Id.simpleGearPanel);
            var autoPanel = FindViewById<Android.Views.View>(Resource.Id.autoGearPanel);
            var seqPanel = FindViewById<Android.Views.View>(Resource.Id.seqGearPanel);
            var gear5Panel = FindViewById<Android.Views.View>(Resource.Id.manualGear5Panel);
            var gear6Panel = FindViewById<Android.Views.View>(Resource.Id.manualGear6Panel);
            if (simplePanel == null || autoPanel == null || seqPanel == null) return;

            simplePanel.Visibility = _gearMode == 0 ? ViewStates.Visible : ViewStates.Gone;
            autoPanel.Visibility = _gearMode == 1 ? ViewStates.Visible : ViewStates.Gone;
            seqPanel.Visibility = _gearMode == 2 ? ViewStates.Visible : ViewStates.Gone;
            if (gear5Panel != null) gear5Panel.Visibility = _gearMode == 3 ? ViewStates.Visible : ViewStates.Gone;
            if (gear6Panel != null) gear6Panel.Visibility = _gearMode == 4 ? ViewStates.Visible : ViewStates.Gone;

            if (_gearMode == 1) UpdateAutoDrHighlight();
            if (_gearMode == 3 || _gearMode == 4)
            {
                // 5档面板没有 6 挡：若上次停留在 6 挡，切回 5 档时回落空挡
                if (_gearMode == 3 && _manualGearSelected > 5) _manualGearSelected = 0;
                UpdateManualGearHighlight();
            }
        }

        /// <summary>真实自动挡拨动开关：点按切换并保持（再次点选同挡位则回到空挡）</summary>
        private void ToggleAutoDr(int dr)
        {
            _autoDrSelected = (_autoDrSelected == dr) ? 0 : dr;
            UpdateAutoDrHighlight();
        }

        /// <summary>高亮真实自动挡拨动开关选中的挡位（P/D/N/R），未选中时暗色</summary>
        private void UpdateAutoDrHighlight()
        {
            void SetSelected(Button btn, bool sel)
            {
                if (btn == null) return;
                btn.SetBackgroundResource(sel ? Resource.Drawable.btn_accent : Resource.Drawable.btn_dark);
            }

            SetSelected(btnAutoP, _autoDrSelected == 2);
            SetSelected(btnAutoD, _autoDrSelected == 1);
            SetSelected(btnAutoN, _autoDrSelected == 0);
            SetSelected(btnAutoR, _autoDrSelected == -1);
        }

        /// <summary>高亮当前手动挡挂挡，其余恢复暗色</summary>
        private void UpdateManualGearHighlight()
        {
            void SetSelected(Button btn, bool sel)
            {
                if (btn == null) return;
                btn.SetBackgroundResource(sel ? Resource.Drawable.btn_accent : Resource.Drawable.btn_dark);
            }

            // 5档面板高亮
            SetSelected(btnGearR, _manualGearSelected == -1);
            for (int i = 0; i < 6; i++)
                SetSelected(btnManualGears[i], _manualGearSelected == i + 1);
            SetSelected(btnGearN, _manualGearSelected == 0);
            // 6档面板高亮
            SetSelected(btn6GearR, _manualGearSelected == -1);
            for (int i = 0; i < 6; i++)
                SetSelected(btn6ManualGears[i], _manualGearSelected == i + 1);
            SetSelected(btn6GearN, _manualGearSelected == 0);
        }

        protected override void OnResume()
        {
            base.OnResume();
            if (steerEnabled)
            {
                StartSensors();
                sendTimer?.Change(0, sendIntervalMs);
            }
        }

        protected override void OnPause()
        {
            base.OnPause();
            sendTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            StopSensors();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            CancelDiscovery();
            CancelReconnect();
            mAutoReconnect = false;
            sendTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            sendTimer?.Dispose();
            sendTimer = null;
            StopSensors();
            mSensorManager?.UnregisterListener(this);

            try { _btClient?.Close(); } catch { }

            foreach (var socket in Sct)
            {
                if (socket != null && socket.Connected)
                {
                    try { socket.Shutdown(SocketShutdown.Both); } catch { }
                    socket.Close();
                    socket.Dispose();
                }
            }
        }

        // ==================== 辅助 ====================
        private void LogToUI(string msg)
        {
            RunOnUiThread(() => textView3.Text = msg);
        }

        // ==================== UDP 服务发现 ====================
        private volatile bool mDiscoveryInitialDone = false;

        private void StartDiscovery()
        {
            CancelDiscovery();
            mDiscoveryInitialDone = false;
            mDiscoverCts = new CancellationTokenSource();
            var ct = mDiscoverCts.Token;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                UdpClient udp = null;
                try
                {
                    udp = new UdpClient(DISCOVERY_PORT);
                    udp.Client.ReceiveTimeout = 1000;

                    while (!ct.IsCancellationRequested)
                    {
                        try
                        {
                            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                            byte[] data = udp.Receive(ref remoteEP);
                            string msg = Encoding.UTF8.GetString(data);
                            if (msg.StartsWith(DISCOVERY_MAGIC + ":"))
                            {
                                var parts = msg.Split(':');
                                if (parts.Length >= 3)
                                {
                                    string server = parts[1] + ":" + parts[2];
                                    if (server != mDiscoveredServer)
                                    {
                                        mDiscoveredServer = server;
                                        // 初始发现阶段：只填充 IPText，不做连接（由 Startup 逻辑统一处理）
                                        if (!mDiscoveryInitialDone)
                                        {
                                            RunOnUiThread(() =>
                                            {
                                                if (string.IsNullOrEmpty(IPText.Text?.Trim()))
                                                    IPText.Text = server;
                                            });
                                        }
                                        // 之后发现的服务器：自动连接（用于服务器切换场景）
                                        // USB 模式忽略 —— 它的端点是固定的 127.0.0.1，不能被局域网地址带跑
                                        else if (!IsConnected && mAutoReconnect && mConnectMode != MODE_USB)
                                        {
                                            RunOnUiThread(() =>
                                            {
                                                IPText.Text = server;
                                                textView3.Text = $"发现服务器: {server}";
                                                if (!IsConnected && mAutoReconnect)
                                                {
                                                    btnConnect.Enabled = false;
                                                    try { ConnectNow(server); } catch { }
                                                }
                                            });
                                        }
                                    }
                                }
                            }
                        }
                        catch (SocketException) { continue; }
                        catch { break; }
                    }
                }
                catch { }
                finally
                {
                    try { udp?.Close(); } catch { }
                }
            });
        }

        private void CancelDiscovery()
        {
            if (mDiscoverCts != null)
            {
                try { mDiscoverCts.Cancel(); mDiscoverCts.Dispose(); } catch { }
                mDiscoverCts = null;
            }
        }

        // ==================== 自动重连 ====================
        private System.Threading.Timer mReconnectTimer;

        private void OnConnectionLost()
        {
            if (!mAutoReconnect) return;

            RunOnUiThread(() =>
            {
                textView3.Text = "连接已断开，稍后自动重连...";
                steeringWheel.Connected = false;
                steeringWheel.CenterText = "";
                btnConnect.Enabled = false;
            });

            // 3秒后尝试重连
            ScheduleReconnect();
        }

        private void ScheduleReconnect()
        {
            CancelReconnect();
            mReconnectTimer = new System.Threading.Timer(_ =>
            {
                RunOnUiThread(() =>
                {
                    if (IsConnected) { CancelReconnect(); return; }
                    if (!mAutoReconnect) { CancelReconnect(); return; }
                    textView3.Text = $"自动重连中...";
                    steeringWheel.Connected = false;
                    steeringWheel.CenterText = "重连中...";
                    btnConnect.Enabled = false;
                });

                // 给UI线程一点时间更新状态
                Thread.Sleep(100);

                // 在后台线程执行重连（ConnectNow 内部自行启动后台任务，不再阻塞 UI）
                if (!IsConnected && mAutoReconnect)
                {
                    try
                    {
                        if (mConnectMode == MODE_BT) ConnectBluetoothAuto();
                        else ConnectNow();
                    }
                    catch { }
                }
            }, null, 3000, Timeout.Infinite);
        }

        private void CancelReconnect()
        {
            if (mReconnectTimer != null)
            {
                try { mReconnectTimer.Dispose(); } catch { }
                mReconnectTimer = null;
            }
        }

    }




}




