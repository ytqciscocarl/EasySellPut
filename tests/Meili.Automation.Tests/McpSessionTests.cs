using System.Text.Json;
using Meili.Automation;

namespace Meili.Automation.Tests;

public sealed class McpSessionTests : IDisposable
{
    private readonly string _dir=Path.Combine(Path.GetTempPath(),"mcp-session-"+Guid.NewGuid().ToString("N"));
    [Fact]
    public void Only_initialized_live_sessions_in_this_data_directory_are_reported()
    {
        Assert.Empty(McpSessions.Read(_dir).Connections);
        string? client=null;McpSessionInfo active;
        using(var session=new McpSessionLease(_dir,false,()=>client))
        {
            session.Publish();Assert.Empty(McpSessions.Read(_dir).Connections);
            client="fixture client";session.Publish();
            active=Assert.Single(McpSessions.Read(_dir).Connections);
            Assert.True(active.ReadOnly);Assert.Equal(client,active.Client);
            Assert.Empty(McpSessions.Read(_dir+"-other").Connections);
            Assert.Empty(McpSessions.Read(_dir,DateTimeOffset.UtcNow.AddSeconds(31)).Connections);
        }
        Assert.Empty(Directory.GetFiles(McpSessions.Folder(_dir),"*.json"));
        // Stop the heartbeat owner before hand-writing malformed snapshots. The initial timer callback
        // may otherwise legitimately republish a live record while these reader-only assertions run.
        var file=Path.Combine(McpSessions.Folder(_dir),active.SessionId+".json");
        void Write(McpSessionInfo value)=>File.WriteAllText(file,JsonSerializer.Serialize(value,WireJson.Options));
        Write(active with{ProcessStartTicks=1});Assert.Empty(McpSessions.Read(_dir).Connections);
        Write(active with{Client=null!});Assert.Empty(McpSessions.Read(_dir).Connections);
        Write(active with{SessionId="invalid"});Assert.Empty(McpSessions.Read(_dir).Connections);
        File.WriteAllText(file,"{");Assert.Empty(McpSessions.Read(_dir).Connections);
        File.Delete(file);
        Assert.Empty(Directory.GetFiles(McpSessions.Folder(_dir),"*.json"));
    }
    [Fact]
    public void Setup_probes_never_look_like_an_AI_connection_and_write_mode_is_explicit()
    {
        using var setup=new McpSessionLease(_dir,false,()=>McpSessions.SetupClientName);
        setup.Publish();Assert.Empty(McpSessions.Read(_dir).Connections);
        using var writable=new McpSessionLease(_dir,true,()=>"write client");writable.Publish();
        Assert.False(Assert.Single(McpSessions.Read(_dir).Connections).ReadOnly);
    }
    public void Dispose(){if(Directory.Exists(_dir))Directory.Delete(_dir,true);}
}
