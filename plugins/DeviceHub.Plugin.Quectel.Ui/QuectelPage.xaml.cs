using DeviceHub.Sdk.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DeviceHub.Plugin.Quectel.Ui;

/// <summary>
/// Quectel 模块页面。只读信息直接显示；会改变模块设置的操作（频段、锁定、制式、漫游、飞行模式、网卡模式、重启、
/// GPS、短信收发删）都先弹确认框，用户取消时把控件恢复成模块的实际状态。
/// </summary>
public sealed partial class QuectelPage : UserControl
{
    public QuectelViewModel Vm { get; }

    public QuectelPage(IPluginUiBridge bridge)
    {
        Vm = new QuectelViewModel(bridge);
        InitializeComponent();
        Loaded += async (_, _) => await Vm.LoadAsync();
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "" or null or nameof(QuectelViewModel.Bubbles)) DispatcherQueue.TryEnqueue(AfterSmsUpdate);
        };
    }

    private void Sections_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var sel = sender.SelectedItem;
        StatusPanel.Visibility = Show(sel == StatusTab);
        LockPanel.Visibility = Show(sel == LockTab);
        SmsPanel.Visibility = Show(sel == SmsTab);
        GpsPanel.Visibility = Show(sel == GpsTab);
        SettingsPanel.Visibility = Show(sel == SettingsTab);
        AdvancedPanel.Visibility = Show(sel == AdvancedTab);
        if (sel == SmsTab) AfterSmsUpdate();
    }

    // —— 短信会话 ——

    private bool _selecting;
    private int _bubbleCount = -1;
    private string? _bubbleThread;

    /// <summary>会话列表重建后把选中项找回来；气泡变多（新消息）时滚到底部。</summary>
    private void AfterSmsUpdate()
    {
        var key = Vm.SelectedThread;
        if (key is not null && (ThreadList.SelectedItem as ThreadRow)?.Key != key)
        {
            _selecting = true;
            ThreadList.SelectedItem = Vm.Threads.FirstOrDefault(t => t.Key == key);
            _selecting = false;
        }
        var bubbles = Vm.Bubbles;
        if (bubbles.Count > 0 && (bubbles.Count != _bubbleCount || key != _bubbleThread))
            BubbleList.ScrollIntoView(bubbles[^1]);
        _bubbleCount = bubbles.Count;
        _bubbleThread = key;
    }

    private void ThreadList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selecting || ThreadList.SelectedItem is not ThreadRow t) return;
        Vm.SelectThread(t.Key);
        AfterSmsUpdate();
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        _selecting = true;
        ThreadList.SelectedItem = null;
        _selecting = false;
        Vm.StartNewChat();
        SmsTo.Text = "";
        SmsTo.Focus(FocusState.Programmatic);
    }

    private void SmsText_TextChanged(object sender, TextChangedEventArgs e) => SmsCount.Text = $"{SmsText.Text.Length}/70";

    private void SmsText_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
        if (shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return; // Shift+Enter 换行
        e.Handled = true;
        SendSms_Click(sender, e);
    }

    private void CopySms_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SmsMessage m) return;
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(m.Text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
    }

    public static Visibility Show(bool v) => v ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Hide(bool v) => v ? Visibility.Collapsed : Visibility.Visible;

    private async Task<bool> ConfirmAsync(string title, string body, string primary = "确定")
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>用户取消后把控件拉回模块的真实状态。</summary>
    private void Revert() => Vm.PropertyChangedAll();

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = Vm.InvokeAsync("Refresh", new { });

    // —— 频段 ——

    private async void ApplyBands_Click(object sender, RoutedEventArgs e)
    {
        var bands = Vm.Bands.Where(b => b.IsOn).Select(b => b.Band).ToList();
        if (bands.Count == 0) { await ConfirmAsync("至少选一个频段", "全部取消会让模块搜不到网。", "知道了"); return; }
        if (!await ConfirmAsync("限定 LTE 频段",
                $"模块将只使用 B{string.Join("/B", bands)}。设置立即生效并在断电后保留；所选频段没有信号时模块会断网。\n\n可以随时用「恢复全部频段」还原。"))
            return;
        await Vm.InvokeAsync("SetBands", new { bands });
    }

    private async void RestoreBands_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync("恢复全部频段", "把 LTE 频段恢复为模块支持的全部频段。"))
            await Vm.InvokeAsync("RestoreBands", new { });
    }

    // —— 小区 / 频点锁定 ——

    private void UseServing_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.State.Serving is { Earfcn: { } earfcn, Pci: { } pci })
        {
            EarfcnBox.Value = earfcn;
            PciBox.Value = pci;
        }
    }

    private async void LockNeighbour_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not NeighbourCell { Pci: { } pci } n) return;
        await LockAsync(n.Earfcn, pci);
    }

    private async void Lock_Click(object sender, RoutedEventArgs e)
    {
        if (double.IsNaN(EarfcnBox.Value)) { await ConfirmAsync("缺少 EARFCN", "请填写要锁定的频点号。", "知道了"); return; }
        await LockAsync((int)EarfcnBox.Value, double.IsNaN(PciBox.Value) ? null : (int)PciBox.Value);
    }

    private async Task LockAsync(int earfcn, int? pci)
    {
        var what = pci is { } p ? $"小区 EARFCN {earfcn} / PCI {p}" : $"频点 EARFCN {earfcn}（该频点上的任意小区）";
        if (!await ConfirmAsync("锁定" + (pci is null ? "频点" : "小区"),
                $"模块将只驻留在{what}。设置在断电后保留；该小区或频点信号消失时模块会断网，需要「解除锁定」才能恢复。"))
            return;
        if (pci is { } c) await Vm.InvokeAsync("LockCell", new { earfcn, pci = c });
        else await Vm.InvokeAsync("LockFrequency", new { earfcn });
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e) => await Vm.InvokeAsync("Unlock", new { });

    // —— 网络 ——

    private async void ScanMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Vm.Applying || ScanModeBox.SelectedIndex < 0 || ScanModeBox.SelectedIndex == Vm.ScanModeIndex) return;
        var mode = QuectelViewModel.ScanModeFromIndex(ScanModeBox.SelectedIndex);
        var name = ((ComboBoxItem)ScanModeBox.SelectedItem).Content;
        if (!await ConfirmAsync("切换网络制式", $"模块将改为「{name}」，会短暂断网重新搜网。设置在断电后保留。")) { Revert(); return; }
        await Vm.InvokeAsync("SetScanMode", new { mode });
    }

    private async void Roaming_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Vm.Applying || RoamingBox.SelectedIndex < 0 || RoamingBox.SelectedIndex == Vm.RoamingIndex) return;
        var mode = QuectelViewModel.RoamingFromIndex(RoamingBox.SelectedIndex);
        var name = ((ComboBoxItem)RoamingBox.SelectedItem).Content;
        var warn = mode == 1 ? "\n\n当前 SIM 正在漫游时，禁止漫游会让模块断网。" : "";
        if (!await ConfirmAsync("修改漫游设置", $"漫游改为「{name}」。设置在断电后保留。{warn}")) { Revert(); return; }
        await Vm.InvokeAsync("SetRoaming", new { mode });
    }

    private async void Airplane_Toggled(object sender, RoutedEventArgs e)
    {
        if (Vm.Applying || AirplaneSwitch.IsOn == Vm.AirplaneOn) return;
        bool on = AirplaneSwitch.IsOn;
        if (on && !await ConfirmAsync("进入飞行模式", "关闭模块射频，蜂窝网络与短信都会断开。")) { Revert(); return; }
        await Vm.InvokeAsync("SetRadio", new { on = !on });
    }

    private async void UsbNet_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Vm.Applying || UsbNetBox.SelectedIndex < 0 || UsbNetBox.SelectedIndex == Vm.UsbNetIndex) return;
        int mode = UsbNetBox.SelectedIndex;
        var name = ((ComboBoxItem)UsbNetBox.SelectedItem).Content;
        if (!await ConfirmAsync("切换 USB 网卡模式",
                $"把模块的 USB 网卡改为「{name}」，重启模块后生效，USB 设备组成会变化。\n\n大疆设备上使用时需要 ECM 模式，用完记得改回。"))
        { Revert(); return; }
        await Vm.InvokeAsync("SetUsbNet", new { mode });
        if (await ConfirmAsync("现在重启模块？", "网卡模式要重启模块后才生效。", "重启"))
            await Vm.InvokeAsync("Reboot", new { });
    }

    private async void Reboot_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync("重启模块", "模块会断开 USB 约 20 秒，蜂窝连接中断，之后自动重新接管。", "重启"))
            await Vm.InvokeAsync("Reboot", new { });
    }

    // —— GPS ——

    private async void Gps_Toggled(object sender, RoutedEventArgs e)
    {
        if (Vm.Applying || GpsSwitch.IsOn == Vm.GpsOn) return;
        await Vm.InvokeAsync("SetGps", new { on = GpsSwitch.IsOn });
    }

    // —— 短信 ——

    private async void LoadSms_Click(object sender, RoutedEventArgs e)
    {
        // 第一次读取前说明一下"会标为已读"，之后的刷新直接读。
        if (!Vm.SmsLoaded && !await ConfirmAsync("读取短信", "从模块读取全部短信。读取后未读短信会被标为已读，之后新短信会自动出现在这里。", "读取"))
            return;
        await Vm.InvokeAsync("LoadSms", new { });
    }

    private async void DeleteSms_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SmsMessage m) return;
        var where = m.Index < 0 ? "（只删除本机的发送记录）" : "（从模块存储里永久删除）";
        if (await ConfirmAsync("删除短信", $"删除这条短信？{where}", "删除"))
            await Vm.InvokeAsync("DeleteSms", new { index = m.Index });
    }

    private bool _sending;

    /// <summary>
    /// 发送：已有会话直接发（像聊天软件），新号码的第一条先确认一次号码与资费。
    /// </summary>
    private async void SendSms_Click(object sender, RoutedEventArgs e)
    {
        if (_sending || !Vm.CanOperate) return;
        var to = Vm.ReplyTo ?? SmsTo.Text.Trim();
        var text = SmsText.Text.Trim();
        if (text.Length == 0) return;
        if (to.Length == 0) { SmsTo.Focus(FocusState.Programmatic); return; }
        if (Vm.ReplyTo is null && !await ConfirmAsync("发送到新号码", $"发送到 {to}？按运营商资费收费。", "发送")) return;
        _sending = true;
        try
        {
            await Vm.InvokeAsync("SendSms", new { to, text });
            if (!Vm.Status.StartsWith("已发送", StringComparison.Ordinal)) return;
            SmsText.Text = "";
            // 新会话发出第一条后切到这个会话。
            if (Vm.ReplyTo is null) Vm.SelectThread(AtParse.PeerKey(to));
            AfterSmsUpdate();
        }
        finally { _sending = false; }
    }

    // —— AT 控制台 ——

    private async void RunAt_Click(object sender, RoutedEventArgs e) => await RunAtAsync();

    private async void AtInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        await RunAtAsync();
    }

    private async Task RunAtAsync()
    {
        var cmd = AtInput.Text.Trim();
        if (cmd.Length == 0) return;
        await Vm.InvokeAsync("RunAt", new { command = cmd });
        AtInput.SelectAll();
    }

    private async void ClearConsole_Click(object sender, RoutedEventArgs e) => await Vm.InvokeAsync("ClearConsole", new { });
}

/// <summary>插件 UI 入口。</summary>
public sealed class QuectelPageEntry : IPluginPage
{
    public FrameworkElement CreatePage(IPluginUiBridge bridge) => new QuectelPage(bridge);
}
