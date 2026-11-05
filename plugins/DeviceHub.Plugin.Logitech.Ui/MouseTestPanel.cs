using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace DeviceHub.Plugin.Logitech.Ui;

/// <summary>
/// 鼠标按键与移动测试：五个按键 + 滚轮的按下状态和次数、指针轨迹、移动速度和回报率估算。
/// 纯代码构建（不另加 XAML），只看鼠标类指针；系统不区分是哪只鼠标，接着几只鼠标时都会响应。
/// </summary>
public sealed class MouseTestPanel : UserControl
{
    private const int TrailPoints = 400;
    private static readonly string[] Names = ["左键", "右键", "中键", "后退", "前进"];

    private readonly Key[] _keys;
    private readonly Key _wheel;
    private readonly Border _pad;
    private readonly Polyline _trail;
    private readonly TextBlock _hint, _stats;
    private readonly bool[] _down = new bool[5];
    private readonly DispatcherTimer _wheelFlash = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private int _wheelUp, _wheelDown, _wheelLeft, _wheelRight;

    // 速度与回报率：按 500ms 窗口统计（系统按帧合并指针消息，回报率只能用中间点个数估算）
    private ulong _windowStart, _lastStamp;
    private Point? _lastPos;
    private double _windowDist;
    private int _windowPoints;
    private double _speed, _rate;

    public MouseTestPanel()
    {
        _keys = Names.Select(n => new Key(n)).ToArray();
        _wheel = new Key("滚轮");
        var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var k in _keys) keys.Children.Add(k.Root);
        keys.Children.Add(_wheel.Root);

        _trail = new Polyline { Stroke = Res("AccentFillColorDefaultBrush"), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        _hint = new TextBlock
        {
            Text = "在这里移动、点击、滚动鼠标",
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res("TextFillColorTertiaryBrush"), IsHitTestVisible = false,
        };
        var canvas = new Canvas { Children = { _trail } };
        _pad = new Border
        {
            Height = 240,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Res("CardStrokeColorDefaultBrush"),
            Background = Res("ControlAltFillColorSecondaryBrush"),
            Child = new Grid { Children = { canvas, _hint } },
        };
        _pad.SizeChanged += (_, _) => _pad.Clip = new RectangleGeometry { Rect = new Rect(0, 0, _pad.ActualWidth, _pad.ActualHeight) };
        _pad.PointerPressed += (_, e) => OnPointer(e, Phase.Pressed);
        _pad.PointerReleased += (_, e) => OnPointer(e, Phase.Released);
        _pad.PointerMoved += (_, e) => OnPointer(e, Phase.Moved);
        _pad.PointerCaptureLost += (_, _) => { Array.Clear(_down); Render(); };
        _pad.PointerWheelChanged += OnWheel;

        _stats = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = Res("TextFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center };
        var reset = new Button { Content = "重置" };
        reset.Click += (_, _) => Reset();
        var bottom = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        Grid.SetColumn(reset, 1);
        bottom.Children.Add(_stats);
        bottom.Children.Add(reset);

        _wheelFlash.Tick += (_, _) => { _wheelFlash.Stop(); _wheel.Set(false); };
        Content = new StackPanel { Spacing = 10, Children = { new ScrollViewer { Content = keys, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled }, _pad, bottom } };
        Reset();
    }

    private enum Phase { Pressed, Released, Moved }

    private void OnPointer(PointerRoutedEventArgs e, Phase phase)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        var pt = e.GetCurrentPoint(_pad);
        var p = pt.Properties;
        // 按住一个键再按另一个时系统只发 PointerMoved，所以每个事件都和上次的按下状态比较
        bool[] now = [p.IsLeftButtonPressed, p.IsRightButtonPressed, p.IsMiddleButtonPressed, p.IsXButton1Pressed, p.IsXButton2Pressed];
        for (int i = 0; i < 5; i++)
        {
            if (now[i] && !_down[i]) _keys[i].Count++;
            _down[i] = now[i];
        }
        if (phase == Phase.Pressed) _pad.CapturePointer(e.Pointer);
        else if (phase == Phase.Released && !now.Any(b => b)) _pad.ReleasePointerCapture(e.Pointer);

        if (phase == Phase.Moved) Track(e);
        _hint.Visibility = Visibility.Collapsed;
        Render();
        e.Handled = true; // 后退 / 前进键不要触发页面导航
    }

    private void Track(PointerRoutedEventArgs e)
    {
        // 中间点按时间倒序，包含本次的当前点
        var points = e.GetIntermediatePoints(_pad);
        double scale = XamlRoot?.RasterizationScale ?? 1;
        for (int i = points.Count - 1; i >= 0; i--)
        {
            var pt = points[i];
            var pos = pt.Position;
            if (_lastPos is { } last)
            {
                double dx = (pos.X - last.X) * scale, dy = (pos.Y - last.Y) * scale;
                _windowDist += Math.Sqrt(dx * dx + dy * dy);
            }
            if (_windowStart == 0 || pt.Timestamp - _lastStamp > 200_000) { _windowStart = pt.Timestamp; _windowDist = 0; _windowPoints = 0; }
            _windowPoints++;
            _lastPos = pos;
            _lastStamp = pt.Timestamp;

            _trail.Points.Add(pos);
            if (_trail.Points.Count > TrailPoints) _trail.Points.RemoveAt(0);
        }
        var span = (_lastStamp - _windowStart) / 1_000_000.0;
        if (span >= 0.5)
        {
            _speed = _windowDist / span;
            _rate = _windowPoints / span;
            _windowStart = _lastStamp; _windowDist = 0; _windowPoints = 0;
        }
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        var p = e.GetCurrentPoint(_pad).Properties;
        int d = p.MouseWheelDelta;
        if (p.IsHorizontalMouseWheel) { if (d > 0) _wheelRight++; else _wheelLeft++; }
        else if (d > 0) _wheelUp++; else _wheelDown++;
        _wheel.Set(true);
        _wheelFlash.Stop();
        _wheelFlash.Start();
        _hint.Visibility = Visibility.Collapsed;
        Render();
        e.Handled = true; // 别让外层页面跟着滚
    }

    private void Render()
    {
        for (int i = 0; i < 5; i++) _keys[i].Set(_down[i]);
        _wheel.Detail = _wheelLeft + _wheelRight > 0
            ? $"↑{_wheelUp} ↓{_wheelDown} ←{_wheelLeft} →{_wheelRight}"
            : $"↑{_wheelUp} ↓{_wheelDown}";
        _stats.Text = $"速度 {_speed:N0} 像素/秒 · 回报率约 {_rate:N0} Hz（系统合并指针消息，仅供参考）· 点击次数 {_keys.Sum(k => k.Count)}";
    }

    private void Reset()
    {
        foreach (var k in _keys) k.Count = 0;
        _wheelUp = _wheelDown = _wheelLeft = _wheelRight = 0;
        _trail.Points.Clear();
        _lastPos = null;
        _windowStart = 0;
        _speed = _rate = 0;
        _hint.Visibility = Visibility.Visible;
        Render();
    }

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>一个按键指示块：名称 + 次数，按下时用强调色。</summary>
    private sealed class Key
    {
        public readonly Border Root;
        private readonly TextBlock _name, _detail;
        private int _count;

        public Key(string name)
        {
            _name = new TextBlock { Text = name, HorizontalAlignment = HorizontalAlignment.Center };
            _detail = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center };
            Root = new Border
            {
                MinWidth = 64,
                Padding = new Thickness(12, 6, 12, 6),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = Res("CardStrokeColorDefaultBrush"),
                Child = new StackPanel { Children = { _name, _detail } },
            };
            Set(false);
        }

        public int Count { get => _count; set { _count = value; _detail.Text = $"{value} 次"; } }
        public string Detail { set => _detail.Text = value; }

        public void Set(bool down)
        {
            Root.Background = Res(down ? "AccentFillColorDefaultBrush" : "CardBackgroundFillColorDefaultBrush");
            var fg = Res(down ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
            _name.Foreground = fg;
            _detail.Foreground = down ? fg : Res("TextFillColorSecondaryBrush");
        }
    }
}
