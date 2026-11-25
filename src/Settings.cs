using System;
using System.Collections.Generic;
using System.IO;

namespace PeripheralBattery
{
    /// <summary>紧凑模式下每台设备显示什么。</summary>
    internal enum CompactStyle
    {
        /// <summary>图标 + 电量胶囊（默认）。</summary>
        IconAndBattery = 0,

        /// <summary>只有图标，电量靠悬停面板看。</summary>
        IconOnly = 1
    }

    internal sealed class Settings
    {
        public bool ShowTaskbar = true;
        public int RefreshSeconds = 30;
        public readonly List<string> HiddenIds = new List<string>();

        // 悬停背景自定义：HoverColorArgb 为 0 时跟随系统主题
        public int HoverColorArgb = 0;

        /// <summary>悬停背景默认不透明度（1–255）。60% ≈ 153，与菜单里的「60%」一致。</summary>
        public const int DefaultHoverAlpha = 153;

        /// <summary>旧版本的默认值。它不在菜单的可选项里，出现即说明用户没改过。</summary>
        private const int LegacyDefaultHoverAlpha = 230;

        /// <summary>配置格式版本。1（无该键）= 旧版，2 = 透明度默认值改为 60%。</summary>
        private const int ConfigVersion = 2;

        public int HoverAlpha = DefaultHoverAlpha;

        /// <summary>设备数超过这个值就自动切紧凑模式。</summary>
        public const int DefaultCompactThreshold = 3;

        /// <summary>阈值可选范围：2–6 台。再大任务栏本来也放不下。</summary>
        public const int MinCompactThreshold = 2;
        public const int MaxCompactThreshold = 6;

        /// <summary>无视设备数量，一直用紧凑样式。</summary>
        public bool ForceCompact = false;

        public int CompactThreshold = DefaultCompactThreshold;

        public CompactStyle CompactStyle = CompactStyle.IconAndBattery;

        /// <summary>不显示在任务栏上的显示器（按稳定设备 Id 记）。默认全显示。</summary>
        public readonly List<string> HiddenBrightnessIds = new List<string>();

        /// <summary>滚轮调亮度的步长（百分比）。</summary>
        public const int DefaultBrightnessStep = 5;

        public const int MinBrightnessStep = 1;
        public const int MaxBrightnessStep = 20;

        public int BrightnessStep = DefaultBrightnessStep;

        /// <summary>
        /// 曾经被自动隐藏过的仿冒接收器。只记一次：用户之后在菜单里重新勾上，
        /// 就不会再被自动隐藏（否则每次刷新都跟他对着干）。
        /// </summary>
        public readonly List<string> AutoHiddenReceivers = new List<string>();

        private static string FolderPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PeripheralBattery");
            }
        }

        private static string FilePath
        {
            get { return Path.Combine(FolderPath, "settings.ini"); }
        }

        public static Settings Load()
        {
            Settings settings = new Settings();
            try
            {
                if (!File.Exists(FilePath))
                {
                    return settings;
                }

                string[] lines = File.ReadAllLines(FilePath);
                bool hasVersion = false;
                foreach (string line in lines)
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    if (key == "configVersion")
                    {
                        hasVersion = true;
                    }
                    else if (key == "showTaskbar")
                    {
                        settings.ShowTaskbar = value == "1";
                    }
                    else if (key == "refreshSeconds")
                    {
                        int seconds;
                        if (int.TryParse(value, out seconds) && seconds >= 10)
                        {
                            // 手改 ini 写成 999999 会变成十几天才刷新一次，加个上限
                            settings.RefreshSeconds = Math.Min(3600, seconds);
                        }
                    }
                    else if (key == "hiddenIds")
                    {
                        settings.HiddenIds.Clear();
                        string[] ids = value.Split('|');
                        foreach (string id in ids)
                        {
                            if (id.Length > 0)
                            {
                                settings.HiddenIds.Add(id);
                            }
                        }
                    }
                    else if (key == "hoverColor")
                    {
                        int argb;
                        if (int.TryParse(value, out argb))
                        {
                            settings.HoverColorArgb = argb;
                        }
                    }
                    else if (key == "hoverAlpha")
                    {
                        int alpha;
                        if (int.TryParse(value, out alpha))
                        {
                            settings.HoverAlpha = Math.Max(1, Math.Min(255, alpha));
                        }
                    }
                    else if (key == "forceCompact")
                    {
                        settings.ForceCompact = value == "1";
                    }
                    else if (key == "compactThreshold")
                    {
                        int threshold;
                        if (int.TryParse(value, out threshold))
                        {
                            settings.CompactThreshold = Math.Max(
                                MinCompactThreshold, Math.Min(MaxCompactThreshold, threshold));
                        }
                    }
                    else if (key == "compactStyle")
                    {
                        int style;
                        if (int.TryParse(value, out style) && style == (int)CompactStyle.IconOnly)
                        {
                            settings.CompactStyle = CompactStyle.IconOnly;
                        }
                    }
                    else if (key == "brightnessHidden")
                    {
                        settings.HiddenBrightnessIds.Clear();
                        string[] ids = value.Split('|');
                        foreach (string id in ids)
                        {
                            if (id.Length > 0)
                            {
                                settings.HiddenBrightnessIds.Add(id);
                            }
                        }
                    }
                    else if (key == "brightnessStep")
                    {
                        int step;
                        if (int.TryParse(value, out step))
                        {
                            settings.BrightnessStep = Math.Max(
                                MinBrightnessStep, Math.Min(MaxBrightnessStep, step));
                        }
                    }
                    else if (key == "receiverAutoHidden")
                    {
                        settings.AutoHiddenReceivers.Clear();
                        string[] ids = value.Split('|');
                        foreach (string id in ids)
                        {
                            if (id.Length > 0)
                            {
                                settings.AutoHiddenReceivers.Add(id);
                            }
                        }
                    }
                }

                // 旧版配置没有 configVersion。它的默认透明度是 230（约 90%），而 230
                // 并不在菜单的五个档位里，出现即说明用户从没动过这一项，直接迁移到新的 60%。
                if (!hasVersion)
                {
                    if (settings.HoverAlpha == LegacyDefaultHoverAlpha)
                    {
                        settings.HoverAlpha = DefaultHoverAlpha;
                    }

                    settings.Save();
                }
            }
            catch
            {
                // 配置损坏时回退默认值
            }

            return settings;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(FolderPath);
                string[] lines = new string[]
                {
                    "configVersion=" + ConfigVersion,
                    "showTaskbar=" + (ShowTaskbar ? "1" : "0"),
                    "refreshSeconds=" + RefreshSeconds,
                    "hiddenIds=" + string.Join("|", HiddenIds.ToArray()),
                    "hoverColor=" + HoverColorArgb,
                    "hoverAlpha=" + HoverAlpha,
                    "forceCompact=" + (ForceCompact ? "1" : "0"),
                    "compactThreshold=" + CompactThreshold,
                    "compactStyle=" + (int)CompactStyle,
                    "brightnessHidden=" + string.Join("|", HiddenBrightnessIds.ToArray()),
                    "brightnessStep=" + BrightnessStep,
                    "receiverAutoHidden=" + string.Join("|", AutoHiddenReceivers.ToArray())
                };

                // 先写临时文件再整体替换：直接覆盖写时如果断电或进程被杀，
                // 会留下半截文件，下次启动只剩前面几个键，用户的设置等于被重置
                string temp = FilePath + ".tmp";
                File.WriteAllLines(temp, lines);

                if (File.Exists(FilePath))
                {
                    try
                    {
                        File.Replace(temp, FilePath, null);
                        return;
                    }
                    catch
                    {
                        // 某些文件系统不支持 Replace，退回到「删掉再改名」
                    }
                }

                File.Delete(FilePath);
                File.Move(temp, FilePath);
            }
            catch
            {
            }
        }
    }
}
