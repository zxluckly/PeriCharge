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
        private readonly WpfOverlay _overlay;
        private readonly Forms.NotifyIcon _tray;
        private readonly Forms.ContextMenuStrip _menu;
        private readonly DispatcherTimer _timer;

        private DateTime _lastRefresh = DateTime.MinValue;
        private int _lastVersion = -1;
        private volatile bool _refreshing;
        private bool _exiting;
        private string _lastHidSignature = string.Empty;

        internal AppController(System.Windows.Application app)
        {
            _app = app;
            _settings = Settings.Load();
            _service = new BatteryService();

            _overlay = new WpfOverlay();
            _overlay.RefreshRequested += delegate { StartRefresh(); };

            _menu = BuildMenu();

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

        private void StartRefresh()
        {
            if (_refreshing)
            {
                return;
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
                parts.Add(UiTheme.GetDisplayName(item.Name) + " "
                    + (item.Online ? item.Percent + "%" : "未连接"));
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
                StartRefresh();
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
                deviceItem.Checked = !_settings.HiddenIds.Contains(item.Id);
                deviceItem.Click += delegate { ToggleDevice(item.Id); };
                menu.Items.Add(deviceItem);
            }

            menu.Items.Add(new Forms.ToolStripSeparator());

            Forms.ToolStripMenuItem refreshItem = new Forms.ToolStripMenuItem("立即刷新");
            refreshItem.Click += delegate { StartRefresh(); };
            menu.Items.Add(refreshItem);

            Forms.ToolStripMenuItem rescanItem = new Forms.ToolStripMenuItem("重新扫描设备");
            rescanItem.Click += delegate
            {
                _service.RequestFullScan();
                StartRefresh();
            };
            menu.Items.Add(rescanItem);

            Forms.ToolStripMenuItem hidMenu = new Forms.ToolStripMenuItem("识别 HID 设备（VID/PID）");
            BuildHidMenu(hidMenu);
            menu.Items.Add(hidMenu);

            menu.Items.Add(new Forms.ToolStripSeparator());

            Forms.ToolStripMenuItem taskbarItem = new Forms.ToolStripMenuItem("在任务栏显示电量");
            taskbarItem.Checked = _settings.ShowTaskbar;
            taskbarItem.Click += delegate
            {
                _settings.ShowTaskbar = !_settings.ShowTaskbar;
                _settings.Save();
                _overlay.SetEnabled(_settings.ShowTaskbar);
            };
            menu.Items.Add(taskbarItem);

            Forms.ToolStripMenuItem autoStartItem = new Forms.ToolStripMenuItem("开机自动启动");
            autoStartItem.Checked = AutoStart.IsEnabled();
            autoStartItem.Click += delegate { AutoStart.SetEnabled(!AutoStart.IsEnabled()); };
            menu.Items.Add(autoStartItem);

            menu.Items.Add(new Forms.ToolStripSeparator());

            Forms.ToolStripMenuItem exitItem = new Forms.ToolStripMenuItem("退出");
            exitItem.Click += delegate { Exit(); };
            menu.Items.Add(exitItem);
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
            _overlay.Close();
            Log.Write("程序退出");
            _app.Shutdown();
        }
    }
}
