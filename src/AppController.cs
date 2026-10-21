using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
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
        private readonly DispatcherTimer _timer;

        private DateTime _lastRefresh = DateTime.MinValue;
        private int _lastVersion = -1;
        private volatile bool _refreshing;
        private bool _exiting;
        private string _lastHidSignature = string.Empty;
        private ContextMenu _openMenu;

        internal AppController(System.Windows.Application app)
        {
            _app = app;
            _settings = Settings.Load();
            _service = new BatteryService();

            _overlay = new WpfOverlay();
            _overlay.RefreshRequested += delegate { StartRefresh(); };
            _overlay.SetEnabled(_settings.ShowTaskbar);

            _tray = new Forms.NotifyIcon();
            _tray.Text = "外设电量监控";
            _tray.Icon = IconFactory.Create(0, false);
            _tray.Visible = true;
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
            List<DeviceBattery> visible = new List<DeviceBattery>();
            foreach (DeviceBattery item in all)
            {
                if (!_settings.HiddenIds.Contains(item.Id))
                {
                    visible.Add(item);
                }
            }

            DeviceBattery[] shown = visible.ToArray();
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
            if (e.Button == Forms.MouseButtons.Right)
            {
                ShowMenu();
            }
            else if (e.Button == Forms.MouseButtons.Left)
            {
                StartRefresh();
            }
        }

        private void ShowMenu()
        {
            ApplyMenuTheme();

            if (_openMenu != null && _openMenu.IsOpen)
            {
                _openMenu.IsOpen = false;
            }

            ContextMenu menu = new ContextMenu();
            menu.StaysOpen = false;

            MenuItem title = new MenuItem();
            title.Header = "外设电量";
            title.IsEnabled = false;
            menu.Items.Add(title);

            DeviceBattery[] all = _service.Snapshot();
            if (all.Length == 0)
            {
                MenuItem empty = new MenuItem();
                empty.Header = "（正在扫描或没有可读设备）";
                empty.IsEnabled = false;
                menu.Items.Add(empty);
            }

            foreach (DeviceBattery snapshot in all)
            {
                DeviceBattery item = snapshot;
                MenuItem deviceItem = new MenuItem();
                deviceItem.Header = UiTheme.GetDisplayName(item.Name) + "    "
                    + (item.Online ? item.Percent + "%" : "未连接");
                deviceItem.IsCheckable = true;
                deviceItem.IsChecked = !_settings.HiddenIds.Contains(item.Id);
                deviceItem.Click += delegate { ToggleDevice(item.Id); };
                menu.Items.Add(deviceItem);
            }

            menu.Items.Add(new Separator());

            MenuItem refreshItem = new MenuItem();
            refreshItem.Header = "立即刷新";
            refreshItem.Click += delegate { StartRefresh(); };
            menu.Items.Add(refreshItem);

            MenuItem rescanItem = new MenuItem();
            rescanItem.Header = "重新扫描设备";
            rescanItem.Click += delegate
            {
                _service.RequestFullScan();
                StartRefresh();
            };
            menu.Items.Add(rescanItem);

            MenuItem hidMenu = new MenuItem();
            hidMenu.Header = "识别 HID 设备（VID/PID）";
            BuildHidMenu(hidMenu);
            menu.Items.Add(hidMenu);

            menu.Items.Add(new Separator());

            MenuItem taskbarItem = new MenuItem();
            taskbarItem.Header = "在任务栏显示电量";
            taskbarItem.IsCheckable = true;
            taskbarItem.IsChecked = _settings.ShowTaskbar;
            taskbarItem.Click += delegate
            {
                _settings.ShowTaskbar = !_settings.ShowTaskbar;
                _settings.Save();
                _overlay.SetEnabled(_settings.ShowTaskbar);
            };
            menu.Items.Add(taskbarItem);

            MenuItem autoStartItem = new MenuItem();
            autoStartItem.Header = "开机自动启动";
            autoStartItem.IsCheckable = true;
            autoStartItem.IsChecked = AutoStart.IsEnabled();
            autoStartItem.Click += delegate { AutoStart.SetEnabled(!AutoStart.IsEnabled()); };
            menu.Items.Add(autoStartItem);

            menu.Items.Add(new Separator());

            MenuItem exitItem = new MenuItem();
            exitItem.Header = "退出";
            exitItem.Click += delegate { Exit(); };
            menu.Items.Add(exitItem);

            menu.PlacementTarget = null;
            menu.Placement = PlacementMode.MousePoint;
            _openMenu = menu;
            menu.Opened += delegate
            {
                // 捕获鼠标，确保点击任务栏/桌面等外部区域时菜单能关闭
                System.Windows.Input.Mouse.Capture(menu, System.Windows.Input.CaptureMode.SubTree);
            };
            menu.Closed += delegate
            {
                System.Windows.Input.Mouse.Capture(null);
                if (ReferenceEquals(_openMenu, menu))
                {
                    _openMenu = null;
                }
            };
            menu.IsOpen = true;
        }

        private void ApplyMenuTheme()
        {
            bool light = Theme.IsLight();
            string background = light ? "#F7F7F7" : "#202020";
            string foreground = light ? "#1B1B1B" : "#F2F2F2";
            string hover = light ? "#E4E4E4" : "#3C3C3C";
            string border = light ? "#C8C8C8" : "#424242";
            string check = light ? "#1E8E3E" : "#70DE8E";
            string disabled = light ? "#9A9A9A" : "#6C6C6C";

            string menuXaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='{x:Type ContextMenu}'>
  <Setter Property='Background' Value='__BG__'/>
  <Setter Property='Foreground' Value='__FG__'/>
  <Setter Property='BorderBrush' Value='__BORDER__'/>
  <Setter Property='BorderThickness' Value='1'/>
  <Setter Property='Padding' Value='6'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='{x:Type ContextMenu}'>
        <Border Background='{TemplateBinding Background}'
                BorderBrush='{TemplateBinding BorderBrush}'
                BorderThickness='{TemplateBinding BorderThickness}'
                CornerRadius='9'
                Padding='{TemplateBinding Padding}'>
          <StackPanel IsItemsHost='True'/>
        </Border>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

            string itemXaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='{x:Type MenuItem}'>
  <Setter Property='Foreground' Value='__FG__'/>
  <Setter Property='Padding' Value='10,6'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='{x:Type MenuItem}'>
        <Border x:Name='Bd' Background='Transparent' CornerRadius='5' Padding='{TemplateBinding Padding}'>
          <Grid>
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width='18'/>
              <ColumnDefinition Width='*'/>
              <ColumnDefinition Width='Auto'/>
            </Grid.ColumnDefinitions>
            <TextBlock x:Name='Check' Grid.Column='0' Text='&#x2713;' Foreground='__CHECK__' Visibility='Collapsed' VerticalAlignment='Center' HorizontalAlignment='Center'/>
            <ContentPresenter Grid.Column='1' ContentSource='Header' VerticalAlignment='Center' RecognizesAccessKey='True'/>
            <TextBlock x:Name='Arrow' Grid.Column='2' Text='&#x25B8;' Margin='10,0,0,0' Visibility='Collapsed' Foreground='__FG__'/>
            <Popup x:Name='PART_Popup' Placement='Right' IsOpen='{TemplateBinding IsSubmenuOpen}'
                   PlacementTarget='{Binding RelativeSource={RelativeSource TemplatedParent}}'
                   AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'>
              <Border Background='__BG__' BorderBrush='__BORDER__' BorderThickness='1' CornerRadius='9' Padding='6'>
                <StackPanel IsItemsHost='True'/>
              </Border>
            </Popup>
          </Grid>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsHighlighted' Value='True'>
            <Setter TargetName='Bd' Property='Background' Value='__HOVER__'/>
          </Trigger>
          <Trigger Property='IsChecked' Value='True'>
            <Setter TargetName='Check' Property='Visibility' Value='Visible'/>
          </Trigger>
          <Trigger Property='Role' Value='SubmenuHeader'>
            <Setter TargetName='Arrow' Property='Visibility' Value='Visible'/>
          </Trigger>
          <Trigger Property='IsEnabled' Value='False'>
            <Setter Property='Foreground' Value='__DISABLED__'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

            string separatorXaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='{x:Type Separator}'>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='{x:Type Separator}'>
        <Border Height='1' Background='__BORDER__' Margin='10,4'/>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

            try
            {
                menuXaml = menuXaml.Replace("__BG__", background).Replace("__FG__", foreground)
                    .Replace("__BORDER__", border);
                itemXaml = itemXaml.Replace("__BG__", background).Replace("__FG__", foreground)
                    .Replace("__BORDER__", border).Replace("__HOVER__", hover)
                    .Replace("__CHECK__", check).Replace("__DISABLED__", disabled);
                separatorXaml = separatorXaml.Replace("__BORDER__", border);

                Application.Current.Resources[typeof(ContextMenu)] = XamlReader.Parse(menuXaml);
                Application.Current.Resources[typeof(MenuItem)] = XamlReader.Parse(itemXaml);
                Application.Current.Resources[typeof(Separator)] = XamlReader.Parse(separatorXaml);
            }
            catch (Exception ex)
            {
                Log.Error("应用菜单样式失败", ex);
            }
        }

        private void BuildHidMenu(MenuItem hidMenu)
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
                    MenuItem none = new MenuItem();
                    none.Header = "（未发现 HID 设备）";
                    none.IsEnabled = false;
                    hidMenu.Items.Add(none);
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

                    MenuItem hidItem = new MenuItem();
                    hidItem.Header = text;
                    hidItem.IsEnabled = false;
                    hidItem.ToolTip = "输入报告 " + device.InputReportLength
                        + " 字节，输出报告 " + device.OutputReportLength
                        + " 字节，特性报告 " + device.FeatureReportLength + " 字节";
                    hidMenu.Items.Add(hidItem);
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
