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

        /// <summary>已经记过「读不到」的设备，避免定时全量扫描每分钟重复刷日志。</summary>
        private readonly HashSet<string> _reportedFailure = new HashSet<string>();

        /// <summary>
        /// 多久做一次全量扫描（秒）。
        /// 平时只刷新「当前能读到电量」的设备；关机设备不进列表也不刷新。
        /// 但要在设备重新上线时发现它，就必须偶尔把所有已配对设备探一遍——
        /// 这是唯一的发现手段，所以留一个低频兜底，靠后台派发完成，不阻塞界面。
        /// </summary>
        private const int FullScanIntervalSeconds = 60;

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

        /// <summary>每台已配对设备的会话：连接对象、缓存的 GATT 服务/特征、连接状态订阅。</summary>
        private readonly Dictionary<string, BleSession> _sessions = new Dictionary<string, BleSession>();

        private volatile bool _scanAll;
        private volatile int _version;
        private int _busy;
        private bool _loggedFirstRefresh;
        private DateTime _lastFullScanAt = DateTime.MinValue;

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
                // 定时全量扫描是发现「刚上线设备」的唯一手段，低频兜底一次
                if (!_scanAll && (DateTime.Now - _lastFullScanAt).TotalSeconds >= FullScanIntervalSeconds)
                {
                    _scanAll = true;
                }

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
            if (fullScan)
            {
                _lastFullScanAt = DateTime.Now;
            }

            if (fullScan)
            {
                await DiscoverBleSessionsAsync().ConfigureAwait(false);
            }

            // 只对「当前已连接」的设备发读请求。
            // 未连接的设备一个字节都不发，也就不存在等超时的问题——
            // 它们靠 ConnectionStatusChanged 事件通知我们什么时候连上了。
            List<BleSession> targets = new List<BleSession>();
            lock (_lock)
            {
                foreach (BleSession session in _sessions.Values)
                {
                    if (IsConnected(session))
                    {
                        targets.Add(session);
                    }
                }
            }

            for (int i = 0; i < targets.Count; i++)
            {
                StartDeviceRead(targets[i]);
            }
        }

        /// <summary>
        /// 全量发现：枚举已配对设备，为每台建立会话（BluetoothLEDevice + 连接状态订阅）。
        /// 建立会话本身不发无线请求，只是拿到对象并挂上事件，所以即使设备关机也很快。
        /// </summary>
        private async Task DiscoverBleSessionsAsync()
        {
            DeviceInformationCollection all;
            try
            {
                string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                all = await AwaitAsync(
                    DeviceInformation.FindAllAsync(selector, new string[0], DeviceInformationKind.AssociationEndpoint),
                    DateTime.Now.AddMilliseconds(EnumerationBudgetMs)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 蓝牙关闭或适配器不可用时保持旧数据
                Log.Error("枚举蓝牙设备失败", ex);
                return;
            }

            List<Task> creates = new List<Task>();
            List<string> seen = new List<string>();
            foreach (DeviceInformation info in all)
            {
                seen.Add(info.Id);
                creates.Add(EnsureSessionAsync(info));
            }

            if (creates.Count > 0)
            {
                await Task.WhenAny(
                    Task.WhenAll(creates.ToArray()),
                    Task.Delay(EnumerationBudgetMs)).ConfigureAwait(false);
            }

            // 已经不再配对的设备，会话一并清掉
            List<string> dropped = new List<string>();
            lock (_lock)
            {
                foreach (string id in _sessions.Keys)
                {
                    if (!seen.Contains(id))
                    {
                        dropped.Add(id);
                    }
                }
            }

            foreach (string id in dropped)
            {
                RemoveSession(id);
            }

            if (all.Count != _lastBleCount)
            {
                _lastBleCount = all.Count;
                int connectedCount = 0;
                lock (_lock)
                {
                    foreach (BleSession session in _sessions.Values)
                    {
                        if (IsConnected(session))
                        {
                            connectedCount++;
                        }
                    }
                }

                Log.Write("枚举到 " + all.Count + " 个已配对 BLE 设备，其中已连接 " + connectedCount + " 个");
            }
        }

        private async Task EnsureSessionAsync(DeviceInformation info)
        {
            lock (_lock)
            {
                if (_sessions.ContainsKey(info.Id))
                {
                    return;
                }
            }

            BluetoothLEDevice device = null;
            try
            {
                DateTime deadline = DateTime.Now.AddMilliseconds(EnumerationBudgetMs);
                ulong address;
                if (TryParseAddress(info.Id, out address))
                {
                    device = await AwaitAsync(
                        BluetoothLEDevice.FromBluetoothAddressAsync(address), deadline).ConfigureAwait(false);
                }

                if (device == null)
                {
                    device = await AwaitAsync(
                        BluetoothLEDevice.FromIdAsync(info.Id), deadline).ConfigureAwait(false);
                }
            }
            catch
            {
                // 单个设备拿不到对象不影响其他设备
            }

            if (device == null)
            {
                return;
            }

            BleSession session = new BleSession();
            session.Id = info.Id;
            session.Name = string.IsNullOrEmpty(info.Name) ? "未知设备" : info.Name;
            session.Device = device;

            string id = info.Id;
            session.Handler = delegate(BluetoothLEDevice sender, object args)
            {
                OnConnectionStatusChanged(id);
            };

            try
            {
                // 需要引用 Facades\System.Runtime.InteropServices.WindowsRuntime.dll，
                // 否则直接编译 winmd 时该事件不会投影成 C# event（CS1545）
                device.ConnectionStatusChanged += session.Handler;
            }
            catch
            {
                device.Dispose();
                return;
            }

            bool duplicate = false;
            lock (_lock)
            {
                if (_sessions.ContainsKey(id))
                {
                    duplicate = true;
                }
                else
                {
                    _sessions[id] = session;
                }
            }

            if (duplicate)
            {
                try
                {
                    device.ConnectionStatusChanged -= session.Handler;
                }
                catch
                {
                }

                device.Dispose();
            }
        }

        /// <summary>
        /// 设备连上/断开时由 WinRT 线程池回调。
        /// 连上就立刻读一次，断开就从列表移除——不用等下一轮刷新去"猜"。
        /// </summary>
        private void OnConnectionStatusChanged(string id)
        {
            BleSession session;
            lock (_lock)
            {
                if (!_sessions.TryGetValue(id, out session))
                {
                    return;
                }
            }

            bool connected = IsConnected(session);
            if (connected)
            {
                StartDeviceRead(session);
                return;
            }

            lock (_lock)
            {
                _devices.Remove(id);
            }

            // 服务和特征在断开时释放，设备对象和订阅保留，等它下次连上
            ReleaseGatt(session);
            _version++;
        }

        private static bool IsConnected(BleSession session)
        {
            if (session == null || session.Device == null)
            {
                return false;
            }

            try
            {
                return session.Device.ConnectionStatus == BluetoothConnectionStatus.Connected;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }

        private void ReleaseGatt(BleSession session)
        {
            if (session.Service != null)
            {
                try
                {
                    session.Service.Dispose();
                }
                catch
                {
                }

                session.Service = null;
            }

            session.Level = null;
            session.PowerState = null;
            session.LevelStatus = null;
        }

        private void RemoveSession(string id)
        {
            BleSession session;
            lock (_lock)
            {
                if (!_sessions.TryGetValue(id, out session))
                {
                    return;
                }

                _sessions.Remove(id);
                _devices.Remove(id);
            }

            ReleaseGatt(session);

            if (session.Device != null)
            {
                try
                {
                    if (session.Handler != null)
                    {
                        session.Device.ConnectionStatusChanged -= session.Handler;
                    }
                }
                catch
                {
                }

                try
                {
                    session.Device.Dispose();
                }
                catch
                {
                }
            }

            _version++;
        }

        /// <summary>
        /// 启动一台设备的读取，读到结果后自己写回 <see cref="_devices"/> 并递增版本号。
        /// 同一台设备同时只允许一个读取任务，避免上一轮还没回来又叠一次。
        /// </summary>
        private void StartDeviceRead(BleSession session)
        {
            lock (_lock)
            {
                if (!_inflight.Add(session.Id))
                {
                    return;
                }
            }

            // 故意不 await：慢设备自己读完自己回填，不许拖住本轮刷新
            Task ignored = ReadAndApplyAsync(session);
        }

        private async Task ReadAndApplyAsync(BleSession session)
        {
            try
            {
                ReadResult result = await ReadBatteryPercentAsync(session).ConfigureAwait(false);
                ApplyResult(session, result);
            }
            catch (Exception ex)
            {
                Log.Error("读取设备电量失败：" + session.Name, ex);
            }
            finally
            {
                lock (_lock)
                {
                    _inflight.Remove(session.Id);
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

        private void ApplyResult(BleSession session, ReadResult result)
        {
            lock (_lock)
            {
                if (result.Percent < 0)
                {
                    // 读不到就不进列表，已经在列表里的也移出去：
                    // 关机/休眠设备不该占一个「未连接」条目。
                    bool wasShown = _devices.Remove(session.Id);

                    // 同一台设备只记一次，避免反复刷日志
                    if (wasShown || _reportedFailure.Add(session.Id))
                    {
                        Log.Write("未读到电量，" + (wasShown ? "移出列表：" : "不显示：")
                            + session.Name + "（" + result.Error + "）");
                    }

                    return;
                }

                _reportedFailure.Remove(session.Id);

                bool isNew = !_devices.ContainsKey(session.Id);
                DeviceBattery item;
                if (!_devices.TryGetValue(session.Id, out item))
                {
                    item = new DeviceBattery();
                    item.Id = session.Id;
                    item.Kind = DeviceKind.BluetoothLE;
                    _devices[session.Id] = item;
                }

                item.Name = session.Name;
                item.Percent = result.Percent;
                item.Online = true;
                item.Supported = true;
                item.Charging = result.Charging;
                item.Full = result.Full;
                item.UpdatedAt = DateTime.Now;
                item.Error = null;

                if (isNew)
                {
                    Log.Write("已读到：" + session.Name + " " + result.Percent + "%"
                        + (result.Charging ? "（充电中）" : (result.Full ? "（已充满）" : "")));
                }
            }
        }

        private sealed class ReadResult
        {
            public int Percent;
            public string Error;
            public bool Charging;
            public bool Full;
        }

        /// <summary>
        /// 一台 BLE 设备的会话。设备对象和连接状态订阅在整个进程生命周期内保留，
        /// GATT 服务与特征只在连接期间持有（断开时 ReleaseGatt 释放）。
        /// </summary>
        private sealed class BleSession
        {
            public string Id;
            public string Name;
            public BluetoothLEDevice Device;
            public GattDeviceService Service;
            public GattCharacteristic Level;        // 0x2A19 Battery Level
            public GattCharacteristic PowerState;   // 0x2A1A Battery Power State
            public GattCharacteristic LevelStatus;  // 0x2BED Battery Level Status
            public Windows.Foundation.TypedEventHandler<BluetoothLEDevice, object> Handler;
        }

        /// <summary>
        /// 从服务里按 UUID 取特征。GetCharacteristics(uuid) 读的是系统缓存的 GATT 数据库，
        /// 不发无线请求；设备不可达时返回空列表。
        /// 同步版本已被标记过时（建议用 GetCharacteristicsForUuidAsync），
        /// 但异步版会发起无线往返，这里就是要"不发请求"，所以继续用同步版。
        /// </summary>
#pragma warning disable 0618
        private static GattCharacteristic FindCharacteristic(GattDeviceService service, Guid uuid)
        {
            try
            {
                IReadOnlyList<GattCharacteristic> list = service.GetCharacteristics(uuid);
                if (list != null && list.Count > 0)
                {
                    return list[0];
                }
            }
            catch
            {
            }

            return null;
        }
#pragma warning restore 0618

        /// <summary>
        /// 读取标准 BLE 充电状态：0x2A1A Battery Power State（优先）
        /// 或 0x2BED Battery Level Status。两者都是可选特征，失败不影响电量。
        /// </summary>
        private static async Task ReadChargeStateAsync(
            BleSession session, ReadResult result, DateTime deadline)
        {
            try
            {
                GattCharacteristic powerState = session.PowerState;
                GattCharacteristic levelStatus = session.LevelStatus;

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

        private async Task<ReadResult> ReadBatteryPercentAsync(BleSession session)
        {
            ReadResult result = new ReadResult();
            result.Percent = -1;

            BluetoothLEDevice device = session.Device;
            if (device == null)
            {
                result.Error = "会话已释放";
                return result;
            }

            // 只读已连接的设备：未连接时一个字节都不发，直接返回。
            // 以前是先发请求再等 8 秒超时才知道设备不在，这是"扫关机设备"的根源。
            if (!IsConnected(session))
            {
                result.Error = "设备未连接";
                return result;
            }

            DateTime deadline = DateTime.Now.AddMilliseconds(DeviceBudgetMs);

            try
            {
                // 服务和特征只在连接期间发现一次，之后每轮只发一次读请求。
                // GetGattService / GetCharacteristics 读的是系统缓存的 GATT 数据库，
                // 不会发起无线往返；设备不可达时返回 null 而不是等超时。
                if (session.Level == null)
                {
                    GattDeviceService service = null;
                    try
                    {
                        // 同步版读系统缓存，不发无线往返；异步版会真的去问设备
#pragma warning disable 0618
                        service = device.GetGattService(BatteryServiceUuid);
#pragma warning restore 0618
                    }
                    catch
                    {
                        service = null;
                    }

                    if (service == null)
                    {
                        result.Error = "设备没有标准电池服务";
                        return result;
                    }

                    GattCharacteristic level = FindCharacteristic(service, BatteryLevelUuid);
                    if (level == null)
                    {
                        result.Error = "电池服务中没有电量特征";
                        try
                        {
                            service.Dispose();
                        }
                        catch
                        {
                        }

                        return result;
                    }

                    session.Service = service;
                    session.Level = level;
                    session.PowerState = FindCharacteristic(service, BatteryPowerStateUuid);
                    session.LevelStatus = FindCharacteristic(service, BatteryLevelStatusUuid);
                }

                // 只认实时值：不回退 Cached（缓存值可能是几天前的历史数据）
                GattReadResult read = await AwaitAsync(
                    session.Level.ReadValueAsync(BluetoothCacheMode.Uncached), deadline).ConfigureAwait(false);

                if (read.Status != GattCommunicationStatus.Success)
                {
                    result.Error = "电量读取失败：" + read.Status;
                    return result;
                }

                DataReader reader = DataReader.FromBuffer(read.Value);
                byte value = reader.ReadByte();
                result.Percent = value > 100 ? 100 : value;

                // 可选：读取标准充电状态特征
                await ReadChargeStateAsync(session, result, deadline).ConfigureAwait(false);
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

                // 连接可能已经失效，下次重新发现服务
                ReleaseGatt(session);
                return result;
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
