using DeviceHub.App.Pages;
using DeviceHub.App.Services;
using DeviceHub.Contracts;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeviceHub.App;

public sealed partial class MainWindow : Window
{
    private readonly AppState _state = AppState.Instance;
    private bool _dialogOpen;
    private bool _rebuilding;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SizeAndCenter(1080, 720);

        _state.Dispatcher = DispatcherQueue.GetForCurrentThread();
        _state.Client.Disconnected += () => _state.OnUi(() => SetConn(false));
        _state.PermissionRequested += OnPermissionRequested;
        _state.DevicesRefreshed += RebuildDeviceMenu;

        ContentFrame.Navigate(typeof(HomePage));
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        await _state.InitializeAsync();
        SetConn(_state.Client.Connected);
        RebuildDeviceMenu();
        if (Environment.GetEnvironmentVariable("DEVICEHUB_SELFTEST") == "1") await SelfTestAsync();
    }

    /// <summary>
    /// 只读自检：发现设备 → 建议插件 → 动态加载插件 UI。
    /// 不授权、不做设备 I/O，避免在真机上写入（真机写入走手动授权流程）。
    /// </summary>
    private async Task SelfTestAsync()
    {
        try
        {
            var agent = _state.Client.Agent!;
            Console.WriteLine($"SELFTEST: 新设备={_state.NewDevices.Count} 正在管理={_state.ManagedDevices.Count}（离线 {_state.ManagedDevices.Count(d => !d.Connected)}） 授权记录={(await agent.ListGrantsAsync()).Count}");
            Console.WriteLine("SELFTEST: 侧边栏图标 " + string.Join(", ", _state.ManagedDevices.Select(d => $"{d.DeviceKey}={(Glyphs.IsValid(d.Glyph) ? ((int)d.Glyph![0]).ToString("X4") : "默认")}")));
            // 离线的已授权设备：插件应报告"已授权、未连接"，而不是"未授权"。
            foreach (var d in _state.ManagedDevices.Where(d => !d.Connected && d.OwnerPluginId is not null))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(await agent.GetPluginStateAsync(d.OwnerPluginId!, d.DeviceKey));
                    string F(string n) => doc.RootElement.TryGetProperty(n, out var v) ? v.ToString() : "?";
                    Console.WriteLine($"SELFTEST: 离线设备 {d.DeviceKey} authorized={F("authorized")} connected={F("connected")}");
                }
                catch (Exception ex) { Console.WriteLine($"SELFTEST: 离线设备 {d.DeviceKey} 读状态失败 {ex.Message}"); }
            }
            var usb = await agent.GetUsbTopologyAsync();
            var usbIssues = new List<(Services.UsbRow, UsbIssue)>();
            var usbRows = Services.UsbTreeBuilder.Build(usb, showEmptyPorts: false, usbIssues);
            Console.WriteLine($"SELFTEST: USB 控制器={usb.Controllers.Count} 设备={usbRows.Sum(CountDevices)} 诊断={usbIssues.Count}");
            ContentFrame.Navigate(typeof(UsbPage));
            Services.OsdWindow.Show(new OsdMessage
            {
                PluginId = "selftest", DeviceKey = "selftest:none", DeviceName = "自检设备",
                Kind = OsdKind.BatteryLow, Glyph = "", Text = "电量低（12%），请及时充电", Percent = 12,
            });
            Console.WriteLine("SELFTEST: 屏幕状态提示窗口已显示");
            var remap = await agent.GetRemapConfigAsync();
            ContentFrame.Navigate(typeof(RemapPage));
            _ = new KeyPicker(CaptureMode.Trigger);
            Console.WriteLine($"SELFTEST: REMAP page ok rules={remap.Rules.Count} macros={remap.Macros.Count} engineError={(await agent.GetRemapEngineErrorAsync()) ?? "none"}");
            // 每个已安装插件的 XAML 都加载一次（不依赖设备在线；设备标识是假的，后端返回默认状态）。
            foreach (var p in _state.Plugins.Where(p => p.UiAssemblyPath is not null && p.UiPageTypeName is not null))
            {
                try
                {
                    var b = new Services.PluginUiBridge(_state.Client, p.Id, "selftest:none");
                    var pg = Services.PluginUiLoader.LoadPage(p.UiAssemblyPath!, p.UiPageTypeName!, b);
                    Console.WriteLine($"SELFTEST: 插件 {p.Id} XAML 加载成功 type={pg.GetType().FullName}");
                    foreach (var w in p.Widgets)
                        foreach (var size in w.Sizes)
                        {
                            var wb = new Services.PluginUiBridge(_state.Client, p.Id, "selftest:none");
                            var el = Services.PluginUiLoader.LoadWidget(p.UiAssemblyPath!, p.UiWidgetTypeName!, w.Id, size, wb);
                            Console.WriteLine($"SELFTEST: 插件 {p.Id} 小组件 {w.Id}/{size} 创建成功 type={el.GetType().Name}");
                            wb.Dispose();
                        }
                }
                catch (Exception ex) { Console.WriteLine($"SELFTEST: 插件 {p.Id} XAML 加载失败 {ex.Message} ← {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}"); }
            }

            var dev = _state.NewDevices.FirstOrDefault() ?? _state.ManagedDevices.FirstOrDefault();
            if (dev is null) { Console.WriteLine("SELFTEST: 没有可被插件管理的设备"); return; }
            Console.WriteLine($"SELFTEST: {dev.DisplayName} [{dev.DeviceKey}] {dev.ChannelSummary}");

            var candidates = await agent.ListCandidatePluginsAsync(dev.DeviceKey);
            Console.WriteLine($"SELFTEST: 建议插件={string.Join(",", candidates.Select(c => c.Id))}");
            if (candidates.Count == 0) return;

            var plugin = _state.Plugins.FirstOrDefault(p => p.Id == candidates[0].Id);
            var bridge = new Services.PluginUiBridge(_state.Client, plugin!.Id, dev.DeviceKey);
            var page = Services.PluginUiLoader.LoadPage(plugin.UiAssemblyPath!, plugin.UiPageTypeName!, bridge);
            Console.WriteLine($"SELFTEST: 插件 XAML 动态加载成功 type={page.GetType().FullName}");
        }
        catch (Exception ex) { Console.WriteLine("SELFTEST: 失败 " + ex); }
    }

    private static int CountDevices(Services.UsbRow r) => (r.Badge.Length > 0 ? 1 : 0) + r.Children.Sum(CountDevices);

    /// <summary>按有效像素设定初始窗口大小（随 DPI 缩放），并居中到当前显示器工作区。</summary>
    private void SizeAndCenter(int width, int height)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest).WorkArea;
        int w = Math.Min((int)(width * scale), area.Width), h = Math.Min((int)(height * scale), area.Height);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    private void SetConn(bool ok)
    {
        if (ok) { ConnBar.IsOpen = false; }
        else { ConnBar.Severity = InfoBarSeverity.Error; ConnBar.Title = "未连接"; ConnBar.Message = "Agent 未运行或已断开。"; ConnBar.IsOpen = true; }
    }

    /// <summary>用正在管理的设备重建左侧菜单。</summary>
    private void RebuildDeviceMenu()
    {
        _rebuilding = true;
        var selectedKey = (Nav.SelectedItem as NavigationViewItem)?.Tag as string;

        // 只移除设备项（Tag 以 "dev:" 开头），保留顶部的"新设备/插件/分隔线/标题"。
        foreach (var item in Nav.MenuItems.OfType<NavigationViewItem>()
                     .Where(i => (i.Tag as string)?.StartsWith("dev:") == true).ToList())
            Nav.MenuItems.Remove(item);

        // 子设备（如接收器上配对的鼠标）挂在父设备下面，默认展开；未连接的设备（含离线占位）淡显。
        var managedKeys = _state.ManagedDevices.Select(d => d.DeviceKey).ToHashSet();
        foreach (var dev in _state.ManagedDevices.Where(d => d.ParentKey is null || !managedKeys.Contains(d.ParentKey)))
        {
            var item = DeviceItem(dev);
            foreach (var child in _state.ManagedDevices.Where(c => c.ParentKey == dev.DeviceKey))
                item.MenuItems.Add(DeviceItem(child));
            item.IsExpanded = item.MenuItems.Count > 0;
            Nav.MenuItems.Add(item);
        }

        var hasDevices = _state.ManagedDevices.Count > 0;
        DeviceSeparator.Visibility = hasDevices ? Visibility.Visible : Visibility.Collapsed;
        DeviceHeader.Visibility = hasDevices ? Visibility.Visible : Visibility.Collapsed;
        _rebuilding = false;

        // 还原选中项；默认选中"主页"（页面已在构造时导航过去，这里只同步选中态，不重复导航）
        if (selectedKey != null && selectedKey.StartsWith("dev:"))
        {
            var match = FindItem(selectedKey);
            if (match != null) { Nav.SelectedItem = match; return; }
        }
        if (Nav.SelectedItem == null)
        {
            _rebuilding = true;
            Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == "home");
            _rebuilding = false;
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_rebuilding) return;
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        if (tag is null) return;
        if (tag == "home") ContentFrame.Navigate(typeof(HomePage));
        else if (tag == "new") ContentFrame.Navigate(typeof(NewDevicePage));
        else if (tag == "plugins") ContentFrame.Navigate(typeof(PluginsPage));
        else if (tag == "usb") ContentFrame.Navigate(typeof(UsbPage));
        else if (tag == "tool:remap") ContentFrame.Navigate(typeof(RemapPage));
        else if (tag.StartsWith("dev:"))
        {
            var key = tag[4..];
            var dev = _state.ManagedDevices.FirstOrDefault(d => d.DeviceKey == key);
            if (dev != null) ContentFrame.Navigate(typeof(DevicePluginHostPage), dev);
        }
    }

    public void NavigateToManagedDevice(string deviceKey)
    {
        var item = FindItem("dev:" + deviceKey);
        if (item != null) Nav.SelectedItem = item;
    }

    private static NavigationViewItem DeviceItem(DeviceInfo dev) => new()
    {
        Content = dev.DisplayName,
        Tag = "dev:" + dev.DeviceKey,
        Icon = new FontIcon { Glyph = Glyphs.IsValid(dev.Glyph) ? dev.Glyph : Glyphs.Device }, // 插件可指定图标（如 AirPods 用耳机）
        Opacity = dev.Connected ? 1 : 0.55,
    };

    /// <summary>在菜单（含子设备层级）里按 Tag 找项。</summary>
    private NavigationViewItem? FindItem(string tag) =>
        Nav.MenuItems.OfType<NavigationViewItem>()
            .SelectMany(i => i.MenuItems.OfType<NavigationViewItem>().Prepend(i))
            .FirstOrDefault(i => (string?)i.Tag == tag);

    // 兜底：仍支持插件自发的授权弹窗（新流程下一般不触发）
    private async void OnPermissionRequested(PermissionRequest req)
    {
        if (_dialogOpen) return;
        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "设备访问请求",
                Content = $"插件「{req.PluginDisplayName}」请求访问：{req.Device.DisplayName}\n\n用途：{req.Reason}",
                PrimaryButtonText = "始终允许",
                CloseButtonText = "拒绝",
                DefaultButton = ContentDialogButton.Primary,
            };
            var result = await dialog.ShowAsync();
            var scope = result == ContentDialogResult.Primary ? AccessScope.Always : AccessScope.Denied;
            if (_state.Client.Agent != null) await _state.Client.Agent.RespondPermissionAsync(req.RequestId, scope);
        }
        finally { _dialogOpen = false; }
    }
}
