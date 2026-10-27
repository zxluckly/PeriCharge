using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private readonly Dictionary<string, DateTime> _retryAfter = new Dictionary<string, DateTime>();

        /// <summary>读不到的设备退避多久再试（秒）。</summary>
        private const int RetryBackoffSeconds = 60;

        /// <summary>
        /// 单个 BLE 设备一次读取的总预算（毫秒）。
        /// 关机设备的每次 WinRT 调用都要等系统超时，逐段相加能到三十多秒，
        /// 所以整台设备共用一个截止时间，到点就放弃，别再往下走。
        /// </summary>
        private const int DeviceBudgetMs = 8000;

        /// <summary>枚举已配对设备的预算（毫秒）。正常几十毫秒就返回，这里只防卡死。</summary>
        private const int EnumerationBudgetMs = 1500;

        /// <summary>一轮刷新超过这个耗时就打一条日志（毫秒）。</summary>
        private const int SlowRefreshMs = 3000;

        /// <summary>正在读取的设备，防止上一轮还没回来又发起一次。</summary>
        private readonly HashSet<string> _inflight = new HashSet<string>();

        private volatile bool _scanAll;
        private volatile int _version;
        private int _busy;
        private bool _loggedFirstRefresh;

        /// <summary>上次记录的已配对 BLE 设备数量，用于抑制重复日志（-1 表示还没记过）。</summary>
        private int _lastBleCount = -1;

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
            // 全量扫描意味着设备可能已经变了，HID 的枚举缓存一起作废
            HidBatteryService.Invalidate();
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

            Stopwatch watch = Stopwatch.StartNew();

            try
            {
                // HID（DualSense 这类经典蓝牙手柄）是毫秒级返回的，先读并立刻发布一次，
                // 否则首屏要等后面 BLE 的枚举和各设备超时，看起来像启动卡了十几秒。
                await UpdateHidDevicesAsync().ConfigureAwait(false);
                _version++;

                // 这里只做「枚举 + 派发」，不等待各设备读回来：
                // 关机设备单次要等十几秒，等它就是把整轮刷新一起拖住。
                // 每台设备读到结果后自己 ApplyResult 并递增 _version，UI 一秒内自然刷新。
                await UpdateBleDevicesAsync().ConfigureAwait(false);

                if (_scanAll)
                {
                    _scanAll = false;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
                _version++;
            }

            watch.Stop();

            // 只在首轮和超预算时记录，正常情况下一行都不写
            if (!_loggedFirstRefresh || watch.ElapsedMilliseconds > SlowRefreshMs)
            {
                _loggedFirstRefresh = true;
                Log.Write("刷新耗时 " + watch.ElapsedMilliseconds + " ms（HID + BLE 枚举与派发）");
            }
        }

        private async Task UpdateBleDevicesAsync()
        {
            bool fullScan = _scanAll;
            List<DeviceInformation> targets = new List<DeviceInformation>();

            try
            {
                string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                DeviceInformationCollection all = await AwaitAsync(
                    DeviceInformation.FindAllAsync(selector, new string[0], DeviceInformationKind.AssociationEndpoint),
                    DateTime.Now.AddMilliseconds(EnumerationBudgetMs)).ConfigureAwait(false);

                DateTime now = DateTime.Now;
                foreach (DeviceInformation info in all)
                {
                    bool known;
                    DateTime retryAt = DateTime.MinValue;
                    lock (_lock)
                    {
                        known = _supported.Contains(info.Id);
                        if (!fullScan)
                        {
                            _retryAfter.TryGetValue(info.Id, out retryAt);
                        }
                    }

                    if (!fullScan && !known)
                    {
                        continue;
                    }

                    // 关机或休眠的设备每次刷新都要等满超时（实测 8 秒），
                    // 失败后退避一段时间再试，避免每一轮都被它拖慢
                    if (!fullScan && retryAt > now)
                    {
                        continue;
                    }

                    targets.Add(info);
                }

                // 这条每轮都会命中，只在数量变化时记一次，
                // 否则一天下来光这一行就能刷掉上百 KB 日志
                if (all.Count != _lastBleCount)
                {
                    _lastBleCount = all.Count;
                    Log.Write("枚举到 " + all.Count + " 个已配对 BLE 设备，本次刷新 " + targets.Count + " 个");
                }
            }
            catch (Exception ex)
            {
                // 蓝牙关闭或适配器不可用时保持旧数据
                Log.Error("枚举蓝牙设备失败", ex);
            }

            // 派发出去就返回，不等结果：慢设备的结果由它自己完成后回填
            for (int i = 0; i < targets.Count; i++)
            {
                StartDeviceRead(targets[i]);
            }
        }

        /// <summary>
        /// 启动一台设备的读取，读到结果后自己写回 <see cref="_devices"/> 并递增版本号。
        /// 同一台设备同时只允许一个读取任务，避免上一轮还没回来又叠一次。
        /// </summary>
        private void StartDeviceRead(DeviceInformation info)
        {
            lock (_lock)
            {
                if (!_inflight.Add(info.Id))
                {
                    return;
                }
            }

            // 故意不 await：慢设备自己读完自己回填，不许拖住本轮刷新
            Task ignored = ReadAndApplyAsync(info);
        }

        private async Task ReadAndApplyAsync(DeviceInformation info)
        {
            try
            {
                ReadResult result = await ReadBatteryPercentAsync(info.Id).ConfigureAwait(false);
                ApplyResult(info, result);
            }
            catch (Exception ex)
            {
                Log.Error("读取设备电量失败：" + info.Name, ex);
            }
            finally
            {
                lock (_lock)
                {
                    _inflight.Remove(info.Id);
                }

                // 结果落地后让 UI 尽快刷新，不用等下一轮
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

        private void ApplyResult(DeviceInformation info, ReadResult result)
        {
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
                    _retryAfter.Remove(info.Id);
                }
                else
                {
                    item.Online = false;
                    item.Error = result.Error;
                    // 有电池服务但这次读不到（关机/休眠/不可达）：退避后再试，
                    // 免得每轮刷新都为它等一次超时；没有电池服务的设备不用退避
                    if (result.HasBatteryService)
                    {
                        _retryAfter[info.Id] = DateTime.Now.AddSeconds(RetryBackoffSeconds);
                    }

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
            IReadOnlyList<GattCharacteristic> characteristics, ReadResult result, DateTime deadline)
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
                        powerState.ReadValueAsync(BluetoothCacheMode.Uncached), deadline).ConfigureAwait(false);

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
                        levelStatus.ReadValueAsync(BluetoothCacheMode.Uncached), deadline).ConfigureAwait(false);

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
                            // Additional Battery Status 低 3 位：
                            // 0 未知 / 1 充电中 / 2 放电中 / 3 已充满 / 4 未充电
                            int chargeState = data[index] & 0x07;
                            result.Charging = chargeState == 1;
                            result.Full = chargeState == 3;
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

            // 整台设备共用一个截止时间：关机设备每段 WinRT 调用都要等系统超时，
            // 逐段相加能到三十多秒，必须整体封顶
            DateTime deadline = DateTime.Now.AddMilliseconds(DeviceBudgetMs);

            // GattDeviceService 各自持有一个设备句柄，必须逐个 Dispose。
            // 之前只 Dispose 了 BluetoothLEDevice，这些服务对象一直挂到 GC 才释放，
            // 每轮刷新就漏掉一批句柄（Xbox 这类设备一次能开出十来个 GATT 服务）。
            List<GattDeviceService> openedServices = new List<GattDeviceService>();

            try
            {
                ulong address;
                if (TryParseAddress(deviceId, out address))
                {
                    device = await AwaitAsync(
                        BluetoothLEDevice.FromBluetoothAddressAsync(address), deadline).ConfigureAwait(false);
                }

                if (device == null)
                {
                    device = await AwaitAsync(
                        BluetoothLEDevice.FromIdAsync(deviceId), deadline).ConfigureAwait(false);
                }

                if (device == null)
                {
                    result.Error = "设备不可达";
                    return result;
                }

                // 用缓存服务列表快速找到电池服务（未连接设备也能返回），但读值必须走 Uncached
                GattDeviceServicesResult services = await AwaitAsync(
                    device.GetGattServicesAsync(), deadline).ConfigureAwait(false);

                if (services.Status != GattCommunicationStatus.Success)
                {
                    result.Error = "GATT 服务读取失败：" + services.Status;
                    return result;
                }

                GattDeviceService batteryService = null;
                foreach (GattDeviceService service in services.Services)
                {
                    openedServices.Add(service);
                    if (service.Uuid == BatteryServiceUuid)
                    {
                        batteryService = service;
                    }
                }

                if (batteryService == null)
                {
                    result.Error = "设备没有标准电池服务";
                    return result;
                }

                result.HasBatteryService = true;

                GattCharacteristicsResult characteristics = await AwaitAsync(
                    batteryService.GetCharacteristicsAsync(), deadline).ConfigureAwait(false);

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
                    batteryLevel.ReadValueAsync(BluetoothCacheMode.Uncached), deadline).ConfigureAwait(false);

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
                await ReadChargeStateAsync(characteristics.Characteristics, result, deadline).ConfigureAwait(false);
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
                foreach (GattDeviceService service in openedServices)
                {
                    try
                    {
                        service.Dispose();
                    }
                    catch
                    {
                        // 设备已经掉线时 Dispose 也可能抛，忽略即可
                    }
                }

                if (device != null)
                {
                    device.Dispose();
                }
            }
        }

        /// <summary>
        /// 等待一个 WinRT 异步操作，但不超过 deadline。
        /// 超时后这里只是「不再等」，底层操作仍会自己跑完（最终返回 Unreachable 之类）。
        /// 试过给它挂 CancellationToken 主动取消，实测句柄增长反而更快，
        /// 所以维持放弃等待的写法，不主动取消。
        /// </summary>
        private static async Task<T> AwaitAsync<T>(Windows.Foundation.IAsyncOperation<T> operation, DateTime deadline)
        {
            int remaining = RemainingMs(deadline);
            if (remaining <= 0)
            {
                throw new TimeoutException();
            }

            Task<T> task = System.WindowsRuntimeSystemExtensions.AsTask(operation);
            Task completed = await Task.WhenAny(task, Task.Delay(remaining)).ConfigureAwait(false);
            if (completed != task)
            {
                throw new TimeoutException();
            }

            return await task.ConfigureAwait(false);
        }

        private static int RemainingMs(DateTime deadline)
        {
            double left = (deadline - DateTime.Now).TotalMilliseconds;
            if (left <= 0)
            {
                return 0;
            }

            return left > int.MaxValue ? int.MaxValue : (int)left;
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
