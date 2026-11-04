# 方案 B 验证说明

## 已验证结论（headless 自检，2026-10-25）
> 本节是当时基于 Sim 示例插件 + 模拟设备的记录；两者已于 2026-10-26 删除，现行自检见「界面与流程」。

通过 App 启动自检（设环境变量 `DEVICEHUB_SELFTEST=1`）在无 GUI 交互下跑通，日志实测：
- `SELFTEST: 插件 XAML 动态加载成功 type=DeviceHub.Plugin.Sim.Ui.SimPage (x:Bind + PRI 解析 OK)`
  → **风险点 1 成立**：App 进程内动态加载外部插件程序集的 WinUI3 页面，`x:Bind` 编译绑定与 PRI 资源均正常。
- `SELFTEST-IO: before dpi=800 → afterSetDpi1600 dpi=1600, status=已同步到设备`
  → 端到端链路 **UI → Agent → PluginHost → 插件后端 → Broker → 设备** 打通，且受授权门控。
- Agent 日志：PluginHost 独立进程启动、`已接管设备 sim:1a2c:7f49`、`UI 已连接`。

### 关键工程结论：PRI 处理比预想简单
**WinUI3 非打包（unpackaged）应用会自动加载 exe 同目录下的每个 `*.pri`**，因此插件 UI 的
`x:Bind` + xbf 资源，只需把插件的 `DeviceHub.Plugin.Sim.Ui.pri` 和 xbf 布局**复制到 App 目录旁**即可解析，
**不需要 makepri merge**（本机 SDK 的 makepri 也确实没有 merge 子命令）。
- 约束：资源管理器在**进程启动时**扫描 `*.pri`，所以插件资源必须在 App 启动前就位。
- 对应重启式更新：更新在 Agent 停旧后端 + 暂存新版本后，由启动器/部署步骤把新版 UI 资源摆到 App 目录旁，
  再启动 App → 新 XAML 生效。生产中若要进程内运行时合并，可用 MrtCore 的 `Mrm*` API，但本 PoC 证明无需如此。
- 若插件 XAML 用到**自定义元素类型**（非框架控件），还需把插件的 `IXamlMetadataProvider` 聚合进 App；
  本示例页面只用框架控件，未触发该需求（已在正文说明）。



## 打包与安装（2026-10-26）
- `tools\pack.ps1 [-Version 0.1.0]` 产出到 `dist\`：Inno 安装包（按用户安装到 `%LocalAppData%\Programs\DeviceHub`，免管理员）、
  自签名 MSIX、免安装 zip、`<插件Id>-<版本>.dhplugin`，以及 MSIX 用的公钥 `DeviceHub-Dev.cer`。
  App/Agent/PluginHost 均 self-contained（目标机无需 .NET 与 Windows App SDK）。
- **签名**：首次运行会在 CurrentUser\My 创建 `CN=DeviceHub Dev` 代码签名证书并复用；安装 MSIX 前需把 `.cer`
  导入「本地计算机 › 受信任的人」——`tools\trust-dev-cert.ps1`（自提权，并清理导错位置的副本）。
- **启动链**：App 入口（自定义 `Program.Main`）发现 Agent 管道不存在时，从 exe 同目录以无窗口方式启动 Agent，
  Agent 再拉起 PluginHost；Agent 日志写到 `%LocalAppData%\DeviceHub\logs\agent.log`。
- **内置插件**：安装目录 `plugins\*.dhplugin` 由 Agent 首次启动时安装到插件仓库（仓库里没有或版本更低时）。
  「插件」页「安装插件…」可装 `.dhplugin`；「重启界面以应用更新」时 Agent 先切换待定版本并重启插件后端。
- **插件 UI 资源不进安装目录**：App 通过 `Application.ResourceManagerRequested` 提供资源管理器，
  XAML 查不到插件的 `Files/<UiAssembly>/*.xbf` 时（`ResourceNotFound`）由 `PluginResourceResolver` 指向插件仓库。
  因此 MSIX 的只读安装目录不受影响（实测：Inno 与 MSIX 两种安装，运行后安装目录均无新增文件）。
  未覆盖：插件用 `x:Uid` 的本地化字符串（需再查插件自己的 .pri），GK65 未用到。
- **数据隔离**：MSIX 版写 `%LocalAppData%` 会落到 `Packages\DeviceHub_<hash>\LocalCache`，与 Inno/开发版数据互不可见。
  两种版本同时运行时会争同一个 Agent 管道与单实例锁，只应保留其一。
- 已修的打包缺陷：`-p:Version` 会改掉 SDK 程序集版本导致插件后端加载失败（现只设 InformationalVersion/FileVersion）；
  Agent 写 `state.json` 用 `Active`、App 读 `active`（现不区分大小写）。

## 设备聚合与插件自声明（2026-10-26）
- **聚合**：`NativeHid` 枚举每个 HID 顶层集合时读取 PnP `DEVPKEY_Device_ContainerId`（"设备和打印机"
  就按它分组），`DeviceBroker` 按 (VID, PID, ContainerId) 把集合合成一个物理设备，集合列为
  `DeviceInfo.Channels`（Id 形如 `mi02-ff01-0001`）。无容器时退回序列号，再退回单独路径。
  设备 key 优先用序列号（跨重启/换口稳定），授权以物理设备为单位。
  实测：GK65 接收器的 6 个集合（mi00 键盘、mi01 的 4 个集合、mi02 配置通道）合并为一项。
- **插件自声明**：每个插件项目自带 `plugin.json`（部署时原样作为 manifest.json），在 `devices` 里声明
  总线、VID、PID（单值或数组）、友好名称 `name`，以及要用的通道 `channels`（`role` + 接口号/UsagePage/Usage）。
  设备须具备全部声明的通道才算匹配；匹配后"新设备"页显示插件声明的名称。
- **通道最小权限**：插件 I/O 传通道角色（省略 = 第一个声明的通道），Broker 经 manifest 解析到具体集合；
  未声明的集合一律 AccessDenied。撤销授权时关闭该设备全部通道的句柄。

## 设备显示由插件决定、授权记录、状态记忆（2026-10-26）
- **显示**：设备名称 = 插件运行时 `DescribeAsync().Title` › manifest `devices[].name` › HID 产品串；
  副标题与「设备详情」里的信息项也来自 `DescribeAsync`（SDK 默认实现返回 null）。核心只补插件名、接入时间、通道等。
- **授权记录**：`grants.json` 改为 `{"scope":2,"grantedAt":"…"}`，兼容旧的纯数字格式（接入时间取文件修改时间）。
  「已授权的设备」显示 设备名 / 插件 / 接入时间，「详情」弹窗汇总设备、授权、插件信息与 HID 通道。
- **GK65 状态记忆**：键盘没有读命令，官方软件也只靠本地 `ledeffect.xml` 记忆。插件优先用 DeviceHub 上次保存的状态；
  首次接管时从 `ledeffect.xml` 导入（`sel` 为下拉序号，模式码 = 序号 0→0，其余 序号+1）。
  界面明确提示「键盘不支持读取当前设置」；用键盘 Fn 快捷键改的灯效无法得知。
- **界面**：页面按 WinUI 设置页规范（标题 + 说明、SettingsCard 卡片 4px 间距、分组标题、36px 边距、MaxWidth 1000），
  GK65 页用 SelectorBar 分「灯光 / 按键测试」；去掉了键盘不存在的 DPI 和未抓到协议的方向/RGB/侧灯/恢复出厂。
  默认窗口 1080×720（有效像素）并居中。
- **按键页布局**：键位几何取自官方驱动 `positions.xml`（rect 非零的 64 个物理键 + 固件 Fn 占位），
  提取为 `plugins/Gk65.Shared/gk65-layout.json` 嵌入插件 UI；按坐标在 Canvas 上绘制 65% 配列，
  Viewbox 随可用宽度等比缩小。键帽只做展示；「按键测试」开关打开时，本窗口收到的按键在图上高亮（不全局监听、不记录）。
  Fn 由固件处理，系统收不到，不会高亮。

## GK65 真机协议（2026-10-26 实抓）
- **通道**：接收器 `VID_1A2C PID_7FFF` 的 `mi_02`（UsagePage 0xFF01 / Usage 0x01，无 report id，
  In/Out 均 65 字节，无 Feature）。官方软件用 `WriteFile` 写 65 字节（`00` + 64 字节负载），
  落到总线上是端点 0x04 的 64 字节中断 OUT。
- **单向**：设备对写入不回包（HIDCLASS rundown 显示 mi_02 `NumReadReports=0`），未发现读命令；
  官方软件也不回读（固件版本恒显示 v0.0），状态只存在本地 `ledeffect.xml`。
- **帧格式**：`00 | BB AA 99 88 | 命令 | 参数… | 补 0`，无校验。
- **已核实**：面板灯效 `AA <模式> <亮度0-5> <速度0-4>`，模式码 = `uires/xml/dlg_main.xml` 下拉项 data（0..19）。
  样本：Steady `AA 00 05 00`、Breathing `AA 02 05 04`、Neon_stream `AA 04 05 04`、
  Streamer `AA 05 05 04` / 速度 2 `AA 05 05 02`、亮度 4 `AA 00 04 00`。
- **真机写入验证**：用 `HidProbe --panel` 直接写 mi_02（与 Broker 同为 65 字节 WriteFile）：
  Steady/5/0（与当前相同，无变化）→ 亮度 4（用户目视确认变暗一档）→ 亮度 5（恢复），三帧均 written=65。
- **改键不经过键盘**（2026-10-26 实测）：在官方软件把 Menu(App) 键改成 Volume- 并 Apply，再 Delete 恢复，
  全程 WriteFile 只写了本地 `record.xml`（`<record code="70065" … group="Volume-"><item code="c00ea"/>`）与
  `macro.xml`，**没有任何发往 mi_02 的报文**。安装包里的 `vhidflt.sys`（虚拟 HID 过滤驱动）也未安装。
  结论：映射由官方软件在电脑端运行时实现，键盘固件不保存改键。宏绑定在同一对话框、同一 record.xml，同属电脑端功能。
  **决定：DeviceHub 不做改键与宏**（属于 PowerToys 键盘管理器一类的系统功能，不是设备能力），已从插件中移除。
- **未抓到**：方向（试过的灯效都没有方向选项）、侧灯（官方界面隐藏）、恢复出厂；
  对应帧保持 Verified=false，插件不下发。官方软件没有 DPI / 配置切换界面。
- **抓包方法**：USB ETW（UCX/USBXHCI/HIDCLASS）只给包头不给负载；最终用自写的只读 WOW64 调试器
  （在 32 位 `KERNELBASE!WriteFile` 下 int3 → dump 缓冲区 → 单步重布 → 脱离）拿到负载。
  官方软件以管理员运行，UI 操作需提权进程注入输入（UIPI）。

## GK65 真实插件（功能模型 + UI 完整，面板灯效写入已按实抓协议实现）
- 功能定义全部来自驱动配置文件(忠实提取到 `plugins/Gk65.Shared/gk65-data.json`)：
  20 种灯效(常亮…正弦光波)、117 个键位(HID usage)、22 个功能/媒体键、6 个灯光区域、
  3 套配置(P1/P2/P3)、宏(4 种播放模式 + 3 种延时模式)。
- UI(`DeviceHub.Plugin.Gk65.Ui`)：「灯光」（模式/亮度/速度）与「按键测试」（65% 键位图）两部分，详见上文。
- **写入**：`Gk65Protocol` 按上节实抓格式编码；`Gk65Plugin.Send` 只下发 Verified=true 的帧
  （经 `DeviceBroker.WriteOutputAsync` → `WriteFile` 65 字节），未核实的帧只更新状态串"未下发"。
  接管设备时不回读也不主动下发，只在用户改设置时写。

## 界面与流程（最新）
- **Mica 标题栏**：`Window.SystemBackdrop = MicaBackdrop` + `ExtendsContentIntoTitleBar` 自定义标题栏。
- **左侧 = 正在管理的设备**：每个已授权设备一个导航项，点击直接进入其插件 UI。
- **「新设备」菜单**：设备发现 + 已授权（权限）合并到此页；只列出有候选插件的未授权设备。
- **授权流程**：新设备页点「打开」→ 弹出该设备的建议插件 → 选一个点「授权并使用」→
  设备进入左侧「正在管理」并跳转到其插件 UI。授权即 Always 授权，重启后自动接管。
- 自检（`DEVICEHUB_SELFTEST=1`，只读：不授权、不做设备 I/O）实测：`GK65 三模键盘 [hid:1a2c:7fff:…] 6 个 HID 通道`
  → `建议插件=com.devicehub.gk65` → `插件 XAML 动态加载成功 type=DeviceHub.Plugin.Gk65.Ui.Gk65Page`。

## 一键运行
```powershell
pwsh -ExecutionPolicy Bypass -File tools\run.ps1
```
脚本会：停旧进程 → 构建 → 部署 GK65 插件到 `%LocalAppData%\DeviceHub\plugins` → 启动 Agent → 启动 App。

手动分步：
```powershell
dotnet build DeviceHub.slnx -c Debug
pwsh -File tools\deploy-plugin.ps1
# 终端 1：后台核心（带日志）
src\DeviceHub.Agent\bin\x64\Debug\net10.0-windows\win-x64\DeviceHub.Agent.exe
# 终端 2：界面
src\DeviceHub.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\DeviceHub.App.exe
```

## 验证清单（对应方案 B 的风险点）

### 1. 三进程 RPC 链路
- 启动 App 后顶部横幅应显示「已连接」。
- Agent 控制台日志出现：`插件已启动 com.devicehub.gk65 (pid …)`；授权后出现 `已接管设备 hid:1a2c:7fff:…`。
- 任务管理器可见三个进程：`DeviceHub.Agent`、`DeviceHub.PluginHost`、`DeviceHub.App`。

### 2. 进程隔离 / 进程管理（Job Object + 心跳 + 重启）
- 在「插件」页点「停止」→ PluginHost 进程消失，App 不受影响。
- 手动在任务管理器结束 `DeviceHub.PluginHost` → Agent 日志出现心跳失败并按 1s,2s,4s… 退避重启。
- 关闭 Agent → PluginHost 一并退出（Job Object 的 KILL_ON_JOB_CLOSE）。

### 3. 授权强制（连接管理）
- 首次接管设备时，App 弹出「设备访问请求」对话框。
- 选「拒绝」→ 插件 UI 的滑块禁用、状态显示未授权；设备 I/O 返回 AccessDenied。
- 选「始终允许」→ 控件可用；改面板灯效（模式/亮度/速度）实时下发到真机，未核实的设置显示「未下发」。
- 「权限」页可见授权记录，点「撤销」后 Agent 立即关闭句柄；下次写操作返回 AccessRevoked。

### 4. 进程内动态加载 XAML（最关键）
- 在「设备」页点设备的「打开」→ 右侧加载的是**插件程序集里的原生 WinUI3 页面**（DPI 滑块、RGB + 预览色块）。
- 该页面使用 `x:Bind` 编译绑定，由 App 在运行时从 `%LocalAppData%\DeviceHub\plugins\...\ui\` 动态加载，App 对该程序集**无编译期引用**。
- 若加载失败（通常是 PRI/xbf 资源解析），宿主容器会显示异常堆栈——这是该风险点的直接观测窗口。

### 5. 重启式更新 + 回滚
- 「插件」页点某插件「更新」→ 选新版本目录或 .dhplugin 包暂存（Agent 停止旧后端进程）。
  （原先的 Sim v1.1 演示更新包已随示例插件删除。）
- 点「重启界面生效更新」→ App 自我重启；重启后「插件」页显示新版本。
- 回滚：删除 `state.json` 的 `active` 指回 `1.0.0`，或调用 `PluginManager.Rollback`（连续崩溃会自动触发熔断/禁用）。

## 真实设备（USB 接收器直通后）
- `DeviceBroker` 用 `SetupDi + HidD_*` 枚举真实 HID 集合并按 ContainerId 聚合，`DeviceKey` 形如 `hid:1a2c:7fff:<序列号>`。
- GK65 插件在 `plugin.json` 声明 `1a2c:7fff` + `config` 通道（mi_02 / 0xFF01），接收器插入后显示为「GK65 三模键盘」。
- 协议与真机写入验证见上文「GK65 真机协议」。
```

## AirPods 插件（2026-10-26）
参考 AirPodsDesktop（BLE 广播解析）、PodBridge（连接门控、入耳位修正、AAP 降噪）、NTPods（AAP 命令表与推送解析）。
- **蓝牙总线**：Agent 新增 `BluetoothService`（Agent TFM 改为 `net10.0-windows10.0.26100.0` 以使用 WinRT 蓝牙）。
  监视已配对设备，取蓝牙 Device ID 的 VendorId = 0x004C（Apple）者，`DeviceKey` 形如 `bt:004c:<型号>:<蓝牙地址>`，
  `DeviceBus.Bluetooth`；配对/连接变化时通知 UI，并让运行中的插件接管新出现的已授权设备（`SyncDevicesAsync`）。
- **两个通道**（`HidChannelInfo.Kind`，manifest 里用 `kind` 声明）：
  - `continuity`：核心按需开 BLE 扫描，只把「型号相符 + 该设备已连接」的 Apple 接近配对包（27 字节）交给插件，
    并清零末尾 16 字节加密段；`ReadInputAsync` 返回一条广播（带 `Rssi`）。免驱动。
  - `aap`（manifest 中 `optional: true`）：经第三方 L2CAP 驱动（NTPodsAAP 或 PodBridgeAAP，均为测试签名）连 PSM 0x1001，
    `WriteOutputAsync` 发一帧、`ReadInputAsync` 收一帧。没装驱动时设备上没有这个通道，插件访问得到「未安装 AAP 驱动」。
- **插件**：`plugins/DeviceHub.Plugin.AirPods(.Ui)`。电量 / 充电 / 入耳 / 开盖来自广播（10% 精度），左右耳广播按侧合并；
  有 AAP 通道时握手 → 解锁功能 → 订阅推送，电量改用 AAP（1%），并提供聆听模式（关闭/降噪/通透/自适应，按型号限制）与对话感知。
- **已验证**：整个解决方案编译通过；解析自测全部通过（PodBridge 的两组接近配对测试向量，含左右翻转、入耳异或、
  0xF 未知哨兵；AAP 控制帧/订阅帧字节、电量推送与回显解析）；本机（无蓝牙适配器）Agent 启动正常、两个插件后端都启动；
  App 自检（现会逐个加载所有插件的 XAML）中 `AirPodsPage` 动态加载成功。
- **未验证**：本机没有蓝牙硬件，真机 AirPods 的枚举、广播接收、AAP 驱动收发都还没跑过；两种驱动的 IOCTL 按各自源码实现，未实测。

## USB 拓扑与诊断（2026-10-29）
核心功能（不是插件）：左侧导航「USB」。思路同 macOS 的 WhatCable：每个设备接在哪个物理接口、协商到多快、能不能更快。
- **Agent**：`Native/NativeUsb.cs` 用 USBView 同款 hub IOCTL 扫描（主控制器 → 根集线器 → 端口 → 设备，集线器递归）。
  只读：不打开设备本身，描述符请求经集线器发出，不需要管理员权限，也不走插件授权。每个端口读：
  端口属性（Type-C、外部可插拔、伴随端口）、连接信息 EX/V2（速率、端口支持的协议、"设备能更快"标志）、
  SuperSpeed+ 通道数、字符串/配置/BOS 描述符（取电、接口类别、LPM/SS/SS+ 能力、Billboard 备用模式）、PnP 节点（驱动、问题代码、子功能名）。
  一个物理 USB 3 接口在系统里是 USB 3 端口 + USB 2 伴随端口（外置 USB 3 集线器是两个集线器），扫描后按伴随关系合并成一个。
- **诊断**（`Core/UsbDiagnostics.cs`）：设备能力 > 实际速率时区分"线缆只有 USB 2.0 线芯 / 接在 USB 2.0 接口 / 受集线器限速 / 10G 设备跑 5G / 高速设备跑全速"；
  另有供电不足、过流、识别失败、带宽不足、层级过深、驱动问题代码、DisplayPort/雷电备用模式失败（Billboard）。
- **推送**：`Native/UsbWatcher.cs`（CM_Register_Notification，USB 设备/集线器接口，合并 0.5s）→ `IAppCallback.OnUsbChanged` → USB 页自动重扫。
- **调试**：`DeviceHub.Agent.exe --usb-dump` 打印一次扫描结果 JSON 后退出；设 `DEVICEHUB_USB_TRACE=1` 时 stderr 输出每一步 IOCTL。
- **实测（本 VM）**：3 个控制器（VMware xHCI/EHCI/UHCI）、4 个设备；xHCI 根集线器 USB 3 端口 1–4 与 USB 2 伴随端口 5–8 正确合并；
  自检 `SELFTEST: USB 控制器=3 设备=4 诊断=0`，UI Automation 读到树节点与详情。诊断分支用合成数据跑过 8 个用例，结果符合预期。
- **未验证**：VM 里没有真实 USB 3 / Type-C / SuperSpeed+ / Billboard 设备，BOS、SS+ 通道数、Type-C 标志与外置 USB 3 集线器的合并只按文档实现。
- **做不到**：Windows 不向用户态公开 Type-C 线缆 e-marker（线缆额定电流/速率）与 PD 协商功率，所以没有 WhatCable 的"这根线是 100W/40Gbps"信息，
  线缆质量只能从"设备能力 vs 实际速率"间接判断。

## 设备名记忆与自定义名（2026-10-29）
- 授权记录（grants.json）新增 `lastName`（设备在线时的显示名，变化才写盘）和 `customName`（用户起的名字）。撤销授权时一并删除。
- 显示名优先级：自定义名 > 上次在线时的名称 > 插件 manifest 按设备键 VID/PID 匹配到的声明名。所以没记过名字的旧记录离线时也有名字。
- 「已授权的设备」离线时照常显示名字，副标题加「未连接」；列表和设备页头都有「重命名」（留空 = 恢复默认名）。接口：`IAgentApi.RenameDeviceAsync`。
- 实测：经 Agent 管道调用 ListGrants → Rename("  我的键盘 ") → Rename("") 依次得到 GK65 三模键盘 / 我的键盘（已去空格）/ GK65 三模键盘；
  UI Automation 读到离线 GK65 行显示名字与「未连接」。设备在线时记下名字（RememberName）这一步本机没有在线的已授权设备，未实测。

## 子设备与屏幕状态提示（2026-11-03）
- **子设备**（SDK `IPluginContext.PublishChildrenAsync`）：插件给自己有 Always 授权的物理设备发布子设备（整体替换），
  键为 `父键/子Id`（`DeviceKeys`；核心生成的物理键不含 '/'）。子设备没有通道，I/O 仍用父键；`DeviceInfo.ParentKey` 标明归属。
  Agent：`DeviceBroker` 保存子设备表，枚举时跟在在线的父设备后面；`PermissionService.InheritGrant` 给子设备建继承授权
  （所以改名、记名、详情照常可用），撤销父设备授权时级联撤销并清掉子设备；插件进程停止/崩溃时清掉它发布的子设备；
  子设备不参与 manifest 匹配。App：左侧导航把子设备挂在父设备下（默认展开、离线淡显），「已授权的设备」列表不单列子设备。
- **罗技插件拆分**：接收器页只显示配对表；每个配对设备是接收器下的子设备，有自己的设置页（DPI、滚轮…）。
  子设备 Id 用配对寄存器里的序列号（Unifying 0x30+n-1 [1..4]、Bolt 0x50+n [4..7]），读不到时沿用缓存，再退到 `slotN`。
  打开接收器连接通知（寄存器 0x00 |= 0x0100，只加这一位），后台监听 0x41 连接/断开与 0x1000/0x1004 电量事件；
  在本软件里改过的 DPI/回报率/指针速度/SmartShift/滚轮/Fn 按设备键存储，设备重新上线时只写回与当前值不同的项。
- **屏幕状态提示**（SDK `INotifier.ShowOsdAsync`）：插件只能填模板字段（`OsdContent`：种类、Fluent 字形、≤40 字文本、可选 0–100 进度）。
  Agent 校验设备归属（Always 授权，含子设备）、截断文本、字形限定私用区，并由核心填写设备名（自定义名 > 上次显示名 > 核心名称），插件无法隐藏。
  App：`Services/OsdWindow` 在主显示器工作区底部居中弹出，3 秒后隐藏，不抢焦点、不进任务栏/Alt+Tab，新提示替换旧提示。界面未运行时不显示。
- **实测**：用模拟 Unifying 接收器（内存 HID++ 应答，槽位 1 = 带 DPI/统一电池的鼠标）驱动真实插件后端，15 项全部通过：
  子设备发布（序列号 A1B2C3D4）、接收器/子设备两种视图、子设备页写 DPI、接收器页拒绝设备设置、
  0x41 断开 → 离线 + 断开提示（发到子设备键）、0x41 重连 → 自动写回 DPI 1600 + 连接提示（鼠标图标、电量 75%）、电量跌破 15% 与开始充电提示。
  App 自检新增一次状态提示：`SELFTEST: 屏幕状态提示窗口已显示`，四个插件页面（含改过的罗技页）均加载成功。
- **未验证**：本机没有罗技硬件，真接收器的序列号偏移、0x41 时序和通知开关只按 Solaar 实现；Agent 侧子设备/授权继承/OSD 校验只编译和走查，
  未经真设备端到端运行；VM 截图为空白，提示窗的外观（亚克力、圆角、位置）未目视确认。

## 授权列表修复、离线设备常驻侧边栏、USB 页改版、Unifying 配对（2026-11-03）

- **已授权列表为空**：`ListGrantsAsync` 用 `ToDictionary(DeviceKey)` 建设备表，设备键重复时整个调用抛异常，App 那边列表就一直是空的。
  重复键的来源：蓝牙服务按 DeviceInformation.Id 记设备，同一台设备有两条（重新配对、经典 / LE）就会得到两个相同的 `bt:` 键；
  HID 端序列号相同但 ContainerId 不同的几组也会算出同一个键。现在三处都按键合并（蓝牙优先取已连接的那条），
  App 读授权失败时显示错误原因而不是空白。用户机器上的实际原因未复现确认（本机 1 条授权，能正常列出）。
- **侧边栏**：已授权但不在线的设备用授权记录补一个离线占位，一直留在侧边栏（淡显，页头副标题"未连接"），子设备照样挂在父设备下。
  自检：`正在管理=1（离线 1） 授权记录=1`（本机 GK65 未接）。
- **USB 页**：诊断收成一行汇总（"发现 N 条诊断" + 查看问题），列表撑满到底部；点设备弹出详情对话框（诊断、全部信息、复制）。
  自检导航到 USB 页无异常；对话框的外观与交互未能在本机截图确认。
- **Unifying 配对**：接收器页"配对新设备"（短寄存器 0xB2 动作 1，30 秒窗口），0x4A 通知反映窗口开关与失败原因，
  0x41 带来新槽位时重读配对表并提示"已配对：名称"；配对设备行上"取消配对"（0xB2 动作 3）。
  取消配对后插件调用新增的 `IPluginContext.ForgetChildAsync`，核心撤销该子设备的授权并移除，不会作为离线占位残留。
  Bolt（需要配对码）和 Nano / LIGHTSPEED 接收器暂不支持。
  模拟接收器 25 项检查全部通过（新增 10 项：开窗写入、新设备出现与提示、0x4A 正常关闭与"槽位已满"、取消配对写入与通知核心、子设备页不能取消配对）。
  真机上 0xB2 / 0x4A 的字节布局按 Solaar 实现，未经实测。

## 鼠标测试、内存与 I/O 优化、0.2.0（2026-11-04）

- **插件内存**：插件页原来显示进程工作集，里面大半是和其他进程共享的 .NET 运行时映像；空闲插件实测工作集约 38MB、专用内存约 10MB。
  现在改报专用内存（接近任务管理器"内存"列）。设备活跃时插件不停分配短命对象，GC 按 CPU 三级缓存定第 0 代预算
  （本机 36MB 三级缓存），模拟轮询负载下专用内存 30MB；Agent 启动插件进程时设 `DOTNET_GCgen0size=0x400000` 后降到 19MB，
  第 0 代回收从 8 次变 38 次（8 秒）。这个设置只认环境变量，`runtimeconfig` 里的 `System.GC.Gen0Size` / `GCgen0size` 实测无效。
  空闲时调 `gcConcurrent`、`TieredPGO`、gen0 都没有差别（差异 < 0.5MB）。
- **Agent HID 读**：原来每次读输入报告都在线程池线程上同步等到超时，插件轮询越多线程越多。改成重叠 I/O 的完成事件交给线程池等待，
  不占线程；超时 ≤ 2ms 或已有缓存报告时直接同步完成。本机能打开的 HID 集合上实测：300ms 超时按时返回 null；并发 50 个 1 秒读线程数 13 → 14；
  读到一半关闭句柄不崩（I/O 结束前句柄被引用住）。真实设备上收发报告未经实测。
- **罗技监听**：每轮只用 1ms 取走驱动缓冲区里的报告，再在锁外歇 200ms（原来持锁每个集合等 50ms），RPC 次数约减半，
  用户操作不再排在监听的等待后面。模拟接收器 25 项检查仍全部通过。
- **按键与移动测试**：鼠标 / 轨迹球页新增测试区：五个按键和滚轮的按下状态与次数、指针轨迹、移动速度、回报率估算（系统按帧合并指针消息，只供参考）。
  不区分是哪只鼠标。面板在代码里创建：第一次把插件自定义控件写进 XAML 时，App 运行时解析插件页面报 `XAML parsing failed`
  （App 不认识插件里的类型），改为代码创建后四个插件页面自检全部加载成功。交互本身未能在本机截图确认。
- 版本：应用 0.2.0，罗技插件 1.1.0，其余插件不变。

## 罗技页面分页、插件版本管理、插件自定义图标（2026-11-04）

- **两层标题**：DeviceHub 的页头已经显示设备名和副标题，罗技页面又画了一遍"图标 + 名称 + 连接状态"，鼠标页还有第三层"设备概览"。
  现在罗技页面和 GK65 一样不画自己的标题，只在顶部放分页（鼠标 / 轨迹球：设置、按键测试）和"重新读取"，连接信息合成一行。
- **插件升级**：原来从插件页安装新版本只是"暂存"：插件进程被停掉，要点"立即重启界面"才切换，点"稍后"插件就一直停着；
  重装同一版本时插件停了不再启动。现在安装即切换（停旧版本 → 切 active → 启动新版本后端，失败时把原来的启动回来），
  上一个版本保留，插件页显示"上一版本"并可一键回滚；只保留当前和上一个版本目录。附带插件在 Agent 启动时仍会自动升级，
  但不会把用户刚回滚掉的版本装回去。插件界面的程序集已加载进 App 进程，换版本后要重启界面才更新（会提示）。
  直接调 PluginManager 的 8 项检查全部通过（首次安装、升级、清理旧目录、重装同版本、回滚、附带插件不覆盖回滚、附带新版本自动升级、来回切换）。
  用 0.2.0 发布包里的 Agent 启动一次，本机罗技插件从 1.0.0 自动升级到 1.1.0，说明附带插件升级本来就是好的。
- **侧边栏图标**：`DeviceDescription.Glyph`（Segoe Fluent Icons 私用区单个字符，核心校验），核心随授权记录保存，离线时照样显示。
  AirPods 耳机、GameSir 手柄、GK65 键盘、罗技接收器 USB、配对设备按类型（鼠标 / 键盘…）。模拟接收器检查：鼠标 U+E962、接收器 U+E88E。
  本机 GK65 未接，自检显示默认图标（从没在新版本下在线过，还没有记下的图标）。
- 版本：应用 0.2.1；插件罗技 1.2.0，AirPods / GameSir / GK65 1.0.1。四个插件页面自检全部加载成功；分页与图标的实际外观未能截图确认。

## 离线设备误报"未授权"、主页与插件小组件（2026-11-04）

- **离线误报未授权**：侧边栏的离线设备（已授权、设备不在）打开页面时，插件从没 Attach 过它，`GetStateAsync` 返回默认状态 `Authorized=false`，页面显示"未授权"。
  另外 Agent 的 `RequestDeviceAccessAsync` 在设备不在线时直接返回 Denied；GK65 的 `DetachAsync` 拔掉接收器时把 `Authorized` 清掉。
  修复：SDK 新增 `IDeviceChannel.GetAccessAsync`（不弹窗、离线可查，经 `IHostToAgentApi.GetDeviceAccessAsync`）；四个插件第一次被问到未接管的设备时向核心查授权；
  `RequestDeviceAccessAsync` 离线时返回已有授权；GK65 增加 `Connected`，离线时读回上次保存的设置、只读显示"未连接"，Detach 不再清授权。
  实测：本机 GK65 授权在、接收器未接，自检 `离线设备 hid:1a2c:7fff:… authorized=True connected=False`。
- **主页**：侧边栏顶部新增「主页」并作为默认页。卡片网格（VariableSizedWrapGrid，单元 184×160，小 1×1 / 宽 2×1 / 大 2×2），
  右键或编辑模式下的「…」菜单：打开设备页、改尺寸、前移/后移、移除；编辑模式可拖动排序，卡片内容不响应点击。「添加小组件」可多选。
  布局存在 `%LOCALAPPDATA%\DeviceHub\app\home.json`；没有布局文件时按当前设备生成默认布局（设备总览 + 每台设备的第一个小组件），用户改过才落盘。
  内置小组件「设备总览」。设备被撤销授权 / 插件卸载 / 插件不再提供该小组件时卡片显示原因，不报错。
- **插件小组件 API**：plugin.json 新增 `uiWidgetType` 与 `widgets[]`（id、title、description、glyph、sizes、defaultSize、targets = any/physical/child）；
  UI 程序集实现 `IPluginWidgetProvider.CreateWidget(widgetId, size, bridge)`，bridge 与设备页同一套状态/方法。核心画外框（设备名、图标、菜单），插件只给内容。
  `WidgetKit` 提供状态绑定（显示时读取 + 订阅推送，隐藏时退订）、电池图标和统一的数值/说明构件。
- 首批小组件：罗技 电量（接收器上列出所有配对设备）/ DPI（上一档、下一档）；GameSir 电量；AirPods 电量（左 / 右 / 盒）/ 降噪模式；GK65 灯效（模式 + 亮度）。
  自检新增：每个插件每个小组件的每种尺寸都创建一次，11 个组合全部成功。
- 版本：插件罗技 1.3.0，AirPods / GameSir / GK65 1.1.0（依赖新 SDK 的 `GetAccessAsync`，需配新版 Agent）。
- **未验证**：VM 截图为空白，主页与小组件的外观、拖动排序、右键菜单未目视确认；没有罗技 / AirPods / GameSir 真机在线，小组件的在线数据只按 DTO 走查。
