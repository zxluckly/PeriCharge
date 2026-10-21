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
        public DateTime UpdatedAt { get; set; }
        public string Error { get; set; }

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
            copy.UpdatedAt = UpdatedAt;
            copy.Error = Error;
            return copy;
        }
    }
}
