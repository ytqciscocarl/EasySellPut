using System.Text.Json;
using Meili.Automation;
using Meili.Core.Diagnostics;
using Meili.Core.Settings;

namespace Meili.Cli;

internal static class UpdateCommand
{
    public static async Task<object?> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length is 2 or 3 && args[1] == "source" && (args.Length == 2 || args[2] == "--json"))
            return UpdateClient.SourceConfiguration;
        if (args.Length >= 2 && args[1] == "apply")
        {
            if (args.Length != 4 || args[2] != "--plan") throw new ArgumentException("update apply --plan PATH is the desktop installation handoff.");
            var file = new FileInfo(args[3]);
            if (!file.Exists || file.Length > 131072 || file.LinkTarget is not null) throw new ArgumentException("Invalid installation handoff.");
            var plan = JsonSerializer.Deserialize<UpdateInstallPlan>(await File.ReadAllTextAsync(file.FullName, ct), UpdateInstaller.Json) ?? throw new ArgumentException("Missing installation handoff.");
            AppLog.Initialize(plan.DataDirectory);
            return await UpdateInstaller.ApplyAsync(plan, ct);
        }
        if (args.Length < 2 || args[1] is not ("check" or "download")) throw new ArgumentException("update source [--json] | update check|download [--data-dir PATH] [--json]");
        string? data = null; var seen = new HashSet<string>();
        for (var i = 2; i < args.Length; i++)
        {
            if (!seen.Add(args[i])) throw new ArgumentException("Duplicate update option.");
            if (args[i] == "--json") continue;
            if (args[i] != "--data-dir" || ++i >= args.Length) throw new ArgumentException("Unknown update option."); data = args[i];
        }
        var directory = LocalPaths.Resolve(data).Path;
        var settings = new SettingsStore(directory).Load();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(args[1] == "check" ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(15));
        using var http = await UpdateClient.CreateHttpAsync(settings.Network, deadline.Token);
        var client = new UpdateClient(http, UpdateClient.RuntimeId, UpdateClient.CurrentVersion);
        var check = await client.CheckAsync(deadline.Token);
        if (args[1] == "check" || check.Release is null) return check;
        var folder = Path.Combine(Path.GetTempPath(), "EasySellPut-updates", Guid.NewGuid().ToString("N"));
        var archive = await client.DownloadAsync(check.Release, folder, null, deadline.Token);
        return new { status = "downloaded_verified", version = check.LatestVersion, archive, install = "Use the desktop Settings > Updates to confirm installation. No application or client process was changed." };
    }
}
