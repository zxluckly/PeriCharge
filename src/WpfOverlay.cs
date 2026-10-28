using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PeripheralBattery
{
    /// <summary>
    /// 嵌入任务栏的 WPF 透明悬浮条（与 Lyricify 相同的实现方式）。
    /// WPF Window + AllowsTransparency 提供逐像素透明，SetParent 到 Shell_TrayWnd 实现嵌入。
    /// </summary>
    internal sealed class WpfOverlay : Window
    {
        private readonly Border _root;
        private readonly StackPanel _content;
        private readonly Border _refreshButton;
        private readonly TextBlock _refreshGlyph;
        private readonly SolidColorBrush _buttonHoverBrush;
        private readonly ScaleTransform _buttonScale;
        private readonly RotateTransform _glyphRotate;
        private readonly LinearGradientBrush _backgroundBrush;
        private readonly SolidColorBrush _borderBrush;
        private readonly uint _taskbarCreatedMessage;

        private DeviceBattery[] _items = new DeviceBattery[0];
        private string _emptyText = "正在扫描蓝牙设备…";
        private bool _hovered;
        private bool _enabled;
        private int _hoverColorArgb;
        private int _hoverAlpha = Settings.DefaultHoverAlpha;
        private bool _destroyed;
        private IntPtr _taskbar;
        private HwndSource _source;

        internal event EventHandler RefreshRequested;

        /// <summary>
        /// 窗口被外部销毁（资源管理器重启导致父任务栏窗口级联销毁）后，
        /// WPF Window 无法再 Show，必须由外部重建新实例。
        /// </summary>
        internal bool IsDestroyed
        {
            get { return _destroyed; }
        }

        internal WpfOverlay()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            ShowActivated = false;
            Topmost = false;

            _root = new Border();
            _root.CornerRadius = new CornerRadius(9);
            _root.BorderThickness = new Thickness(1);
            _root.Padding = new Thickness(14, 0, 14, 0);
            _root.MinHeight = 34;

            _backgroundBrush = new LinearGradientBrush();
            _backgroundBrush.StartPoint = new Point(0, 0);
            _backgroundBrush.EndPoint = new Point(0, 1);
            _backgroundBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            _backgroundBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            // alpha 不能为 0：分层窗口在完全透明区域不接收鼠标，保留 1/255 以便悬停命中
            _backgroundBrush.Opacity = 1.0 / 255.0;

            _borderBrush = new SolidColorBrush(Colors.Transparent);
            _borderBrush.Opacity = 1.0 / 255.0;

            _root.Background = _backgroundBrush;
            _root.BorderBrush = _borderBrush;

            DockPanel dock = new DockPanel();
            _content = new StackPanel();
            _content.Orientation = Orientation.Horizontal;
            _content.VerticalAlignment = VerticalAlignment.Center;

            _refreshButton = new Border();
            _refreshButton.Width = 22;
            _refreshButton.Height = 22;
            _refreshButton.CornerRadius = new CornerRadius(11);
            _refreshButton.Margin = new Thickness(8, 0, 0, 0);
            _refreshButton.Cursor = System.Windows.Input.Cursors.Hand;
            _refreshButton.Visibility = Visibility.Collapsed;
            _refreshButton.RenderTransformOrigin = new Point(0.5, 0.5);
            _buttonScale = new ScaleTransform(1, 1);
            _refreshButton.RenderTransform = _buttonScale;

            _refreshGlyph = new TextBlock();
            _refreshGlyph.Text = "\uE72C";
            _refreshGlyph.FontFamily = new FontFamily("Segoe MDL2 Assets");
            _refreshGlyph.FontSize = 12;
            _refreshGlyph.HorizontalAlignment = HorizontalAlignment.Center;
            _refreshGlyph.VerticalAlignment = VerticalAlignment.Center;
            _refreshGlyph.RenderTransformOrigin = new Point(0.5, 0.5);
            _glyphRotate = new RotateTransform(0);
            _refreshGlyph.RenderTransform = _glyphRotate;

            _buttonHoverBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255));
            _buttonHoverBrush.Opacity = 0;
            Border hoverLayer = new Border();
            hoverLayer.CornerRadius = new CornerRadius(11);
            hoverLayer.Background = _buttonHoverBrush;

            Grid buttonGrid = new Grid();
            buttonGrid.Children.Add(hoverLayer);
            buttonGrid.Children.Add(_refreshGlyph);
            _refreshButton.Child = buttonGrid;

            _refreshButton.MouseEnter += delegate
            {
                AnimateBrush(_buttonHoverBrush, 1.0, 120);
            };
            _refreshButton.MouseLeave += delegate
            {
                AnimateBrush(_buttonHoverBrush, 0.0, 150);
                AnimateScale(1.0, 120);
            };
            _refreshButton.MouseLeftButtonDown += delegate(object sender, System.Windows.Input.MouseButtonEventArgs e)
            {
                e.Handled = true;
                AnimateScale(0.86, 80);
            };
            _refreshButton.MouseLeftButtonUp += delegate(object sender, System.Windows.Input.MouseButtonEventArgs e)
            {
                e.Handled = true;
                AnimateScale(1.0, 120);
                AnimateGlyphRotation();

                EventHandler handler = RefreshRequested;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            };

            DockPanel.SetDock(_refreshButton, Dock.Right);
            dock.Children.Add(_refreshButton);
            dock.Children.Add(_content);
            _root.Child = dock;
            Content = _root;

            _root.MouseLeftButtonUp += delegate(object sender, System.Windows.Input.MouseButtonEventArgs e)
            {
                e.Handled = true;
                EventHandler handler = RefreshRequested;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            };
            _root.MouseEnter += delegate { SetHovered(true); };
            _root.MouseLeave += delegate { SetHovered(false); };

            _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            ApplyTheme();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _source = PresentationSource.FromVisual(this) as HwndSource;
            if (_source != null)
            {
                _source.AddHook(WndProc);
            }

            AttachToTaskbar();
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            AttachToTaskbar();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // 作为 Shell_TrayWnd 的子窗口，任务栏销毁时会被级联销毁；
            // 收到销毁消息后标记不可用，避免后续 Show/定位抛异常。
            if (msg == NativeMethods.WM_DESTROY || msg == NativeMethods.WM_NCDESTROY)
            {
                _destroyed = true;
                _taskbar = IntPtr.Zero;
            }

            if (msg == (int)_taskbarCreatedMessage)
            {
                AttachToTaskbar();
                handled = true;
            }

            return IntPtr.Zero;
        }

        internal void SetHoverStyle(int argb, int alpha)
        {
            _hoverColorArgb = argb;
            // 下限留 1：完全透明的分层窗口不接收鼠标，悬浮条会变得既悬停不了也点不到
            _hoverAlpha = Math.Max(1, Math.Min(255, alpha));
            ApplyTheme();
        }

        internal void SetEnabled(bool enabled)
        {
            if (_destroyed)
            {
                return;
            }

            _enabled = enabled;
            if (!enabled)
            {
                Hide();
                return;
            }

            if (!IsVisible)
            {
                Show();
            }

            AttachToTaskbar();
        }

        internal void UpdateItems(DeviceBattery[] items)
        {
            _items = items;
            _emptyText = items.Length == 0 ? "未发现可读取电量的设备" : string.Empty;
            if (_destroyed)
            {
                return;
            }

            RebuildContent();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdatePosition));
        }

        internal void SetScanning(bool scanning)
        {
            if (_destroyed)
            {
                return;
            }

            if (scanning && _items.Length == 0)
            {
                _emptyText = "正在扫描蓝牙设备…";
                RebuildContent();
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdatePosition));
            }
        }

        internal void RefreshPosition()
        {
            if (_enabled && !_destroyed)
            {
                AttachToTaskbar();
            }
        }

        private void AttachToTaskbar()
        {
            if (!_enabled || _destroyed)
            {
                return;
            }

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (hwnd == IntPtr.Zero || taskbar == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                return;
            }

            _taskbar = taskbar;
            if (NativeMethods.GetParent(hwnd) != taskbar)
            {
                NativeMethods.SetParent(hwnd, taskbar);
                int style = NativeMethods.GetWindowLong(hwnd, -16);
                style = (style & ~unchecked((int)0x80000000)) | 0x40000000;
                NativeMethods.SetWindowLong(hwnd, -16, style);
            }

            UpdatePosition();
        }

        private void UpdatePosition()
        {
            if (!_enabled || _destroyed)
            {
                return;
            }

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || _taskbar == IntPtr.Zero
                || !NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindow(_taskbar))
            {
                return;
            }

            // WPF 在初始化/布局后可能重置父窗口，这里每次定位前校正
            if (NativeMethods.GetParent(hwnd) != _taskbar)
            {
                NativeMethods.SetParent(hwnd, _taskbar);
                int style = NativeMethods.GetWindowLong(hwnd, -16);
                style = (style & ~unchecked((int)0x80000000)) | 0x40000000;
                NativeMethods.SetWindowLong(hwnd, -16, style);
            }

            NativeMethods.RECT trayRect;
            if (!NativeMethods.GetWindowRect(_taskbar, out trayRect))
            {
                return;
            }

            IntPtr notify = NativeMethods.FindWindowEx(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            int rightEdge = trayRect.Right - 220;
            if (notify != IntPtr.Zero)
            {
                NativeMethods.RECT notifyRect;
                if (NativeMethods.GetWindowRect(notify, out notifyRect) && notifyRect.Left > trayRect.Left)
                {
                    rightEdge = notifyRect.Left - 8;
                }
            }

            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (scale <= 0)
            {
                scale = 1;
            }

            int width = (int)Math.Ceiling(ActualWidth * scale);
            int height = (int)Math.Ceiling(ActualHeight * scale);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            int x = rightEdge - width;
            int y = trayRect.Top + (trayRect.Bottom - trayRect.Top - height) / 2;

            NativeMethods.POINT clientPoint = new NativeMethods.POINT();
            clientPoint.X = x;
            clientPoint.Y = y;
            NativeMethods.ScreenToClient(_taskbar, ref clientPoint);

            NativeMethods.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                clientPoint.X,
                clientPoint.Y,
                width,
                height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        private void SetHovered(bool hovered)
        {
            if (_hovered == hovered)
            {
                return;
            }

            _hovered = hovered;
            _refreshButton.Visibility = hovered ? Visibility.Visible : Visibility.Collapsed;
            ApplyTheme();
            AnimateBackground(hovered);
            UpdateLayout();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdatePosition));
        }

        private void AnimateBackground(bool visible)
        {
            DoubleAnimation animation = new DoubleAnimation(
                visible ? 1.0 : 1.0 / 255.0,
                new Duration(TimeSpan.FromMilliseconds(170)));
            animation.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };

            _backgroundBrush.BeginAnimation(Brush.OpacityProperty, animation);
            _borderBrush.BeginAnimation(Brush.OpacityProperty, animation);
        }

        private void AnimateBrush(SolidColorBrush brush, double target, int milliseconds)
        {
            DoubleAnimation animation = new DoubleAnimation(
                target, new Duration(TimeSpan.FromMilliseconds(milliseconds)));
            animation.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            brush.BeginAnimation(Brush.OpacityProperty, animation);
        }

        private void AnimateScale(double target, int milliseconds)
        {
            DoubleAnimation animation = new DoubleAnimation(
                target, new Duration(TimeSpan.FromMilliseconds(milliseconds)));
            animation.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            _buttonScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            _buttonScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }

        private void AnimateGlyphRotation()
        {
            _glyphRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            _glyphRotate.Angle = 0;
            DoubleAnimation animation = new DoubleAnimation(
                360.0, new Duration(TimeSpan.FromMilliseconds(320)));
            animation.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            _glyphRotate.BeginAnimation(RotateTransform.AngleProperty, animation);
        }

        private void ApplyTheme()
        {
            Color top;
            Color bottom;

            if (_hoverColorArgb != 0)
            {
                byte red = (byte)((_hoverColorArgb >> 16) & 0xFF);
                byte green = (byte)((_hoverColorArgb >> 8) & 0xFF);
                byte blue = (byte)(_hoverColorArgb & 0xFF);
                top = Color.FromArgb(255, red, green, blue);
                bottom = top;
            }
            else
            {
                // 跟随系统只决定颜色，不透明度仍由用户设置决定
                top = ToMediaColor(UiTheme.BackgroundTop);
                bottom = ToMediaColor(UiTheme.BackgroundBottom);
            }

            int alpha = _hoverAlpha;

            if (_backgroundBrush.GradientStops.Count >= 2)
            {
                _backgroundBrush.GradientStops[0].Color = Color.FromArgb((byte)alpha, top.R, top.G, top.B);
                _backgroundBrush.GradientStops[1].Color = Color.FromArgb((byte)alpha, bottom.R, bottom.G, bottom.B);
            }

            _borderBrush.Color = ToMediaColor(UiTheme.Border);

            Color text = ToMediaColor(UiTheme.TextPrimary);
            _buttonHoverBrush.Color = Color.FromArgb(60, text.R, text.G, text.B);
            _refreshButton.Background = new SolidColorBrush(Color.FromArgb(30, text.R, text.G, text.B));
            _refreshButton.BorderBrush = new SolidColorBrush(Color.FromArgb(60, text.R, text.G, text.B));
            _refreshButton.BorderThickness = new Thickness(1);
            _refreshGlyph.Foreground = new SolidColorBrush(text);
        }

        private void RebuildContent()
        {
            _content.Children.Clear();

            if (_items.Length == 0)
            {
                TextBlock empty = new TextBlock();
                empty.Text = _emptyText;
                empty.FontFamily = new FontFamily("Segoe UI");
                empty.FontSize = 13;
                empty.Foreground = new SolidColorBrush(ToMediaColor(UiTheme.TextSecondary));
                empty.VerticalAlignment = VerticalAlignment.Center;
                _content.Children.Add(empty);
                return;
            }

            for (int i = 0; i < _items.Length; i++)
            {
                _content.Children.Add(CreateDeviceView(_items[i]));

                if (i < _items.Length - 1)
                {
                    System.Windows.Shapes.Ellipse dot = new System.Windows.Shapes.Ellipse();
                    dot.Width = 2;
                    dot.Height = 2;
                    dot.Margin = new Thickness(14, 0, 14, 0);
                    dot.VerticalAlignment = VerticalAlignment.Center;
                    dot.Fill = new SolidColorBrush(ToMediaColor(UiTheme.TextSecondary));
                    dot.Opacity = 0.45;
                    _content.Children.Add(dot);
                }
            }
        }

        private UIElement CreateDeviceView(DeviceBattery item)
        {
            bool online = item.Online;
            Color primary = ToMediaColor(UiTheme.TextPrimary);
            Color secondary = ToMediaColor(UiTheme.TextSecondary);
            Color accent = ToMediaColor(online ? UiTheme.BatteryColor(item.Percent) : UiTheme.TextSecondary);

            StackPanel panel = new StackPanel();
            panel.Orientation = Orientation.Horizontal;
            panel.VerticalAlignment = VerticalAlignment.Center;

            TextBlock icon = new TextBlock();
            icon.Text = GetIconGlyph(item.Name);
            icon.FontFamily = new FontFamily("Segoe MDL2 Assets");
            icon.FontSize = 13;
            icon.Foreground = new SolidColorBrush(online ? secondary : ToMediaColor(UiTheme.TextDisabled));
            icon.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(icon);

            TextBlock name = new TextBlock();
            name.Text = UiTheme.GetDisplayName(item.Name);
            name.FontFamily = new FontFamily("Segoe UI");
            name.FontSize = 13;
            name.MaxWidth = 180;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.Foreground = new SolidColorBrush(online ? primary : secondary);
            name.Margin = new Thickness(6, 0, 0, 0);
            name.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(name);

            Border pill = new Border();
            pill.CornerRadius = new CornerRadius(9);
            pill.Padding = new Thickness(8, 1, 8, 1);
            pill.Margin = new Thickness(8, 0, 0, 0);
            pill.VerticalAlignment = VerticalAlignment.Center;
            pill.Background = new SolidColorBrush(Color.FromArgb(online ? (byte)32 : (byte)16, accent.R, accent.G, accent.B));
            pill.BorderBrush = new SolidColorBrush(Color.FromArgb(online ? (byte)84 : (byte)38, accent.R, accent.G, accent.B));
            pill.BorderThickness = new Thickness(1);

            string valueText = online ? item.Percent + "%" : "未连接";
            if (online && item.Charging)
            {
                valueText += " ⚡";
            }
            else if (online && item.Full)
            {
                valueText += " ✦";
            }

            TextBlock value = new TextBlock();
            value.Text = valueText;
            value.FontFamily = new FontFamily("Segoe UI");
            value.FontSize = 12;
            value.FontWeight = FontWeights.SemiBold;
            value.Foreground = new SolidColorBrush(accent);
            value.VerticalAlignment = VerticalAlignment.Center;
            pill.Child = value;
            panel.Children.Add(pill);

            return panel;
        }

        private static string GetIconGlyph(string name)
        {
            DeviceIconKind kind = UiTheme.GetIconKind(name);
            if (kind == DeviceIconKind.Keyboard)
            {
                return "\uE765";
            }

            if (kind == DeviceIconKind.Mouse)
            {
                return "\uE962";
            }

            if (kind == DeviceIconKind.Gamepad)
            {
                return "\uE7FC";
            }

            if (kind == DeviceIconKind.Headset)
            {
                return "\uE7F6";
            }

            return "\uE7C3";
        }

        private static Color ToMediaColor(System.Drawing.Color color)
        {
            return Color.FromArgb(color.A, color.R, color.G, color.B);
        }
    }
}
