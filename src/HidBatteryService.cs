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

            // 蓝牙报告 78 字节，电量状态字节在 offset 54；USB 报告 64 字节在 offset 53
            int statusIndex = report.Length >= 78 ? 54 : 53;
            if (statusIndex >= report.Length)
            {
                Log.Write("DualSense 报告长度异常：" + report.Length);
                return null;
            }

            int status = report[statusIndex];
            int capacity = status & 0x0F;
            int chargingState = (status >> 4) & 0x0F;

            // capacity 为 0-10 档，每档 10%；个别固件直接上报 0-100
            int percent = capacity <= 10 ? capacity * 10 : capacity;
            if (percent > 100)
            {
                percent = 100;
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
            item.Charging = chargingState == 1 || chargingState == 2;
            return item;
        }
    }
}
