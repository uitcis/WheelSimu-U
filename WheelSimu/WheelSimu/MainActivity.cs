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
        Button btnClearAngle;
        Button btnNetMode;
        Button btnLayoutSwitch;  // 布局切换按钮
        Switch SteerEnableSwitch;
        ToggleButton HandbrakeSwitch;
        SteeringWheelView steeringWheel;

        /// <summary>连接模式: 0=TCP, 1=UDP, 2=蓝牙</summary>
        private int mConnectMode = 0;

        /// <summary>布局模式: 0=赛车HUD, 1=模拟方向盘</summary>
        private int _layoutMode = 0;

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
        double OffSet = 0; //偏移补偿
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
        private volatile float _latestAngle;

        // UDP 服务自动发现
        const int DISCOVERY_PORT = 5051;
        const string DISCOVERY_MAGIC = "WHEELSIMU_SERVER";
        private CancellationTokenSource mDiscoverCts;
        private volatile string mDiscoveredServer = null;

        // 自动重连
        private volatile bool mAutoReconnect = true;

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

                // 读取布局偏好：0=赛车HUD(content_main), 1=模拟方向盘(content_wheel)
                var prefs = GetSharedPreferences("WheelSimuPrefs", FileCreationMode.Private);
                _layoutMode = prefs.GetInt("LayoutMode", 0);
                SetContentView(_layoutMode == 0 ? Resource.Layout.activity_main : Resource.Layout.activity_wheel);


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
            IPText.Text = prefs.GetString("LastIP", "192.168.1.100:5050");

            btnConnect = FindViewById<Button>(Resource.Id.Connect);
            btnConnect.Text = "重连: 开";  // 初始状态：自动重连开启
            btnNetMode = FindViewById<Button>(Resource.Id.btnNetMode);
            btnLayoutSwitch = FindViewById<Button>(Resource.Id.btnLayoutSwitch);
            btnSet = FindViewById<Button>(Resource.Id.btnSet);
            btnReset = FindViewById<Button>(Resource.Id.btnReset);
            btnSetSrd = FindViewById<Button>(Resource.Id.btnSetSrd);
            btnSetSru = FindViewById<Button>(Resource.Id.btnSetSru);


            btnGearUp = FindViewById<Button>(Resource.Id.btnGearUp);
            btnGearDown = FindViewById<Button>(Resource.Id.btnGearDown);
            btnClearAngle = FindViewById<Button>(Resource.Id.btnClearAngle);
            SteerEnableSwitch = FindViewById<Switch>(Resource.Id.SteerEnableSwitch);
            HandbrakeSwitch = FindViewById<ToggleButton>(Resource.Id.HandbrakeSwitch);

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
                // 切换布局模式并重启 Activity
                _layoutMode = _layoutMode == 0 ? 1 : 0;
                var p = GetSharedPreferences("WheelSimuPrefs", FileCreationMode.Private);
                p.Edit().PutInt("LayoutMode", _layoutMode).Commit();
                // 保存当前IP（Recreate 会重新读取）
                p.Edit().PutString("LastIP", IPText.Text).Commit();
                Recreate();
            };

            btnClearAngle.Click += delegate
            {
                ThreadPool.QueueUserWorkItem(o => BtnClearAngle_OnClick());
            };

            SteerEnableSwitch.Click += delegate
            {
                ThreadPool.QueueUserWorkItem(o => SteerEnableSwitch_OnClick());
            };

            //vvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvv事件接口设置vvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvv



            //^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^其他事件委托^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

            //vvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvv其他事件委托vvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvv

            // 启动 UDP 服务发现（后台监听广播）
            StartDiscovery();

            // 启动后自动尝试连接（延迟 1.5s 等 WiFi 就绪，优先用发现的服务器）
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(1500);
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
                    _latestHb       = HandbrakeSwitch.Checked ? 1 : 0;
                    _latestGearUp   = btnGearUp.Pressed ? 1 : 0;
                    _latestGearDn   = btnGearDown.Pressed ? 1 : 0;
                    _latestGear     = btnGearUp.Pressed ? 1 : (btnGearDown.Pressed ? -1 : 0);
                    _latestSet      = btnSet.Pressed ? -1 : (btnReset.Pressed ? 1 : 0);
                    _latestSetSR    = btnSetSrd.Pressed ? -1 : (btnSetSru.Pressed ? 1 : 0);
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
                            _latestGearUp, _latestGearDn, _latestGear, _latestSet, _latestSetSR, _latestHb);
                        try { Sct[1]?.Send(_sendBuf, len, SocketFlags.None); }
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
        private int BuildSendDataToBuffer(double angle, int t, int b, int c, int gu, int gd, int g, int s, int sr, int h)
        {
            string data = $"A={angle:0.0},T={t},B={b},C={c},Gu={gu},Gd={gd},G={g},S={s},SR={sr},H={h}@";
            return Encoding.UTF8.GetBytes(data, 0, data.Length, _sendBuf, 0);
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
            y -= OffSet * (90 - Math.Abs(y)) / 90;

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
            // 循环切换: TCP → UDP → 蓝牙 → TCP
            mConnectMode = (mConnectMode + 1) % 3;
            string[] modes = { "TCP", "UDP", "蓝牙" };
            btnNetMode.Text = modes[mConnectMode];
        }

        /// <summary>发起连接（不切换自动重连开关），失败时按自动重连策略处理</summary>
        private void ConnectNow(string ipOverride = null)
        {
            string ip = ipOverride ?? IPText.Text?.Trim();
            if (string.IsNullOrEmpty(ip))
            {
                RunOnUiThread(() => textView3.Text = "等待服务器广播...");
                RunOnUiThread(() => btnConnect.Enabled = true);
                return;
            }

            RunOnUiThread(() => btnConnect.Enabled = false);
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
        }

        private void BtnConnect_OnClick()
        {
            if (!mAutoReconnect)
            {
                // === 打开自动重连：连接并启用断线自动重连 ===
                mAutoReconnect = true;
                RunOnUiThread(() => btnConnect.Text = "重连: 开");
                RunOnUiThread(() => textView2.Text = "自动重连: 开");
                ConnectNow();
            }
            else
            {
                // === 关闭自动重连：断开连接并停止重连 ===
                mAutoReconnect = false;
                CancelReconnect();
                Sct[1]?.Close();
                IsConnected = false;
                RunOnUiThread(() => btnConnect.Text = "重连: 关");
                RunOnUiThread(() => textView3.Text = "已断开 (自动重连: 关)");
                RunOnUiThread(() => btnConnect.Enabled = true);
            }
        }

        private void DoConnect(string rawText)
        {
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
            IPData[1].Port = 5050;

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

            // 保存IP
            var prefs = GetSharedPreferences("WheelSimuPrefs", FileCreationMode.Private);
            var editor = prefs.Edit();
            editor.PutString("LastIP", IPText.Text);
            editor.Commit();

            RunOnUiThread(() => textView4.Text = "Connecting ...");
            string[] modeLabels = { "TCP", "UDP", "蓝牙" };
            RunOnUiThread(() => textView5.Text = modeLabels[mConnectMode]);

            if (mConnectMode == 2)
                Sct[1] = new Socket(AddressFamily.InterNetwork, SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            else if (mConnectMode == 1)
                Sct[1] = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            else
                Sct[1] = new Socket(AddressFamily.InterNetwork, SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);

            Sct[1].SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            Sct[1].NoDelay = true; // 禁用 Nagle 算法，降低延迟

            RunOnUiThread(() => textView4.Text = "Connecting ......");
            IPEndPoint RemoteEndPoint = new IPEndPoint(IPAddress.Parse(IPData[0].IP), IPData[0].Port);
            RunOnUiThread(() => textView2.Text = $"→ {IPData[0].IP}:{IPData[0].Port}");

            if (mConnectMode == 1)
            {
                Sct[1].Bind(new IPEndPoint(IPAddress.Any, 5050));
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
            IsConnected = true;
            CancelReconnect();
            RunOnUiThread(() => btnConnect.Text = "重连: 开");
            RunOnUiThread(() => btnConnect.Enabled = true);
        }

        private void BtnClearAngle_OnClick()
        {
            // 方向盘打开时不归零，避免运动中校准导致误差
            if (steerEnabled)
            {
                RunOnUiThread(() => textView2.Text = "请先关闭方向盘再归零");
                return;
            }
            try
            {
                switch (SensorMode)
                {
                    case 0: { OffSet = AcY1 * gAngle * 10; break; }
                    case 1: { OffSet = AcY1 * gAngle; break; }
                    case 2: { OffSet = (AcY1 - AcY2) * gAngle * 10; break; }
                    case 3: { OffSet = (AcY1 - AcY2) * gAngle; break; }
                    default: { OffSet = AcY1 * 10; break; }
                }

                Hp = 0;
                RunOnUiThread(() => textView2.Text = "归零完成");
            }
            catch (Exception ex)
            {
                RunOnUiThread(() => textView2.Text = ex.Message);
            }
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
                                        else if (!IsConnected && mAutoReconnect)
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
                    btnConnect.Enabled = false;
                });

                // 给UI线程一点时间更新状态
                Thread.Sleep(100);

                // 在UI线程上执行重连
                RunOnUiThread(() =>
                {
                    if (!IsConnected && mAutoReconnect)
                    {
                        try { ConnectNow(); } catch { }
                    }
                });
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




