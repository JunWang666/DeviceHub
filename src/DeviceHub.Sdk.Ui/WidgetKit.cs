using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeviceHub.Sdk.Ui;

/// <summary>
/// 写主页小组件的小工具：绑定状态（首次读取 + 订阅推送，回调都在 UI 线程），以及几个统一外观的构件。
/// 不是必须用，只是让各插件的小组件看起来是一家人。
/// </summary>
public static class WidgetKit
{
    private static readonly JsonSerializerOptions J = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// 元素显示时读一次状态并订阅后端推送，隐藏（被移除、页面切走）时退订。
    /// JSON 按 <typeparamref name="T"/> 反序列化（不区分大小写）；读取失败时 apply 收到 null。
    /// </summary>
    public static void Bind<T>(FrameworkElement root, IPluginUiBridge bridge, Action<T?> apply) where T : class
    {
        var ui = DispatcherQueue.GetForCurrentThread();
        void OnPush(string json) => ui.TryEnqueue(() => apply(Parse<T>(json)));
        root.Loaded += async (_, _) =>
        {
            bridge.StatePushed -= OnPush;
            bridge.StatePushed += OnPush;
            try { apply(Parse<T>(await bridge.GetStateAsync())); }
            catch { apply(null); }
        };
        root.Unloaded += (_, _) => bridge.StatePushed -= OnPush;
    }

    /// <summary>调用后端方法；返回的最新状态交给 apply。失败时不抛出（小组件不弹错误框）。</summary>
    public static async void Invoke<T>(IPluginUiBridge bridge, string method, object args, Action<T?> apply) where T : class
    {
        try { apply(Parse<T>(await bridge.InvokeAsync(method, JsonSerializer.Serialize(args)))); }
        catch { /* 下一次推送会带回真实状态 */ }
    }

    private static T? Parse<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, J); } catch { return null; }
    }

    /// <summary>Segoe Fluent Icons 的电池图标（按 10% 一档，区分充电）。</summary>
    public static string BatteryGlyph(int percent, bool charging)
    {
        int l = Math.Clamp((int)Math.Round(percent / 10.0), 0, 10);
        if (charging) return l switch { 10 => "", 9 => "", _ => ((char)(0xE85A + l)).ToString() };
        return l == 10 ? "" : ((char)(0xE850 + l)).ToString();
    }

    public static Brush SecondaryBrush => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    /// <summary>大号数值（如"85%"），可带左侧图标。</summary>
    public static StackPanel Metric(string glyph, string value, double fontSize = 28)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (glyph.Length > 0) sp.Children.Add(new FontIcon { Glyph = glyph, FontSize = fontSize * 0.75, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = value, FontSize = fontSize, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    /// <summary>次要说明文字（单行省略）。</summary>
    public static TextBlock Caption(string text) => new()
    {
        Text = text, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = SecondaryBrush,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
    };

    /// <summary>居中的提示（未连接、未授权、暂无数据）。</summary>
    public static FrameworkElement Notice(string text) => new TextBlock
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Foreground = SecondaryBrush, VerticalAlignment = VerticalAlignment.Center,
    };
}
