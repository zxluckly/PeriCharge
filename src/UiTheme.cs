using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PeripheralBattery
{
    internal enum DeviceIconKind
    {
        Generic,
        Keyboard,
        Mouse,
        Gamepad,
        Headset
    }

    internal static class UiTheme
    {
        internal static Color BackgroundTop
        {
            get { return Theme.IsLight() ? Color.FromArgb(247, 247, 247) : Color.FromArgb(45, 45, 45); }
        }

        internal static Color BackgroundBottom
        {
            get { return Theme.IsLight() ? Color.FromArgb(236, 236, 236) : Color.FromArgb(28, 28, 28); }
        }

        internal static Color Border
        {
            get { return Theme.IsLight() ? Color.FromArgb(30, 0, 0, 0) : Color.FromArgb(38, 255, 255, 255); }
        }

        internal static Color Highlight
        {
            get { return Theme.IsLight() ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(26, 255, 255, 255); }
        }

        internal static Color TextPrimary
        {
            get { return Theme.IsLight() ? Color.FromArgb(27, 27, 27) : Color.FromArgb(242, 242, 242); }
        }

        internal static Color TextSecondary
        {
            get { return Theme.IsLight() ? Color.FromArgb(95, 95, 95) : Color.FromArgb(158, 158, 158); }
        }

        internal static Color TextDisabled
        {
            get { return Theme.IsLight() ? Color.FromArgb(166, 166, 166) : Color.FromArgb(108, 108, 108); }
        }

        internal static Color AccentGreen
        {
            get { return Theme.IsLight() ? Color.FromArgb(30, 142, 62) : Color.FromArgb(112, 222, 142); }
        }

        internal static Color AccentAmber
        {
            get { return Theme.IsLight() ? Color.FromArgb(178, 106, 0) : Color.FromArgb(255, 196, 84); }
        }

        internal static Color AccentRed
        {
            get { return Theme.IsLight() ? Color.FromArgb(198, 40, 40) : Color.FromArgb(255, 108, 108); }
        }

        internal static Color MenuBackground
        {
            get { return Theme.IsLight() ? Color.FromArgb(247, 247, 247) : Color.FromArgb(32, 32, 32); }
        }

        internal static Color MenuHover
        {
            get { return Theme.IsLight() ? Color.FromArgb(228, 228, 228) : Color.FromArgb(60, 60, 60); }
        }

        internal static Color MenuBorder
        {
            get { return Theme.IsLight() ? Color.FromArgb(200, 200, 200) : Color.FromArgb(66, 66, 66); }
        }

        internal static string GetDisplayName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "未知设备";
            }

            if (name.IndexOf("Xbox Wireless Controller", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Xbox 手柄";
            }

            if (name.IndexOf("DualSense Wireless Controller", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "DualSense";
            }

            if (name.Length > 16)
            {
                return name.Substring(0, 15) + "…";
            }

            return name;
        }

        internal static Color BatteryColor(int percent)
        {
            if (percent <= 20)
            {
                return AccentRed;
            }

            if (percent <= 50)
            {
                return AccentAmber;
            }

            return AccentGreen;
        }

        internal static GraphicsPath RoundedRect(Rectangle rect, int radius)
        {
            int diameter = Math.Max(1, radius * 2);
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        internal static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            float diameter = Math.Max(1f, radius * 2f);
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        internal static DeviceIconKind GetIconKind(string name)
        {
            string lower = (name == null ? string.Empty : name).ToLowerInvariant();
            if (lower.Contains("keyboard") || lower.Contains("键盘") || lower.Contains("r75") || lower.Contains("gm2"))
            {
                return DeviceIconKind.Keyboard;
            }

            if (lower.Contains("mouse") || lower.Contains("鼠标"))
            {
                return DeviceIconKind.Mouse;
            }

            if (lower.Contains("controller") || lower.Contains("gamepad") || lower.Contains("手柄")
                || lower.Contains("dualsense") || lower.Contains("xbox") || lower.Contains("joy"))
            {
                return DeviceIconKind.Gamepad;
            }

            if (lower.Contains("headset") || lower.Contains("耳机") || lower.Contains("audio") || lower.Contains("buds"))
            {
                return DeviceIconKind.Headset;
            }

            return DeviceIconKind.Generic;
        }

        /// <summary>
        /// 设备对应的 Segoe MDL2 Assets 字形，WPF 那边（悬浮条、详情面板）共用。
        /// GDI 画的托盘图标走 <see cref="DrawDeviceIcon"/>，两边图标语义要对得上。
        /// </summary>
        internal static string GetIconGlyph(string name)
        {
            DeviceIconKind kind = GetIconKind(name);
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

        internal static void DrawDeviceIcon(Graphics g, string deviceName, Rectangle bounds, Color color)
        {
            float scale = bounds.Width / 16f;
            DeviceIconKind kind = GetIconKind(deviceName);
            using (Pen pen = new Pen(color, 1.35f * scale))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                using (SolidBrush brush = new SolidBrush(color))
                {
                    if (kind == DeviceIconKind.Keyboard)
                    {
                        RectangleF body = new RectangleF(bounds.Left + 1 * scale, bounds.Top + 5 * scale, 14 * scale, 6.5f * scale);
                        using (GraphicsPath path = RoundedRect(body, 1.5f * scale))
                        {
                            g.DrawPath(pen, path);
                        }

                        for (int i = 0; i < 4; i++)
                        {
                            float keyX = bounds.Left + (3.2f + i * 2.6f) * scale;
                            g.FillRectangle(brush, keyX, bounds.Top + 7.2f * scale, 1.1f * scale, 1.1f * scale);
                        }
                    }
                    else if (kind == DeviceIconKind.Mouse)
                    {
                        RectangleF body = new RectangleF(bounds.Left + 5 * scale, bounds.Top + 2 * scale, 6 * scale, 12 * scale);
                        using (GraphicsPath path = RoundedRect(body, 3 * scale))
                        {
                            g.DrawPath(pen, path);
                        }

                        g.DrawLine(pen, bounds.Left + 8 * scale, bounds.Top + 2.5f * scale, bounds.Left + 8 * scale, bounds.Top + 7 * scale);
                    }
                    else if (kind == DeviceIconKind.Gamepad)
                    {
                        RectangleF body = new RectangleF(bounds.Left + 1 * scale, bounds.Top + 5.5f * scale, 14 * scale, 5.5f * scale);
                        using (GraphicsPath path = RoundedRect(body, 2.5f * scale))
                        {
                            g.DrawPath(pen, path);
                        }

                        g.FillEllipse(brush, bounds.Left + 3.2f * scale, bounds.Top + 7.2f * scale, 1.6f * scale, 1.6f * scale);
                        g.FillEllipse(brush, bounds.Left + 11.2f * scale, bounds.Top + 7.2f * scale, 1.6f * scale, 1.6f * scale);
                    }
                    else if (kind == DeviceIconKind.Headset)
                    {
                        g.DrawArc(pen, bounds.Left + 3 * scale, bounds.Top + 2.5f * scale, 10 * scale, 10 * scale, 200, 140);
                        RectangleF leftEar = new RectangleF(bounds.Left + 1.5f * scale, bounds.Top + 8 * scale, 2.5f * scale, 5 * scale);
                        RectangleF rightEar = new RectangleF(bounds.Left + 12 * scale, bounds.Top + 8 * scale, 2.5f * scale, 5 * scale);
                        using (GraphicsPath path = RoundedRect(leftEar, 1.2f * scale))
                        {
                            g.DrawPath(pen, path);
                        }

                        using (GraphicsPath path = RoundedRect(rightEar, 1.2f * scale))
                        {
                            g.DrawPath(pen, path);
                        }
                    }
                    else
                    {
                        RectangleF body = new RectangleF(bounds.Left + 3 * scale, bounds.Top + 5 * scale, 10 * scale, 6.5f * scale);
                        using (GraphicsPath path = RoundedRect(body, 1.5f * scale))
                        {
                            g.DrawPath(pen, path);
                        }

                        g.FillRectangle(brush, bounds.Left + 12.5f * scale, bounds.Top + 7 * scale, 1.8f * scale, 2.5f * scale);
                    }
                }
            }
        }
    }
}
