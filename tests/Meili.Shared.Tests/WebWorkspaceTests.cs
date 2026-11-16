using System.Text.Json;
using Meili.Core.Settings;

namespace Meili.Shared.Tests;

public sealed class WebWorkspaceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "meili-web-tabs-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    [Fact]
    public void Saved_tabs_reorder_edit_remove_and_motion_survive_restart()
    {
        var workspace = new WebWorkspace(_directory);
        var a = workspace.Save(null, " 市场 ", "example.com/news");
        var b = workspace.Save(null, "Research", "https://example.org/");
        Assert.Equal("https://example.com/news", a.Url); Assert.Equal("市场", a.Name);
        workspace.Move(b.Id, -1); workspace.SetReduceMotion(true);
        workspace.Save(a.Id, "News", "https://example.com/new");
        var reloaded = new WebWorkspace(_directory);
        Assert.Equal(new[] { b.Id, a.Id }, reloaded.Tabs.Select(t => t.Id)); Assert.True(reloaded.ReduceMotion);
        Assert.Equal("https://example.com/new", reloaded.Tabs[1].Url);
        reloaded.Remove(a.Id); Assert.Single(new WebWorkspace(_directory).Tabs);
    }
    [Theory]
    [InlineData("News", "javascript:alert(1)")]
    [InlineData("News", "file:///tmp/local")]
    [InlineData("News", "https://user:password@example.com")]
    [InlineData("News", "https://example.com/\nsecret")]
    [InlineData("", "https://example.com")]
    [InlineData("News", "data:text/html,Hi")]
    public void Rejects_unsafe_or_incomplete_tabs(string name, string address)
    {
        Assert.False(WebWorkspace.TryNormalize(name, address, out _, out _));
        var workspace = new WebWorkspace(_directory);
        Assert.Throws<ArgumentException>(() => workspace.Save(null, name, address)); Assert.Empty(workspace.Tabs);
    }
    [Fact]
    public void Conflicts_do_not_lose_other_process_changes_or_mutate_memory()
    {
        var a = new WebWorkspace(_directory); var b = new WebWorkspace(_directory);
        var saved = a.Save(null, "First", "https://example.com");
        Assert.Throws<DataConflictException>(() => b.Save(null, "Lost", "https://example.org"));
        Assert.Empty(b.Tabs); Assert.Equal(saved, Assert.Single(new WebWorkspace(_directory).Tabs));
        Assert.Throws<DataConflictException>(() => b.SetReduceMotion(true)); Assert.False(b.ReduceMotion);
    }
    [Fact]
    public void Bounded_tabs_and_corrupt_state_are_not_silently_overwritten()
    {
        var workspace = new WebWorkspace(_directory);
        for (var i = 0; i < WebWorkspace.MaximumTabs; i++) workspace.Save(null, "Tab " + i, "https://example.com/" + i);
        Assert.Throws<InvalidOperationException>(() => workspace.Save(null, "Overflow", "https://example.com"));
        Assert.Equal(WebWorkspace.MaximumTabs, new WebWorkspace(_directory).Tabs.Count);
        var file = Path.Combine(_directory, "web-workspace.json"); File.WriteAllText(file, "invalid json");
        Assert.Throws<JsonException>(() => new WebWorkspace(_directory)); Assert.Equal("invalid json", File.ReadAllText(file));
        File.WriteAllText(file, "{\"SchemaVersion\":99,\"Tabs\":[]}"); Assert.Throws<InvalidDataException>(() => new WebWorkspace(_directory));
        File.WriteAllText(file, "{\"SchemaVersion\":1,\"Tabs\":[null]}"); Assert.Throws<InvalidDataException>(() => new WebWorkspace(_directory));
    }
}
