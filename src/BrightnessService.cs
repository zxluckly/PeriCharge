using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace PeripheralBattery
{
    /// <summary>一台显示器的亮度状态。</summary>
    internal sealed class MonitorBrightness
    {
        /// <summary>稳定标识（MONITOR\PnPId\实例，取不到时退回 \\.\DISPLAYn）。</summary>
        public string Id { get; set; }

        /// <summary>界面上显示的名字。</summary>
        public string Name { get; set; }

        /// <summary>GDI 设备名 \\.\DISPLAYn，写亮度时靠它重新定位显示器。</summary>
        public string DeviceName { get; set; }

        /// <summary>
        /// 同一个 HMONITOR 下的第几个物理显示器。
        /// 复制模式（两块屏显示同一画面）时 Windows 只给一个 HMONITOR，
        /// 但底下的两块屏各有自己的 DDC 通道，得靠这个下标区分。
        /// </summary>
        public int PhysicalIndex { get; set; }

        public bool Primary { get; set; }

        /// <summary>能不能通过 DDC/CI 调（OSD 里没开 DDC/CI 的屏会是 false）。</summary>
        public bool Controllable { get; set; }

        /// <summary>
        /// 内置屏：笔记本面板走 eDP，DDC/CI 根本不存在，
        /// 读写得走 WMI（WmiMonitorBrightness / WmiMonitorBrightnessMethods）。
        /// </summary>
        public bool Wmi { get; set; }

        public int Percent { get; set; }

        public MonitorBrightness()
        {
            Id = string.Empty;
            Name = string.Empty;
            DeviceName = string.Empty;
            Percent = -1;
        }

        public MonitorBrightness Clone()
        {
            MonitorBrightness copy = new MonitorBrightness();
            copy.Id = Id;
            copy.Name = Name;
            copy.DeviceName = DeviceName;
            copy.PhysicalIndex = PhysicalIndex;
            copy.Primary = Primary;
            copy.Controllable = Controllable;
            copy.Wmi = Wmi;
            copy.Percent = Percent;
            return copy;
        }
    }

    /// <summary>
    /// 显示器亮度读写：走 DDC/CI（dxva2.dll），和 lumen 用的是同一条通路。
    ///
    /// 要点：
    ///   · 物理显示器句柄不能长期持有——显示器休眠/拔线后句柄失效，之后所有调用都返回 false，
    ///     所以每次 Scan / Set 都重新枚举、用完立即 DestroyPhysicalMonitors。
    ///   · 一次 DDC 往返几十毫秒，Scan 必须在后台线程跑，不能碰 UI 线程。
    ///   · 亮度区间不一定是 0–100（有的屏是 0–50 或 10–100），统一换算成百分比。
    /// </summary>
    internal static class BrightnessService
    {
        // ---- Win32 / DDC 声明 ----

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(
            IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(
            string device, uint devNum, ref DISPLAY_DEVICE info, uint flags);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(
            IntPtr hMonitor, out uint count);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(
            IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetMonitorBrightness(
            IntPtr hPhysicalMonitor, out uint min, out uint current, out uint max);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetMonitorBrightness(IntPtr hPhysicalMonitor, uint value);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool DestroyPhysicalMonitors(
            uint count, [In] PHYSICAL_MONITOR[] monitors);

        // ---- 对外接口 ----

        /// <summary>
        /// 枚举当前所有显示器并读一次亮度。耗时约每屏几十毫秒，必须在后台线程调用。
        /// 读不到亮度的屏也会返回（Controllable=false），由上层决定显不显示。
        /// </summary>
        internal static List<MonitorBrightness> Scan()
        {
            List<MonitorBrightness> result = new List<MonitorBrightness>();
            List<IntPtr> handles = new List<IntPtr>();

            MonitorEnumProc callback = delegate(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data)
            {
                handles.Add(hMonitor);
                return true;
            };

            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
                GC.KeepAlive(callback);
            }
            catch (Exception ex)
            {
                Log.Error("枚举显示器失败", ex);
                return result;
            }

            int index = 0;
            foreach (IntPtr hMonitor in handles)
            {
                MONITORINFOEX info = new MONITORINFOEX();
                info.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                if (!GetMonitorInfo(hMonitor, ref info))
                {
                    continue;
                }

                string key = MonitorKey(info.szDevice);
                bool primary = (info.dwFlags & 0x00000001) != 0;

                uint count = 0;
                PHYSICAL_MONITOR[] physicals = null;
                bool hasPhysicals = GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out count)
                    && count > 0;
                if (hasPhysicals)
                {
                    physicals = new PHYSICAL_MONITOR[count];
                    hasPhysicals = GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicals);
                }

                if (!hasPhysicals)
                {
                    // 拿不到物理显示器句柄（极少见）：照样列出来，只是调不了
                    index++;
                    MonitorBrightness blind = new MonitorBrightness();
                    blind.DeviceName = info.szDevice;
                    blind.Id = key;
                    blind.Primary = primary;
                    blind.Name = "显示器 " + index;
                    result.Add(blind);
                    continue;
                }

                try
                {
                    // 复制模式下 Windows 只给一个 HMONITOR，但底下的每块屏都有独立 DDC 通道，
                    // 所以每个物理显示器单独出一条，不能只取第一个能控的
                    for (uint i = 0; i < count; i++)
                    {
                        index++;

                        MonitorBrightness monitor = new MonitorBrightness();
                        monitor.DeviceName = info.szDevice;
                        monitor.PhysicalIndex = (int)i;
                        monitor.Primary = primary;
                        monitor.Id = key + "#" + i;
                        monitor.Name = "显示器 " + index;

                        uint min, current, max;
                        if (GetMonitorBrightness(physicals[i].hPhysicalMonitor, out min, out current, out max)
                            && max > min)
                        {
                            monitor.Controllable = true;
                            monitor.Percent = ToPercent(current, min, max);

                            string description = physicals[i].szPhysicalMonitorDescription;
                            if (!string.IsNullOrEmpty(description)
                                && description.IndexOf("Generic", StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                monitor.Name = description;
                            }
                        }

                        result.Add(monitor);
                    }
                }
                finally
                {
                    DestroyPhysicalMonitors(count, physicals);
                }
            }

            // 内置屏（笔记本面板）走 eDP，没有 DDC 通道，单独用 WMI 读一条。
            // 复制模式下它和外接屏共用一个 HMONITOR，DDC 那边只能读到外接屏，
            // 不补这一条的话内置屏永远不出现——lumen 也是靠 WMI 才管到内置屏的
            int builtin = ReadWmiBrightness();
            if (builtin >= 0)
            {
                MonitorBrightness monitor = new MonitorBrightness();
                monitor.Id = "WMI";
                monitor.Name = "内置屏";
                monitor.DeviceName = "WMI";
                monitor.Wmi = true;
                monitor.Controllable = true;
                monitor.Percent = Math.Max(0, Math.Min(100, builtin));
                result.Add(monitor);
            }

            // 主屏排最前，内置屏排最后，其余按枚举顺序
            result.Sort(delegate(MonitorBrightness a, MonitorBrightness b)
            {
                if (a.Primary != b.Primary)
                {
                    return a.Primary ? -1 : 1;
                }

                if (a.Wmi != b.Wmi)
                {
                    return a.Wmi ? 1 : -1;
                }

                int byDevice = string.CompareOrdinal(a.DeviceName, b.DeviceName);
                if (byDevice != 0)
                {
                    return byDevice;
                }

                return a.PhysicalIndex.CompareTo(b.PhysicalIndex);
            });

            return result;
        }

        /// <summary>
        /// 把某台显示器调到指定百分比。内部重新枚举一次拿句柄（见类注释），
        /// 写完回读校验，失败重试一次——DDC 偶尔会丢一帧。
        /// </summary>
        internal static bool Set(MonitorBrightness monitor, int percent)
        {
            if (monitor == null || string.IsNullOrEmpty(monitor.DeviceName))
            {
                return false;
            }

            int target = Math.Max(0, Math.Min(100, percent));

            if (monitor.Wmi)
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    if (!WriteWmiBrightness(target))
                    {
                        continue;
                    }

                    // WmiSetBrightness 多数机器不回填返回值，靠回读确认到底生效没有
                    int actual = ReadWmiBrightness();
                    if (actual < 0 || Math.Abs(actual - target) <= 2)
                    {
                        return true;
                    }
                }

                return false;
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (SetOnce(monitor.DeviceName, monitor.PhysicalIndex, target))
                {
                    return true;
                }
            }

            return false;
        }

        // ---- 内置屏：WMI 通路 ----
        //
        // 笔记本面板的亮度由 ACPI/显卡驱动暴露成 WMI 类，DDC/CI 完全用不了。
        // 读：WmiMonitorBrightness.CurrentBrightness（0–100）
        // 写：WmiMonitorBrightnessMethods.WmiSetBrightness(Timeout, Brightness)
        // 写入比较慢（实测 0.3–1 秒），所以调用方都在后台线程里跑。

        private static int ReadWmiBrightness()
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                {
                    foreach (ManagementBaseObject item in searcher.Get())
                    {
                        object value = item["CurrentBrightness"];
                        if (value != null)
                        {
                            return Convert.ToInt32(value);
                        }
                    }
                }
            }
            catch
            {
                // 台式机没有这个 WMI 类，属正常情况
            }

            return -1;
        }

        private static bool WriteWmiBrightness(int percent)
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    "root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods"))
                {
                    foreach (ManagementBaseObject item in searcher.Get())
                    {
                        ManagementObject method = (ManagementObject)item;
                        object result = method.InvokeMethod(
                            "WmiSetBrightness",
                            new object[] { (uint)0, (byte)percent });

                        // 这个方法在多数机器上返回 null（成功但不回填），
                        // 只有明确拿到非 0 才当作失败
                        if (result == null)
                        {
                            return true;
                        }

                        return Convert.ToUInt32(result) == 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("WMI 调亮度失败", ex);
            }

            return false;
        }

        private static bool SetOnce(string deviceName, int physicalIndex, int percent)
        {
            List<IntPtr> handles = new List<IntPtr>();
            MonitorEnumProc callback = delegate(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data)
            {
                handles.Add(hMonitor);
                return true;
            };

            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
                GC.KeepAlive(callback);
            }
            catch
            {
                return false;
            }

            foreach (IntPtr hMonitor in handles)
            {
                MONITORINFOEX info = new MONITORINFOEX();
                info.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                if (!GetMonitorInfo(hMonitor, ref info))
                {
                    continue;
                }

                if (!string.Equals(info.szDevice, deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                uint count;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out count) || count == 0)
                {
                    return false;
                }

                // 复制模式下同一个 HMONITOR 挂着两块屏，必须按扫描时的下标取，
                // 否则永远只调到第一块
                if (physicalIndex < 0 || physicalIndex >= (int)count)
                {
                    return false;
                }

                PHYSICAL_MONITOR[] physicals = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicals))
                {
                    return false;
                }

                try
                {
                    IntPtr target = physicals[physicalIndex].hPhysicalMonitor;

                    uint min, current, max;
                    if (!GetMonitorBrightness(target, out min, out current, out max) || max <= min)
                    {
                        return false;
                    }

                    uint raw = min + (uint)((max - min) * (long)percent / 100);
                    if (raw > max)
                    {
                        raw = max;
                    }

                    if (!SetMonitorBrightness(target, raw))
                    {
                        return false;
                    }

                    // 回读校验：固件偶尔会吞掉一帧，读回来对不上就让外层重试
                    uint checkMin, checkCurrent, checkMax;
                    if (GetMonitorBrightness(target, out checkMin, out checkCurrent, out checkMax)
                        && checkMax > checkMin)
                    {
                        int actual = ToPercent(checkCurrent, checkMin, checkMax);
                        return Math.Abs(actual - percent) <= 2;
                    }

                    return true;
                }
                finally
                {
                    DestroyPhysicalMonitors(count, physicals);
                }
            }

            return false;
        }

        private static int ToPercent(uint value, uint min, uint max)
        {
            if (max <= min)
            {
                return -1;
            }

            long percent = (long)(value - min) * 100 / (max - min);
            if (percent < 0)
            {
                percent = 0;
            }
            if (percent > 100)
            {
                percent = 100;
            }

            return (int)percent;
        }

        /// <summary>
        /// 取显示器的设备接口路径（MONITOR\DEL4098\...），比 \\.\DISPLAYn 稳定：
        /// 插拔顺序一变，DISPLAYn 的编号会跟着变，路径不会。
        /// </summary>
        private static string MonitorKey(string deviceName)
        {
            try
            {
                DISPLAY_DEVICE device = new DISPLAY_DEVICE();
                device.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (EnumDisplayDevices(deviceName, 0, ref device, 0)
                    && !string.IsNullOrEmpty(device.DeviceID))
                {
                    return device.DeviceID;
                }
            }
            catch
            {
            }

            return deviceName;
        }
    }
}
