using System.Text.Json;
using Meili.Core.Settings;

namespace Meili.Shared.Services;

public sealed record WebWorkspaceTab(string Id, string Name, string Url)
{
    public string Glyph => System.Globalization.StringInfo.GetNextTextElement(Name).ToUpperInvariant();
    public string Host => new Uri(Url).IdnHost;
}

/// <summary>Desktop presentation bookmarks, not browser history or an automation URL execution API.</summary>
public sealed class WebWorkspace
{
    public const int MaximumTabs = 12;
    private readonly SnapshotFile _file;
    private State _state;
    private sealed record State(int SchemaVersion, bool ReduceMotion, List<WebWorkspaceTab> Tabs);
    public IReadOnlyList<WebWorkspaceTab> Tabs => _state.Tabs.AsReadOnly();
    public bool ReduceMotion => _state.ReduceMotion;
    public WebWorkspace(string dataDirectory)
    {
        _file = new SnapshotFile(Path.Combine(dataDirectory, "web-workspace.json"));
        var json = _file.Read();
        _state = json is null ? new(1, false, []) : JsonSerializer.Deserialize<State>(json) ?? throw new InvalidDataException("Invalid web workspace.");
        if (_state.SchemaVersion != 1 || _state.Tabs is null || _state.Tabs.Count > MaximumTabs || _state.Tabs.Select(t => t?.Id).Distinct().Count() != _state.Tabs.Count)
            throw new InvalidDataException("Invalid web workspace.");
        foreach (var tab in _state.Tabs)
            if (tab is null || !Guid.TryParseExact(tab.Id, "N", out _) || !TryNormalize(tab.Name, tab.Url, out _, out _)) throw new InvalidDataException("Invalid web workspace tab.");
    }
    public static bool TryNormalize(string? name, string? address, out string title, out string url)
    {
        title = (name ?? "").Trim(); url = (address ?? "").Trim();
        if (!url.Contains("://", StringComparison.Ordinal) && !url.Contains(':')) url = "https://" + url;
        if (title.Length is 0 or > 40 || title.Any(char.IsControl) || url.Length > 2048 || url.Any(char.IsControl) || !NewsReaderPolicy.IsAllowed(url)) return false;
        var uri = new Uri(url);
        if (string.IsNullOrWhiteSpace(uri.Host)) return false;
        url = uri.AbsoluteUri; return true;
    }
    public WebWorkspaceTab Save(string? id, string name, string address)
    {
        if (!TryNormalize(name, address, out var title, out var url)) throw new ArgumentException("Invalid name or HTTP/HTTPS address.");
        var tabs = _state.Tabs.ToList();
        var index = id is null ? -1 : tabs.FindIndex(t => t.Id == id);
        if (id is not null && index < 0) throw new ArgumentException("Tab no longer exists.");
        if (index < 0 && tabs.Count >= MaximumTabs) throw new InvalidOperationException("Web workspace is full.");
        var tab = new WebWorkspaceTab(id ?? Guid.NewGuid().ToString("N"), title, url);
        if (index < 0) tabs.Add(tab); else tabs[index] = tab;
        Commit(_state with { Tabs = tabs }); return tab;
    }
    public void Remove(string id) => Commit(_state with { Tabs = _state.Tabs.Where(t => t.Id != id).ToList() });
    public void Move(string id, int delta)
    {
        if (delta is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(delta));
        var tabs = _state.Tabs.ToList(); var from = tabs.FindIndex(t => t.Id == id); var to = from + delta;
        if (from < 0 || to < 0 || to >= tabs.Count) return;
        (tabs[from], tabs[to]) = (tabs[to], tabs[from]); Commit(_state with { Tabs = tabs });
    }
    public void SetReduceMotion(bool enabled) => Commit(_state with { ReduceMotion = enabled });
    private void Commit(State next)
    {
        _file.Write(JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
        _state = next;
    }
}
