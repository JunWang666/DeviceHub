using DeviceHub.Contracts;

namespace DeviceHub.App.Services;

/// <summary>
/// 按键录制（经 Agent 的低级钩子）：录制期间按下的键不会传给任何程序，所以 Alt+Tab、Win 键这类组合也能录到。
/// 同一时间只有一个录制；新的录制开始时旧的视为取消。
/// </summary>
public sealed class InputCapture
{
    public static InputCapture Instance { get; } = new();

    private TaskCompletionSource<InputTrigger?>? _tcs;
    private Action<KeyModifiers>? _held;
    private Action<MacroStep>? _step;
    private Action? _done;

    private InputCapture()
    {
        AppState.Instance.Client.InputCaptured += e => AppState.Instance.OnUi(() => Handle(e));
        AppState.Instance.Client.Disconnected += () => AppState.Instance.OnUi(() => Handle(new InputCaptureEvent { Done = true }));
    }

    public bool Active => _tcs is not null || _step is not null;

    /// <summary>录一个按键；返回 null 表示取消 / 超时。held：按住修饰键时实时回报。</summary>
    public async Task<InputTrigger?> CaptureAsync(CaptureMode mode, Action<KeyModifiers>? held = null)
    {
        if (AppState.Instance.Client.Agent is not { } agent) return null;
        Cancel();
        var tcs = new TaskCompletionSource<InputTrigger?>();
        _tcs = tcs;
        _held = held;
        await agent.StartInputCaptureAsync(mode);
        return await tcs.Task;
    }

    /// <summary>连续录制宏步骤，直到 <see cref="Stop"/> 或超时（done 回调）。</summary>
    public async Task StartMacroAsync(Action<MacroStep> step, Action done)
    {
        if (AppState.Instance.Client.Agent is not { } agent) { done(); return; }
        Cancel();
        _step = step;
        _done = done;
        await agent.StartInputCaptureAsync(CaptureMode.Macro);
    }

    public void Stop() => _ = AppState.Instance.Client.Agent?.StopInputCaptureAsync();

    private void Cancel()
    {
        if (!Active) return;
        Stop();
        Handle(new InputCaptureEvent { Done = true });
    }

    private void Handle(InputCaptureEvent e)
    {
        if (e.Step is { } s) _step?.Invoke(s);
        if (e.Trigger is { } t) _tcs?.TrySetResult(t);
        else if (!e.Done) _held?.Invoke(e.Held);
        if (!e.Done) return;
        _tcs?.TrySetResult(null);
        var done = _done;
        _tcs = null; _held = null; _step = null; _done = null;
        done?.Invoke();
    }
}
