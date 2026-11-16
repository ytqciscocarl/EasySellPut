# MCP 全功能覆盖审计

本次审计从桌面功能出发，而非仅检查已登记工具。原 28 个操作均有双传输场景，但缺少完整 K 线指标、期权补全与历史、策略预设/逐腿分析、画线、部分偏好和维护操作。现补齐为 45 个操作，CLI 与 MCP 共用 Application 和 catalog；没有为 MCP 引入桌面依赖。

## 功能对应

| 桌面业务 | MCP 工具 |
| --- | --- |
| 搜索、报价、历史 OHLCV | search_symbols / get_quotes / get_bars |
| 全部图表指标 MA、EMA、BOLL、MACD、RSI、KDJ、成交量与对比标的 | get_chart_analysis（计算完整历史后分页） |
| 图表画线持久化 | get_chart_drawings / add_chart_drawing / update_chart_drawing / delete_chart_drawing / clear_chart_drawings |
| 期权到期日、筛选与原始报价 | get_expirations / get_option_chain |
| IV/Greeks 补全、价差与宽价差、价外距离、OI 条形比例、IV 微笑数据、OI 日变化 | analyze_option_chain |
| 定价利率来源、股息率及缺失回退 | get_pricing_assumptions / calculate_option |
| IV Rank/Percentile、ATM 期限结构 | get_volatility_insights |
| 记录共享本地 IV/OI 历史 | record_option_snapshot（写入） |
| 全部策略预设及近远期组腿 | list_strategy_presets / build_strategy |
| 自定义组合、到期与到期前收益曲线、Greeks、盈亏平衡、概率、Reg-T 估算 | analyze_strategy |
| 假设价格/时间/IV 变化的组合与逐腿价格、盈亏、解释事实 | strategy_what_if |
| 保存、读取、删除策略 | save_strategy / get_saved_strategies / delete_strategy |
| 自选分组增删改、标的增删、排序 | 原有 watchlist 工具及 move_watchlist_symbol |
| 公司资料、年度/季度财务数据、新闻、财报/除息事件 | get_fundamentals / get_financials / get_news / get_market_events |
| 提醒规则增删改/启停、记录查询/清除 | 原有 alert 工具及 clear_alert_history |
| 本进程数据源健康状态、主动检测 | get_source_health / probe_data_sources |
| 安全偏好读取与局部修改、指标参数、行情超时、代理模式、语言/主题/颜色等 | get_settings / update_preferences |

## 明确边界

- 桌面窗口导航、选中态、缩放、布局、原生阅读器和外部浏览器/文件管理器操作是呈现。MCP 返回同一业务数据，不控制窗口或直接生成 GUI 草稿。客户端可将 build_strategy 的 legs 直接传给分析和保存；组腿编辑通过修改 legs 后重新分析完成。
- CSV 文件选择与本地保存对话框不做成任意文件写入工具。CLI JSON/MCP 结构数据覆盖其底层内容，客户端自行决定导出格式和目标文件。
- 持续提醒、托盘通知及“立即检查”对运行中监控状态的更新仍由桌面的单一监控器负责；MCP 管理规则、查询报价/历史，不启动第二套监控或发送重复通知。
- 代理 URL/凭据、任意文件路径、窗口几何不对 MCP 开放。偏好补丁可选现有代理模式，不接受代理地址。网络设置改动需重启 MCP 才应用；桌面在外部写入后也要重新加载/重启再编辑。
- IV 历史必须由真实记录积累，少于 20 个过去日期样本时 Rank/Percentile 不可用。read-only 查询不新增 IV/OI 记录；`record_option_snapshot` 可选 expiry 记录所选到期日 OI，IV 仍沿用 IV30/ATM 近 30 天口径；该工具显式写入当前观测，不补造历史。SQLite 初始化/行情缓存不等同于用户历史记录写入。
- `probe_data_sources` 复用桌面的逐来源探测，状态仅说明本进程该探测端点的结果，不代表该来源全部能力成功；未知不当成健康。
- 所有写工具仍需 `--allow-writes`，默认发现列表隐藏且调用再次拒绝。画线存储已迁移为 SnapshotFile 冲突保护，旧快照不能覆盖新数据。

## 使用示例

```sh
easysellput chart AAPL --interval 1d --limit 100 --json
easysellput option-analysis AAPL --expiry 2027-06-18 --json
easysellput insights AAPL --json
easysellput presets --json
easysellput get_pricing_assumptions AAPL --json
easysellput probe_data_sources --source-id yahoo --json
```

复杂参数使用 `--input FILE`。例如 `strategy_what_if` 输入：

```json
{"legs":[{"type":"Call","side":"Long","quantity":1,"strike":100,"premium":3,"expiry":"2027-06-18","iv":0.25}],"spot":110,"valuationTime":"2026-11-14T08:00:00Z","horizonDays":7,"volatilityShift":0.05}
```

新工具均为兼容性增加，原 schemaVersion=1 保留。analyze_strategy 额外返回 curve.valuationPnl；未请求 valuationTime 时为 null。save_strategy 新增可选 kind（默认 Custom），可保留预设类型。build_strategy 返回的 premiums 是调用方提供的参考价，isExecutable=false；无足够报价时 status=insufficient_quotes，不补造合约。

## 持续校验

`tests/automation/desktop_capabilities.json` 审核 17 个共享 view-model 文件、67 个 RelayCommand：每个命令关联业务工具或具体界面/监控归属/安全例外。新增/移除文件和命令、引用不存在的工具会使能力检查失败。自动检查不能判断既有函数内部新增了什么业务，因此 AGENTS.md 仍要求评审业务逻辑与持久化变更。

`capability_cases.json` 为 45 个操作各提供一个有效场景、结果断言和双传输比较路径。新工具未登记场景仍失败；MCP 初始化、发现、权限、schema 与 CLI 目录逐项匹配。单元测试另外验证完整指标分页、真实 Analytics Greeks、IV 历史不被只读查询写入、全部预设可构建并直接保存、逐腿未来估值、偏好安全合并及画线冲突保护。

沿用完整离线回归、真实 CLI/MCP 进程检查和 Windows x64/ARM64、macOS Intel/Apple Silicon 的实际发行包与原生 WebView CI。离线固定数据不证明实时联网可用。

本地验收记录：完整回归 `build/out/tests/run-9bvyOrqU` 通过；补跑 Shared 的画线冲突展示测试通过，累计 Core 173、Data 97、Analytics 206、Shared 42、Mac 60、Automation 30，共 608 项离线测试通过。另有 14 项 Python guard、43 项真实 CLI/MCP 场景及既有真实浏览器工作台/时间线交互回归。Live 分类未计入离线通过数。

2026-11-14 显式 `MEILI_LIVE=1`，新临时目录、生产 stdio MCP：`get_chart_analysis(AAPL, limit=2)` 成功返回 Yahoo 501 根历史柱的分页分析；`analyze_option_chain(AAPL, limit=2)` 成功返回 CBOE 94 条合约的分页分析。未向用户真实数据目录写入历史或偏好。此次联网测试只针对新增分析入口，不将其等同于所有来源及所有工具的实时联网验收。
