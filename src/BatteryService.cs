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
        private static readonly Guid BatteryPowerStateUuid = new Guid("00002a1a-0000-1000-8000-00805f9b34fb");
        private static readonly Guid BatteryLevelStatusUuid = new Guid("00002bed-0000-1000-8000-00805f9b34fb");

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

                await UpdateHidDevicesAsync().ConfigureAwait(false);

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

        private async Task UpdateHidDevicesAsync()
        {
            List<DeviceBattery> hidDevices = await Task.Run(delegate
            {
                return HidBatteryService.ReadAll();
            }).ConfigureAwait(false);

            lock (_lock)
            {
                foreach (DeviceBattery item in hidDevices)
                {
                    _devices[item.Id] = item;
                }

                // 本次未枚举到的 HID 设备视为已断开
                List<string> stale = new List<string>();
                foreach (string key in _devices.Keys)
                {
                    if (!key.StartsWith("hid:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    bool found = false;
                    foreach (DeviceBattery item in hidDevices)
                    {
                        if (item.Id == key)
                        {
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        stale.Add(key);
                    }
                }

                foreach (string key in stale)
                {
                    _devices.Remove(key);
                }
            }
        }

        private async Task UpdateDeviceAsync(DeviceInformation info)
        {
                ReadResult result = await ReadBatteryPercentAsync(info.Id).ConfigureAwait(false);

            lock (_lock)
            {
                bool known = _supported.Contains(info.Id);
                if (result.Percent < 0 && !known && !result.HasBatteryService)
                {
                    // 没有电池服务的设备（手机、平板等）不进入列表
                    Log.Write("跳过设备 " + info.Name + "：" + result.Error);
                    return;
                }

                // 有电池服务的设备即使当前未连接也保留在列表中，后续刷新继续尝试
                if (result.HasBatteryService)
                {
                    _supported.Add(info.Id);
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
                    item.Charging = result.Charging;
                    item.Full = result.Full;
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
            public bool HasBatteryService;
            public bool Charging;
            public bool Full;
        }

        /// <summary>
        /// 读取标准 BLE 充电状态：0x2A1A Battery Power State（优先）
        /// 或 0x2BED Battery Level Status。两者都是可选特征，失败不影响电量。
        /// </summary>
        private static async Task ReadChargeStateAsync(
            IReadOnlyList<GattCharacteristic> characteristics, ReadResult result)
        {
            try
            {
                GattCharacteristic powerState = null;
                GattCharacteristic levelStatus = null;
                foreach (GattCharacteristic characteristic in characteristics)
                {
                    if (characteristic.Uuid == BatteryPowerStateUuid)
                    {
                        powerState = characteristic;
                    }
                    else if (characteristic.Uuid == BatteryLevelStatusUuid)
                    {
                        levelStatus = characteristic;
                    }
                }

                if (powerState != null)
                {
                    GattReadResult read = await AwaitAsync(
                        powerState.ReadValueAsync(BluetoothCacheMode.Uncached), 5000).ConfigureAwait(false);

                    if (read != null && read.Status == GattCommunicationStatus.Success
                        && read.Value.Length >= 1)
                    {
                        DataReader reader = DataReader.FromBuffer(read.Value);
                        byte value = reader.ReadByte();
                        // bits 6-7: 0=Unknown, 1=Charging, 2=Discharging, 3=Not charging
                        int chargeState = (value >> 6) & 0x03;
                        result.Charging = chargeState == 1;
                        return;
                    }
                }

                if (levelStatus != null)
                {
                    GattReadResult read = await AwaitAsync(
                        levelStatus.ReadValueAsync(BluetoothCacheMode.Uncached), 5000).ConfigureAwait(false);

                    if (read != null && read.Status == GattCommunicationStatus.Success
                        && read.Value.Length >= 2)
                    {
                        byte[] data = new byte[read.Value.Length];
                        DataReader.FromBuffer(read.Value).ReadBytes(data);

                        int flags = data[0];
                        int index = 1;
                        if ((flags & 0x01) != 0)
                        {
                            index++; // Battery Level Present
                        }

                        if ((flags & 0x02) != 0 && index < data.Length)
                        {
                            // Additional Battery Status bits 0-1: 1=Charging
                            int chargeState = data[index] & 0x03;
                            result.Charging = chargeState == 1;
                        }
                    }
                }
            }
            catch
            {
                // 充电状态是可选信息，读取失败忽略
            }
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

                // 用缓存服务列表快速找到电池服务（未连接设备也能返回），但读值必须走 Uncached
                GattDeviceServicesResult services = await AwaitAsync(
                    device.GetGattServicesAsync(), 8000).ConfigureAwait(false);

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

                result.HasBatteryService = true;

                GattCharacteristicsResult characteristics = await AwaitAsync(
                    batteryService.GetCharacteristicsAsync(), 5000).ConfigureAwait(false);

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

                // 只认实时值：未连接/休眠的设备会返回 Unreachable，此时标记离线，
                // 绝不回退到 Cached（缓存值可能是几天前的历史数据）。
                GattReadResult read = await AwaitAsync(
                    batteryLevel.ReadValueAsync(BluetoothCacheMode.Uncached), 8000).ConfigureAwait(false);

                if (read.Status != GattCommunicationStatus.Success)
                {
                    result.Error = device.ConnectionStatus == BluetoothConnectionStatus.Disconnected
                        ? "设备未连接（" + read.Status + "）"
                        : "电量读取失败：" + read.Status;
                    return result;
                }

                DataReader reader = DataReader.FromBuffer(read.Value);
                byte value = reader.ReadByte();
                result.Percent = value > 100 ? 100 : value;

                // 可选：读取标准充电状态特征
                await ReadChargeStateAsync(characteristics.Characteristics, result).ConfigureAwait(false);
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
