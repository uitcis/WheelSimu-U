using System;
using Android.Content;
using Android.Graphics;
using Android.Views;

namespace WheelSimu
{
    /// <summary>
    /// 布局2 虚拟摇杆：底座 + 可拖动蘑菇头。
    /// 拖动输出 -100..100 模拟轴量（PercentX/PercentY，Y 轴上为负，与游戏摇杆约定一致），
    /// 松手自动回中并停止上报（IsTouched=false）。
    /// 触摸期间 IsTouched=true，同时充当 L3/R3（LS/RS）按钮按下位。
    /// </summary>
    public class JoystickView : View
    {
        readonly Paint _baseFill;
        readonly Paint _baseRing;
        readonly Paint _knobFill;
        readonly Paint _knobRing;
        readonly Paint _labelPaint;

        float _dragRadius;   // 蘑菇头中心最大偏移（px）
        float _offX, _offY;  // 蘑菇头相对中心偏移（px）
        int _pointerId = -1;

        /// <summary>底座中央文字（LS/RS）</summary>
        public string Label { get; set; } = "";

        /// <summary>是否正被触摸（拖动中）</summary>
        public bool IsTouched { get; private set; }

        /// <summary>X 轴百分比：-100(左)..100(右)</summary>
        public int PercentX { get; private set; }

        /// <summary>Y 轴百分比：-100(上)..100(下)，上为负</summary>
        public int PercentY { get; private set; }

        public JoystickView(Context ctx) : base(ctx)
        {
            _baseFill = new Paint(PaintFlags.AntiAlias) { Color = Color.Argb(38, 255, 255, 255) };
            _baseRing = new Paint(PaintFlags.AntiAlias)
            {
                Color = Color.Argb(130, 128, 136, 144),
                StrokeWidth = 3f,
            };
            _baseRing.SetStyle(Paint.Style.Stroke);

            _knobFill = new Paint(PaintFlags.AntiAlias) { Color = Color.Argb(210, 38, 46, 58) };
            _knobRing = new Paint(PaintFlags.AntiAlias)
            {
                Color = Color.Argb(255, 79, 195, 247),
                StrokeWidth = 3f,
            };
            _knobRing.SetStyle(Paint.Style.Stroke);

            _labelPaint = new Paint(PaintFlags.AntiAlias)
            {
                Color = Color.Argb(255, 79, 195, 247),
                TextAlign = Paint.Align.Center,
                FakeBoldText = true,
            };
        }

        protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
        {
            base.OnSizeChanged(w, h, oldw, oldh);
            float knobDia = w * 0.44f;                       // 蘑菇头直径
            _dragRadius = w / 2f - knobDia / 2f - 2f;        // 可拖动半径
            _labelPaint.TextSize = knobDia * 0.30f;
        }

        public override bool OnTouchEvent(MotionEvent e)
        {
            switch (e.ActionMasked)
            {
                case MotionEventActions.Down:
                    _pointerId = e.GetPointerId(0);
                    IsTouched = true;
                    Update(e);
                    Parent?.RequestDisallowInterceptTouchEvent(true);
                    return true;

                case MotionEventActions.Move:
                    if (_pointerId >= 0 && e.FindPointerIndex(_pointerId) >= 0)
                    {
                        Update(e);
                        return true;
                    }
                    break;

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    _pointerId = -1;
                    IsTouched = false;
                    Reset();
                    return true;
            }
            return base.OnTouchEvent(e);
        }

        void Update(MotionEvent e)
        {
            int i = e.FindPointerIndex(_pointerId);
            float cx = Width / 2f, cy = Height / 2f;
            float dx = e.GetX(i) - cx, dy = e.GetY(i) - cy;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len > _dragRadius && len > 0)
            {
                dx = dx / len * _dragRadius;
                dy = dy / len * _dragRadius;
            }
            _offX = dx;
            _offY = dy;
            PercentX = (int)Math.Round(dx / _dragRadius * 100);
            PercentY = (int)Math.Round(dy / _dragRadius * 100);
            Invalidate();
        }

        void Reset()
        {
            _offX = _offY = 0;
            PercentX = PercentY = 0;
            Invalidate();
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            float cx = Width / 2f, cy = Height / 2f;
            float r = Width / 2f - 3f;

            // 底座
            canvas.DrawCircle(cx, cy, r, _baseFill);
            canvas.DrawCircle(cx, cy, r, _baseRing);

            // 蘑菇头
            float kx = cx + _offX, ky = cy + _offY;
            float kr = Width * 0.22f;
            canvas.DrawCircle(kx, ky, kr, _knobFill);
            canvas.DrawCircle(kx, ky, kr, _knobRing);

            if (!string.IsNullOrEmpty(Label))
            {
                var textBounds = new Rect();
                _labelPaint.GetTextBounds(Label, 0, Label.Length, textBounds);
                float ty = ky - textBounds.ExactCenterY();
                canvas.DrawText(Label, kx, ty, _labelPaint);
            }
        }
    }
}
