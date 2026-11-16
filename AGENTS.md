# Engineering rules

## Architecture

EasySellPut is a local .NET 10 app: Windows WPF, macOS Avalonia, cross-platform CLI and stdio MCP. Keep user data local. Reuse this checkout; create another worktree only when requested.

| Project | Responsibility | Dependency boundary |
| --- | --- | --- |
| `Meili.Core` | Models, interfaces, settings, alerts, storage protection | No UI or transport dependencies |
| `Meili.Analytics` | Deterministic pricing, indicators, strategy math | No UI, network, storage or transport |
| `Meili.Data` | Providers, routing, failover, rate limits, SQLite | Core |
| `Meili.Application` | Shared use cases, validation, operation schemas | Core, Analytics, Data; never Shared/UI |
| `Meili.Shared` | Desktop view models, presentation, platform abstractions | Application and lower layers; never CLI |
| `Meili.App` / `Meili.Mac` | Native views, controls, OS integration, composition | Shared and native adapters |
| `Meili.Cli` | CLI formatting and official SDK MCP transport | Application; never Shared or desktop assemblies |

- Put reusable business behavior in Application and calculations in Analytics. Views, view models and transports orchestrate; do not duplicate algorithms in XAML, JavaScript or adapters.
- Keep provider quirks in Data and OS behavior in native adapters. Register dependencies in composition roots; do not add global service lookups inside business services.
- Existing `Meili.Shared.Services` namespaces in Application are compatibility names, not assembly dependencies. Reuse their pricing, IV/OI and safety services.
- Bound network/concurrent work, propagate cancellation, keep I/O off the UI thread, marshal presentation updates back, and reject stale async continuations after navigation or request replacement.
- Preserve unrelated changes and public contracts. Do not publish releases or merge upstream without instruction.

## Business contracts and data

- Evaluate desktop, CLI and MCP for every capability. Shared business operations need one application service, CLI command and MCP tool. Document specific presentation/ownership exceptions in PR notes and `tests/automation/desktop_capabilities.json`.
- `OperationCatalog` owns names, schemas, bounds and permission semantics. Treat JSON versions, errors and exit codes as public APIs; document migrations for breaking changes.
- CLI JSON emits one result to stdout; diagnostics use stderr. MCP stdout is protocol-only. Use the official SDK and test discovery, calls, errors, cancellation and shutdown.
- MCP is read-only unless `--allow-writes` is explicit: hide write tools and reject unauthorized invocation. Never expose arbitrary shell/filesystem/browser execution, credentials or self-installation through MCP.
- Preserve sources, timestamps, freshness, delays, adjustment basis, pricing assumptions and missing-data meaning. Non-finite JSON numbers become null with explanatory metadata. Never fabricate prices, coverage, confirmation or a successful connectivity state.
- Resolve the same local/portable directory across entry points; support `--data-dir`. JSON writers must use `SnapshotFile` locking, atomic replacement and optimistic revision checks. Surface conflicts; never overwrite stale/corrupt state. Long-running MCP requests reload stores.
- Keep SQLite migrations/concurrency tested. IV/OI recording is an explicit write; read-only insights use existing observations. Preference patches are allowlisted, preserve omitted fields and exclude proxy credentials.

## UI system

- Follow the existing Claude-inspired design, Discord-style single navigation rail and chart-first workspace. Read the nearest page, primitive and gallery example before implementing UI.
- Reuse or extend shared components before creating another one: `AppCard`, button/input styles, `AppSelect`, `AppFormHeader`, `AppHelp`, `AppPopover`, `AppDisclosure`, `AppDrawer`, `AppDialog`, `AppLoading`, and existing chart/browser/split containers. Do not copy templates or introduce page-specific substitutes.
- `src/Meili.Shared/DesignSystem/tokens.json` owns palette, fonts, spacing, dimensions and motion. Generate native/CSS resources with `python3 build/generate-design-system.py`. Use live brushes, bundled fonts, pinned Lucide vectors and `DesignMotion`; preserve licenses/checksums. No hard-coded visual systems or font-glyph icons.
- Put a genuinely missing reusable primitive in the design-system layer, implement affected platform variants and add a gallery example. Keep business-specific composition near its page.
- Let hierarchy, defaults, selection and visible results teach the workflow. Keep text concise; use accessible `AppHelp` for explanations and `AppPopover`/`AppDisclosure` for secondary controls. Do not place interactive settings in tooltips or restore full-width help expanders.
- Align button families, icon canvases, labels/help/fields and validation rows using shared metrics; avoid compensating offsets. Keep related toolbar controls on one row when space permits. Collapse empty errors; preserve values and reveal invalid fields.
- Preserve keyboard access, focus return, selection, drafts and actionable loading/error states. Keep units, financial uncertainty, consequential actions and attribution visible. Settings/language/theme/motion belong in the bottom status bar.
- Animate only small app-owned chrome with short interruptible feedback and reduced-motion support. Never animate market values, full pages, native WebViews or split dimensions; never delay cancellation for animation.
- Blend window chrome with the app, omit redundant visible branding, and reserve native caption-button/resize safe areas. Preserve drag, maximize/fullscreen, restore and unsupported-platform fallbacks.
- Check light/dark, Chinese/English, compact/wide and relevant focus/disabled/selected/error states. Inspect actual screenshots and provide representative images or accessible links to the user. Label fixture/headless/native captures honestly; a mockup or build link is not visual acceptance.

## Feature invariants

- **Charts and strategies:** use bundled Lightweight Charts with visible attribution and notices. Compute indicators/payoff/Greeks in Analytics. Share one StrategyViewModel; preserve entry premiums, reject stale/cross-symbol drafts and expose truthful midpoint/last/missing price basis.
- **Drawings:** price-pane gestures only; preview, Esc, return to pan, selection/edit/single deletion. Validate tool/chart epochs, absolute indices and drawing revisions through ChartDrawingStore and SnapshotFile. Keep CLI/MCP mutations equivalent.
- **Events:** use MarketTimelineService. Preserve original Eastern publication dates, date-only/range/unknown status and provenance; chart alignment is separate. Hover and click use the same filtered candle bucket; selection shows every grouped event with a return-to-all action. Do not invent candles, dates or historical coverage.
- **News and websites:** articles use reusable left/right ArticleSplitView, compact source/actions and no modal/address bar; resizing retains browser identity/history and close releases it. Personal tabs retain add/edit/sort/remove, lazy creation and sessions; persist bookmark metadata through SnapshotFile, not browsing history or credentials.
- **WebView isolation:** system WKWebView/WebView2 only; no bundled Chromium. Remote HTTP/HTTPS pages get no chart token, bridge, injected scripts, permission grants or downloads. Validate redirects/popups; external opening is explicit. Private chart documents use tokenized loopback navigation and bounded typed events; resolve event URLs in C# from current identity.
- **Background and quotes:** reuse QuoteHub/QuotePump and DesktopActivity; no duplicate market timers or CLI/MCP alert monitors. No subscribers means no quote timer; enabled alerts retain cadence and IV sampling requires enabled IV rules. Hide/minimize pauses chrome work and releases private charts; restore preserves page/model. Keep personal browser sessions and explicit quit working.
- **Market glance:** visible symbols only, bounded/cancelable bar requests, stale-generation rejection. Mini charts use real latest-session bars, fixed Eastern axes and gaps; keep regular/extended prices and quote/bar provenance separate. Do not inject Windows taskbar extensions. Session shading uses MarketClock, not an invented exchange calendar.
- **AI setup:** share McpConnectionSetup and the embedded companion skill. Desktop copies prompts; it never executes setup. Explicit CLI setup preserves config/edited skills and requires scoped replacement. Status represents initialized, live, data-directory-scoped MCP sessions, not copied configuration. Guidance never grants write permission.
- **Updates:** bake the CI repository (`GITHUB_REPOSITORY`) into each build; local builds default to no update source. Never infer trust from runtime environment, remotes or user settings. Validate packaged GUI/CLI source and pinned key against the release repository; signed indexes must name it. Keep the pinned signature key, exact version/RID/size/hash, HTTPS host limits and bounded downloads. No production fake feed, trust override or MCP installer. External helper preflights before shutdown, waits for the exact parent, preserves data and rolls back same-volume swaps. Reject unsafe archives, incompatible/downgrade packages and active installed instances. Never bypass OS trust; releases remain manual/draft-only until explicitly published with verified signing setup.

## Testing and CI

- Identify affected behavior, layers, platforms and regression risks before completion. Run sufficient existing checks and add meaningful tests for changed business behavior, bugs, cancellation, concurrency and contracts. Do not write implementation-mirroring or meaningless documentation tests.
- Review `.github/workflows/ci.yml` and affected scripts/inventories for every change. Extend CI when behavior, schemas, dependencies, platforms or components change; otherwise name the existing coverage. Never weaken checks, skip relevant tests or remove architectures to obtain green CI.
- Each catalog operation needs exactly one meaningful case in `tests/automation/capability_cases.json`, exercised through real CLI and MCP processes with schema/permission/result parity. Update desktop and companion-skill inventories too. Do not replace executable coverage with fixed tool counts or help-only checks.
- Keep fixtures in test hosts, never production fake-data switches or production references to test projects. Use temporary data directories. Live validation requires deliberate `MEILI_LIVE=1`; fixtures are not live evidence.
- UI changes require affected component/journey coverage and screenshots. Renderer changes require real Chromium interactions; native browser/package changes require Windows/macOS x64/ARM64 execution. Run the full offline WPF suite, not a class allowlist.
- CI must build real packages, verify runtime/architecture/checksums, extract into paths with spaces and run the extracted GUI/CLI/MCP plus updater swap/relaunch on native runners. Preserve all four desktop and five CLI targets and downloadable evidence. Cross-compilation is not native execution; ad-hoc/unsigned packages are not trusted distribution signatures.
- Release only from `main`: successful main push CI → reuse artifacts → platform signing/notarization → native final-package checks → signed draft → explicit publish workflow. `work`/PR builds never release. Keep release secrets in a main-only `release` Environment; never rebuild application packages, bypass signing or publish locally. See [release flow](docs/44-GitHubActions主分支发布流程.md).
- Keep `CI · Required checks` as the aggregate check; branch rules must require it. Keep names short and platform-first; update workflow triggers, release validation and branch rules together when renaming. Rerun affected checks after fixes and inspect the delivered code revision's CI. Later documentation-only commits may cite unchanged code results explicitly, without claiming pending CI passed.
- Report checks/commands, outcomes, screenshots and CI links. Distinguish passed/failed/skipped/unrun, fixture/live, headless/native and build/package execution. Resolve relevant failures and state remaining gaps; compilation or zero tests does not prove readiness.

## Commands

Use the SDK pinned by `global.json`; source `tools/env.sh` or `tools/env.ps1`. Keep TLS/checksum validation and UTF-8 BOM in non-ASCII Windows PowerShell scripts. For Cloud's NuGet mutex restriction, use approved escalation rather than disabling verification.

```sh
bash build/test-headless.sh
powershell -ExecutionPolicy Bypass -File build/build.ps1 -Test
dotnet build src/Meili.Cli/Meili.Cli.csproj
bash build/pack-cli.sh <rid> [output-parent]
python3 tests/automation/cli_mcp_smoke.py --executable <published-path>
```

## References

Read the relevant feature guide before changing its behavior:

- Architecture: [overview](docs/01-需求与架构.md), [CLI/MCP](docs/08-CLI与MCP使用说明.md), [coverage](docs/09-CI与能力覆盖.md).
- UI: [design system](docs/34-Claude设计系统.md), [UX acceptance](docs/35-全页面UX审计与验收.md), [form alignment](docs/40-表单文字与控件对齐检查.md), [window chrome](docs/43-沉浸式窗口标题栏.md).
- Charts: [rendering](docs/14-TradingView图表改造.md), [strategy workbench](docs/15-期权链与策略联动工作台.md), [drawing](docs/32-按钮规格与画线交互.md), [events](docs/39-聚合事件点击与左右分屏修复.md).
- Desktop: [web tabs](docs/31-Discord工作区与网页标签.md), [market glance](docs/36-行情速览与跨平台托盘.md), [background lifecycle](docs/37-后台运行性能与CI审查.md), [article split](docs/38-新闻分屏阅读.md).
- Delivery: [platform packages](docs/12-Windows与macOS的CLI和MCP.md), [AI setup/skill](docs/19-Codex与ClaudeCode的Skill和MCP.md), [signed updates](docs/29-检查更新与安装.md).
