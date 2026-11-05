using DeviceHub.App.Services;
using DeviceHub.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DeviceHub.App.Pages;

/// <summary>USB 拓扑与诊断（核心功能，只读，不涉及插件与授权）。</summary>
public sealed partial class UsbPage : Page
{
    private UsbTopology? _topology;
    private List<UsbIssueRow> _issues = new();
    private int _scan;
    private bool _dialogOpen;

    public UsbPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        AppState.Instance.Client.UsbChanged += OnUsbChanged;
        await ScanAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => AppState.Instance.Client.UsbChanged -= OnUsbChanged;

    private void OnUsbChanged() => AppState.Instance.OnUi(() => _ = ScanAsync());

    private async void Refresh_Click(object s, RoutedEventArgs e) => await ScanAsync();

    private void EmptyToggle_Toggled(object s, RoutedEventArgs e) => Render();

    private async Task ScanAsync()
    {
        if (AppState.Instance.Client.Agent is not { } agent) return;
        var scan = ++_scan;
        Busy.IsActive = true;
        try
        {
            var topo = await agent.GetUsbTopologyAsync();
            if (scan != _scan) return; // 期间又触发了一次扫描，以后者为准
            _topology = topo;
            Render();
        }
        catch (Exception ex)
        {
            Summary.Severity = InfoBarSeverity.Error;
            Summary.Title = "扫描失败";
            Summary.Message = ex.Message;
            IssuesButton.Visibility = Visibility.Collapsed;
            Summary.IsOpen = true;
        }
        finally { if (scan == _scan) Busy.IsActive = false; }
    }

    private void Render()
    {
        if (_topology is null) return;
        var issues = new List<(UsbRow Row, UsbIssue Issue)>();
        var roots = UsbTreeBuilder.Build(_topology, EmptyToggle.IsOn, issues);
        Tree.ItemsSource = roots;

        _issues = issues.OrderByDescending(i => i.Issue.Level).Select(i => new UsbIssueRow { Row = i.Row, Issue = i.Issue }).ToList();
        int errors = _issues.Count(i => i.Issue.Level == UsbIssueLevel.Error);
        int warnings = _issues.Count(i => i.Issue.Level == UsbIssueLevel.Warning);
        if (_issues.Count == 0)
        {
            Summary.Severity = InfoBarSeverity.Success;
            Summary.Title = "一切正常";
            Summary.Message = "所有设备都以它支持的最高速率运行。";
        }
        else
        {
            Summary.Severity = errors > 0 ? InfoBarSeverity.Error : warnings > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
            Summary.Title = $"发现 {_issues.Count} 条诊断";
            // 只放最重要的一条，其余点"查看问题"
            Summary.Message = _issues.Count == 1 ? _issues[0].Title : $"{_issues[0].Title} 等";
        }
        IssuesButton.Visibility = _issues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Summary.IsOpen = roots.Count > 0;
    }

    private async void Tree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is UsbRow row) await ShowDetailsAsync(row);
    }

    /// <summary>所有诊断的列表弹窗；点某条的"查看"转到该设备的详情弹窗。</summary>
    private async void Issues_Click(object s, RoutedEventArgs e)
    {
        if (_dialogOpen || _issues.Count == 0) return;
        UsbRow? next = null;
        var panel = new StackPanel { Spacing = 4, MinWidth = 440 };
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"诊断（{_issues.Count}）",
            Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
            CloseButtonText = "关闭",
        };
        foreach (var i in _issues)
        {
            var view = new Button { Content = "查看", Tag = i.Row };
            view.Click += (b, _) => { next = (UsbRow)((Button)b).Tag; dlg.Hide(); };
            panel.Children.Add(new InfoBar
            {
                IsOpen = true, IsClosable = false, Severity = i.Severity,
                Title = i.Title, Message = i.Message, ActionButton = view,
            });
        }

        _dialogOpen = true;
        try { await dlg.ShowAsync(); }
        finally { _dialogOpen = false; }
        if (next is not null) await ShowDetailsAsync(next);
    }

    private async Task ShowDetailsAsync(UsbRow row)
    {
        if (_dialogOpen) return;
        var panel = new StackPanel { Spacing = 12, MinWidth = 440 };
        if (!string.IsNullOrEmpty(row.Subtitle))
            panel.Children.Add(new TextBlock
            {
                Text = row.Subtitle, TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        foreach (var i in row.Issues.Select(i => new UsbIssueRow { Row = row, Issue = i }))
            panel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = i.Severity, Title = i.Issue.Title, Message = i.Issue.Detail });

        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var d in row.Details)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = d.Name, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
            var value = new TextBlock { Text = d.Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(name, r); Grid.SetRow(value, r); Grid.SetColumn(value, 1);
            grid.Children.Add(name); grid.Children.Add(value);
        }
        panel.Children.Add(grid);

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 12,
                Children =
                {
                    new FontIcon { Glyph = row.Glyph, FontSize = 24 },
                    new TextBlock { Text = row.Title, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
                },
            },
            Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
            PrimaryButtonText = "复制信息",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        };
        dlg.PrimaryButtonClick += (_, a) =>
        {
            a.Cancel = true; // 复制后不关窗
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(row.ToText());
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            dlg.PrimaryButtonText = "已复制";
        };

        _dialogOpen = true;
        try { await dlg.ShowAsync(); }
        finally { _dialogOpen = false; }
    }
}
