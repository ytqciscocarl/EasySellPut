using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Meili.Core.Diagnostics;
using Meili.Core.Settings;

namespace Meili.Automation;

public sealed record UpdateInstallPlan(string Archive, string SignedManifest, string Signature, string Rid, string Target, string DataDirectory, int ParentPid, long ParentStartTicks, string ReadyFile);
public sealed record UpdateInstallResult(string Status, string Version, string? Backup = null, string? Error = null, string? Staging = null);

/// <summary>Runs from a temporary copy of the old bundled CLI. No elevation, shell scripts or running-process replacement.</summary>
public static class UpdateInstaller
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string ResultFile(string data)
    {
        var path = Path.GetFullPath(data); if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        var id = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..24];
        return Path.Combine(McpConnectionSetup.ProfileDataDirectory + "-Updater", "state", id + ".json");
    }
    public static string InstalledRoot(string baseDirectory, bool windows)
    {
        var root = Path.GetFullPath(windows ? baseDirectory : Path.Combine(baseDirectory, "../.."));
        ValidateTarget(root, windows);
        return Path.TrimEndingDirectorySeparator(root);
    }
    public static void ValidateTarget(string target, bool windows)
    {
        target = Path.GetFullPath(target);
        if (target.Contains("/AppTranslocation/", StringComparison.Ordinal) || Path.GetDirectoryName(target) is null) throw new IOException("Move the app to a permanent writable installation folder first.");
        RejectLinks(target);
        if (windows)
        {
            if (!File.Exists(Path.Combine(target, "EasySellPut.exe")) || !File.Exists(Path.Combine(target, "portable.flag")) || !File.Exists(Path.Combine(target, "automation", "easysellput.exe"))) throw new IOException("Install a complete EasySellPut desktop package first.");
        }
        else if (!target.EndsWith("/EasySellPut.app", StringComparison.Ordinal) || !File.Exists(Path.Combine(target, "Contents/MacOS/MeiliMac")) || !File.Exists(Path.Combine(target, "Contents/Resources/automation/easysellput"))) throw new IOException("Install the complete EasySellPut.app first.");
    }
    internal static void RejectLinks(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // macOS itself supplies these immutable root aliases for temporary/system paths.
                if (OperatingSystem.IsMacOS() && directory.FullName is "/var" or "/tmp" or "/etc" && directory.ResolveLinkTarget(true)?.FullName == "/private" + directory.FullName) continue;
                throw new IOException("Linked installation/staging paths are not supported.");
            }
    }
    public static async Task VerifyArchiveAsync(string archive, UpdateAsset asset, CancellationToken ct)
    {
        var info = new FileInfo(archive);
        if (!info.Exists || info.LinkTarget is not null || info.Length != asset.Size) throw new InvalidDataException("Package size changed after download.");
        await using var stream = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
        var digest = await SHA256.HashDataAsync(stream, ct);
        if (!Convert.ToHexString(digest).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Package checksum changed after download.");
    }
    public static async Task ExtractAsync(string archive, string output, string expectedRoot, CancellationToken ct)
    {
        RejectLinks(output); Directory.CreateDirectory(output);
        using var zip = ZipFile.OpenRead(archive); long expanded = 0, actualExpanded = 0; var buffer = new byte[65536];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (zip.Entries.Count > 30000) throw new InvalidDataException("Too many package entries.");
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName;
            var parts = name.TrimEnd('/').Split('/');
            var mode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            if (parts.Length == 0 || parts[0] != expectedRoot || parts.Any(p => p is "" or "." or ".." || p.Any(char.IsControl) || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(['\\', ':', '\0', '<', '>', '|', '?', '*', '"']) >= 0 || System.Text.RegularExpressions.Regex.IsMatch(p, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                || (mode & 0xF000) is not (0 or 0x8000 or 0x4000) || !seen.Add(name.TrimEnd('/'))) throw new InvalidDataException("Unsafe or duplicated package path.");
            expanded += entry.Length;
            if (expanded > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Expanded package is too large.");
            var path = Path.Combine(output, Path.Combine(parts));
            if (name.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var input = entry.Open())
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                long actual = 0; int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    actual += count; actualExpanded += count;
                    if (actual > entry.Length || actualExpanded > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Expanded package exceeds declared bounds.");
                    await file.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                if (actual != entry.Length) throw new InvalidDataException("Truncated package entry.");
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)(mode & 0x1FF));
        }
    }
    public static void Swap(string target, string candidate, string backup, bool windows)
    {
        // Portable Windows data moves with the transaction. External profile data never moves.
        var oldData = Path.Combine(target, "data"); var nextData = Path.Combine(candidate, "data");
        var movedData = false; var movedOld = false;
        try
        {
            if (windows && Directory.Exists(oldData))
            {
                RejectLinks(oldData);
                if (Directory.Exists(nextData) || File.Exists(nextData)) throw new InvalidDataException("Update package must not contain user data.");
                Directory.Move(oldData, nextData); movedData = true;
            }
            Directory.Move(target, backup); movedOld = true;
            Directory.Move(candidate, target);
        }
        catch
        {
            // Roll back in reverse order. Never delete a failed backup or the user's portable data.
            if (movedOld && !Directory.Exists(target)) Directory.Move(backup, target);
            if (movedData && Directory.Exists(nextData) && !Directory.Exists(oldData)) Directory.Move(nextData, oldData);
            throw;
        }
    }
    public static async Task<UpdateInstallResult> ApplyAsync(UpdateInstallPlan plan, CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(plan.ReadyFile))throw new InvalidDataException("Missing installer handoff marker.");
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var cancelWatch=new Timer(_=>{if(File.Exists(plan.ReadyFile+".cancel"))cancellation.Cancel();},null,TimeSpan.Zero,TimeSpan.FromMilliseconds(100));
        ct=cancellation.Token;
        if(plan.ParentPid<=0 || plan.ParentStartTicks<=0)throw new InvalidDataException("Invalid desktop process identity.");
        if (plan.Rid != UpdateClient.RuntimeId) throw new InvalidDataException("Update architecture does not match this process.");
        var windows = OperatingSystem.IsWindows();
        if (!windows && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        ValidateTarget(plan.Target, windows);
        EnsureOtherInstancesClosed(plan.Target, plan.ParentPid);
        var bytes = Convert.FromBase64String(plan.SignedManifest);
        UpdateClient.VerifyManifest(bytes, Convert.FromBase64String(plan.Signature), UpdateClient.PublicKey);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(bytes, Json) ?? throw new InvalidDataException("Missing signed manifest.");
        UpdateClient.VerifyRepository(manifest, UpdateClient.Repository);
        if (manifest.SchemaVersion != 1 || !Version.TryParse(manifest.Version, out var version) || version <= new Version(UpdateClient.CurrentVersion.ToString(3))) throw new InvalidDataException("Refusing an incompatible or older update.");
        var asset = manifest.Assets.Single(a => a.Rid == plan.Rid);
        if (asset.FileName != $"EasySellPut-Desktop-{manifest.Version}-{plan.Rid}.zip" || asset.Size is <= 0 or > UpdateClient.MaxPackageBytes) throw new InvalidDataException("Invalid signed asset.");
        await VerifyArchiveAsync(plan.Archive, asset, ct);
        if (McpSessions.Read(plan.DataDirectory) is { Available: false } or { Connections.Count: > 0 }) throw new IOException("Close the AI client's active MCP connection before installing.");
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.Target));
        var data = Path.GetFullPath(plan.DataDirectory);
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (data.Equals(target, comparison) || (data.StartsWith(target + Path.DirectorySeparatorChar, comparison) && !data.Equals(Path.Combine(target, "data"), comparison))) throw new IOException("Move custom data outside the installation folder before updating.");
        if (Path.GetFullPath(ResultFile(data)).StartsWith(target + Path.DirectorySeparatorChar, comparison)) throw new IOException("Update status must live outside the installation folder.");
        var parent = Path.GetDirectoryName(target)!;
        await using var gate = new FileStream(Path.Combine(parent, ".EasySellPut-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var stage = Path.Combine(parent, ".EasySellPut-stage-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(parent, ".EasySellPut-backup-" + Guid.NewGuid().ToString("N"));
        var swapped = false; var swapAttempted = false;
        try
        {
            var rootName = Path.GetFileNameWithoutExtension(asset.FileName);
            await ExtractAsync(plan.Archive, stage, rootName, ct);
            var candidate = windows ? Path.Combine(stage, rootName) : Path.Combine(stage, rootName, "EasySellPut.app");
            ValidateTarget(candidate, windows);
            if (!windows)
            {
                var start = new ProcessStartInfo("/usr/bin/codesign") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "--verify", "--deep", "--strict", candidate }) start.ArgumentList.Add(arg);
                using var check = Process.Start(start)!;
                var output = check.StandardOutput.ReadToEndAsync(ct); var errors = check.StandardError.ReadToEndAsync(ct);
                await check.WaitForExitAsync(ct); await Task.WhenAll(output, errors);
                if (check.ExitCode != 0) throw new InvalidDataException("The macOS bundle signature is invalid.");
            }
            if (windows && Directory.Exists(Path.Combine(candidate, "data"))) throw new InvalidDataException("Update contains portable user data.");
            CheckCancelled(plan,ct); new SnapshotFile(plan.ReadyFile).Write("ready");
            await WaitForParentExitAsync(plan,ct);
            EnsureOtherInstancesClosed(target, -1);
            if (McpSessions.Read(data) is { Available: false } or { Connections.Count: > 0 }) throw new IOException("An MCP session is still using the installation.");
            swapAttempted = true; Swap(target, candidate, backup, windows); swapped = true;
            var result = new UpdateInstallResult("installed", manifest.Version, backup);
            RecordResult(data,result);
            try { Restart(target, data, windows); }
            catch
            {
                var failed = Path.Combine(parent, ".EasySellPut-failed-" + Guid.NewGuid().ToString("N"));
                Swap(target, backup, failed, windows); swapped = false;
                throw;
            }
            return result;
        }
        catch (Exception ex)
        {
            if(ex is OperationCanceledException)AppLog.Info("updater","Update cancelled before replacement completed.");
            else AppLog.Error("updater", "Update installation failed", ex);
            var result = new UpdateInstallResult(swapped ? "restart_required" : ex is OperationCanceledException ? "cancelled" : "failed", manifest.Version, Directory.Exists(backup) ? backup : null, "Update did not finish. The previous application/data were retained; inspect local update diagnostics.", Directory.Exists(stage) ? stage : null);
            try
            {
                RecordResult(data,result);
                if (ex is not OperationCanceledException && !ParentAlive(plan)) Restart(target, data, windows);
            }
            catch (Exception reportError) { AppLog.Error("updater", "Could not report/reopen after installation failure", reportError); }
            throw;
        }
        finally
        {
            // A backup is deliberately retained. Staged leftovers may be removed by the user if cleanup is locked.
            try { if ((swapped || !swapAttempted) && Directory.Exists(stage)) Directory.Delete(stage, true); }
            catch (Exception ex) when(ex is IOException or UnauthorizedAccessException) { AppLog.Warn("updater", "Staging cleanup deferred", ex); }
        }
    }
    private static void RecordResult(string data,UpdateInstallResult result)
    {
        try{new SnapshotFile(ResultFile(data)).Write(JsonSerializer.Serialize(result,Json));}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){AppLog.Error("updater","Could not persist update status",ex);}
    }
    private static void EnsureOtherInstancesClosed(string target, int allowedPid)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)) + Path.DirectorySeparatorChar;
        foreach (var process in Process.GetProcesses()) using (process)
        {
            if (process.Id == allowedPid || process.Id == Environment.ProcessId) continue;
            try
            {
                if (process.ProcessName is not ("MeiliMac" or "EasySellPut" or "easysellput")) continue;
                var executable = process.MainModule?.FileName ?? throw new IOException("Cannot inspect another EasySellPut instance.");
                if (Path.GetFullPath(executable).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Close other EasySellPut/CLI instances before updating this installation.");
            }
            catch (InvalidOperationException) {/* expected: a process that exited while enumerating no longer holds the app. */}
        }
    }
    private static void CheckCancelled(UpdateInstallPlan plan,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(File.Exists(plan.ReadyFile+".cancel"))throw new OperationCanceledException("Installer cancelled by the desktop.");
    }
    internal static async Task WaitForParentExitAsync(UpdateInstallPlan plan,CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromMinutes(2));
        while(ParentAlive(plan)){CheckCancelled(plan,deadline.Token);await Task.Delay(200,deadline.Token);}
        CheckCancelled(plan,deadline.Token);
    }
    private static bool ParentAlive(UpdateInstallPlan plan)
    {
        try { using var process = Process.GetProcessById(plan.ParentPid); return !process.HasExited && Math.Abs(process.StartTime.ToUniversalTime().Ticks - plan.ParentStartTicks) < TimeSpan.FromSeconds(2).Ticks; }
        catch (ArgumentException) { return false; } // expected: parent has exited
    }
    private static void Restart(string target, string data, bool windows)
    {
        var start = new ProcessStartInfo(windows ? Path.Combine(target, "EasySellPut.exe") : "/usr/bin/open") { UseShellExecute = false, WorkingDirectory = target };
        if (!windows) foreach (var argument in new[] { "-n", target, "--args" }) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(data); Process.Start(start)?.Dispose();
    }
}
