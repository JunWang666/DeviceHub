using DeviceHub.App.Services;
using DeviceHub.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace DeviceHub.App.Pages;

/// <summary>
/// 可自定义的主页：一组小组件卡片（核心内置 + 插件提供）。
/// 核心画卡片外框（设备名、图标、菜单），插件经 IPluginWidgetProvider 只提供内容；
/// 布局（顺序、尺寸）存在界面本地，见 <see cref="HomeLayoutStore"/>。
/// </summary>
public sealed partial class HomePage : Page
{
    private readonly AppState _state = AppState.Instance;
    private List<WidgetPlacement> _items = new();
    private readonly Dictionary<string, Card> _cards = new();
    /// <summary>用户改过布局（或已有布局文件）；否则每次刷新都按当前设备重新生成默认布局。</summary>
    private bool _customized;
    private bool _editing;
    private string? _dragId;

    private sealed class Card
    {
        public required WidgetPlacement P;
        public required Border Root;
        public required FontIcon Icon;
        public required TextBlock Title;
        public required ContentControl Body;
        public required Button Menu;
        public string Signature = "";
        public PluginUiBridge? Bridge;

        public void DisposeBridge() { Bridge?.Dispose(); Bridge = null; }
    }

    public HomePage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _state.DevicesRefreshed += Refresh;
            var saved = HomeLayoutStore.Load();
            _customized = saved is not null;
            _items = saved ?? DefaultLayout();
            Sync();
        };
        Unloaded += (_, _) =>
        {
            _state.DevicesRefreshed -= Refresh;
            foreach (var c in _cards.Values) c.DisposeBridge();
            _cards.Clear();
        };
    }

    // —— 布局 ——

    /// <summary>默认布局：设备总览 + 每台设备的第一个可用小组件。</summary>
    private List<WidgetPlacement> DefaultLayout()
    {
        var list = new List<WidgetPlacement> { new() { WidgetId = BuiltinWidgets.Devices, Size = BuiltinWidgets.DevicesInfo.DefaultSize } };
        foreach (var dev in _state.ManagedDevices)
        {
            if (dev.OwnerPluginId is not { } pid || _state.Plugins.FirstOrDefault(p => p.Id == pid) is not { } plugin) continue;
            if (plugin.Widgets.FirstOrDefault(w => Applies(w, dev)) is not { } w) continue;
            // 默认 Id 固定，重新生成默认布局时卡片不会每次都重建
            list.Add(new() { Id = $"default:{pid}:{w.Id}:{dev.DeviceKey}", PluginId = pid, WidgetId = w.Id, DeviceKey = dev.DeviceKey, Size = w.DefaultSize });
        }
        return list;
    }

    private static bool Applies(WidgetInfo w, DeviceInfo dev) => w.Targets switch
    {
        "child" => dev.ParentKey is not null,
        "physical" => dev.ParentKey is null,
        _ => true,
    };

    private void Save()
    {
        _customized = true;
        HomeLayoutStore.Save(_items);
    }

    private void Refresh()
    {
        if (!_customized) _items = DefaultLayout();
        Sync();
    }

    /// <summary>让面板与 _items 一致：复用已有卡片，只在顺序变了时重排，只在可用性/尺寸变了时重建内容。</summary>
    private void Sync()
    {
        var ids = _items.Select(i => i.Id).ToHashSet();
        foreach (var id in _cards.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            _cards[id].DisposeBridge();
            _cards.Remove(id);
        }

        var roots = new List<UIElement>();
        foreach (var p in _items)
        {
            if (!_cards.TryGetValue(p.Id, out var card)) _cards[p.Id] = card = CreateCard(p);
            card.P = p;
            UpdateCard(card);
            roots.Add(card.Root);
        }
        if (!Board.Children.SequenceEqual(roots))
        {
            Board.Children.Clear();
            foreach (var r in roots) Board.Children.Add(r);
        }
        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HintText.Text = _editing ? "拖动卡片排序；用卡片右上角的菜单调整大小或移除。" : "右键小组件可调整大小、排序或移除。";
    }

    // —— 卡片 ——

    private Card CreateCard(WidgetPlacement p)
    {
        var icon = new FontIcon { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        };
        var menu = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(6, 2, 6, 2),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(menu, "更多");
        var head = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 8), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(icon);
        Grid.SetColumn(title, 1); head.Children.Add(title);
        Grid.SetColumn(menu, 2); head.Children.Add(menu);

        var body = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(head);
        Grid.SetRow(body, 1); grid.Children.Add(body);

        var root = new Border { Style = (Style)Resources["WidgetCardStyle"], Child = grid, AllowDrop = true };
        var card = new Card { P = p, Root = root, Icon = icon, Title = title, Body = body, Menu = menu };

        menu.Flyout = Menu(card);
        root.ContextFlyout = Menu(card);
        head.Tapped += (_, _) => { if (!_editing) OpenDevice(card.P.DeviceKey); };

        // 编辑模式下拖动排序：拖到哪张卡片上，就插到它的位置。
        root.DragStarting += (_, e) =>
        {
            _dragId = card.P.Id;
            e.Data.SetText(card.P.Id);
            e.Data.RequestedOperation = DataPackageOperation.Move;
        };
        root.DropCompleted += (_, _) => _dragId = null;
        root.DragOver += (_, e) =>
        {
            if (_dragId is null || _dragId == card.P.Id) return;
            e.AcceptedOperation = DataPackageOperation.Move;
            e.DragUIOverride.Caption = "移到这里";
        };
        root.Drop += (_, _) =>
        {
            if (_dragId is null || _dragId == card.P.Id) return;
            var moving = _items.FirstOrDefault(i => i.Id == _dragId);
            if (moving is null) return;
            int to = _items.IndexOf(card.P);
            _items.Remove(moving);
            _items.Insert(Math.Clamp(to, 0, _items.Count), moving);
            Save();
            Sync();
        };
        return card;
    }

    private void UpdateCard(Card card)
    {
        var p = card.P;
        var (title, glyph, tip, info, dev, plugin) = Resolve(p);
        if (info is not null && !info.Sizes.Contains(p.Size)) p.Size = info.DefaultSize;

        VariableSizedWrapGrid.SetColumnSpan(card.Root, p.Size == WidgetSize.Small ? 1 : 2);
        VariableSizedWrapGrid.SetRowSpan(card.Root, p.Size == WidgetSize.Large ? 2 : 1);
        card.Title.Text = title;
        card.Icon.Glyph = glyph;
        ToolTipService.SetToolTip(card.Title, tip);
        double headOpacity = dev is { Connected: false } ? 0.6 : 1;
        card.Title.Opacity = card.Icon.Opacity = headOpacity;

        card.Menu.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
        card.Root.CanDrag = _editing;
        card.Body.IsHitTestVisible = !_editing; // 编辑时不误触插件里的开关
        card.Root.BorderBrush = _editing ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"] : null;
        if (!_editing) card.Root.ClearValue(Border.BorderBrushProperty);

        if (p.PluginId is null)
        {
            // 内置小组件很轻，每次刷新都重画（设备名、在线状态会变）
            card.Body.Content = p.WidgetId == BuiltinWidgets.Devices ? DevicesContent() : Placeholder("这个小组件已不再提供");
            return;
        }

        var sig = $"{plugin?.Version}|{plugin?.UiAssemblyPath}|{info is not null}|{dev is not null}|{p.Size}";
        if (sig == card.Signature) return;
        card.Signature = sig;
        card.DisposeBridge();
        card.Body.Content = PluginContent(card, plugin, info, dev);
    }

    private (string Title, string Glyph, string Tip, WidgetInfo? Info, DeviceInfo? Dev, PluginInfo? Plugin) Resolve(WidgetPlacement p)
    {
        if (p.PluginId is null)
        {
            var bi = BuiltinWidgets.Find(p.WidgetId);
            return (bi?.Title ?? "小组件", bi?.Glyph ?? Glyphs.Device, bi?.Description ?? "", bi, null, null);
        }
        var plugin = _state.Plugins.FirstOrDefault(x => x.Id == p.PluginId);
        var info = plugin?.Widgets.FirstOrDefault(w => w.Id == p.WidgetId);
        var dev = _state.ManagedDevices.FirstOrDefault(d => d.DeviceKey == p.DeviceKey);
        var glyph = Glyphs.IsValid(dev?.Glyph) ? dev!.Glyph! : info?.Glyph ?? Glyphs.Device;
        var tip = $"{info?.Title ?? p.WidgetId} · {plugin?.DisplayName ?? p.PluginId}";
        return (dev?.DisplayName ?? "设备已移除", glyph, tip, info, dev, plugin);
    }

    private FrameworkElement PluginContent(Card card, PluginInfo? plugin, WidgetInfo? info, DeviceInfo? dev)
    {
        if (plugin is null) return Placeholder("插件未安装");
        if (info is null) return Placeholder("插件已不再提供这个小组件");
        if (dev is null) return Placeholder("设备已不再由插件管理");
        if (plugin.UiAssemblyPath is null || plugin.UiWidgetTypeName is null) return Placeholder("插件没有提供界面");
        try
        {
            card.Bridge = new PluginUiBridge(_state.Client, plugin.Id, dev.DeviceKey);
            return PluginUiLoader.LoadWidget(plugin.UiAssemblyPath, plugin.UiWidgetTypeName, info.Id, card.P.Size, card.Bridge);
        }
        catch (Exception ex)
        {
            card.DisposeBridge();
            return Placeholder("小组件加载失败", ex.GetBaseException().Message);
        }
    }

    private static FrameworkElement Placeholder(string text, string? detail = null)
    {
        var sp = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Secondary });
        if (detail is not null)
            sp.Children.Add(new TextBlock
            {
                Text = detail, TextWrapping = TextWrapping.Wrap, MaxLines = 3, Foreground = Tertiary,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            });
        return sp;
    }

    private static Brush Secondary => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    private static Brush Tertiary => (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

    /// <summary>内置"设备总览"：每台正在管理的设备一行（子设备缩进），点击进入设备页。</summary>
    private FrameworkElement DevicesContent()
    {
        var devs = _state.ManagedDevices.ToList();
        if (devs.Count == 0) return Placeholder("还没有设备", "在「新设备」里添加后会出现在这里");
        var panel = new StackPanel { Spacing = 2 };
        foreach (var d in devs)
        {
            var row = new Grid
            {
                ColumnSpacing = 10, Padding = new Thickness(d.ParentKey is null ? 4 : 22, 4, 4, 4), CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Opacity = d.Connected ? 1 : 0.6,
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new FontIcon { Glyph = Glyphs.IsValid(d.Glyph) ? d.Glyph : Glyphs.Device, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = d.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                Text = d.Connected ? (string.IsNullOrEmpty(d.Subtitle) ? "已连接" : d.Subtitle) : "未连接",
                TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Secondary,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            });
            Grid.SetColumn(text, 1); row.Children.Add(text);
            var key = d.DeviceKey;
            row.PointerEntered += (_, _) => row.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
            row.PointerExited += (_, _) => row.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            row.Tapped += (_, _) => { if (!_editing) OpenDevice(key); };
            panel.Children.Add(row);
        }
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(-4, 0, -4, 0) };
    }

    private static void OpenDevice(string? deviceKey)
    {
        if (deviceKey is not null && App.MainWindow is MainWindow mw) mw.NavigateToManagedDevice(deviceKey);
    }

    // —— 卡片菜单（右键 / 编辑模式的 … 按钮）——

    private MenuFlyout Menu(Card card)
    {
        var flyout = new MenuFlyout();
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            var p = card.P;
            var (_, _, _, info, dev, _) = Resolve(p);
            if (dev is not null)
            {
                var open = new MenuFlyoutItem { Text = "打开设备页", Icon = new FontIcon { Glyph = "" } };
                open.Click += (_, _) => OpenDevice(p.DeviceKey);
                flyout.Items.Add(open);
                flyout.Items.Add(new MenuFlyoutSeparator());
            }
            var sizes = info?.Sizes ?? [WidgetSize.Small];
            if (sizes.Count > 1)
            {
                foreach (var s in sizes)
                {
                    var item = new RadioMenuFlyoutItem { Text = SizeName(s), GroupName = "size" + p.Id, IsChecked = s == p.Size };
                    item.Click += (_, _) => { if (p.Size == s) return; p.Size = s; Save(); Sync(); };
                    flyout.Items.Add(item);
                }
                flyout.Items.Add(new MenuFlyoutSeparator());
            }
            int idx = _items.IndexOf(p);
            var left = new MenuFlyoutItem { Text = "前移", Icon = new FontIcon { Glyph = "" }, IsEnabled = idx > 0 };
            left.Click += (_, _) => Move(p, -1);
            var right = new MenuFlyoutItem { Text = "后移", Icon = new FontIcon { Glyph = "" }, IsEnabled = idx >= 0 && idx < _items.Count - 1 };
            right.Click += (_, _) => Move(p, +1);
            flyout.Items.Add(left);
            flyout.Items.Add(right);
            flyout.Items.Add(new MenuFlyoutSeparator());
            var remove = new MenuFlyoutItem { Text = "移除", Icon = new FontIcon { Glyph = "" } };
            remove.Click += (_, _) => { _items.Remove(p); Save(); Sync(); };
            flyout.Items.Add(remove);
        };
        return flyout;
    }

    private static string SizeName(WidgetSize s) => s switch { WidgetSize.Wide => "宽", WidgetSize.Large => "大", _ => "小" };

    private void Move(WidgetPlacement p, int delta)
    {
        int i = _items.IndexOf(p), j = i + delta;
        if (i < 0 || j < 0 || j >= _items.Count) return;
        (_items[i], _items[j]) = (_items[j], _items[i]);
        Save();
        Sync();
    }

    // —— 顶部按钮 ——

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        _editing = EditToggle.IsChecked == true;
        EditText.Text = _editing ? "完成" : "编辑";
        Sync();
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var options = Options();
        if (options.Count == 0)
        {
            await new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "没有可添加的小组件", CloseButtonText = "好",
                Content = "可用的小组件都已经在主页上了。添加设备、或安装提供小组件的插件后，这里会出现更多选择。",
            }.ShowAsync();
            return;
        }
        var list = new ListView
        {
            ItemsSource = options, ItemTemplate = (DataTemplate)Resources["OptionTemplate"],
            SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 440, MinWidth = 380,
        };
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "添加小组件", Content = list,
            PrimaryButtonText = "添加", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        list.SelectionChanged += (_, _) => dlg.IsPrimaryButtonEnabled = list.SelectedItems.Count > 0;
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        foreach (var o in list.SelectedItems.OfType<WidgetOption>())
            _items.Add(new WidgetPlacement { PluginId = o.PluginId, WidgetId = o.WidgetId, DeviceKey = o.DeviceKey, Size = o.Size });
        Save();
        Sync();
    }

    /// <summary>还没放到主页上的（设备 × 小组件）组合，内置的排在前面。</summary>
    private List<WidgetOption> Options()
    {
        bool Placed(string? plugin, string widget, string? device) =>
            _items.Any(i => i.PluginId == plugin && i.WidgetId == widget && i.DeviceKey == device);

        var list = new List<WidgetOption>();
        var bi = BuiltinWidgets.DevicesInfo;
        if (!Placed(null, bi.Id, null))
            list.Add(new WidgetOption { Title = bi.Title, Subtitle = bi.Description ?? "", Glyph = bi.Glyph!, WidgetId = bi.Id, Size = bi.DefaultSize });

        foreach (var dev in _state.ManagedDevices)
        {
            if (dev.OwnerPluginId is not { } pid || _state.Plugins.FirstOrDefault(p => p.Id == pid) is not { } plugin) continue;
            foreach (var w in plugin.Widgets.Where(w => Applies(w, dev) && !Placed(pid, w.Id, dev.DeviceKey)))
                list.Add(new WidgetOption
                {
                    Title = $"{dev.DisplayName} · {w.Title}",
                    Subtitle = string.IsNullOrEmpty(w.Description) ? plugin.DisplayName : $"{w.Description}（{plugin.DisplayName}）",
                    Glyph = w.Glyph ?? (Glyphs.IsValid(dev.Glyph) ? dev.Glyph! : Glyphs.Device),
                    PluginId = pid, WidgetId = w.Id, DeviceKey = dev.DeviceKey, Size = w.DefaultSize,
                });
        }
        return list;
    }
}
