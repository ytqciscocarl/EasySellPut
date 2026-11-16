using System.Diagnostics;
using System.Text.Json;
using Meili.Core.Diagnostics;
using Meili.Core.Settings;

namespace Meili.Automation;

public sealed record McpSessionInfo(string SessionId, int ProcessId, long ProcessStartTicks, string Client, bool ReadOnly, DateTimeOffset UpdatedAt);
public sealed record McpSessionStatus(bool Available, IReadOnlyList<McpSessionInfo> Connections);

/// <summary>Local diagnostics only, scoped to one data directory. Never an authorization or market-health signal.</summary>
public static class McpSessions
{
    public const string SetupClientName = "EasySellPut setup verification";
    public static string Folder(string dataDirectory) => Path.Combine(dataDirectory, ".mcp-runtime");
    public static McpSessionStatus Read(string dataDirectory, DateTimeOffset? time = null)
    {
        var now = time ?? DateTimeOffset.UtcNow; var sessions = new List<McpSessionInfo>(); var folder = Folder(dataDirectory);
        try
        {
            try { _ = File.GetAttributes(folder); }
            catch (FileNotFoundException) { return new(true, sessions); }
            catch (DirectoryNotFoundException) { return new(true, sessions); }
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return new(false, sessions);
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Take(256))
            {
                try
                {
                    if (file.Length > 8192 || file.LinkTarget is not null) continue;
                    var session = JsonSerializer.Deserialize<McpSessionInfo>(File.ReadAllText(file.FullName), WireJson.Options);
                    if (session is null || session.Client == SetupClientName || string.IsNullOrWhiteSpace(session.Client) || session.Client.Length > 64 || !Guid.TryParseExact(session.SessionId, "N", out _) || Path.GetFileNameWithoutExtension(file.Name) != session.SessionId || session.ProcessId <= 0 || session.ProcessStartTicks <= 0
                        || now - session.UpdatedAt > TimeSpan.FromSeconds(30) || session.UpdatedAt - now > TimeSpan.FromSeconds(5)) continue;
                    using var process = Process.GetProcessById(session.ProcessId);
                    if (process.HasExited || Math.Abs(process.StartTime.ToUniversalTime().Ticks - session.ProcessStartTicks) > TimeSpan.FromSeconds(2).Ticks) continue;
                    sessions.Add(session with { Client = new string(session.Client.Where(c => !char.IsControl(c)).ToArray()) });
                }
                catch (UnauthorizedAccessException) { return new(false, sessions); /* expected: unavailable diagnostics are explicitly shown as unknown by the UI. */}
                catch (System.ComponentModel.Win32Exception) { return new(false, sessions); /* expected: unavailable diagnostics are explicitly shown as unknown by the UI. */}
                catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException)
                { /* expected: partial/expired diagnostics and exited processes do not prove a live connection. */ }
            }
            return new(true, sessions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(false, sessions); /* expected: unavailable diagnostics are explicitly shown as unknown by the UI. */}
    }
}

public sealed class McpSessionLease : IDisposable
{
    private readonly object _gate = new();
    private readonly string _file;
    private readonly Timer _timer;
    private readonly Func<string?> _client;
    private readonly bool _writes;
    private readonly long _start = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    private readonly string _id = Guid.NewGuid().ToString("N");
    private bool _disposed, _warned;
    public McpSessionLease(string dataDirectory, bool writes, Func<string?> client)
    {
        _file = Path.Combine(McpSessions.Folder(dataDirectory), _id + ".json"); _writes = writes; _client = client;
        _timer = new Timer(_ => Publish(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }
    public void Publish()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var name = _client(); if (string.IsNullOrWhiteSpace(name) || name == McpSessions.SetupClientName) return;
                name = new string(name.Where(c => !char.IsControl(c)).Take(64).ToArray());
                if (string.IsNullOrWhiteSpace(name)) return;
                var folder = Path.GetDirectoryName(_file)!;
                if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("MCP diagnostic directory is linked.");
                Directory.CreateDirectory(folder);
                var snapshot = new SnapshotFile(_file);
                snapshot.Write(JsonSerializer.Serialize(new McpSessionInfo(_id, Environment.ProcessId, _start, name, !_writes, DateTimeOffset.UtcNow), WireJson.Options));
                _warned = false;
            }
            catch (Exception ex)
            { if (!_warned) { AppLog.Warn("mcp-status", "Could not publish local MCP diagnostics", ex); _warned = true; } }
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true; _timer.Dispose();
            try { File.Delete(_file); File.Delete(_file + ".lock"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("mcp-status", "Diagnostic cleanup deferred; process liveness/expiry will invalidate it", ex); }
        }
    }
}
