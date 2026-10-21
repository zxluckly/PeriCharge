using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PeripheralBattery
{
    internal sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return UiTheme.MenuBackground; } }
        public override Color ImageMarginGradientBegin { get { return UiTheme.MenuBackground; } }
        public override Color ImageMarginGradientMiddle { get { return UiTheme.MenuBackground; } }
        public override Color ImageMarginGradientEnd { get { return UiTheme.MenuBackground; } }
        public override Color MenuBorder { get { return UiTheme.MenuBorder; } }
        public override Color MenuItemBorder { get { return Color.Transparent; } }
        public override Color MenuItemSelected { get { return UiTheme.MenuHover; } }
        public override Color MenuItemSelectedGradientBegin { get { return UiTheme.MenuHover; } }
        public override Color MenuItemSelectedGradientEnd { get { return UiTheme.MenuHover; } }
        public override Color MenuItemPressedGradientBegin { get { return UiTheme.MenuHover; } }
        public override Color MenuItemPressedGradientEnd { get { return UiTheme.MenuHover; } }
        public override Color SeparatorDark { get { return Color.FromArgb(52, 52, 52); } }
        public override Color SeparatorLight { get { return Color.FromArgb(52, 52, 52); } }
        public override Color CheckBackground { get { return Color.Transparent; } }
        public override Color CheckSelectedBackground { get { return UiTheme.MenuHover; } }
        public override Color CheckPressedBackground { get { return UiTheme.MenuHover; } }
    }

    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        internal DarkMenuRenderer() : base(new DarkColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush brush = new SolidBrush(UiTheme.MenuBackground))
            {
                e.Graphics.FillRectangle(brush, e.AffectedBounds);
            }
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (GraphicsPath path = UiTheme.RoundedRect(
                new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), 9))
            using (Pen pen = new Pen(UiTheme.MenuBorder, 1f))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.DrawPath(pen, path);
            }
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled)
            {
                return;
            }

            Rectangle bounds = new Rectangle(4, 1, Math.Max(0, e.Item.Width - 8), Math.Max(0, e.Item.Height - 2));
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            using (GraphicsPath path = UiTheme.RoundedRect(bounds, 4))
            using (SolidBrush brush = new SolidBrush(UiTheme.MenuHover))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            DeviceBattery device = e.Item.Tag as DeviceBattery;
            if (device != null)
            {
                Rectangle rect = e.TextRectangle;
                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    return;
                }

                int iconSize = 16;
                Rectangle iconRect = new Rectangle(rect.Left, rect.Top + (rect.Height - iconSize) / 2, iconSize, iconSize);
                UiTheme.DrawDeviceIcon(
                    e.Graphics,
                    device.Name,
                    iconRect,
                    device.Online ? UiTheme.TextSecondary : UiTheme.TextDisabled);

                int stateWidth = 56;
                Rectangle nameRect = new Rectangle(
                    rect.Left + iconSize + 6,
                    rect.Top,
                    Math.Max(0, rect.Width - iconSize - 6 - stateWidth),
                    rect.Height);

                TextRenderer.DrawText(
                    e.Graphics,
                    UiTheme.GetDisplayName(device.Name),
                    e.TextFont,
                    nameRect,
                    device.Online ? UiTheme.TextPrimary : UiTheme.TextSecondary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                string state = device.Online ? device.Percent + "%" : "未连接";
                Color stateColor = device.Online
                    ? UiTheme.BatteryColor(device.Percent)
                    : UiTheme.TextSecondary;
                Rectangle stateRect = new Rectangle(rect.Right - stateWidth, rect.Top, stateWidth, rect.Height);
                TextRenderer.DrawText(
                    e.Graphics,
                    state,
                    e.TextFont,
                    stateRect,
                    stateColor,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }

            e.TextColor = e.Item.Enabled ? UiTheme.TextPrimary : UiTheme.TextDisabled;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle rect = e.ImageRectangle;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                rect = new Rectangle(6, (e.Item.Height - 10) / 2, 10, 10);
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(UiTheme.AccentGreen, 1.8f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                Point[] points = new Point[]
                {
                    new Point(rect.Left + 1, rect.Top + rect.Height / 2),
                    new Point(rect.Left + rect.Width / 2 - 1, rect.Bottom - 2),
                    new Point(rect.Right - 1, rect.Top + 1)
                };
                e.Graphics.DrawLines(pen, points);
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            Rectangle rect = e.Item.ContentRectangle;
            using (Pen pen = new Pen(Color.FromArgb(52, 52, 52), 1f))
            {
                e.Graphics.DrawLine(pen, rect.Left + 10, rect.Top + rect.Height / 2, rect.Right - 10, rect.Top + rect.Height / 2);
            }
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = UiTheme.TextPrimary;
            base.OnRenderArrow(e);
        }
    }
}
