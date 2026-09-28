using System;
using Android.Content;
using Android.Graphics;
using Android.Util;
using Android.Views;

namespace WheelSimu
{
    /// <summary>
    /// 赛车风格方向盘 — 平底运动方向盘 + 发光角度弧 + 中心数字 HUD
    /// 参考 Real Racing / GRID 手游的 HUD 设计
    /// </summary>
    public class SteeringWheelView : View
    {
        private float _angle;
        private float _smoothAngle;

        // Paints
        private Paint _bgPaint;
        private Paint _rimPaint;
        private Paint _rimGlowPaint;       // 轮辋外发光
        private Paint _spokePaint;
        private Paint _hubPaint;
        private Paint _hubInnerPaint;
        private Paint _markerPaint;
        private Paint _angleTextPaint;
        private Paint _dotPaint;           // 已连接绿色圆形指示灯
        private Paint _arcBgPaint;         // 背景弧
        private Paint _arcActivePaint;     // 激活角度弧（发光）
        private Paint _centerRingPaint;    // 中心表盘外环

        private float _centerX, _centerY, _radius;
        private float _rimWidth;
        private Path _clipCircle = null!;

        /// <summary>方向盘中心文字（如"重连中..."），空字符串则不显示</summary>
        private string _centerText = "";

        /// <summary>是否已连接：true 时中心显示绿色圆形指示灯</summary>
        private bool _connected;

        public SteeringWheelView(Context context) : base(context) => Init();
        public SteeringWheelView(Context context, IAttributeSet attrs) : base(context, attrs) => Init();
        public SteeringWheelView(Context context, IAttributeSet attrs, int defStyleAttr) : base(context, attrs, defStyleAttr) => Init();

        public float Angle
        {
            get => _angle;
            set
            {
                _angle = value;
                _smoothAngle = value;
                Invalidate();
            }
        }

        /// <summary>设置方向盘中心显示的文字（如"重连中..."），空字符串则不显示</summary>
        public string CenterText
        {
            get => _centerText;
            set
            {
                _centerText = value ?? "";
                Invalidate();
            }
        }

        /// <summary>设置连接状态：true=中心显示绿色圆形指示灯，false=按 CenterText 显示文字</summary>
        public bool Connected
        {
            get => _connected;
            set
            {
                _connected = value;
                Invalidate();
            }
        }

        /// <summary>是否绘制径向渐变背景底板（布局2 手柄界面置 false，避免方向盘后面出现黑色方块）</summary>
        public bool ShowBackdrop { get; set; } = true;

        private void Init()
        {
            // 背景
            _bgPaint = new Paint { AntiAlias = true };

            // 轮辋
            _rimPaint = new Paint { AntiAlias = true };
            _rimPaint.SetStyle(Paint.Style.Stroke);

            // 轮辋外发光（赛车霓虹效果）
            _rimGlowPaint = new Paint { AntiAlias = true };
            _rimGlowPaint.SetStyle(Paint.Style.Stroke);
            _rimGlowPaint.StrokeWidth = 8f;
            _rimGlowPaint.Color = Color.Argb(60, 0, 200, 255); // 青蓝发光
            _rimGlowPaint.SetShadowLayer(12f, 0, 0, Color.Argb(120, 0, 200, 255));
            // 辐条 — 碳纤维黑
            _spokePaint = new Paint { AntiAlias = true };
            _spokePaint.SetStyle(Paint.Style.Fill);

            // 中心 Hub
            _hubPaint = new Paint { AntiAlias = true };
            _hubPaint.SetStyle(Paint.Style.Fill);

            _hubInnerPaint = new Paint { AntiAlias = true };
            _hubInnerPaint.SetStyle(Paint.Style.Fill);

            // 顶部正位标记 — 赛车红
            _markerPaint = new Paint { AntiAlias = true };
            _markerPaint.SetStyle(Paint.Style.FillAndStroke);
            _markerPaint.StrokeWidth = 2f;
            _markerPaint.Color = Color.Argb(255, 255, 50, 50);
            _markerPaint.SetShadowLayer(8f, 0, 0, Color.Argb(180, 255, 50, 50));

            // 中心状态文字（连接状态，由 CenterText 设置）
            _angleTextPaint = new Paint
            {
                AntiAlias = true,
                Color = Color.Argb(255, 0, 255, 200),  // 青绿色 HUD 文字
                TextSize = 42f,
                TextAlign = Paint.Align.Center,
                FakeBoldText = true,
            };
            _angleTextPaint.SetShadowLayer(6f, 0, 0, Color.Argb(150, 0, 255, 200));

            // 已连接指示灯（绿色圆点）
            _dotPaint = new Paint
            {
                AntiAlias = true,
                Color = Color.Argb(255, 76, 175, 80),  // 绿色
            };

            // 背景弧
            _arcBgPaint = new Paint { AntiAlias = true };
            _arcBgPaint.SetStyle(Paint.Style.Stroke);
            _arcBgPaint.StrokeWidth = 4f;
            _arcBgPaint.StrokeCap = Paint.Cap.Round;
            _arcBgPaint.Color = Color.Argb(40, 120, 140, 160);

            // 激活角度弧 — 发光青色
            _arcActivePaint = new Paint { AntiAlias = true };
            _arcActivePaint.SetStyle(Paint.Style.Stroke);
            _arcActivePaint.StrokeWidth = 6f;
            _arcActivePaint.StrokeCap = Paint.Cap.Round;
            _arcActivePaint.Color = Color.Argb(220, 0, 220, 255);
            _arcActivePaint.SetShadowLayer(10f, 0, 0, Color.Argb(180, 0, 220, 255));

            // 中心表盘外环
            _centerRingPaint = new Paint { AntiAlias = true };
            _centerRingPaint.SetStyle(Paint.Style.Stroke);
            _centerRingPaint.StrokeWidth = 2f;
            _centerRingPaint.Color = Color.Argb(100, 0, 200, 255);
        }

        protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
        {
            base.OnSizeChanged(w, h, oldw, oldh);
            _centerX = w / 2f;
            _centerY = h / 2f;
            _radius = Math.Min(w, h) / 2f - 28f;
            _rimWidth = _radius * 0.12f;
            _rimPaint.StrokeWidth = _rimWidth;

            _clipCircle = new Path();
            _clipCircle.AddCircle(_centerX, _centerY, _radius + _rimWidth / 2f + 2f, Path.Direction.Cw);
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);

            DrawBackground(canvas);

            // 外圈发光弧 (固定，不旋转)
            DrawAngleArc(canvas);

            canvas.Save();
            canvas.Rotate(_smoothAngle, _centerX, _centerY);
            canvas.ClipPath(_clipCircle);

            // 轮辋外发光
            canvas.DrawCircle(_centerX, _centerY, _radius + _rimWidth / 2f + 4f, _rimGlowPaint);

            // 轮辋
            DrawRim(canvas);

            // 辐条 — 平底运动方向盘（Y 型）
            DrawFlatBottomSpokes(canvas);

            // 中心轴承
            DrawHub(canvas);

            canvas.Restore();

            // 固定标记
            DrawTopMarker(canvas);
            DrawTickMarks(canvas);
            DrawCenterHUD(canvas);
        }

        // ================================================================
        //  背景 — 深色径向渐变 + 外圈暗角
        // ================================================================
        private void DrawBackground(Canvas canvas)
        {
            if (!ShowBackdrop) return;

            float bgSize = _radius + 40f;
            var bgRect = new RectF(_centerX - bgSize, _centerY - bgSize,
                                   _centerX + bgSize, _centerY + bgSize);

            var bgGrad = new RadialGradient(_centerX, _centerY, bgSize,
                new int[] { Color.Argb(255, 22, 26, 35), Color.Argb(255, 10, 12, 18), Color.Argb(255, 5, 6, 10) },
                new float[] { 0f, 0.7f, 1f },
                Shader.TileMode.Clamp);
            _bgPaint.SetShader(bgGrad);
            canvas.DrawRoundRect(bgRect, 24f, 24f, _bgPaint);
            _bgPaint.SetShader(null);
        }

        // ================================================================
        //  轮辋 — 碳纤维纹理 + 红色缝线
        // ================================================================
        private void DrawRim(Canvas canvas)
        {
            // 主体：深色金属渐变
            var rimGradient = new SweepGradient(_centerX, _centerY,
                new int[] {
                    Color.Argb(255, 50, 52, 60),
                    Color.Argb(255, 30, 32, 38),
                    Color.Argb(255, 55, 57, 65),
                    Color.Argb(255, 28, 30, 36),
                    Color.Argb(255, 50, 52, 60)
                },
                new float[] { 0f, 0.25f, 0.5f, 0.75f, 1f });
            _rimPaint.SetShader(rimGradient);
            canvas.DrawCircle(_centerX, _centerY, _radius, _rimPaint);
            _rimPaint.SetShader(null);

            // 内外边缘亮线
            var edgePaint = new Paint { AntiAlias = true };
            edgePaint.SetStyle(Paint.Style.Stroke);
            edgePaint.StrokeWidth = 1.5f;

            edgePaint.Color = Color.Argb(120, 180, 200, 220);
            canvas.DrawCircle(_centerX, _centerY, _radius + _rimWidth / 2f - 1f, edgePaint);

            edgePaint.Color = Color.Argb(80, 100, 110, 130);
            canvas.DrawCircle(_centerX, _centerY, _radius - _rimWidth / 2f + 1f, edgePaint);

            // 红色缝线（12 点位置正中）
            var stitchPaint = new Paint { AntiAlias = true };
            stitchPaint.SetStyle(Paint.Style.Stroke);
            stitchPaint.StrokeWidth = 2f;
            stitchPaint.Color = Color.Argb(200, 220, 40, 40);
            float stitchR = _radius;
            float stitchArc = 8f;
            // 严格以 -90°（正上方）为中点对称分布
            float[] offsets = { -8f, 0f, 8f };
            foreach (float off in offsets)
            {
                float startAngle = -90 + off - stitchArc / 2f;
                var oval = new RectF(_centerX - stitchR, _centerY - stitchR,
                                     _centerX + stitchR, _centerY + stitchR);
                canvas.DrawArc(oval, startAngle, stitchArc, false, stitchPaint);
            }
        }

        // ================================================================
        //  平底运动方向盘辐条 (Y 型 / 两横一竖平底)
        // ================================================================
        private void DrawFlatBottomSpokes(Canvas canvas)
        {
            float hubRadius = _radius * 0.20f;
            float innerRimR = _radius - _rimWidth / 2f;
            float spokeHalfW = _radius * 0.07f;

            // 碳纤维渐变
            var spokeGrad = new LinearGradient(0, _centerY - spokeHalfW, 0, _centerY + spokeHalfW,
                new int[] { Color.Argb(255, 45, 47, 55), Color.Argb(255, 25, 27, 33), Color.Argb(255, 40, 42, 50) },
                new float[] { 0f, 0.5f, 1f },
                Shader.TileMode.Clamp);
            _spokePaint.SetShader(spokeGrad);

            // 左横杠
            DrawSpoke(canvas, _centerX - innerRimR, _centerY - spokeHalfW,
                      _centerX - hubRadius, _centerY + spokeHalfW);

            // 右横杠
            DrawSpoke(canvas, _centerX + hubRadius, _centerY - spokeHalfW,
                      _centerX + innerRimR, _centerY + spokeHalfW);

            // 上竖杠
            DrawSpoke(canvas, _centerX - spokeHalfW, _centerY - innerRimR,
                      _centerX + spokeHalfW, _centerY - hubRadius);

            _spokePaint.SetShader(null);

            // 辐条边缘高光
            var highlightPaint = new Paint { AntiAlias = true };
            highlightPaint.SetStyle(Paint.Style.Stroke);
            highlightPaint.StrokeWidth = 1f;
            highlightPaint.Color = Color.Argb(50, 200, 220, 255);
            canvas.DrawLine(_centerX - innerRimR, _centerY - spokeHalfW, _centerX - hubRadius, _centerY - spokeHalfW, highlightPaint);
            canvas.DrawLine(_centerX + hubRadius, _centerY - spokeHalfW, _centerX + innerRimR, _centerY - spokeHalfW, highlightPaint);
            canvas.DrawLine(_centerX - spokeHalfW, _centerY - innerRimR, _centerX - spokeHalfW, _centerY - hubRadius, highlightPaint);

            // 竖向梁中线（中心高亮）
            var centerLinePaint = new Paint { AntiAlias = true };
            centerLinePaint.SetStyle(Paint.Style.Stroke);
            centerLinePaint.StrokeWidth = 1.5f;
            centerLinePaint.Color = Color.Argb(160, 200, 220, 255);
            canvas.DrawLine(_centerX, _centerY - innerRimR, _centerX, _centerY - hubRadius, centerLinePaint);
        }

        private void DrawSpoke(Canvas canvas, float left, float top, float right, float bottom)
        {
            canvas.DrawRect(new RectF(left, top, right, bottom), _spokePaint);

            var edgePaint = new Paint { AntiAlias = true };
            edgePaint.SetStyle(Paint.Style.Stroke);
            edgePaint.StrokeWidth = 1f;
            edgePaint.Color = Color.Argb(100, 80, 85, 95);
            canvas.DrawRect(new RectF(left, top, right, bottom), edgePaint);
        }

        // ================================================================
        //  中心轴承 — 碳纤维 + 青色发光环
        // ================================================================
        private void DrawHub(Canvas canvas)
        {
            float hubR = _radius * 0.20f;

            // 外环 — 青色发光
            canvas.DrawCircle(_centerX, _centerY, hubR + 2f, _centerRingPaint);

            // 金属外圈
            var hubGrad = new RadialGradient(_centerX, _centerY, hubR,
                new int[] { Color.Argb(255, 90, 95, 110), Color.Argb(255, 50, 52, 62), Color.Argb(255, 30, 32, 40) },
                new float[] { 0f, 0.6f, 1f },
                Shader.TileMode.Clamp);
            _hubPaint.SetShader(hubGrad);
            canvas.DrawCircle(_centerX, _centerY, hubR, _hubPaint);
            _hubPaint.SetShader(null);

            // 内圈深色
            float innerR = hubR * 0.70f;
            var innerGrad = new RadialGradient(_centerX, _centerY, innerR,
                new int[] { Color.Argb(255, 20, 22, 30), Color.Argb(255, 8, 10, 15) },
                null, Shader.TileMode.Clamp);
            _hubInnerPaint.SetShader(innerGrad);
            canvas.DrawCircle(_centerX, _centerY, innerR, _hubInnerPaint);
            _hubInnerPaint.SetShader(null);

            // 内圈边框
            var innerEdge = new Paint { AntiAlias = true };
            innerEdge.SetStyle(Paint.Style.Stroke);
            innerEdge.StrokeWidth = 1.5f;
            innerEdge.Color = Color.Argb(120, 0, 180, 220);
            canvas.DrawCircle(_centerX, _centerY, innerR, innerEdge);
        }

        // ================================================================
        //  顶部正位标记 — 赛车红三角 + 发光
        // ================================================================
        private void DrawTopMarker(Canvas canvas)
        {
            float outerY = _centerY - _radius - _rimWidth / 2f - 6f;
            float innerY = outerY + 18f;
            float halfW = 8f;

            var path = new Path();
            path.MoveTo(_centerX, outerY);
            path.LineTo(_centerX - halfW, innerY);
            path.LineTo(_centerX + halfW, innerY);
            path.Close();
            canvas.DrawPath(path, _markerPaint);
        }

        // ================================================================
        //  刻度标记 — 每 15°，±90° 范围
        // ================================================================
        private void DrawTickMarks(Canvas canvas)
        {
            float outerBase = _radius - _rimWidth / 2f - 10f;
            for (int a = -90; a <= 90; a += 15)
            {
                float rad = a * MathF.PI / 180f;
                bool isMajor = a % 30 == 0;
                float tickLen = isMajor ? 12f : 6f;
                float innerBase = outerBase - tickLen;

                float cos = MathF.Cos(rad);
                float sin = MathF.Sin(rad);
                float x1 = _centerX + innerBase * sin;
                float y1 = _centerY - innerBase * cos;
                float x2 = _centerX + outerBase * sin;
                float y2 = _centerY - outerBase * cos;

                var tickPaint = new Paint { AntiAlias = true };
                tickPaint.SetStyle(Paint.Style.Stroke);
                tickPaint.StrokeCap = Paint.Cap.Round;
                tickPaint.StrokeWidth = isMajor ? 3f : 1.5f;

                if (a == 0)
                {
                    tickPaint.Color = Color.Argb(220, 255, 60, 60);
                    tickPaint.SetShadowLayer(4f, 0, 0, Color.Argb(100, 255, 60, 60));
                }
                else
                {
                    tickPaint.Color = Color.Argb(isMajor ? 160 : 80, 160, 180, 200);
                }
                canvas.DrawLine(x1, y1, x2, y2, tickPaint);
            }
        }

        // ================================================================
        //  角度弧线 — 发光青色，随角度增长
        // ================================================================
        private void DrawAngleArc(Canvas canvas)
        {
            float margin = 16f;
            var oval = new RectF(_centerX - _radius + margin, _centerY - _radius + margin,
                                 _centerX + _radius - margin, _centerY + _radius - margin);

            // 背景弧
            canvas.DrawArc(oval, -180, 180, false, _arcBgPaint);

            // 当前角度弧
            float sweep = -_angle;
            if (Math.Abs(sweep) > 0.5f)
            {
                canvas.DrawArc(oval, -90, sweep, false, _arcActivePaint);
            }
        }

        // ================================================================
        //  中心 HUD — 已连接=绿色圆点 / 未连接=状态文字（如"重连中..."）
        // ================================================================
        private void DrawCenterHUD(Canvas canvas)
        {
            // 已连接：绿色圆形指示灯（优先于文字）
            if (_connected)
            {
                float r = Math.Max(10f, _radius * 0.10f);
                _dotPaint.SetShadowLayer(12f, 0, 0, Color.Argb(170, 0, 255, 120));
                canvas.DrawCircle(_centerX, _centerY, r, _dotPaint);
                _dotPaint.SetShadowLayer(0, 0, 0, Color.Transparent);
                return;
            }

            // 未连接：按 CenterText 显示（如"重连中..."），空则不绘制
            if (string.IsNullOrEmpty(_centerText)) return;
            float textY = _centerY + _angleTextPaint.TextSize * 0.35f;
            canvas.DrawText(_centerText, _centerX, textY, _angleTextPaint);
        }
    }
}
