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
        internal static List<DeviceBattery> ReadAll()
        {
            List<DeviceBattery> result = new List<DeviceBattery>();
            List<HidDeviceInfo> devices;

            try
            {
                devices = HidEnumerator.Enumerate();
            }
            catch (Exception ex)
            {
                Log.Error("HID 枚举失败", ex);
                return result;
            }

            foreach (HidDeviceInfo device in devices)
            {
                if (device.VendorId == 0x054C && device.ProductId == 0x0CE6
                    && device.UsagePage == 0x01 && device.Usage == 0x05)
                {
                    DeviceBattery item = ReadDualSense(device);
                    if (item != null)
                    {
                        result.Add(item);
                    }
                }
            }

            return result;
        }

        private static DeviceBattery ReadDualSense(HidDeviceInfo device)
        {
            byte[] report;
            string error;
            if (!HidEnumerator.ReadInputReport(device, 3000, out report, out error))
            {
                Log.Write("DualSense 读取失败：" + error);
                return null;
            }

            // 蓝牙报告 78 字节（含 report ID）：电量字节 54、状态字节 55
            // USB 报告 64 字节：电量字节 53、状态字节 54
            int levelIndex = report.Length >= 78 ? 54 : 53;
            if (levelIndex >= report.Length)
            {
                Log.Write("DualSense 报告长度异常：" + report.Length);
                return null;
            }

            int levelByte = report[levelIndex];
            int statusByte = levelIndex + 1 < report.Length ? report[levelIndex + 1] : 0;

            // 协议参考 ds.evua.cc / hid-playstation：
            // level 低 4 位为 0-10 档，每档 10% 再加 5%；bit5 表示已充满
            // status bit3 表示正在充电，bit0 表示耳机已连接
            int capacity = levelByte & 0x0F;
            int percent = Math.Min(capacity * 10 + 5, 100);
            bool full = (levelByte & 0x20) != 0;
            bool charging = (statusByte & 0x08) != 0;
            bool headphone = (statusByte & 0x01) != 0;

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
            item.Error = headphone ? "耳机已连接" : null;
            return item;
        }
    }
}
