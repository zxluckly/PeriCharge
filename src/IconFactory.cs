using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PeripheralBattery
{
    internal static class IconFactory
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        internal static Icon Create(int percent, bool hasData)
        {
            Bitmap bitmap = new Bitmap(32, 32);
            try
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    Color fillColor = hasData
                        ? ColorFor(percent)
                        : Color.FromArgb(160, 160, 160);

                    RectangleF body = new RectangleF(5f, 8f, 20f, 15f);
                    using (GraphicsPath bodyPath = UiTheme.RoundedRect(body, 3f))
                    using (Pen pen = new Pen(Color.White, 1.8f))
                    {
                        g.DrawPath(pen, bodyPath);
                    }

                    RectangleF cap = new RectangleF(25.2f, 12.5f, 3.2f, 6f);
                    using (GraphicsPath capPath = UiTheme.RoundedRect(cap, 1.2f))
                    using (SolidBrush capBrush = new SolidBrush(Color.White))
                    {
                        g.FillPath(capBrush, capPath);
                    }

                    int clamped = Math.Max(0, Math.Min(100, percent));
                    float innerWidth = 16f * clamped / 100f;
                    if (innerWidth > 0.5f)
                    {
                        RectangleF fillRect = new RectangleF(6.8f, 9.8f, innerWidth, 11.4f);
                        Color light = Color.FromArgb(
                            fillColor.A,
                            Math.Min(255, fillColor.R + 45),
                            Math.Min(255, fillColor.G + 45),
                            Math.Min(255, fillColor.B + 45));
                        using (GraphicsPath fillPath = UiTheme.RoundedRect(fillRect, 2f))
                        using (LinearGradientBrush brush = new LinearGradientBrush(fillRect, light, fillColor, 90f))
                        {
                            g.FillPath(brush, fillPath);
                        }
                    }
                }

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon temporary = Icon.FromHandle(handle))
                    {
                        return (Icon)temporary.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
            finally
            {
                bitmap.Dispose();
            }
        }

        internal static Color ColorFor(int percent)
        {
            if (percent <= 20)
            {
                return Color.FromArgb(255, 92, 92);
            }

            if (percent <= 50)
            {
                return Color.FromArgb(255, 196, 84);
            }

            return Color.FromArgb(120, 220, 120);
        }
    }
}
