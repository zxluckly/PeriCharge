# WPF 任务栏嵌入实现说明

本文记录本项目中「把 WPF 透明悬浮条嵌入 Windows 任务栏」的最终方案、关键代码与踩坑经验，并附一份可复用的提示词。

---

## 一、目标

在 Windows 10/11 上实现一个任务栏悬浮条：

- 位于任务栏内部（系统托盘左侧）
- 背景逐像素透明，非悬停时只显示文字/图标
- 鼠标悬停时背景以动画渐入，并显示交互按钮
- 任务栏隐藏（自动隐藏、全屏应用）时自动隐藏
- 不轮询任务栏位置，不产生独立浮动窗口的兼容问题

## 二、最终方案

**WPF `Window` + `AllowsTransparency` + `SetParent` 到 `Shell_TrayWnd`**

参考对象：Lyricify Lite、TaskbarLyrics 等任务栏歌词工具。它们实际使用的是「WPF 透明窗口 + 作为任务栏子窗口」的方式，而不是 WinForms/GDI 或 `HwndSource`。

关键点：

1. WPF 的 `AllowsTransparency = true` 由 DirectComposition 渲染，**即使窗口被 `SetParent` 变成子窗口，仍然支持逐像素透明**。
2. 子窗口的显示/隐藏、移动由父窗口（任务栏）驱动，任务栏隐藏时悬浮条自动消失，无需监听或轮询。
3. 不能用 `HwndSource`（`UsesPerPixelOpacity`）代替 `Window`：`SetParent` 后透明会失效（变黑），且收不到鼠标消息。
4. 不能用 WinForms/GDI 子窗口实现透明：`UpdateLayeredWindow` 对子窗口返回 `ERROR_INVALID_PARAMETER (87)`，`TransparencyKey` 也不生效。

## 三、关键实现步骤

### 1. 创建透明 WPF 窗口

```csharp
Window window = new Window();
window.WindowStyle = WindowStyle.None;
window.AllowsTransparency = true;
window.Background = Brushes.Transparent;
window.ShowInTaskbar = false;
window.ResizeMode = ResizeMode.NoResize;
window.SizeToContent = SizeToContent.WidthAndHeight;
window.ShowActivated = false;
window.Topmost = false;
```

### 2. 显示后取得 HWND 并挂到任务栏

```csharp
IntPtr hwnd = new WindowInteropHelper(window).Handle;
IntPtr taskbar = FindWindow("Shell_TrayWnd", null);

SetParent(hwnd, taskbar);

int style = GetWindowLong(hwnd, GWL_STYLE);
style = (style & ~WS_POPUP) | WS_CHILD;
SetWindowLong(hwnd, GWL_STYLE, style);
```

**注意**：必须在窗口 `Show()` 且句柄创建之后再 `SetParent`；窗口初始化/布局完成后，WPF 可能重置父窗口关系，因此每次定位前都要检查：

```csharp
if (GetParent(hwnd) != taskbar)
{
    SetParent(hwnd, taskbar);
    // 重新设置 WS_CHILD
}
```

### 3. 定位（关键：父客户区坐标）

`SetWindowPos` 对 `WS_CHILD` 窗口使用**父窗口客户区坐标**，不是屏幕坐标。必须先转换：

```csharp
RECT trayRect;
GetWindowRect(taskbar, out trayRect);

IntPtr notify = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
RECT notifyRect;
GetWindowRect(notify, out notifyRect);

int rightEdge = notifyRect.Left - 8;                 // 通知区左侧
int x = rightEdge - width;
int y = trayRect.Top + (trayRect.Bottom - trayRect.Top - height) / 2;

POINT point = new POINT { X = x, Y = y };
ScreenToClient(taskbar, ref point);

SetWindowPos(hwnd, IntPtr.Zero, point.X, point.Y, width, height,
    SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
```

尺寸变化（内容更新、悬停按钮出现）后，在 `DispatcherPriority.Loaded` 中重新计算位置。

### 4. 任务栏重建

Explorer 重启后任务栏窗口会被销毁重建，需要重新附加：

```csharp
uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");

// 在 HwndSource hook 中
if (msg == (int)taskbarCreated)
{
    AttachToTaskbar();
}
```

### 5. 透明背景与悬停

窗口整体透明，内容用 `Border` 绘制圆角背景。非悬停时背景画刷透明度设为 `1/255` 而不是 0：

```csharp
// 完全透明（alpha=0）的区域不参与鼠标命中，
// 保留 1/255 的不可见 alpha，视觉上仍是透明，但整个矩形都能触发悬停。
_backgroundBrush.Opacity = 1.0 / 255.0;
```

悬停过渡使用 `DoubleAnimation` 动画 `Brush.Opacity`：

```csharp
DoubleAnimation animation = new DoubleAnimation(
    hovered ? 1.0 : 1.0 / 255.0,
    TimeSpan.FromMilliseconds(170));
animation.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
backgroundBrush.BeginAnimation(Brush.OpacityProperty, animation);
```

### 6. 鼠标交互

WPF `Window` 作为子窗口时，`MouseEnter` / `MouseLeave` / `MouseLeftButtonUp` 正常工作。按钮可以用 `Border` + 图标，配合：

- 悬停：背景透明度动画
- 按下：`ScaleTransform` 缩放到 0.86
- 松开：回弹并旋转图标 360°

### 7. 主题跟随

读取注册表判断深浅色，并在 `SystemEvents.UserPreferenceChanged` 时重绘：

```
HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize
  SystemUsesLightTheme   (DWORD, 1 = 浅色)
  AppsUseLightTheme      (DWORD, 兜底)
```

### 8. 启动时序

`SetEnabled` / `Show()` 不能早于 WPF 消息循环。在 `AppController` 构造函数里延迟到空闲：

```csharp
Dispatcher.CurrentDispatcher.BeginInvoke(
    DispatcherPriority.ApplicationIdle,
    new Action(() => overlay.SetEnabled(settings.ShowTaskbar)));
```

否则首次启动悬浮条不显示，需要手动取消再勾选。

## 四、踩坑记录

| 现象 | 原因 | 解决 |
|---|---|---|
| 子窗口 `UpdateLayeredWindow` 返回 87 | 分层窗口的逐像素 API 对子窗口不可用 | 改用 WPF `AllowsTransparency` |
| 子窗口 `TransparencyKey` 后完全不可见 | WinForms 颜色键与子窗口样式冲突 | 同上 |
| `HwndSource` 挂到任务栏后背景变黑 | `UsesPerPixelOpacity` 在 `SetParent` 后失效 | 改用 WPF `Window` |
| `HwndSource` 子窗口收不到鼠标 | 输入未随子窗口生效 | 改用 WPF `Window` |
| 悬浮条出现在屏幕顶部 | `SetWindowPos` 用了屏幕坐标 | `ScreenToClient` 转父客户区坐标 |
| 窗口有时失去父窗口关系 | WPF 初始化/布局重置 | 每次定位前检查并重新 `SetParent` |
| 完全透明时无法悬停 | alpha=0 区域鼠标穿透 | 背景保留 1/255 alpha |
| 启动时不显示悬浮条 | `Show()` 早于 WPF 消息循环 | 延迟到 `ApplicationIdle` |
| 菜单勾选初始不显示 | `Checked` 未触发布局 | 用 `CheckState` + `PerformLayout()` |

## 五、可复用提示词

```text
你是一个熟悉 Windows 桌面开发（WPF、Win32 互操作）的工程师。
请用 C# + .NET Framework 4.8 + WPF 实现一个“任务栏嵌入式透明悬浮条”。

## 目标
在 Windows 10/11 任务栏内部（系统托盘左侧）显示一条透明悬浮条：
- 非悬停时背景完全透明，只显示内容（图标、文字、胶囊）
- 鼠标悬停时背景以 150–200ms 缓动动画渐入，并显示交互按钮
- 任务栏自动隐藏、全屏应用时，悬浮条随任务栏自动隐藏
- 不使用定时器轮询任务栏位置

## 硬性技术约束
1. 必须使用 WPF Window：
   - WindowStyle=None、AllowsTransparency=True、Background=Transparent
   - ShowInTaskbar=False、ResizeMode=NoResize、SizeToContent=WidthAndHeight
   - ShowActivated=False、Topmost=False
2. 窗口 Show() 后取得 HWND：
   - SetParent(hwnd, FindWindow("Shell_TrayWnd", null))
   - 修改 GWL_STYLE：去掉 WS_POPUP，加上 WS_CHILD
3. 定位必须用父窗口客户区坐标：
   - 从 FindWindowEx(Shell_TrayWnd, "TrayNotifyWnd") 取通知区位置
   - 用 ScreenToClient(taskbar, ...) 转换后再 SetWindowPos
4. 不要用 HwndSource 或 WinForms 窗口做透明子窗口：
   - HwndSource 的 UsesPerPixelOpacity 在 SetParent 后透明失效
   - WinForms 子窗口的 UpdateLayeredWindow 会返回错误 87
5. 透明背景的 alpha 不能为 0：
   - 非悬停状态保留 1/255 的不可见 alpha，保证鼠标命中
   - 悬停动画在 1/255 与 1.0 之间过渡
6. 窗口内容变化、悬停展开按钮后，需要重新测量并定位
   （DispatcherPriority.Loaded 中调用定位方法）
7. 注册 TaskbarCreated 消息（RegisterWindowMessage("TaskbarCreated")），
   Explorer 重启后重新 SetParent 并定位
8. WPF 初始化后可能重置父窗口，每次定位前检查 GetParent(hwnd)，
   不等于任务栏时重新 SetParent 并设置 WS_CHILD
9. 初始显示状态延迟到 DispatcherPriority.ApplicationIdle 再应用，
   避免 WPF 消息循环启动前 Show() 不完整
10. 主题跟随注册表：
    HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize
    SystemUsesLightTheme / AppsUseLightTheme
    在 SystemEvents.UserPreferenceChanged 时更新颜色

## 交互
- 内容区鼠标悬停：背景淡入，显示刷新按钮
- 刷新按钮：悬停高亮、按下缩放、松开旋转一周
- 子窗口会随任务栏隐藏/显示，不要自己轮询或判断

## 验收
1. 启动后悬浮条位于任务栏内，背景透明，非悬停看不到卡片背景
2. 悬停时背景平滑淡入，移开平滑淡出
3. 任务栏自动隐藏（或全屏应用）时悬浮条随之消失
4. 退出全屏后悬浮条自动恢复
5. Explorer 重启后悬浮条自动重新挂到任务栏
6. 重启程序后无需手动操作，悬浮条按上次配置显示
```
