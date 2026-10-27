using System;
using System.Collections.Generic;

namespace PeripheralBattery
{
    /// <summary>
    /// 从 HID 输入报告读取经典蓝牙外设电量。
    /// 目前支持 DualSense（VID_054C / PID_0CE6）。
    /// </summary>
    internal static class HidBatteryService
    {
        /// <summary>
        /// 枚举结果的缓存。SetupAPI 枚举一次要把系统里每个 HID 接口都 CreateFile 打开再关闭
        /// （鼠标、键盘、各种接收器都算），一轮就是十几个句柄开合，纯属浪费。
        /// 设备增删由 WM_DEVICECHANGE 通知失效；万一某次广播没收到，超过
        /// <see cref="CacheMaxAgeSeconds"/> 秒也会强制重新枚举一次，不会一直用旧列表。
        /// </summary>
        private static List<HidDeviceInfo> s_devices;
        private static DateTime s_cachedAt = DateTime.MinValue;
        private static volatile bool s_dirty = true;

        private const int CacheMaxAgeSeconds = 180;

        /// <summary>设备列表可能已变化（插拔、休眠唤醒），下一轮重新枚举。</summary>
        internal static void Invalidate()
        {
            s_dirty = true;
        }

        private static List<HidDeviceInfo> GetDevices()
        {
            bool expired = (DateTime.Now - s_cachedAt).TotalSeconds >= CacheMaxAgeSeconds;
            if (s_devices != null && !s_dirty && !expired)
            {
                return s_devices;
            }

            try
            {
                s_devices = HidEnumerator.Enumerate();
                s_cachedAt = DateTime.Now;
                s_dirty = false;
            }
            catch (Exception ex)
            {
                Log.Error("HID 枚举失败", ex);
                if (s_devices == null)
                {
                    s_devices = new List<HidDeviceInfo>();
                }
            }

            return s_devices;
        }

        internal static List<DeviceBattery> ReadAll()
        {
            List<DeviceBattery> result = new List<DeviceBattery>();
            List<HidDeviceInfo> devices = GetDevices();

            int attempts = 0;
            int succeeded = 0;

            // 同一个手柄同时插着 USB 又连着蓝牙时，Windows 会为它暴露两个 HID 节点
            // （USB 的 VID_054C&PID_0CE6 和蓝牙的 {00001124-...}_VID&0002054C_PID&0CE6），
            // 两个都能读到电量，都读就会重复显示两个同名设备。
            // 这里先按产品名分组，遇到「恰好一个 USB 一个蓝牙」就只读 USB（插线时 USB 报告
            // 才是权威值），USB 读不到才退到蓝牙——每轮只读一遍，不用读完再合并。
            Dictionary<string, List<HidDeviceInfo>> groups = new Dictionary<string, List<HidDeviceInfo>>();
            List<string> order = new List<string>();

            foreach (HidDeviceInfo device in devices)
            {
                if (device.VendorId != 0x054C || device.ProductId != 0x0CE6
                    || device.UsagePage != 0x01 || device.Usage != 0x05)
                {
                    continue;
                }

                string key = device.ProductName ?? string.Empty;
                List<HidDeviceInfo> group;
                if (!groups.TryGetValue(key, out group))
                {
                    group = new List<HidDeviceInfo>();
                    groups[key] = group;
                    order.Add(key);
                }

                group.Add(device);
            }

            foreach (string key in order)
            {
                List<HidDeviceInfo> group = groups[key];

                HidDeviceInfo usb = null;
                HidDeviceInfo bluetooth = null;
                foreach (HidDeviceInfo device in group)
                {
                    if (IsUsbTransport(device.DevicePath))
                    {
                        usb = device;
                    }
                    else
                    {
                        bluetooth = device;
                    }
                }

                if (usb != null && bluetooth != null && group.Count == 2)
                {
                    attempts++;
                    DeviceBattery fromUsb = ReadDualSense(usb);
                    if (fromUsb != null)
                    {
                        succeeded++;
                        result.Add(fromUsb);
                        continue;
                    }

                    attempts++;
                    DeviceBattery fromBluetooth = ReadDualSense(bluetooth);
                    if (fromBluetooth != null)
                    {
                        succeeded++;
                        result.Add(fromBluetooth);
                    }
                    continue;
                }

                foreach (HidDeviceInfo device in group)
                {
                    attempts++;
                    DeviceBattery item = ReadDualSense(device);
                    if (item != null)
                    {
                        succeeded++;
                        result.Add(item);
                    }
                }
            }

            // 缓存里有设备却一个都读不到，多半是插拔后节点变了而通知没收到，
            // 下次重新枚举一次；能读到就不动，避免每轮都白枚举一遍
            if (attempts > 0 && succeeded == 0)
            {
                s_dirty = true;
            }

            return result;
        }

        /// <summary>
        /// 蓝牙 HID 的设备路径里带蓝牙 HID 服务 UUID {00001124-...}，USB 的是 VID_054C&amp;PID_0CE6。
        /// </summary>
        private static bool IsUsbTransport(string devicePath)
        {
            return devicePath == null
                || devicePath.IndexOf("{00001124", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static byte s_lastLevelByte = 0xFF;
        private static byte s_lastConnectByte = 0xFF;

        private static DeviceBattery ReadDualSense(HidDeviceInfo device)
        {
            byte[] report;
            string error;
            if (!HidEnumerator.ReadInputReport(device, 3000, out report, out error))
            {
                Log.Write("DualSense 读取失败：" + error);
                return null;
            }

            // 电量字节：蓝牙报告 78 字节在 54，USB 报告 64 字节在 53。
            // 只能按长度判断，不能按报告 ID：实测蓝牙节点回来的报告首字节也是 0x01，
            // 按 ID 会把 78 字节的蓝牙报告当成 USB 报告去读 53，读到的是别的字段
            // （日志里出现过 0x6C → 等级 12 这种非法值）。
            int reportId = report.Length > 0 ? report[0] : 0;
            int levelIndex;
            if (report.Length >= 78)
            {
                levelIndex = 54;
            }
            else if (report.Length >= 54)
            {
                levelIndex = 53;
            }
            else
            {
                Log.Write("DualSense 报告长度异常：" + report.Length);
                return null;
            }

            // 连接/断开瞬间会收到报告体为空壳的报告（第 10 字节起全 0），
            // 那种帧读到的电量字节是填充的 0，会显示成 5%
            if (levelIndex == 53 && IsEmptyBody(report))
            {
                Log.Write("DualSense 收到空壳报告，已忽略：len=" + report.Length);
                return null;
            }

            int levelByte = report[levelIndex];
            int connectByte = levelIndex + 1 < report.Length ? report[levelIndex + 1] : 0;

            // 协议参考 hid-playstation / SDL_hidapi_ps5：
            // 电量字节低 4 位是等级（0~10 档，每档约 10%），高 4 位是充电状态：
            // 0 放电 / 1 充电 / 2 充满 / 0xA、0xB 电压或温度异常 / 0xF 充电错误
            int capacity = levelByte & 0x0F;
            int chargeState = (levelByte >> 4) & 0x0F;

            // 注意：旧实现判断的是 connectByte 的 bit3，那是「USB 是否已连接」，不是充电状态
            // （SDL 的 PS5StatePacket 把 53 号字节注释为 "0x08 = USB, 0x01 = headphone"）。
            // 因为 DualSense 只能有线充电，「插着线」和「在充电」在正常场景高度重合，
            // 所以旧逻辑看起来一直是对的；但插线已充满、插线供电不足、充电异常会判断错。
            bool charging = chargeState == 0x1;
            bool full = chargeState == 0x2;
            bool abnormal = chargeState == 0xA || chargeState == 0xB || chargeState == 0xF;
            bool headphone = (connectByte & 0x01) != 0;
            bool usbConnected = (connectByte & 0x08) != 0;

            // 充满时等级位可能为 0，按等级换算会得到 5%，需要直接给 100%
            int percent = full ? 100 : Math.Min(capacity * 10 + 5, 100);

            // 只在字节变化时记录，便于核对充电状态与 USB/耳机标志的实际取值
            if (levelByte != s_lastLevelByte || connectByte != s_lastConnectByte)
            {
                s_lastLevelByte = (byte)levelByte;
                s_lastConnectByte = (byte)connectByte;
                Log.Write(string.Format(
                    "DualSense 电量字节变化：id=0x{0:X2} len={1} b[{2}]=0x{3:X2} b[{4}]=0x{5:X2}"
                    + " → 等级{6} 状态0x{7:X} 充电={8} 满={9} USB={10} 耳机={11}",
                    reportId, report.Length, levelIndex, levelByte, levelIndex + 1, connectByte,
                    capacity, chargeState, charging, full, usbConnected, headphone));
            }

            DeviceBattery item = new DeviceBattery();
            item.Id = "hid:" + device.DevicePath;
            item.Name = string.IsNullOrEmpty(device.ProductName)
                ? "DualSense Wireless Controller"
                : device.ProductName;
            item.Kind = DeviceKind.BluetoothClassic;
            item.Percent = percent;
            item.Online = true;
            item.Supported = true;
            item.UpdatedAt = DateTime.Now;
            item.Charging = charging;
            item.Full = full;
            item.Error = abnormal ? "充电异常" : (headphone ? "耳机已连接" : null);
            return item;
        }

        /// <summary>
        /// 报告体是否为空壳：第 10 字节起全为 0。
        /// 连接/断开瞬间会收到这种报告，前几个字节是合法摇杆数据，长度也够，
        /// 只有看报告体才能和真正的完整报告区分开。
        /// </summary>
        private static bool IsEmptyBody(byte[] report)
        {
            for (int i = 10; i < report.Length; i++)
            {
                if (report[i] != 0)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
