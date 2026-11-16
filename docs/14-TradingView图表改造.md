# TradingView WebView K 线改造

## 选择和授权

使用 TradingView 官方 **Lightweight Charts 5.3.0**，Apache-2.0。它是专业金融图表的渲染组件，**不是**需要另行申请授权的 Advanced Charts，也不包含 Pine Script、TradingView 社区脚本或其行情授权。所有行情仍来自应用的已有供应商，所有指标仍由 `Meili.Analytics` 计算。

官方来源：

- https://github.com/tradingview/lightweight-charts
- https://tradingview.github.io/lightweight-charts/
- https://www.tradingview.com/charting-library-docs/latest/getting_started/

脚本随桌面程序集内嵌，不依赖 CDN。上游包经 npm 发布的 SHA-512 integrity 校验，具体来源、版本和最终脚本 SHA-256 位于 `src/Meili.Shared/WebCharts/vendor/provenance.json`。保留完整 Apache LICENSE、上游 NOTICE、脚本版权头、图表内 TradingView 标识与显式可点击署名。CLI/MCP 无此资源及浏览器依赖。以后升级必须重新核验来源、哈希、API 和许可证，禁止修改上游脚本伪装版本。

## 改造任务

- [x] 替换 Windows/macOS K 线页中的 ScottPlot 控件及手工坐标同步。
- [x] 系统 WebView2 / WKWebView 加载内嵌 Lightweight Charts，不随应用分发 Chromium。
- [x] 蜡烛、成交量、MA、EMA、BOLL、MACD、RSI、KDJ 共用一个时间轴，指标面板可拖动分隔线调整高度。
- [x] 保留原周期切换、供应商、缓存、复权、导出、参数设置、比较曲线、水平线与趋势线。
- [x] 滚轮缩放、拖动平移、十字线、适应窗口；刷新/指标/主题变化保留用户时间轴视野，新标的/周期重置。
- [x] 主题、中英文、红涨绿跌/绿涨红跌随现有设置同步；时间标签按美国东部时区显示。
- [x] 缺失 OHLC 只显示真实收盘点，禁止伪造蜡烛；指标预热和无效数值以空白传递，不把 NaN/Infinity 写入 JSON。
- [x] 有界本地事件通道只接收十字线、画线、组件诊断、用户点击的固定署名链接。
- [x] 共享层数值/协议测试、真实 Chromium 鼠标测试、四个平台原生 WebView 验收及 CI。

## 本地资源与权限

每个可见图表会话绑定 `127.0.0.1` 随机端口及 256 位随机路径令牌，只服务白名单内嵌资源和当前快照。校验 Host、Origin、Sec-Fetch-Site；限制并发、请求头、JSON 长度/深度和超时。CSP 限制脚本及数据请求到当前来源，不允许 iframe、外部脚本或表单提交。图表导航仅允许当前本地文档；拒绝下载、弹窗、权限授权和原生 JavaScript 桥接。

这是桌面进程内部的渲染通道，不是对局域网或互联网开放的后台服务。切走页面即释放会话和 WebView，停止轮询与网络监听。Windows 图表与新闻分别使用独立 WebView2 配置目录；macOS 沿用非持久化 WKWebView 数据存储。新闻网页继续使用原来的无桥接阅读器策略。

比较与绘图继续走原 `ChartsViewModel` / 本地存储，不增加任意 URL、任意文件、命令执行或第三方网络数据接口。无效事件、过期标的事件和越界下标不会修改当前图表。

## 桌面、CLI、MCP

本次替换渲染和鼠标交互，不新增业务指标算法或操作。CLI/MCP 已有的历史 K 线和指标接口继续使用同一个行情及 Analytics 层，返回结构保持兼容；不会启动图表窗口或引入 WebView/TradingView 依赖。未来新增指标算法必须同步考虑桌面、CLI、MCP 和能力场景覆盖。

期权曲面、策略收益曲线属于其他图表，本次仍使用原绘图库。

## 验证

- `dotnet test tests/Meili.Shared.Tests/Meili.Shared.Tests.csproj`：序列化数值/缺口、来源、私有通道、导航限制、类型化画线及过期标的拒绝。
- `python tests/webcharts/test_renderer.py -v`：Playwright 真实 Chromium 验证组件数据、指标面板、空白、署名、鼠标缩放/画线/十字线、刷新视野、主题和本地资源加载；需安装 `playwright==1.56.0` 及 Chromium。
- `bash build/test-headless.sh`：桌面、共享业务、指标、CLI/MCP 全量回归。
- Windows/macOS 原生：`dotnet run --project tests/Meili.NewsBrowser.Smoke/Meili.NewsBrowser.Smoke.csproj -c Release`，同时验收新闻与实际图表组件，包括 120 根数据/119 根真实蜡烛、五面板、MA 结果、主题、指标显隐、停止后重新挂载。
- CI 在 Windows x64/ARM64、macOS Intel/Apple Silicon 实际运行原生宿主；Linux 验证真实浏览器渲染、桌面无头回归及 CLI/MCP。固定测试数据不代表真实行情联网验证，也不保证所有供应商都提供所有周期。

## UI/UX 一致性检查

图表内的控件、边框、焦点、错误色与深浅外观直接取自 `ThemePalette`，与原生页面同步；正文 14px、说明/价格轴 12px，按钮圆角 6px、最小高度 32px，使用应用对应平台的系统字体。保留统一的 8/12px 间距。窄屏隐藏可选手势说明，保留可点击的 TradingView 署名；键盘焦点使用应用强调色。

新闻窗口使用同一组动态资源、按钮尺寸及信息/错误状态背景，主题切换无需重开窗口，空状态栏会收起。新闻发布者的网页正文遵循其自身设计，不注入脚本或样式。网页渲染器出现后续错误时会重新显示原生状态栏。此次样式调整不改变 CLI/MCP 能力及数据协议。

浏览器测试输出 `webchart-unified-dark.png` / `webchart-unified-light.png`；桌面测试覆盖新闻窗口实时主题与语言切换，并可按 `MEILI_RENDER_DIR` 输出阅读窗口截图。CI 继续执行四个平台的实际 WebView 及 Windows 页面集成检查。
