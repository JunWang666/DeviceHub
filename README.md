# DeviceHub — 方案 B 技术验证

聚合外设管理软件的架构验证。核心本体提供进程管理、数据管理、连接(授权)管理与插件热管理；
设备管理能力(含 UI)由插件提供。

## 方案 B 要点
- **后端进程隔离**：每个插件后端运行在独立的 `PluginHost` 进程，崩溃不影响核心，更新可热切换。
- **UI 进程内**：插件的原生 WinUI3 XAML 页面加载进 `App` 进程；更新插件 UI 时重启界面即可，Agent 不重启、设备功能不中断。
- **授权强制**：插件拿不到设备句柄，所有 I/O 经 Agent 的 Device Broker 代理，用户授权后才放行，可随时撤销。

## 本次 PoC 验证的风险点
1. WinUI3 进程内动态加载外部插件 dll 的 XAML 页面（含 `x:Bind` 与 PRI 资源）。
2. Agent ↔ App ↔ PluginHost 三进程的 JSON-RPC 链路。
3. 设备授权弹窗 + Feature Report 代理（真机 GK65 已验证，见 VERIFICATION.md）。
4. 重启式插件更新与失败回滚。

## 项目
- `src/DeviceHub.Contracts` — RPC 接口与 DTO（三端共享）
- `src/DeviceHub.Sdk` — 插件 SDK
- `src/DeviceHub.Agent` — 后台核心：Supervisor / Broker / Permission / PluginManager
- `src/DeviceHub.PluginHost` — 插件宿主进程
- `src/DeviceHub.App` — WinUI3 界面 + 插件 UI 动态加载
- `plugins/DeviceHub.Plugin.Gk65` / `.Gk65.Ui` — GK65 键盘插件（后端 + XAML UI，自带 `plugin.json`）

## 构建
```
dotnet build DeviceHub.sln -c Debug
```
