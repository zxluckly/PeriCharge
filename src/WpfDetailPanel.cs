using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PeripheralBattery
{
    /// <summary>面板亮度滑块改值：只带 Id 和数值，不捕获扫描对象。</summary>
    internal sealed class BrightnessSliderEventArgs : EventArgs
    {
        internal readonly string Id;
        internal readonly int Percent;

        internal BrightnessSliderEventArgs(string id, int percent)
        {
            Id = id;
            Percent = percent;
        }
    }

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

        /// <summary>亮度行的胶囊和文字引用，滚轮调整时就地改值，不整块重建面板。</summary>
        private readonly Dictionary<string, BrightnessRowRefs> _brightnessRows =
            new Dictionary<string, BrightnessRowRefs>();

        private sealed class BrightnessRowRefs
        {
            internal readonly Border Pill;
            internal readonly TextBlock Value;
            internal readonly NeumorphicSlider Slider;

            internal BrightnessRowRefs(Border pill, TextBlock value, NeumorphicSlider slider)
            {
                Pill = pill;
                Value = value;
                Slider = slider;
            }
        }

        private DeviceBattery[] _items = new DeviceBattery[0];
        private MonitorBrightness[] _monitors = new MonitorBrightness[0];
        private string _brightnessSignature = string.Empty;
        private int _hoverColorArgb;
        private int _hoverAlpha = Settings.DefaultHoverAlpha;
        private IntPtr _anchor;
        private bool _destroyed;

        /// <summary>用户拖动亮度滑块时触发，由悬浮条负责防抖写硬件。</summary>
        internal event EventHandler<BrightnessSliderEventArgs> BrightnessSliderChanged;

        /// <summary>正在拖动滑块：此时不要让悬浮条的悬停定时器把面板收掉。</summary>
        internal bool IsDragging
        {
            get
            {
                foreach (BrightnessRowRefs row in _brightnessRows.Values)
                {
                    if (row.Slider != null && row.Slider.IsDragging)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

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

        /// <summary>显示器亮度变了：面板正显示着就重画，高度变了要重新定位。</summary>
        internal void UpdateBrightness(MonitorBrightness[] monitors)
        {
            _monitors = monitors == null ? new MonitorBrightness[0] : monitors;
            if (!IsVisible)
            {
                return;
            }

            // 数值没变就不重建：拖动滑块期间每 30 秒扫描一次，
            // 无谓的重建会把正在拖的滑块换掉。真变了才重画
            if (BuildBrightnessSignature() == _brightnessSignature)
            {
                return;
            }

            ApplyTheme();
            PositionAboveAnchor();
        }

        /// <summary>
        /// 滚轮调亮度时就地改这一行：文字和档位色一起变，不重建整张面板。
        /// 以前滚轮只改悬浮条的图标，面板要等防抖结束才整体重建——而且拿的是
        /// 旧扫描结果里的百分比，于是出现「图标和屏幕都变了、面板数字不动」。
        /// </summary>
        internal void SetBrightnessPercent(string id, int percent)
        {
            if (_destroyed)
            {
                return;
            }

            BrightnessRowRefs row;
            if (!_brightnessRows.TryGetValue(id, out row))
            {
                return;
            }

            Color accent = UiWidgets.PercentAccent(percent, UiTheme.TextSecondary);
            row.Value.Text = UiWidgets.FormatPercent(percent);
            UiWidgets.ApplyPillAccent(row.Pill, row.Value, accent, false);
            row.Slider.SetValue(percent);
            row.Slider.SetAccent(accent);
        }

        private string BuildBrightnessSignature()
        {
            string signature = string.Empty;
            for (int i = 0; i < _monitors.Length; i++)
            {
                signature += _monitors[i].Id + ":" + _monitors[i].Percent + ";";
            }

            return signature;
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

            // WPF 在 Show 之后可能重算窗口样式（悬浮条 SetParent 后就被抹过一次
            // TOOLWINDOW），所以每次显示都校正一遍，不能只在创建时设一次
            ApplyPanelWindowStyles();
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
            ApplyPanelWindowStyles();
        }

        /// <summary>
        /// 不抢焦点、不进 Alt+Tab 和任务视图：它只是悬浮条的一个附属浮层。
        /// 少一个 WS_EX_TOOLWINDOW，任务视图里就会多出一张全黑卡片。
        /// </summary>
        private void ApplyPanelWindowStyles()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            int want = exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
            if (want != exStyle)
            {
                NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, want);
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
            // 取色规则和悬浮条共用一份，主题/自定义颜色两边永远一致
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
            _shadow.Opacity = Theme.IsLight() ? 0.16 : 0.28;

            // 文字和胶囊的颜色是建行时取的，主题一变就得重画
            Rebuild();
        }

        private void Rebuild()
        {
            _rows.Children.Clear();
            _brightnessRows.Clear();
            _brightnessSignature = BuildBrightnessSignature();

            if (_items.Length == 0 && _monitors.Length == 0)
            {
                TextBlock empty = new TextBlock();
                empty.Text = "未发现可读取电量的设备";
                empty.FontFamily = new FontFamily("Segoe UI");
                empty.FontSize = 12;
                empty.Foreground = new SolidColorBrush(UiWidgets.ToMediaColor(UiTheme.TextSecondary));
                _rows.Children.Add(empty);
                return;
            }

            if (_items.Length > 0)
            {
                // 面板的意义就是解释「为什么悬浮条只剩图标」，所以顶部点明台数
                _rows.Children.Add(CreateCaption("已连接 " + _items.Length + " 台设备"));

                for (int i = 0; i < _items.Length; i++)
                {
                    _rows.Children.Add(CreateRow(_items[i], i > 0));
                }
            }

            if (_monitors.Length > 0)
            {
                if (_items.Length > 0)
                {
                    _rows.Children.Add(CreateSectionDivider());
                }

                _rows.Children.Add(CreateCaption("显示器亮度"));

                for (int i = 0; i < _monitors.Length; i++)
                {
                    _rows.Children.Add(CreateBrightnessRow(_monitors[i], i > 0));
                }
            }
        }

        private UIElement CreateCaption(string text)
        {
            TextBlock caption = new TextBlock();
            caption.Text = text;
            caption.FontFamily = new FontFamily("Segoe UI");
            caption.FontSize = 11;
            caption.Foreground = new SolidColorBrush(UiWidgets.ToMediaColor(UiTheme.TextSecondary));
            caption.Margin = new Thickness(0, 0, 0, 8);
            return caption;
        }

        private UIElement CreateSectionDivider()
        {
            Color primary = UiWidgets.ToMediaColor(UiTheme.TextPrimary);

            Border line = new Border();
            line.Height = 1;
            line.Margin = new Thickness(0, 9, 0, 8);
            line.Background = new SolidColorBrush(Color.FromArgb(38, primary.R, primary.G, primary.B));
            return line;
        }

        /// <summary>
        /// 面板一行的公共布局：图标 / 名字（占满剩余宽度）/ 胶囊。
        /// 设备行和亮度行走同一份代码，字号、间距、胶囊样式不会各写各的。
        /// </summary>
        private Grid CreateRowLayout(
            string glyph,
            System.Drawing.Color iconColor,
            string name,
            System.Drawing.Color nameColor,
            string valueText,
            Color accent,
            bool dimmed,
            bool spaced,
            out Border pill,
            out TextBlock value)
        {
            Grid row = new Grid();
            row.Margin = new Thickness(0, spaced ? 7 : 0, 0, 0);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock icon = UiWidgets.CreateGlyph(glyph, UiWidgets.GlyphSize, iconColor);
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);

            TextBlock nameBlock = UiWidgets.CreateName(name, 0, nameColor);
            nameBlock.Margin = new Thickness(8, 0, 10, 0);
            Grid.SetColumn(nameBlock, 1);
            row.Children.Add(nameBlock);

            pill = UiWidgets.CreatePill(valueText, accent, dimmed);
            Grid.SetColumn(pill, 2);
            row.Children.Add(pill);
            value = (TextBlock)pill.Child;

            return row;
        }

        private UIElement CreateRow(DeviceBattery item, bool spaced)
        {
            bool online = item.Online;
            Border pill;
            TextBlock value;
            return CreateRowLayout(
                UiTheme.GetIconGlyph(item.Name),
                online ? UiTheme.TextSecondary : UiTheme.TextDisabled,
                UiTheme.GetDisplayName(item),
                online ? UiTheme.TextPrimary : UiTheme.TextSecondary,
                UiWidgets.FormatBatteryValue(item),
                UiWidgets.ToMediaColor(online ? UiTheme.BatteryColor(item.Percent) : UiTheme.TextSecondary),
                !online,
                spaced,
                out pill,
                out value);
        }

        /// <summary>
        /// 显示器亮度行：布局和设备行完全一致（图标 / 名称 / 胶囊），
        /// 只有数据来源不同，这样面板上下两段看起来是同一套东西。
        /// </summary>
        private UIElement CreateBrightnessRow(MonitorBrightness monitor, bool spaced)
        {
            Border pill;
            TextBlock value;
            Grid row = CreateRowLayout(
                UiWidgets.MonitorGlyph,
                UiTheme.TextSecondary,
                monitor.Name,
                UiTheme.TextPrimary,
                UiWidgets.FormatPercent(monitor.Percent),
                UiWidgets.PercentAccent(monitor.Percent, UiTheme.TextSecondary),
                false,
                spaced,
                out pill,
                out value);

            // Tag 挂在整行上：鼠标停在面板的亮度行上滚轮也能调，
            // 和悬浮条上的亮度项是同一套操作
            row.Tag = monitor;

            NeumorphicSlider slider = CreateBrightnessSlider(monitor);
            _brightnessRows[monitor.Id] = new BrightnessRowRefs(pill, value, slider);

            // 第二行放滑块：左边缘和名字对齐（图标宽度），不横跨图标列
            StackPanel container = new StackPanel();
            container.Orientation = Orientation.Vertical;
            container.Children.Add(row);
            container.Children.Add(slider);
            return container;
        }

        private NeumorphicSlider CreateBrightnessSlider(MonitorBrightness monitor)
        {
            System.Drawing.Color top;
            System.Drawing.Color bottom;
            UiWidgets.ResolveSurface(_hoverColorArgb, out top, out bottom);

            // 滑块在面板下半部，取上下渐变的中间色当「背景色」，
            // 凸起的高光和暗影才会贴着实际面板底色，而不是浮在另一个色阶上
            System.Drawing.Color surface = System.Drawing.Color.FromArgb(
                255,
                (top.R + bottom.R) / 2,
                (top.G + bottom.G) / 2,
                (top.B + bottom.B) / 2);

            NeumorphicSlider slider = NeumorphicSlider.Create(
                monitor.Percent,
                UiWidgets.PercentAccent(monitor.Percent, UiTheme.TextSecondary),
                UiWidgets.ToMediaColor(surface),
                Theme.IsLight());
            slider.Margin = new Thickness(UiWidgets.GlyphSize, 4, 0, 0);

            // 滚轮也认滑块本身：Tag 挂上显示器，低层钩子命中的判断和悬浮条一致
            slider.Tag = monitor;

            // 只捕获 Id，不捕获每轮扫描都会重建的 monitor 对象（见交接文档坑 1 / 坑 7）
            string id = monitor.Id;
            slider.ValueChanged += delegate
            {
                EventHandler<BrightnessSliderEventArgs> handler = BrightnessSliderChanged;
                if (handler != null)
                {
                    handler(this, new BrightnessSliderEventArgs(id, (int)slider.Value));
                }
            };
            return slider;
        }
    }
}
