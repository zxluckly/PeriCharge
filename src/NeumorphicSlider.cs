using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PeripheralBattery
{
    /// <summary>
    /// 新拟态（Neumorphism）滑块，给详情面板里的显示器亮度用。
    ///
    /// 画法：凹槽是「上暗下亮」的渐变，圆形滑块用一圈逐渐变淡的椭圆堆出
    /// 右下暗影 + 左上高光，再叠一个偏移的径向渐变做球面受光。
    /// 不依赖 ControlTemplate 或 Effect：Effect 模糊边缘在 100% 缩放下会发虚，
    /// 分层椭圆在任意 DPI 下都稳定，也省掉每个滑块一个 ShaderEffect 的开销。
    ///
    /// 只处理鼠标拖拽；面板是鼠标驱动的浮层，不需要键盘/焦点。
    /// 程序化改值走 <see cref="SetValue"/>（不触发事件），用户拖拽才触发
    /// <see cref="ValueChanged"/>，避免和悬浮条同步时来回递归。
    /// </summary>
    internal sealed class NeumorphicSlider : FrameworkElement
    {
        private const double ControlHeight = 26;
        private const double ThumbRadius = 9;
        private const double TrackHeight = 6;
        private const double SideInset = 8;

        private struct ShadowLayer
        {
            internal SolidColorBrush Brush;
            internal double OffsetX;
            internal double OffsetY;
            internal double Radius;
        }

        private readonly Color _surface;
        private readonly bool _light;
        private readonly LinearGradientBrush _grooveBrush;
        private readonly Pen _groovePen;
        private readonly RadialGradientBrush _thumbBody;
        private readonly Pen _thumbRing;
        private readonly List<ShadowLayer> _darkShadows = new List<ShadowLayer>();
        private readonly List<ShadowLayer> _lightShadows = new List<ShadowLayer>();

        private LinearGradientBrush _fillBrush;
        private Color _accent;
        private double _value;
        private bool _dragging;

        /// <summary>用户拖拽改变数值时触发（程序化 SetValue 不触发）。</summary>
        internal event EventHandler ValueChanged;

        private NeumorphicSlider(Color accent, Color surface, bool light, double value)
        {
            _surface = surface;
            _light = light;
            _accent = accent;
            _value = Clamp(value);

            Height = ControlHeight;
            Cursor = Cursors.Hand;
            SnapsToDevicePixels = true;
            Focusable = false;

            // 凹槽：顶部压暗、底部提亮，读起来像凹进面板
            _grooveBrush = new LinearGradientBrush(
                Scale(surface, light ? 0.80 : 0.55),
                Scale(surface, light ? 1.16 : 1.35),
                new Point(0, 0), new Point(0, 1));
            _grooveBrush.Freeze();

            LinearGradientBrush edge = new LinearGradientBrush(
                Color.FromArgb(light ? (byte)90 : (byte)130, 0, 0, 0),
                Color.FromArgb(light ? (byte)85 : (byte)70, 255, 255, 255),
                new Point(0, 0), new Point(0, 1));
            edge.Freeze();
            _groovePen = new Pen(edge, 1);
            _groovePen.Freeze();

            // 滑块本体：左上受光、右下背光
            _thumbBody = new RadialGradientBrush();
            _thumbBody.Center = new Point(0.35, 0.32);
            _thumbBody.GradientOrigin = new Point(0.35, 0.32);
            _thumbBody.RadiusX = 0.78;
            _thumbBody.RadiusY = 0.78;
            _thumbBody.GradientStops.Add(new GradientStop(Scale(surface, light ? 1.10 : 1.45), 0));
            _thumbBody.GradientStops.Add(new GradientStop(surface, 0.58));
            _thumbBody.GradientStops.Add(new GradientStop(Scale(surface, light ? 0.90 : 0.80), 1));
            _thumbBody.Freeze();

            _thumbRing = new Pen(
                new SolidColorBrush(Color.FromArgb(light ? (byte)26 : (byte)44, 0, 0, 0)), 1);
            _thumbRing.Freeze();

            BuildShadowLayers();
            _fillBrush = CreateFillBrush(accent);
        }

        /// <summary>percent 小于 0（没读到亮度）时滑块停在中间，拖动即接管。</summary>
        internal static NeumorphicSlider Create(int percent, Color accent, Color surface, bool light)
        {
            return new NeumorphicSlider(accent, surface, light, percent < 0 ? 50 : percent);
        }

        internal double Value
        {
            get { return _value; }
        }

        internal bool IsDragging
        {
            get { return _dragging; }
        }

        /// <summary>程序化改值：只重画，不触发 ValueChanged。</summary>
        internal void SetValue(int value)
        {
            SetValueCore(value, false);
        }

        /// <summary>跨过红/黄/绿分界时换填充色。</summary>
        internal void SetAccent(Color accent)
        {
            if (_accent == accent)
            {
                return;
            }

            _accent = accent;
            _fillBrush = CreateFillBrush(accent);
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
            return new Size(width, ControlHeight);
        }

        private double ThumbCenterX
        {
            get
            {
                double min = SideInset + ThumbRadius;
                double max = Math.Max(min + 1, ActualWidth - SideInset - ThumbRadius);
                return min + (max - min) * _value / 100.0;
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 2 * (SideInset + ThumbRadius) || height <= 0)
            {
                return;
            }

            // 透明底把整个 26px 高度都纳入命中区，点在槽外也能开始拖
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

            double centerY = height / 2;
            double radius = TrackHeight / 2;
            Rect track = new Rect(SideInset, centerY - radius, width - SideInset * 2, TrackHeight);

            dc.DrawRoundedRectangle(_grooveBrush, _groovePen, track, radius, radius);

            // 已填充段裁到滑块中心；右端的直角被滑块盖住，看不出来
            double thumbX = ThumbCenterX;
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, thumbX, height)));
            dc.DrawRoundedRectangle(_fillBrush, null, track, radius, radius);
            dc.Pop();

            DrawThumb(dc, thumbX, centerY);
        }

        private void DrawThumb(DrawingContext dc, double centerX, double centerY)
        {
            // 右下暗影：大而淡的椭圆叠到小而深，避免单个 Effect 的硬边
            for (int i = 0; i < _darkShadows.Count; i++)
            {
                ShadowLayer layer = _darkShadows[i];
                dc.DrawEllipse(layer.Brush, null,
                    new Point(centerX + layer.OffsetX, centerY + layer.OffsetY),
                    layer.Radius, layer.Radius);
            }

            // 左上高光
            for (int i = 0; i < _lightShadows.Count; i++)
            {
                ShadowLayer layer = _lightShadows[i];
                dc.DrawEllipse(layer.Brush, null,
                    new Point(centerX + layer.OffsetX, centerY + layer.OffsetY),
                    layer.Radius, layer.Radius);
            }

            dc.DrawEllipse(_thumbBody, _thumbRing,
                new Point(centerX, centerY), ThumbRadius, ThumbRadius);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            CaptureMouse();
            _dragging = true;
            UpdateFromX(e.GetPosition(this).X);
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (!_dragging)
            {
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _dragging = false;
                ReleaseMouseCapture();
                return;
            }

            UpdateFromX(e.GetPosition(this).X);
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);

            if (!_dragging)
            {
                return;
            }

            UpdateFromX(e.GetPosition(this).X);
            _dragging = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            _dragging = false;
            base.OnLostMouseCapture(e);
        }

        private void UpdateFromX(double x)
        {
            double min = SideInset + ThumbRadius;
            double max = Math.Max(min + 1, ActualWidth - SideInset - ThumbRadius);
            SetValueCore((x - min) * 100.0 / (max - min), true);
        }

        private void SetValueCore(double value, bool raise)
        {
            double next = Clamp(value);
            if (Math.Abs(next - _value) < 0.01)
            {
                return;
            }

            _value = next;
            InvalidateVisual();

            if (raise)
            {
                EventHandler handler = ValueChanged;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
        }

        private void BuildShadowLayers()
        {
            for (int i = 5; i >= 1; i--)
            {
                byte alpha = (byte)(_light ? 8 + (5 - i) * 3 : 13 + (5 - i) * 4);
                SolidColorBrush brush = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
                brush.Freeze();

                ShadowLayer layer;
                layer.Brush = brush;
                layer.OffsetX = 1.6 + i * 0.35;
                layer.OffsetY = 2.0 + i * 0.4;
                layer.Radius = ThumbRadius + i * 0.9;
                _darkShadows.Add(layer);
            }

            for (int i = 4; i >= 1; i--)
            {
                byte alpha = (byte)(_light ? 13 + (4 - i) * 5 : 6 + (4 - i) * 3);
                SolidColorBrush brush = new SolidColorBrush(
                    Color.FromArgb(alpha, 255, 255, 255));
                brush.Freeze();

                ShadowLayer layer;
                layer.Brush = brush;
                layer.OffsetX = -1.1 - i * 0.25;
                layer.OffsetY = -1.4 - i * 0.3;
                layer.Radius = ThumbRadius + i * 0.7;
                _lightShadows.Add(layer);
            }
        }

        private static LinearGradientBrush CreateFillBrush(Color accent)
        {
            LinearGradientBrush brush = new LinearGradientBrush(
                Scale(accent, 1.10), Scale(accent, 0.94),
                new Point(0, 0), new Point(0, 1));
            brush.Freeze();
            return brush;
        }

        private static double Clamp(double value)
        {
            return Math.Max(0, Math.Min(100, Math.Round(value)));
        }

        private static Color Scale(Color color, double factor)
        {
            return Color.FromArgb(
                color.A,
                (byte)Math.Max(0, Math.Min(255, color.R * factor)),
                (byte)Math.Max(0, Math.Min(255, color.G * factor)),
                (byte)Math.Max(0, Math.Min(255, color.B * factor)));
        }
    }
}
