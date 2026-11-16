using System.Diagnostics;
using System.Text.Json;
using Meili.Automation;
using Meili.Core.Settings;

namespace Meili.Shared.Services;

public interface IUpdateCoordinator
{
    bool IsConfigured => true;
    bool CanInstall { get; }
    Task<UpdateCheck> CheckAsync(CancellationToken ct);
    Task<string> DownloadAsync(UpdateRelease release, IProgress<double> progress, CancellationToken ct);
    Task PrepareInstallAsync(UpdateRelease release, string archive, CancellationToken ct);
}

/// <summary>Desktop ownership of updates; the MCP server never launches installers or replaces itself.</summary>
public sealed class UpdateCoordinator(SettingsStore settings, string dataDirectory, string baseDirectory) : IUpdateCoordinator
{
    public bool IsConfigured => UpdateClient.IsConfigured;
    public bool CanInstall
    {
        get { try { _ = UpdateInstaller.InstalledRoot(baseDirectory, OperatingSystem.IsWindows()); return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(); } catch (IOException) { return false; } }
    }
    public async Task<UpdateCheck> CheckAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var http = await UpdateClient.CreateHttpAsync(settings.Current.Network, deadline.Token);
        return await new UpdateClient(http, UpdateClient.RuntimeId, UpdateClient.CurrentVersion).CheckAsync(deadline.Token);
    }
    public async Task<string> DownloadAsync(UpdateRelease release, IProgress<double> progress, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(15));
        using var http = await UpdateClient.CreateHttpAsync(settings.Current.Network, deadline.Token);
        var folder = Path.Combine(Path.GetTempPath(), "EasySellPut-updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return await new UpdateClient(http, UpdateClient.RuntimeId, UpdateClient.CurrentVersion).DownloadAsync(release, folder, progress, deadline.Token);
    }
    public async Task PrepareInstallAsync(UpdateRelease release, string archive, CancellationToken ct)
    {
        var target = UpdateInstaller.InstalledRoot(baseDirectory, OperatingSystem.IsWindows());
        var sessions = McpSessions.Read(dataDirectory);
        if (!sessions.Available) throw new IOException("Cannot inspect active MCP sessions in this data directory.");
        if (sessions.Connections.Count > 0) throw new InvalidOperationException("update_mcp_active");
        var automation = Path.GetDirectoryName(McpConnectionSetup.Bundled(baseDirectory, dataDirectory).Executable)!;
        var folder = Path.Combine(Path.GetDirectoryName(archive)!, "helper-" + Guid.NewGuid().ToString("N"));
        await Task.Run(() => CopyDirectory(automation, folder, ct), ct);
        ct.ThrowIfCancellationRequested();
        var executable = Path.Combine(folder, OperatingSystem.IsWindows() ? "easysellput.exe" : "easysellput");
        var ready = Path.Combine(folder, "ready");
        using var parent = Process.GetCurrentProcess();
        var plan = new UpdateInstallPlan(archive, release.SignedManifest, release.Signature, UpdateClient.RuntimeId, target, dataDirectory, Environment.ProcessId, parent.StartTime.ToUniversalTime().Ticks, ready);
        var planPath = Path.Combine(folder, "install.json");
        new SnapshotFile(planPath).Write(JsonSerializer.Serialize(plan, UpdateInstaller.Json));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        foreach (var argument in new[] { "update", "apply", "--plan", planPath }) start.ArgumentList.Add(argument);
        var cancelFile = ready + ".cancel";
        void CancelHelper()
        {
            try { using var marker = new FileStream(cancelFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Meili.Core.Diagnostics.AppLog.Warn("updater", "Could not persist installer cancellation", ex); }
        }
        using var cancellation = ct.Register(CancelHelper);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the update helper.");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(3));
            while (!File.Exists(ready))
            {
                if (process.HasExited) throw new IOException("Update preflight failed. The application has not been replaced.");
                await Task.Delay(150, deadline.Token);
            }
            ct.ThrowIfCancellationRequested();
            if(process.HasExited)throw new IOException("Update helper exited before the restart handoff.");
            // Only now may the view model request a normal application shutdown.
        }
        catch
        {
            CancelHelper();
            if(!process.HasExited)
            {
                using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try{await process.WaitForExitAsync(cleanup.Token);}
                catch(OperationCanceledException){if(!process.HasExited)process.Kill(entireProcessTree:true);}
            } // Parent is still alive: installation has not begun.
            throw;
        }
    }
    private static void CopyDirectory(string source, string target, CancellationToken ct)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked update helper is not supported.");
        ct.ThrowIfCancellationRequested(); Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            if (new FileInfo(file).LinkTarget is not null) throw new IOException("Linked update helper is not supported.");
            var copy = Path.Combine(target, Path.GetFileName(file)); File.Copy(file, copy);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(copy, File.GetUnixFileMode(file));
        }
        foreach (var directory in Directory.EnumerateDirectories(source)) CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)), ct);
    }
}
