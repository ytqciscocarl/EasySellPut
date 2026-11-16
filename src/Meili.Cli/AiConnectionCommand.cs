using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Meili.Automation;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Meili.Cli;

internal static class AiConnectionCommand
{
    private sealed record Result(int Code, string Output, string Error);
    public static async Task<object> RunAsync(string[] args, Action<string> progress, CancellationToken ct)
    {
        if (args.Length < 2 || args[1] is not ("codex" or "claude-code")) throw new ArgumentException("Usage: connect codex|claude-code [--data-dir PATH] [--project PATH] [--check] [--replace] [--json]");
        var client = args[1]; string? directory = null, project = null; var check = false; var replace = false; var seen = new HashSet<string>();
        for (var i = 2; i < args.Length; i++)
        {
            var key = args[i]; if (!seen.Add(key)) throw new ArgumentException("Duplicate connection option.");
            if (key == "--json") continue;
            if (key == "--check") { check = true; continue; }
            if (key == "--replace") { replace = true; continue; }
            if (key is not ("--data-dir" or "--project") || ++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Unknown or incomplete connection option.");
            if (key == "--data-dir") directory = args[i]; else project = args[i];
        }
        var executable = OwnExecutable(); var data = LocalPaths.Resolve(directory).Path;
        var connection = new McpConnectionSetup(executable, data, OperatingSystem.IsWindows());
        var root = project ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("User profile unavailable; use --project PATH.");
        var clientPath = AiClientLocator.Find(client) ?? throw new AiSetupException("client_not_found", "The selected AI client was not found.", client == "codex" ? "Install Codex CLI, restart the terminal and run codex --version, then retry this command." : "Install Claude Code, restart the terminal and run claude --version, then retry this command.");
        progress("[1/4] Checking " + (client == "codex" ? "Codex" : "Claude Code") + "…");
        var version = await RunClient(clientPath, ["--version"], ct); if (version.Code != 0) throw ClientFailed("Client version check failed.");
        progress("      " + new string(version.Output.Split('\n')[0].Where(c => !char.IsControl(c)).Take(160).ToArray()));
        if (OperatingSystem.IsWindows() && Path.GetExtension(clientPath).ToLowerInvariant() is ".cmd" or ".bat"
            && new[] { executable }.Concat(connection.Arguments).Any(v => v.IndexOfAny(['%', '"', '\r', '\n']) >= 0)) throw new AiSetupException("client_setup_failed", "The selected path is not safe for a batch-script client.", "Use a native .exe client for this path. No skill or client configuration was changed.");
        var current = await Get(clientPath, client, ct);
        if (current.Code != 0 && !Missing(current)) throw ClientFailed("Cannot inspect the client's existing MCP configuration.");
        var matches = current.Code == 0 && Matches(current.Output, client, connection);
        if (current.Code == 0 && !matches && !replace) throw new AiSetupException("connection_conflict", "An easysellput MCP connection already exists with different settings.", "Review the existing connection with " + (client == "codex" ? "codex" : "claude") + " mcp get easysellput. Use --replace only if you intend to replace that named connection; all other connections stay unchanged.");
        if (current.Code == 0 && !matches && replace && client == "claude-code" && !current.Output.Split('\n').Any(line => line.Trim().StartsWith("Scope: User", StringComparison.OrdinalIgnoreCase)))
            throw new AiSetupException("connection_conflict", "The existing Claude connection is outside user scope.", "Review its scope with claude mcp get easysellput and remove or rename it explicitly. This command changes only a user-scoped entry.");
        var skill = AiSkillInstaller.Check(client, root);
        progress("[2/4] Verifying local MCP handshake, read-only tools and data directory…");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        // SDK 2.4 StdioClientTransport wraps Windows commands in cmd /c; quoted app paths
        // then lose the required outer quote pair. Start our known executable directly,
        // with literal ArgumentList tokens, and retain the SDK's stream protocol transport.
        var probeStart = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in connection.Arguments) probeStart.ArgumentList.Add(argument);
        using var probe = new Process { StartInfo = probeStart }; probe.Start();
        var probeErrors = Read(probe.StandardError);
        try
        {
            await using (var mcp = await McpClient.CreateAsync(new StreamClientTransport(probe.StandardInput.BaseStream, probe.StandardOutput.BaseStream), new McpClientOptions { ClientInfo = new() { Name = McpSessions.SetupClientName, Version = "2.0" } }, cancellationToken: deadline.Token))
            {
                var tools = await mcp.ListToolsAsync(cancellationToken: deadline.Token);
                if (!OperationCatalog.All.Where(o => !o.Writes).Select(o => o.Name).ToHashSet().SetEquals(tools.Select(t => t.Name))) throw new AiSetupException("mcp_verification_failed", "MCP tool discovery did not match the read-only catalog.", "Keep the app and bundled CLI from the same desktop package; download a complete package and retry.");
                var result = await mcp.CallToolAsync("get_settings", cancellationToken: deadline.Token);
                var settings = WireJson.Element(result.StructuredContent);
                if (!settings.TryGetProperty("data", out var settingsData) || !settingsData.TryGetProperty("dataDirectory", out var directoryData)
                    || !directoryData.TryGetProperty("path", out var resolved) || !string.Equals(Path.GetFullPath(resolved.GetString() ?? ""), Path.GetFullPath(data), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new AiSetupException("mcp_verification_failed", "MCP resolved a different data directory.", "Copy a fresh setup command from the desktop app using the intended data directory.");
                if (result.IsError == true) throw new AiSetupException("mcp_verification_failed", "MCP settings check failed.", "Check access to the selected data directory and try connect --check again.");
            }
        }
        finally
        {
            try { probe.StandardInput.Close(); } catch (ObjectDisposedException) {/* expected: SDK stream disposal may already close stdin. */}
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await probe.WaitForExitAsync(shutdown.Token); }
            catch (OperationCanceledException) { if (!probe.HasExited) probe.Kill(entireProcessTree: true); await probe.WaitForExitAsync(CancellationToken.None); }
            await probeErrors;
        }
        if (check) return new { client, status = "checked", clientVersion = version.Output.Trim(), skill, connection = matches ? "already_configured" : current.Code == 0 ? "replace_requested" : "register", dataDirectory = data, readOnly = true, changesApplied = false };
        progress("[3/4] Installing companion skill…");
        var installed = AiSkillInstaller.Install(client, root);
        progress("[4/4] " + (matches ? "Keeping existing connection…" : "Registering read-only MCP…"));
        if (!matches)
        {
            if (current.Code == 0)
            {
                var removed = await RunClient(clientPath, client == "codex" ? ["mcp", "remove", "easysellput"] : ["mcp", "remove", "--scope", "user", "easysellput"], ct);
                if (removed.Code != 0) throw ClientFailed("Could not remove the explicitly selected easysellput entry; registration was not attempted.");
            }
            var registration = client == "codex" ? new[] { "mcp", "add", "easysellput", "--", executable }.Concat(connection.Arguments).ToArray()
                : new[] { "mcp", "add", "--transport", "stdio", "--scope", "user", "easysellput", "--", executable }.Concat(connection.Arguments).ToArray();
            var added = await RunClient(clientPath, registration, ct);
            if (added.Code != 0) throw ClientFailed("MCP registration failed; the skill is installed and safe to reuse on retry. If --replace was selected, the previous named connection was removed; retry setup or restore it using your client configuration backup.");
            var verified = await Get(clientPath, client, ct);
            if (verified.Code != 0 || !Matches(verified.Output, client, connection)) throw ClientFailed("The client did not confirm the expected read-only MCP registration.");
        }
        var next = client == "codex" ? "$easysellput-local Compare NVDA and AMD using current quotes and recent events; state source and as-of time." : "/easysellput-local Compare NVDA and AMD using current quotes and recent events; state source and as-of time.";
        return new { client, status = matches ? "already_configured" : "configured", skill = installed, dataDirectory = data, readOnly = true, nextStep = "Restart the client or open a new session, then use: " + next };
    }
    private static AiSetupException ClientFailed(string message) => new("client_setup_failed", message, "Run the client's --version and mcp get easysellput commands in this terminal. Check its configuration permissions, then retry. Other MCP entries are never edited by EasySellPut.");
    private static Task<Result> Get(string executable, string client, CancellationToken ct) => RunClient(executable, client == "codex" ? ["mcp", "get", "easysellput", "--json"] : ["mcp", "get", "easysellput"], ct);
    private static bool Missing(Result result)
    {
        var text = result.Output + "\n" + result.Error;
        return text.Contains("No MCP server named", StringComparison.OrdinalIgnoreCase) || text.Contains("No MCP server found with name", StringComparison.OrdinalIgnoreCase);
    }
    private static bool Matches(string output, string client, McpConnectionSetup connection)
    {
        if (client == "codex")
        {
            try
            {
                using var doc = JsonDocument.Parse(output); var entry = doc.RootElement;
                if (entry.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False) return false;
                var transport = entry.GetProperty("transport");
                return transport.GetProperty("type").GetString() == "stdio" && transport.GetProperty("command").GetString() == connection.Executable
                    && transport.GetProperty("args").EnumerateArray().Select(v => v.GetString()).SequenceEqual(connection.Arguments);
            }
            catch (JsonException) { return false; }
            catch (KeyNotFoundException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
        var lines = output.Split('\n').Select(line => line.Trim()).ToArray();
        return lines.Any(line => line.StartsWith("Scope: User", StringComparison.OrdinalIgnoreCase))
            && lines.Contains("Type: stdio") && lines.Contains("Command: " + connection.Executable) && lines.Contains("Args: " + string.Join(" ", connection.Arguments));
    }
    private static string OwnExecutable()
    {
        var path = Environment.ProcessPath ?? throw new IOException("Executable unavailable.");
        if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            path = Path.ChangeExtension(Assembly.GetEntryAssembly()!.Location, OperatingSystem.IsWindows() ? ".exe" : null);
        if (!File.Exists(path) || path.Contains("/AppTranslocation/", StringComparison.Ordinal)) throw new AiSetupException("unstable_executable", "The executable is missing or running from a temporary macOS location.", "Move EasySellPut to Applications and reopen it, or use a complete published CLI package.");
        return Path.GetFullPath(path);
    }
    private static async Task<Result> RunClient(string executable, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        if (OperatingSystem.IsWindows() && Path.GetExtension(executable).ToLowerInvariant() is ".cmd" or ".bat")
        {
            var values = new[] { executable }.Concat(arguments).ToArray();
            if (values.Any(v => v.IndexOfAny(['%', '"', '\r', '\n']) >= 0)) throw new AiSetupException("client_setup_failed", "This batch client cannot safely receive the selected path.", "Use a native .exe client for paths containing percent signs or quotes, or select a simpler directory. No registration was attempted.");
            start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            // cmd.exe /s strips the outer quote pair. Every actual token stays quoted; delayed expansion is off.
            start.Arguments = "/d /v:off /s /c \"" + string.Join(" ", values.Select(value => "\"" + value + "\"")) + "\"";
        }
        else if (OperatingSystem.IsWindows() && Path.GetExtension(executable).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            string Quote(string s) => "'" + s.Replace("'", "''") + "'";
            var script = "& " + string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote)) + "; exit $LASTEXITCODE";
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
        }
        else { start.FileName = executable; foreach (var arg in arguments) start.ArgumentList.Add(arg); }
        start.StandardOutputEncoding = Encoding.UTF8; start.StandardErrorEncoding = Encoding.UTF8; start.Environment["NO_COLOR"] = "1"; start.Environment["FORCE_COLOR"] = "0";
        using var process = new Process { StartInfo = start };
        try { process.Start(); } catch (System.ComponentModel.Win32Exception) { throw ClientFailed("The selected client could not be launched."); }
        var output = Read(process.StandardOutput); var error = Read(process.StandardError);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        return new(process.ExitCode, await output, await error);
    }
    private static async Task<string> Read(StreamReader reader)
    {
        var text = new StringBuilder(); var buffer = new char[2048]; int count;
        while ((count = await reader.ReadAsync(buffer)) > 0) if (text.Length < 16384) text.Append(buffer, 0, Math.Min(count, 16384 - text.Length));
        return text.ToString();
    }
}
