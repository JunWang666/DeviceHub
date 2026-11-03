using System.Runtime.InteropServices;
using DeviceHub.Contracts;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeviceHub.App.Services;

/// <summary>
/// 屏幕状态提示：类似系统音量浮窗，在主显示器工作区底部居中弹出，几秒后自动隐藏。
/// 模板固定为「图标 + 设备名 + 一行文本 + 可选进度条」，设备名一定显示（由核心填写，插件改不了）。
/// 不抢焦点、不进任务栏和 Alt+Tab；新提示到来时直接替换当前内容并重新计时。
/// </summary>
public static class OsdWindow
{
    private const int Width = 360, Height = 76, BottomGap = 24;
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(3);

    private static Window? _window;
    private static FontIcon _icon = null!;
    private static TextBlock _name = null!, _text = null!;
    private static ProgressBar _bar = null!;
    private static DispatcherTimer? _timer;

    public static void Show(OsdMessage m)
    {
        EnsureWindow();
        _icon.Glyph = m.Glyph;
        _icon.Foreground = Accent(m.Kind);
        _name.Text = m.DeviceName;
        _text.Text = m.Text;
        _text.Visibility = m.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _bar.Visibility = m.Percent is null ? Visibility.Collapsed : Visibility.Visible;
        _bar.Value = m.Percent ?? 0;
        _bar.Foreground = m.Kind is OsdKind.BatteryLow ? Accent(m.Kind) : (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

        Place();
        _window!.AppWindow.Show(false); // 不激活：不打断用户正在操作的窗口
        _timer!.Stop();
        _timer.Start();
    }

    private static Brush Accent(OsdKind kind) => (Brush)Application.Current.Resources[kind switch
    {
        OsdKind.BatteryLow => "SystemFillColorCriticalBrush",
        OsdKind.Warning => "SystemFillColorCautionBrush",
        OsdKind.Charged => "SystemFillColorSuccessBrush",
        OsdKind.Disconnected => "TextFillColorSecondaryBrush",
        _ => "TextFillColorPrimaryBrush",
    }];

    private static void EnsureWindow()
    {
        if (_window is not null) return;

        _icon = new FontIcon { FontSize = 26, VerticalAlignment = VerticalAlignment.Center };
        _name = new TextBlock
        {
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
        };
        _text = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
        };
        _bar = new ProgressBar { Maximum = 100, Margin = new Thickness(0, 6, 0, 0) };

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _name, _text, _bar } };
        Grid.SetColumn(texts, 1);
        var root = new Grid
        {
            Padding = new Thickness(20, 12, 20, 12),
            ColumnSpacing = 16,
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() },
            Children = { _icon, texts },
        };

        _window = new Window { Content = root, SystemBackdrop = new DesktopAcrylicBackdrop(), Title = "DeviceHub 状态" };
        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(true, false); // 保留细边框，Win11 下才有圆角与阴影
        _window.AppWindow.SetPresenter(presenter);
        _window.AppWindow.IsShownInSwitchers = false;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        _timer = new DispatcherTimer { Interval = Duration };
        _timer.Tick += (_, _) => { _timer.Stop(); _window.AppWindow.Hide(); };
        // 隐藏的提示窗也算打开的窗口，主窗口关掉时一起关，否则进程不会退出。
        if (App.MainWindow is { } main) main.Closed += (_, _) => { _timer.Stop(); _window?.Close(); _window = null; };
    }

    /// <summary>主显示器工作区（不含任务栏）底部居中，尺寸按显示器 DPI 缩放。</summary>
    private static void Place()
    {
        var area = DisplayArea.Primary.WorkArea;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window!);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        int w = (int)(Width * scale), h = (int)(Height * scale);
        _window!.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            area.X + (area.Width - w) / 2, area.Y + area.Height - h - (int)(BottomGap * scale), w, h));
    }

    private const int GWL_EXSTYLE = -20;
    private const nint WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
}
