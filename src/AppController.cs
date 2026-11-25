using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace PeripheralBattery
{
    internal sealed class AppController
    {
        private readonly System.Windows.Application _app;
        private readonly Settings _settings;
        private readonly BatteryService _service;
        private readonly Forms.NotifyIcon _tray;

        // 上一次画出来的托盘图标状态，用来跳过重复绘制
        private int _trayPercent = -1;
        private bool _trayOnline;
        private bool _trayIconValid;
        private readonly Forms.ContextMenuStrip _menu;
        private readonly DispatcherTimer _timer;
        private readonly BroadcastWatcher _watcher;
        private WpfOverlay _overlay;

        private DateTime _lastRefresh = DateTime.MinValue;
        private DateTime _lastDeviceChange = DateTime.MinValue;
        private int _lastVersion = -1;
        private volatile bool _refreshing;
        private bool _exiting;
        private string _lastHidSignature = string.Empty;

        // 菜单是复用的，字体只建一次。每次右键都 new Font 又不 Dispose，
        // 常驻进程的 GDI 句柄会随右键次数一直涨
        private static readonly Font MenuFont = new Font("Segoe UI", 9f);
        private static readonly Font MenuTitleFont = new Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold);

        /// <summary>最近一次扫到的显示器（含 DDC 不可控的，菜单里要据此判断）。</summary>
        private MonitorBrightness[] _monitors = new MonitorBrightness[0];

        private DateTime _lastBrightnessScan = DateTime.MinValue;
        private volatile bool _brightnessScanning;
        private string _lastBrightnessSignature = string.Empty;

        /// <summary>工作集修剪间隔。两分钟一次，够把冷页换出去，又不至于频繁抖动。</summary>
        private const int TrimIntervalMs = 120000;

        private readonly System.Threading.Timer _trimTimer;

        internal AppController(System.Windows.Application app)
        {
            _app = app;
            _settings = Settings.Load();
            _service = new BatteryService();

            _overlay = CreateOverlay();
            _menu = BuildMenu();

            // 悬浮条窗口是 Shell_TrayWnd 的子窗口，资源管理器重启时会被一并销毁；
            // 用一个独立的隐藏窗口接收 TaskbarCreated，重建悬浮条。
            // 顺带接 WM_DEVICECHANGE：插拔外设时立刻重新枚举，而不是等下一轮全量扫描。
            _watcher = new BroadcastWatcher();
            _watcher.TaskbarCreated += delegate { OnTaskbarRecreated(); };
            _watcher.DevicesChanged += delegate { OnDevicesChanged(); };

            _tray = new Forms.NotifyIcon();
            _tray.Text = "外设电量监控";
            _tray.Icon = IconFactory.Create(0, false);
            _trayPercent = 0;
            _trayOnline = false;
            _trayIconValid = true;
            _tray.Visible = true;
            _tray.ContextMenuStrip = _menu;
            _tray.MouseClick += OnTrayClick;

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += OnTick;
            _timer.Start();

            // 空闲时把工作集里冷掉的页换出去：常驻内存能从 90 多 MB 掉到 30 MB 以内。
            // 提交量不变，只是不占着物理内存；下次用到的页系统会自己换回来
            _trimTimer = new System.Threading.Timer(
                delegate { TrimWorkingSet(); }, null, TrimIntervalMs, TrimIntervalMs);

            Log.Write("程序启动");
            StartRefresh();
            StartBrightnessScan();

            // WPF 消息循环尚未启动时窗口 Show 不完整，延迟到应用空闲再应用初始显示状态
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle, new Action(delegate
            {
                _overlay.SetEnabled(_settings.ShowTaskbar);
            }));
        }

        private WpfOverlay CreateOverlay()
        {
            WpfOverlay overlay = new WpfOverlay();
            overlay.RefreshRequested += delegate { StartRefresh(true); };
            overlay.SetHoverStyle(_settings.HoverColorArgb, _settings.HoverAlpha);
            overlay.SetCompactOptions(_settings.ForceCompact, _settings.CompactThreshold, _settings.CompactStyle);
            overlay.SetBrightnessStep(_settings.BrightnessStep);
            return overlay;
        }

        private void ApplyCompactSettings()
        {
            _overlay.SetCompactOptions(_settings.ForceCompact, _settings.CompactThreshold, _settings.CompactStyle);
        }

        private void OnTaskbarRecreated()
        {
            // explorer 重启后旧任务栏已销毁。若悬浮条窗口也被级联销毁，则重建一个新的；
            // 若窗口仍在，只需重新挂到新任务栏上。
            if (!_overlay.IsDestroyed)
            {
                _overlay.RefreshPosition();
                return;
            }

            _overlay = CreateOverlay();
            _overlay.SetEnabled(_settings.ShowTaskbar);
            UpdateUi();
            ApplyBrightness();
            Log.Write("资源管理器重启，已重建任务栏悬浮条");
        }

        /// <summary>
        /// 扫一遍显示器亮度。一次 DDC 往返几十毫秒，必须后台跑；
        /// 30 秒一轮就够——亮度主要靠滚轮改，这里只负责发现插拔和外部改动。
        /// </summary>
        private void StartBrightnessScan()
        {
            if (_brightnessScanning || _exiting)
            {
                return;
            }

            _brightnessScanning = true;
            _lastBrightnessScan = DateTime.Now;

            Task.Run(delegate
            {
                List<MonitorBrightness> scanned = BrightnessService.Scan();
                _app.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    _brightnessScanning = false;
                    if (_exiting)
                    {
                        return;
                    }

                    _monitors = scanned.ToArray();
                    ApplyBrightness();
                }));
            });
        }

        /// <summary>
        /// 把「该显示的显示器」推给悬浮条。读不到亮度的那几台（OSD 里没开 DDC/CI、
        /// 或者是走不了 DDC 的内置屏）不占任务栏位置，但菜单里仍然列出来并标灰。
        /// </summary>
        private void ApplyBrightness()
        {
            List<MonitorBrightness> visible = new List<MonitorBrightness>();
            List<string> summary = new List<string>();

            foreach (MonitorBrightness monitor in _monitors)
            {
                summary.Add(monitor.Name + "(" + monitor.Id + ")"
                    + (monitor.Controllable ? "=" + monitor.Percent + "%" : "=不可控"));

                if (!monitor.Controllable)
                {
                    continue;
                }

                if (_settings.HiddenBrightnessIds.Contains(monitor.Id))
                {
                    continue;
                }

                visible.Add(monitor);
            }

            // 只在扫描结果变化时记一条，方便排查「为什么任务栏上没有某台屏」
            string signature = string.Join("；", summary.ToArray());
            if (signature != _lastBrightnessSignature)
            {
                _lastBrightnessSignature = signature;
                Log.Write("显示器亮度：" + (signature.Length == 0 ? "（未发现显示器）" : signature));
            }

            _overlay.UpdateBrightness(visible.ToArray());
        }

        /// <summary>设备树变化（插拔、休眠唤醒）时重新枚举并刷新一次。</summary>
        private void OnDevicesChanged()
        {
            // 一个 USB 复合设备插入会连着发好几条 DBT_DEVNODES_CHANGED，
            // 简单去抖，免得一口气触发多次扫描
            if ((DateTime.Now - _lastDeviceChange).TotalSeconds < 3)
            {
                return;
            }

            _lastDeviceChange = DateTime.Now;
            StartRefresh(true);
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (_exiting)
            {
                return;
            }

            if (!_refreshing && (DateTime.Now - _lastRefresh).TotalSeconds >= _settings.RefreshSeconds)
            {
                StartRefresh();
            }

            if (!_brightnessScanning && (DateTime.Now - _lastBrightnessScan).TotalSeconds >= 30)
            {
                StartBrightnessScan();
            }

            if (_service.Version != _lastVersion)
            {
                _lastVersion = _service.Version;
                UpdateUi();
            }
        }

        /// <summary>定时刷新：只刷新当前列表里的设备，不做全量扫描。</summary>
        private void StartRefresh()
        {
            StartRefresh(false);
        }

        /// <summary>
        /// 触发一次刷新。
        /// <paramref name="userInitiated"/> 为 true 时（点悬浮条按钮、菜单「立即刷新」/「重新扫描设备」）
        /// 顺带做一次全量扫描——用户点了刷新就是希望重新找一遍设备，
        /// 不该被 60 秒的定时全量扫描节流挡住。
        /// </summary>
        private void StartRefresh(bool userInitiated)
        {
            if (_refreshing)
            {
                return;
            }

            if (userInitiated)
            {
                _service.RequestFullScan();
            }

            _refreshing = true;
            _lastRefresh = DateTime.Now;
            _overlay.SetScanning(true);

            Task task = _service.RefreshAsync();
            task.ContinueWith(delegate(Task completed)
            {
                _refreshing = false;
                if (completed.IsFaulted && completed.Exception != null)
                {
                    Log.Error("刷新电量失败", completed.Exception.GetBaseException());
                }

                // 刷新一结束就立刻反映一次，不用等下一秒的定时器
                // （UpdateUi 会碰 WPF 和托盘，必须回到 UI 线程）
                if (_exiting)
                {
                    return;
                }

                _app.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    _lastVersion = _service.Version;
                    UpdateUi();
                }));
            });
        }

        private void UpdateUi()
        {
            DeviceBattery[] all = _service.Snapshot();
            bool hasGenuine = HasGenuineDualSense(all);

            List<DeviceBattery> online = new List<DeviceBattery>();
            foreach (DeviceBattery item in all)
            {
                // 只显示当前真正读到电量的设备，避免离线的历史配对设备常驻
                if (!item.Online || _settings.HiddenIds.Contains(item.Id))
                {
                    continue;
                }

                // 仿冒接收器只在「真手柄也在」的时候让位：只剩它一个时照样显示，
                // 否则用户会看到任务栏上一条设备都没有
                if (item.Receiver && hasGenuine)
                {
                    continue;
                }

                online.Add(item);
            }

            DeviceBattery[] shown = online.ToArray();
            _overlay.UpdateItems(shown);
            UpdateTrayIcon(shown);
        }

        /// <summary>列表里有没有原厂 DualSense（仿冒接收器不算）。</summary>
        private static bool HasGenuineDualSense(DeviceBattery[] items)
        {
            if (items == null)
            {
                return false;
            }

            foreach (DeviceBattery item in items)
            {
                if (item.Online && !item.Receiver
                    && item.Name != null
                    && item.Name.IndexOf("DualSense", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private void UpdateTrayIcon(DeviceBattery[] items)
        {
            int minimum = -1;
            bool anyOnline = false;
            foreach (DeviceBattery item in items)
            {
                if (!item.Online)
                {
                    continue;
                }

                anyOnline = true;
                if (item.Percent >= 0 && (minimum < 0 || item.Percent < minimum))
                {
                    minimum = item.Percent;
                }
            }

            if (minimum < 0)
            {
                minimum = 0;
            }

            // 图标只在电量或在线状态真的变了才重画：每 30 秒 new 一个 Bitmap + Graphics
            // 再 GetHicon，纯属给 GDI 和 GC 添活，画出来的东西一模一样
            if (!_trayIconValid || minimum != _trayPercent || anyOnline != _trayOnline)
            {
                Icon previous = _tray.Icon;
                _tray.Icon = IconFactory.Create(minimum, anyOnline);
                if (previous != null)
                {
                    previous.Dispose();
                }

                _trayPercent = minimum;
                _trayOnline = anyOnline;
                _trayIconValid = true;
            }

            List<string> parts = new List<string>();
            foreach (DeviceBattery item in items)
            {
                string state = item.Online ? item.Percent + "%" : "未连接";
                if (item.Online && item.Charging)
                {
                    state += "(充电)";
                }
                else if (item.Online && item.Full)
                {
                    state += "(满)";
                }

                parts.Add(UiTheme.GetDisplayName(item) + " " + state);
            }

            string tooltip = parts.Count == 0
                ? "外设电量：未发现设备"
                : "外设电量：" + string.Join("，", parts.ToArray());
            if (tooltip.Length > 63)
            {
                tooltip = tooltip.Substring(0, 63);
            }

            _tray.Text = tooltip;
        }

        private void OnTrayClick(object sender, Forms.MouseEventArgs e)
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                StartRefresh(true);
            }
        }

        private Forms.ContextMenuStrip BuildMenu()
        {
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
            menu.Renderer = new DarkMenuRenderer();
            menu.Font = MenuFont;
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            menu.DropShadowEnabled = false;
            menu.Opening += delegate { PopulateMenu(menu); };
            menu.Opened += delegate { ApplyMenuRegion(menu); };
            menu.SizeChanged += delegate
            {
                if (menu.Visible)
                {
                    ApplyMenuRegion(menu);
                }
            };
            return menu;
        }

        private static void ApplyMenuRegion(Forms.ContextMenuStrip menu)
        {
            if (menu.Width <= 0 || menu.Height <= 0)
            {
                return;
            }

            using (System.Drawing.Drawing2D.GraphicsPath path = UiTheme.RoundedRect(
                new Rectangle(0, 0, menu.Width, menu.Height), 9))
            {
                Region previous = menu.Region;
                menu.Region = new Region(path);
                if (previous != null)
                {
                    previous.Dispose();
                }
            }
        }

        private void PopulateMenu(Forms.ContextMenuStrip menu)
        {
            menu.Renderer = new DarkMenuRenderer();
            menu.BackColor = UiTheme.MenuBackground;
            menu.ForeColor = UiTheme.TextPrimary;
            menu.Font = MenuFont;
            menu.ShowCheckMargin = true;
            menu.ShowImageMargin = false;
            menu.Padding = new Forms.Padding(4);

            // Items.Clear() 只是从集合里摘掉，ToolStripItem 自带的 GDI 资源不跟着释放，
            // 右键几百次之后句柄会慢慢涨。逐个 Dispose。
            while (menu.Items.Count > 0)
            {
                Forms.ToolStripItem old = menu.Items[0];
                menu.Items.RemoveAt(0);
                old.Dispose();
            }

            Forms.ToolStripMenuItem title = new Forms.ToolStripMenuItem("外设电量");
            title.Enabled = false;
            title.Font = MenuTitleFont;
            title.ForeColor = UiTheme.TextSecondary;
            menu.Items.Add(title);

            DeviceBattery[] all = _service.Snapshot();
            bool hasGenuine = HasGenuineDualSense(all);
            if (all.Length == 0)
            {
                Forms.ToolStripMenuItem empty = new Forms.ToolStripMenuItem("（正在扫描或没有可读设备）");
                empty.Enabled = false;
                menu.Items.Add(empty);
            }

            foreach (DeviceBattery snapshot in all)
            {
                DeviceBattery item = snapshot;

                // 真手柄也在时，接收器是自动让位的：菜单里如实反映，别让勾选状态骗人
                bool suppressed = item.Receiver && hasGenuine;

                string text = UiTheme.GetDisplayName(item);
                if (suppressed)
                {
                    text += "（真手柄在时不显示）";
                }

                Forms.ToolStripMenuItem deviceItem = new Forms.ToolStripMenuItem(text);
                deviceItem.Tag = item;
                deviceItem.Enabled = !suppressed;
                deviceItem.CheckState = suppressed || _settings.HiddenIds.Contains(item.Id)
                    ? Forms.CheckState.Unchecked
                    : Forms.CheckState.Checked;
                deviceItem.Click += delegate { ToggleDevice(item.Id); };
                menu.Items.Add(deviceItem);
            }

            menu.Items.Add(new Forms.ToolStripSeparator());

            Forms.ToolStripMenuItem refreshItem = new Forms.ToolStripMenuItem("立即刷新");
            refreshItem.Click += delegate { StartRefresh(true); };
            menu.Items.Add(refreshItem);

            Forms.ToolStripMenuItem rescanItem = new Forms.ToolStripMenuItem("重新扫描设备");
            rescanItem.Click += delegate { StartRefresh(true); };
            menu.Items.Add(rescanItem);

            Forms.ToolStripMenuItem hidMenu = new Forms.ToolStripMenuItem("识别 HID 设备（VID/PID）");
            BuildHidMenu(hidMenu);
            menu.Items.Add(hidMenu);

            menu.Items.Add(new Forms.ToolStripSeparator());

            Forms.ToolStripMenuItem taskbarItem = new Forms.ToolStripMenuItem("在任务栏显示电量");
            taskbarItem.CheckState = _settings.ShowTaskbar
                ? Forms.CheckState.Checked
                : Forms.CheckState.Unchecked;
            taskbarItem.Click += delegate
            {
                _settings.ShowTaskbar = !_settings.ShowTaskbar;
                _settings.Save();
                _overlay.SetEnabled(_settings.ShowTaskbar);
            };
            menu.Items.Add(taskbarItem);

            menu.Items.Add(BuildCompactMenu());
            menu.Items.Add(BuildBrightnessMenu());

            Forms.ToolStripMenuItem hoverMenu = new Forms.ToolStripMenuItem("悬停背景");

            Forms.ToolStripMenuItem hoverColorItem = new Forms.ToolStripMenuItem("选择颜色…");
            hoverColorItem.Click += delegate { PickHoverColor(); };
            hoverMenu.DropDownItems.Add(hoverColorItem);

            Forms.ToolStripMenuItem alphaMenu = new Forms.ToolStripMenuItem("不透明度");
            int[] percents = new int[] { 20, 40, 60, 80, 100 };
            foreach (int percent in percents)
            {
                int alpha = percent * 255 / 100;
                int capturedAlpha = alpha;
                int capturedPercent = percent;
                Forms.ToolStripMenuItem alphaItem = new Forms.ToolStripMenuItem(capturedPercent + "%");
                alphaItem.CheckState = _settings.HoverAlpha == capturedAlpha
                    ? Forms.CheckState.Checked
                    : Forms.CheckState.Unchecked;
                alphaItem.Click += delegate
                {
                    _settings.HoverAlpha = capturedAlpha;
                    _settings.Save();
                    _overlay.SetHoverStyle(_settings.HoverColorArgb, _settings.HoverAlpha);
                };
                alphaMenu.DropDownItems.Add(alphaItem);
            }

            hoverMenu.DropDownItems.Add(alphaMenu);
            hoverMenu.DropDownItems.Add(new Forms.ToolStripSeparator());

            Forms.ToolStripMenuItem hoverResetItem = new Forms.ToolStripMenuItem("恢复默认（跟随系统颜色）");
            hoverResetItem.Click += delegate
            {
                _settings.HoverColorArgb = 0;
                _settings.HoverAlpha = Settings.DefaultHoverAlpha;
                _settings.Save();
                _overlay.SetHoverStyle(0, Settings.DefaultHoverAlpha);
            };
            hoverMenu.DropDownItems.Add(hoverResetItem);

            menu.Items.Add(hoverMenu);

            Forms.ToolStripMenuItem autoStartItem = new Forms.ToolStripMenuItem("开机自动启动");
            autoStartItem.CheckState = AutoStart.IsEnabled()
                ? Forms.CheckState.Checked
                : Forms.CheckState.Unchecked;
            autoStartItem.Click += delegate { AutoStart.SetEnabled(!AutoStart.IsEnabled()); };
            menu.Items.Add(autoStartItem);

            menu.Items.Add(new Forms.ToolStripSeparator());

            Forms.ToolStripMenuItem exitItem = new Forms.ToolStripMenuItem("退出");
            exitItem.Click += delegate { Exit(); };
            menu.Items.Add(exitItem);

            menu.PerformLayout();
        }

        /// <summary>
        /// 「显示压缩」子菜单：是否强制、超过几台开始压缩、压缩后显示成什么样。
        /// 设备一多，悬浮条按「图标 + 名称 + 胶囊」排下来能到 800 多像素，任务栏右边放不下。
        /// </summary>
        /// <summary>
        /// 「显示屏幕亮度」：每台显示器一个勾选项，各自决定要不要出现在任务栏上；
        /// 下面再挂一个滚轮步长。读不到亮度的屏（OSD 里没开 DDC/CI）列出来但标灰。
        /// </summary>
        private Forms.ToolStripMenuItem BuildBrightnessMenu()
        {
            Forms.ToolStripMenuItem brightnessMenu = new Forms.ToolStripMenuItem("显示屏幕亮度");

            if (_monitors.Length == 0)
            {
                Forms.ToolStripMenuItem none = new Forms.ToolStripMenuItem("（未检测到显示器）");
                none.Enabled = false;
                brightnessMenu.DropDownItems.Add(none);

                // 首轮扫描还没回来时，顺手再触发一次，下次右键就有内容了
                StartBrightnessScan();
                return brightnessMenu;
            }

            foreach (MonitorBrightness monitor in _monitors)
            {
                MonitorBrightness captured = monitor;
                string text = captured.Name;
                if (!captured.Controllable)
                {
                    text += "（不支持 DDC/CI）";
                }

                Forms.ToolStripMenuItem item = new Forms.ToolStripMenuItem(text);
                item.Enabled = captured.Controllable;
                item.CheckState = captured.Controllable
                    && !_settings.HiddenBrightnessIds.Contains(captured.Id)
                    ? Forms.CheckState.Checked
                    : Forms.CheckState.Unchecked;
                item.Click += delegate { ToggleBrightness(captured); };
                brightnessMenu.DropDownItems.Add(item);
            }

            brightnessMenu.DropDownItems.Add(new Forms.ToolStripSeparator());

            Forms.ToolStripMenuItem stepMenu = new Forms.ToolStripMenuItem("滚轮步长");
            int[] steps = new int[] { 1, 2, 5, 10 };
            for (int i = 0; i < steps.Length; i++)
            {
                int capturedStep = steps[i];
                Forms.ToolStripMenuItem item = new Forms.ToolStripMenuItem(capturedStep + "%");
                item.CheckState = _settings.BrightnessStep == capturedStep
                    ? Forms.CheckState.Checked
                    : Forms.CheckState.Unchecked;
                item.Click += delegate
                {
                    _settings.BrightnessStep = capturedStep;
                    _settings.Save();
                    _overlay.SetBrightnessStep(capturedStep);
                };
                stepMenu.DropDownItems.Add(item);
            }
            brightnessMenu.DropDownItems.Add(stepMenu);

            return brightnessMenu;
        }

        private void ToggleBrightness(MonitorBrightness monitor)
        {
            if (monitor == null || !monitor.Controllable)
            {
                return;
            }

            if (_settings.HiddenBrightnessIds.Contains(monitor.Id))
            {
                _settings.HiddenBrightnessIds.Remove(monitor.Id);
            }
            else
            {
                _settings.HiddenBrightnessIds.Add(monitor.Id);
            }

            _settings.Save();
            ApplyBrightness();
        }

        private Forms.ToolStripMenuItem BuildCompactMenu()
        {
            Forms.ToolStripMenuItem compactMenu = new Forms.ToolStripMenuItem("显示压缩");

            Forms.ToolStripMenuItem forceItem = new Forms.ToolStripMenuItem("始终使用紧凑样式");
            forceItem.CheckState = _settings.ForceCompact
                ? Forms.CheckState.Checked
                : Forms.CheckState.Unchecked;
            forceItem.Click += delegate
            {
                _settings.ForceCompact = !_settings.ForceCompact;
                _settings.Save();
                ApplyCompactSettings();
            };
            compactMenu.DropDownItems.Add(forceItem);

            compactMenu.DropDownItems.Add(new Forms.ToolStripSeparator());

            Forms.ToolStripMenuItem thresholdMenu = new Forms.ToolStripMenuItem("设备超过 N 台时压缩");
            // 勾了「始终」就不看数量，这项灰掉，免得以为设了没生效
            thresholdMenu.Enabled = !_settings.ForceCompact;
            for (int count = Settings.MinCompactThreshold; count <= Settings.MaxCompactThreshold; count++)
            {
                int captured = count;
                Forms.ToolStripMenuItem item = new Forms.ToolStripMenuItem(captured + " 台");
                item.CheckState = _settings.CompactThreshold == captured
                    ? Forms.CheckState.Checked
                    : Forms.CheckState.Unchecked;
                item.Click += delegate
                {
                    _settings.CompactThreshold = captured;
                    _settings.Save();
                    ApplyCompactSettings();
                };
                thresholdMenu.DropDownItems.Add(item);
            }

            compactMenu.DropDownItems.Add(thresholdMenu);
            compactMenu.DropDownItems.Add(new Forms.ToolStripSeparator());

            Forms.ToolStripMenuItem styleMenu = new Forms.ToolStripMenuItem("紧凑样式");
            styleMenu.DropDownItems.Add(BuildCompactStyleItem("图标 + 电量胶囊", CompactStyle.IconAndBattery));
            styleMenu.DropDownItems.Add(BuildCompactStyleItem("仅图标", CompactStyle.IconOnly));
            compactMenu.DropDownItems.Add(styleMenu);

            return compactMenu;
        }

        private Forms.ToolStripMenuItem BuildCompactStyleItem(string text, CompactStyle style)
        {
            Forms.ToolStripMenuItem item = new Forms.ToolStripMenuItem(text);
            item.CheckState = _settings.CompactStyle == style
                ? Forms.CheckState.Checked
                : Forms.CheckState.Unchecked;
            item.Click += delegate
            {
                _settings.CompactStyle = style;
                _settings.Save();
                ApplyCompactSettings();
            };
            return item;
        }

        private void BuildHidMenu(Forms.ToolStripMenuItem hidMenu)
        {
            try
            {
                List<HidDeviceInfo> rawDevices = HidEnumerator.Enumerate();
                Dictionary<string, HidDeviceInfo> uniqueDevices = new Dictionary<string, HidDeviceInfo>();
                Dictionary<string, string> usageKinds = new Dictionary<string, string>();

                foreach (HidDeviceInfo device in rawDevices)
                {
                    string key = device.VendorId.ToString("X4") + ":" + device.ProductId.ToString("X4")
                        + ":" + device.ProductName;
                    string kind = HidEnumerator.GetUsageDescription(device.UsagePage, device.Usage);
                    if (!uniqueDevices.ContainsKey(key))
                    {
                        uniqueDevices[key] = device;
                        usageKinds[key] = kind;
                    }
                    else if (!usageKinds[key].Contains(kind))
                    {
                        usageKinds[key] = usageKinds[key] + "/" + kind;
                    }
                }

                if (uniqueDevices.Count == 0)
                {
                    Forms.ToolStripMenuItem none = new Forms.ToolStripMenuItem("（未发现 HID 设备）");
                    none.Enabled = false;
                    hidMenu.DropDownItems.Add(none);
                }

                List<string> logLines = new List<string>();
                foreach (KeyValuePair<string, HidDeviceInfo> pair in uniqueDevices)
                {
                    HidDeviceInfo device = pair.Value;
                    string name = string.IsNullOrEmpty(device.ProductName) ? "(未命名)" : device.ProductName;
                    string text = "VID_" + device.VendorId.ToString("X4")
                        + " PID_" + device.ProductId.ToString("X4")
                        + "  " + name
                        + "  [" + usageKinds[pair.Key] + "]";

                    Forms.ToolStripMenuItem hidItem = new Forms.ToolStripMenuItem(text);
                    hidItem.Enabled = false;
                    hidItem.ToolTipText = "输入报告 " + device.InputReportLength
                        + " 字节，输出报告 " + device.OutputReportLength
                        + " 字节，特性报告 " + device.FeatureReportLength + " 字节";
                    hidMenu.DropDownItems.Add(hidItem);
                    logLines.Add(text);
                }

                string signature = string.Join("；", logLines.ToArray());
                if (signature != _lastHidSignature)
                {
                    _lastHidSignature = signature;
                    Log.Write("HID 设备 " + uniqueDevices.Count + " 个：" + signature);
                }
            }
            catch (Exception ex)
            {
                Log.Error("枚举 HID 设备失败", ex);
            }
        }

        private void PickHoverColor()
        {
            using (Forms.ColorDialog dialog = new Forms.ColorDialog())
            {
                dialog.FullOpen = true;
                dialog.AnyColor = true;
                if (_settings.HoverColorArgb != 0)
                {
                    dialog.Color = Color.FromArgb(
                        (_settings.HoverColorArgb >> 16) & 0xFF,
                        (_settings.HoverColorArgb >> 8) & 0xFF,
                        _settings.HoverColorArgb & 0xFF);
                }
                else
                {
                    dialog.Color = UiTheme.BackgroundTop;
                }

                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    _settings.HoverColorArgb = (dialog.Color.R << 16)
                        | (dialog.Color.G << 8)
                        | dialog.Color.B;
                    _settings.Save();
                    _overlay.SetHoverStyle(_settings.HoverColorArgb, _settings.HoverAlpha);
                }
            }
        }

        private void ToggleDevice(string id)
        {
            if (_settings.HiddenIds.Contains(id))
            {
                _settings.HiddenIds.Remove(id);
            }
            else
            {
                _settings.HiddenIds.Add(id);
            }

            _settings.Save();
            UpdateUi();
        }

        /// <summary>
        /// 把工作集里不常访问的页交给系统换出。提交内存（Private Bytes）不变，
        /// 变的只是物理内存占用——任务管理器里看到的就是这个数。
        /// </summary>
        private void TrimWorkingSet()
        {
            if (_exiting)
            {
                return;
            }

            try
            {
                using (System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess())
                {
                    NativeMethods.SetProcessWorkingSetSize(self.Handle, new IntPtr(-1), new IntPtr(-1));
                }
            }
            catch
            {
                // 修剪失败无所谓，下次再来
            }
        }

        private void Exit()
        {
            if (_exiting)
            {
                return;
            }

            _exiting = true;
            _timer.Stop();
            if (_trimTimer != null)
            {
                _trimTimer.Dispose();
            }
            _tray.Visible = false;
            _tray.Dispose();
            _watcher.DestroyHandle();
            if (!_overlay.IsDestroyed)
            {
                _overlay.Close();
            }

            Log.Write("程序退出");
            _app.Shutdown();
        }
    }

    /// <summary>
    /// 隐藏的顶层窗口，用来接收系统广播消息：资源管理器的 TaskbarCreated，
    /// 以及设备增删的 WM_DEVICECHANGE。不与任务栏建立父子关系，因此不会被级联销毁。
    /// </summary>
    internal sealed class BroadcastWatcher : Forms.NativeWindow
    {
        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVNODES_CHANGED = 0x0007;

        private readonly uint _taskbarCreatedMessage;

        internal event EventHandler TaskbarCreated;

        /// <summary>设备树发生变化（插拔、休眠唤醒），已配对设备列表需要重新枚举。</summary>
        internal event EventHandler DevicesChanged;

        internal BroadcastWatcher()
        {
            _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

            Forms.CreateParams createParams = new Forms.CreateParams();
            createParams.ClassName = "STATIC";
            createParams.Style = 0;
            CreateHandle(createParams);
        }

        protected override void WndProc(ref Forms.Message message)
        {
            if (message.Msg == (int)_taskbarCreatedMessage)
            {
                EventHandler handler = TaskbarCreated;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
            else if (message.Msg == WM_DEVICECHANGE && message.WParam.ToInt32() == DBT_DEVNODES_CHANGED)
            {
                EventHandler handler = DevicesChanged;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }

            base.WndProc(ref message);
        }
    }
}
