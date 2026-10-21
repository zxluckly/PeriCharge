using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PeripheralBattery
{
    /// <summary>
    /// 嵌入任务栏的悬浮条：SetParent 到 Shell_TrayWnd，作为任务栏的子窗口。
    /// 显示/隐藏由父窗口状态同步，无需轮询定位。
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

        private readonly uint _taskbarCreatedMessage;
        private DeviceBattery[] _items = new DeviceBattery[0];
        private string _emptyText = "正在扫描蓝牙设备…";
        private bool _enabled;
        private bool _attached;

        internal event EventHandler MenuRequested;

        internal TaskbarOverlay()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(40, 40, 40);
            Font = NameFont;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

            _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
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

            AttachToTaskbar();
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
                AttachToTaskbar();
            }
        }

        private void AttachToTaskbar()
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

            if (!IsHandleCreated)
            {
                CreateHandle();
            }

            if (NativeMethods.GetParent(Handle) != taskbar)
            {
                NativeMethods.SetParent(Handle, taskbar);
                int style = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_STYLE);
                style = (style & ~NativeMethods.WS_POPUP) | NativeMethods.WS_CHILD;
                NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_STYLE, style);
                _attached = true;
            }

            Reposition();
            if (!Visible)
            {
                Show();
            }
        }

        private void Reposition()
        {
            if (!_enabled || !IsHandleCreated)
            {
                return;
            }

            IntPtr taskbar = NativeMethods.GetParent(Handle);
            if (taskbar == IntPtr.Zero)
            {
                return;
            }

            NativeMethods.RECT trayRect;
            if (!NativeMethods.GetWindowRect(taskbar, out trayRect))
            {
                return;
            }

            IntPtr notify = NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            int rightEdge = trayRect.Right - (int)Math.Round(220 * DpiScale);
            if (notify != IntPtr.Zero)
            {
                NativeMethods.RECT notifyRect;
                if (NativeMethods.GetWindowRect(notify, out notifyRect) && notifyRect.Left > trayRect.Left)
                {
                    rightEdge = notifyRect.Left - (int)Math.Round(8 * DpiScale);
                }
            }

            NativeMethods.POINT point = new NativeMethods.POINT();
            point.X = rightEdge - Width;
            point.Y = trayRect.Top + (trayRect.Bottom - trayRect.Top - Height) / 2;
            NativeMethods.ScreenToClient(taskbar, ref point);

            NativeMethods.SetWindowPos(
                Handle,
                IntPtr.Zero,
                point.X,
                point.Y,
                Width,
                Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        private void RefreshLayout()
        {
            float scale = DpiScale;
            int padding = (int)Math.Round(14 * scale);
            int gap = (int)Math.Round(14 * scale);
            int width;
            int height = Math.Max((int)Math.Round(34 * scale), (int)Math.Round(24 * scale));

            using (Bitmap measureBitmap = new Bitmap(1, 1))
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

            if (_enabled)
            {
                Reposition();
            }

            Invalidate();
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

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == (int)_taskbarCreatedMessage
                || m.Msg == NativeMethods.WM_DISPLAYCHANGE
                || m.Msg == NativeMethods.WM_SETTINGCHANGE)
            {
                if (_enabled)
                {
                    BeginInvoke(new Action(AttachToTaskbar));
                }
            }

            base.WndProc(ref m);
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (_enabled && IsHandleCreated)
            {
                BeginInvoke(new Action(AttachToTaskbar));
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
                        AttachToTaskbar();
                        Invalidate();
                    }));
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            float scale = DpiScale;
            int padding = (int)Math.Round(14 * scale);
            int gap = (int)Math.Round(14 * scale);

            if (_items.Length > 0)
            {
                int required = ComputeRequiredWidth(g, scale, padding, gap);
                if (required > Width)
                {
                    Width = required;
                    Reposition();
                    return;
                }
            }

            DrawBackground(g, scale);

            if (_items.Length == 0)
            {
                DrawEmpty(g, padding);
                return;
            }

            DrawContent(g, scale, padding, gap);
        }

        private void DrawBackground(Graphics g, float scale)
        {
            Color background = Theme.IsLight() ? Color.FromArgb(250, 250, 250) : Color.FromArgb(40, 40, 40);
            if (BackColor != background)
            {
                BackColor = background;
            }

            Rectangle full = new Rectangle(0, 0, Width, Height);
            int radius = (int)Math.Round(8 * scale);

            using (GraphicsPath path = UiTheme.RoundedRect(full, radius))
            {
                using (SolidBrush brush = new SolidBrush(background))
                {
                    g.FillPath(brush, path);
                }

                using (Pen border = new Pen(UiTheme.Border, 1f))
                {
                    g.DrawPath(border, path);
                }
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
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(online ? 30 : 16, accent)))
                {
                    g.FillPath(fill, path);
                }

                using (Pen border = new Pen(Color.FromArgb(online ? 80 : 36, accent), 1f))
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                if (_attached && IsHandleCreated)
                {
                    NativeMethods.SetParent(Handle, IntPtr.Zero);
                    _attached = false;
                }
            }

            base.Dispose(disposing);
        }
    }
}
