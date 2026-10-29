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
    private UsbRow? _selected;
    private int _scan;

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
            DetailTitle.Text = "扫描失败";
            DetailSubtitle.Text = ex.Message;
        }
        finally { if (scan == _scan) Busy.IsActive = false; }
    }

    private void Render()
    {
        if (_topology is null) return;
        var issues = new List<(UsbRow Row, UsbIssue Issue)>();
        var roots = UsbTreeBuilder.Build(_topology, EmptyToggle.IsOn, issues);
        Tree.ItemsSource = roots;

        var bars = issues.OrderByDescending(i => i.Issue.Level).Select(i => new UsbIssueRow { Row = i.Row, Issue = i.Issue }).ToList();
        IssueList.ItemsSource = bars;
        AllGood.IsOpen = bars.Count == 0 && roots.Count > 0;

        // 重新扫描后保持选中同一位置的节点（按位置 + 名称匹配），否则选第一个设备
        var all = Flatten(roots).ToList();
        var keep = _selected is null ? null : all.FirstOrDefault(r => r.Path == _selected.Path && r.Title == _selected.Title);
        var pick = keep ?? all.FirstOrDefault(r => r.Badge.Length > 0) ?? all.FirstOrDefault();
        if (pick is not null) Select(pick);
    }

    private static IEnumerable<UsbRow> Flatten(IEnumerable<UsbRow> rows) =>
        rows.SelectMany(r => Flatten(r.Children).Prepend(r));

    private void Tree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is UsbRow row) ShowDetails(row);
    }

    private void ShowIssue_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.Tag is UsbRow row) Select(row);
    }

    private void Select(UsbRow row)
    {
        Tree.SelectedItem = row;
        ShowDetails(row);
    }

    private void ShowDetails(UsbRow row)
    {
        _selected = row;
        DetailGlyph.Glyph = row.Glyph;
        DetailTitle.Text = row.Title;
        DetailSubtitle.Text = row.Subtitle;
        DetailIssues.ItemsSource = row.Issues.Select(i => new UsbIssueRow { Row = row, Issue = i }).ToList();
        DetailList.ItemsSource = row.Details;
        CopyButton.Visibility = Visibility.Visible;
    }

    private void Copy_Click(object s, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(_selected.ToText());
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
    }
}
