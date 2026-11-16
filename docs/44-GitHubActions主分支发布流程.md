# GitHub Actions：仅 main 发布

## 流程

| 阶段 | 触发 | 内容 | 能否公开发布 |
| --- | --- | --- | --- |
| CI · Test & Package | 所有 push / PR，也可手动运行 | 共享测试、全目录 CLI/MCP、组件与浏览器测试、原生安装包及升级事务验收 | 否，仅保留 artifacts |
| Release · Prepare draft | main 的成功 CI 自动触发；也可在 main 手动指定 run ID | 校验来源，下载九份既有包，平台签名/公证，再运行最终包，签署发行索引和更新索引，创建版本 tag 与 Release 草稿 | 否 |
| Release · Publish | 在 main 手动输入稳定版本号 | 复验 tag、当前 main、成功 CI、两个签名索引和全部附件，再把草稿转正式发布 | 是 |

`work`、PR、其他分支和标签上的 CI 产物均不能进入发布流程。工作流 job 条件和 Python 策略同时验证 main；成功的 aggregate 不能代替缺失的平台作业。来源必须是本仓库 `.github/workflows/ci.yml` 的成功 **push**，SHA 必须等于当前 main。拒绝 fork、PR、过期/缺失/重复产物和截断的 API 列表。

main 若在准备或发布验证期间前进，流程停止，等待新 main 完整通过 CI。版本已存在时不覆盖或替换附件；正式发布后的新版本需要提升 `Directory.Build.props` 版本。main CI 的自动草稿流程需要版本号已经更新；文档等不需要发行的提交仍可正常通过 CI，但准备发行遇到已有版本会拒绝覆盖。

## 产物与代码一致性

- Desktop：Windows x64/ARM64、macOS Intel/Apple Silicon，共四份 zip，含 GUI、CLI/MCP 和配套 skill。
- 独立 CLI/MCP：Linux x64、Windows x64/ARM64、macOS x64/ARM64，共五份 zip/tar.gz。
- Release 不执行 `dotnet publish`，不重新构建应用；仅对成功 CI 的原生包进行平台签名、公证和重打包。签名会改变包字节，因此重新计算校验和，并对最终包再次执行原生验证。
- 最终验证包括含空格路径解压、CLI/MCP 真实进程、GUI、内置图表、更新替换/重启及数据保留。macOS 另验证 Developer ID、Apple notarization、stapled ticket 和 Gatekeeper。Linux 包保持原始字节，由签名发行索引验证来源。
- `EasySellPut-update.json` 包含构建仓库 `repository` 和四份桌面包，保持客户端协议；`EasySellPut-release.json` 包含九份包的大小/哈希、源码提交、CI run 和原始 artifact 身份。两个索引均使用内置公钥对应的 ECDSA P-256 私钥签名。
- 正式发布前下载全部草稿附件，复验两个签名、九个平台目标、哈希、校验文件和附件集合。没有 `--publish` 的验证不会发布；工作流只在显式手动发布时传入它。

## 首次启用配置

维护者需先在仓库 Settings → Environments 创建 **release**：Deployment branches and tags 选择 **Selected branches and tags**，只添加类型为 **Branch** 的 `main`，不允许 `work` 或 tag。将凭据放在该 Environment 的 Secrets 中，不要提交到 Git，也不要扩大到所有分支。

当前聊天凭据配置 Environment 的 GitHub API 返回 **403 Resource not accessible by integration**，因此本次没有完成服务端 Environment 策略配置。工作流和脚本的 main 限制已实现；服务端策略仍需有仓库管理权限的维护者配置后，再加入正式凭据。

| Secret | 用途 |
| --- | --- |
| `UPDATE_SIGNING_KEY` | 与源码 `update-public-key.pem` 匹配的 ECDSA P-256 PEM 私钥 |
| `APPLE_CERTIFICATE_P12_BASE64` | Developer ID Application 证书及私钥的 P12，单行 base64 |
| `APPLE_CERTIFICATE_PASSWORD` | P12 密码 |
| `APPLE_SIGNING_IDENTITY` | 完整的 `Developer ID Application: … (TEAMID)` 身份 |
| `APPLE_NOTARY_KEY_P8` | Apple notarization API 私钥内容 |
| `APPLE_NOTARY_KEY_ID` | API key ID |
| `APPLE_NOTARY_ISSUER` | API issuer ID |
| `WINDOWS_CERTIFICATE_PFX_BASE64` | 可用于 Windows Authenticode 的证书/私钥 PFX，单行 base64 |
| `WINDOWS_CERTIFICATE_PASSWORD` | PFX 密码 |

macOS 使用临时 keychain、hardened runtime 和 .NET JIT entitlements，Apple 返回 Accepted 后再 staple。Windows 使用证书存储及 SHA-256 签名/时间戳并验签。凭据缺失、信任失败或公证失败会停止，不退回 ad-hoc/未签名正式包。临时密钥材料在清理阶段删除，签名失败不输出含密码的命令行。

当前更新公钥的首次正式初始化仍按 [29](29-检查更新与安装.md) 操作，私钥备份保存在仓库外；提交公钥并重新完成 CI 后才可发布。已有正式客户端时不能随意更换公钥。Apple/Windows 证书由维护者向各自信任体系申请，GitHub Actions 不能自动提供这些身份。

仓库分支规则应要求 `CI · Required checks`（原 `CLI and MCP required`；改名迁移见 [09](09-CI与能力覆盖.md)）。不能因为签名配置尚未完成就去掉平台、忽略测试或绕过公证。CI 测试包仍可下载；它们不等于正式签名发行版。

## 操作

1. 将改动合入 main，并设置新的三段稳定版本号。
2. 等 main 的完整 CI 成功。自动准备工作流启动；也可在 main 手动选择 `Release · Prepare draft`，输入该 CI run ID 和可选说明。
3. 检查草稿、发行说明、签名及所有平台的最终包验证结果。tag 在草稿准备阶段创建并指向该 main SHA；不重新指向已有版本。
4. 在 main 运行 `Release · Publish`，输入如 `0.6.0`。它会再次验证当前 main、tag、来源和附件后公开发布。

所有正式发布通过 Actions 完成，不在本地执行 `gh release create/edit`，也不通过网页手工替换附件。GitHub 管理员始终具有手动修改仓库的权限；工作流约束不能替代仓库权限与 Environment 配置。

## 验证范围

CI 增加 `Release · main guard` 并接入 required 聚合：检查工作流边界，测试 main/work、错误来源、缺失平台、过期产物、篡改哈希、错误附件、凭据缺失和实际 OpenSSL 签名验证。发布编排测试使用临时 fixture 和模拟 GitHub API，不创建真实发行。

新增正式平台签名、公证和公开发布必须在凭据配置后，通过 main 的实际发行流程验收；普通 CI 的 ad-hoc macOS 包不能证明 Developer ID 或 Apple 服务已验证。本次仅实现并验证流程，没有运行正式发布或修改用户安装。

这是工程交付变更，不新增桌面业务、CLI 命令或 MCP 工具，原有业务目录/权限和覆盖场景保持不变。

## 构建与发布仓库绑定

CI 通过 GitHub Actions 环境将当前 `github.repository` 编译进每个 GUI/CLI；不需要上游手改 URL。编译检查拒绝与 `GITHUB_REPOSITORY` 冲突的覆盖。Linux CI 额外构建上游、fork、本地三个真实 CLI，验证仓库绑定、开发禁用和运行时环境无法改源。

每个平台对解压后的 CLI 执行离线 `update source --json`，GUI 自检报告同时输出自身内置源；两者的仓库和公钥指纹必须匹配当前发行仓库和源码。签名后的包也复用同样检查，仍不重新编译。签名更新索引必须包含当前仓库，Publish 再次核对；错误绑定会阻止草稿/发布。

这些检查不替代首次签名密钥配置，也不允许 `work`/PR 发布。首次初始化应先提交仓库自己的公钥，再运行 main CI 并配置对应私钥；已有客户端的密钥不得自动轮换。
