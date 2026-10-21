using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace PeripheralBattery
{
    /// <summary>
    /// 读取已配对 BLE 设备的标准电池服务（0x180F / 0x2A19）。
    /// </summary>
    internal sealed class BatteryService
    {
        private static readonly Guid BatteryServiceUuid = new Guid("0000180f-0000-1000-8000-00805f9b34fb");
        private static readonly Guid BatteryLevelUuid = new Guid("00002a19-0000-1000-8000-00805f9b34fb");

        private readonly object _lock = new object();
        private readonly Dictionary<string, DeviceBattery> _devices = new Dictionary<string, DeviceBattery>();
        private readonly HashSet<string> _supported = new HashSet<string>();

        private volatile bool _scanAll;
        private volatile int _version;
        private int _busy;

        internal BatteryService()
        {
            _scanAll = true;
        }

        internal int Version
        {
            get { return _version; }
        }

        internal void RequestFullScan()
        {
            _scanAll = true;
        }

        internal DeviceBattery[] Snapshot()
        {
            lock (_lock)
            {
                List<DeviceBattery> list = new List<DeviceBattery>();
                foreach (DeviceBattery item in _devices.Values)
                {
                    list.Add(item.Clone());
                }

                list.Sort(delegate(DeviceBattery a, DeviceBattery b)
                {
                    return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                });
                return list.ToArray();
            }
        }

        internal async Task RefreshAsync()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1)
            {
                return;
            }

            try
            {
                bool fullScan = _scanAll;
                List<DeviceInformation> targets = new List<DeviceInformation>();

                try
                {
                    string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                    DeviceInformationCollection all = await AwaitAsync(
                        DeviceInformation.FindAllAsync(selector, new string[0], DeviceInformationKind.AssociationEndpoint),
                        15000).ConfigureAwait(false);

                    foreach (DeviceInformation info in all)
                    {
                        bool known;
                        lock (_lock)
                        {
                            known = _supported.Contains(info.Id);
                        }

                        if (fullScan || known)
                        {
                            targets.Add(info);
                        }
                    }

                    Log.Write("枚举到 " + all.Count + " 个已配对 BLE 设备，本次刷新 " + targets.Count + " 个");
                }
                catch (Exception ex)
                {
                    // 蓝牙关闭或适配器不可用时保持旧数据
                    Log.Error("枚举蓝牙设备失败", ex);
                }

                foreach (DeviceInformation info in targets)
                {
                    await UpdateDeviceAsync(info).ConfigureAwait(false);
                }

                if (fullScan)
                {
                    _scanAll = false;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
                _version++;
            }
        }

        private async Task UpdateDeviceAsync(DeviceInformation info)
        {
            ReadResult result = await ReadBatteryPercentAsync(info.Id).ConfigureAwait(false);

            lock (_lock)
            {
                bool known = _supported.Contains(info.Id);
                if (result.Percent < 0 && !known)
                {
                    // 从未成功读到电量的设备（手机、平板等）不进入列表
                    Log.Write("跳过设备 " + info.Name + "：" + result.Error);
                    return;
                }

                DeviceBattery item;
                if (!_devices.TryGetValue(info.Id, out item))
                {
                    item = new DeviceBattery();
                    item.Id = info.Id;
                    item.Kind = DeviceKind.BluetoothLE;
                    _devices[info.Id] = item;
                }

                item.Name = string.IsNullOrEmpty(info.Name) ? "未知设备" : info.Name;
                if (result.Percent >= 0)
                {
                    item.Percent = result.Percent;
                    item.Online = true;
                    item.Supported = true;
                    item.UpdatedAt = DateTime.Now;
                    item.Error = null;
                    _supported.Add(info.Id);
                }
                else
                {
                    item.Online = false;
                    item.Error = result.Error;
                    Log.Write("设备读取失败：" + item.Name + "（" + result.Error + "）");
                }
            }
        }

        private sealed class ReadResult
        {
            public int Percent;
            public string Error;
        }

        private static async Task<ReadResult> ReadBatteryPercentAsync(string deviceId)
        {
            ReadResult result = new ReadResult();
            result.Percent = -1;
            BluetoothLEDevice device = null;

            try
            {
                ulong address;
                if (TryParseAddress(deviceId, out address))
                {
                    device = await AwaitAsync(
                        BluetoothLEDevice.FromBluetoothAddressAsync(address), 10000).ConfigureAwait(false);
                }

                if (device == null)
                {
                    device = await AwaitAsync(
                        BluetoothLEDevice.FromIdAsync(deviceId), 10000).ConfigureAwait(false);
                }

                if (device == null)
                {
                    result.Error = "设备不可达";
                    return result;
                }

                // 先用缓存模式拿服务/特征列表：未连接的设备也能返回，避免 Unreachable
                GattDeviceServicesResult services = await AwaitAsync(
                    device.GetGattServicesAsync(), 10000).ConfigureAwait(false);

                if (services.Status != GattCommunicationStatus.Success)
                {
                    result.Error = "GATT 服务读取失败：" + services.Status;
                    return result;
                }

                GattDeviceService batteryService = null;
                foreach (GattDeviceService service in services.Services)
                {
                    if (service.Uuid == BatteryServiceUuid)
                    {
                        batteryService = service;
                        break;
                    }
                }

                if (batteryService == null)
                {
                    result.Error = "设备没有标准电池服务";
                    return result;
                }

                GattCharacteristicsResult characteristics = await AwaitAsync(
                    batteryService.GetCharacteristicsAsync(), 8000).ConfigureAwait(false);

                if (characteristics.Status != GattCommunicationStatus.Success)
                {
                    result.Error = "特征读取失败：" + characteristics.Status;
                    return result;
                }

                GattCharacteristic batteryLevel = null;
                foreach (GattCharacteristic characteristic in characteristics.Characteristics)
                {
                    if (characteristic.Uuid == BatteryLevelUuid)
                    {
                        batteryLevel = characteristic;
                        break;
                    }
                }

                if (batteryLevel == null)
                {
                    result.Error = "电池服务中没有电量特征";
                    return result;
                }

                // 先读实时值；设备休眠导致连接失败时退回缓存值
                GattReadResult read = await AwaitAsync(
                    batteryLevel.ReadValueAsync(BluetoothCacheMode.Uncached), 10000).ConfigureAwait(false);

                if (read.Status != GattCommunicationStatus.Success)
                {
                    read = await AwaitAsync(
                        batteryLevel.ReadValueAsync(BluetoothCacheMode.Cached), 5000).ConfigureAwait(false);
                }

                if (read.Status != GattCommunicationStatus.Success)
                {
                    result.Error = "电量读取失败：" + read.Status;
                    return result;
                }

                DataReader reader = DataReader.FromBuffer(read.Value);
                byte value = reader.ReadByte();
                result.Percent = value > 100 ? 100 : value;
                return result;
            }
            catch (TimeoutException)
            {
                result.Error = "读取超时";
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.GetType().Name + "：" + ex.Message;
                return result;
            }
            finally
            {
                if (device != null)
                {
                    device.Dispose();
                }
            }
        }

        private static async Task<T> AwaitAsync<T>(Windows.Foundation.IAsyncOperation<T> operation, int timeoutMs)
        {
            Task<T> task = System.WindowsRuntimeSystemExtensions.AsTask(operation);
            Task completed = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (completed != task)
            {
                throw new TimeoutException();
            }

            return await task.ConfigureAwait(false);
        }

        private static bool TryParseAddress(string deviceId, out ulong address)
        {
            address = 0;
            if (string.IsNullOrEmpty(deviceId))
            {
                return false;
            }

            int index = deviceId.LastIndexOf('-');
            if (index < 0)
            {
                return false;
            }

            string mac = deviceId.Substring(index + 1).Replace(":", string.Empty);
            return ulong.TryParse(mac, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
        }
    }
}
