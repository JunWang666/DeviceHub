namespace DeviceHub.Contracts;

/// <summary>
/// App → Agent 调用。UI 进程通过命名管道调用核心。
/// </summary>
public interface IAgentApi
{
    Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync();
    Task<IReadOnlyList<PluginInfo>> ListPluginsAsync();

    /// <summary>用户在授权弹窗上的回应。</summary>
    Task RespondPermissionAsync(string requestId, AccessScope scope);

    Task<IReadOnlyList<GrantInfo>> ListGrantsAsync();

    /// <summary>设备详情：核心信息（通道、授权、插件）+ 接管插件提供的描述。</summary>
    Task<DeviceDetails?> GetDeviceDetailsAsync(string deviceKey);

    /// <summary>某设备的候选插件（manifest 匹配），供"打开"时弹出建议列表。</summary>
    Task<IReadOnlyList<PluginInfo>> ListCandidatePluginsAsync(string deviceKey);

    /// <summary>授权指定插件管理此设备（Always 授权 + 启动并接管），之后设备进入"正在管理"。</summary>
    Task AuthorizeAndUseAsync(string pluginId, string deviceKey);

    /// <summary>给已授权的设备起名字（存在授权记录里）；name 为空表示恢复默认名。</summary>
    Task RenameDeviceAsync(string pluginId, string deviceKey, string? name);

    /// <summary>撤销某插件对某设备的授权。</summary>
    Task RevokeGrantAsync(string pluginId, string deviceKey);

    /// <summary>手动启动/停止插件进程（进程管理演示）。</summary>
    Task StartPluginAsync(string pluginId);
    Task StopPluginAsync(string pluginId);

    /// <summary>把 UI 控件的值变更转发给插件后端（App → Agent → Host → 插件）。返回插件处理后的最新状态 JSON。</summary>
    Task<string> InvokePluginAsync(string pluginId, string deviceKey, string method, string argsJson);

    /// <summary>读取插件对某设备的当前状态 JSON（用于 UI 首次绑定）。</summary>
    Task<string> GetPluginStateAsync(string pluginId, string deviceKey);

    /// <summary>暂存一个新版本并请求重启界面生效（重启式更新）。</summary>
    Task<bool> StagePluginUpdateAsync(string pluginId, string packagePath);

    /// <summary>触发 UI 重启（Agent 不重启）：先应用待定的插件更新并重启其后端。</summary>
    Task RequestUiRestartAsync();

    /// <summary>安装插件包（.dhplugin）并立即切换到该版本、重启插件后端；原版本留作回滚。插件界面在重启 DeviceHub 界面后换成新版。</summary>
    Task<PluginInstallResult> InstallPluginPackageAsync(string packagePath);

    /// <summary>回到插件的上一个版本并重启后端，返回切换后的版本；没有上一个版本时返回 null。</summary>
    Task<string?> RollbackPluginAsync(string pluginId);

    /// <summary>扫描 USB 拓扑（主控制器 → 集线器 → 端口 → 设备）及诊断结论。只读，不需要设备授权。</summary>
    Task<UsbTopology> GetUsbTopologyAsync();

    // —— 按键映射（工具）——
    Task<RemapConfig> GetRemapConfigAsync();
    /// <summary>保存并立即生效。</summary>
    Task SaveRemapConfigAsync(RemapConfig config);
    /// <summary>
    /// 开始录制：之后的键盘 / 鼠标 / 设备按键经 <see cref="IAppCallback.OnInputCaptured"/> 推给 UI，且不再传给其他程序。
    /// 录到（Trigger / Keys 模式）、超时或 UI 断开时自动结束。
    /// </summary>
    Task StartInputCaptureAsync(CaptureMode mode);
    Task StopInputCaptureAsync();
    /// <summary>按键映射引擎是否在运行（钩子安装失败时为 false，附原因）。</summary>
    Task<string?> GetRemapEngineErrorAsync();
    /// <summary>当前连接的键盘和鼠标（供"仅在这些设备上生效"选择）。</summary>
    Task<IReadOnlyList<InputDeviceInfo>> ListInputDevicesAsync();
}

/// <summary>
/// Agent → App 回调。核心向 UI 推送事件。
/// </summary>
public interface IAppCallback
{
    void OnPermissionRequested(PermissionRequest request);
    void OnDevicesChanged(IReadOnlyList<DeviceInfo> devices);
    void OnPluginStateChanged(PluginInfo plugin);
    /// <summary>插件后端推送的状态变更（Host → Agent → App → 插件 UI）。</summary>
    void OnPluginStatePushed(string pluginId, string deviceKey, string stateJson);
    /// <summary>插件请求的屏幕状态提示（核心已校验设备归属并填好设备名）。</summary>
    void OnOsd(OsdMessage message);
    /// <summary>有 USB 设备或集线器插拔（已合并连续事件），UI 可重新扫描拓扑。</summary>
    void OnUsbChanged();
    /// <summary>按键录制事件（见 <see cref="IAgentApi.StartInputCaptureAsync"/>）。</summary>
    void OnInputCaptured(InputCaptureEvent e);
}

/// <summary>
/// Agent → PluginHost 调用。核心驱动插件后端。
/// </summary>
public interface IHostApi
{
    Task InitializeAsync(string pluginId, string pluginVersionDir);
    Task AttachDeviceAsync(DeviceInfo device);
    Task DetachDeviceAsync(string deviceKey);
    Task<string> InvokeAsync(string deviceKey, string method, string argsJson);
    Task<string> GetStateAsync(string deviceKey);
    /// <summary>插件对设备的描述（标题/副标题/详情项），插件未实现时为 null。</summary>
    Task<DeviceDescription?> DescribeDeviceAsync(string deviceKey);
    /// <summary>更新前通知插件刷写设备状态并准备退出。</summary>
    Task PrepareShutdownAsync();
    Task<int> GetSchemaVersionAsync();
    Task MigrateAsync(int fromSchema, int toSchema);
}

/// <summary>
/// PluginHost → Agent 调用。插件后端经核心访问设备、存储与通知（授权在此强制）。
/// </summary>
public interface IHostToAgentApi
{
    /// <summary>申请访问设备。未授权时 Agent 触发 UI 弹窗并等待结果。</summary>
    Task<AccessScope> RequestDeviceAccessAsync(string pluginId, string deviceKey, string reason);

    /// <summary>查询当前授权（不弹窗、不要求设备在线）。子设备键按其继承的授权返回。</summary>
    Task<AccessScope> GetDeviceAccessAsync(string pluginId, string deviceKey);

    // —— 设备 I/O 代理（核心校验授权后执行）。channel = 插件 manifest 声明的通道角色，null 为默认通道 ——
    Task<DeviceIoResult> GetFeatureAsync(string pluginId, string deviceKey, string? channel, byte reportId, int length);
    Task<DeviceIoResult> SetFeatureAsync(string pluginId, string deviceKey, string? channel, byte[] report);
    Task<DeviceIoResult> WriteOutputAsync(string pluginId, string deviceKey, string? channel, byte[] report);
    /// <summary>读一个 Input 报告；timeoutMs 内无数据返回 Timeout。</summary>
    Task<DeviceIoResult> ReadInputAsync(string pluginId, string deviceKey, string? channel, int timeoutMs);

    // —— 存储（按 pluginId 隔离）——
    Task<string?> StorageGetAsync(string pluginId, string key);
    Task StorageSetAsync(string pluginId, string key, string valueJson);

    // —— 向 UI 推送状态 ——
    Task PushStateAsync(string pluginId, string deviceKey, string stateJson);

    // —— 通知（托盘/Toast）——
    Task NotifyAsync(string pluginId, string title, string message);
    /// <summary>屏幕状态提示；deviceKey 必须是该插件正在管理的设备（或其子设备）。</summary>
    Task ShowOsdAsync(string pluginId, string deviceKey, OsdContent content);

    // —— 子设备 ——
    /// <summary>
    /// 发布 parentKey 下的全部子设备（整体替换，传空列表即清除）。插件必须已获得父设备的 Always 授权；
    /// 子设备随父设备授权，键为 <see cref="DeviceKeys.Child"/>。
    /// </summary>
    Task PublishChildrenAsync(string pluginId, string parentKey, IReadOnlyList<ChildDevice> children);

    /// <summary>子设备已不存在：撤销其授权并从设备列表移除。插件必须已获得父设备的 Always 授权。</summary>
    Task ForgetChildAsync(string pluginId, string parentKey, string childId);

    /// <summary>
    /// 设备按键事件（系统收不到、由插件从设备协议里读到的按键，如罗技鼠标被转发的手势键）。
    /// deviceKey 必须是该插件已获 Always 授权的设备（或其子设备）。交给按键映射处理。
    /// </summary>
    Task RaiseDeviceInputAsync(string pluginId, string deviceKey, DeviceInputEvent e);
}
