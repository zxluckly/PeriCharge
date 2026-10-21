using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace PeripheralBattery
{
    /// <summary>
    /// 锚定在任务栏通知区域左侧的无边框悬浮条。
    /// 使用分层窗口（WS_EX_LAYERED + UpdateLayeredWindow）实现真正的透明背景，
    /// 颜色跟随系统深浅色主题。
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

        private readonly Timer _positionTimer;
        private DeviceBattery[] _items = new DeviceBattery[0];
        private string _emptyText = "正在扫描蓝牙设备…";
        private bool _enabled;

        internal event EventHandler MenuRequested;

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

            _positionTimer = new Timer();
            _positionTimer.Interval = 1000;
            _positionTimer.Tick += delegate { Reposition(); };
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
            // 分层窗口由 UpdateLayeredWindow 负责绘制
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
                _positionTimer.Stop();
                Hide();
                return;
            }

            _positionTimer.Start();
            Reposition();
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

        private void RefreshLayout()
        {
            float scale = DpiScale;
            int padding = (int)Math.Round(14 * scale);
            int gap = (int)Math.Round(14 * scale);
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

            width = Math.Max(width, (int)(72 * scale));
            if (Width != width || Height != height)
            {
                Width = width;
                Height = height;
            }

            Invalidate();
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

        private void Reposition()
        {
            if (!_enabled)
            {
                return;
            }

            IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
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

            int barWidth = bar.Right - bar.Left;
            int barHeight = bar.Bottom - bar.Top;
            int screenHeight = Screen.PrimaryScreen.Bounds.Height;

            // 任务栏不显示时（自动隐藏、全屏应用遮挡、移出屏幕）一并隐藏
            bool taskbarHidden = !NativeMethods.IsWindowVisible(taskbar)
                || barWidth <= 0
                || barHeight < 20
                || bar.Top >= screenHeight
                || bar.Bottom <= 0;

            if (!taskbarHidden)
            {
                NativeMethods.APPBARDATA appBar = new NativeMethods.APPBARDATA();
                appBar.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(appBar);
                uint state = NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETSTATE, ref appBar);
                bool autoHide = (state & NativeMethods.ABS_AUTOHIDE) != 0;
                if (autoHide)
                {
                    NativeMethods.POINT cursor;
                    if (NativeMethods.GetCursorPos(out cursor))
                    {
                        // 底部任务栏自动隐藏时，鼠标不在底部区域就视为任务栏已隐藏
                        if (cursor.Y < bar.Top - 2)
                        {
                            taskbarHidden = true;
                        }
                    }
                }
            }

            if (taskbarHidden)
            {
                Hide();
                return;
            }

            int rightEdge = bar.Right - (int)Math.Round(8 * DpiScale);
            IntPtr tray = NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (tray != IntPtr.Zero)
            {
                NativeMethods.RECT trayRect;
                if (NativeMethods.GetWindowRect(tray, out trayRect) && trayRect.Left > bar.Left)
                {
                    rightEdge = trayRect.Left - (int)Math.Round(10 * DpiScale);
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
                Invalidate();
            }

            if (!Visible)
            {
                Show();
                Invalidate();
                RenderLayered();
            }

            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HWND_TOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            RenderLayered();
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

                    if (_items.Length > 0)
                    {
                        int required = ComputeRequiredWidth(g, scale, padding, gap);
                        if (required > Width)
                        {
                            Width = required;
                            RenderLayered();
                            return;
                        }

                        DrawContent(g, scale, padding, gap);
                    }
                    else
                    {
                        DrawEmpty(g, scale, padding);
                    }
                }

                NativeMethods.SetLayeredBitmap(Handle, bitmap, Location);
            }
        }

        private void DrawContent(Graphics g, float scale, int padding, int gap)
        {
            DrawBackground(g, scale);

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

        private void DrawEmpty(Graphics g, float scale, int padding)
        {
            DrawBackground(g, scale);

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

        private void DrawBackground(Graphics g, float scale)
        {
            Rectangle full = new Rectangle(0, 0, Width, Height);
            int radius = (int)Math.Round(9 * scale);
            int alpha = Theme.IsLight() ? 238 : 232;

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

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
            {
                EventHandler handler = MenuRequested;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
        }
    }
}
