# CLI 与 MCP 实测记录

> 历史记录：新环境的真实行情已于 2026-11-14（北京时间）复测通过。最新结果见 [真实行情上线验收](11-真实行情上线验收.md)。下方保留当时网络阻断的原始结论。

日期：2026-11-13。测试当前工作区代码，Linux x64，SDK 10.0.403。使用独立临时数据目录，没有读写用户实际自选股、策略或提醒数据。

## 已验证通过

| 检查 | 结果 |
|---|---|
| Release 构建生产 CLI 与测试专用行情主机 | 通过 |
| 业务、缺失值、参数、取消与共享存储冲突测试 | 9 项通过，0 失败、0 跳过 |
| 覆盖门禁反例测试 | 4 项通过 |
| 全部已登记操作经 CLI 与 MCP 执行 | 27 项全部通过，使用确定性离线行情；双入口结果、Schema 和权限一致 |
| 生产 CLI/MCP 真实进程 | 通过：本地数据读写、并发写入、定价、指标、工具发现、错误返回、默认只读、显式写入、会话结束 |
| 重新发布 Linux x64 自包含可执行程序并执行同一进程检查 | 通过，包括原生 SQLite 初始化与行情源注册 |
| 真实行情失败后的 MCP 会话恢复 | ping 和期权定价继续通过，无需重启服务 |

当前运行完整证据位于：`build/out/verify-cli-mcp-xXaAozhS/`。

- `Automation.trx` / `unit-tests.log`：本次 9 项 .NET 测试。
- `coverage-guard.log`：4 项覆盖门禁反例。
- `capability-coverage.json` / `capability-coverage.log`：逐操作 CLI/MCP 覆盖。
- `production-smoke.log` / `published-smoke.log`：生产程序与自包含程序进程检查。
- `live-results.json`：真实网络请求、退出状态及失败后恢复检查。
- `publish/`：本次重新生成并已测试的自包含程序。

本次按 CLI/MCP 范围运行以上检查，没有把之前的 558 项全量回归重新计作本次结果。

## 真实行情：受环境阻断，尚未验证成功

对 Yahoo chart 和 CBOE AAPL 端点的直接 HTTPS 探测，均在云代理 CONNECT 阶段收到 **403 Forbidden**，未获得上游行情响应。运行环境显示 restricted 网络策略已生效，当前自定义允许域名仍为空。

生产发行程序查询 AAPL / MSFT 的结果：

- CLI：约 8.13 秒后退出码 130，JSON 为 `success=false`、`error.code=cancelled`。
- MCP `get_quotes`：约 8.07 秒后 `isError=true`，结构化错误同为 cancelled。
- 同一 MCP 会话接着执行 ping 和 Black-Scholes 定价通过（S=K=100、T=1、r=0.05、vol=0.2，价格约 10.45058357）。

这证明了该网络限制下的超时和恢复处理，不能证明真实行情功能已经取得有效数据，也不能据此判断第三方服务不可用。

已将以下四个必要域名加入云环境配置草稿，保留原有软件包域名预设，没有直接修改运行中的网络策略：

- query1.finance.yahoo.com
- query2.finance.yahoo.com
- fc.yahoo.com
- cdn.cboe.com

在环境设置审阅并保存网络草稿后，应刷新状态并用新连接重测；发布后可供后续任务复用。草稿保存不代表运行中的网络放行已经生效。

## 平台范围

本次实际执行平台为 Linux x64。GitHub Actions 配置了 Windows/macOS 测试，但本记录不声称已在这些系统或实际桌面 MCP 客户端中运行。MCP 使用标准 stdio JSON-RPC 客户端与生产官方 C# SDK 服务端实际通信。
