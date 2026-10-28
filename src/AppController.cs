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
            _tray.Visible = true;
            _tray.ContextMenuStrip = _menu;
            _tray.MouseClick += OnTrayClick;

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += OnTick;
            _timer.Start();

            Log.Write("程序启动");
            StartRefresh();

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
            return overlay;
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
            Log.Write("资源管理器重启，已重建任务栏悬浮条");
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
            List<DeviceBattery> online = new List<DeviceBattery>();
            foreach (DeviceBattery item in all)
            {
                // 只显示当前真正读到电量的设备，避免离线的历史配对设备常驻
                if (!_settings.HiddenIds.Contains(item.Id) && item.Online)
                {
                    online.Add(item);
                }
            }

            DeviceBattery[] shown = online.ToArray();
            _overlay.UpdateItems(shown);
            UpdateTrayIcon(shown);
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

            Icon previous = _tray.Icon;
            _tray.Icon = IconFactory.Create(minimum, anyOnline);
            if (previous != null)
            {
                previous.Dispose();
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

                parts.Add(UiTheme.GetDisplayName(item.Name) + " " + state);
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
            menu.Font = new Font("Segoe UI", 9f);
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
            menu.Font = new Font("Segoe UI", 9f);
            menu.ShowCheckMargin = true;
            menu.ShowImageMargin = false;
            menu.Padding = new Forms.Padding(4);
            menu.Items.Clear();

            Forms.ToolStripMenuItem title = new Forms.ToolStripMenuItem("外设电量");
            title.Enabled = false;
            title.Font = new Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold);
            title.ForeColor = UiTheme.TextSecondary;
            menu.Items.Add(title);

            DeviceBattery[] all = _service.Snapshot();
            if (all.Length == 0)
            {
                Forms.ToolStripMenuItem empty = new Forms.ToolStripMenuItem("（正在扫描或没有可读设备）");
                empty.Enabled = false;
                menu.Items.Add(empty);
            }

            foreach (DeviceBattery snapshot in all)
            {
                DeviceBattery item = snapshot;
                Forms.ToolStripMenuItem deviceItem = new Forms.ToolStripMenuItem(UiTheme.GetDisplayName(item.Name));
                deviceItem.Tag = item;
                deviceItem.CheckState = _settings.HiddenIds.Contains(item.Id)
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

        private void Exit()
        {
            if (_exiting)
            {
                return;
            }

            _exiting = true;
            _timer.Stop();
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
