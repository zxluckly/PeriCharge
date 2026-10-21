# 外设电量监控

Windows 托盘 + 任务栏悬浮条，实时显示蓝牙外设电量。

## 当前 MVP 能力

- 枚举已配对的 BLE（低功耗蓝牙）设备，读取标准 GATT 电池服务（`0x180F` / `0x2A19`）
- 任务栏右侧悬浮条：显示设备名称与电量，颜色随电量变化
- 系统托盘图标：电池图形 + 最低电量颜色，悬停显示全部设备
- 右键菜单：设备显隐、立即刷新、重新扫描、任务栏显示开关、开机自启、退出
- 每 30 秒自动刷新；只有实时（Uncached）读取成功的设备才显示电量，未连接设备显示「未连接」，不回退系统缓存值
- **HID 设备识别**：枚举本机 HID 接口，显示 VID/PID、产品名、用途页和报告长度，用于判断设备类型与后续协议适配

## 设备支持情况

| 设备类型 | 电量读取 | 说明 |
|---|---|---|
| DualSense 手柄（经典蓝牙/USB） | ✅ | 解析 HID 输入报告，实测 70% |
| BLE 键盘/鼠标（如 R75Pro） | ✅ | 标准 GATT 电池服务；仅在设备连接时可读到实时值 |
| Xbox 蓝牙手柄 | ✅ | 标准 GATT 电池服务；需处于连接状态 |
| 2.4G USB 接收器（RK2.4G Dongle、2.4G Mouse） | ⚠️ | 已被 HID 识别，但电量走厂商私有协议，尚未实现 |
| 经典蓝牙耳机/鼠标 | ⚠️ | 依赖 HFP 电池上报，部分设备不提供 |
| 手机、平板 | — | 无电池服务，自动忽略 |

## 2.4G 接收器的实现路径

参考 `参考网页/` 中的 WebHID 设备查看器，识别思路是：

1. 用 SetupAPI 枚举 HID 接口，`HidD_GetAttributes` 读取 VID/PID，`HidP_GetCaps` 读取 UsagePage/Usage
2. `UsagePage 0x01/0x02` 为鼠标、`0x01/0x06` 为键盘、`0xFF00+` 为厂商自定义集合
3. 厂商自定义集合通常承载私有协议；下一步需要打开该接口读取 Input Report，观察电量变化时哪个字节在变，再针对性解析

本程序已实现第 1、2 步（菜单「识别 HID 设备」）。第 3 步需要实际设备抓包，且部分白牌接收器固件根本不上报电量，无法软件读取。

## 构建

依赖系统自带的 .NET Framework 4.8 编译器与 Windows SDK 的 `Windows.winmd`，无需安装 .NET SDK。

```bat
build.bat
```

产物：`bin\PeripheralBattery.exe`

## 运行

直接双击 `bin\PeripheralBattery.exe`。程序为单实例，配置和日志保存在：

```
%APPDATA%\PeripheralBattery\settings.ini
%APPDATA%\PeripheralBattery\log.txt
```

首次启动会串行扫描全部已配对 BLE 设备（约 30–50 秒），之后只刷新成功读到电量的设备。

## 实现说明

- 任务栏悬浮条使用 **WPF 子窗口嵌入**：`SetParent` 到 `Shell_TrayWnd`，与 Lyricify 相同的做法
- WPF `AllowsTransparency` 提供逐像素透明与圆角；任务栏隐藏/显示、移动随父窗口自动同步，无需轮询
- 悬停时背景加深并显示刷新按钮；背景与文字颜色跟随系统深浅色主题
- 电量读取使用 WinRT `BluetoothLEDevice` + GATT，先缓存模式发现服务、再通过 Uncached 读取实时值，带超时等待；**不会把系统缓存值当作实时电量**
- DualSense 通过 SetupAPI 找到 HID 游戏手柄接口，读取 Input Report，按 `status & 0x0F` 解析电量档位（每档 10%）
- 设备仅在成功读到实时电量后才显示数字；未连接设备显示「未连接」
- HID 识别使用 SetupAPI / hid.dll（`HidD_GetAttributes`、`HidP_GetCaps`），对应 WebHID 的 `getDevices()` + `collections`

## 后续计划

- 2.4G 接收器 Input Report 监听与私有协议适配（罗技 HID++ 优先）
- 经典蓝牙 HFP 电量接入
- 多显示器、任务栏位置（顶部/侧边）适配
- 低电量气泡通知
