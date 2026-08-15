using System;
using Android.Content;
using Android.Graphics;
using Android.Util;
using Android.Views;

namespace WheelSimu
{
    /// <summary>
    /// 赛车风格踏板进度条 — 霓虹发光边框 + 光晕滑块 + 实时百分比
    /// 参考 Real Racing / 极限竞速 手游的踏板 HUD
    /// </summary>
    public class PedalGaugeView : View
    {
        private float _progress; // 0-100
        private string _label = "";
        private bool _isPressed; // 按下状态

        // 颜色
        private int _fillColor1, _fillColor2, _labelColor;
        private Android.Graphics.Color _accentColor;
        private Android.Graphics.Color _glowColor;  // 发光色（更亮版本）

        // Paints
        private Paint _bgPaint;
        private Paint _fillPaint;
        private Paint _shinePaint;
        private Paint _thumbPaint;
        private Paint _thumbGlowPaint;     // 滑块光晕
        private Paint _borderPaint;
        private Paint _borderGlowPaint;    // 边框发光
        private Paint _pctPaint;
        private Paint _labelPaint;
        private Paint _tickPaint;

        private RectF _drawRect;

        public event EventHandler<float> ProgressChanged;

        public PedalGaugeView LinkedPedal { get; set; }

        public PedalGaugeView(Context context) : base(context) => Init();
        public PedalGaugeView(Context context, IAttributeSet attrs) : base(context, attrs) => Init();
        public PedalGaugeView(Context context, IAttributeSet attrs, int defStyleAttr) : base(context, attrs, defStyleAttr) => Init();

        public float Progress
        {
            get => _progress;
            set
            {
                _progress = Math.Clamp(value, 0, 100);
                Invalidate();
            }
        }

        public void SetColors(int fillColor1, int fillColor2, int labelColor)
        {
            _fillColor1 = fillColor1;
            _fillColor2 = fillColor2;
            _labelColor = labelColor;
            _accentColor = new Color((int)labelColor);
            // 发光色 = 标签色提亮
            _glowColor = Color.Argb(180,
                Math.Min(255, _accentColor.R + 80),
                Math.Min(255, _accentColor.G + 80),
                Math.Min(255, _accentColor.B + 80));
            Invalidate();
        }

        public void SetLabel(string text)
        {
            _label = text ?? "";
            Invalidate();
        }

        private void Init()
        {
            _fillColor1 = Color.Argb(255, 56, 142, 60).ToArgb();
            _fillColor2 = Color.Argb(255, 27, 94, 32).ToArgb();
            _labelColor = Color.Argb(255, 76, 175, 80).ToArgb();
            _accentColor = Color.Argb(255, 76, 175, 80);
            _glowColor = Color.Argb(180, 156, 255, 180);

            _bgPaint = new Paint { AntiAlias = true };
            _bgPaint.SetStyle(Paint.Style.Fill);

            _fillPaint = new Paint { AntiAlias = true };
            _fillPaint.SetStyle(Paint.Style.Fill);

            _shinePaint = new Paint
            {
                AntiAlias = true,
                StrokeWidth = 3f,
                StrokeCap = Paint.Cap.Round,
            };
            _shinePaint.SetStyle(Paint.Style.Stroke);

            // 滑块
            _thumbPaint = new Paint { AntiAlias = true };
            _thumbPaint.SetStyle(Paint.Style.Fill);

            // 滑块光晕
            _thumbGlowPaint = new Paint { AntiAlias = true };
            _thumbGlowPaint.SetStyle(Paint.Style.Fill);

            // 百分比文字
            _pctPaint = new Paint
            {
                AntiAlias = true,
                TextSize = 26f,
                TextAlign = Paint.Align.Center,
                FakeBoldText = true,
                Color = Color.Argb(255, 255, 255, 255),
            };

            // 标签
            _labelPaint = new Paint
            {
                AntiAlias = true,
                TextSize = 32f,
                TextAlign = Paint.Align.Center,
                FakeBoldText = true,
            };

            // 边框
            _borderPaint = new Paint
            {
                AntiAlias = true,
                Color = Color.Argb(80, 200, 200, 210),
                StrokeWidth = 1.5f,
            };
            _borderPaint.SetStyle(Paint.Style.Stroke);

            // 边框发光
            _borderGlowPaint = new Paint
            {
                AntiAlias = true,
                StrokeWidth = 3f,
            };
            _borderGlowPaint.SetStyle(Paint.Style.Stroke);

            // 刻度
            _tickPaint = new Paint
            {
                AntiAlias = true,
                Color = Color.Argb(30, 220, 220, 230),
                StrokeWidth = 1f,
            };
            _tickPaint.SetStyle(Paint.Style.Stroke);

            _drawRect = new RectF();
            Clickable = true;
            Focusable = true;
        }

        protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
        {
            base.OnSizeChanged(w, h, oldw, oldh);
            _drawRect.Set(PaddingLeft, PaddingTop, w - PaddingRight, h - PaddingBottom);
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);

            float w = _drawRect.Width();
            float h = _drawRect.Height();
            float left = _drawRect.Left;
            float top = _drawRect.Top;

            if (w <= 0 || h <= 0) return;

            // 两列：左标签 30%，右刻度条 70%
            float textW = w * 0.30f;
            float barW = w - textW;
            float barLeft = left + textW;
            float barRight = barLeft + barW;
            float textCenterX = left + textW / 2f;

            float fillH = h * _progress / 100f;
            float fillTop = top + h - fillH;
            float cornerR = 10f;

            // === 刻度条背景 ===
            var barRect = new RectF(barLeft, top, barRight, top + h);
            var bgGrad = new LinearGradient(0, top, 0, top + h,
                new int[] { Color.Argb(255, 18, 22, 30).ToArgb(), Color.Argb(255, 8, 10, 14).ToArgb() },
                null, Shader.TileMode.Clamp);
            _bgPaint.SetShader(bgGrad);
            canvas.DrawRoundRect(barRect, cornerR, cornerR, _bgPaint);
            _bgPaint.SetShader(null);

            // === 刻度线 ===
            for (int i = 25; i < 100; i += 25)
            {
                float y = top + h - (h * i / 100f);
                canvas.DrawLine(barLeft + 6, y, barRight - 6, y, _tickPaint);
            }

            // === 彩色填充 ===
            if (fillH > 0)
            {
                var fillRect = new RectF(barLeft + 2, fillTop, barRight - 2, top + h - 2);
                var fillGrad = new LinearGradient(0, fillTop, 0, top + h,
                    new int[] { _fillColor1, _fillColor2 },
                    new float[] { 0f, 1f },
                    Shader.TileMode.Clamp);
                _fillPaint.SetShader(fillGrad);
                canvas.DrawRect(fillRect, _fillPaint);
                _fillPaint.SetShader(null);

                // 填充顶部发光亮线
                _shinePaint.Color = _glowColor;
                _shinePaint.Alpha = _isPressed ? 255 : 180;
                _shinePaint.SetShadowLayer(_isPressed ? 8f : 4f, 0, 0, _glowColor);
                canvas.DrawLine(barLeft + 4, fillTop, barRight - 4, fillTop, _shinePaint);
                _shinePaint.SetShadowLayer(0, 0, 0, Color.Transparent);
            }

            // === 滑块（长方形 + 发光） ===
            float thumbH = barW * 0.14f;
            float thumbW = barW * 0.60f;
            float thumbY = fillTop - thumbH / 2f;
            float thumbL = barLeft + (barW - thumbW) / 2f;
            var thumbRect = new RectF(thumbL, thumbY, thumbL + thumbW, thumbY + thumbH);

            // 光晕（按下时更强）
            _thumbGlowPaint.Color = _glowColor;
            _thumbGlowPaint.Alpha = _isPressed ? 100 : 40;
            _thumbGlowPaint.SetShadowLayer(_isPressed ? 12f : 6f, 0, 0, _glowColor);
            canvas.DrawRoundRect(thumbRect, 4f, 4f, _thumbGlowPaint);
            _thumbGlowPaint.SetShadowLayer(0, 0, 0, Color.Transparent);

            // 滑块本体 — 白色
            _thumbPaint.Color = Color.Argb(255, 240, 245, 250);
            canvas.DrawRoundRect(thumbRect, 4f, 4f, _thumbPaint);

            // 滑块内部颜色条
            var innerPaint = new Paint { AntiAlias = true };
            innerPaint.SetStyle(Paint.Style.Fill);
            innerPaint.Color = _accentColor;
            var innerRect = new RectF(thumbL + 3, thumbY + 3, thumbL + thumbW - 3, thumbY + thumbH - 3);
            canvas.DrawRoundRect(innerRect, 2f, 2f, innerPaint);

            // === 边框（发光，按下时更强） ===
            _borderGlowPaint.Color = _glowColor;
            _borderGlowPaint.Alpha = _isPressed ? 150 : 50;
            _borderGlowPaint.SetShadowLayer(_isPressed ? 8f : 3f, 0, 0, _glowColor);
            canvas.DrawRoundRect(barRect, cornerR, cornerR, _borderGlowPaint);
            _borderGlowPaint.SetShadowLayer(0, 0, 0, Color.Transparent);

            canvas.DrawRoundRect(barRect, cornerR, cornerR, _borderPaint);

            // === 百分比文字（刻度条内底部） ===
            _pctPaint.SetShadowLayer(3f, 0, 1f, Color.Argb(200, 0, 0, 0));
            float pctX = barLeft + barW / 2f;
            float pctY = top + h - _pctPaint.TextSize * 0.5f;
            canvas.DrawText($"{_progress:F0}", pctX, pctY, _pctPaint);
            _pctPaint.SetShadowLayer(0, 0, 0, Color.Transparent);

            // === 标签文字（左侧竖排居中） ===
            if (!string.IsNullOrEmpty(_label))
            {
                _labelPaint.Color = new Color(_labelColor);
                _labelPaint.SetShadowLayer(4f, 0, 1f, Color.Argb(180, 0, 0, 0));

                float labelH = _labelPaint.Descent() - _labelPaint.Ascent();
                float totalH = labelH * _label.Length;
                float startY = top + (h - totalH) / 2f - _labelPaint.Ascent();

                for (int i = 0; i < _label.Length; i++)
                {
                    float cy = startY + labelH * i;
                    canvas.DrawText(_label[i].ToString(), textCenterX, cy, _labelPaint);
                }
                _labelPaint.SetShadowLayer(0, 0, 0, Color.Transparent);
            }
        }

        public override bool OnTouchEvent(MotionEvent e)
        {
            if (!Enabled) return base.OnTouchEvent(e);

            float h = _drawRect.Height();
            float top = _drawRect.Top;

            var action = e.ActionMasked;
            if (action == MotionEventActions.Down || action == MotionEventActions.Move)
            {
                _isPressed = true;
                float y = e.GetY();
                float newProgress = 100f - ((y - top) / h * 100f);
                newProgress = Math.Clamp(newProgress, 0, 100);

                if (Math.Abs(newProgress - _progress) > 0.5f || action == MotionEventActions.Down)
                {
                    _progress = newProgress;
                    if (_progress > 0 && LinkedPedal != null && LinkedPedal.Progress > 0)
                        LinkedPedal.Progress = 0;
                    ProgressChanged?.Invoke(this, _progress);
                    Invalidate();
                }
                return true;
            }
            else if (action == MotionEventActions.Up || action == MotionEventActions.Cancel)
            {
                _isPressed = false;
                Invalidate();
            }
            return base.OnTouchEvent(e);
        }
    }
}
