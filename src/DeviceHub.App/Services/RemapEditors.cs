using System.Text.Json;
using DeviceHub.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace DeviceHub.App.Services;

/// <summary>
/// 按键选择器：修饰键开关 + 主键下拉 + 「录制」。触发键模式下下拉里还有鼠标按键，录到的设备按键（如罗技手势键）也显示在下拉里。
/// </summary>
public sealed class KeyPicker : UserControl
{
    private readonly ToggleButton _ctrl = Mod("Ctrl"), _alt = Mod("Alt"), _shift = Mod("Shift"), _win = Mod("Win");
    private readonly ComboBox _combo = new() { MinWidth = 220, PlaceholderText = "选择按键" };
    private readonly Button _record = new();
    private readonly TextBlock _hint = new() { Style = Caption(), Foreground = Brush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap };
    private readonly CaptureMode _mode;
    private readonly bool _allowEmptyKey;
    private bool _recording, _setting;
    private InputTrigger? _device; // 录到的设备按键（下拉里的最后一项）

    public event Action? Changed;

    private sealed record Item(string Text, TriggerKind Kind, int Key, MouseButton Button)
    {
        public override string ToString() => Text;
    }

    /// <param name="mode">Trigger：键盘 + 鼠标 + 设备按键；Keys：只有键盘（动作 / 宏步骤）。</param>
    /// <param name="allowEmptyKey">动作可以只是修饰键（如单按 Ctrl+Shift）。</param>
    public KeyPicker(CaptureMode mode, bool allowEmptyKey = false)
    {
        _mode = mode;
        _allowEmptyKey = allowEmptyKey;
        if (allowEmptyKey) _combo.Items.Add(new Item("（只按修饰键）", TriggerKind.Key, 0, default));
        foreach (var (vk, name) in KeyNames.Keys) _combo.Items.Add(new Item(name, TriggerKind.Key, vk, default));
        if (mode == CaptureMode.Trigger)
            foreach (var b in KeyNames.TriggerButtons) _combo.Items.Add(new Item(KeyNames.Mouse(b), TriggerKind.Mouse, 0, b));

        foreach (var t in new[] { _ctrl, _alt, _shift, _win }) { t.Checked += (_, _) => Raise(); t.Unchecked += (_, _) => Raise(); }
        _combo.SelectionChanged += (_, _) => Raise();
        _record.Click += Record_Click;
        SetRecordButton(false);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _ctrl, _alt, _shift, _win } };
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _combo, _record } };
        Content = new StackPanel { Spacing = 8, Children = { row, row2, _hint } };
        _hint.Text = mode == CaptureMode.Trigger
            ? "点「录制」后按下按键、组合键或鼠标中键 / 侧键 / 滚轮倾斜；罗技手势键等设备按键需先在设备页的「按键」里接管。"
            : "点「录制」后直接按下组合键（如 Win + Tab）。";
    }

    private static ToggleButton Mod(string text) => new() { Content = text, MinWidth = 56 };

    public InputTrigger? Value
    {
        get
        {
            var mods = (_ctrl.IsChecked == true ? KeyModifiers.Ctrl : 0) | (_alt.IsChecked == true ? KeyModifiers.Alt : 0)
                     | (_shift.IsChecked == true ? KeyModifiers.Shift : 0) | (_win.IsChecked == true ? KeyModifiers.Win : 0);
            return _combo.SelectedItem switch
            {
                Item { Kind: TriggerKind.Device } when _device is not null => Copy(_device, mods),
                Item { Kind: TriggerKind.Mouse } i => new InputTrigger { Kind = TriggerKind.Mouse, Button = i.Button, Modifiers = mods },
                Item { Kind: TriggerKind.Key } i when i.Key != 0 || mods != 0 => new InputTrigger { Kind = TriggerKind.Key, Key = i.Key, Modifiers = mods },
                _ => null,
            };
        }
        set
        {
            _setting = true;
            try
            {
                var m = value?.Modifiers ?? 0;
                _ctrl.IsChecked = m.HasFlag(KeyModifiers.Ctrl);
                _alt.IsChecked = m.HasFlag(KeyModifiers.Alt);
                _shift.IsChecked = m.HasFlag(KeyModifiers.Shift);
                _win.IsChecked = m.HasFlag(KeyModifiers.Win);
                if (value is null) { _combo.SelectedIndex = -1; return; }
                if (value.Kind == TriggerKind.Device)
                {
                    _device = Copy(value, 0);
                    var existing = _combo.Items.OfType<Item>().FirstOrDefault(i => i.Kind == TriggerKind.Device);
                    if (existing is not null) _combo.Items.Remove(existing);
                    var item = new Item("设备按键：" + (value.Name ?? value.ControlId), TriggerKind.Device, 0, default);
                    _combo.Items.Add(item);
                    _combo.SelectedItem = item;
                    return;
                }
                var match = _combo.Items.OfType<Item>().FirstOrDefault(i => i.Kind == value.Kind &&
                    (value.Kind == TriggerKind.Mouse ? i.Button == value.Button : i.Key == value.Key));
                if (match is null && value.Kind == TriggerKind.Key)
                {
                    // 列表里没有的键（如输入法键），临时加一项
                    match = new Item(KeyNames.Key(value.Key), TriggerKind.Key, value.Key, default);
                    _combo.Items.Add(match);
                }
                _combo.SelectedItem = match;
            }
            finally { _setting = false; }
        }
    }

    private static InputTrigger Copy(InputTrigger t, KeyModifiers mods) => new()
    {
        Kind = t.Kind, Key = t.Key, Button = t.Button, Modifiers = mods,
        PluginId = t.PluginId, DeviceKey = t.DeviceKey, ControlId = t.ControlId, Name = t.Name,
    };

    private void Raise() { if (!_setting) Changed?.Invoke(); }

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recording) { InputCapture.Instance.Stop(); return; }
        _recording = true;
        SetRecordButton(true);
        var oldHint = _hint.Text;
        _hint.Text = "请按下按键…（录制期间按键不会传给任何程序；再点一次取消）";
        try
        {
            var t = await InputCapture.Instance.CaptureAsync(_mode == CaptureMode.Trigger ? CaptureMode.Trigger : CaptureMode.Keys,
                held => _hint.Text = held == 0 ? "请按下按键…" : $"{KeyNames.Mods(held)} + …");
            if (t is not null) { Value = t; Changed?.Invoke(); }
        }
        finally
        {
            _recording = false;
            SetRecordButton(false);
            _hint.Text = oldHint;
        }
    }

    private void SetRecordButton(bool recording) => _record.Content = new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 8,
        Children =
        {
            new FontIcon { Glyph = recording ? "\uE71A" : "\uE7C8", FontSize = 14 },
            new TextBlock { Text = recording ? "停止" : "录制" },
        },
    };

    internal static Style Caption() => (Style)Application.Current.Resources["CaptionTextBlockStyle"];
    internal static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}

/// <summary>编辑一条映射规则的对话框。</summary>
public static class RuleDialog
{
    private static readonly (RemapActionKind Kind, string Text)[] Kinds =
    [
        (RemapActionKind.Keys, "按键 / 组合键"),
        (RemapActionKind.Mouse, "鼠标按键"),
        (RemapActionKind.Text, "输入文本"),
        (RemapActionKind.Macro, "执行宏"),
        (RemapActionKind.Run, "启动程序 / 打开网址"),
        (RemapActionKind.Disable, "禁用（吞掉这个键）"),
    ];

    /// <summary>返回编辑后的规则；取消返回 null。</summary>
    public static async Task<RemapRule?> ShowAsync(XamlRoot root, RemapRule? rule, IReadOnlyList<MacroDef> macros, bool shortcut)
    {
        var r = rule is null ? new RemapRule() : JsonSerializer.Deserialize<RemapRule>(JsonSerializer.Serialize(rule))!;

        var trigger = new KeyPicker(CaptureMode.Trigger);
        trigger.Value = rule is null ? null : r.Trigger;

        var kind = new ComboBox { MinWidth = 220 };
        foreach (var k in Kinds) kind.Items.Add(k.Text);
        kind.SelectedIndex = Math.Max(0, Array.FindIndex(Kinds, k => k.Kind == r.Action.Kind));

        var keys = new KeyPicker(CaptureMode.Keys, allowEmptyKey: true);
        if (r.Action.Kind == RemapActionKind.Keys && rule is not null)
            keys.Value = new InputTrigger { Kind = TriggerKind.Key, Key = r.Action.Key, Modifiers = r.Action.Modifiers };

        var mouse = new ComboBox { MinWidth = 220 };
        foreach (var b in KeyNames.ActionButtons) mouse.Items.Add(KeyNames.Mouse(b));
        mouse.SelectedIndex = Math.Max(0, Array.IndexOf(KeyNames.ActionButtons, r.Action.Button));

        var text = new TextBox { Text = r.Action.Text ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, PlaceholderText = "要输入的文字（支持中文、换行）" };

        var macro = new ComboBox { MinWidth = 220, PlaceholderText = macros.Count == 0 ? "还没有宏：先在「宏」分页里新建" : "选择宏" };
        foreach (var m in macros) macro.Items.Add(m.Name.Length > 0 ? m.Name : "（未命名）");
        macro.SelectedIndex = macros.ToList().FindIndex(m => m.Id == r.Action.MacroId);
        var macroHint = new TextBlock { Text = "宏执行期间再按一次触发键会停止它。", Style = KeyPicker.Caption(), Foreground = KeyPicker.Brush("TextFillColorSecondaryBrush") };

        var path = new TextBox { Text = r.Action.Path ?? "", PlaceholderText = @"程序路径、文件或网址，如 C:\Windows\notepad.exe、https://…" };
        var args = new TextBox { Text = r.Action.Args ?? "", PlaceholderText = "启动参数（可选）" };

        var actionHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void ShowAction()
        {
            actionHost.Content = Kinds[kind.SelectedIndex].Kind switch
            {
                RemapActionKind.Keys => keys,
                RemapActionKind.Mouse => mouse,
                RemapActionKind.Text => text,
                RemapActionKind.Macro => new StackPanel { Spacing = 6, Children = { macro, macroHint } },
                RemapActionKind.Run => new StackPanel { Spacing = 8, Children = { path, args } },
                _ => new TextBlock { Text = "按下触发键时什么也不做。", Foreground = KeyPicker.Brush("TextFillColorSecondaryBrush") },
            };
        }
        kind.SelectionChanged += (_, _) => ShowAction();
        ShowAction();

        var apps = new AppScopeEditor(r.Apps);
        IReadOnlyList<InputDeviceInfo> present = [];
        try { if (AppState.Instance.Client.Agent is { } agent) present = await agent.ListInputDevicesAsync(); } catch { }
        var devices = new DeviceScopeEditor(present, r.Devices);
        var devHeader = Header("仅在这些设备上生效");
        var devNote = new TextBlock
        {
            Text = "设备按键（如罗技手势键）本身就属于某个设备，不需要再选设备。",
            Style = KeyPicker.Caption(), Foreground = KeyPicker.Brush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap,
        };
        void ShowDevices()
        {
            bool device = trigger.Value?.Kind == TriggerKind.Device;
            devices.Visibility = device ? Visibility.Collapsed : Visibility.Visible;
            devNote.Visibility = device ? Visibility.Visible : Visibility.Collapsed;
        }
        trigger.Changed += ShowDevices;
        ShowDevices();
        var error = new TextBlock { Foreground = KeyPicker.Brush("SystemFillColorCriticalBrush"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        var body = new StackPanel
        {
            Spacing = 8, MinWidth = 460,
            Children =
            {
                Header("按下"), trigger,
                Header("执行"), kind, actionHost,
                Header("仅在这些程序中生效"), apps,
                devHeader, devices, devNote,
                error,
            },
        };
        var dlg = new ContentDialog
        {
            XamlRoot = root,
            Title = rule is null ? (shortcut ? "添加快捷键映射" : "添加按键映射") : "编辑映射",
            Content = new ScrollViewer { Content = body, MaxHeight = 560, Padding = new Thickness(0, 0, 16, 0) },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        dlg.PrimaryButtonClick += (_, e) =>
        {
            string? msg = null;
            var t = trigger.Value;
            var k = Kinds[kind.SelectedIndex].Kind;
            if (t is null) msg = "请选择或录制要映射的按键。";
            else if (t.Kind == TriggerKind.Key && t.Key == 0) msg = "触发键不能只有修饰键。";
            else if (k == RemapActionKind.Keys && keys.Value is null) msg = "请选择或录制目标按键。";
            else if (k == RemapActionKind.Text && text.Text.Length == 0) msg = "请输入要输入的文本。";
            else if (k == RemapActionKind.Macro && macro.SelectedIndex < 0) msg = "请选择一个宏。";
            else if (k == RemapActionKind.Run && path.Text.Trim().Length == 0) msg = "请填写要启动的程序或网址。";
            if (msg is not null)
            {
                error.Text = msg;
                error.Visibility = Visibility.Visible;
                e.Cancel = true;
            }
        };
        InputCapture.Instance.Stop();
        var result = await dlg.ShowAsync();
        if (InputCapture.Instance.Active) InputCapture.Instance.Stop();
        if (result != ContentDialogResult.Primary) return null;

        r.Trigger = trigger.Value!;
        var ak = Kinds[kind.SelectedIndex].Kind;
        var kv = keys.Value;
        r.Action = new RemapAction
        {
            Kind = ak,
            Key = ak == RemapActionKind.Keys ? kv?.Key ?? 0 : 0,
            Modifiers = ak == RemapActionKind.Keys ? kv?.Modifiers ?? 0 : 0,
            Button = ak == RemapActionKind.Mouse ? KeyNames.ActionButtons[Math.Max(0, mouse.SelectedIndex)] : default,
            Text = ak == RemapActionKind.Text ? text.Text.Replace("\r\n", "\n").Replace('\r', '\n') : null,
            MacroId = ak == RemapActionKind.Macro && macro.SelectedIndex >= 0 ? macros[macro.SelectedIndex].Id : null,
            Path = ak == RemapActionKind.Run ? path.Text.Trim() : null,
            Args = ak == RemapActionKind.Run && args.Text.Trim().Length > 0 ? args.Text.Trim() : null,
        };
        r.Apps = apps.Value;
        r.Devices = r.Trigger.Kind == TriggerKind.Device ? null : devices.Value;
        return r;
    }

    internal static TextBlock Header(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        Margin = new Thickness(0, 12, 0, 0),
    };
}

/// <summary>编辑宏的对话框：步骤列表（上移 / 下移 / 删除）、添加步骤、录制。</summary>
public static class MacroDialog
{
    private static readonly (MacroStepKind Kind, string Text)[] Kinds =
    [
        (MacroStepKind.KeyTap, "按键 / 组合键（按下并抬起）"),
        (MacroStepKind.KeyDown, "按下按键"),
        (MacroStepKind.KeyUp, "抬起按键"),
        (MacroStepKind.Text, "输入文本"),
        (MacroStepKind.Delay, "等待"),
        (MacroStepKind.MouseClick, "鼠标点击"),
        (MacroStepKind.MouseDown, "按下鼠标键"),
        (MacroStepKind.MouseUp, "抬起鼠标键"),
    ];

    public static async Task<MacroDef?> ShowAsync(XamlRoot root, MacroDef? macro)
    {
        var m = macro is null ? new MacroDef() : JsonSerializer.Deserialize<MacroDef>(JsonSerializer.Serialize(macro))!;
        var steps = m.Steps.ToList();

        var name = new TextBox { Text = m.Name, PlaceholderText = "宏的名称，如「填写签名」" };
        var repeat = new NumberBox { Value = Math.Max(1, m.Repeat), Minimum = 1, Maximum = 10000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, MinWidth = 140, Header = "执行次数" };

        var list = new StackPanel { Spacing = 2 };
        var listScroll = new ScrollViewer { Content = list, MaxHeight = 240, Padding = new Thickness(0, 0, 12, 0) };
        var empty = new TextBlock { Text = "还没有步骤。在下面添加，或点「录制」直接按键。", Foreground = KeyPicker.Brush("TextFillColorSecondaryBrush") };

        void Render()
        {
            list.Children.Clear();
            empty.Visibility = steps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < steps.Count; i++)
            {
                int idx = i;
                var row = new Grid { ColumnSpacing = 4, Padding = new Thickness(8, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                for (int c = 0; c < 3; c++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var no = new TextBlock { Text = $"{i + 1}", Foreground = KeyPicker.Brush("TextFillColorTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center };
                var txt = new TextBlock { Text = KeyNames.Step(steps[i]), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(txt, 1);
                row.Children.Add(no);
                row.Children.Add(txt);
                AddIcon(row, 2, "\uE70E", "上移", idx > 0, () => { (steps[idx - 1], steps[idx]) = (steps[idx], steps[idx - 1]); Render(); });
                AddIcon(row, 3, "\uE70D", "下移", idx < steps.Count - 1, () => { (steps[idx + 1], steps[idx]) = (steps[idx], steps[idx + 1]); Render(); });
                AddIcon(row, 4, "\uE711", "删除", true, () => { steps.RemoveAt(idx); Render(); });
                list.Children.Add(row);
            }
        }

        // —— 添加步骤 ——
        var kind = new ComboBox { MinWidth = 240 };
        foreach (var k in Kinds) kind.Items.Add(k.Text);
        kind.SelectedIndex = 0;
        var keyPicker = new KeyPicker(CaptureMode.Keys);
        var stepText = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, PlaceholderText = "要输入的文字" };
        var ms = new NumberBox { Value = 100, Minimum = 0, Maximum = 600000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, MinWidth = 160, Header = "毫秒" };
        var button = new ComboBox { MinWidth = 220 };
        foreach (var b in KeyNames.ActionButtons) button.Items.Add(KeyNames.Mouse(b));
        button.SelectedIndex = 0;
        var paramHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void ShowParams() => paramHost.Content = Kinds[kind.SelectedIndex].Kind switch
        {
            MacroStepKind.KeyTap or MacroStepKind.KeyDown or MacroStepKind.KeyUp => keyPicker,
            MacroStepKind.Text => stepText,
            MacroStepKind.Delay => ms,
            _ => button,
        };
        kind.SelectionChanged += (_, _) => ShowParams();
        ShowParams();
        var addError = new TextBlock { Foreground = KeyPicker.Brush("SystemFillColorCriticalBrush"), Visibility = Visibility.Collapsed };
        var add = new Button { Content = "添加步骤" };
        add.Click += (_, _) =>
        {
            var k = Kinds[kind.SelectedIndex].Kind;
            var step = new MacroStep { Kind = k };
            switch (k)
            {
                case MacroStepKind.KeyTap or MacroStepKind.KeyDown or MacroStepKind.KeyUp:
                    if (keyPicker.Value is not { } t) { Fail("请选择按键。"); return; }
                    step.Key = t.Key;
                    if (k == MacroStepKind.KeyTap) step.Modifiers = t.Modifiers;
                    else if (t.Key == 0) { Fail("按下 / 抬起需要一个具体的键。"); return; }
                    break;
                case MacroStepKind.Text:
                    if (stepText.Text.Length == 0) { Fail("请输入文本。"); return; }
                    step.Text = stepText.Text.Replace("\r\n", "\n").Replace('\r', '\n');
                    break;
                case MacroStepKind.Delay:
                    step.Ms = double.IsNaN(ms.Value) ? 0 : (int)ms.Value;
                    break;
                default:
                    step.Button = KeyNames.ActionButtons[Math.Max(0, button.SelectedIndex)];
                    break;
            }
            addError.Visibility = Visibility.Collapsed;
            steps.Add(step);
            Render();
            listScroll.ChangeView(null, listScroll.ScrollableHeight + 1000, null);
        };
        void Fail(string msg) { addError.Text = msg; addError.Visibility = Visibility.Visible; }

        // —— 录制 ——
        var recordDelays = new CheckBox { Content = "录制按键之间的间隔", IsChecked = true };
        var record = new Button();
        var recordHint = new TextBlock { Style = KeyPicker.Caption(), Foreground = KeyPicker.Brush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap,
            Text = "录制时按下的键会依次加到步骤末尾，并且不会传给其他程序（用鼠标点「停止」结束，最长 2 分钟）。" };
        bool recording = false;
        int recordStart = 0;
        void SetRecord(bool on) => record.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { new FontIcon { Glyph = on ? "\uE71A" : "\uE7C8", FontSize = 14 }, new TextBlock { Text = on ? "停止录制" : "录制" } },
        };
        SetRecord(false);
        record.Click += async (_, _) =>
        {
            if (recording) { InputCapture.Instance.Stop(); return; }
            recording = true;
            recordStart = steps.Count;
            SetRecord(true);
            await InputCapture.Instance.StartMacroAsync(
                s =>
                {
                    if (s.Kind == MacroStepKind.Delay && recordDelays.IsChecked != true) return;
                    if (s.Kind == MacroStepKind.Delay && steps.Count == recordStart) return; // 开头的等待没有意义
                    steps.Add(s);
                    Render();
                    listScroll.ChangeView(null, listScroll.ScrollableHeight + 1000, null);
                },
                () =>
                {
                    recording = false;
                    SetRecord(false);
                    Compact(steps, recordStart);
                    Render();
                });
        };

        var clear = new Button { Content = "清空步骤" };
        clear.Click += (_, _) => { steps.Clear(); Render(); };

        Render();
        var body = new StackPanel
        {
            Spacing = 8, MinWidth = 500,
            Children =
            {
                name, repeat,
                RuleDialog.Header("步骤"),
                empty, listScroll,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { record, recordDelays, clear } },
                recordHint,
                RuleDialog.Header("添加步骤"),
                kind, paramHost, add, addError,
            },
        };
        var dlg = new ContentDialog
        {
            XamlRoot = root,
            Title = macro is null ? "新建宏" : "编辑宏",
            Content = new ScrollViewer { Content = body, MaxHeight = 600, Padding = new Thickness(0, 0, 16, 0) },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        dlg.PrimaryButtonClick += (_, e) =>
        {
            if (name.Text.Trim().Length == 0) { Fail("请给宏起个名字。"); e.Cancel = true; }
            else if (steps.Count == 0) { Fail("宏至少要有一个步骤。"); e.Cancel = true; }
        };
        var result = await dlg.ShowAsync();
        if (recording) InputCapture.Instance.Stop();
        if (result != ContentDialogResult.Primary) return null;
        m.Name = name.Text.Trim();
        m.Repeat = double.IsNaN(repeat.Value) ? 1 : Math.Max(1, (int)repeat.Value);
        m.Steps = steps;
        return m;
    }

    /// <summary>把录到的"按下 X、（间隔）、抬起 X"合并成一次"按键 X"，步骤列表读起来短很多。</summary>
    private static void Compact(List<MacroStep> steps, int from)
    {
        for (int i = from; i < steps.Count; i++)
        {
            if (steps[i].Kind != MacroStepKind.KeyDown) continue;
            int j = i + 1;
            if (j < steps.Count && steps[j].Kind == MacroStepKind.Delay) j++;
            if (j < steps.Count && steps[j].Kind == MacroStepKind.KeyUp && steps[j].Key == steps[i].Key)
            {
                steps[i] = new MacroStep { Kind = MacroStepKind.KeyTap, Key = steps[i].Key };
                steps.RemoveRange(i + 1, j - i);
            }
        }
    }

    private static void AddIcon(Grid row, int col, string glyph, string tip, bool enabled, Action click)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Padding = new Thickness(8, 6, 8, 6),
            IsEnabled = enabled,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(b, tip);
        b.Click += (_, _) => click();
        Grid.SetColumn(b, col);
        row.Children.Add(b);
    }
}
