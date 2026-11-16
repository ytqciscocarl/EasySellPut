using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meili.Automation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Meili.Cli;

public static class Program
{
    public static Task<int> Main(string[] args) => RunAsync(args);

    /// <summary>Shared executable entry point; test hosts may supply deterministic market services.</summary>
    public static async Task<int> RunAsync(string[] args, Func<Meili.Core.Settings.DataDirectoryInfo, AutomationService>? serviceFactory = null)
    {
        bool json = args.Contains("--json");
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try
        {
            if(args.FirstOrDefault()=="update")
            {
                if(args.Skip(1).SequenceEqual(new[]{"--help"})){Console.WriteLine("update source [--json] | update check|download [--data-dir PATH] [--json]\nReads signed stable GitHub Releases. Download verifies signature/size/SHA-256; installation requires desktop confirmation. update apply --plan PATH is the internal desktop handoff, never an MCP tool.");return 0;}
                var updateResult=await UpdateCommand.RunAsync(args,stop.Token);
                if(json)Console.WriteLine(WireJson.Element(WireJson.Success(updateResult)).GetRawText());else if(args.ElementAtOrDefault(1)!="apply")PrintHuman(WireJson.Element(updateResult));
                return 0;
            }
            if(args.FirstOrDefault()=="connect")
            {
                if(args.Skip(1).SequenceEqual(new[]{"--help"}))
                {Console.WriteLine("connect codex|claude-code [--data-dir PATH] [--project PATH] [--check] [--replace] [--json]\nChecks the installed client and read-only MCP, installs/upgrades the unedited bundled skill, then registers and verifies the connection. --check does not modify client configuration or skills. --replace explicitly replaces a conflicting easysellput entry only.");return 0;}
                var connected=await AiConnectionCommand.RunAsync(args,line=>{if(!json)Console.WriteLine(line);},stop.Token);
                if(json)Console.WriteLine(WireJson.Element(WireJson.Success(connected)).GetRawText());else
                {
                    var report=WireJson.Element(connected);
                    Console.WriteLine(args.Contains("--check")?"✓ Checks passed · no client/skill changes applied":"✓ Setup complete · read-only MCP + companion skill");
                    Console.WriteLine("Data   "+report.GetProperty("dataDirectory").GetString());
                    var skill=report.GetProperty("skill");Console.WriteLine("Skill  "+skill.GetProperty("status").GetString()+" · "+skill.GetProperty("path").GetString());
                    if(report.TryGetProperty("nextStep",out var next))Console.WriteLine("\nNext   "+next.GetString());
                }
                return 0;
            }
            if (args.FirstOrDefault() == "install-skill")
            {
                if (args.Skip(1).SequenceEqual(new[] { "--help" }))
                {
                    Console.WriteLine("install-skill --client codex|claude-code [--project PATH] [--json]\nDefault: user profile. --project installs only inside the selected project. Custom skills are preserved; exact known bundled versions upgrade with a backup. Reconnect the AI client after installation.");
                    return 0;
                }
                string? client = null, project = null;
                var seen = new HashSet<string>();
                for (var i = 1; i < args.Length; i++)
                {
                    var key = args[i];
                    if (!seen.Add(key)) throw new ArgumentException("Duplicate setup option.");
                    if (key == "--json") continue;
                    if (key is not ("--client" or "--project") || ++i >= args.Length) throw new ArgumentException("Usage: install-skill --client codex|claude-code [--project PATH] [--json]");
                    if (key == "--client") client = args[i]; else project = args[i];
                }
                if (client is null) throw new ArgumentException("--client is required.");
                var root = project ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(root)) throw new IOException("User profile unavailable; specify --project.");
                var installed = AiSkillInstaller.Install(client, root);
                if (json) Console.WriteLine(WireJson.Element(WireJson.Success(installed)).GetRawText()); else PrintHuman(WireJson.Element(installed));
                return 0;
            }
            var parsed = Parse(args);
            if (parsed.Help)
            {
                if (json) Console.WriteLine(WireJson.Element(WireJson.Success(new
                {
                    version = typeof(Program).Assembly.GetName().Version?.ToString(3),
                    operations = OperationCatalog.All.Where(o => parsed.Operation is null || o.Name == parsed.Operation)
                        .Select(o => new { o.Name, o.Description, o.Writes, inputSchema = o.Schema })
                })).GetRawText());
                else PrintHelp(parsed.Operation);
                return 0;
            }
            if (parsed.Version) { Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3)); return 0; }
            var directory = LocalPaths.Resolve(parsed.DataDir);
            using var service = serviceFactory?.Invoke(directory) ?? new AutomationService(directory);
            if (parsed.Mcp) { await ServeMcp(service, parsed.AllowWrites, parsed.Timeout, stop.Token); return 0; }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(parsed.Timeout));
            var result = await service.ExecuteAsync(parsed.Operation!, parsed.Arguments, true, deadline.Token);
            if (json) Console.WriteLine(result.GetRawText()); else PrintHuman(result.GetProperty("data"));
            return 0;
        }
        catch (Exception e) // expected: return structured errors and a nonzero CLI exit or MCP isError
        {
            if(args.FirstOrDefault()=="update")Meili.Core.Diagnostics.AppLog.Error("updater","Update command failed",e);
            Console.Error.WriteLine($"{WireJson.Code(e)}: {WireJson.Element(WireJson.Error(e)).GetProperty("error").GetProperty("message").GetString()}");
            if (!json) Console.Error.WriteLine("Next: "+WireJson.Recovery(e));
            if (json) Console.WriteLine(JsonSerializer.Serialize(WireJson.Error(e), WireJson.Options));
            return e switch { ArgumentException or JsonException or FormatException or OverflowException => 2, UnauthorizedAccessException => 3, Meili.Core.Settings.DataConflictException or SkillInstallConflictException => 4, AiSetupException setup when setup.Code=="connection_conflict" => 4, OperationCanceledException => 130, _ => 1 };
        }
    }

    private static async Task ServeMcp(AutomationService service, bool writes, int timeout, CancellationToken ct)
    {
        string? observedClient=null;
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], DisableDefaults = true });
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddMcpServer(o =>
        {
            o.ServerInfo = new Implementation { Name = "EasySellPut", Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.6.0" };
            o.ServerInstructions = AiWorkflowGuide.Instructions + (writes ? " Explicit write mode is enabled; still require a user-requested change." : " This connection is read-only.");
        }).WithStdioServerTransport()
        .WithListPromptsHandler((request,cancel)=>ValueTask.FromResult(new ListPromptsResult
        {
            Prompts=AiWorkflowGuide.All.Select(w=>new Prompt{Name=w.Name,Title=w.Title,Description=w.Description,
                Arguments=[new PromptArgument{Name="symbol",Description="US ticker, e.g. NVDA; required for ticker workflows.",Required=w.NeedsSymbol},new PromptArgument{Name="question",Description="Optional user question or explicit position context; max 1000 characters."}]}).ToList()
        }))
        .WithGetPromptHandler((request,cancel)=>
        {
            observedClient=request.Server.ClientInfo?.Name;
            var parameters=request.Params??throw new ModelContextProtocol.McpProtocolException("Missing prompt parameters.",ModelContextProtocol.McpErrorCode.InvalidParams);
            var recipe=AiWorkflowGuide.All.FirstOrDefault(w=>w.Name==parameters.Name)??throw new ModelContextProtocol.McpProtocolException("Unknown EasySellPut prompt.",ModelContextProtocol.McpErrorCode.InvalidParams);
            var arguments=parameters.Arguments??new Dictionary<string,JsonElement>();
            if(arguments.Keys.Any(k=>k is not ("symbol" or "question")))throw new ModelContextProtocol.McpProtocolException("Unknown prompt argument.",ModelContextProtocol.McpErrorCode.InvalidParams);
            if(arguments.Values.Any(v=>v.ValueKind!=JsonValueKind.String))throw new ModelContextProtocol.McpProtocolException("Prompt arguments must be strings.",ModelContextProtocol.McpErrorCode.InvalidParams);
            var symbol=arguments.TryGetValue("symbol",out var symbolArg)?symbolArg.GetString():null;
            var question=arguments.TryGetValue("question",out var questionArg)?questionArg.GetString():null;
            if((recipe.NeedsSymbol||!string.IsNullOrWhiteSpace(symbol))&&!Meili.Core.Settings.WatchlistStore.TryNormalizeSymbol(symbol,out _,out _))throw new ModelContextProtocol.McpProtocolException("A valid ticker symbol is required.",ModelContextProtocol.McpErrorCode.InvalidParams);
            if(question?.Length>1000)throw new ModelContextProtocol.McpProtocolException("Question is too long.",ModelContextProtocol.McpErrorCode.InvalidParams);
            return ValueTask.FromResult(new GetPromptResult{Description=recipe.Description,Messages=[new PromptMessage{Role=Role.User,Content=new TextContentBlock{Text=recipe.Title+" for "+(symbol?.ToUpperInvariant()??"local connection")+".\n"+recipe.Steps+"\nDiscover actual schemas before calling tools.\nUser question/context: "+(question??"Use a concise evidence-first summary.")}}]});
        })
        .WithListResourcesHandler((request,cancel)=>ValueTask.FromResult(new ListResourcesResult{Resources=[
            new Resource{Uri="easysellput://guides/workflows",Name="Task recipes",Description="Tool selection and task-specific research workflows.",MimeType="text/markdown"},
            new Resource{Uri="easysellput://guides/skill",Name="Companion skill",Description="Complete local research guide, safety/units and all tool usage.",MimeType="text/markdown"},
            new Resource{Uri="easysellput://capabilities",Name="Available capabilities",Description="Compiled tool catalog for this connection's permissions.",MimeType="application/json"}
        ]}))
        .WithReadResourceHandler((request,cancel)=>
        {
            observedClient=request.Server.ClientInfo?.Name;
            var uri=request.Params?.Uri;
            var content=uri switch
            {
                "easysellput://guides/workflows"=>AiWorkflowGuide.Markdown,
                "easysellput://guides/skill"=>AiSkillInstaller.Content,
                "easysellput://capabilities"=>WireJson.Element(new{readOnly=!writes,operations=OperationCatalog.All.Where(o=>writes||!o.Writes).Select(o=>new{o.Name,o.Description,o.Writes,inputSchema=o.Schema})}).GetRawText(),
                _=>throw new ModelContextProtocol.McpProtocolException("Unknown resource URI; arbitrary file/URL reads are not supported.",ModelContextProtocol.McpErrorCode.InvalidParams)
            };
            return ValueTask.FromResult(new ReadResourceResult{Contents=[new TextResourceContents{Uri=uri!,MimeType=uri=="easysellput://capabilities"?"application/json":"text/markdown",Text=content}]});
        })
        .WithListToolsHandler((request, cancel) =>
        {
            observedClient=request.Server.ClientInfo?.Name;
            return ValueTask.FromResult(new ListToolsResult
        {
            Tools = OperationCatalog.All.Where(x => writes || !x.Writes).Select(x => new Tool
            {
                Name = x.Name,
                Title = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(x.Name.Replace('_',' ')),
                Description = x.Description,
                InputSchema = x.Schema,
                Annotations = new ToolAnnotations { ReadOnlyHint = !x.Writes, DestructiveHint = x.Writes && (x.Name.StartsWith("delete_",StringComparison.Ordinal) || x.Name.StartsWith("clear_",StringComparison.Ordinal) || x.Name=="save_strategy"), IdempotentHint = !x.Writes, OpenWorldHint = x.Name is "search_symbols" or "get_quotes" or "get_bars" or "get_expirations" or "get_option_chain" or "get_fundamentals" or "get_financials" or "get_news" or "get_market_events" or "get_chart_analysis" or "analyze_option_chain" or "get_pricing_assumptions" or "get_volatility_insights" or "record_option_snapshot" or "probe_data_sources" }
            }).ToList()
        });
        })
        .WithCallToolHandler(async (request, cancel) =>
        {
            try
            {
                observedClient=request.Server.ClientInfo?.Name;
                var p = request.Params ?? throw new ArgumentException("Missing tool parameters.");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel); deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
                var input = WireJson.Element(p.Arguments ?? new Dictionary<string, JsonElement>());
                var data = await service.ExecuteAsync(p.Name, input, writes, deadline.Token);
                return new CallToolResult { Content = [new TextContentBlock { Text = data.GetRawText() }], StructuredContent = data };
            }
            catch (Exception e) // expected: return structured errors and a nonzero CLI exit or MCP isError
            {
                var error = WireJson.Element(WireJson.Error(e));
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = error.GetRawText() }], StructuredContent = error };
            }
        });
        using var host = builder.Build();
        var server=host.Services.GetService<ModelContextProtocol.Server.McpServer>();
        using var session=new McpSessionLease(service.DirectoryInfo.Path,writes,()=>server?.ClientInfo?.Name??observedClient);
        await host.RunAsync(ct);
    }

    internal sealed record Parsed(string? Operation, JsonElement Arguments, string? DataDir, int Timeout, bool Mcp, bool AllowWrites, bool Help, bool Version);
    internal static Parsed Parse(string[] args)
    {
        string? directory = null, input = null; int timeout = 60; bool mcp = false, writes = false, help = false, version = false;
        var words = new List<string>(); var fields = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--json") continue;
            if (arg is "--help" or "-h") { help = true; continue; }
            if (arg == "--version") { version = true; continue; }
            if (arg == "--allow-writes") { writes = true; continue; }
            if (arg.StartsWith("--"))
            {
                var pair = arg[2..].Split('=', 2); var key = pair[0];
                var value = pair.Length == 2 ? pair[1] : ++i < args.Length ? args[i] : throw new ArgumentException($"Missing value for --{key}.");
                if (value.StartsWith("--")) throw new ArgumentException($"Missing value for --{key}.");
                switch (key)
                {
                    case "data-dir": if (directory is not null) throw new ArgumentException("Duplicate --data-dir."); directory = value; break;
                    case "timeout": if (!int.TryParse(value, out timeout) || timeout is < 1 or > 600) throw new ArgumentException("timeout must be 1..600 seconds."); break;
                    case "input": if (input is not null) throw new ArgumentException("Duplicate --input."); input = value; break;
                    default: if (!fields.TryAdd(key, value)) throw new ArgumentException($"Duplicate --{key}."); break;
                }
            }
            else words.Add(arg);
        }
        if (words.Count == 0) { if (fields.Count > 0 || input is not null) throw new ArgumentException("An operation is required."); if (!version) help = true; return new(null, WireJson.Element(new { }), directory, timeout, false, writes, help, version); }
        if (words[0] == "mcp")
        {
            if (words.Count != 2 || words[1] != "serve" || fields.Count > 0 || input is not null) throw new ArgumentException("Usage: easysellput mcp serve [--data-dir PATH] [--allow-writes] [--timeout SECONDS]");
            mcp = true; return new(null, WireJson.Element(new { }), directory, timeout, mcp, writes, help, version);
        }
        var command = words[0];
        var aliases = new Dictionary<string, string> { ["quote"] = "get_quotes", ["bars"] = "get_bars", ["search"] = "search_symbols", ["expirations"] = "get_expirations", ["options"] = "get_option_chain", ["fundamentals"] = "get_fundamentals", ["financials"] = "get_financials", ["news"] = "get_news", ["events"] = "get_market_events", ["chart"] = "get_chart_analysis", ["option-analysis"] = "analyze_option_chain", ["insights"] = "get_volatility_insights", ["presets"] = "list_strategy_presets", ["sources"] = "get_source_health", ["price"] = "calculate_option", ["indicators"] = "calculate_indicators" };
        if (command is "watchlist" or "strategy" or "alerts" or "settings")
        {
            if (words.Count < 2) throw new ArgumentException($"{command} needs a subcommand. See --help.");
            var sub = words[1]; words.RemoveAt(1);
            command = (command, sub) switch
            {
                ("watchlist", "list") => "get_watchlists",
                ("watchlist", "add") => "add_watchlist_symbol",
                ("watchlist", "remove") => "remove_watchlist_symbol",
                ("watchlist", "create-group") => "create_watchlist_group",
                ("watchlist", "rename-group") => "rename_watchlist_group",
                ("watchlist", "delete-group") => "delete_watchlist_group",
                ("strategy", "list") => "get_saved_strategies",
                ("strategy", "save") => "save_strategy",
                ("strategy", "delete") => "delete_strategy",
                ("strategy", "analyze") => "analyze_strategy",
                ("alerts", "list") => "get_alerts",
                ("alerts", "history") => "get_alert_history",
                ("alerts", "save") => "save_alert",
                ("alerts", "delete") => "delete_alert",
                ("alerts", "enable") => "set_alert_enabled",
                ("settings", "show") => "get_settings",
                _ => throw new ArgumentException("Unknown subcommand.")
            };
        }
        else if (aliases.TryGetValue(command, out var mapped)) command = mapped;
        var op = OperationCatalog.Find(command);
        if (help) return new(command, WireJson.Element(new { }), directory, timeout, false, writes, true, version);
        JsonObject values = new();
        if (input is not null)
        {
            var file = new FileInfo(input); if (file.Length > 2_000_000) throw new ArgumentException("Input JSON exceeds 2 MB.");
            values = JsonNode.Parse(File.ReadAllText(input)) as JsonObject ?? throw new ArgumentException("--input must contain a JSON object.");
        }
        var props = op.Schema.GetProperty("properties");
        if (words.Count > 1)
        {
            string key = command switch { "get_quotes" => "symbols", "search_symbols" => "query", _ when props.TryGetProperty("symbol", out _) => "symbol", _ => throw new ArgumentException("Unexpected positional argument.") };
            if (values.ContainsKey(key)) throw new ArgumentException($"Duplicate argument {key}.");
            if (key == "symbols") values[key] = new JsonArray(words.Skip(1).Select(s => JsonValue.Create(s)).ToArray());
            else { if (words.Count != 2) throw new ArgumentException("Unexpected positional argument."); values[key] = words[1]; }
        }
        foreach (var (key, value) in fields)
        {
            var name = props.EnumerateObject().Select(p => p.Name).FirstOrDefault(p => Kebab(p) == key || p == key) ?? throw new ArgumentException($"Unknown option --{key}.");
            if (values.ContainsKey(name)) throw new ArgumentException($"Duplicate argument {name} (including --input/positional arguments).");
            var type = props.GetProperty(name).GetProperty("type").GetString();
            values[name] = type switch { "string" => JsonValue.Create(value), "array" or "object" => JsonNode.Parse(value), "integer" => JsonValue.Create(int.Parse(value, CultureInfo.InvariantCulture)), "number" => JsonValue.Create(double.Parse(value, CultureInfo.InvariantCulture)), "boolean" => JsonValue.Create(bool.Parse(value)), _ => throw new ArgumentException("Unsupported argument.") };
        }
        return new(command, JsonSerializer.SerializeToElement(values), directory, timeout, false, writes, false, version);
    }
    private static string Kebab(string s) => string.Concat(s.Select(c => char.IsUpper(c) ? "-" + char.ToLowerInvariant(c) : c.ToString()));
    private static void PrintHelp(string? operation)
    {
        Console.WriteLine("EasySellPut — local CLI + stdio MCP\nUsage: easysellput <command> [options] [--json] [--data-dir PATH] [--timeout 60]\n       easysellput mcp serve [--allow-writes] [--data-dir PATH]\nMaintenance: update check|download [--json] (signed stable desktop updates; installation is desktop-confirmed)\nSetup: connect codex|claude-code [--check] [--json] (verifies MCP, installs skill and registers the client)\n       install-skill --client codex|claude-code [--project PATH] [--json] (CLI only; preserves custom skills; known bundled upgrades are backed up)\nAliases: quote, bars, search, expirations, options, fundamentals, financials, news, events, chart, option-analysis, insights, presets, sources, price, indicators\nGroups: watchlist list/add/remove/create-group/rename-group/delete-group; strategy list/save/delete/analyze; alerts list/history/save/delete/enable; settings show\nUse any operation name below directly. --input FILE accepts an argument JSON object.\nNumbers use invariant decimal units. --help after an operation prints its JSON schema.\nExit: 0 success, 1 failure, 2 arguments, 3 permission, 4 conflict, 130 cancelled/timeout.");
        foreach (var op in OperationCatalog.All.Where(o => operation is null || o.Name == operation))
        {
            Console.WriteLine($"\n{op.Name}{(op.Writes ? " [writes local data]" : "")}: {op.Description}");
            if (operation is not null) Console.WriteLine(JsonSerializer.Serialize(op.Schema, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    private static void PrintHuman(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 && data[0].ValueKind == JsonValueKind.Object)
        {
            var cols = data[0].EnumerateObject().Where(p => p.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)).Select(p => p.Name).Take(8).ToArray();
            Console.WriteLine(string.Join("\t", cols));
            foreach (var row in data.EnumerateArray()) Console.WriteLine(string.Join("\t", cols.Select(k => row.TryGetProperty(k, out var v) ? v.ToString() : "")));
        }
        else Console.WriteLine(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }
}
