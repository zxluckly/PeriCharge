using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        private readonly DispatcherTimer _hookTimer;
        private readonly WpfDetailPanel _panel;

        private DeviceBattery[] _items = new DeviceBattery[0];
        private MonitorBrightness[] _monitors = new MonitorBrightness[0];
        private string _emptyText = "正在扫描蓝牙设备…";
        private bool _hovered;
        private bool _compact;
        private bool _enabled;

        /// <summary>滚轮调亮度的步长。</summary>
        private int _brightnessStep = Settings.DefaultBrightnessStep;

        /// <summary>亮度项的文字块，滚轮调整时直接改文字，不重建整条。</summary>
        private readonly Dictionary<string, TextBlock> _brightnessLabels = new Dictionary<string, TextBlock>();

        /// <summary>亮度项的胶囊，滚轮跨过红/黄/绿分界时要同步换色。</summary>
        private readonly Dictionary<string, Border> _brightnessPills = new Dictionary<string, Border>();

        /// <summary>仅图标模式下亮度项的容器，滚轮调整时换掉里面的水位图标。</summary>
        private readonly Dictionary<string, Border> _brightnessSlots = new Dictionary<string, Border>();

        /// <summary>每台显示器一个写入防抖定时器：滚轮连滚时不至于每一格都发一次 DDC。</summary>
        private readonly Dictionary<string, DispatcherTimer> _brightnessTimers = new Dictionary<string, DispatcherTimer>();

        /// <summary>待写入的目标值（Id → 百分比）。定时器只认这个，不持有显示器对象。</summary>
        private readonly Dictionary<string, int> _pendingBrightness = new Dictionary<string, int>();

        /// <summary>上一次画出来的内容指纹，用来跳过无意义的重建。</summary>
        private string _contentSignature;

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

        /// <summary>低级鼠标钩子，用来在 NOACTIVATE 窗口上收滚轮。</summary>
        private IntPtr _mouseHook;
        private NativeMethods.LowLevelMouseProc _mouseHookProc;

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
            _panel.BrightnessSliderChanged += delegate(object sender, BrightnessSliderEventArgs e)
            {
                SetBrightnessFromSlider(e.Id, e.Percent);
            };

            // 鼠标从悬浮条挪到面板上时，悬浮条的 MouseLeave 会先到，
            // 只靠进出事件会在半路把面板收掉。所以弹出后改成定时看光标位置：
            // 只要还在悬浮条或面板上就保持，两边都不在才收。
            _hoverTimer = new DispatcherTimer();
            _hoverTimer.Interval = TimeSpan.FromMilliseconds(120);
            _hoverTimer.Tick += OnHoverTimerTick;

            // 低级钩子被系统摘掉后查不出来，靠这个定时器定期重装自愈。
            // 5 秒一次：重装只是两个 API 调用，开销可以忽略；
            // 间隔再长的话，用户滚轮失效后要等很久才恢复
            _hookTimer = new DispatcherTimer();
            _hookTimer.Interval = TimeSpan.FromSeconds(5);
            _hookTimer.Tick += delegate { RefreshMouseHook(); };

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

            InstallMouseHook();
            _hookTimer.Start();
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

            foreach (DispatcherTimer timer in _brightnessTimers.Values)
            {
                timer.Stop();
            }
            _brightnessTimers.Clear();

            UninstallMouseHook();
            _hookTimer.Stop();

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

            // 设置变了，指纹作废，强制重画一次
            _contentSignature = null;
            RefreshContentIfChanged();

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

            RefreshContentIfChanged();

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
                _contentSignature = BuildContentSignature();
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
            }

            ApplyOverlayWindowStyles(hwnd);
            UpdatePosition();
        }

        /// <summary>
        /// 悬浮条是任务栏的子窗口，但 WPF 在 SetParent 之后会重算窗口样式，
        /// 把 WS_EX_TOOLWINDOW / WS_EX_NOACTIVATE 一起抹掉——实测 exStyle 只剩 LAYERED。
        /// 少了 TOOLWINDOW，Win11 的任务视图会把它当成一个普通应用窗口，
        /// 显示成一张只有电池图标的全黑卡片。每次定位前都校正一遍。
        /// </summary>
        private static void ApplyOverlayWindowStyles(IntPtr hwnd)
        {
            int style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
            int childStyle = (style & ~unchecked((int)0x80000000)) | 0x40000000;
            if (childStyle != style)
            {
                NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_STYLE, childStyle);
            }

            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            int wantEx = exStyle | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            if (wantEx != exStyle)
            {
                NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, wantEx);
            }
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
            }

            ApplyOverlayWindowStyles(hwnd);

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
            // 拖动滑块时鼠标可能被捕获到面板外，同样不能当成离开
            if (!hovered && _hovered && (_panel.IsDragging || IsCursorInsidePanel()))
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

            if (IsCursorInBar(cursor) || _panel.IsDragging
                || _panel.ContainsScreenPoint(cursor.X, cursor.Y))
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
            // 跟随系统只决定颜色，不透明度仍由用户设置决定；取色规则和面板共用一份
            System.Drawing.Color topSource;
            System.Drawing.Color bottomSource;
            UiWidgets.ResolveSurface(_hoverColorArgb, out topSource, out bottomSource);
            Color top = UiWidgets.ToMediaColor(topSource);
            Color bottom = UiWidgets.ToMediaColor(bottomSource);

            int alpha = _hoverAlpha;

            if (_backgroundBrush.GradientStops.Count >= 2)
            {
                _backgroundBrush.GradientStops[0].Color = Color.FromArgb((byte)alpha, top.R, top.G, top.B);
                _backgroundBrush.GradientStops[1].Color = Color.FromArgb((byte)alpha, bottom.R, bottom.G, bottom.B);
            }

            _borderBrush.Color = UiWidgets.ToMediaColor(UiTheme.Border);

            Color text = UiWidgets.ToMediaColor(UiTheme.TextPrimary);
            _buttonHoverBrush.Color = Color.FromArgb(60, text.R, text.G, text.B);
            _refreshButton.Background = new SolidColorBrush(Color.FromArgb(30, text.R, text.G, text.B));
            _refreshButton.BorderBrush = new SolidColorBrush(Color.FromArgb(60, text.R, text.G, text.B));
            _refreshButton.BorderThickness = new Thickness(1);
            _refreshGlyph.Foreground = new SolidColorBrush(text);

            // 详情面板跟着同一套配色走，主题一变两边一起换
            _panel.SetStyle(_hoverColorArgb, _hoverAlpha);
        }

        /// <summary>
        /// 内容没变就不重建视觉树。每 30 秒刷新一次，重建一次要 new 十几个
        /// TextBlock / Border / Geometry，白造垃圾；数据完全一致时直接跳过。
        /// </summary>
        private void RefreshContentIfChanged()
        {
            _compact = _forceCompact || _items.Length > _compactThreshold;

            string next = BuildContentSignature();
            if (next == _contentSignature)
            {
                return;
            }

            _contentSignature = next;
            RebuildContent();
        }

        private string BuildContentSignature()
        {
            StringBuilder signature = new StringBuilder(96);
            signature.Append(_compact ? 'C' : 'N').Append((int)_compactStyle).Append('|');
            // 空列表时的提示文字也算内容：「正在扫描…」和「未发现设备」不能混为一谈
            signature.Append(_emptyText).Append('|');

            for (int i = 0; i < _items.Length; i++)
            {
                DeviceBattery item = _items[i];
                signature.Append(item.Id).Append(':').Append(item.Percent)
                    .Append(item.Online ? 'o' : '-')
                    .Append(item.Charging ? 'c' : '-')
                    .Append(item.Full ? 'f' : '-').Append(';');
            }

            signature.Append('|');
            for (int i = 0; i < _monitors.Length; i++)
            {
                signature.Append(_monitors[i].Id).Append(':').Append(_monitors[i].Percent).Append(';');
            }

            return signature.ToString();
        }

        private void RebuildContent()
        {
            _content.Children.Clear();
            _brightnessLabels.Clear();
            _brightnessPills.Clear();
            _brightnessSlots.Clear();

            // 设备多了就把设备名去掉：四台按「图标 + 名字 + 胶囊」排下来要 800 多像素，
            // 任务栏右边根本放不下，压成「图标 + 胶囊」能收到一半以内，
            // 名字挪到悬停面板里看。阈值和是否强制都由设置决定
            _compact = _forceCompact || _items.Length > _compactThreshold;
            if (_items.Length == 0 && _monitors.Length == 0)
            {
                TextBlock empty = new TextBlock();
                empty.Text = _emptyText;
                empty.FontFamily = new FontFamily("Segoe UI");
                empty.FontSize = 13;
                empty.Foreground = new SolidColorBrush(UiWidgets.ToMediaColor(UiTheme.TextSecondary));
                empty.VerticalAlignment = VerticalAlignment.Center;
                _content.Children.Add(empty);
                return;
            }

            List<UIElement> entries = new List<UIElement>();

            for (int i = 0; i < _items.Length; i++)
            {
                entries.Add(_compact
                    ? CreateCompactDeviceView(_items[i])
                    : CreateDeviceView(_items[i]));
            }

            // 亮度段接在设备段后面，顺序固定为主屏在前
            for (int i = 0; i < _monitors.Length; i++)
            {
                entries.Add(CreateBrightnessView(_monitors[i]));
            }

            for (int i = 0; i < entries.Count; i++)
            {
                _content.Children.Add(entries[i]);

                if (i < entries.Count - 1)
                {
                    _content.Children.Add(CreateSeparatorDot());
                }
            }
        }

        private UIElement CreateSeparatorDot()
        {
            System.Windows.Shapes.Ellipse dot = new System.Windows.Shapes.Ellipse();
            dot.Width = 2;
            dot.Height = 2;
            dot.Margin = new Thickness(14, 0, 14, 0);
            dot.VerticalAlignment = VerticalAlignment.Center;
            dot.Fill = new SolidColorBrush(UiWidgets.ToMediaColor(UiTheme.TextSecondary));
            dot.Opacity = 0.45;
            return dot;
        }

        /// <summary>
        /// 显示器亮度：常规模式是「图标 + 名字 + 胶囊」，紧凑模式跟电量一样可以切成仅图标水位。
        /// 整项都能接收滚轮——鼠标停在上面滚一格就是 ±一个步长。
        /// </summary>
        private UIElement CreateBrightnessView(MonitorBrightness monitor)
        {
            Border hit = new Border();
            hit.Background = Brushes.Transparent;
            hit.Tag = monitor;

            // 仅图标模式：和电量一样用图标里的水位表示高低，颜色也复用同一套阈值
            if (_compact && _compactStyle == CompactStyle.IconOnly)
            {
                hit.Child = CompactIcon.Create(UiWidgets.MonitorGlyph, monitor.Percent, CompactIcon.GlyphSize);
                _brightnessSlots[monitor.Id] = hit;
                return hit;
            }

            // 和电量用同一套阈值配色：低 → 红、中 → 黄、高 → 绿，
            // 一眼就能看出哪块屏偏暗，视觉语言和电量段完全一致
            Color accent = UiWidgets.PercentAccent(monitor.Percent, UiTheme.TextSecondary);

            StackPanel panel = new StackPanel();
            panel.Orientation = Orientation.Horizontal;
            panel.VerticalAlignment = VerticalAlignment.Center;

            panel.Children.Add(UiWidgets.CreateGlyph(
                UiWidgets.MonitorGlyph, UiWidgets.GlyphSize, UiTheme.TextSecondary));

            if (!_compact)
            {
                TextBlock name = UiWidgets.CreateName(
                    monitor.Name, UiWidgets.NameMaxWidth, UiTheme.TextPrimary);
                name.Margin = new Thickness(UiWidgets.IconGap, 0, 0, 0);
                panel.Children.Add(name);
            }

            Border pill = UiWidgets.CreatePill(
                UiWidgets.FormatPercent(monitor.Percent), accent, false);
            pill.Margin = new Thickness(_compact ? UiWidgets.IconGap : UiWidgets.NamePillGap, 0, 0, 0);
            panel.Children.Add(pill);

            _brightnessLabels[monitor.Id] = (TextBlock)pill.Child;
            _brightnessPills[monitor.Id] = pill;
            hit.Child = panel;
            return hit;
        }

        // ---- 滚轮：悬浮条是 WS_EX_NOACTIVATE 的窗口，常规 WM_MOUSEWHEEL 收不到
        //      （系统默认只发给焦点窗口），所以挂一个低级鼠标钩子自己判断光标位置 ----

        private void InstallMouseHook()
        {
            if (_mouseHook != IntPtr.Zero)
            {
                return;
            }

            // 委托必须由字段持有：一旦被 GC 回收，钩子回调就会指向已释放的内存
            _mouseHookProc = OnLowLevelMouse;
            _mouseHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL,
                _mouseHookProc,
                NativeMethods.GetModuleHandle(null),
                0);

            if (_mouseHook == IntPtr.Zero)
            {
                Log.Write("安装滚轮钩子失败：" + Marshal.GetLastWin32Error());
            }
        }

        private void UninstallMouseHook()
        {
            if (_mouseHook == IntPtr.Zero)
            {
                return;
            }

            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            _mouseHookProc = null;
        }

        /// <summary>
        /// 重装钩子。系统摘掉低级钩子后不会通知、也查不到状态，
        /// 只能靠定期重装自愈（跟着 30 秒一轮的亮度扫描走）。
        /// </summary>
        private void RefreshMouseHook()
        {
            UninstallMouseHook();
            InstallMouseHook();
        }

        private IntPtr OnLowLevelMouse(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0
                    && wParam == new IntPtr(NativeMethods.WM_MOUSEWHEEL)
                    && !_destroyed)
                {
                    MonitorBrightness monitor = HitTestBrightness();
                    if (monitor != null)
                    {
                        NativeMethods.MSLLHOOKSTRUCT data =
                            (NativeMethods.MSLLHOOKSTRUCT)Marshal.PtrToStructure(
                                lParam, typeof(NativeMethods.MSLLHOOKSTRUCT));

                        // mouseData 的高 16 位是带符号的滚轮增量
                        int delta = (short)((data.mouseData >> 16) & 0xFFFF);
                        int direction = delta > 0 ? 1 : -1;

                        // 回调里绝不能做重活：低级钩子整条链路有 300ms 超时
                        // （LowLevelHooksTimeout），超时后系统会**静默摘掉钩子**，
                        // 之后滚轮彻底没反应，只能重启程序。
                        // Twinkle Tray 这类同样装了钩子的程序一旦卡住链路，
                        // 我们就会被连坐。所以这里只判断命中，调整动作丢进 UI 队列。
                        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate
                        {
                            AdjustBrightness(monitor, direction);
                        }));

                        return new IntPtr(1);   // 吞掉，别让下面的窗口跟着滚
                    }
                }
            }
            catch
            {
                // 钩子回调里抛异常会被系统摘掉钩子，这里一律吞掉
            }

            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        /// <summary>
        /// 从鼠标下的元素往上找，看是不是某个亮度项。
        /// DirectlyOver 在分层窗口未参与命中链、或鼠标刚移入时会返回 null，
        /// 再按屏幕坐标做一次 InputHitTest 兜底，否则偶尔滚不动。
        /// </summary>
        private MonitorBrightness HitTestBrightness()
        {
            MonitorBrightness owner = FindBrightnessOwner(Mouse.DirectlyOver as DependencyObject);
            if (owner != null)
            {
                return owner;
            }

            NativeMethods.POINT cursor;
            if (!NativeMethods.GetCursorPos(out cursor))
            {
                return null;
            }

            try
            {
                System.Windows.Point local = PointFromScreen(
                    new System.Windows.Point(cursor.X, cursor.Y));
                return FindBrightnessOwner(InputHitTest(local) as DependencyObject);
            }
            catch
            {
                // 窗口尚未完成布局时 PointFromScreen 可能抛异常，按未命中处理
                return null;
            }
        }

        /// <summary>从命中元素沿视觉树往上找带亮度 Tag 的项。</summary>
        private static MonitorBrightness FindBrightnessOwner(DependencyObject hit)
        {
            while (hit != null)
            {
                FrameworkElement element = hit as FrameworkElement;
                if (element != null && element.Tag is MonitorBrightness)
                {
                    return (MonitorBrightness)element.Tag;
                }

                hit = VisualTreeHelper.GetParent(hit);
            }

            return null;
        }

        /// <summary>
        /// 滚轮调一格：先改本地值让 UI 立刻跟手，再防抖写硬件。
        /// 直接每格都发 DDC 的话，连续滚动会积压一堆几十毫秒的往返。
        ///
        /// 传进来的 <paramref name="monitor"/> 是亮度项 Tag 上挂的对象，可能是
        /// 上一轮扫描留下的旧实例（内容指纹没变时视觉树不会重建）。所以这里必须
        /// 按 Id 回到 <see cref="_monitors"/> 里重新取当前对象，否则面板拿到的是
        /// 新数组里的旧百分比——表现就是图标和屏幕都动了、悬停面板数值不动。
        /// </summary>
        private void AdjustBrightness(MonitorBrightness monitor, int direction)
        {
            if (monitor == null)
            {
                return;
            }

            MonitorBrightness current = FindMonitor(monitor.Id);
            if (current == null || !current.Controllable)
            {
                return;
            }

            int step = _brightnessStep < Settings.MinBrightnessStep
                ? Settings.DefaultBrightnessStep
                : _brightnessStep;

            int from = current.Percent < 0 ? 50 : current.Percent;
            int next = Math.Max(0, Math.Min(100, from + direction * step));
            if (next == current.Percent)
            {
                return;
            }

            current.Percent = next;
            ApplyBrightnessVisual(current.Id, next);
            _panel.SetBrightnessPercent(current.Id, next);

            // 指纹按更新后的 _monitors 算，下一轮扫描不会因为「内容变了」白重建一次
            _contentSignature = BuildContentSignature();
            ScheduleBrightnessWrite(current);
        }

        /// <summary>
        /// 面板滑块拖到某个值：和滚轮共用同一条链路——改当前对象、刷新悬浮条、
        /// 180ms 防抖后写硬件。滑块自己已经跟手，这里只负责数据和硬件。
        /// </summary>
        private void SetBrightnessFromSlider(string id, int percent)
        {
            MonitorBrightness current = FindMonitor(id);
            if (current == null || !current.Controllable)
            {
                return;
            }

            int target = Math.Max(0, Math.Min(100, percent));
            if (target == current.Percent)
            {
                return;
            }

            current.Percent = target;
            ApplyBrightnessVisual(id, target);
            _panel.SetBrightnessPercent(id, target);
            _contentSignature = BuildContentSignature();
            ScheduleBrightnessWrite(current);
        }

        /// <summary>按 Id 找当前扫描结果里的那台显示器（不持有、不捕获对象本身）。</summary>
        private MonitorBrightness FindMonitor(string id)
        {
            for (int i = 0; i < _monitors.Length; i++)
            {
                if (_monitors[i].Id == id)
                {
                    return _monitors[i];
                }
            }

            return null;
        }

        /// <summary>
        /// 就地刷新某一项亮度：仅图标模式换水位图标，带胶囊的模式同时改文字和档位色。
        /// 悬浮条和面板各自维护自己的引用，滚轮时都走同一套刷新逻辑。
        /// </summary>
        private void ApplyBrightnessVisual(string id, int percent)
        {
            Border slot;
            if (_compact && _compactStyle == CompactStyle.IconOnly
                && _brightnessSlots.TryGetValue(id, out slot))
            {
                slot.Child = CompactIcon.Create(UiWidgets.MonitorGlyph, percent, CompactIcon.GlyphSize);
                return;
            }

            TextBlock value;
            if (!_brightnessLabels.TryGetValue(id, out value))
            {
                return;
            }

            value.Text = UiWidgets.FormatPercent(percent);

            Border pill;
            if (_brightnessPills.TryGetValue(id, out pill))
            {
                UiWidgets.ApplyPillAccent(
                    pill, value, UiWidgets.PercentAccent(percent, UiTheme.TextSecondary), false);
            }
        }

        private void ScheduleBrightnessWrite(MonitorBrightness monitor)
        {
            // 目标值按 Id 记下来：定时器只建一次，闭包里绝不能捕获 monitor 对象本身。
            // 每 30 秒重新扫描会把悬浮条换成新的 MonitorBrightness 实例，
            // 捕获旧对象的定时器会一直写那个「冻结」的百分比——
            // 表现就是界面在变、屏幕纹丝不动
            _pendingBrightness[monitor.Id] = monitor.Percent;

            DispatcherTimer timer;
            if (!_brightnessTimers.TryGetValue(monitor.Id, out timer))
            {
                string id = monitor.Id;
                timer = new DispatcherTimer();
                timer.Interval = TimeSpan.FromMilliseconds(180);
                timer.Tick += delegate
                {
                    timer.Stop();

                    // 面板的数值和配色在 AdjustBrightness 里已经就地更新过了，
                    // 这里不再整块重建——重建比改一个文字块贵，还会闪一下
                    WriteBrightness(id);
                };
                _brightnessTimers[monitor.Id] = timer;
            }

            timer.Stop();
            timer.Start();
        }

        /// <summary>按 Id 找当前那台显示器，用待写入的目标值落盘。</summary>
        private void WriteBrightness(string id)
        {
            if (_destroyed)
            {
                return;
            }

            int target;
            if (!_pendingBrightness.TryGetValue(id, out target))
            {
                return;
            }

            MonitorBrightness current = FindMonitor(id);
            if (current == null)
            {
                return;
            }

            MonitorBrightness snapshot = current.Clone();
            snapshot.Percent = target;
            Task.Run(delegate
            {
                bool ok = BrightnessService.Set(snapshot, target);
                if (ok)
                {
                    return;
                }

                Log.Write("设置亮度失败：" + snapshot.Name + " → " + target + "%");

                // 写不进去就把界面改回硬件真实值：数字变了、屏幕没变比直接失败更坑
                List<MonitorBrightness> actual = BrightnessService.Scan();
                for (int i = 0; i < actual.Count; i++)
                {
                    if (actual[i].Id != snapshot.Id || !actual[i].Controllable)
                    {
                        continue;
                    }

                    int value = actual[i].Percent;
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                    {
                        ApplyBrightnessValue(snapshot.Id, value);
                    }));
                    break;
                }
            });
        }

        /// <summary>把某一项亮度改回指定值（写入失败后回滚界面用）。</summary>
        private void ApplyBrightnessValue(string id, int percent)
        {
            if (_destroyed)
            {
                return;
            }

            MonitorBrightness current = FindMonitor(id);
            if (current != null)
            {
                current.Percent = percent;
            }

            ApplyBrightnessVisual(id, percent);
            _panel.SetBrightnessPercent(id, percent);
            _contentSignature = BuildContentSignature();
        }

        /// <summary>显示器列表变了（插拔、重新扫描）时整条重画。</summary>
        internal void UpdateBrightness(MonitorBrightness[] monitors)
        {
            _monitors = monitors == null ? new MonitorBrightness[0] : monitors;
            if (_destroyed)
            {
                return;
            }

            RefreshContentIfChanged();
            _panel.UpdateBrightness(_monitors);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdatePosition));
        }

        /// <summary>滚轮步长变化时调用，不需要重画。</summary>
        internal void SetBrightnessStep(int step)
        {
            _brightnessStep = Math.Max(Settings.MinBrightnessStep, Math.Min(Settings.MaxBrightnessStep, step));
        }

        /// <summary>
        /// 紧凑模式：去掉设备名，图标保留，电量按设置显示成胶囊或者干脆不显示。
        /// 名字是最占宽度的部分（最长 180），砍掉它一台设备能从 270 收到 90 左右。
        /// </summary>
        private UIElement CreateCompactDeviceView(DeviceBattery item)
        {
            bool online = item.Online;
            System.Drawing.Color accentSource = online ? UiTheme.BatteryColor(item.Percent) : UiTheme.TextSecondary;

            if (_compactStyle == CompactStyle.IconOnly)
            {
                // 没有胶囊可读，就把电量做成图标里的水位：底色 + 档位色从底部填上来，
                // 绿 / 琥珀 / 红再加高度，扫一眼就知道谁快没电
                return CompactIcon.Create(item, CompactIcon.GlyphSize);
            }

            StackPanel panel = new StackPanel();
            panel.Orientation = Orientation.Horizontal;
            panel.VerticalAlignment = VerticalAlignment.Center;

            panel.Children.Add(UiWidgets.CreateGlyph(
                UiTheme.GetIconGlyph(item.Name), UiWidgets.GlyphSize,
                online ? UiTheme.TextSecondary : UiTheme.TextDisabled));

            Border pill = UiWidgets.CreatePill(
                UiWidgets.FormatBatteryValue(item), UiWidgets.ToMediaColor(accentSource), !online);
            pill.Margin = new Thickness(UiWidgets.IconGap, 0, 0, 0);
            panel.Children.Add(pill);

            return panel;
        }

        private UIElement CreateDeviceView(DeviceBattery item)
        {
            bool online = item.Online;
            System.Drawing.Color accentSource = online ? UiTheme.BatteryColor(item.Percent) : UiTheme.TextSecondary;

            StackPanel panel = new StackPanel();
            panel.Orientation = Orientation.Horizontal;
            panel.VerticalAlignment = VerticalAlignment.Center;

            panel.Children.Add(UiWidgets.CreateGlyph(
                UiTheme.GetIconGlyph(item.Name), UiWidgets.GlyphSize,
                online ? UiTheme.TextSecondary : UiTheme.TextDisabled));

            TextBlock name = UiWidgets.CreateName(
                UiTheme.GetDisplayName(item), UiWidgets.NameMaxWidth,
                online ? UiTheme.TextPrimary : UiTheme.TextSecondary);
            name.Margin = new Thickness(UiWidgets.IconGap, 0, 0, 0);
            panel.Children.Add(name);

            Border pill = UiWidgets.CreatePill(
                UiWidgets.FormatBatteryValue(item), UiWidgets.ToMediaColor(accentSource), !online);
            pill.Margin = new Thickness(UiWidgets.NamePillGap, 0, 0, 0);
            panel.Children.Add(pill);

            return panel;
        }
    }
}
