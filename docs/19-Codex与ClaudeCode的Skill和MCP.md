# AI 工具接入：统一提示词、Skill 与 MCP

底部 **MCP 状态**可以点击，复制通用配置提示词；设置页的「复制通用配置提示词」复制相同内容。粘贴给当前使用的本机 AI 工具即可。提示词带有当前可执行文件、stdio 参数和数据目录，让工具识别自己的官方配置方式，核对同名连接、只读握手和 skill 支持。不同 harness 不共用臆造的命令或配置文件路径。不支持 skill 的客户端可以读取指导资源作为会话上下文，不能声称已安装 skill。

应用只复制，不执行命令或修改其他工具配置。远程云端 harness 无法直接启动用户电脑上的本地 stdio；需要在同一台电脑的本地代理中接入。复制成功、配置保存、MCP 实际连接和行情可用是不同状态。

## 终端安装

设置页还提供 Codex / Claude Code 卡片：复制连接命令、官方安装帮助、开始提问示例。默认折叠完整命令和手动 JSON/TOML。

macOS 默认路径的命令：

```sh
/Applications/EasySellPut.app/Contents/Resources/automation/easysellput connect codex
/Applications/EasySellPut.app/Contents/Resources/automation/easysellput connect claude-code
```

Windows PowerShell 示例：

```powershell
& 'C:\path\easysellput.exe' connect codex --data-dir 'C:\path\data'
& 'C:\path\easysellput.exe' connect claude-code --data-dir 'C:\path\data'
```

界面生成实际路径，含空格的路径安全引用；非默认/便携数据目录始终显式保留。macOS 先将应用移入 Applications，移动后重新复制。

`connect` 分四步：检查客户端及已有配置 → 启动包内只读 MCP，核对工具与实际数据目录 → 安装/更新 skill → 用官方客户端命令登记并回读验证。默认用户级 MCP；`--project PATH` 仅改变 skill 位置。可加 `--json` 获取一个结构化结果；诊断不污染 MCP stdout。

- `--check` 只做预检、实际 MCP 握手与计划，不安装 skill、不登记客户端；MCP 启动仍可能初始化指定的本地应用数据目录。
- 重复执行复用一致的配置和 skill。
- 同名配置不一致时退出码 4。先检查差异，明确需要替换再加 `--replace`；只移除/重建该名称的用户级连接，不动其他连接。Claude 项目级冲突不会被自动移除。若移除后登记失败，会报告需要重试/恢复，不声称成功。
- `client_not_found`、`client_setup_failed`、`mcp_verification_failed`、`skill_conflict`、`connection_conflict` 提供恢复建议。
- 完成后重启 AI 客户端或新开会话。Codex 用 `$easysellput-local`，Claude Code 用 `/easysellput-local`；卡片可以直接复制示例问题。

## Skill 安装与工作流

源文件：[easysellput-local](../skills/easysellput-local/SKILL.md)，版本 2.0，随 CLI 嵌入。

- Codex：`~/.agents/skills/easysellput-local/SKILL.md`。
- Claude Code：`~/.claude/skills/easysellput-local/SKILL.md`。
- 指定项目：`easysellput install-skill --client codex --project PATH`，也支持 `claude-code`。这个旧命令仍可用，仅安装 skill。

相同版本不改写；精确匹配已发布原始内容的旧版可以自动更新，保留唯一 `SKILL.md.backup-*` 并返回 updated/backup。用户改过的内容一律保留并报冲突；链接目标拒绝写入。原子写入和乐观冲突检查保护并发安装。

Skill 包含全部 43 个工具的用途与读写指南，并增加任务路由、调用顺序、并行边界、JSON 参数示例、单位、来源/时间、IV/定价假设、缺失数据与故障恢复。工具的实时 schema 仍是参数权威；skill 不授予写权限，不执行交易。

MCP 增加可选 prompts：market_brief、option_research、strategy_review、connection_check，以及固定只读资源：

- `easysellput://guides/skill`：与包内安装器相同的完整 skill。
- `easysellput://guides/workflows`：任务步骤。
- `easysellput://capabilities`：当前权限下的编译能力目录。

这些资源不能读取任意文件；不支持 prompts/resources 的客户端仍能发现和调用原有工具。默认 27 个只读工具，明确启用写模式才提供 43 个工具。

## 底部连接状态

每个初始化后的真实 MCP 会话按当前数据目录写入有限的本地心跳诊断，界面每 5 秒更新。展示未连接、连接数、只读/可写；悬停查看客户端与权限，点击复制统一配置提示词。设置预检客户端不会计入连接数。退出清理，崩溃后通过 PID、进程启动时间与 30 秒过期排除失效记录；读取失败显示未知，不冒充未连接。诊断文件不是授权机制，也不说明行情源健康。

## 验证与 CI

真实进程测试覆盖只读握手、prompts/resources、任意 URI 拒绝、诊断生命周期、两类 fixture 客户端的预检/登记/重复安装/冲突/显式替换/自定义 skill 保护；测试只用临时目录。此 smoke 已被现有原生打包 CI 调用，覆盖 Windows/macOS 架构包，无需另建脱离打包的工作流。

能力检查从编译目录核对 skill 的完整工具清单与权限，并逐个执行 43 项 CLI/MCP 场景。窗口/剪贴板属于桌面呈现，安装配置属于用户主动触发的 CLI 辅助，不新增业务 MCP 工具；见 desktop_capabilities.json。

本轮还在隔离临时 Claude 配置中测试官方 Claude Code 2.1.325 的实际注册/回读，未修改真实用户客户端配置、未发起 AI 对话。跨平台最终运行结果以原生 CI 为准。

参考：[Codex CLI](https://developers.openai.com/codex/cli/)、[Codex Skills](https://developers.openai.com/codex/skills/)、[Claude Code MCP](https://code.claude.com/docs/en/mcp)、[Claude Code Skills](https://code.claude.com/docs/en/skills)。

Windows 自验证通过 ProcessStartInfo.ArgumentList 直接启动已知自带可执行文件，再使用官方 SDK StreamClientTransport 完成协议；避免 SDK 2.4 的 cmd 包装破坏含空格的安装路径。标准错误被有界读取，取消/退出会关闭标准输入并限时回收子进程，不改变实际客户端 stdio 配置。原生解压包 smoke 保留路径空格与 shell 元字符场景；路径断言比较 Windows 规范路径，兼容 8.3 短路径。
