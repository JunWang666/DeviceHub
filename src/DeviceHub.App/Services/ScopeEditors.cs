using System.Diagnostics;
using DeviceHub.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeviceHub.App.Services;

/// <summary>
/// 「仅在这些设备上生效」：当前连接的键盘 / 鼠标做成勾选列表，外加「识别设备」（按一下那个键盘的键或晃一下那只鼠标）。
/// 规则里存过、但现在没连接的设备也列出来（标"未连接"），不会因为设备暂时拔掉而丢失设置。
/// </summary>
public sealed class DeviceScopeEditor : UserControl
{
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly TextBlock _hint = new() { Style = KeyPicker.Caption(), Foreground = KeyPicker.Brush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap };
    private readonly Button _identify = new();
    private readonly List<(DeviceFilter Filter, CheckBox Box)> _items = new();
    private bool _identifying;

    public DeviceScopeEditor(IReadOnlyList<InputDeviceInfo> present, IReadOnlyList<DeviceFilter>? selected)
    {
        foreach (var d in present)
            Add(new DeviceFilter { Id = d.Id, Kind = d.Kind, Name = d.Name }, selected?.Any(s => s.Kind == d.Kind && Same(s.Id, d.Id)) == true, online: true);
        foreach (var s in selected ?? [])
            if (!present.Any(d => d.Kind == s.Kind && Same(d.Id, s.Id))) Add(s, true, online: false);

        _identify.Click += Identify_Click;
        SetIdentify(false);
        _hint.Text = DefaultHint;
        Content = new StackPanel { Spacing = 8, Children = { _list, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _identify } }, _hint } };
    }

    private const string DefaultHint = "都不勾 = 所有设备。按最近一次有输入的键盘 / 鼠标判断：鼠标总是准的；换了键盘后按下的第一个被映射的单键可能按上一个键盘算。同型号的两个设备、同一接收器上的设备无法区分。";

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private CheckBox Add(DeviceFilter f, bool isChecked, bool online)
    {
        var box = new CheckBox
        {
            IsChecked = isChecked,
            Content = $"{(f.Kind == InputDeviceKind.Keyboard ? "键盘" : "鼠标")} · {f.Name ?? f.Id}{(online ? "" : "（未连接）")}",
        };
        ToolTipService.SetToolTip(box, f.Id);
        _items.Add((f, box));
        _list.Children.Add(box);
        return box;
    }

    public List<DeviceFilter>? Value
    {
        get
        {
            var l = _items.Where(i => i.Box.IsChecked == true).Select(i => i.Filter).ToList();
            return l.Count == 0 ? null : l;
        }
    }

    private async void Identify_Click(object sender, RoutedEventArgs e)
    {
        if (_identifying) { InputCapture.Instance.Stop(); return; }
        _identifying = true;
        SetIdentify(true);
        _hint.Text = "请按一下那个键盘上的 Shift，或晃动那只鼠标…（按下的键会照常生效）";
        try
        {
            var d = await InputCapture.Instance.IdentifyDeviceAsync();
            if (d is null) return;
            var existing = _items.FirstOrDefault(i => i.Filter.Kind == d.Kind && Same(i.Filter.Id, d.Id));
            if (existing.Box is not null) existing.Box.IsChecked = true;
            else Add(new DeviceFilter { Id = d.Id, Kind = d.Kind, Name = d.Name }, true, online: true);
        }
        finally
        {
            _identifying = false;
            SetIdentify(false);
            _hint.Text = DefaultHint;
        }
    }

    private void SetIdentify(bool on) => _identify.Content = new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 8,
        Children = { new FontIcon { Glyph = on ? "" : "", FontSize = 14 }, new TextBlock { Text = on ? "停止识别" : "识别设备" } },
    };
}

/// <summary>「仅在这些程序中生效」：进程名文本框 + 从正在运行（有窗口）的程序里挑。</summary>
public sealed class AppScopeEditor : UserControl
{
    private readonly TextBox _box;

    public AppScopeEditor(string? apps)
    {
        _box = new TextBox { Text = apps ?? "", PlaceholderText = "留空 = 所有程序；多个用分号分隔，如 chrome.exe; code.exe" };
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) => Fill(flyout);
        var pick = new DropDownButton { Content = "从运行中的程序选择", Flyout = flyout };
        Grid.SetColumn(pick, 1);
        Content = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
            Children = { _box, pick },
        };
    }

    public string? Value => _box.Text.Trim().Length > 0 ? _box.Text.Trim() : null;

    private void Fill(MenuFlyout flyout)
    {
        flyout.Items.Clear();
        var current = _box.Text.Split([';', ',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (exe, title) in Running())
        {
            var item = new MenuFlyoutItem { Text = title.Length > 0 ? $"{exe}  —  {Trim(title)}" : exe, IsEnabled = !current.Contains(exe) };
            item.Click += (_, _) =>
            {
                var t = _box.Text.Trim().TrimEnd(';');
                _box.Text = t.Length == 0 ? exe : $"{t}; {exe}";
            };
            flyout.Items.Add(item);
        }
        if (flyout.Items.Count == 0) flyout.Items.Add(new MenuFlyoutItem { Text = "没有找到有窗口的程序", IsEnabled = false });
    }

    private static string Trim(string s) => s.Length > 40 ? s[..39] + "…" : s;

    /// <summary>有主窗口的进程（去重，按名称排序）；以管理员身份运行的拿不到路径，用进程名。</summary>
    private static List<(string Exe, string Title)> Running()
    {
        var self = Environment.ProcessId;
        var list = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self || p.MainWindowHandle == 0) continue;
                var exe = p.ProcessName + ".exe";
                if (!list.ContainsKey(exe)) list[exe] = p.MainWindowTitle;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return list.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => (kv.Key, kv.Value)).ToList();
    }
}
