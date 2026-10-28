using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PeripheralBattery
{
    /// <summary>
    /// 悬浮条的详情面板。设备多到悬浮条只放得下图标时，鼠标移上去弹出这张卡片，
    /// 把每台设备的名称、电量和充电状态一次列全。
    ///
    /// 它是独立的顶层窗口，不是任务栏的子窗口 —— 面板要浮在悬浮条上方，
    /// 挂到任务栏下面会被任务栏的客户区裁掉。用 WS_EX_NOACTIVATE 保证弹出时不抢焦点，
    /// 否则鼠标扫过悬浮条就会把当前窗口的焦点弄丢。
    /// </summary>
    internal sealed class WpfDetailPanel : Window
    {
        /// <summary>卡片本体的宽度（DIP），不含四周留给阴影的透明边。</summary>
        private const double PanelWidth = 252;

        /// <summary>卡片与悬浮条之间的间距（DIP）。</summary>
        private const double AnchorGap = 8;

        /// <summary>四周留出的透明边，给投影效果用（阴影画在窗口外会被裁掉）。</summary>
        private const double ShadowMargin = 8;

        private readonly Border _root;
        private readonly StackPanel _rows;
        private readonly LinearGradientBrush _backgroundBrush;
        private readonly SolidColorBrush _borderBrush;
        private readonly DropShadowEffect _shadow;

        private DeviceBattery[] _items = new DeviceBattery[0];
        private int _hoverColorArgb;
        private int _hoverAlpha = Settings.DefaultHoverAlpha;
        private IntPtr _anchor;
        private bool _destroyed;

        internal WpfDetailPanel()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            SizeToContent = SizeToContent.Height;
            Width = PanelWidth + ShadowMargin * 2;
            Visibility = Visibility.Hidden;

            _shadow = new DropShadowEffect();
            _shadow.BlurRadius = 14;
            _shadow.ShadowDepth = 2;
            _shadow.Direction = 270;
            _shadow.Opacity = 0.20;
            _shadow.Color = Colors.Black;

            _root = new Border();
            _root.Margin = new Thickness(ShadowMargin);
            _root.CornerRadius = new CornerRadius(10);
            _root.BorderThickness = new Thickness(1);
            _root.Padding = new Thickness(12, 10, 12, 10);
            _root.Effect = _shadow;

            _backgroundBrush = new LinearGradientBrush();
            _backgroundBrush.StartPoint = new Point(0, 0);
            _backgroundBrush.EndPoint = new Point(0, 1);
            _backgroundBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            _backgroundBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            _root.Background = _backgroundBrush;

            _borderBrush = new SolidColorBrush(Colors.Transparent);
            _root.BorderBrush = _borderBrush;

            _rows = new StackPanel();
            _rows.Orientation = Orientation.Vertical;
            _root.Child = _rows;
            Content = _root;
        }

        /// <summary>窗口被系统销毁（会话结束、显示器变化等）后不能再 Show。</summary>
        internal bool IsDestroyed
        {
            get { return _destroyed; }
        }

        internal void SetStyle(int argb, int alpha)
        {
            _hoverColorArgb = argb;
            // 面板面积比悬浮条大得多，照搬悬浮条那档透明度铺在桌面上会看不清字，
            // 所以给一个下限；用户把不透明度调到 100% 时仍按 100% 走
            _hoverAlpha = Math.Max(1, Math.Min(255, Math.Max(alpha, 235)));

            // 没显示时不必重画，下次 ShowAbove 会按最新主题画一遍
            if (IsVisible)
            {
                ApplyTheme();
            }
        }

        internal void UpdateItems(DeviceBattery[] items)
        {
            _items = items == null ? new DeviceBattery[0] : items;
            if (!IsVisible)
            {
                // 没显示时只记下数据，等 ShowAbove 一起画，省一次重建
                return;
            }

            ApplyTheme();
            PositionAboveAnchor();
        }

        internal void ShowAbove(IntPtr anchor, DeviceBattery[] items)
        {
            if (_destroyed || anchor == IntPtr.Zero)
            {
                return;
            }

            _anchor = anchor;
            _items = items == null ? new DeviceBattery[0] : items;

            // 每次弹出都按当前数据、当前系统主题重画一遍：
            // 用户切了亮/暗主题不用重启程序
            ApplyTheme();

            if (!IsVisible)
            {
                Show();
            }

            PositionAboveAnchor();
        }

        /// <summary>悬浮条的位置或尺寸变了，把面板贴回去。</summary>
        internal void Reposition()
        {
            if (!_destroyed && IsVisible)
            {
                PositionAboveAnchor();
            }
        }

        internal void HidePanel()
        {
            if (!_destroyed && IsVisible)
            {
                Hide();
            }
        }

        /// <summary>屏幕坐标（物理像素）是否落在面板上，用于判断鼠标是不是从悬浮条挪到了面板。</summary>
        internal bool ContainsScreenPoint(int x, int y)
        {
            if (_destroyed || !IsVisible)
            {
                return false;
            }

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

            return x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                // 不抢焦点、不进 Alt+Tab：它只是悬浮条的一个附属浮层
                int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
                NativeMethods.SetWindowLong(
                    hwnd,
                    NativeMethods.GWL_EXSTYLE,
                    exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _destroyed = true;
            base.OnClosed(e);
        }

        private void PositionAboveAnchor()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || _anchor == IntPtr.Zero)
            {
                return;
            }

            // 高度靠 SizeToContent 算，布局没跑完读到的是上一次的高度
            UpdateLayout();

            NativeMethods.RECT barRect;
            NativeMethods.RECT selfRect;
            if (!NativeMethods.GetWindowRect(_anchor, out barRect)
                || !NativeMethods.GetWindowRect(hwnd, out selfRect))
            {
                return;
            }

            int width = selfRect.Right - selfRect.Left;
            int height = selfRect.Bottom - selfRect.Top;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            // 窗口尺寸是物理像素，用它反推缩放比，阴影边才能按同样比例留出来
            double scale = width / (PanelWidth + ShadowMargin * 2);
            if (scale <= 0)
            {
                scale = 1;
            }

            int margin = (int)Math.Round(ShadowMargin * scale);
            int gap = (int)Math.Round(AnchorGap * scale);

            // 卡片右侧与悬浮条右侧对齐、底边压在悬浮条上方：加减 margin 是把它从窗口边挪回卡片边
            int x = barRect.Right - width + margin;
            int y = barRect.Top - height - gap + margin;

            if (x < 0)
            {
                x = 0;
            }

            if (y < 0)
            {
                y = 0;
            }

            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_TOPMOST,
                x,
                y,
                0,
                0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
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
            _shadow.Opacity = Theme.IsLight() ? 0.16 : 0.28;

            // 文字和胶囊的颜色是建行时取的，主题一变就得重画
            Rebuild();
        }

        private void Rebuild()
        {
            _rows.Children.Clear();

            if (_items.Length == 0)
            {
                TextBlock empty = new TextBlock();
                empty.Text = "未发现可读取电量的设备";
                empty.FontFamily = new FontFamily("Segoe UI");
                empty.FontSize = 12;
                empty.Foreground = new SolidColorBrush(ToMediaColor(UiTheme.TextSecondary));
                _rows.Children.Add(empty);
                return;
            }

            // 面板的意义就是解释「为什么悬浮条只剩图标」，所以顶部点明台数
            TextBlock caption = new TextBlock();
            caption.Text = "已连接 " + _items.Length + " 台设备";
            caption.FontFamily = new FontFamily("Segoe UI");
            caption.FontSize = 11;
            caption.Foreground = new SolidColorBrush(ToMediaColor(UiTheme.TextSecondary));
            caption.Margin = new Thickness(0, 0, 0, 8);
            _rows.Children.Add(caption);

            for (int i = 0; i < _items.Length; i++)
            {
                _rows.Children.Add(CreateRow(_items[i], i > 0));
            }
        }

        private UIElement CreateRow(DeviceBattery item, bool spaced)
        {
            bool online = item.Online;
            Color primary = ToMediaColor(UiTheme.TextPrimary);
            Color secondary = ToMediaColor(UiTheme.TextSecondary);
            Color accent = ToMediaColor(online ? UiTheme.BatteryColor(item.Percent) : UiTheme.TextSecondary);

            Grid row = new Grid();
            row.Margin = new Thickness(0, spaced ? 7 : 0, 0, 0);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock icon = new TextBlock();
            icon.Text = UiTheme.GetIconGlyph(item.Name);
            icon.FontFamily = new FontFamily("Segoe MDL2 Assets");
            icon.FontSize = 13;
            icon.Foreground = new SolidColorBrush(online ? secondary : ToMediaColor(UiTheme.TextDisabled));
            icon.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);

            TextBlock name = new TextBlock();
            name.Text = UiTheme.GetDisplayName(item.Name);
            name.FontFamily = new FontFamily("Segoe UI");
            name.FontSize = 13;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.Foreground = new SolidColorBrush(online ? primary : secondary);
            name.Margin = new Thickness(8, 0, 10, 0);
            name.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

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
            Grid.SetColumn(pill, 2);
            row.Children.Add(pill);

            return row;
        }

        private static Color ToMediaColor(System.Drawing.Color color)
        {
            return Color.FromArgb(color.A, color.R, color.G, color.B);
        }
    }
}
