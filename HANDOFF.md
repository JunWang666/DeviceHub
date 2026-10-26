# DeviceHub 续作提示词(新会话用,开启 Computer use)

把下面整段贴进新会话:

---

我在做 **DeviceHub**(`C:\Code\DeviceHub`):WinUI3 聚合外设管理软件,用来替代罗技之类的厂商软件。本体负责进程管理、数据管理、连接管理(访问外设前必须经用户授权)和插件热管理;设备管理能力(包括 UI)由插件提供。下面是前一个会话做完的部分,和这次要你做的事。

## 已完成(不要重做)
- **架构(方案 B,三进程)**:`DeviceHub.Agent`(后台核心,控制台 exe)、`DeviceHub.PluginHost`(每个插件一个隔离进程,Job Object 管控,带心跳和退避重启)、`DeviceHub.App`(WinUI3 界面)。进程间用 Named Pipe + StreamJsonRpc 2.25.29 通信,两端都用 SystemTextJsonFormatter。环境:.NET 10、Windows App SDK 1.8。解决方案文件是 `DeviceHub.slnx`。
- 插件 UI 在 App 进程内用 `Assembly.LoadFrom` 加载,更新时重启 App 生效。**关键发现**:非打包 WinUI3 启动时会自动加载 exe 同目录下所有 `*.pri`,所以把插件的 `.pri` 和 xbf 拷到 App exe 旁边,动态 XAML(含 x:Bind)就能正常工作,不需要 makepri merge。由 `tools/deploy-plugin.ps1` 部署。
- 插件拿不到设备句柄:设备 I/O 全部由 Agent 的 `DeviceBroker` 代理,并受授权检查(grants.json)。
- 界面:Mica 标题栏;左侧导航顶部是「新设备」和「插件」,下面列出已管理的设备。新设备流程:点「打开」→ 弹出候选插件 → 选一个点「授权并使用」。
- 自检:设置 `DEVICEHUB_SELFTEST=1` 后启动 App,无界面跑完整条链路,已通过。详见 `VERIFICATION.md`。
- **GK65 插件**:`plugins/DeviceHub.Plugin.Gk65`(后端)和 `plugins/DeviceHub.Plugin.Gk65.Ui`(灯光 + 按键测试两部分;改键与宏已移除,见 VERIFICATION.md)。功能数据从驱动配置文件提取到 `plugins/Gk65.Shared/gk65-data.json`:20 种灯效、117 个键、22 个功能/媒体键、6 个灯光区域、3 套配置、宏模式。
- **`Gk65Protocol.cs` 的写入协议是暂定的**:写入帧(report 0x07,命令 0xF0 系列)标注 Verified=false/"待验证",字节布局是从静态分析猜的,**传输方式也不对**(见下)。

## 真机已查明的情况(只读探测得出)
- GK65 三模键盘,通过 USB 接收器直通进来:`VID_1A2C PID_7FFF`(驱动配置里写的是 7F49,以真机为准)。
- **配置通道是 `mi_02`**:UsagePage 0xFF01 / Usage 0x01,Input 和 Output 报告都是 65 字节,**没有 Feature 报告**。所以收发要用 WriteFile/ReadFile 或 HidD_SetOutputReport,而**不是** Get/SetFeature。设备是一问一答,不会主动推送数据。
- `mi_00` 是系统键盘集合,系统受保护,打开会报 error 5。`mi_01` 下有 col01–04,其中 col04 是厂商自定义输入(FF01,4 字节)。
- 官方驱动:`C:\Program Files (x86)\700M\ShinetekTools.exe`。
- 工具:`tools/HidProbe`(只读探测;`--capture N` 在 N 秒内并发读取 mi_02 的回包);`tools/capture-hid.ps1`(会自动请求管理员权限的 ETW 抓包,provider 是 Microsoft-Windows-Input-HIDCLASS。**未验证**是否能拿到报文内容)。
- 前一次 60 秒被动抓包没抓到任何东西:那段时间官方软件没有被操作。
- 本机**没有** Wireshark/USBPcap。PowerShell 5.1 按 ANSI 读取脚本,所以脚本只能写 ASCII,中文用码点拼出来。

## 这次要你做的(需要 Computer use)
1. 先启动抓包:`HidProbe.exe --capture 120`(放后台);可以的话再加上 ETW 抓包(要点一次 UAC)。
2. **用 Computer use 亲自操作 ShinetekTools**:每次只做一个无害的操作,并记下时间点,比如 DPI 调一档、切换一个灯效、改一个颜色、切换 P1/P2。做完把设置**改回原值**。
3. 根据抓到的报文还原命令格式:读命令和写命令、校验方式、报文长度。如果回包抓不到写入内容,就换个办法拿官方软件发出的数据(比如 ETW 带报文内容的事件,或者 API Monitor 之类的 hook 方案,先跟我商量)。
4. 修正 `Gk65Protocol`,让 `DeviceBroker` 支持通过 mi_02 的 Output/Input 报告收发(65 字节),匹配 PID 7FFF;核实过的命令改成 Verified=true。
5. **安全约束**:不要往真键盘盲写猜出来的帧。只发从官方软件抓到、已确认含义的帧;先试读命令,再试写命令,而且写之前先告诉我。

---
