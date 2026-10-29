using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PeripheralBattery
{
    /// <summary>
    /// 仅图标模式的设备图标：同一字形叠两层，上层按电量从底部裁剪出「水位」。
    /// 独立成静态类、不碰 <see cref="WpfOverlay"/> 的实例状态，离线对照图才能直接
    /// 编译这一个源文件渲染 —— 验的就是线上跑的这一份代码。
    /// </summary>
    internal static class CompactIcon
    {
        /// <summary>字形大小。14px 时 25% 与 50% 几乎读不出差别，20px 一眼能分。</summary>
        internal const double GlyphSize = 20;

        /// <summary>
        /// 未填充部分的 alpha，作用在前景色的中性灰上，不用档位色。
        /// 整枚同一个色相时，「一半亮一半暗」会被眼睛读成抗锯齿而不是水位；
        /// 中性灰靠色相 + 亮度双重区分。56–80 是实测的可读区间，64 居中。
        /// </summary>
        internal const byte TrackAlpha = 64;

        /// <summary>有电时至少填这么高：1% 在 20px 字形上等于看不见，但红档又必须能认出来。</summary>
        internal const double MinFill = 0.1;

        private static readonly Typeface IconFace = new Typeface(
            new FontFamily("Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        internal static UIElement Create(DeviceBattery item, double size)
        {
            string glyph = UiTheme.GetIconGlyph(item.Name);

            // 没读到电量就没有水位可言：整枚用禁用色，也不建填充层，和改动前一致
            if (!item.Online || item.Percent < 0)
            {
                return MakeGlyph(glyph, size, ToMediaColor(UiTheme.TextDisabled));
            }

            int percent = item.Percent;
            Color accent = ToMediaColor(UiTheme.BatteryColor(percent));
            if (percent >= 100)
            {
                // 满电不留未填充层：留一层只会在字形顶边糊出灰描边
                return MakeGlyph(glyph, size, accent);
            }

            System.Drawing.Color primary = UiTheme.TextPrimary;
            Color track = Color.FromArgb(TrackAlpha, primary.R, primary.G, primary.B);

            Grid grid = new Grid();
            grid.HorizontalAlignment = HorizontalAlignment.Center;
            grid.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(MakeGlyph(glyph, size, track));

            TextBlock fillText = MakeGlyph(glyph, size, accent);
            grid.Children.Add(fillText);

            double ratio = percent / 100.0;
            if (ratio > 0 && ratio < MinFill)
            {
                ratio = MinFill;
            }

            // 比例必须按字形墨迹高度算，不能用行盒：行盒比墨迹高一大截，
            // 按行盒算出来的 25% 只覆盖到墨迹的 2% 位置
            Rect ink = InkBounds(glyph, size);
            if (ink.Height <= 0)
            {
                return grid;
            }

            double top = ink.Bottom - ink.Height * ratio;

            grid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size measured = grid.DesiredSize;
            fillText.Clip = new RectangleGeometry(
                new Rect(0, top, measured.Width, measured.Height - top));
            return grid;
        }

        private static TextBlock MakeGlyph(string glyph, double size, Color color)
        {
            TextBlock text = new TextBlock();
            text.Text = glyph;
            text.FontFamily = new FontFamily("Segoe MDL2 Assets");
            text.FontSize = size;
            text.Foreground = new SolidColorBrush(color);
            text.VerticalAlignment = VerticalAlignment.Center;
            return text;
        }

        /// <summary>字形实际占的矩形（墨迹），不是含行距的行盒。</summary>
        private static Rect InkBounds(string glyph, double size)
        {
            FormattedText formatted = new FormattedText(
                glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, IconFace, size, Brushes.Black, 1.0);
            return formatted.BuildGeometry(new Point(0, 0)).Bounds;
        }

        private static Color ToMediaColor(System.Drawing.Color color)
        {
            return Color.FromArgb(color.A, color.R, color.G, color.B);
        }
    }
}
