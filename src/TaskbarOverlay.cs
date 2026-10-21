using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PeripheralBattery
{
    /// <summary>
    /// 锚定在任务栏通知区域左侧的无边框悬浮条。
    /// 参考 TaskbarLyrics 的做法：TOPMOST + WS_EX_NOACTIVATE，按任务栏实时矩形定位。
    /// </summary>
    internal sealed class TaskbarOverlay : Form
    {
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
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
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
            int padding = (int)Math.Round(12 * scale);
            int gap = (int)Math.Round(10 * scale);

            int contentWidth;
            if (_items.Length == 0)
            {
                contentWidth = TextRenderer.MeasureText(_emptyText, Font).Width;
            }
            else
            {
                contentWidth = 0;
                for (int i = 0; i < _items.Length; i++)
                {
                    contentWidth += MeasureItemWidth(_items[i]);
                    if (i < _items.Length - 1)
                    {
                        contentWidth += gap;
                    }
                }
            }

            // GDI 测量与实际绘制存在少量偏差，额外留出余量，避免长设备名被裁切
            int slack = (int)Math.Round(16 * scale) * Math.Max(1, _items.Length);
            int width = Math.Max(contentWidth + padding * 2 + slack, (int)(44 * scale));
            int height = Math.Max(TextRenderer.MeasureText("电量", Font).Height + (int)(6 * scale), (int)(24 * scale));

            if (Width != width || Height != height)
            {
                Width = width;
                Height = height;
            }

            Invalidate();
        }

        private int MeasureItemWidth(DeviceBattery item)
        {
            float scale = DpiScale;
            // GDI 测量值比实际绘制偏窄，按比例补偿
            int nameWidth = (int)Math.Round(TextRenderer.MeasureText(item.Name, Font).Width * 1.15);
            int percentWidth = TextRenderer.MeasureText("100%", Font).Width;
            return nameWidth + (int)Math.Round(6 * scale) + percentWidth;
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

            // 任务栏自动隐藏时高度会缩到几个像素，此时一并隐藏
            if (barWidth <= 0 || barHeight < 20)
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
                    rightEdge = trayRect.Left - (int)Math.Round(8 * DpiScale);
                }
            }
            else
            {
                // Windows 11 任务栏结构变化时的回退：给时钟和图标区留出空间
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
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = DpiScale;
            int radius = (int)Math.Round(6 * scale);

            Rectangle borderRect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = CreateRoundedRect(borderRect, radius))
            {
                using (SolidBrush background = new SolidBrush(Color.FromArgb(232, 32, 32, 32)))
                {
                    g.FillPath(background, path);
                }

                using (Pen border = new Pen(Color.FromArgb(60, 255, 255, 255), 1f))
                {
                    g.DrawPath(border, path);
                }
            }

            int left = (int)Math.Round(10 * scale);
            int textY = (Height - Font.Height) / 2;

            if (_items.Length == 0)
            {
                TextRenderer.DrawText(g, _emptyText, Font, new Point(left, textY), Color.FromArgb(190, 190, 190));
                return;
            }

            int x = left;
            for (int i = 0; i < _items.Length; i++)
            {
                DeviceBattery item = _items[i];
                Color nameColor = item.Online
                    ? Color.FromArgb(236, 236, 236)
                    : Color.FromArgb(140, 140, 140);

                string name = item.Name;
                TextRenderer.DrawText(g, name, Font, new Point(x, textY), nameColor);
                x += TextRenderer.MeasureText(name, Font).Width + (int)Math.Round(6 * scale);

                string percentText = item.Online ? item.Percent + "%" : "--";
                Color percentColor = item.Online
                    ? IconFactory.ColorFor(item.Percent)
                    : Color.FromArgb(140, 140, 140);
                TextRenderer.DrawText(g, percentText, Font, new Point(x, textY), percentColor);
                x += TextRenderer.MeasureText(percentText, Font).Width;

                if (i < _items.Length - 1)
                {
                    x += (int)Math.Round(10 * scale);
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

        private static GraphicsPath CreateRoundedRect(Rectangle rect, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
