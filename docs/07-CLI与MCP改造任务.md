# CLI 与 MCP 完整改造任务

基线：2026-11-13，已 fetch origin/main 并 fast-forward 检查，0780481 已是远端最新提交。

目标：保持 Windows/macOS 本地桌面应用，新增无需启动 UI 的跨平台 CLI 与本地 stdio MCP。共用业务服务，数据沿用本地目录，不部署 Web 服务。

## 顺序任务与验收

- [x] T1：建立无 UI 的 Application 服务层和 CLI 项目，接入固定版本官方 MCP C# SDK。
- [x] T2：实现带参数校验、取消/超时、分页和来源元数据的行情查询；支持搜索、报价、K线、到期日、期权链、基本面、财报、新闻、数据源诊断。
- [x] T3：复用分析库实现定价/IV、技术指标、策略分析；NaN/无界结果以合法 JSON 和显式状态表示，不伪造价格。
- [x] T4：统一跨平台数据目录，提供自选股、策略、提醒、设置的读取及明确支持的修改操作；为桌面/CLI/MCP 的共享 JSON 加入跨进程锁与冲突检测，防止旧快照覆盖新数据。
- [x] T5：完成 CLI 帮助、JSON/人读输出、错误和退出码；MCP 使用同一服务并默认只读，显式 --allow-writes 开启修改工具。
- [x] T6：实现自动化集成测试：CLI 实际进程、MCP 初始化/工具发现/调用/错误/取消、离线业务数据和共享存储冲突。回归原有跨平台测试。
- [x] T7：提供可重复发布脚本、实际验证的本机包、客户端配置与命令文档；更新根 AGENTS.md，要求未来功能评估桌面/CLI/MCP 三个入口。
- [x] T8：完成最终检查与任务验收记录，明确原生平台和实时联网验证的边界。

## 本版边界

- CLI 与 MCP 查询和分析可独立于桌面运行；不自动操作桌面窗口。
- 持续提醒仍由桌面程序执行。命令行可管理提醒规则和读取历史，不额外启动第二套提醒引擎，避免重复通知。GUI 运行中若发生外部写入，旧快照保存必须报冲突，重新加载后才能继续保存。
- MCP 不监听网络端口，不提供任意路径读写、任意 URL 请求或任意代码执行工具。CLI 文件输入由操作者明确指定。
- 不自动发布 GitHub Release，不推送远端；版本包保存在忽略的本地输出目录。

## 执行记录

1. T1–T3：新增 Meili.Application（命名空间 Meili.Automation，避免与 WPF/Avalonia Application 类型重名）与 Meili.Cli，固定 ModelContextProtocol 2.4.0 官方 SDK；27 项共享操作统一用于 CLI 和 MCP。
2. T4：将无 UI 的 StrategyStore 移入新层并保留公共命名空间；SettingsStore、AlertStore、StrategyStore 使用 SnapshotFile。旧快照写入必须报 data_conflict；客户端读取每次刷新。
3. T5：CLI 支持完整帮助、命令别名、JSON 文件输入、分页、超时和退出码；stdio MCP 默认仅公开 17 个读取/计算工具，--allow-writes 后公开全部 27 个操作，调用时再次验证权限。
4. T6：原 549 个跨平台测试通过；新增 9 项业务/存储/取消测试通过；Python 标准库真实进程测试通过（CLI/MCP 定价一致、协议初始化/发现/调用/错误、默认只读、写入模式、EOF 退出、完成请求的取消通知兼容、双 CLI 并发写入和长运行 MCP 读取更新）。进行中的异步请求取消由应用层测试验证，不把已完成请求的通知兼容等同于端到端取消验证。
5. Windows WPF 应用及测试项目交叉编译通过，0 警告/错误；未在 Linux 上执行 Windows UI 测试。
6. Linux x64 自包含发行可执行文件已通过同一组 CLI/MCP 真实进程测试；其余五个架构交叉发布成功。

## 最终验收（0.6.0）

- 完整回归目录：`build/out/tests/run-q5QAsXfz`。Core 173、Data 94、Analytics 206、Shared 18、Mac 58、Automation 9，合计 **558 项通过、0 失败、选中的离线测试 0 跳过**。Category=Live 已明确排除，不计入通过数。
- 开发版 CLI/MCP 真实进程检查通过；Linux x64 自包含发行可执行程序再次通过（包括原生 SQLite 初始化、八个行情源注册、版本号、17/27 工具数量、协议、权限和并发）。
- Windows WPF 应用及测试项目交叉编译通过（0 警告、0 错误），未运行 Windows 原生测试。
- 六套自包含发行包：linux-x64、linux-arm64、win-x64、win-arm64、osx-x64、osx-arm64，位于 `build/out/cli`，每包附 SHA-256 校验文件。除 Linux x64 外仅完成交叉发布，尚未在对应系统/架构运行。
- 根 `AGENTS.md` 已加入三入口功能评估、共享业务服务、CLI/MCP 契约、权限、并发存储与真实进程测试要求。
- 既有的 macOS 设置页语言标签滞后不属于本次改造；没有为通过测试而修改原有断言。
- 未执行真实行情联网验收、Windows/macOS 原生 UI 验收或 PowerShell 发布脚本执行；不据此声称这些行为已验证。MCP 的网络访问仍受本地代理和第三方行情源限制。
- Git 变更留在本地供审阅；未提交、推送或发布 GitHub Release。
