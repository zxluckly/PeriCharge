# 外设电量监控

Windows 托盘 + 任务栏悬浮条，实时显示蓝牙外设电量。

## 当前 MVP 能力

- 枚举已配对的 BLE（低功耗蓝牙）设备，读取标准 GATT 电池服务（`0x180F` / `0x2A19`）
- 任务栏右侧悬浮条：显示设备名称与电量，颜色随电量变化
- 系统托盘图标：电池图形 + 最低电量颜色，悬停显示全部设备
- 右键菜单：设备显隐、立即刷新、重新扫描、任务栏显示开关、开机自启、退出
- 每 30 秒自动刷新；只有实时（Uncached）读取成功的设备才显示，读不到的设备不显示，也不回退系统缓存值
- 手动刷新（悬浮条按钮 / 托盘左键 / 菜单「立即刷新」「重新扫描设备」）会顺带做一次全量扫描，
  不受 60 秒的定时全量扫描节流限制；刷新一结束立即更新界面，不用等下一秒的定时器
- **HID 设备识别**：枚举本机 HID 接口，显示 VID/PID、产品名、用途页和报告长度，用于判断设备类型与后续协议适配

## 设备支持情况

| 设备类型 | 电量读取 | 说明 |
|---|---|---|
| DualSense 手柄（经典蓝牙/USB） | ✅ | 解析 HID 输入报告，实测 70% |
| BLE 键盘/鼠标（如 R75Pro） | ✅ | 标准 GATT 电池服务；仅在设备连接时可读到实时值 |
| Xbox 蓝牙手柄 | ✅ | 标准 GATT 电池服务；需处于连接状态 |
| 2.4G USB 接收器（RK2.4G Dongle、2.4G Mouse） | ⚠️ | 已被 HID 识别，但电量走厂商私有协议，尚未实现 |
| 经典蓝牙耳机/鼠标 | ⚠️ | 依赖 HFP 电池上报，部分设备不提供 |

## 2.4G 接收器的实现路径

识别思路是：

1. 用 SetupAPI 枚举 HID 接口，`HidD_GetAttributes` 读取 VID/PID，`HidP_GetCaps` 读取 UsagePage/Usage
2. `UsagePage 0x01/0x02` 为鼠标、`0x01/0x06` 为键盘、`0xFF00+` 为厂商自定义集合
3. 厂商自定义集合通常承载私有协议；下一步需要打开该接口读取 Input Report，观察电量变化时哪个字节在变，再针对性解析

本程序已实现第 1、2 步（菜单「识别 HID 设备」）。第 3 步需要实际设备抓包，且部分白牌接收器固件根本不上报电量，无法软件读取。

## 构建

依赖系统自带的 .NET Framework 4.8 编译器与 Windows SDK 的 `Windows.winmd`，无需安装 .NET SDK。

```bat
build.bat
```

产物：`bin\PeripheralBattery.exe`，程序图标取自 `src\app.ico`（由 `/win32icon` 内嵌，
含 16/20/24/32/40/48/64/128/256 九种尺寸）。

编译命令行里有一条容易被忽略的引用：`Facades\System.Runtime.InteropServices.WindowsRuntime.dll`。
直接对着 `Windows.winmd` 编译时，`ConnectionStatusChanged` 不会被投影成 C# 事件（报 CS1545 / CS0571），
加上这个 facade 引用才能正常用 `+=` 订阅。

## 运行

直接双击 `bin\PeripheralBattery.exe`。程序为单实例，配置和日志保存在：

```
%APPDATA%\PeripheralBattery\settings.ini
%APPDATA%\PeripheralBattery\log.txt
```

首次启动先读 HID 手柄并立即显示（实测 1 秒内出数据），再枚举已配对的 BLE 设备。

**刷新不等待慢设备**：一轮刷新只做「HID 读取 + BLE 枚举 + 派发」，实测 50~270 ms 就结束；
每台 BLE 设备的结果读到后自己回填界面，关机设备等满超时也只影响它自己，不会拖住整轮。

**只碰连着的设备**：每台已配对的 BLE 设备会建一个会话，并订阅系统的 `ConnectionStatusChanged` 事件。
每轮刷新只给 `ConnectionStatus == Connected` 的会话派发读取，没连上的设备一次无线请求都不发。
设备连上时事件立刻触发一次读取，断开时立刻从列表移除，不必等下一轮轮询。

**列表只放读得到电量的设备**：读不到（关机、休眠、无标准电池服务）的设备不显示，
已经显示的设备一旦读不到也会立即移出，不会留一个「未连接」占位。

`log.txt` 超过 1 MB 会自动轮转为 `log.1.txt`（只保留一份备份），长期运行不会无限增长。
刷新耗时超过 3 秒时会在日志里留一条 `刷新耗时 xxx ms`，正常情况下不写。

## 实现说明

- 任务栏悬浮条使用 **WPF 子窗口嵌入**：`SetParent` 到 `Shell_TrayWnd`
- WPF `AllowsTransparency` 提供逐像素透明与圆角；任务栏隐藏/显示、移动随父窗口自动同步，无需轮询
- 资源管理器重启会连带销毁任务栏的所有子窗口（WPF `Window` 随之变为已关闭、无法再 `Show`）。程序用独立的隐藏窗口监听 `TaskbarCreated` 广播，自动重建悬浮条并重新挂载到新任务栏，不会崩溃
- 悬停时背景加深并显示刷新按钮；背景与文字颜色跟随系统深浅色主题
- 电量读取使用 WinRT `BluetoothLEDevice` + GATT。每台设备建一个会话，会话里缓存 `GattDeviceService`
  与三个特征（`0x2A19` 电量、`0x2A1A` 电源状态、`0x2BED` 电量状态），只在首次读取时发现一次，之后每轮直接 `ReadValueAsync`
- 读取前先判 `ConnectionStatus`：设备没连上直接跳过，不发任何无线请求。这是 Xbox 手柄从「一直读不到」
  变成「稳定读到」的关键——之前对已断开的设备反复发起 GATT 读取，每次都要等满超时
- 订阅 `ConnectionStatusChanged`：连上即读、断开即移出列表，状态变化不再依赖轮询周期
- 服务与特征用同步 API 发现（`GetGattService` / `GetCharacteristics`），读的是系统缓存、不发无线往返；
  异步版会真的去问设备，反而更慢
- 电量值本身仍以 `BluetoothCacheMode.Uncached` 读取，**不会把系统缓存值当作实时电量**
- BLE 读取是「派发即返回」：`RefreshAsync` 不 await 各设备，读得慢的设备自己回填并递增版本号，UI 一秒内跟上。
  整台设备共用 8 秒截止时间，避免逐段超时累加到三十多秒
- `GattDeviceService` 在设备断开或会话销毁时 `Dispose`——每个都持有设备句柄，
  只 Dispose `BluetoothLEDevice` 不够，句柄会随每轮刷新累积
- DualSense 通过 SetupAPI 找到 HID 游戏手柄接口，读取 Input Report：低 4 位是电量档位（每档约 10%），高 4 位是充电状态（0 放电 / 1 充电 / 2 充满 / A、B 异常 / F 错误）
- 同一手柄同时插 USB 又连着蓝牙时，Windows 会暴露两个 HID 节点，程序会把它们合并成一个，保留 USB 读数；两个节点都在时只读 USB，读不到才退到蓝牙
- HID 枚举结果会缓存，靠 `WM_DEVICECHANGE` 通知失效，不必每轮把系统里所有 HID 接口都打开一遍
- 设备仅在成功读到实时电量后才显示；读不到的设备直接不显示，不留「未连接」占位
- HID 识别使用 SetupAPI / hid.dll（`HidD_GetAttributes`、`HidP_GetCaps`），对应 WebHID 的 `getDevices()` + `collections`

## 后续计划

- 2.4G 接收器 Input Report 监听与私有协议适配（罗技 HID++ 优先）
- 经典蓝牙 HFP 电量接入
- 多显示器、任务栏位置（顶部/侧边）适配
- 低电量气泡通知
