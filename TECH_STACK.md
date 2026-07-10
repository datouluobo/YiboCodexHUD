# YiboCodexHUD 技术栈选型

## 选型结论

`YiboCodexHUD` 的主实现技术栈确定为：

- 运行时：`.NET 8`
- 语言：`C#`
- UI 框架：`WPF`
- 架构模式：`MVVM`
- 系统交互：`Win32 P/Invoke`
- 数据协议接入：`codex.exe app-server --stdio` / `chatgpt.exe app-server --stdio`
- 序列化：`System.Text.Json`
- 日志：`Microsoft.Extensions.Logging`
- 配置：`appsettings.json` + 本地用户配置文件

不作为主方案的技术栈：

- `WinUI 3`
- `Python + PySide6`
- `Electron`
- 浏览器注入或前端 DOM 改造

## 为什么选这套

这个项目的关键不是做一个通用桌面应用，而是做一个长期挂在 Windows 上的轻量辅助层。它要解决的核心问题有：

- 跟随另一个原生桌面窗口
- 在标题栏附近稳定显示
- 支持透明、置顶、点击穿透
- 对 Codex/ChatGPT 更新尽量不敏感
- 以最少依赖读取结构化用量数据

在这些约束下，`C# + WPF + Win32 P/Invoke` 是目前风险最低的一套。

### 1. 为什么不是 WinUI 3

`WinUI 3` 的现代感更强，但对本项目并不是决定性优势。

它的问题在于：

- 对外部窗口跟随和各种细粒度窗口样式控制，最终仍然要大量回到 Win32
- 透明窗体、输入穿透、悬浮层这类需求的经验积累不如 `WPF` 充足
- 工程排障成本通常高于 `WPF`

对于一个“系统增强小工具”来说，稳定性和开发效率比 UI 新旧更重要。

### 2. 为什么不是 Python + PySide6

`PySide6` 很适合快速验证，但不适合作为长期主实现。

主要原因：

- 分发体积通常更大
- 冷启动和常驻体验不如 `.NET`
- Win32 深度集成时维护体验不如 `C#`
- 后续如果要做自启动、托盘、异常恢复、日志和升级，`.NET` 更顺手

因此 `Python + PySide6` 可以作为一次性原型工具，但不建议作为正式工程。

### 3. 为什么不是 Electron

这个项目没有必要引入浏览器运行时。

Electron 的问题很直接：

- 常驻资源占用偏高
- 做透明悬浮层和窗口跟随并不会更简单
- 对“非侵入式、轻量、稳定”的目标帮助不大

## 分层建议

建议把工程拆成四层：

### 1. Core

职责：

- 定义领域模型
- 定义接口
- 放纯业务逻辑

建议内容：

- `UsageSnapshot`
- `AccountStatus`
- `IRateLimitService`
- `IClock`

### 2. Infrastructure

职责：

- 启动并管理 `app-server`
- 处理 `stdio` 请求响应
- JSON 解析
- 配置读取
- 日志落盘

建议内容：

- `CodexAppServerProcess`
- `CodexProtocolClient`
- `RateLimitService`
- `SettingsStore`

### 3. Desktop UI

职责：

- 悬浮层窗口
- 托盘菜单
- ViewModel
- 刷新状态展示

建议内容：

- `OverlayWindow`
- `OverlayViewModel`
- `TrayIconHost`
- `SettingsWindow`

### 4. Windows Interop

职责：

- 查找 Codex/ChatGPT 主窗口
- 读取窗口矩形
- 监听前台窗口变化
- 设置点击穿透和扩展样式

建议内容：

- `WindowTracker`
- `Win32WindowFinder`
- `OverlayPositioner`

## 关键库建议

建议尽量少依赖，优先使用 .NET 自带能力。

首选：

- `CommunityToolkit.Mvvm`
- `Microsoft.Extensions.DependencyInjection`
- `Microsoft.Extensions.Logging`
- `Microsoft.Extensions.Options`

可选：

- `Serilog`
  - 仅当需要更强的文件日志和诊断体验时再加
- `Hardcodet.NotifyIcon.Wpf`
  - 如果托盘能力想快速落地，可以评估

不建议一开始引入：

- 重型 UI 组件库
- 复杂消息总线
- ORM
- 本地数据库

当前阶段这些都不是刚需。

## 通信方式结论

主通信方式确定为：

- 本地进程通信：`stdio`

补充说明：

- 这里不需要 `websocket`
- 也不建议为了“看起来更标准”去额外包一层 `websocket`
- 如果后续需要调试探测外部接口，默认优先 `http`

这和当前项目约束是一致的：

- 主目标是稳定读取现成结构化数据
- 不主动耦合前端实现
- 不把工程复杂度浪费在不必要的传输层上

## 发布方式建议

建议初期使用：

- `dotnet publish`
- Windows x64 单文件发布

建议参数方向：

- `SelfContained=true`
- `PublishSingleFile=true`

是否开启裁剪可稍后再评估，不建议第一版就激进压缩。

## 第一阶段实现边界

第一阶段只做这些：

- 读取 `account/read`
- 读取 `account/rateLimits/read`
- 转成内部统一模型
- 在 Codex/ChatGPT 标题栏附近显示一行文本
- 支持自动刷新
- 支持异常状态提示
- 支持托盘退出和手动刷新

先不做：

- 趋势图
- 历史统计
- 设置页大而全配置
- 复杂动画
- 多窗口复杂布局

## 最终建议

如果目标是“尽快做出一个能长期维护的正式版本”，建议直接按下面这套开工：

- `.NET 8`
- `C#`
- `WPF`
- `MVVM`
- `Win32 P/Invoke`
- `app-server --stdio`

这套方案最符合当前项目的真实需求，也最能平衡：

- 开发速度
- 系统集成能力
- 维护成本
- 对 Codex/ChatGPT 更新的抗脆弱性
