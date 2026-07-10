# YiboCodexHUD

## 项目目标

`YiboCodexHUD` 是一个面向 Codex/ChatGPT 桌面端的非侵入式辅助工具。

目标是把 Codex/ChatGPT 账户的剩余用量信息显示在主窗口标题栏附近，减少用户进入多级菜单查看的成本。

目标显示内容：

- 短窗口用量，例如 `5 小时`
- 长窗口用量，例如 `1 周`
- 两个窗口的已用百分比
- 重置时间
- 可用重置次数

## 项目约束

- 不修改 Codex/ChatGPT 安装包
- 不依赖前端 UI 结构
- 尽量降低对 Codex/ChatGPT 高频更新的敏感度
- UI 只做外部悬浮层

## 已确认结论

### 数据入口

- Codex/ChatGPT 桌面端存在独立 `app-server`
- 主数据入口使用 `codex.exe app-server --stdio` / `chatgpt.exe app-server --stdio`
- 已验证可调用：
  - `initialize`
  - `account/read`
  - `account/rateLimits/read`
  - `account/usage/read`

### 主数据源

标题栏显示的核心数据来自：

- `account/rateLimits/read`

可直接使用的字段包括：

- `rateLimits.primary.usedPercent`
- `rateLimits.primary.windowDurationMins`
- `rateLimits.primary.resetsAt`
- `rateLimits.secondary.usedPercent`
- `rateLimits.secondary.windowDurationMins`
- `rateLimits.secondary.resetsAt`
- `rateLimitResetCredits.availableCount`

辅助状态字段包括：

- `account/read` 返回的登录态
- `account/read` 返回的 `planType`
- `account/read` 返回的邮箱等账户信息

扩展数据源：

- `account/usage/read`
  - 可用于趋势图、历史统计、用量详情页
  - 不是第一阶段必需项

### 路线结论

- 主方案：外部悬浮层 + `app-server` 结构化数据读取
- `OCR` 和菜单自动化只作为最后兜底
- 不采用修改 Codex/ChatGPT 客户端、前端注入、DOM 依赖或 HTTP 逆向作为主方案

## 技术栈结论

主技术栈：

- `.NET 8`
- `C#`
- `WPF`
- `MVVM`
- `Win32 P/Invoke`

数据通信：

- `codex.exe app-server --stdio` / `chatgpt.exe app-server --stdio`

技术栈详细说明见：

- `TECH_STACK.md`

## 推荐架构

### 数据采集层

职责：

- 启动并维护 `codex.exe app-server --stdio` / `chatgpt.exe app-server --stdio`
- 完成 `initialize`
- 调用 `account/read`
- 调用 `account/rateLimits/read`
- 可选调用 `account/usage/read`
- 统一做数据模型转换

建议统一模型：

```ts
type UsageSnapshot = {
  accountEmail: string | null;
  planType: string | null;
  shortWindowUsedPercent: number | null;
  shortWindowMinutes: number | null;
  shortWindowResetsAt: number | null;
  longWindowUsedPercent: number | null;
  longWindowMinutes: number | null;
  longWindowResetsAt: number | null;
  resetCreditsAvailable: number | null;
  fetchedAt: number;
};
```

### 展示层

职责：

- 找到 Codex/ChatGPT 主窗口
- 监听窗口位置、尺寸、最小化状态
- 在标题栏目标区域绘制轻量悬浮层
- 默认点击穿透
- 尽量不影响 Codex/ChatGPT 原交互

### 调度层

职责：

- 首次启动时加载一次
- 周期刷新，例如每 3 到 10 分钟
- 在关键交互后主动刷新
- `app-server` 断开后自动重连

## 当前开发重点

当前核心风险已经不是“能不能拿到数据”，而是显示层的稳定性和自然度。

下一步建议顺序：

1. 先封装独立的 `app-server` 客户端模块
2. 将 `account/rateLimits/read` 转成内部统一模型
3. 做最小窗口跟随层，先只显示文本
4. 再补样式、刷新策略、异常处理
5. 最后再考虑设置页、历史统计、用量详情页
