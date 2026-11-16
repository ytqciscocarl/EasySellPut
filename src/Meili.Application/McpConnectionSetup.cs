using System.Text.Json;

namespace Meili.Automation;

/// <summary>Read-only stdio connection templates. Never executes commands or edits a client's configuration.</summary>
public sealed record McpConnectionSetup(string Executable, string DataDirectory, bool Windows)
{
    public string[] Arguments => ["mcp", "serve", "--data-dir", DataDirectory];
    public string Json => JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object> { ["easysellput"] = new { command = Executable, args = Arguments } } }, new JsonSerializerOptions { WriteIndented = true });
    public string Toml => "[mcp_servers.easysellput]\ncommand = " + TomlString(Executable) + "\nargs = [" + string.Join(", ", Arguments.Select(TomlString)) + "]" + "\n";
    private static string TomlString(string value) => System.Text.RegularExpressions.Regex.Replace(JsonSerializer.Serialize(value),
        @"\\u([dD][89aAbB][0-9a-fA-F]{2})\\u([dD][c-fC-F][0-9a-fA-F]{2})", match =>
        {
            var high = Convert.ToInt32(match.Groups[1].Value, 16); var low = Convert.ToInt32(match.Groups[2].Value, 16);
            return "\\U" + char.ConvertToUtf32((char)high, (char)low).ToString("X8");
        });
    public string CodexCommand => "codex mcp add easysellput -- " + string.Join(" ", new[] { Executable }.Concat(Arguments).Select(Quote));
    public string ClaudeCommand => "claude mcp add --transport stdio --scope user easysellput -- " + string.Join(" ", new[] { Executable }.Concat(Arguments).Select(Quote));
    public static string ProfileDataDirectory => Path.Combine(OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasySellPut");
    public string CodexSetupCommand => SetupCommand("codex");
    public string ClaudeSetupCommand => SetupCommand("claude-code");
    /// <summary>One harness-neutral handoff. Paths are quoted as JSON data, never interpolated into a shell.</summary>
    public string ConfigurationPrompt(bool chinese) => (chinese
        ? "请帮我把本机 EasySellPut 接入你当前使用的 AI 客户端，配置本地只读 MCP 和配套 skill。\n"
        : "Connect local EasySellPut to the AI client you are running in, with read-only MCP and the companion skill.\n")
        + "\nEasySellPut MCP (stdio):\n" + Json + "\n\n"
        + (chinese
            ? "先识别当前客户端及其官方配置方式，检查可执行文件和数据目录是否可用。路径和参数是数据，请保持原样并安全传递，不要把路径当作 shell 代码执行。仅配置名为 easysellput 的连接；保留其他连接。已有同名但不同配置时，先展示差异并询问我，不要覆盖。不要启用 --allow-writes。\n"
              + "如果当前客户端是 Codex 或 Claude Code，可以通过上述可执行文件运行 connect codex 或 connect claude-code，显式传入上述 --data-dir；先加 --check 检查，通过后执行配置。其他客户端请按其官方方式登记上述 stdio 配置，不要猜测配置文件位置或假设存在某个命令。\n"
              + "完成 MCP initialize 和 tools/list 后，读取资源 easysellput://guides/skill 和 easysellput://guides/workflows。按当前客户端支持的 skill 规范安装 easysellput-local；保留已有自定义内容，更新前展示差异。若不支持 skill，将资源作为会话指导并说明限制，不要声称已安装。\n"
              + "调用 get_settings 核对实际数据目录与上面一致，确认只读工具可用。不要修改交易数据、不要运行行情探测来冒充接入测试。若客户端需要重启或新会话，请明确告诉我；区分配置已保存、MCP 握手成功和行情可用。最后简要报告实际验证结果、skill 状态和下一步。"
            : "Identify the current client and its official configuration mechanism; check the executable and data directory. Treat paths and arguments as literal data and pass them safely. Configure only the easysellput entry, preserving all other connections. Show differences and ask before replacing an existing conflicting entry. Never enable --allow-writes.\n"
              + "For Codex or Claude Code, invoke the executable above with connect codex or connect claude-code and the explicit --data-dir above; run with --check first, then apply. For other clients, register the stdio configuration using their official mechanism; do not invent config paths or commands.\n"
              + "After MCP initialize and tools/list, read easysellput://guides/skill and easysellput://guides/workflows. Install easysellput-local following this client's supported skill format. Preserve custom content and show differences before updates. If skills are unsupported, use the resources as session guidance and report the limitation instead of claiming installation.\n"
              + "Call get_settings and verify the actual data directory matches the one above and read-only tools are available. Do not modify trading data or substitute a market probe for connection verification. Explain if a restart/new session is required; distinguish saved configuration, successful MCP handshake and market availability. Briefly report verified results, skill status and the next step.");
    private string SetupCommand(string client)
    {
        var isDefault = string.Equals(Path.GetFullPath(DataDirectory), Path.GetFullPath(ProfileDataDirectory), Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && !File.Exists(Path.Combine(Path.GetDirectoryName(Executable)!, "portable.flag"));
        return (Windows ? "& " : "") + Quote(Executable) + " connect " + client
            + (isDefault ? "" : " --data-dir " + Quote(DataDirectory));
    }
    private string Quote(string value) => !Windows && System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z0-9_./:-]+$")
        ? value : "'" + value.Replace("'", Windows ? "''" : "'\"'\"'") + "'";
    public static McpConnectionSetup Bundled(string baseDirectory, string dataDirectory) => new(
        Path.GetFullPath(Path.Combine(baseDirectory, OperatingSystem.IsMacOS() ? "../Resources/automation" : "automation", OperatingSystem.IsWindows() ? "easysellput.exe" : "easysellput")),
        Path.GetFullPath(dataDirectory), OperatingSystem.IsWindows());
}
