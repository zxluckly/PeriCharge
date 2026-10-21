using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PeripheralBattery
{
    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly Settings _settings;
        private readonly BatteryService _service;
        private readonly TaskbarOverlay _overlay;
        private readonly NotifyIcon _tray;
        private readonly Timer _uiTimer;

        private DateTime _lastRefresh = DateTime.MinValue;
        private int _lastVersion = -1;
        private volatile bool _refreshing;
        private bool _exiting;
        private ContextMenuStrip _openMenu;
        private string _lastHidSignature = string.Empty;

        internal TrayApplicationContext(Settings settings)
        {
            _settings = settings;
            _service = new BatteryService();

            _overlay = new TaskbarOverlay();
            _overlay.MenuRequested += delegate { ShowMenu(); };
            _overlay.SetScanning(true);
            _overlay.SetEnabled(settings.ShowTaskbar);

            _tray = new NotifyIcon();
            _tray.Text = "外设电量监控";
            _tray.Icon = IconFactory.Create(0, false);
            _tray.Visible = true;
            _tray.MouseClick += OnTrayMouseClick;

            _uiTimer = new Timer();
            _uiTimer.Interval = 1000;
            _uiTimer.Tick += OnUiTick;
            _uiTimer.Start();

            Log.Write("程序启动");
            StartRefresh();
        }

        private void OnUiTick(object sender, EventArgs e)
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

            Task refreshTask = _service.RefreshAsync();
            refreshTask.ContinueWith(delegate(Task completed)
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
            List<DeviceBattery> visible = new List<DeviceBattery>();
            foreach (DeviceBattery item in all)
            {
                if (!_settings.HiddenIds.Contains(item.Id))
                {
                    visible.Add(item);
                }
            }

            DeviceBattery[] shown = visible.ToArray();
            _overlay.SetItems(shown);
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

            string tooltip = BuildTooltip(items);
            if (tooltip.Length > 63)
            {
                tooltip = tooltip.Substring(0, 63);
            }

            _tray.Text = tooltip;
        }

        private static string BuildTooltip(DeviceBattery[] items)
        {
            if (items.Length == 0)
            {
                return "外设电量：未发现设备";
            }

            List<string> parts = new List<string>();
            foreach (DeviceBattery item in items)
            {
                parts.Add(item.Name + " " + (item.Online ? item.Percent + "%" : "未连接"));
            }

            return "外设电量：" + string.Join("，", parts.ToArray());
        }

        private void OnTrayMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
            {
                ShowMenu();
            }
        }

        private void ShowMenu()
        {
            // 关闭并释放上一次的菜单，避免反复打开时句柄堆积
            if (_openMenu != null)
            {
                ContextMenuStrip previousMenu = _openMenu;
                _openMenu = null;
                previousMenu.Close();
                previousMenu.Dispose();
            }

            ContextMenuStrip menu = new ContextMenuStrip();
            _openMenu = menu;
            menu.ShowImageMargin = false;

            ToolStripMenuItem title = new ToolStripMenuItem("外设电量监控");
            title.Enabled = false;
            menu.Items.Add(title);

            DeviceBattery[] all = _service.Snapshot();
            if (all.Length == 0)
            {
                ToolStripMenuItem empty = new ToolStripMenuItem("（正在扫描或没有可读设备）");
                empty.Enabled = false;
                menu.Items.Add(empty);
            }

            foreach (DeviceBattery snapshot in all)
            {
                DeviceBattery item = snapshot;
                string stateText = item.Online ? item.Percent + "%" : "未连接";
                string text = item.Name + "    " + stateText;
                ToolStripMenuItem deviceItem = new ToolStripMenuItem(text, null, delegate
                {
                    ToggleDevice(item.Id);
                });
                deviceItem.Checked = !_settings.HiddenIds.Contains(item.Id);
                menu.Items.Add(deviceItem);
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("立即刷新", null, delegate { StartRefresh(); }));
            menu.Items.Add(new ToolStripMenuItem("重新扫描设备", null, delegate
            {
                _service.RequestFullScan();
                StartRefresh();
            }));

            ToolStripMenuItem hidMenu = new ToolStripMenuItem("识别 HID 设备（VID/PID）");
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
                    ToolStripMenuItem none = new ToolStripMenuItem("（未发现 HID 设备）");
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

                    ToolStripMenuItem hidItem = new ToolStripMenuItem(text);
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

            menu.Items.Add(hidMenu);

            ToolStripMenuItem taskbarItem = new ToolStripMenuItem("在任务栏显示电量");
            taskbarItem.Checked = _settings.ShowTaskbar;
            taskbarItem.Click += delegate
            {
                _settings.ShowTaskbar = !_settings.ShowTaskbar;
                _settings.Save();
                _overlay.SetEnabled(_settings.ShowTaskbar);
            };
            menu.Items.Add(taskbarItem);

            ToolStripMenuItem autoStartItem = new ToolStripMenuItem("开机自动启动");
            autoStartItem.Checked = AutoStart.IsEnabled();
            autoStartItem.Click += delegate { AutoStart.SetEnabled(!AutoStart.IsEnabled()); };
            menu.Items.Add(autoStartItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("退出", null, delegate { Exit(); }));

            menu.Closed += delegate
            {
                // 只解除引用；立即 Dispose 会让 ToolStrip 内部清理访问已释放对象
                if (ReferenceEquals(_openMenu, menu))
                {
                    _openMenu = null;
                }
            };
            menu.Show(Cursor.Position);
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
            _uiTimer.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            _overlay.Close();
            _overlay.Dispose();
            Log.Write("程序退出");
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_exiting)
            {
                _exiting = true;
                _uiTimer.Stop();
                _tray.Visible = false;
                _tray.Dispose();
                _overlay.Close();
                _overlay.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
