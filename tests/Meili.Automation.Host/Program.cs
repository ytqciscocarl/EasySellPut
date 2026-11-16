using Meili.Automation;
using Meili.Automation.Tests;

if(args.Length==5 && args[0]=="--update-transaction"){await UpdateFixture.RunAsync(args[1],args[2],args[3],args[4]);return 0;}

if (args.SequenceEqual(new[] { "--connection-fixtures" }))
{
    var values = new[] {
        new McpConnectionSetup("/Applications/My App's 🚀/easysellput", "/tmp/中文 data '$HOME' $(echo unsafe)", false),
        new McpConnectionSetup(@"C:\Program Files\My App's 🚀\easysellput.exe", @"C:\Users\测试\data '$HOME' $(echo unsafe)", true)
    };
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(values.Select(v => new {v.Executable,v.DataDirectory,v.Windows,v.Arguments,v.Json,v.Toml,v.CodexCommand,v.ClaudeCommand,v.CodexSetupCommand,v.ClaudeSetupCommand})));
    return 0;
}

// Test-only executable: real CLI/MCP adapters and official SDK, deterministic market data.
// This host is never shipped in the production distribution.
return await Meili.Cli.Program.RunAsync(args, directory => new AutomationService(directory, new FakeMarket(), [new FixtureProbe()]));

sealed class FixtureProbe : Meili.Core.Abstractions.IQuoteProvider
{
    public string Id=>"fixture";
    public string DisplayName=>"Offline fixture probe";
    public Meili.Core.Models.DataCapability Capabilities=>Meili.Core.Models.DataCapability.Quote;
    public Task<IReadOnlyList<Meili.Core.Models.Quote>> GetQuotesAsync(IReadOnlyList<string> symbols,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<Meili.Core.Models.Quote>>([new(){Symbol="AAPL",Price=100,Source="fixture"}]);
}
