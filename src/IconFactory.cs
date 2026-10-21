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

                    using (Pen pen = new Pen(Color.White, 2f))
                    {
                        g.DrawRectangle(pen, 4, 9, 21, 14);
                        g.FillRectangle(Brushes.White, 26, 13, 3, 6);
                    }

                    int clamped = Math.Max(0, Math.Min(100, percent));
                    int fillWidth = (int)Math.Round(17.0 * clamped / 100.0);
                    using (SolidBrush brush = new SolidBrush(fillColor))
                    {
                        g.FillRectangle(brush, 6, 11, fillWidth, 10);
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
