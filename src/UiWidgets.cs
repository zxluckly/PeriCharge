using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PeripheralBattery
{
    /// <summary>
    /// 悬浮条与详情面板共用的 WPF 视觉构件。
    ///
    /// 之前两处各写各的：胶囊的内边距、名字间距、亮度/电量的图标字号、
    /// 「--%」的格式、颜色换算全都有两份，改了一处另一处就慢慢跑偏。
    /// 统一收在这里之后，样式只有一份来源，新增一种指标也能直接用同一套。
    /// </summary>
    internal static class UiWidgets
    {
        /// <summary>Segoe MDL2 Assets 的 TVMonitor。设备段和亮度段共用同一枚显示器图标。</summary>
        internal const string MonitorGlyph = "\uE7F4";

        /// <summary>图标字号。悬浮条与面板一致。</summary>
        internal const double GlyphSize = 13;

        /// <summary>名字字号。</summary>
        internal const double NameSize = 13;

        /// <summary>胶囊内文字字号。</summary>
        internal const double ValueSize = 12;

        /// <summary>图标与后面元素的水平间距。</summary>
        internal const double IconGap = 6;

        /// <summary>名字与胶囊之间的水平间距。</summary>
        internal const double NamePillGap = 8;

        /// <summary>悬浮条里名字的最大宽度（超出省略）。设备和显示器共用，样式才一致。</summary>
        internal const double NameMaxWidth = 180;

        internal static Color ToMediaColor(System.Drawing.Color color)
        {
            return Color.FromArgb(color.A, color.R, color.G, color.B);
        }

        /// <summary>图标字形。颜色传 System.Drawing.Color，和 UiTheme 的取色方式保持一致。</summary>
        internal static TextBlock CreateGlyph(string glyph, double size, System.Drawing.Color color)
        {
            TextBlock text = new TextBlock();
            text.Text = glyph;
            text.FontFamily = new FontFamily("Segoe MDL2 Assets");
            text.FontSize = size;
            text.Foreground = new SolidColorBrush(ToMediaColor(color));
            text.VerticalAlignment = VerticalAlignment.Center;
            return text;
        }

        /// <summary>名字文字块。maxWidth 小于等于 0 表示不限制宽度（面板里由星号列约束）。</summary>
        internal static TextBlock CreateName(string text, double maxWidth, System.Drawing.Color color)
        {
            TextBlock name = new TextBlock();
            name.Text = text;
            name.FontFamily = new FontFamily("Segoe UI");
            name.FontSize = NameSize;
            if (maxWidth > 0)
            {
                name.MaxWidth = maxWidth;
            }
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.Foreground = new SolidColorBrush(ToMediaColor(color));
            name.VerticalAlignment = VerticalAlignment.Center;
            return name;
        }

        /// <summary>
        /// 数值胶囊。电量、亮度共用同一份尺寸和透明度；dimmed 用于「未连接」这类弱化状态。
        /// </summary>
        internal static Border CreatePill(string text, Color accent, bool dimmed)
        {
            Border pill = new Border();
            pill.CornerRadius = new CornerRadius(9);
            pill.Padding = new Thickness(8, 1, 8, 1);
            pill.VerticalAlignment = VerticalAlignment.Center;

            TextBlock value = new TextBlock();
            value.Text = text;
            value.FontFamily = new FontFamily("Segoe UI");
            value.FontSize = ValueSize;
            value.FontWeight = FontWeights.SemiBold;
            value.VerticalAlignment = VerticalAlignment.Center;
            pill.Child = value;

            ApplyPillAccent(pill, value, accent, dimmed);
            return pill;
        }

        /// <summary>
        /// 改胶囊的档位色。滚轮调亮度跨过红/黄/绿分界时必须同步换色，
        /// 否则数字变了颜色不变，反而不如不显示。
        /// </summary>
        internal static void ApplyPillAccent(Border pill, TextBlock value, Color accent, bool dimmed)
        {
            pill.Background = new SolidColorBrush(
                Color.FromArgb(dimmed ? (byte)16 : (byte)32, accent.R, accent.G, accent.B));
            pill.BorderBrush = new SolidColorBrush(
                Color.FromArgb(dimmed ? (byte)38 : (byte)84, accent.R, accent.G, accent.B));
            pill.BorderThickness = new Thickness(1);
            value.Foreground = new SolidColorBrush(accent);
        }

        /// <summary>亮度百分比文字。没读数时统一显示「--%」，与电量「未连接」区分开。</summary>
        internal static string FormatPercent(int percent)
        {
            return percent < 0 ? "--%" : percent + "%";
        }

        /// <summary>电量文字（含充电/充满标记）。悬浮条和面板共用，避免两处标记不一样。</summary>
        internal static string FormatBatteryValue(DeviceBattery item)
        {
            if (!item.Online)
            {
                return "未连接";
            }

            string text = item.Percent + "%";
            if (item.Charging)
            {
                text += " ⚡";
            }
            else if (item.Full)
            {
                text += " ✦";
            }

            return text;
        }

        /// <summary>百分比对应的档位色；没有读数时退回次要文字色。</summary>
        internal static Color PercentAccent(int percent, System.Drawing.Color fallback)
        {
            return percent < 0
                ? ToMediaColor(fallback)
                : ToMediaColor(UiTheme.BatteryColor(percent));
        }

        /// <summary>
        /// 悬停背景的上下渐变色。0 表示跟随系统主题；自定义色时上下同色。
        /// 悬浮条和面板都按这一份规则取色。
        /// </summary>
        internal static void ResolveSurface(int hoverColorArgb, out System.Drawing.Color top, out System.Drawing.Color bottom)
        {
            if (hoverColorArgb != 0)
            {
                byte red = (byte)((hoverColorArgb >> 16) & 0xFF);
                byte green = (byte)((hoverColorArgb >> 8) & 0xFF);
                byte blue = (byte)(hoverColorArgb & 0xFF);
                top = System.Drawing.Color.FromArgb(255, red, green, blue);
                bottom = top;
                return;
            }

            top = UiTheme.BackgroundTop;
            bottom = UiTheme.BackgroundBottom;
        }
    }
}
