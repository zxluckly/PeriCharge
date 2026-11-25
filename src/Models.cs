using System;

namespace PeripheralBattery
{
    internal enum DeviceKind
    {
        BluetoothLE,
        BluetoothClassic,
        UsbReceiver
    }

    internal sealed class DeviceBattery
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public DeviceKind Kind { get; set; }
        public int Percent { get; set; }
        public bool Online { get; set; }
        public bool Supported { get; set; }
        public bool Charging { get; set; }
        public bool Full { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string Error { get; set; }

        /// <summary>
        /// 这台设备是仿冒 DualSense 的接收器（HAUTE42 之类），不是原厂手柄。
        /// 判据是 feature report 0x20 里的固件编译日期：原厂一定有，仿冒的是全 0。
        /// </summary>
        public bool Receiver { get; set; }

        public DeviceBattery()
        {
            Id = string.Empty;
            Name = string.Empty;
            Percent = -1;
        }

        public DeviceBattery Clone()
        {
            DeviceBattery copy = new DeviceBattery();
            copy.Id = Id;
            copy.Name = Name;
            copy.Kind = Kind;
            copy.Percent = Percent;
            copy.Online = Online;
            copy.Supported = Supported;
            copy.Charging = Charging;
            copy.Full = Full;
            copy.UpdatedAt = UpdatedAt;
            copy.Error = Error;
            copy.Receiver = Receiver;
            return copy;
        }
    }
}
