using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PeripheralBattery
{
    /// <summary>
    /// 覆盖在任务栏上的透明悬浮条。
    /// 使用顶层分层窗口（UpdateLayeredWindow）实现逐像素透明与悬停效果；
    /// 位置与显示状态由 WinEventHook 事件驱动，任务栏消失/出现时同步，不做轮询。
    /// </summary>
    internal sealed class TaskbarOverlay : Form
    {
        private static readonly Font NameFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        private static readonly Font ValueFont = new Font("Segoe UI Semibold", 9f, FontStyle.Regular, GraphicsUnit.Point);
        private static readonly Font EmptyFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

        private const int IconSize = 16;
        private const int MaxNameWidth = 260;
        private const int PillWidth = 52;
        private const int PillHeight = 18;
        private const int ButtonSize = 22;
        private const int WmUpdatePosition = 0x8000 + 1;

        private readonly uint _taskbarCreatedMessage;
        private readonly NativeMethods.WinEventDelegate _winEventCallback;
        private IntPtr _winEventHook;
        private IntPtr _taskbarHandle;

        private DeviceBattery[] _items = new DeviceBattery[0];
        private string _emptyText = "正在扫描蓝牙设备…";
        private bool _enabled;
        private bool _hovered;
        private Rectangle _refreshButtonRect;

        internal event EventHandler MenuRequested;
        internal event EventHandler RefreshRequested;

        internal TaskbarOverlay()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(28, 28, 28);
            Font = NameFont;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

            _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            _winEventCallback = OnWinEvent;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 分层窗口由 UpdateLayeredWindow 绘制
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_winEventHook == IntPtr.Zero)
            {
                _winEventHook = NativeMethods.SetWinEventHook(
                    NativeMethods.EVENT_OBJECT_SHOW,
                    NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
                    IntPtr.Zero,
                    _winEventCallback,
                    0,
                    0,
                    NativeMethods.WINEVENT_OUTOFCONTEXT);
            }

            if (_enabled)
            {
                UpdatePosition();
            }
        }

        private float DpiScale
        {
            get
            {
                float scale = DeviceDpi / 96f;
                return scale <= 0 ? 1f : scale;
            }
        }

        internal void SetEnabled(bool enabled)
        {
            _enabled = enabled;
            if (!enabled)
            {
                Hide();
                return;
            }

            if (!IsHandleCreated)
            {
                CreateControl();
            }

            UpdatePosition();
        }

        internal void SetItems(DeviceBattery[] items)
        {
            _items = items;
            _emptyText = items.Length == 0 ? "未发现可读取电量的设备" : string.Empty;
            RefreshLayout();
        }

        internal void SetScanning(bool scanning)
        {
            if (scanning && _items.Length == 0)
            {
                _emptyText = "正在扫描蓝牙设备…";
                RefreshLayout();
            }
        }

        internal void RefreshPosition()
        {
            if (_enabled)
            {
                UpdatePosition();
            }
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (hwnd != _taskbarHandle || !IsHandleCreated)
            {
                return;
            }

            NativeMethods.PostMessage(Handle, WmUpdatePosition, IntPtr.Zero, IntPtr.Zero);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmUpdatePosition)
            {
                UpdatePosition();
                return;
            }

            if (m.Msg == (int)_taskbarCreatedMessage
                || m.Msg == NativeMethods.WM_DISPLAYCHANGE
                || m.Msg == NativeMethods.WM_SETTINGCHANGE)
            {
                UpdatePosition();
                return;
            }

            base.WndProc(ref m);
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (_enabled && IsHandleCreated)
            {
                BeginInvoke(new Action(UpdatePosition));
            }
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
            {
                Theme.Invalidate();
                if (_enabled && IsHandleCreated)
                {
                    BeginInvoke(new Action(delegate
                    {
                        UpdatePosition();
                        RenderLayered();
                    }));
                }
            }
        }

        private void UpdatePosition()
        {
            if (!_enabled)
            {
                Hide();
                return;
            }

            IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
            _taskbarHandle = taskbar;
            if (taskbar == IntPtr.Zero)
            {
                Hide();
                return;
            }

            NativeMethods.RECT bar;
            if (!NativeMethods.GetWindowRect(taskbar, out bar))
            {
                Hide();
                return;
            }

            int barHeight = bar.Bottom - bar.Top;
            int screenHeight = Screen.PrimaryScreen.Bounds.Height;
            bool hidden = !NativeMethods.IsWindowVisible(taskbar)
                || barHeight < 20
                || bar.Top >= screenHeight
                || bar.Bottom <= 0;

            if (!hidden)
            {
                NativeMethods.APPBARDATA appBar = new NativeMethods.APPBARDATA();
                appBar.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(appBar);
                uint state = NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETSTATE, ref appBar);
                bool autoHide = (state & NativeMethods.ABS_AUTOHIDE) != 0;
                if (autoHide)
                {
                    NativeMethods.POINT cursor;
                    if (NativeMethods.GetCursorPos(out cursor) && cursor.Y < bar.Top - 2)
                    {
                        hidden = true;
                    }
                }
            }

            if (hidden)
            {
                Hide();
                return;
            }

            int rightEdge = bar.Right - (int)Math.Round(8 * DpiScale);
            IntPtr notify = NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify != IntPtr.Zero)
            {
                NativeMethods.RECT notifyRect;
                if (NativeMethods.GetWindowRect(notify, out notifyRect) && notifyRect.Left > bar.Left)
                {
                    rightEdge = notifyRect.Left - (int)Math.Round(10 * DpiScale);
                }
            }
            else
            {
                rightEdge = bar.Right - (int)Math.Round(220 * DpiScale);
            }

            int x = rightEdge - Width;
            int y = bar.Top + (barHeight - Height) / 2;
            if (x < bar.Left)
            {
                x = bar.Left + (int)Math.Round(8 * DpiScale);
            }

            if (Location.X != x || Location.Y != y)
            {
                Location = new Point(x, y);
            }

            if (!Visible)
            {
                Show();
            }

            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HWND_TOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

            RenderLayered();
        }

        private void RefreshLayout()
        {
            float scale = DpiScale;
            int padding = (int)Math.Round(14 * scale);
            int gap = (int)Math.Round(14 * scale);
            int extra = _hovered ? (int)Math.Round(ButtonSize * scale) + (int)Math.Round(8 * scale) : 0;
            int width;
            int height = Math.Max((int)Math.Round(34 * scale), (int)Math.Round(24 * scale));

            using (Bitmap measureBitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(measureBitmap))
            {
                if (_items.Length == 0)
                {
                    int textWidth = (int)Math.Ceiling(graphics.MeasureString(_emptyText, EmptyFont).Width);
                    width = textWidth + padding * 2 + (int)Math.Round(8 * scale);
                }
                else
                {
                    width = ComputeRequiredWidth(graphics, scale, padding, gap);
                }
            }

            width = Math.Max(width, (int)(72 * scale)) + extra;
            if (Width != width || Height != height)
            {
                Width = width;
                Height = height;
            }

            if (_enabled)
            {
                UpdatePosition();
            }

            RenderLayered();
        }

        private int ComputeRequiredWidth(Graphics graphics, float scale, int padding, int gap)
        {
            int total = padding * 2;
            for (int i = 0; i < _items.Length; i++)
            {
                total += MeasureItemWidth(graphics, _items[i], scale);
                if (i < _items.Length - 1)
                {
                    total += gap;
                }
            }

            return total + (int)Math.Round(4 * scale);
        }

        private static int MeasureItemWidth(Graphics graphics, DeviceBattery item, float scale)
        {
            int iconSize = (int)Math.Round(IconSize * scale);
            int iconGap = (int)Math.Round(6 * scale);
            int nameGap = (int)Math.Round(8 * scale);
            int pillWidth = (int)Math.Round(PillWidth * scale);

            string name = UiTheme.GetDisplayName(item.Name);
            int nameWidth = (int)Math.Ceiling(graphics.MeasureString(name, NameFont).Width)
                + (int)Math.Round(8 * scale);
            nameWidth = Math.Min(nameWidth, (int)Math.Round(MaxNameWidth * scale));

            return iconSize + iconGap + nameWidth + nameGap + pillWidth;
        }

        private void RenderLayered()
        {
            if (!IsHandleCreated || Width <= 0 || Height <= 0)
            {
                return;
            }

            int width = Math.Max(1, Width);
            int height = Math.Max(1, Height);
            using (Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);

                    float scale = DpiScale;
                    int padding = (int)Math.Round(14 * scale);
                    int gap = (int)Math.Round(14 * scale);

                    DrawBackground(g, scale);

                    if (_items.Length == 0)
                    {
                        DrawEmpty(g, padding);
                    }
                    else
                    {
                        DrawContent(g, scale, padding, gap);
                    }

                    if (_hovered)
                    {
                        int buttonSize = (int)Math.Round(ButtonSize * scale);
                        int buttonGap = (int)Math.Round(8 * scale);
                        _refreshButtonRect = new Rectangle(
                            Width - padding - buttonSize,
                            (Height - buttonSize) / 2,
                            buttonSize,
                            buttonSize);
                        DrawRefreshButton(g, _refreshButtonRect);
                    }
                    else
                    {
                        _refreshButtonRect = Rectangle.Empty;
                    }
                }

                NativeMethods.SetLayeredBitmap(Handle, bitmap, Location);
            }
        }

        private void DrawBackground(Graphics g, float scale)
        {
            Rectangle full = new Rectangle(0, 0, Width, Height);
            int radius = (int)Math.Round(9 * scale);
            int alpha = _hovered ? 246 : 228;
            if (Theme.IsLight() && _hovered)
            {
                alpha = 250;
            }

            using (GraphicsPath path = UiTheme.RoundedRect(full, radius))
            {
                Color top = UiTheme.BackgroundTop;
                Color bottom = UiTheme.BackgroundBottom;
                using (LinearGradientBrush background = new LinearGradientBrush(
                    full,
                    Color.FromArgb(alpha, top),
                    Color.FromArgb(alpha, bottom),
                    LinearGradientMode.Vertical))
                {
                    g.FillPath(background, path);
                }

                using (Pen border = new Pen(UiTheme.Border, 1f))
                {
                    g.DrawPath(border, path);
                }
            }

            using (Pen highlight = new Pen(UiTheme.Highlight, 1f))
            {
                g.DrawLine(highlight, radius, 1, Width - radius - 1, 1);
            }
        }

        private void DrawEmpty(Graphics g, int padding)
        {
            using (StringFormat format = new StringFormat())
            {
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;
                using (SolidBrush brush = new SolidBrush(UiTheme.TextSecondary))
                {
                    g.DrawString(
                        _emptyText,
                        EmptyFont,
                        brush,
                        new RectangleF(padding, 0, Width - padding * 2, Height),
                        format);
                }
            }
        }

        private void DrawContent(Graphics g, float scale, int padding, int gap)
        {
            int iconSize = (int)Math.Round(IconSize * scale);
            int iconGap = (int)Math.Round(6 * scale);
            int nameGap = (int)Math.Round(8 * scale);
            int pillWidth = (int)Math.Round(PillWidth * scale);
            int pillHeight = (int)Math.Round(PillHeight * scale);
            int dotSize = Math.Max(2, (int)Math.Round(2 * scale));

            int x = padding;
            for (int i = 0; i < _items.Length; i++)
            {
                DeviceBattery item = _items[i];
                string displayName = UiTheme.GetDisplayName(item.Name);

                Rectangle iconRect = new Rectangle(x, (Height - iconSize) / 2, iconSize, iconSize);
                UiTheme.DrawDeviceIcon(
                    g,
                    item.Name,
                    iconRect,
                    item.Online ? UiTheme.TextSecondary : UiTheme.TextDisabled);
                x += iconSize + iconGap;

                int nameWidth = (int)Math.Ceiling(g.MeasureString(displayName, NameFont).Width)
                    + (int)Math.Round(8 * scale);
                nameWidth = Math.Min(nameWidth, (int)Math.Round(MaxNameWidth * scale));

                using (StringFormat format = new StringFormat())
                {
                    format.LineAlignment = StringAlignment.Center;
                    format.Trimming = StringTrimming.EllipsisCharacter;
                    format.FormatFlags = StringFormatFlags.NoWrap;
                    using (SolidBrush brush = new SolidBrush(item.Online ? UiTheme.TextPrimary : UiTheme.TextSecondary))
                    {
                        g.DrawString(
                            displayName,
                            NameFont,
                            brush,
                            new RectangleF(x, 0, nameWidth, Height),
                            format);
                    }
                }

                x += nameWidth + nameGap;

                Rectangle pillRect = new Rectangle(x, (Height - pillHeight) / 2, pillWidth, pillHeight);
                DrawPill(g, pillRect, item);
                x += pillWidth;

                if (i < _items.Length - 1)
                {
                    int dotX = x + gap / 2;
                    using (SolidBrush dot = new SolidBrush(Color.FromArgb(52, UiTheme.TextSecondary)))
                    {
                        g.FillEllipse(dot, dotX - dotSize / 2, Height / 2 - dotSize / 2, dotSize, dotSize);
                    }

                    x += gap;
                }
            }
        }

        private static void DrawPill(Graphics g, Rectangle rect, DeviceBattery item)
        {
            bool online = item.Online;
            Color accent = online ? UiTheme.BatteryColor(item.Percent) : UiTheme.TextSecondary;

            using (GraphicsPath path = UiTheme.RoundedRect(rect, rect.Height / 2))
            {
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(online ? 38 : 20, accent)))
                {
                    g.FillPath(fill, path);
                }

                using (Pen border = new Pen(Color.FromArgb(online ? 96 : 46, accent), 1f))
                {
                    g.DrawPath(border, path);
                }
            }

            string text = online ? item.Percent + "%" : "未连接";
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                using (SolidBrush brush = new SolidBrush(accent))
                {
                    g.DrawString(text, ValueFont, brush, rect, format);
                }
            }
        }

        private static void DrawRefreshButton(Graphics g, Rectangle rect)
        {
            using (GraphicsPath path = UiTheme.RoundedRect(rect, rect.Height / 2))
            {
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(28, UiTheme.TextPrimary)))
                {
                    g.FillPath(fill, path);
                }

                using (Pen border = new Pen(Color.FromArgb(60, UiTheme.TextPrimary), 1f))
                {
                    g.DrawPath(border, path);
                }
            }

            float left = rect.Left + rect.Width * 0.26f;
            float top = rect.Top + rect.Height * 0.26f;
            float size = rect.Width * 0.48f;
            using (Pen pen = new Pen(UiTheme.TextPrimary, 1.6f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, left, top, size, size, 40, 285);
                float ax = left + size * 0.82f;
                float ay = top + size * 0.08f;
                using (SolidBrush brush = new SolidBrush(UiTheme.TextPrimary))
                {
                    PointF[] arrow = new PointF[]
                    {
                        new PointF(ax - 2.6f, ay - 3.2f),
                        new PointF(ax + 3.2f, ay - 1.2f),
                        new PointF(ax - 1.4f, ay + 3.4f)
                    };
                    g.FillPolygon(brush, arrow);
                }
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (!_hovered)
            {
                _hovered = true;
                RefreshLayout();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hovered)
            {
                _hovered = false;
                RefreshLayout();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && _hovered && _refreshButtonRect.Contains(e.Location))
            {
                EventHandler refreshHandler = RefreshRequested;
                if (refreshHandler != null)
                {
                    refreshHandler(this, EventArgs.Empty);
                }

                return;
            }

            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
            {
                EventHandler handler = MenuRequested;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                if (_winEventHook != IntPtr.Zero)
                {
                    NativeMethods.UnhookWinEvent(_winEventHook);
                    _winEventHook = IntPtr.Zero;
                }
            }

            base.Dispose(disposing);
        }
    }
}
