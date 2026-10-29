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
        private readonly DispatcherTimer _hoverTimer;
        private readonly WpfDetailPanel _panel;

        private DeviceBattery[] _items = new DeviceBattery[0];
        private string _emptyText = "正在扫描蓝牙设备…";
        private bool _hovered;
        private bool _compact;
        private bool _enabled;

        /// <summary>无视设备数量，一直用紧凑样式。</summary>
        private bool _forceCompact;

        /// <summary>设备数超过这个值就自动切紧凑模式。</summary>
        private int _compactThreshold = Settings.DefaultCompactThreshold;

        /// <summary>紧凑模式下显示成什么样。</summary>
        private CompactStyle _compactStyle = CompactStyle.IconAndBattery;
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

            // 紧凑模式的详情面板：鼠标移上去才弹
            _panel = new WpfDetailPanel();

            // 鼠标从悬浮条挪到面板上时，悬浮条的 MouseLeave 会先到，
            // 只靠进出事件会在半路把面板收掉。所以弹出后改成定时看光标位置：
            // 只要还在悬浮条或面板上就保持，两边都不在才收。
            _hoverTimer = new DispatcherTimer();
            _hoverTimer.Interval = TimeSpan.FromMilliseconds(120);
            _hoverTimer.Tick += OnHoverTimerTick;

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

        protected override void OnClosed(EventArgs e)
        {
            // 面板是独立顶层窗口，不跟着悬浮条一起销毁，得手动关掉
            _hoverTimer.Stop();
            if (!_panel.IsDestroyed)
            {
                _panel.Close();
            }

            base.OnClosed(e);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // 作为 Shell_TrayWnd 的子窗口，任务栏销毁时会被级联销毁；
            // 收到销毁消息后标记不可用，避免后续 Show/定位抛异常。
            if (msg == NativeMethods.WM_DESTROY || msg == NativeMethods.WM_NCDESTROY)
            {
                // 面板是独立的顶层窗口，不会被任务栏级联销毁，得自己收掉，
                // 否则资源管理器重启后会留下一张无主的浮层
                HideDetailPanel();
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

        /// <summary>
        /// 压缩显示的三项设置：是否强制、超过几台开始压缩、压缩后显示成什么样。
        /// </summary>
        internal void SetCompactOptions(bool force, int threshold, CompactStyle style)
        {
            _forceCompact = force;
            _compactThreshold = Math.Max(
                Settings.MinCompactThreshold, Math.Min(Settings.MaxCompactThreshold, threshold));
            _compactStyle = style;

            if (_destroyed)
            {
                return;
            }

            RebuildContent();

            if (!_compact)
            {
                // 刚从紧凑切回完整，面板就没用了
                HideDetailPanel();
            }
            else if (_panel.IsVisible)
            {
                _panel.UpdateItems(_items);
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdatePosition));
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
                HideDetailPanel();
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

            // 面板开着的时候内容跟着刷新；设备掉到 3 台以内就不用面板了
            if (_panel.IsVisible)
            {
                if (_compact)
                {
                    _panel.UpdateItems(_items);
                }
                else
                {
                    HideDetailPanel();
                }
            }

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

            // 尺寸不自己算：设备是陆续读到的（蓝牙先来、HID 后到、手柄开机才有电量），
            // 内容宽度一直在变，而 SizeToContent 本来就会跟着内容重算窗口大小。
            // 之前这里用 SetWindowPos 显式给宽高，等于和它抢方向盘 —— 它按内容算一个尺寸，
            // 这里又按自己量出来的 DesiredSize 算另一个（DesiredSize 本身会被当前窗口宽度裁剪，
            // 宽的时候量出 439，窄的时候量出 469），窗口就在两个宽度之间来回跳。
            // 所以这里只强制跑完一次布局让窗口先长到内容需要的尺寸，再把结果读回来用于定位。
            UpdateLayout();

            NativeMethods.RECT windowRect;
            if (!NativeMethods.GetWindowRect(hwnd, out windowRect))
            {
                return;
            }

            int width = windowRect.Right - windowRect.Left;
            int height = windowRect.Bottom - windowRect.Top;
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

            // SWP_NOSIZE：宽高由 SizeToContent 决定，这里只挪位置
            NativeMethods.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                clientPoint.X,
                clientPoint.Y,
                0,
                0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

            // 面板贴着悬浮条放。定位是延后一轮做的（BeginInvoke），托盘区宽度又常变，
            // 面板可能比悬浮条早一步落位，所以这里以最终位置为准再对齐一次
            _panel.Reposition();
        }

        private void SetHovered(bool hovered)
        {
            // 紧凑模式下鼠标常要从悬浮条挪到详情面板上，这一路不算「离开」，
            // 否则面板刚弹出来就被自己收掉了。真正该收的时机交给 _hoverTimer 判断。
            if (!hovered && _hovered && IsCursorInsidePanel())
            {
                return;
            }

            if (_hovered == hovered)
            {
                return;
            }

            _hovered = hovered;
            _refreshButton.Visibility = hovered ? Visibility.Visible : Visibility.Collapsed;
            ApplyTheme();
            AnimateBackground(hovered);
            UpdateLayout();

            // 面板贴在悬浮条上方，得等布局跑完（悬停时多出来的刷新按钮会改变宽度）
            if (hovered)
            {
                ShowDetailPanel();
            }
            else
            {
                HideDetailPanel();
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdatePosition));
        }

        private void ShowDetailPanel()
        {
            if (!_compact || _destroyed || !_enabled || _items.Length == 0)
            {
                return;
            }

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            // 数据和主题都在 ShowAbove 里一次性重画，这里只管什么时候弹
            _panel.ShowAbove(hwnd, _items);
            _hoverTimer.Start();
        }

        private void HideDetailPanel()
        {
            _hoverTimer.Stop();
            _panel.HidePanel();
        }

        /// <summary>
        /// 光标已经离开「悬浮条 + 面板」这块区域时收掉面板。
        /// 用定时器轮询而不是进出事件：两者相邻，鼠标跨越时事件顺序不可靠，
        /// 会出现面板闪一下就没、或者移开了还不收的情况。
        /// </summary>
        private void OnHoverTimerTick(object sender, EventArgs e)
        {
            if (_destroyed || !_enabled)
            {
                HideDetailPanel();
                return;
            }

            NativeMethods.POINT cursor;
            if (!NativeMethods.GetCursorPos(out cursor))
            {
                return;
            }

            if (IsCursorInBar(cursor) || _panel.ContainsScreenPoint(cursor.X, cursor.Y))
            {
                return;
            }

            HideDetailPanel();

            // 光标已经不在悬浮条上了，悬停样式（背景、刷新按钮）也一起收掉
            SetHovered(false);
        }

        private bool IsCursorInBar(NativeMethods.POINT cursor)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            NativeMethods.RECT rect;
            if (!NativeMethods.GetWindowRect(hwnd, out rect))
            {
                return false;
            }

            return cursor.X >= rect.Left && cursor.X < rect.Right
                && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
        }

        private bool IsCursorInsidePanel()
        {
            NativeMethods.POINT cursor;
            if (!NativeMethods.GetCursorPos(out cursor))
            {
                return false;
            }

            return _panel.ContainsScreenPoint(cursor.X, cursor.Y);
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

            // 详情面板跟着同一套配色走，主题一变两边一起换
            _panel.SetStyle(_hoverColorArgb, _hoverAlpha);
        }

        private void RebuildContent()
        {
            _content.Children.Clear();

            // 设备多了就把设备名去掉：四台按「图标 + 名字 + 胶囊」排下来要 800 多像素，
            // 任务栏右边根本放不下，压成「图标 + 胶囊」能收到一半以内，
            // 名字挪到悬停面板里看。阈值和是否强制都由设置决定
            _compact = _forceCompact || _items.Length > _compactThreshold;

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
                bool last = i == _items.Length - 1;
                _content.Children.Add(_compact
                    ? CreateCompactDeviceView(_items[i])
                    : CreateDeviceView(_items[i]));

                if (!last)
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

        /// <summary>
        /// 紧凑模式：去掉设备名，图标保留，电量按设置显示成胶囊或者干脆不显示。
        /// 名字是最占宽度的部分（最长 180），砍掉它一台设备能从 270 收到 90 左右。
        /// </summary>
        private UIElement CreateCompactDeviceView(DeviceBattery item)
        {
            bool online = item.Online;
            Color accent = ToMediaColor(online ? UiTheme.BatteryColor(item.Percent) : UiTheme.TextSecondary);

            if (_compactStyle == CompactStyle.IconOnly)
            {
                // 没有胶囊可读，就把电量做成图标里的水位：底色 + 档位色从底部填上来，
                // 绿 / 琥珀 / 红再加高度，扫一眼就知道谁快没电
                return CompactIcon.Create(item, CompactIcon.GlyphSize);
            }

            StackPanel panel = new StackPanel();
            panel.Orientation = Orientation.Horizontal;
            panel.VerticalAlignment = VerticalAlignment.Center;

            TextBlock icon = new TextBlock();
            icon.Text = UiTheme.GetIconGlyph(item.Name);
            icon.FontFamily = new FontFamily("Segoe MDL2 Assets");
            icon.FontSize = 13;
            icon.Foreground = new SolidColorBrush(online ? ToMediaColor(UiTheme.TextSecondary) : ToMediaColor(UiTheme.TextDisabled));
            icon.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(icon);

            Border pill = CreateBatteryPill(item, accent, online);
            pill.Margin = new Thickness(6, 0, 0, 0);
            panel.Children.Add(pill);

            return panel;
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
            icon.Text = UiTheme.GetIconGlyph(item.Name);
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

            Border pill = CreateBatteryPill(item, accent, online);
            pill.Margin = new Thickness(8, 0, 0, 0);
            panel.Children.Add(pill);

            return panel;
        }

        /// <summary>电量胶囊，常规模式和紧凑模式共用同一套样式。</summary>
        private static Border CreateBatteryPill(DeviceBattery item, Color accent, bool online)
        {
            Border pill = new Border();
            pill.CornerRadius = new CornerRadius(9);
            pill.Padding = new Thickness(8, 1, 8, 1);
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
            return pill;
        }

        private static Color ToMediaColor(System.Drawing.Color color)
        {
            return Color.FromArgb(color.A, color.R, color.G, color.B);
        }
    }
}
