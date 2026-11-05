using DeviceHub.App.Services;
using DeviceHub.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace DeviceHub.App.Pages;

/// <summary>
/// 工具 › 按键映射（参照 PowerToys 键盘管理器）：按键 / 快捷键 / 宏三个分页。
/// 配置保存在 Agent，改完立即生效；规则执行在 Agent 的低级钩子里。
/// </summary>
public sealed partial class RemapPage : Page
{
    private RemapConfig _config = new();
    private bool _loading;

    public RemapPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e) => await LoadAsync();

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (InputCapture.Instance.Active) InputCapture.Instance.Stop();
    }

    private async Task LoadAsync()
    {
        if (AppState.Instance.Client.Agent is not { } agent)
        {
            ShowBar("未连接 Agent", "按键映射由后台 Agent 执行，请先确认 Agent 在运行。", InfoBarSeverity.Error);
            return;
        }
        try
        {
            _config = await agent.GetRemapConfigAsync();
            _loading = true;
            EnabledToggle.IsOn = _config.Enabled;
            _loading = false;
            Render();
            await CheckEngineAsync();
        }
        catch (Exception ex) { ShowBar("读取失败", ex.Message, InfoBarSeverity.Error); }
    }

    private async Task CheckEngineAsync()
    {
        if (AppState.Instance.Client.Agent is not { } agent) return;
        var err = await agent.GetRemapEngineErrorAsync();
        if (err is not null) ShowBar("按键映射未能启动", err, InfoBarSeverity.Error);
        else ShowBar("提示", "以管理员身份运行的程序在前台时，映射对它不生效（Windows 限制）。", InfoBarSeverity.Informational, closable: true);
    }

    private void ShowBar(string title, string msg, InfoBarSeverity sev, bool closable = false)
    {
        EngineBar.Title = title;
        EngineBar.Message = msg;
        EngineBar.Severity = sev;
        EngineBar.IsClosable = closable;
        EngineBar.IsOpen = true;
    }

    private async Task SaveAsync()
    {
        if (AppState.Instance.Client.Agent is not { } agent) return;
        try
        {
            await agent.SaveRemapConfigAsync(_config);
            await CheckEngineAsync();
        }
        catch (Exception ex) { ShowBar("保存失败", ex.Message, InfoBarSeverity.Error); }
    }

    private async void EnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _config.Enabled = EnabledToggle.IsOn;
        Render();
        await SaveAsync();
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => Render();

    private bool IsShortcut(RemapRule r) => r.Trigger.Modifiers != 0;

    // ———————————————— 列表 ————————————————

    private void Render()
    {
        List.Children.Clear();
        List.Opacity = _config.Enabled ? 1 : 0.6;
        var tab = Tabs.SelectedItem;
        if (tab == MacrosTab)
        {
            AddText.Text = "新建宏";
            TabHint.Text = "宏是一串按键、文本、鼠标点击和等待。在「按键」或「快捷键」分页里把某个按键映射为「执行宏」来触发它。";
            foreach (var m in _config.Macros) List.Children.Add(MacroRow(m));
            Empty.Text = "还没有宏。";
            Empty.Visibility = _config.Macros.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        bool shortcuts = tab == ShortcutsTab;
        AddText.Text = shortcuts ? "添加快捷键映射" : "添加按键映射";
        TabHint.Text = shortcuts
            ? "按下组合键（如 Ctrl + Shift + K、Ctrl + 鼠标侧键）时执行动作。可以只在指定程序里生效。"
            : "把一个键或鼠标键换成别的：无论同时按着什么修饰键都生效（A → B 时 Ctrl + A 会变成 Ctrl + B）。";
        var rules = _config.Rules.Where(r => IsShortcut(r) == shortcuts).ToList();
        foreach (var r in rules) List.Children.Add(RuleRow(r));
        Empty.Text = shortcuts ? "还没有快捷键映射。" : "还没有按键映射。";
        Empty.Visibility = rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement RuleRow(RemapRule r)
    {
        var from = Chip(KeyNames.Trigger(r.Trigger), r.Trigger.Kind switch { TriggerKind.Mouse => "", TriggerKind.Device => "", _ => "" });
        var arrow = new FontIcon { Glyph = "", FontSize = 14, Foreground = Res("TextFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center };
        var to = Chip(KeyNames.Action(r.Action, _config.Macros), r.Action.Kind switch
        {
            RemapActionKind.Mouse => "", RemapActionKind.Text => "", RemapActionKind.Macro => "",
            RemapActionKind.Run => "", RemapActionKind.Disable => "", _ => "",
        });
        var main = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { from, arrow, to } };
        var scope = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(r.Apps) ? "所有程序" : "仅在 " + r.Apps,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = Res("TextFillColorSecondaryBrush"),
        };
        var left = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { main, scope } };

        var toggle = new ToggleSwitch { IsOn = r.Enabled, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        toggle.Toggled += async (_, _) => { r.Enabled = toggle.IsOn; await SaveAsync(); };
        var edit = IconButton("", "编辑", async () =>
        {
            var updated = await RuleDialog.ShowAsync(XamlRoot, r, _config.Macros, IsShortcut(r));
            if (updated is null) return;
            _config.Rules[_config.Rules.IndexOf(r)] = updated;
            SwitchToRuleTab(updated);
            await SaveAsync();
        });
        var del = IconButton("", "删除", async () =>
        {
            _config.Rules.Remove(r);
            Render();
            await SaveAsync();
        });
        return Card(left, toggle, edit, del, r.Enabled ? 1 : 0.55);
    }

    private UIElement MacroRow(MacroDef m)
    {
        int used = _config.Rules.Count(r => r.Action.Kind == RemapActionKind.Macro && r.Action.MacroId == m.Id);
        var title = new TextBlock { Text = m.Name.Length > 0 ? m.Name : "（未命名）", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        var preview = string.Join("，", m.Steps.Take(4).Select(KeyNames.Step)) + (m.Steps.Count > 4 ? "…" : "");
        var detail = new TextBlock
        {
            Text = $"{m.Steps.Count} 步{(m.Repeat > 1 ? $" × {m.Repeat} 次" : "")} · {(used > 0 ? $"被 {used} 个映射使用" : "未被映射使用")} · {preview}",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = Res("TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var left = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { title, detail } };
        var edit = IconButton("", "编辑", async () =>
        {
            var updated = await MacroDialog.ShowAsync(XamlRoot, m);
            if (updated is null) return;
            _config.Macros[_config.Macros.IndexOf(m)] = updated;
            Render();
            await SaveAsync();
        });
        var del = IconButton("", "删除", async () =>
        {
            if (used > 0)
            {
                var dlg = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = $"删除宏「{m.Name}」？",
                    Content = $"有 {used} 个映射用到这个宏，删除后它们会一并删除。",
                    PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
                };
                if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
                _config.Rules.RemoveAll(r => r.Action.Kind == RemapActionKind.Macro && r.Action.MacroId == m.Id);
            }
            _config.Macros.Remove(m);
            Render();
            await SaveAsync();
        });
        return Card(left, null, edit, del, 1);
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.SelectedItem == MacrosTab)
        {
            var m = await MacroDialog.ShowAsync(XamlRoot, null);
            if (m is null) return;
            _config.Macros.Add(m);
            Render();
            await SaveAsync();
            return;
        }
        var r = await RuleDialog.ShowAsync(XamlRoot, null, _config.Macros, Tabs.SelectedItem == ShortcutsTab);
        if (r is null) return;
        // 同一个触发键（同样的程序范围）只留一条，新规则替换旧规则
        _config.Rules.RemoveAll(x => x.Trigger.SameSource(r.Trigger) && (x.Apps ?? "") == (r.Apps ?? ""));
        _config.Rules.Add(r);
        SwitchToRuleTab(r);
        await SaveAsync();
    }

    /// <summary>录到的是组合键就切到「快捷键」分页（反之亦然），让用户看到刚加的规则。</summary>
    private void SwitchToRuleTab(RemapRule r)
    {
        var want = IsShortcut(r) ? ShortcutsTab : KeysTab;
        if (Tabs.SelectedItem != want) Tabs.SelectedItem = want; // SelectionChanged 会 Render
        else Render();
    }

    // ———————————————— 小部件 ————————————————

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private static Border Chip(string text, string glyph) => new()
    {
        Padding = new Thickness(10, 4, 10, 4),
        CornerRadius = new CornerRadius(4),
        Background = Res("ControlFillColorSecondaryBrush"),
        BorderBrush = Res("ControlStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1),
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                new FontIcon { Glyph = glyph, FontSize = 14, Foreground = Res("TextFillColorSecondaryBrush") },
                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 320, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        },
    };

    private static Button IconButton(string glyph, string tip, Func<Task> click)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Padding = new Thickness(10, 8, 10, 8),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(b, tip);
        b.Click += async (_, _) => await click();
        return b;
    }

    private static Border Card(UIElement left, UIElement? toggle, UIElement edit, UIElement del, double opacity)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(left);
        if (toggle is not null) { Grid.SetColumn((FrameworkElement)toggle, 1); grid.Children.Add(toggle); }
        Grid.SetColumn((FrameworkElement)edit, 2); grid.Children.Add(edit);
        Grid.SetColumn((FrameworkElement)del, 3); grid.Children.Add(del);
        left.Opacity = opacity;
        return new Border
        {
            Padding = new Thickness(16, 12, 12, 12),
            MinHeight = 68,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = Res("CardStrokeColorDefaultBrush"),
            Background = Res("CardBackgroundFillColorDefaultBrush"),
            Child = grid,
        };
    }
}
