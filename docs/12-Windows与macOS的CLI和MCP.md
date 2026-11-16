# Windows / macOS 的 CLI 和 MCP

CLI 和 MCP 使用同一个原生可执行文件，不需要打开桌面界面，也不需要另外安装 .NET。

| 系统 | 机器架构 | 发行目标 | 可执行文件 |
|---|---|---|---|
| Windows | Intel / AMD 64 位 | win-x64 | easysellput.exe |
| Windows | ARM64（例如 Windows on ARM） | win-arm64 | easysellput.exe |
| macOS | Intel | osx-x64 | easysellput |
| macOS | Apple Silicon（M 系列） | osx-arm64 | easysellput |

解压完整发行包，不要只复制可执行文件。包内还包含运行时、SQLite 本地库、LICENSE、使用说明及适合本平台的 MCP 配置示例。CLI/MCP 不依赖 WPF 或 Avalonia；这是独立工具包，桌面安装器暂不自动附带它。

## 获取发行包

GitHub Actions 的 `CI · Test & Package` 成功运行后，可在该运行的 Artifacts 中下载：

- CLI-MCP-win-x64
- CLI-MCP-win-arm64
- CLI-MCP-osx-x64
- CLI-MCP-osx-arm64

Actions 下载的 artifact 是外层 ZIP，先解压它，再解压内部实际发行 ZIP / tar.gz；旁边的 .sha256 文件用于核对实际发行包。它们是 CI 构建产物，不是自动发布的 GitHub Release。

CI 在以上四种架构的原生 runner 上打包、核对校验和及可执行文件机器类型、解压到带空格的路径，然后实际运行 CLI/MCP 进程检查。MCP 工具发现、只读权限、行情分析计算与本地读写检查都不依赖桌面窗口。Linux 同时保留检查。

## Windows 快速开始

根据机器选择 win-x64 或 win-arm64，并解压整个目录。在 PowerShell 中：

```powershell
cd 'C:\Tools\EasySellPutCLI'
.\easysellput.exe --version
.\easysellput.exe quote AAPL MSFT --json
.\easysellput.exe watchlist list --json
```

目录不在 PATH 时必须使用 `.\` 或完整路径。需要从源码打包时：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build\pack-cli.ps1
# 默认识别 Windows x64 / ARM64；也可显式传 -Runtime win-arm64。
```

仓库内有本地 SDK 时优先使用；否则使用已安装且符合 global.json 的系统 SDK。PowerShell 打包脚本保留 UTF-8 BOM，兼容 Windows PowerShell 5.1 的中文文档路径。

## macOS 快速开始

M 系列芯片选 osx-arm64，Intel Mac 选 osx-x64。解压后在终端进入实际发行目录：

```bash
./easysellput --version
./easysellput quote AAPL MSFT --json
./easysellput watchlist list --json
```

tar.gz 保留可执行权限。不要在图形界面双击后把短暂出现的终端窗口当作服务；CLI 从终端使用，MCP 由客户端启动。源码打包：

```bash
bash build/pack-cli.sh
# 默认识别 macOS 与当前架构；也可显式传 osx-arm64 / osx-x64。
```

当前包没有 Developer ID 签名和公证。若系统对下载的可执行文件进行拦截，请按 macOS 正常安全设置确认，或从源码构建；原生 CI 运行不能代替下载后 Gatekeeper 流程验收。

## MCP 客户端配置

客户端使用 stdio，由客户端创建进程；无需手动先开一个服务或开放端口。

Windows 示例（按实际解压位置修改）：

```json
{
  "mcpServers": {
    "easysellput": {
      "command": "C:\\Tools\\EasySellPutCLI\\easysellput.exe",
      "args": ["mcp", "serve"]
    }
  }
}
```

macOS 示例：

```json
{
  "mcpServers": {
    "easysellput": {
      "command": "/Users/YOUR_NAME/Applications/EasySellPutCLI/easysellput",
      "args": ["mcp", "serve"]
    }
  }
}
```

command 必须是实际可执行文件的绝对路径。不要写 `~`、`%LOCALAPPDATA%` 或 `$HOME`，不要把整条带参数的命令塞进 command。目录中有空格可直接写在 JSON 字符串中，不要再给路径加一层引号。macOS 图形客户端不一定继承终端 PATH，所以优先完整路径。

默认只读。需要允许客户端修改自选股、策略或提醒时，显式把 `--allow-writes` 加到 args。不同客户端配置外层可能不同，请放到该客户端的 MCP 配置位置。

## 与桌面版共享本地数据

- Windows 默认 `%LOCALAPPDATA%\EasySellPut`。
- macOS 默认 `~/Library/Application Support/EasySellPut`。
- 桌面便携版或自定义目录：CLI 与 MCP 都可传 `--data-dir`，指向桌面实际数据目录。

修改发生在其他进程后，桌面需要重新加载或重启再编辑；旧快照保存会报冲突，不能覆盖新数据。MCP 本地数据会在每次操作时重新读取。

## 验证范围

原生 CI 使用确定性离线行情验证 CLI/MCP 和发行包，不把这描述成所有地区的真实行情可用性。真实行情此前已在 Linux 验证，Windows/macOS 用户仍需要可访问行情源的网络和正确代理设置。可显式运行 `MEILI_LIVE=1` 的实盘脚本进行独立验收；完整命令见 CLI/MCP 使用说明。
