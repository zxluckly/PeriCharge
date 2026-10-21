using System;
using System.Collections.Generic;
using System.IO;

namespace PeripheralBattery
{
    internal sealed class Settings
    {
        public bool ShowTaskbar = true;
        public int RefreshSeconds = 30;
        public readonly List<string> HiddenIds = new List<string>();

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
                foreach (string line in lines)
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    if (key == "showTaskbar")
                    {
                        settings.ShowTaskbar = value == "1";
                    }
                    else if (key == "refreshSeconds")
                    {
                        int seconds;
                        if (int.TryParse(value, out seconds) && seconds >= 10)
                        {
                            settings.RefreshSeconds = seconds;
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
                    "showTaskbar=" + (ShowTaskbar ? "1" : "0"),
                    "refreshSeconds=" + RefreshSeconds,
                    "hiddenIds=" + string.Join("|", HiddenIds.ToArray())
                };
                File.WriteAllLines(FilePath, lines);
            }
            catch
            {
            }
        }
    }
}
