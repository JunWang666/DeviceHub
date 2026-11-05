using System.Runtime.InteropServices;

namespace DeviceHub.Agent.Native;

/// <summary>
/// USB 设备/集线器接口的到达与移除通知（CM_Register_Notification，不需要窗口）。
/// 插拔一个设备会连着触发好几次（复合设备的每个接口、USB 3 集线器的两半），合并 0.5s 内的事件后才回调。
/// </summary>
public sealed class UsbWatcher : IDisposable
{
    private readonly List<nint> _handles = new();
    private readonly CmNotifyCallback _callback; // 保持委托存活，防止被 GC 回收后本机代码回调野指针
    private readonly Timer _debounce;

    public event Action? Changed;

    public UsbWatcher()
    {
        _debounce = new Timer(_ => Changed?.Invoke());
        _callback = OnNotify;
        foreach (var guid in new[] { NativeUsb.GUID_DEVINTERFACE_USB_DEVICE, NativeUsb.GUID_DEVINTERFACE_USB_HUB, GUID_DEVINTERFACE_COMPORT })
        {
            var filter = new CM_NOTIFY_FILTER
            {
                cbSize = (uint)Marshal.SizeOf<CM_NOTIFY_FILTER>(),
                FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE,
                ClassGuid = guid,
            };
            if (CM_Register_Notification(ref filter, nint.Zero, _callback, out var h) == 0) _handles.Add(h);
        }
    }

    private uint OnNotify(nint h, nint ctx, uint action, nint data, uint size)
    {
        if (action is CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL or CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL)
            _debounce.Change(500, Timeout.Infinite);
        return 0;
    }

    public void Dispose()
    {
        foreach (var h in _handles) CM_Unregister_Notification(h);
        _handles.Clear();
        _debounce.Dispose();
    }

    /// <summary>COM 口接口（USB 串口驱动就绪时到达），串口设备靠它及时出现。</summary>
    private static readonly Guid GUID_DEVINTERFACE_COMPORT = new("86e0d1e0-8089-11d0-9ce4-08003e301f73");
    private const uint CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE = 0;
    private const uint CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL = 0, CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL = 1;

    /// <summary>联合体按最大成员（InstanceId[200] 宽字符）定长 416 字节，接口过滤只用开头的 GUID。</summary>
    [StructLayout(LayoutKind.Sequential, Size = 416)]
    private struct CM_NOTIFY_FILTER
    {
        public uint cbSize;
        public uint Flags;
        public uint FilterType;
        public uint Reserved;
        public Guid ClassGuid;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint CmNotifyCallback(nint hNotify, nint context, uint action, nint eventData, uint eventDataSize);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Register_Notification(ref CM_NOTIFY_FILTER filter, nint context, CmNotifyCallback callback, out nint handle);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Unregister_Notification(nint handle);
}
