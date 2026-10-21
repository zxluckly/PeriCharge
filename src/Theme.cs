using System;
using Microsoft.Win32;

namespace PeripheralBattery
{
    internal static class Theme
    {
        private static bool _cachedLight;
        private static DateTime _cachedAt = DateTime.MinValue;

        internal static bool IsLight()
        {
            if ((DateTime.Now - _cachedAt).TotalSeconds < 1.0)
            {
                return _cachedLight;
            }

            _cachedLight = ReadSystemLightTheme();
            _cachedAt = DateTime.Now;
            return _cachedLight;
        }

        internal static void Invalidate()
        {
            _cachedAt = DateTime.MinValue;
        }

        private static bool ReadSystemLightTheme()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object system = key.GetValue("SystemUsesLightTheme");
                        if (system is int)
                        {
                            return (int)system != 0;
                        }

                        object apps = key.GetValue("AppsUseLightTheme");
                        if (apps is int)
                        {
                            return (int)apps != 0;
                        }
                    }
                }
            }
            catch
            {
            }

            return false;
        }
    }
}
